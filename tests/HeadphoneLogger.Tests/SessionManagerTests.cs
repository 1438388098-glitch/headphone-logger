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
        _db = new TestDb();
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
        _scanner.Current = fg ?? new ForegroundInfo("chrome.exe", "哔哩哔哩 - bilibili");
        _sm.NotifyDeviceInserted(device ?? new DeviceInfo("dev-1", "WH-1000XM4"));
    }

    private void SwitchTo(ForegroundInfo fg) => _sm.NotifyForegroundChanged(fg);

    private IReadOnlyList<SegmentView> Segments => _db.Stats.GetSegments(new SegmentFilter());

    [Fact]
    public void Insert_CreatesSessionAndFirstSegment_Unconfirmed()
    {
        Insert(fg: new ForegroundInfo("steam.exe", "CS2"));

        Assert.Equal(SessionState.InSession, _sm.State);
        Assert.Equal(Scene.Game, _sm.CurrentScene);
        var seg = Segments.Single();
        Assert.False(seg.Confirmed);
        Assert.Equal(Scene.Game, seg.Scene);
        Assert.Equal("steam.exe", seg.AppName);
    }

    [Fact]
    public void Insert_WithSoundingMusic_AttachesConcurrentScene()
    {
        _monitor.SoundingProcesses.Add("cloudmusic.exe");
        Insert(fg: new ForegroundInfo("chrome.exe", "哔哩哔哩 - bilibili"));

        var seg = Segments.Single();
        Assert.Equal(Scene.Video, seg.Scene);
        Assert.Contains(Scene.Music, seg.Scenes);
    }

    [Fact]
    public void Concurrent_Dedup_ExcludesSameSceneAndOther()
    {
        // steam 发声 = 主场景游戏，去重；explorer 发声 = 其他，排除
        _monitor.SoundingProcesses.Add("steam.exe");
        _monitor.SoundingProcesses.Add("explorer.exe");
        Insert(fg: new ForegroundInfo("steam.exe", "CS2"));

        var seg = Segments.Single();
        Assert.Single(seg.Scenes);
        Assert.Equal(Scene.Game, seg.Scenes[0]);
    }

    [Fact]
    public void SceneSwitch_RaisesRequest_AndFinalizesOldSegment()
    {
        CaptureRequests();
        Insert(); // Video
        _clock.Advance(TimeSpan.FromMinutes(30));
        SwitchTo(new ForegroundInfo("steam.exe", "CS2"));

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
        Insert(); // chrome/bilibili → Video
        SwitchTo(new ForegroundInfo("chrome.exe", "YouTube - 首页")); // 仍 Video
        Assert.Equal(0, _requestCount);
    }

    [Fact]
    public void Confirm_CreatesConfirmedSegment_WithConcurrentTags()
    {
        CaptureRequests();
        Insert();
        SwitchTo(new ForegroundInfo("steam.exe", "CS2"));
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
        SwitchTo(new ForegroundInfo("steam.exe", "CS2"));
        _sm.ResolvePendingScene(Scene.Course, new HashSet<Scene> { Scene.Course }, confirmed: true);

        Assert.Equal(Scene.Course, Segments.OrderBy(s => s.StartTime).Last().Scene);
    }

    [Fact]
    public void Timeout_AutoResolve_UsesPrefillAndUnconfirmed()
    {
        CaptureRequests();
        Insert();
        SwitchTo(new ForegroundInfo("steam.exe", "CS2"));
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
        SwitchTo(new ForegroundInfo("steam.exe", "CS2")); // 触发
        SwitchTo(new ForegroundInfo("Code.exe", "Program.cs")); // pending 中再切

        Assert.Equal(1, _requestCount); // 不重复弹窗
        Assert.Equal(Scene.Coding, _sm.PendingDraft!.MainScene); // 草稿合并为最新场景
    }

    [Fact]
    public void DeviceRemoved_EndsSession_ComputesDuration()
    {
        Insert();
        _clock.Advance(TimeSpan.FromHours(2));
        _sm.NotifyDeviceRemoved();

        Assert.Equal(SessionState.Idle, _sm.State);
        var seg = Segments.Single();
        Assert.Equal(7200, seg.DurationSec);
        Assert.Equal(7200, _db.Stats.GetOverview().TodaySec);
    }

    [Fact]
    public void DeviceRemoved_Under60s_NoConfirmed_DiscardsSession()
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
        SwitchTo(new ForegroundInfo("steam.exe", "CS2"));
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
        SwitchTo(new ForegroundInfo("steam.exe", "CS2")); // pending
        _clock.Advance(TimeSpan.FromMinutes(2));
        _sm.NotifyDeviceRemoved();

        var segs = Segments.OrderBy(s => s.StartTime).ToList();
        Assert.Equal(2, segs.Count);
        Assert.False(segs[^1].Confirmed);
        Assert.Equal(Scene.Game, segs[^1].Scene);
    }

    [Fact]
    public void DoubleInsert_Ignored()
    {
        Insert();
        _sm.NotifyDeviceInserted(new DeviceInfo("dev-2", "AirPods Max"));
        Assert.Single(Segments); // 不会为第二个设备建会话
        Assert.Equal("WH-1000XM4", _sm.CurrentSession!.DeviceName);
    }

    [Fact]
    public void Start_ClosesHangingSessions()
    {
        var t0 = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.FromHours(8));
        var session = _db.Store.CreateSession(new DeviceInfo("dev-1", "WH-1000XM4"), t0);
        _db.Store.CreateSegment(session.Id, t0, "steam.exe", "CS2", Scene.Game, true,
            new HashSet<Scene> { Scene.Game });

        _clock.Now = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.FromHours(8));
        _sm.Start();

        var seg = Segments.Single();
        Assert.Equal(3600, seg.DurationSec); // 悬挂段被收尾
    }
}
