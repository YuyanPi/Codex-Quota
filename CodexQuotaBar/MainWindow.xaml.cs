using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace CodexQuotaBar;

public partial class MainWindow : Window
{
    private static readonly Brush PlentyBrush = BrushFrom("#35C878");
    private static readonly Brush ModerateBrush = BrushFrom("#F4C44E");
    private static readonly Brush TightBrush = BrushFrom("#F05D5E");
    private static readonly Brush EmptyBrush = BrushFrom("#788291");

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly WindowPreferences _preferences = WindowPreferences.Load();
    private readonly System.Windows.Controls.ToolTip _floatingToolTip = new();
    private NotifyIcon? _trayIcon;
    private QuotaSnapshot? _snapshot;
    private string? _lastError;
    private bool _refreshing;
    private bool _floatingEnabled;
    private bool _isCompact;
    private bool _allowExit;
    private bool _isDragging;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;

    public MainWindow()
    {
        InitializeComponent();
        FloatingSurface.ToolTip = _floatingToolTip;
        Loaded += MainWindow_Loaded;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                if (_floatingEnabled)
                {
                    WindowState = WindowState.Normal;
                    SetCompact(true);
                }
                else
                {
                    ShowInTaskbar = true;
                }
            }
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            UpdateResetLabels();
            await RefreshAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _floatingEnabled = _preferences.FloatingEnabled;
        _isCompact = _floatingEnabled && _preferences.IsCompact;
        Topmost = _preferences.Topmost;
        TopmostButton.Content = Topmost ? "◉" : "○";
        TopmostButton.ToolTip = Topmost ? "取消置顶" : "保持置顶";
        ContextTopmostItem.Header = Topmost ? "取消置顶" : "保持置顶";
        if (_floatingEnabled)
        {
            EnsureTrayIcon();
        }

        if (!RestoreWindowPosition())
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 12;
            Top = area.Bottom - Height - 12;
        }

        ApplyViewState();
        UpdateFloatingValueAlignmentFromPosition();
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
            _lastError = null;
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
        RenderWindow(snapshot.FiveHour, FiveProgress, FivePercentText);
        RenderWindow(snapshot.Weekly, WeekProgress, WeekPercentText);
        RenderFloatingValue(snapshot.FiveHour.RemainingPercent, FloatingFiveText);
        RenderFloatingValue(snapshot.Weekly.RemainingPercent, FloatingWeekText);
        UpdatedText.Text = $"更新 {snapshot.FetchedAt:HH:mm:ss}";
        UpdateResetLabels();
        UpdateFloatingToolTip();
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
            percent.Foreground = EmptyBrush;
            return;
        }

        var color = GetStatusColor(remaining);
        progress.Value = remaining;
        progress.Foreground = color;
        percent.Text = $"{remaining}%";
        percent.Foreground = color;
    }

    private static void RenderFloatingValue(int? remaining, System.Windows.Controls.TextBlock text)
    {
        text.Text = remaining?.ToString() ?? "--";
        text.Foreground = remaining is int value ? GetStatusColor(value) : EmptyBrush;
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

    private void UpdateFloatingToolTip()
    {
        var five = _snapshot?.FiveHour.RemainingPercent?.ToString() ?? "--";
        var weekly = _snapshot?.Weekly.RemainingPercent?.ToString() ?? "--";
        var content = new StringBuilder($"5h {five} | 周 {weekly}");
        if (_lastError is not null)
        {
            content.AppendLine();
            content.Append("刷新失败：").Append(_lastError);
        }

        _floatingToolTip.Content = content.ToString();
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
        _lastError = message;
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
        UpdatedText.Text = "刷新失败";
        UpdateFloatingToolTip();
        UpdateContextMenuState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void FloatingSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Opacity = 1.0;
        if (e.ClickCount == 2)
        {
            _isDragging = false;
            FloatingSurface.ReleaseMouseCapture();
            SetCompact(false);
            e.Handled = true;
            return;
        }

        _dragStartScreen = FloatingSurface.PointToScreen(e.GetPosition(FloatingSurface));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _isDragging = true;
        FloatingSurface.CaptureMouse();
        e.Handled = true;
    }

    private void FloatingSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = FloatingSurface.PointToScreen(e.GetPosition(FloatingSurface));
        var dpi = VisualTreeHelper.GetDpi(this);
        Opacity = 1.0;
        Left = _dragStartLeft + (currentScreen.X - _dragStartScreen.X) / dpi.DpiScaleX;
        Top = _dragStartTop + (currentScreen.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
    }

    private void FloatingSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        FloatingSurface.ReleaseMouseCapture();
        SnapToNearestHorizontalEdge(e);
        Opacity = 0.55;
        SavePreferences();
        e.Handled = true;
    }

    private void FloatingSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isCompact)
        {
            Opacity = 1.0;
        }
    }

    private void FloatingSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isCompact && !_isDragging)
        {
            Opacity = 0.55;
        }
    }

    private void SnapToNearestHorizontalEdge(System.Windows.Input.MouseEventArgs e)
    {
        var releasePoint = FloatingSurface.PointToScreen(e.GetPosition(FloatingSurface));
        var screen = Screen.FromPoint(new System.Drawing.Point((int)releasePoint.X, (int)releasePoint.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        var screenBounds = screen.Bounds;
        var surfaceTopLeft = FloatingSurface.PointToScreen(new Point(0, 0));
        var surfaceTopRight = FloatingSurface.PointToScreen(new Point(FloatingSurface.ActualWidth, 0));
        var leftPx = surfaceTopLeft.X;
        var rightPx = surfaceTopRight.X;
        var widthPx = rightPx - leftPx;
        var leftDistance = leftPx - screenBounds.Left;
        var rightDistance = screenBounds.Right - rightPx;
        var nearLeftEdge = leftDistance <= 0;
        var nearRightEdge = rightDistance <= 0;
        if (nearLeftEdge || nearRightEdge)
        {
            var dockLeft = nearLeftEdge && (!nearRightEdge || leftDistance <= rightDistance);
            var targetPx = dockLeft
                ? screenBounds.Left - widthPx / 2
                : screenBounds.Right - widthPx / 2;
            Left += (targetPx - leftPx) / dpi.DpiScaleX;
            SetFloatingValueAlignment(dockLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        }
        else
        {
            var maxLeft = Math.Max(screenBounds.Left, screenBounds.Right - widthPx);
            var clampedLeftPx = Math.Clamp(leftPx, screenBounds.Left, maxLeft);
            Left += (clampedLeftPx - leftPx) / dpi.DpiScaleX;
            SetFloatingValueAlignment(HorizontalAlignment.Center);
        }

        var topPx = PointToScreen(new Point(0, 0)).Y;
        var heightPx = ActualHeight * dpi.DpiScaleY;
        var minTop = work.Top + 6;
        var maxTop = work.Bottom - heightPx - 6;
        var clampedTopPx = Math.Clamp(topPx, minTop, Math.Max(minTop, maxTop));
        Top += (clampedTopPx - topPx) / dpi.DpiScaleY;
    }

    private void UpdateFloatingValueAlignmentFromPosition()
    {
        if (!_isCompact)
        {
            SetFloatingValueAlignment(HorizontalAlignment.Center);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var surfaceTopLeft = FloatingSurface.PointToScreen(new Point(0, 0));
        var surfaceTopRight = FloatingSurface.PointToScreen(new Point(FloatingSurface.ActualWidth, 0));
        var leftPx = surfaceTopLeft.X;
        var widthPx = surfaceTopRight.X - leftPx;
        var tolerancePx = 4 * dpi.DpiScaleX;
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (Math.Abs(leftPx - (bounds.Right - widthPx / 2)) <= tolerancePx)
            {
                SetFloatingValueAlignment(HorizontalAlignment.Left);
                return;
            }

            if (Math.Abs(leftPx - (bounds.Left - widthPx / 2)) <= tolerancePx)
            {
                SetFloatingValueAlignment(HorizontalAlignment.Right);
                return;
            }
        }

        SetFloatingValueAlignment(HorizontalAlignment.Center);
    }

    private void SetFloatingValueAlignment(HorizontalAlignment alignment)
    {
        FloatingFiveText.HorizontalAlignment = alignment;
        FloatingWeekText.HorizontalAlignment = alignment;
    }

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.Content = Topmost ? "◉" : "○";
        TopmostButton.ToolTip = Topmost ? "取消置顶" : "保持置顶";
        ContextTopmostItem.Header = Topmost ? "取消置顶" : "保持置顶";
        SavePreferences();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _floatingEnabled = true;
        SetCompact(true);
    }

    private void SetCompact(bool compact)
    {
        _floatingEnabled = true;
        _isCompact = compact;
        ApplyViewState();
        SavePreferences();
    }

    private void ApplyViewState()
    {
        MainSurface.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        FloatingSurface.Visibility = _isCompact ? Visibility.Visible : Visibility.Collapsed;
        if (!_isCompact)
        {
            Opacity = 1.0;
        }

        Width = _isCompact ? 48 : 230;
        Height = _isCompact ? 48 : 146;
        MinWidth = Width;
        MinHeight = Height;
        ShowInTaskbar = !_isCompact;
        EnsureTrayIconIfEnabled();
        UpdateContextMenuState();
    }

    private void UpdateContextMenuState()
    {
        if (ContextMenu?.Items.Count > 1 && ContextMenu.Items[0] is MenuItem openItem &&
            ContextMenu.Items[1] is MenuItem compactItem)
        {
            openItem.Visibility = _isCompact ? Visibility.Visible : Visibility.Collapsed;
            compactItem.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        }

        if (ContextMenu?.Items.Count > 3 && ContextMenu.Items[3] is MenuItem refreshItem)
        {
            refreshItem.ToolTip = _lastError is null ? null : $"上次刷新失败：{_lastError}";
        }
    }

    private void OpenMainWindow_Click(object sender, RoutedEventArgs e)
    {
        _floatingEnabled = true;
        SetCompact(false);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void EnsureTrayIconIfEnabled()
    {
        if (_floatingEnabled)
        {
            EnsureTrayIcon();
            return;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e) => OpenMainWindow_Click(sender!, new RoutedEventArgs());

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!,
            Text = "Codex Quota Bar",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;
        _trayIcon.ContextMenuStrip.Items.Add("展开主界面", null, (_, _) => OpenMainWindow_Click(this, new RoutedEventArgs()));
        _trayIcon.ContextMenuStrip.Items.Add("立即刷新", null, async (_, _) => await RefreshAsync());
        _trayIcon.ContextMenuStrip.Items.Add("切换置顶", null, (_, _) => ToggleTopmost_Click(this, new RoutedEventArgs()));
        _trayIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => Exit_Click(this, new RoutedEventArgs()));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_floatingEnabled)
        {
            SetCompact(true);
            return;
        }

        _allowExit = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit && _floatingEnabled)
        {
            e.Cancel = true;
            SetCompact(true);
            return;
        }

        SavePreferences();
        _refreshTimer.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _allowExit = true;
        Close();
    }

    private bool RestoreWindowPosition()
    {
        if (!_preferences.HasPosition)
        {
            return false;
        }

        Left = _preferences.Left;
        Top = _preferences.Top;
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(Width, Height));
        var bounds = new System.Drawing.Rectangle((int)topLeft.X, (int)topLeft.Y,
            (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
        var visibleOnAnyScreen = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
        if (visibleOnAnyScreen)
        {
            return true;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
        return false;
    }

    private void SavePreferences()
    {
        if (!IsLoaded || !double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return;
        }

        _preferences.FloatingEnabled = _floatingEnabled;
        _preferences.IsCompact = _isCompact;
        _preferences.Topmost = Topmost;
        _preferences.Left = Left;
        _preferences.Top = Top;
        _preferences.Save();
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

