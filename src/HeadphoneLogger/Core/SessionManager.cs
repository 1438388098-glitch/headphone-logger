using HeadphoneLogger.Audio;
using HeadphoneLogger.Storage;

namespace HeadphoneLogger.Core;

public sealed class SceneChangeRequestedEventArgs : EventArgs
{
    public required SceneDraft Draft { get; init; }
}

public enum SessionState
{
    Idle,
    InSession,
}

/// <summary>
/// 会话状态机（空闲→使用中→结束）。核心交互：
/// 插入静默建会话 → 前台跨场景切换时结束旧段、弹气泡请求确认 → 确认/改场景/超时落库开新段 → 拔出收尾。
/// 全部依赖抽象接口，可脱离硬件测试。所有回调假设在单线程（UI 线程）上到达，由装配层负责串行化。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ISessionStore _store;
    private readonly IAudioDeviceMonitor _monitor;
    private readonly ISceneRuleEngine _rules;
    private readonly IForegroundScanner _scanner;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>触发场景切换气泡请求。UI 订阅后展示气泡，最终调 <see cref="ResolvePendingScene"/> 落库。</summary>
    public event EventHandler<SceneChangeRequestedEventArgs>? SceneChangeRequested;

    public SessionState State { get; private set; } = SessionState.Idle;
    public Scene? CurrentScene => _currentMainScene;
    public Session? CurrentSession => _currentSession;
    public bool HasPending => _pending is not null;
    public SceneDraft? PendingDraft => _pending;

    private Session? _currentSession;
    private Segment? _activeSegment;
    private Scene? _currentMainScene;
    private bool _anyConfirmedThisSession;
    private SceneDraft? _pending;
    private DateTimeOffset _pendingSince;
    private bool _disposed;

    public SessionManager(ISessionStore store, IAudioDeviceMonitor monitor, ISceneRuleEngine rules,
        IForegroundScanner scanner, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _monitor = monitor;
        _rules = rules;
        _scanner = scanner;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>启动收尾：关闭上次关机/睡眠遗留的悬挂会话。启动时耳机在场不产生新会话（见 spec）。</summary>
    public void Start()
    {
        _store.CloseHangingSessions(_clock());
    }

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>线程契约：以下 Notify* 方法只在 UI 线程调用（装配层负责从 COM/后台线程串行化过来）。</summary>
    public void NotifyDeviceInserted(DeviceInfo device) => OnDeviceInserted(device);
    public void NotifyDeviceRemoved() => OnDeviceRemoved();
    public void NotifyForegroundChanged(ForegroundInfo foreground) => OnForegroundChanged(foreground);

    private void OnDeviceInserted(DeviceInfo device)
    {
        if (State == SessionState.InSession)
            return; // 抖动/重复事件

        var now = _clock();
        _currentSession = _store.CreateSession(device, now);
        _anyConfirmedThisSession = false;
        _pending = null;

        var fg = _scanner.Current;
        var main = _rules.Map(fg.ProcessName, fg.WindowTitle);
        var scenes = new HashSet<Scene> { main };
        scenes.UnionWith(SampleConcurrentScenes(main));
        _activeSegment = _store.CreateSegment(_currentSession.Id, now, fg.ProcessName, fg.WindowTitle,
            main, confirmed: false, scenes);
        _currentMainScene = main;
        State = SessionState.InSession;
    }

    private void OnDeviceRemoved()
    {
        if (State != SessionState.InSession)
            return;

        var now = _clock();
        if (_pending is not null)
        {
            // 气泡未决：按预填自动落库（未确认），保证不丢段
            ResolvePendingScene(_pending.MainScene, SampleConcurrentScenes(_pending.MainScene), confirmed: false);
        }
        if (_activeSegment is not null)
            _store.FinalizeSegment(_activeSegment.Id, now);
        _store.EndSession(_currentSession!.Id, now);

        // 误插兜底：<60s 且无任何已确认段 → 整会话删除
        var elapsed = (now - _currentSession.StartTime).TotalSeconds;
        if (elapsed < 60 && !_anyConfirmedThisSession)
            _store.DeleteSession(_currentSession.Id);

        Reset();
    }

    private void OnForegroundChanged(ForegroundInfo foreground)
    {
        if (State != SessionState.InSession)
            return;

        var newScene = _rules.Map(foreground.ProcessName, foreground.WindowTitle);
        if (newScene == _currentMainScene)
            return; // 同一场景内的窗口切换不触发

        if (_pending is not null)
        {
            // 气泡未决期间再切换：合并覆盖草稿，不重复弹窗；段起点保持最初切换时刻
            _pending = BuildDraft(foreground, newScene);
            return;
        }

        var now = _clock();
        if (_activeSegment is not null)
            _store.FinalizeSegment(_activeSegment.Id, now);
        _pendingSince = now;
        _pending = BuildDraft(foreground, newScene);
        SceneChangeRequested?.Invoke(this, new SceneChangeRequestedEventArgs { Draft = _pending });
    }

    /// <summary>气泡结果：确认/改场景/超时。chosenMain 为空 → 用预填主场景。</summary>
    public void ResolvePendingScene(Scene? chosenMain, IReadOnlySet<Scene> concurrent, bool confirmed)
    {
        if (_pending is null || _currentSession is null)
            return;

        var main = chosenMain ?? _pending.MainScene;
        var scenes = new HashSet<Scene> { main };
        scenes.UnionWith(concurrent);
        scenes.Remove(Scene.Unmarked);
        _activeSegment = _store.CreateSegment(_currentSession.Id, _pendingSince, _pending.AppName,
            _pending.WindowTitle, main, confirmed, scenes);
        _currentMainScene = main;
        if (confirmed)
            _anyConfirmedThisSession = true;
        _pending = null;
    }

    /// <summary>当前主场景下的并发标签默认值（供气泡勾选初始状态）。</summary>
    public IReadOnlySet<Scene> DefaultConcurrentFor(Scene main) => SampleConcurrentScenes(main);

    private SceneDraft BuildDraft(ForegroundInfo fg, Scene main) => new()
    {
        MainScene = main,
        ConcurrentScenes = SampleConcurrentScenes(main),
        AppName = fg.ProcessName,
        WindowTitle = fg.WindowTitle,
    };

    /// <summary>采样发声进程 → 并发场景标签。排除「其他/未标注」，并与主场景去重。</summary>
    private IReadOnlySet<Scene> SampleConcurrentScenes(Scene main)
    {
        var set = new HashSet<Scene>();
        foreach (var proc in _monitor.GetSoundingProcessNames())
        {
            var s = _rules.Map(proc, null);
            if (s is Scene.Other or Scene.Unmarked)
                continue;
            if (s == main)
                continue;
            set.Add(s);
        }
        return set;
    }

    private void Reset()
    {
        State = SessionState.Idle;
        _currentSession = null;
        _activeSegment = null;
        _currentMainScene = null;
        _anyConfirmedThisSession = false;
        _pending = null;
    }
}
