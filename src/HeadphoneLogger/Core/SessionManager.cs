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
/// 插入/启动建会话 → **有声音才记录**：声音开始时按当前场景开段，声音停止收尾；
/// 前台跨场景切换且**切到的窗口正在发声**才结束旧段、弹气泡确认 → 确认/改场景/超时落库开新段 → 拔出收尾。
/// 切到无声窗口（微信/QQ 等）不产生任何记录，避免高频垃圾段。
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

    /// <summary>会话数据变化（建段/收尾/删除）通知，供统计窗实时刷新。</summary>
    public event EventHandler? SessionDataChanged;

    /// <summary>会话状态切换（Idle ⇄ InSession）通知，供装配层按需启停前台轮询等。</summary>
    public event EventHandler? SessionStateChanged;

    public SessionState State { get; private set; } = SessionState.Idle;
    public Scene? CurrentScene => _currentMainScene;
    public Session? CurrentSession => _currentSession;
    public bool HasPending => _pending is not null;
    public SceneDraft? PendingDraft => _pending;

    private const int SilentThreshold = 2;        // 连续 N 次无声音采样才视为声音停止（2s 采样 → 约 4s 迟滞，防切歌/缓冲碎段）
    private const double MinPendingSec = 5;       // 未确认草稿存活短于此则声音停止时不落段（防细缝废段）

    private Session? _currentSession;
    private Segment? _activeSegment;
    private Scene? _currentMainScene;
    private bool _anySegmentThisSession;
    private bool _audioActive;
    private int _silentStreak;
    private SceneDraft? _pending;
    private DateTimeOffset _pendingSince;
    private int _draftVersion;

    public SessionManager(ISessionStore store, IAudioDeviceMonitor monitor, ISceneRuleEngine rules,
        IForegroundScanner scanner, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _monitor = monitor;
        _rules = rules;
        _scanner = scanner;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>启动收尾：关闭悬挂会话；若启动时耳机已在场（常插用户），立即进入会话等待声音。</summary>
    public void Start()
    {
        _store.CloseHangingSessions(_clock());
        // 常插用户不会有「插入」事件，必须在启动时播种会话，否则永远无法进入 InSession
        if (State == SessionState.Idle && _monitor.CurrentDevice is { } device)
            BeginSession(device);
    }

    /// <summary>不持有可释放资源，保留空实现以便装配层统一 Dispose。</summary>
    public void Dispose() { }

    /// <summary>线程契约：以下 Notify* 方法只在 UI 线程调用（装配层负责从 COM/后台线程串行化过来）。</summary>
    public void NotifyDeviceInserted(DeviceInfo device) => OnDeviceInserted(device);
    public void NotifyDeviceRemoved() => OnDeviceRemoved();
    public void NotifyForegroundChanged(ForegroundInfo foreground) => OnForegroundChanged(foreground);

    /// <summary>音频活动状态（是否存在发声进程）。由装配层定时采样调用；状态未变时无操作。
    /// 有声音才记录：声音开始按当前场景开段，声音停止收尾当前段。
    /// 无声音采用迟滞（连续 N 次采样确认）才收尾，避免切歌/缓冲产生的碎段。</summary>
    public void NotifyAudioActivity(bool present)
    {
        if (State != SessionState.InSession)
            return;

        if (present)
        {
            _silentStreak = 0;
            if (_audioActive)
                return;
            // 声音开始：无活跃段则按当前场景开段（静默，不弹气泡）
            _audioActive = true;
            if (_activeSegment is null)
            {
                var fg = _scanner.Current;
                StartSegment(_clock(), fg.ProcessName, fg.WindowTitle);
                SessionDataChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        // 无声音：需连续 SilentThreshold 次采样确认，防音频抖动碎段
        _silentStreak++;
        if (_silentStreak < SilentThreshold || !_audioActive)
            return;
        _audioActive = false;
        _silentStreak = 0;

        // 声音停止：气泡未决则自动落库，活跃段收尾
        if (_pending is not null)
        {
            // 细缝段防护：切换后声音立刻停（草稿存活 < MinPendingSec）→ 直接丢弃草稿，不落废段
            if ((_clock() - _pendingSince).TotalSeconds < MinPendingSec)
                _pending = null;
            else
                ResolvePendingScene(_pending.MainScene, SampleConcurrentScenes(_pending.MainScene), confirmed: false);
        }
        if (_activeSegment is not null)
        {
            _store.FinalizeSegment(_activeSegment.Id, _clock());
            _activeSegment = null;
        }
        SessionDataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeviceInserted(DeviceInfo device)
    {
        if (State == SessionState.InSession)
            return; // 抖动/重复事件
        BeginSession(device);
    }

    /// <summary>建会话（插入事件与启动基准共用）。会话本身不落段，等声音出现才开始记录。</summary>
    private void BeginSession(DeviceInfo device)
    {
        var now = _clock();
        _currentSession = _store.CreateSession(device, now);
        _anySegmentThisSession = false;
        _audioActive = false;
        _silentStreak = 0;
        _pending = null;
        State = SessionState.InSession;
        _scanner.SetActive(true); // 会话中才需要跟踪前台
        SessionDataChanged?.Invoke(this, EventArgs.Empty);
        SessionStateChanged?.Invoke(this, EventArgs.Empty);
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
        {
            _store.FinalizeSegment(_activeSegment.Id, now);
            _activeSegment = null;
        }
        _store.EndSession(_currentSession!.Id, now);

        // 误插兜底：<60s 且从未有段（即从未发声，纯误插）→ 整会话删除
        var elapsed = (now - _currentSession.StartTime).TotalSeconds;
        if (elapsed < 60 && !_anySegmentThisSession)
            _store.DeleteSession(_currentSession.Id);

        Reset();
        _scanner.SetActive(false); // 会话结束，暂停前台轮询
        SessionDataChanged?.Invoke(this, EventArgs.Empty);
        SessionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnForegroundChanged(ForegroundInfo foreground)
    {
        // 有声音才记录：无声音时前台怎么切都不产生记录
        if (State != SessionState.InSession || !_audioActive)
            return;

        var newScene = _rules.Map(foreground.ProcessName, foreground.WindowTitle);
        if (newScene == _currentMainScene)
        {
            // 气泡未决期间切回原场景：取消本次切换（丢弃草稿），由 UI 隐藏气泡。
            // H1 修复：触发切换时旧段已被收尾，音频仍活跃则立即补开一段，避免记录空洞。
            if (_pending is not null)
                _pending = null;
            if (_activeSegment is null)
                StartSegment(_clock(), foreground.ProcessName, foreground.WindowTitle);
            return;
        }

        if (_pending is not null)
        {
            // 气泡未决期间再切换：只合并「正在发声」的窗口；切到无声窗口（如微信）不改变草稿，
            // 避免把进行中的游戏/视频时间错记成会议。段起点保持最初切换时刻。
            if (foreground.ProcessName is not { Length: > 0 } p2 ||
                !_monitor.GetSoundingProcessNames().Contains(p2, StringComparer.OrdinalIgnoreCase))
                return;
            _pending = BuildDraft(foreground, newScene);
            return;
        }

        // 只采样一次发声进程：既判「是否记录/弹气泡」，又用于并发标签
        var sounding = _monitor.GetSoundingProcessNames();
        if (foreground.ProcessName is not { Length: > 0 } p ||
            !sounding.Contains(p, StringComparer.OrdinalIgnoreCase))
            return; // 切到的窗口不发声（如微信/QQ）：不记录、不弹气泡，当前段继续

        var now = _clock();
        if (_activeSegment is not null)
        {
            _store.FinalizeSegment(_activeSegment.Id, now);
            _activeSegment = null;
        }
        _pendingSince = now;
        _pending = BuildDraft(foreground, newScene, sounding);
        SceneChangeRequested?.Invoke(this, new SceneChangeRequestedEventArgs { Draft = _pending });
    }

    /// <summary>气泡结果：确认/改场景/超时。chosenMain 为空 → 用预填主场景。</summary>
    public void ResolvePendingScene(Scene? chosenMain, IReadOnlySet<Scene> concurrent, bool confirmed)
    {
        if (_pending is null || _currentSession is null)
            return;

        var main = chosenMain ?? _pending.MainScene;
        var scenes = new HashSet<Scene> { main };
        foreach (var scene in concurrent)
        {
            // 并发标签只保留有效场景；主场景可能本身是「其他」，故不能整体剔除 Other
            if (scene is Scene.Unmarked or Scene.Other)
                continue;
            scenes.Add(scene);
        }
        AddSegment(_pendingSince, _pending.AppName, _pending.WindowTitle, main, confirmed, scenes);
        _currentMainScene = main;
        _pending = null;
        SessionDataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>开段：主场景规则——前台在发声则用前台场景，否则取发声场景首个，再无则「其他」。</summary>
    private Segment StartSegment(DateTimeOffset start, string? appName, string? windowTitle)
    {
        var sounding = _monitor.GetSoundingProcessNames();
        var foregroundSounding = appName is { Length: > 0 } p &&
                                 sounding.Contains(p, StringComparer.OrdinalIgnoreCase);
        var soundingScenes = sounding
            .Select(proc => _rules.Map(proc, null))
            .Where(s => s is not Scene.Other and not Scene.Unmarked)
            .ToHashSet();

        var main = foregroundSounding
            ? _rules.Map(appName, windowTitle)
            : (soundingScenes.Count > 0 ? soundingScenes.OrderBy(s => s).First() : Scene.Other);
        var scenes = new HashSet<Scene> { main };
        scenes.UnionWith(soundingScenes);
        return AddSegment(start, appName, windowTitle, main, confirmed: false, scenes);
    }

    private Segment AddSegment(DateTimeOffset start, string? appName, string? windowTitle,
        Scene main, bool confirmed, IReadOnlySet<Scene> scenes)
    {
        _anySegmentThisSession = true;
        _activeSegment = _store.CreateSegment(_currentSession!.Id, start, appName, windowTitle, main, confirmed, scenes);
        _currentMainScene = main;
        return _activeSegment;
    }

    private SceneDraft BuildDraft(ForegroundInfo fg, Scene main, IReadOnlyList<string>? sounding = null) => new()
    {
        MainScene = main,
        ConcurrentScenes = SampleConcurrentScenes(main, sounding),
        AppName = fg.ProcessName,
        WindowTitle = fg.WindowTitle,
        Version = ++_draftVersion,
    };

    /// <summary>采样发声进程 → 并发场景标签。排除「其他/未标注」，并与主场景去重。sounding 为空时自行枚举。</summary>
    private IReadOnlySet<Scene> SampleConcurrentScenes(Scene main, IReadOnlyList<string>? sounding = null)
    {
        var set = new HashSet<Scene>();
        foreach (var proc in sounding ?? _monitor.GetSoundingProcessNames())
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
        _anySegmentThisSession = false;
        _audioActive = false;
        _silentStreak = 0;
        _pending = null;
        _pendingSince = default;
    }
}
