using HeadphoneLogger.Core;
using HeadphoneLogger.Storage;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Tests;

public sealed class StatsRepositoryTests : IDisposable
{
    // 本地时钟固定为 2026-08-14（周五）中午
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hpl-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _conn;
    private readonly SessionStore _store;
    private readonly StatsRepository _stats;

    public StatsRepositoryTests()
    {
        _conn = AppDatabase.Open(_path);
        _store = new SessionStore(_conn);
        _stats = new StatsRepository(_conn, () => Now);
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static DateTimeOffset Local(DateTime d) => new(d); // 按本机时区解释为墙钟时间

    private Session AddSession(DateTime start, string deviceName = "WH-1000XM4") =>
        _store.CreateSession(new DeviceInfo("dev-1", deviceName), Local(start));

    private Segment AddSegment(DateTime start, DateTime end, Scene scene, IReadOnlySet<Scene>? scenes = null,
        string deviceName = "WH-1000XM4", bool confirmed = true)
    {
        var session = AddSession(start, deviceName);
        var seg = _store.CreateSegment(session.Id, Local(start), "app.exe", "title", scene, confirmed,
            scenes ?? new HashSet<Scene> { scene });
        _store.FinalizeSegment(seg.Id, Local(end));
        return seg;
    }

    [Fact]
    public void SceneAllocation_CountsOverlapPerScene()
    {
        AddSegment(new DateTime(2026, 8, 14, 9, 0, 0), new DateTime(2026, 8, 14, 10, 0, 0),
            Scene.Game, new HashSet<Scene> { Scene.Game, Scene.Music });

        var alloc = _stats.GetSceneAllocation(new DateTime(2026, 8, 14), new DateTime(2026, 8, 15));

        Assert.Equal(3600, alloc.TotalSec); // 去重的总时长
        Assert.Equal(3600, alloc.PerScene.Single(s => s.Scene == Scene.Game).Sec);
        Assert.Equal(3600, alloc.PerScene.Single(s => s.Scene == Scene.Music).Sec);
        // 各场景之和可以大于总时长（重叠如实呈现）
        Assert.Equal(7200, alloc.PerScene.Sum(s => s.Sec));
    }

    [Fact]
    public void SceneAllocation_MissingScenes_FallsBackToMainScene()
    {
        // 旧数据可能没有 segment_scenes 行，退化为只计主场景
        AddSegment(new DateTime(2026, 8, 14, 9, 0, 0), new DateTime(2026, 8, 14, 9, 30, 0), Scene.Coding);

        var alloc = _stats.GetSceneAllocation(new DateTime(2026, 8, 14), new DateTime(2026, 8, 15));
        Assert.Equal(1800, alloc.PerScene.Single(s => s.Scene == Scene.Coding).Sec);
        Assert.Equal(1800, alloc.TotalSec);
    }

    [Fact]
    public void LocalDayBoundary_AttributesByLocalStartDate()
    {
        // 本地 00:30 的段（UTC 前一日 16:30），必须记入本地「今天」
        AddSegment(new DateTime(2026, 8, 14, 0, 30, 0), new DateTime(2026, 8, 14, 1, 0, 0), Scene.Other);

        var dailies = _stats.GetDailyDurations(30);
        var today = dailies.Single(d => d.LocalDate == new DateTime(2026, 8, 14));
        Assert.Equal(1800, today.TotalSec);
        Assert.Equal(30, dailies.Count);
    }

    [Fact]
    public void DailyDurations_CoversLast30Days_IncludingZeroDays()
    {
        AddSegment(new DateTime(2026, 8, 14, 10, 0, 0), new DateTime(2026, 8, 14, 11, 0, 0), Scene.Music);
        AddSegment(new DateTime(2026, 7, 16, 10, 0, 0), new DateTime(2026, 7, 16, 10, 30, 0), Scene.Meeting);

        var dailies = _stats.GetDailyDurations(30);
        Assert.Equal(30, dailies.Count);
        Assert.Equal(3600, dailies.Single(d => d.LocalDate == new DateTime(2026, 8, 14)).TotalSec);
        Assert.Equal(1800, dailies.Single(d => d.LocalDate == new DateTime(2026, 7, 16)).TotalSec);
    }

    [Fact]
    public void Overview_ComputesTodayYesterdayWeekMonth()
    {
        AddSegment(new DateTime(2026, 8, 14, 9, 0, 0), new DateTime(2026, 8, 14, 11, 0, 0), Scene.Game);   // 今日 2h
        AddSegment(new DateTime(2026, 8, 13, 20, 0, 0), new DateTime(2026, 8, 13, 21, 0, 0), Scene.Video); // 昨日 1h
        AddSegment(new DateTime(2026, 8, 11, 10, 0, 0), new DateTime(2026, 8, 11, 10, 30, 0), Scene.Coding); // 本周一 30m
        AddSegment(new DateTime(2026, 8, 7, 10, 0, 0), new DateTime(2026, 8, 7, 11, 0, 0), Scene.Meeting);  // 上周五 1h
        AddSegment(new DateTime(2026, 7, 20, 10, 0, 0), new DateTime(2026, 7, 20, 10, 45, 0), Scene.Music); // 上月 45m

        var o = _stats.GetOverview();
        Assert.Equal(7200, o.TodaySec);
        Assert.Equal(3600, o.YesterdaySec);
        Assert.Equal(7200 + 3600 + 1800, o.WeekSec);              // 本周：14/13/11
        Assert.Equal(3600, o.PrevWeekSec);                        // 上周：7
        Assert.Equal(7200 + 3600 + 1800 + 3600, o.MonthSec);      // 本月：14/13/11/7（8/7 属上周但同在本月）
        Assert.Equal(2700, o.PrevMonthSec);                       // 上月
    }

    [Fact]
    public void HourlyHeatmap_SplitsAcrossHourBoundaries()
    {
        // 8/13 23:30 → 8/14 01:30：跨两个自然日、三个整点
        AddSegment(new DateTime(2026, 8, 13, 23, 30, 0), new DateTime(2026, 8, 14, 1, 30, 0), Scene.Video);

        var cells = _stats.GetHourlyHeatmap();
        Assert.Equal(1800, cells.Where(c => c.Hour == 23).Sum(c => c.Sec));
        Assert.Equal(3600, cells.Where(c => c.Hour == 0).Sum(c => c.Sec));
        Assert.Equal(1800, cells.Where(c => c.Hour == 1).Sum(c => c.Sec));
        // 周三(8/13)→weekday 3，周五(8/14)→weekday 4（周一起）
        Assert.Equal(1800, cells.Single(c => c.Weekday == 3 && c.Hour == 23).Sec);
        Assert.Equal(3600, cells.Single(c => c.Weekday == 4 && c.Hour == 0).Sec);
        Assert.Equal(1800, cells.Single(c => c.Weekday == 4 && c.Hour == 1).Sec);
    }

    [Fact]
    public void DeviceDurations_GroupsByDeviceName()
    {
        var a = AddSession(new DateTime(2026, 8, 14, 9, 0, 0), "WH-1000XM4");
        _store.EndSession(a.Id, Local(new DateTime(2026, 8, 14, 10, 0, 0)));
        var b = AddSession(new DateTime(2026, 8, 14, 11, 0, 0), "AirPods Max");
        _store.EndSession(b.Id, Local(new DateTime(2026, 8, 14, 11, 30, 0)));

        var list = _stats.GetDeviceDurations(new DateTime(2026, 8, 14), new DateTime(2026, 8, 15));
        Assert.Equal(3600, list.Single(d => d.DeviceName == "WH-1000XM4").TotalSec);
        Assert.Equal(1800, list.Single(d => d.DeviceName == "AirPods Max").TotalSec);
    }

    [Fact]
    public void DeviceDurations_UnknownDevice_FallsBackToLabel()
    {
        var session = _store.CreateSession(null, Local(new DateTime(2026, 8, 14, 9, 0, 0)));
        _store.EndSession(session.Id, Local(new DateTime(2026, 8, 14, 9, 10, 0)));

        var list = _stats.GetDeviceDurations(new DateTime(2026, 8, 14), new DateTime(2026, 8, 15));
        Assert.Equal(600, list.Single(d => d.DeviceName == "未知设备").TotalSec);
    }

    [Fact]
    public void GetSegments_FiltersBySceneAndDevice()
    {
        AddSegment(new DateTime(2026, 8, 14, 9, 0, 0), new DateTime(2026, 8, 14, 10, 0, 0), Scene.Game,
            new HashSet<Scene> { Scene.Game, Scene.Music }, "WH-1000XM4");
        AddSegment(new DateTime(2026, 8, 14, 11, 0, 0), new DateTime(2026, 8, 14, 12, 0, 0), Scene.Coding,
            null, "AirPods Max");

        var music = _stats.GetSegments(new SegmentFilter(Scene: Scene.Music));
        Assert.Single(music);

        var airpods = _stats.GetSegments(new SegmentFilter(DeviceName: "AirPods Max"));
        Assert.Single(airpods);
        Assert.Equal(Scene.Coding, airpods[0].Scene);
    }

    [Fact]
    public void UpdateSegment_RewritesScenesAndNote()
    {
        var seg = AddSegment(new DateTime(2026, 8, 14, 9, 0, 0), new DateTime(2026, 8, 14, 10, 0, 0), Scene.Other);

        _stats.UpdateSegment(seg.Id, Scene.Course, [Scene.Course, Scene.Meeting], "补记：网课 + 会议", confirmed: true);

        var view = _stats.GetSegments(new SegmentFilter()).Single(s => s.Id == seg.Id);
        Assert.Equal(Scene.Course, view.Scene);
        Assert.Equal("补记：网课 + 会议", view.Note);
        Assert.True(view.Confirmed);
        Assert.Equal(2, view.Scenes.Count);
        Assert.Contains(Scene.Meeting, view.Scenes);
    }

    [Fact]
    public void CloseHangingSessions_FinalizesOpenSegments()
    {
        var session = AddSession(new DateTime(2026, 8, 14, 9, 0, 0));
        _store.CreateSegment(session.Id, Local(new DateTime(2026, 8, 14, 9, 0, 0)), "app.exe", "t", Scene.Game, true,
            new HashSet<Scene> { Scene.Game });

        _store.CloseHangingSessions(Local(new DateTime(2026, 8, 14, 10, 0, 0)));

        var segs = _stats.GetSegments(new SegmentFilter());
        Assert.Equal(3600, segs.Single().DurationSec);
        var view = _stats.GetOverview();
        Assert.Equal(3600, view.TodaySec);
    }

    [Fact]
    public void EndSession_ComputesDuration()
    {
        var session = AddSession(new DateTime(2026, 8, 14, 9, 0, 0));
        _store.EndSession(session.Id, Local(new DateTime(2026, 8, 14, 9, 5, 30)));

        var dur = _stats.GetDeviceDurations(new DateTime(2026, 8, 14), new DateTime(2026, 8, 15))
            .Single(d => d.DeviceName == "WH-1000XM4").TotalSec;
        Assert.Equal(330, dur);
    }
}
