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
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = await CodexAppServerClient.StartAsync(timeout.Token);
            _snapshot = await client.ReadQuotaAsync(timeout.Token);
            RenderSnapshot(_snapshot);
        }
        catch (OperationCanceledException)
        {
            ShowError("读取超时。请打开 ChatGPT 桌面版或 VS Code Codex，确认已用 ChatGPT 账户登录，然后重试。");
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
        RenderWindow(snapshot.FiveHour, FiveProgress, FivePercentText);
        RenderWindow(snapshot.Weekly, WeekProgress, WeekPercentText);
        UpdatedText.Text = $"更新 {snapshot.FetchedAt:HH:mm:ss}";
        UpdateResetLabels();
    }

    private static void RenderWindow(
        QuotaWindow window,
        System.Windows.Controls.ProgressBar progress,
        System.Windows.Controls.TextBlock percent)
    {
        if (window.RemainingPercent is not int remaining)
        {
            progress.Value = 0;
            progress.Foreground = EmptyBrush;
            percent.Text = "--%";
            return;
        }

        var color = GetStatusColor(remaining);
        progress.Value = remaining;
        progress.Foreground = color;
        percent.Text = $"{remaining}%";
        percent.Foreground = color;
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
        if (local <= DateTimeOffset.Now)
        {
            return "即将刷新";
        }

        return local.Date == DateTimeOffset.Now.Date
            ? $"重置 {local:HH:mm}"
            : $"重置 {local:MM-dd HH:mm}";
    }

    private static Brush GetStatusColor(int remaining) => remaining switch
    {
        >= 51 => PlentyBrush,
        >= 21 => ModerateBrush,
        >= 1 => TightBrush,
        _ => EmptyBrush
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

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.Content = Topmost ? "◉" : "○";
        TopmostButton.ToolTip = Topmost ? "取消置顶" : "保持置顶";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
