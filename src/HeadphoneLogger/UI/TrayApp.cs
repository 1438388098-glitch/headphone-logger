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
    private readonly ISceneRuleEngine _engine;
    private readonly AutoStart _autoStart;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly SceneConfirmBubble _bubble;
    private readonly System.Windows.Forms.Timer _bubbleSync;
    private readonly System.Windows.Forms.Timer _audioPoll;
    private readonly Form _syncHost = new(); // 隐藏宿主：BeginInvoke 串行化到 UI 线程
    private readonly ToolStripMenuItem _autoStartItem;
    private StatsWindow? _statsWindow;
    private bool _disposed;
    private bool _suppressAutoStartEvent;
    private int _renderedDraftVersion = -1;

    public TrayApp(SessionManager sm, StatsRepository stats, IAudioDeviceMonitor monitor,
        IForegroundScanner scanner, ISceneRuleEngine engine, AutoStart autoStart)
    {
        _sm = sm;
        _stats = stats;
        _monitor = monitor;
        _scanner = scanner;
        _engine = engine;
        _autoStart = autoStart;

        // 强制创建隐藏宿主的窗口句柄：否则 COM 回调线程对它 BeginInvoke 会抛
        // InvalidOperationException（句柄未创建的控件不能 Invoke/BeginInvoke）。
        _ = _syncHost.Handle;

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
            if (_suppressAutoStartEvent)
                return;
            var ok = _autoStartItem.Checked ? _autoStart.Enable() : _autoStart.Disable();
            if (!ok)
            {
                // 注册失败：回滚勾选态，避免与真实注册表状态不一致
                _suppressAutoStartEvent = true;
                _autoStartItem.Checked = !_autoStartItem.Checked;
                _suppressAutoStartEvent = false;
            }
        };
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Application.Exit();
        var openDataItem = new ToolStripMenuItem("打开数据文件夹");
        openDataItem.Click += (_, _) => OpenDataFolder();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            openItem,
            new ToolStripSeparator(),
            _autoStartItem,
            new ToolStripSeparator(),
            openDataItem,
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
            {
                // 只在草稿确实变化时刷新，避免 400ms 无条件重建打断用户勾选/下拉
                if (draft.Version != _renderedDraftVersion)
                {
                    _bubble.UpdateDraft(draft);
                    _renderedDraftVersion = draft.Version;
                }
            }
            else
            {
                // pending 被取消（如切回原场景）：隐藏残留气泡
                _bubble.HideBubble();
            }
        };

        // 有声音才记录：定时采样发声进程，驱动声音开始/停止开段/收尾
        _audioPoll = new System.Windows.Forms.Timer { Interval = 2000 };
        _audioPoll.Tick += (_, _) =>
        {
            if (_sm.State != SessionState.InSession)
                return; // 无会话不采样，省开销
            _sm.NotifyAudioActivity(_monitor.GetSoundingProcessNames().Count > 0);
        };

        _sm.SceneChangeRequested += OnSceneChangeRequested;
        _sm.SessionDataChanged += OnSessionDataChanged;
        _sm.SessionStateChanged += (_, _) => UpdateTrayState();
        _monitor.DeviceInserted += OnMonitorInserted;
        _monitor.DeviceRemoved += OnMonitorRemoved;
        _scanner.ForegroundChanged += OnScannerForeground;
        // 主题切换：正在显示的气泡即时换肤
        App.ThemeSettings.ThemeChanged += OnThemeChanged;
    }

    /// <summary>主题切换时让气泡即时换肤（经隐藏宿主封送到 UI 线程）。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        try
        {
            _syncHost.BeginInvoke(new Action(() => _bubble.ApplyTheme(App.ThemeSettings.Dark)));
        }
        catch
        {
            // 应用退出窗口期：丢弃该次主题刷新
        }
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
        _audioPoll.Start();
        ShowFirstRunHint();
        UpdateTrayState();
    }

    /// <summary>首次运行引导：只提示一次（注册表标记），让用户理解「有声音才记录 + 双击看统计」。</summary>
    private void ShowFirstRunHint()
    {
        const string keyPath = @"Software\HeadphoneLogger";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
            var firstRun = key.GetValue("FirstRunDone") is null;
            if (!firstRun)
                return;
            key.SetValue("FirstRunDone", "1");
            _tray.ShowBalloonTip(6000, "耳机使用记录正在后台工作",
                "插入耳机、有声音时才会记录，可双击托盘图标查看统计。", ToolTipIcon.Info);
        }
        catch
        {
            // 注册表不可写：跳过引导，不影响主流程
        }
    }

    /// <summary>托盘图标文字随状态变化：会话中显示设备名，空闲显示待机，供用户感知「正在记录」。</summary>
    private void UpdateTrayState()
    {
        if (_disposed)
            return;
        if (_sm.State == SessionState.InSession && _sm.CurrentSession is { } s)
        {
            var dev = string.IsNullOrEmpty(s.DeviceName) ? "耳机" : s.DeviceName;
            _tray.Text = $"耳机使用记录 · 正在记录（{dev}）";
            _tray.Icon = IconFactory.CreateRecordingIcon();
        }
        else
        {
            _tray.Text = "耳机使用记录 · 待机";
            _tray.Icon = IconFactory.CreateHeadphoneIcon();
        }
    }

    private void OnMonitorInserted(object? sender, DeviceInfo device)
    {
        try
        {
            _syncHost.BeginInvoke(new Action(() =>
            {
                _scanner.SetActive(true);
                _sm.NotifyDeviceInserted(device);
            }));
        }
        catch
        {
            // 串行化失败（应用退出窗口期）：丢弃该次事件，不向 COM 回调线程抛异常
        }
    }

    private void OnMonitorRemoved(object? sender, EventArgs e)
    {
        try
        {
            // 统一封送到 UI 线程：拔出即收气泡 + 通知状态机收尾 + 暂停前台轮询
            _syncHost.BeginInvoke(new Action(() =>
            {
                _sm.NotifyDeviceRemoved();
                _scanner.SetActive(false);
                _bubble.HideBubble();
            }));
        }
        catch
        {
            // 同上：退出窗口期丢弃事件
        }
    }

    private void OnScannerForeground(object? sender, ForegroundChangedEventArgs e) =>
        _sm.NotifyForegroundChanged(e.Foreground);

    private void OnSessionDataChanged(object? sender, EventArgs e) =>
        _statsWindow?.RefreshNow();

    private void OnSceneChangeRequested(object? sender, SceneChangeRequestedEventArgs e)
    {
        _bubble.ShowBubble(e.Draft);
        _renderedDraftVersion = e.Draft.Version;
    }

    private void ResolveBubble(SceneConfirmResult result)
    {
        // 若合并发生了但气泡还停留在旧草稿（400ms 窗口内点确认），先刷到最新再读勾选态
        if (_sm.PendingDraft is { } draft && draft.Version != _renderedDraftVersion)
        {
            _bubble.UpdateDraft(draft);
            _renderedDraftVersion = draft.Version;
        }
        _sm.ResolvePendingScene(result.MainScene, result.ConcurrentScenes, result.Confirmed);
        _bubble.HideBubble();
    }

    private void OpenStatsWindow()
    {
        if (_statsWindow is null || _statsWindow.IsDisposed)
            _statsWindow = new StatsWindow(_stats, _engine);
        if (!_statsWindow.Visible)
            _statsWindow.Show();
        _statsWindow.Activate();
    }

    private static void OpenDataFolder()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "headphone-logger");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开失败（资源管理器异常）：静默，不影响其他菜单项
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 退出前终结进行中会话/段（否则 end_time 悬空，数字要等下次启动才补齐）
        if (_sm.State == SessionState.InSession)
            _sm.NotifyDeviceRemoved();

        _bubbleSync.Stop();
        _bubbleSync.Dispose();
        _audioPoll.Stop();
        _audioPoll.Dispose();

        _sm.SceneChangeRequested -= OnSceneChangeRequested;
        _sm.SessionDataChanged -= OnSessionDataChanged;
        _monitor.DeviceInserted -= OnMonitorInserted;
        _monitor.DeviceRemoved -= OnMonitorRemoved;
        _scanner.ForegroundChanged -= OnScannerForeground;
        App.ThemeSettings.ThemeChanged -= OnThemeChanged;

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
