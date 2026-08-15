using HeadphoneLogger.Audio;
using HeadphoneLogger.Core;
using HeadphoneLogger.Storage;

namespace HeadphoneLogger.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly TestDb _db;
    private readonly FakeMonitor _monitor = new();
    private readonly FakeScanner _scanner = new();
    private readonly MutableClock _clock = new();
    private readonly SessionManager _sm;
    private readonly SceneRuleEngine _engine = new(SceneRuleSeed.DefaultRules());

    public SessionManagerTests()
    {
        // StatsRepository 必须与 SessionManager 共用同一时钟，否则「今日/本周」等聚合按真实日期计算，日期敏感测试会随系统日期翻页而挂
        _db = new TestDb(() => _clock.Now.LocalDateTime);
        _sm = new SessionManager(_db.Store, _monitor, _engine, _scanner, () => _clock.Now);
    }

    public void Dispose()
    {
        _sm.Dispose();
        _db.Dispose();
    }

    private SceneDraft? _lastDraft;
    private int _requestCount;

    private void CaptureRequests()
    {
        _sm.SceneChangeRequested += (_, e) =>
        {
            _requestCount++;
            _lastDraft = e.Draft;
        };
    }

    private void Insert(DeviceInfo? device = null, ForegroundInfo? fg = null)
    {
        _scanner.Current = fg ?? new ForegroundInfo("chrome", "哔哩哔哩 - bilibili");
        _sm.NotifyDeviceInserted(device ?? new DeviceInfo("dev-1", "WH-1000XM4"));
    }

    private void SwitchTo(ForegroundInfo fg) => _sm.NotifyForegroundChanged(fg);

    /// <summary>模拟切到「正在发声」的窗口（发声进程命中才记录/弹气泡）。</summary>
    private void SwitchToSounding(ForegroundInfo fg)
    {
        if (fg.ProcessName is { Length: > 0 } p)
            _monitor.SoundingProcesses.Add(p);
        SwitchTo(fg);
    }

    /// <summary>模拟声音开始（把当前前台计入发声进程）。</summary>
    private void AudioOn()
    {
        if (_scanner.Current.ProcessName is { Length: > 0 } p && !_monitor.SoundingProcesses.Contains(p))
            _monitor.SoundingProcesses.Add(p);
        _sm.NotifyAudioActivity(true);
    }

    /// <summary>模拟声音持续停止（连续多次无声音采样，跨过 2 次采样迟滞）→ 收尾当前段。</summary>
    private void AudioOff()
    {
        _sm.NotifyAudioActivity(false);
        _sm.NotifyAudioActivity(false);
    }

    /// <summary>单次「当前采样无声音」：低于迟滞阈值，不应收尾/不应新建段。</summary>
    private void SingleSilentSample() => _sm.NotifyAudioActivity(false);

    private IReadOnlyList<SegmentView> Segments => _db.Stats.GetSegments(new SegmentFilter());

    [Fact]
    public void Insert_ThenAudioOn_CreatesSessionAndFirstSegment_Unconfirmed()
    {
        Insert(fg: new ForegroundInfo("steam", "CS2"));
        Assert.Equal(SessionState.InSession, _sm.State);
        Assert.Empty(Segments); // 未发声不落段

        AudioOn();
        Assert.Equal(Scene.Game, _sm.CurrentScene);
        var seg = Segments.Single();
        Assert.False(seg.Confirmed);
        Assert.Equal(Scene.Game, seg.Scene);
        Assert.Equal("steam", seg.AppName);
    }

    [Fact]
    public void Insert_WithSoundingMusic_AttachesConcurrentScene()
    {
        _monitor.SoundingProcesses.Add("cloudmusic");
        Insert(fg: new ForegroundInfo("chrome", "哔哩哔哩 - bilibili"));
        AudioOn();

        var seg = Segments.Single();
        Assert.Equal(Scene.Video, seg.Scene);
        Assert.Contains(Scene.Music, seg.Scenes);
    }

    [Fact]
    public void Concurrent_Dedup_ExcludesSameSceneAndOther()
    {
        // steam 发声 = 主场景游戏，去重；explorer 发声 = 其他，排除
        _monitor.SoundingProcesses.Add("steam");
        _monitor.SoundingProcesses.Add("explorer");
        Insert(fg: new ForegroundInfo("steam", "CS2"));
        AudioOn();

        var seg = Segments.Single();
        Assert.Single(seg.Scenes);
        Assert.Equal(Scene.Game, seg.Scenes[0]);
    }

    [Fact]
    public void SceneSwitch_RaisesRequest_AndFinalizesOldSegment()
    {
        CaptureRequests();
        Insert();
        AudioOn(); // Video
        _clock.Advance(TimeSpan.FromMinutes(30));
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));

        Assert.Equal(1, _requestCount);
        Assert.NotNull(_lastDraft);
        Assert.Equal(Scene.Game, _lastDraft!.MainScene);
        // 旧段已结束（时长 30 分钟）
        var old = Segments.OrderBy(s => s.StartTime).First();
        Assert.Equal(1800, old.DurationSec);
    }

    [Fact]
    public void SameScene_WindowSwitch_DoesNotTrigger()
    {
        CaptureRequests();
        Insert();
        AudioOn(); // chrome/bilibili → Video
        SwitchTo(new ForegroundInfo("chrome", "YouTube - 首页")); // 仍 Video
        Assert.Equal(0, _requestCount);
    }

    [Fact]
    public void Confirm_CreatesConfirmedSegment_WithConcurrentTags()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        Assert.NotNull(_lastDraft);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _sm.ResolvePendingScene(Scene.Game, new HashSet<Scene> { Scene.Game, Scene.Music }, confirmed: true);

        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(2, segs.Count);
        var newest = segs[^1];
        Assert.Equal(Scene.Game, newest.Scene);
        Assert.True(newest.Confirmed);
        Assert.Contains(Scene.Music, newest.Scenes);
        Assert.Equal(Scene.Game, _sm.CurrentScene);
    }

    [Fact]
    public void Confirm_UserPick_CustomScene()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        _sm.ResolvePendingScene(Scene.Course, new HashSet<Scene> { Scene.Course }, confirmed: true);

        Assert.Equal(Scene.Course, Segments.OrderBy(s => s.StartTime).Last().Scene);
    }

    [Fact]
    public void Timeout_AutoResolve_UsesPrefillAndUnconfirmed()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        Assert.Equal(Scene.Game, _lastDraft!.MainScene);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _sm.ResolvePendingScene(null, new HashSet<Scene>(), confirmed: false);

        var newest = Segments.OrderBy(s => s.StartTime).Last();
        Assert.Equal(Scene.Game, newest.Scene); // 预填主场景
        Assert.False(newest.Confirmed);
    }

    [Fact]
    public void Pending_ThenSwitch_CoalescesDraft_NoSecondEvent()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // 触发
        SwitchToSounding(new ForegroundInfo("Code", "Program.cs")); // pending 中再切

        Assert.Equal(1, _requestCount); // 不重复弹窗
        Assert.Equal(Scene.Coding, _sm.PendingDraft!.MainScene); // 草稿合并为最新场景
    }

    [Fact]
    public void Pending_ThenReturnToOriginalScene_CancelsDraft()
    {
        CaptureRequests();
        Insert();
        AudioOn(); // Video
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // 触发 → pending(Game)
        Assert.NotNull(_sm.PendingDraft);

        SwitchTo(new ForegroundInfo("chrome", "哔哩哔哩 - bilibili")); // 切回原场景（Video）
        Assert.Null(_sm.PendingDraft); // 气泡被取消，不落错场景段
        Assert.Equal(1, _requestCount);
        Assert.Equal(2, Segments.Count); // H1：取消后补开一段，记录不中断
    }

    [Fact]
    public void PendingCancel_RecordingContinues_NoHole()
    {
        // H1 回归：气泡取消后音频仍在，后续时长必须继续记（此前会永久空洞）
        Insert();
        AudioOn(); // Video 段
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // 触发气泡，旧段已收尾
        SwitchTo(new ForegroundInfo("chrome", "哔哩哔哩 - bilibili")); // 取消气泡 → 补开段
        _clock.Advance(TimeSpan.FromMinutes(30));
        AudioOff(); // 收尾补开的段

        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal(Scene.Video, segs[0].Scene);
        Assert.Equal(Scene.Video, segs[1].Scene); // 补开段仍是原场景
        Assert.Equal(1800, segs[1].DurationSec); // 取消后 30 分钟被完整记录
    }

    [Fact]
    public void SingleSilentSample_DoesNotFinalize_NoJitterFragment()
    {
        // H2：单次无声音采样不触发收尾，避免切歌/缓冲产生碎段
        Insert();
        AudioOn();
        _clock.Advance(TimeSpan.FromMinutes(10));
        SingleSilentSample();
        _clock.Advance(TimeSpan.FromMinutes(5));
        AudioOn(); // 声音恢复

        Assert.Single(Segments); // 仍是同一段，没有碎段
    }

    [Fact]
    public void AudioOff_WithTinyPending_DiscardsDraft_NoFissureSegment()
    {
        // H2 细缝段防护：切换后声音立刻停（草稿存活 < 阈值）→ 丢弃草稿，不落 0~2s 废段
        CaptureRequests();
        Insert();
        AudioOn(); // Video 段
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // 触发气泡，旧段收尾
        _clock.Advance(TimeSpan.FromSeconds(3)); // 草稿仅存活 3s
        AudioOff();

        Assert.Null(_sm.PendingDraft); // 草稿被丢弃
        Assert.Single(Segments); // 不产生废段
    }

    [Fact]
    public void AudioOff_WithMaturePending_ResolvesUnconfirmed()
    {
        // 草稿存活超过阈值：声音停止时正常自动落库（未确认）
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        _clock.Advance(TimeSpan.FromMinutes(2)); // 草稿存活 2 分钟 > 5s
        AudioOff();

        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(2, segs.Count);
        Assert.False(segs[^1].Confirmed);
        Assert.Equal(Scene.Game, segs[^1].Scene);
    }

    [Fact]
    public void SceneSwitch_ToSilentApp_WhilePending_KeepsDraft()
    {
        // M1：气泡未决期间切到无声窗口，草稿不被覆盖成会议，段起点保持最初切换时刻
        CaptureRequests();
        Insert();
        AudioOn(); // Video
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // 触发 → pending(Game)
        _clock.Advance(TimeSpan.FromSeconds(30));

        SwitchTo(new ForegroundInfo("wechat", "聊天")); // 无声切换，不应覆盖草稿

        Assert.Equal(Scene.Game, _sm.PendingDraft!.MainScene); // 草稿保持游戏
        _sm.ResolvePendingScene(Scene.Game, new HashSet<Scene> { Scene.Game }, confirmed: true);
        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(Scene.Game, segs[^1].Scene); // 落库为游戏，不是会议
    }

    [Fact]
    public void ResolvePendingScene_FiltersOtherAndUnmarkedFromConcurrent()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        _sm.ResolvePendingScene(Scene.Game, new HashSet<Scene> { Scene.Game, Scene.Other, Scene.Unmarked }, confirmed: true);

        var newest = Segments.OrderBy(s => s.StartTime).Last();
        Assert.Equal(Scene.Game, newest.Scene);
        Assert.Single(newest.Scenes); // Other/Unmarked 被过滤，只剩主场景
    }

    [Fact]
    public void SceneSwitch_ToSilentApp_Ignored_NoSegment()
    {
        CaptureRequests();
        Insert();
        AudioOn(); // Video 段
        _clock.Advance(TimeSpan.FromMinutes(30));
        SwitchTo(new ForegroundInfo("wechat", "聊天")); // 无声前台

        Assert.Equal(0, _requestCount); // 不弹气泡
        Assert.Null(_sm.PendingDraft);
        Assert.Single(Segments); // 不产生微信段，原段继续
    }

    [Fact]
    public void SceneSwitch_ToSilentApp_WhileMusicPlaying_IgnoresSwitch()
    {
        _monitor.SoundingProcesses.Add("cloudmusic");
        Insert(fg: new ForegroundInfo("chrome", "哔哩哔哩 - bilibili"));
        AudioOn();
        _clock.Advance(TimeSpan.FromMinutes(10));
        SwitchTo(new ForegroundInfo("wechat", "聊天")); // 无声切换

        Assert.Equal(0, _requestCount);
        Assert.Single(Segments); // 不产生微信段
        Assert.Contains(Scene.Music, Segments.Single().Scenes); // 音乐仍在并发标签
    }

    [Fact]
    public void AudioOff_FinalizesActiveSegment()
    {
        Insert();
        AudioOn();
        _clock.Advance(TimeSpan.FromMinutes(30));
        AudioOff();

        Assert.Equal(1800, Segments.Single().DurationSec);
    }

    [Fact]
    public void AudioOn_AfterOff_StartsNewSegment()
    {
        Insert();
        AudioOn();
        AudioOff();
        _clock.Advance(TimeSpan.FromMinutes(10));
        AudioOn();

        Assert.Equal(2, Segments.Count);
    }

    [Fact]
    public void DeviceRemoved_EndsSession_ComputesDuration()
    {
        Insert();
        AudioOn();
        _clock.Advance(TimeSpan.FromHours(2));
        _sm.NotifyDeviceRemoved();

        Assert.Equal(SessionState.Idle, _sm.State);
        var seg = Segments.Single();
        Assert.Equal(7200, seg.DurationSec);
        Assert.Equal(7200, _db.Stats.GetOverview().TodaySec);
    }

    [Fact]
    public void DeviceRemoved_Under60s_NoSound_DiscardsSession()
    {
        Insert();
        _clock.Advance(TimeSpan.FromSeconds(30));
        _sm.NotifyDeviceRemoved();

        Assert.Empty(Segments);
        Assert.Equal(0, _db.Stats.GetOverview().TodaySec);
    }

    [Fact]
    public void DeviceRemoved_Under60s_WithConfirmedSegment_Keeps()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2"));
        _sm.ResolvePendingScene(Scene.Game, new HashSet<Scene> { Scene.Game }, confirmed: true);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _sm.NotifyDeviceRemoved();

        Assert.Equal(2, Segments.Count); // 会话保留
    }

    [Fact]
    public void DeviceRemoved_WithPending_AutoResolvesUnconfirmed()
    {
        CaptureRequests();
        Insert();
        AudioOn();
        SwitchToSounding(new ForegroundInfo("steam", "CS2")); // pending
        _clock.Advance(TimeSpan.FromMinutes(2));
        _sm.NotifyDeviceRemoved();

        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(2, segs.Count);
        Assert.False(segs[^1].Confirmed);
        Assert.Equal(Scene.Game, segs[^1].Scene);
    }

    [Fact]
    public void DeviceRemoved_Under60s_WithSound_Keeps()
    {
        Insert();
        AudioOn();
        _clock.Advance(TimeSpan.FromSeconds(40));
        _sm.NotifyDeviceRemoved();

        Assert.Single(Segments); // 有声音活动即保留
        Assert.Equal(SessionState.Idle, _sm.State);
    }

    [Fact]
    public void DoubleInsert_Ignored()
    {
        Insert();
        _sm.NotifyDeviceInserted(new DeviceInfo("dev-2", "AirPods Max"));
        Assert.Equal(SessionState.InSession, _sm.State); // 不会为第二个设备重建会话
        Assert.Equal("WH-1000XM4", _sm.CurrentSession!.DeviceName);
    }

    [Fact]
    public void Start_ClosesHangingSessions()
    {
        var t0 = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.FromHours(8));
        var session = _db.Store.CreateSession(new DeviceInfo("dev-1", "WH-1000XM4"), t0);
        _db.Store.CreateSegment(session.Id, t0, "steam", "CS2", Scene.Game, true,
            new HashSet<Scene> { Scene.Game });

        _clock.Now = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(8));
        _sm.Start();

        var seg = Segments.Single();
        Assert.Equal(3600, seg.DurationSec); // 悬挂段被收尾
    }

    [Fact]
    public void Start_WithHeadphonesPresent_OpensSession_AndSoundBeginsSegment()
    {
        // 常插用户：启动时耳机已在场，立即进入会话；声音出现才开始记段
        _monitor.CurrentDevice = new DeviceInfo("dev-1", "WH-1000XM4");
        _scanner.Current = new ForegroundInfo("chrome", "哔哩哔哩 - bilibili");
        _sm.Start();

        Assert.Equal(SessionState.InSession, _sm.State);
        Assert.Empty(Segments); // 尚未发声

        AudioOn();
        Assert.Equal(Scene.Video, _sm.CurrentScene);
        var seg = Segments.Single();
        Assert.Equal(Scene.Video, seg.Scene);
        Assert.False(seg.Confirmed);
    }

    [Fact]
    public void Start_HeadphonesAbsent_StaysIdle()
    {
        _monitor.CurrentDevice = null;
        _sm.Start();
        Assert.Equal(SessionState.Idle, _sm.State);
        Assert.Empty(Segments);
    }

    [Fact]
    public void DataChangedEvent_FiresOnWrite()
    {
        var fired = 0;
        _sm.SessionDataChanged += (_, _) => fired++;
        Insert();   // 建会话
        AudioOn();  // 建段
        Assert.True(fired >= 2);
    }
}
