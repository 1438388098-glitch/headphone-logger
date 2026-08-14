using HeadphoneLogger.App;
using HeadphoneLogger.Audio;
using HeadphoneLogger.Core;
using HeadphoneLogger.Storage;

namespace HeadphoneLogger.UI;

/// <summary>
/// 托盘应用协调器：负责事件串行化（音频回调在 COM 线程 → 经隐藏宿主 Form 派发到 UI 线程）、
/// 气泡确认流转、统计窗口单例、托盘菜单。
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly SessionManager _sm;
    private readonly StatsRepository _stats;
    private readonly IAudioDeviceMonitor _monitor;
    private readonly IForegroundScanner _scanner;
    private readonly AutoStart _autoStart;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly SceneConfirmBubble _bubble;
    private readonly System.Windows.Forms.Timer _bubbleSync;
    private readonly Form _syncHost = new(); // 隐藏宿主：BeginInvoke 串行化到 UI 线程
    private readonly ToolStripMenuItem _autoStartItem;
    private StatsWindow? _statsWindow;
    private bool _disposed;

    public TrayApp(SessionManager sm, StatsRepository stats, IAudioDeviceMonitor monitor,
        IForegroundScanner scanner, AutoStart autoStart)
    {
        _sm = sm;
        _stats = stats;
        _monitor = monitor;
        _scanner = scanner;
        _autoStart = autoStart;

        _tray = new NotifyIcon
        {
            Icon = IconFactory.CreateHeadphoneIcon(),
            Text = "耳机使用记录",
            Visible = true,
        };

        _menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("查看统计");
        openItem.Click += (_, _) => OpenStatsWindow();
        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = _autoStart.IsEnabled() };
        _autoStartItem.CheckedChanged += (_, _) =>
        {
            if (_autoStartItem.Checked)
                _autoStart.Enable();
            else
                _autoStart.Disable();
        };
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Application.Exit();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            openItem,
            new ToolStripSeparator(),
            _autoStartItem,
            new ToolStripSeparator(),
            exitItem,
        });
        _tray.ContextMenuStrip = _menu;
        _tray.DoubleClick += (_, _) => OpenStatsWindow();

        _bubble = new SceneConfirmBubble { OnResolve = ResolveBubble };
        _bubbleSync = new System.Windows.Forms.Timer { Interval = 400 };
        _bubbleSync.Tick += (_, _) =>
        {
            if (_sm.PendingDraft is { } draft)
                _bubble.UpdateDraft(draft);
        };

        _sm.SceneChangeRequested += OnSceneChangeRequested;
        _monitor.DeviceInserted += OnMonitorInserted;
        _monitor.DeviceRemoved += OnMonitorRemoved;
        _scanner.ForegroundChanged += OnScannerForeground;
    }

    public void Start()
    {
        try
        {
            _monitor.Start();
        }
        catch
        {
            // 音频子系统不可用：托盘与统计仍可用，仅不记录会话
        }
        _scanner.Start();
        _sm.Start();
        _bubbleSync.Start();
    }

    private void OnMonitorInserted(object? sender, DeviceInfo device) =>
        _syncHost.BeginInvoke(new Action(() => _sm.NotifyDeviceInserted(device)));

    private void OnMonitorRemoved(object? sender, EventArgs e) =>
        _syncHost.BeginInvoke(new Action(() => _sm.NotifyDeviceRemoved()));

    private void OnScannerForeground(object? sender, ForegroundChangedEventArgs e) =>
        _sm.NotifyForegroundChanged(e.Foreground);

    private void OnSceneChangeRequested(object? sender, SceneChangeRequestedEventArgs e) =>
        _bubble.ShowBubble(e.Draft);

    private void ResolveBubble(SceneConfirmResult result)
    {
        _sm.ResolvePendingScene(result.MainScene, result.ConcurrentScenes, result.Confirmed);
        _bubble.HideBubble();
    }

    private void OpenStatsWindow()
    {
        if (_statsWindow is null || _statsWindow.IsDisposed)
            _statsWindow = new StatsWindow(_stats);
        if (!_statsWindow.Visible)
            _statsWindow.Show();
        _statsWindow.Activate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _bubbleSync.Stop();
        _bubbleSync.Dispose();

        _sm.SceneChangeRequested -= OnSceneChangeRequested;
        _monitor.DeviceInserted -= OnMonitorInserted;
        _monitor.DeviceRemoved -= OnMonitorRemoved;
        _scanner.ForegroundChanged -= OnScannerForeground;

        _bubble.Dispose();
        _syncHost.Dispose();
        _menu.Dispose();
        _tray.Visible = false;
        _tray.Dispose();

        _sm.Dispose();
        _scanner.Dispose();
        _monitor.Dispose();
        _statsWindow?.Dispose();
    }
}
