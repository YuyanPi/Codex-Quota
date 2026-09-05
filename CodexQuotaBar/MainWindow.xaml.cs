using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexQuotaBar;

public partial class MainWindow : Window
{
    private static readonly Brush PlentyBrush = BrushFrom("#35C878");
    private static readonly Brush ModerateBrush = BrushFrom("#F4C44E");
    private static readonly Brush TightBrush = BrushFrom("#F05D5E");
    private static readonly Brush EmptyBrush = BrushFrom("#788291");

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private QuotaSnapshot? _snapshot;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        _refreshTimer.Tick += async (_, _) =>
        {
            UpdateResetLabels();
            await RefreshAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
        Height = Math.Min(Height, area.Height - 32);
        _refreshTimer.Start();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        MessagePanel.Visibility = Visibility.Collapsed;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var client = await CodexAppServerClient.StartAsync(timeout.Token);
            _snapshot = await client.ReadQuotaAsync(timeout.Token);
            RenderSnapshot(_snapshot);
        }
        catch (OperationCanceledException)
        {
            ShowError("读取超时。请确认 Codex 桌面版或 CLI 已安装并完成登录，然后重试。");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private void RenderSnapshot(QuotaSnapshot snapshot)
    {
        ModelText.Text = snapshot.ModelName;
        RenderWindow(snapshot.FiveHour, FiveProgress, FivePercentText, FiveStatusText, FiveStatusDot);
        RenderWindow(snapshot.Weekly, WeekProgress, WeekPercentText, WeekStatusText, WeekStatusDot);
        SourceText.Text = snapshot.SourceName;
        UpdatedText.Text = $"更新 {snapshot.FetchedAt:HH:mm:ss}";
        UpdateResetLabels();
    }

    private static void RenderWindow(
        QuotaWindow window,
        System.Windows.Controls.ProgressBar progress,
        System.Windows.Controls.TextBlock percent,
        System.Windows.Controls.TextBlock status,
        System.Windows.Shapes.Ellipse dot)
    {
        if (window.RemainingPercent is not int remaining)
        {
            progress.Value = 0;
            progress.Foreground = EmptyBrush;
            percent.Text = "--%";
            status.Text = "暂不可用";
            dot.Fill = EmptyBrush;
            return;
        }

        var (color, label) = GetStatus(remaining);
        progress.Value = remaining;
        progress.Foreground = color;
        percent.Text = $"{remaining}%";
        percent.Foreground = color;
        status.Text = label;
        dot.Fill = color;
    }

    private void UpdateResetLabels()
    {
        if (_snapshot is null)
        {
            return;
        }

        FiveResetText.Text = FormatReset(_snapshot.FiveHour.ResetsAt);
        WeekResetText.Text = FormatReset(_snapshot.Weekly.ResetsAt);
    }

    private static string FormatReset(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "重置时间未知";
        }

        var local = value.Value.ToLocalTime();
        var remaining = local - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "即将刷新";
        }

        var countdown = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}天{remaining.Hours}小时后"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}小时{remaining.Minutes}分后"
                : $"{Math.Max(1, remaining.Minutes)}分钟后";

        return $"{local:MM-dd HH:mm} · {countdown}";
    }

    private static (Brush Color, string Label) GetStatus(int remaining) => remaining switch
    {
        >= 51 => (PlentyBrush, "余量充足"),
        >= 21 => (ModerateBrush, "余量适中"),
        >= 1 => (TightBrush, "余量紧张"),
        _ => (EmptyBrush, "已用尽")
    };

    private void ShowError(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
        UpdatedText.Text = "刷新失败";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
