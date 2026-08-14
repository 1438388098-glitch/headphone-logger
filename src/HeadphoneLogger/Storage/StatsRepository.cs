using HeadphoneLogger.Core;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Storage;

public sealed record OverviewStats(long TodaySec, long YesterdaySec, long WeekSec, long PrevWeekSec, long MonthSec, long PrevMonthSec);
public sealed record SceneCount(Scene Scene, long Sec);
public sealed record SceneAllocation(long TotalSec, IReadOnlyList<SceneCount> PerScene);
public sealed record DailyDuration(DateTime LocalDate, long TotalSec);
public sealed record DeviceDuration(string DeviceName, long TotalSec);
public sealed record HourCell(int Weekday, int Hour, long Sec); // Weekday: Mon=0..Sun=6
public sealed record SegmentView(
    long Id, long SessionId, DateTimeOffset StartTime, DateTimeOffset? EndTime, long DurationSec,
    string? AppName, string? WindowTitle, Scene Scene, bool Confirmed, string? Note,
    IReadOnlyList<Scene> Scenes);
public sealed record SegmentFilter(
    DateTime? FromLocal = null, DateTime? ToLocalExclusive = null, Scene? Scene = null, string? DeviceName = null);

/// <summary>
/// 统计聚合（读侧）：以段为单位在 C# 内存聚合，全部按**本地时区日界**切分。
/// 场景分配按「每段时长同时计入每个场景」的重叠口径。
/// </summary>
public sealed class StatsRepository
{
    private readonly SqliteConnection _conn;
    private readonly Func<DateTime> _nowLocal;
    private readonly object _gate = new();

    public StatsRepository(SqliteConnection conn, Func<DateTime>? nowLocal = null)
    {
        _conn = conn;
        _nowLocal = nowLocal ?? (() => DateTime.Now);
    }

    private sealed class Row
    {
        public required Segment Segment { get; init; }
        public string? DeviceName { get; init; }
        public IReadOnlyList<Scene> Scenes { get; set; } = [];
    }

    private List<Row> LoadSegments()
    {
        var rows = new List<Row>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.id, s.session_id, s.start_time, s.end_time, s.duration_sec,
                       s.app_name, s.window_title, s.scene, s.confirmed, s.note, sess.device_name
                FROM segments s
                JOIN sessions sess ON sess.id = s.session_id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Row
                {
                    Segment = new Segment
                    {
                        Id = reader.GetInt64(0),
                        SessionId = reader.GetInt64(1),
                        StartTime = TimeFmt.ParseUtc(reader.GetString(2)),
                        EndTime = reader.IsDBNull(3) ? null : TimeFmt.ParseUtc(reader.GetString(3)),
                        DurationSec = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        AppName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        WindowTitle = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Scene = ParseScene(reader.GetString(7)),
                        Confirmed = reader.GetInt64(8) != 0,
                        Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                    },
                    DeviceName = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }
        }

        var groups = new Dictionary<long, List<Scene>>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT segment_id, scene FROM segment_scenes;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sid = reader.GetInt64(0);
                if (!groups.TryGetValue(sid, out var list))
                    groups[sid] = list = [];
                list.Add(ParseScene(reader.GetString(1)));
            }
        }
        foreach (var row in rows)
        {
            if (groups.TryGetValue(row.Segment.Id, out var list))
                row.Scenes = list;
        }
        return rows;
    }

    private long EffectiveDuration(Row row)
    {
        var seg = row.Segment;
        if (seg.EndTime.HasValue)
            return Math.Max(0, seg.DurationSec ?? (long)((seg.EndTime.Value - seg.StartTime).TotalSeconds));
        // 进行中段：算到当前本地时刻
        return Math.Max(0, (long)(_nowLocal() - seg.StartTime.LocalDateTime).TotalSeconds);
    }

    public OverviewStats GetOverview()
    {
        var now = _nowLocal().Date;
        var weekStart = StartOfWeek(now);
        var rows = LoadSegments();
        long Sum(Func<DateTime, bool> pred) => rows.Where(r => pred(r.Segment.StartTime.LocalDateTime.Date)).Sum(EffectiveDuration);

        long TodaySec() => Sum(d => d == now);
        long YesterdaySec() => Sum(d => d == now.AddDays(-1));
        long WeekSec() => Sum(d => d >= weekStart && d < weekStart.AddDays(7));
        long PrevWeekSec() => Sum(d => d >= weekStart.AddDays(-7) && d < weekStart);
        long MonthSec() => Sum(d => d.Year == now.Year && d.Month == now.Month);
        long PrevMonthSec() => Sum(d =>
        {
            var prev = now.AddMonths(-1);
            return d.Year == prev.Year && d.Month == prev.Month;
        });

        return new OverviewStats(TodaySec(), YesterdaySec(), WeekSec(), PrevWeekSec(), MonthSec(), PrevMonthSec());
    }

    public SceneAllocation GetSceneAllocation(DateTime fromLocal, DateTime toLocalExclusive)
    {
        var rows = LoadSegments().Where(r =>
        {
            var d = r.Segment.StartTime.LocalDateTime.Date;
            return d >= fromLocal.Date && d < toLocalExclusive.Date;
        }).ToList();

        long total = rows.Sum(EffectiveDuration);
        var perScene = new Dictionary<Scene, long>();
        foreach (var row in rows)
        {
            var dur = EffectiveDuration(row);
            var scenes = row.Scenes.Count > 0 ? row.Scenes : [row.Segment.Scene];
            foreach (var scene in scenes)
                perScene[scene] = perScene.GetValueOrDefault(scene) + dur;
        }
        var list = perScene
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new SceneCount(kv.Key, kv.Value))
            .ToList();
        return new SceneAllocation(total, list);
    }

    public IReadOnlyList<DailyDuration> GetDailyDurations(int days)
    {
        var now = _nowLocal().Date;
        var from = now.AddDays(-(days - 1));
        var rows = LoadSegments().Where(r =>
        {
            var d = r.Segment.StartTime.LocalDateTime.Date;
            return d >= from && d <= now;
        }).ToList();

        var byDay = new Dictionary<DateTime, long>();
        for (var d = from; d <= now; d = d.AddDays(1))
            byDay[d] = 0;
        foreach (var row in rows)
        {
            var d = row.Segment.StartTime.LocalDateTime.Date;
            byDay[d] = byDay.GetValueOrDefault(d) + EffectiveDuration(row);
        }
        return byDay.OrderBy(kv => kv.Key).Select(kv => new DailyDuration(kv.Key, kv.Value)).ToList();
    }

    /// <summary>一周七天 × 24 小时热力图（周一起）。进行中段计入到当前时刻。</summary>
    public IReadOnlyList<HourCell> GetHourlyHeatmap()
    {
        var grid = new long[7, 24];
        var now = _nowLocal();
        foreach (var row in LoadSegments())
        {
            var start = row.Segment.StartTime.LocalDateTime;
            var end = row.Segment.EndTime.HasValue ? row.Segment.EndTime.Value.LocalDateTime : now;
            if (end <= start)
                continue;
            var cur = start;
            while (cur < end)
            {
                var hourEnd = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
                var clamp = end < hourEnd ? end : hourEnd;
                var sec = (long)(clamp - cur).TotalSeconds;
                grid[WeekdayIndex(cur), cur.Hour] += sec;
                cur = clamp;
            }
        }

        var cells = new List<HourCell>();
        for (var w = 0; w < 7; w++)
            for (var h = 0; h < 24; h++)
                if (grid[w, h] > 0)
                    cells.Add(new HourCell(w, h, grid[w, h]));
        return cells;
    }

    /// <summary>各耳机使用时长：按会话设备名分组，按会话开始本地日过滤。</summary>
    public IReadOnlyList<DeviceDuration> GetDeviceDurations(DateTime fromLocal, DateTime toLocalExclusive)
    {
        var sessions = LoadSessions().Where(s =>
        {
            var d = s.StartTime.LocalDateTime.Date;
            return d >= fromLocal.Date && d < toLocalExclusive.Date;
        }).ToList();

        var byDevice = new Dictionary<string, long>();
        foreach (var s in sessions)
        {
            var name = string.IsNullOrEmpty(s.DeviceName) ? "未知设备" : s.DeviceName;
            var dur = s.EndTime.HasValue
                ? Math.Max(0, s.DurationSec ?? (long)((s.EndTime.Value - s.StartTime).TotalSeconds))
                : Math.Max(0, (long)(_nowLocal() - s.StartTime.LocalDateTime).TotalSeconds);
            byDevice[name] = byDevice.GetValueOrDefault(name) + dur;
        }

        return byDevice
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new DeviceDuration(kv.Key, kv.Value))
            .ToList();
    }

    public IReadOnlyList<SegmentView> GetSegments(SegmentFilter filter)
    {
        var now = _nowLocal();
        return LoadSegments()
            .Where(r =>
            {
                var d = r.Segment.StartTime.LocalDateTime.Date;
                if (filter.FromLocal.HasValue && d < filter.FromLocal.Value.Date) return false;
                if (filter.ToLocalExclusive.HasValue && d >= filter.ToLocalExclusive.Value.Date) return false;
                if (filter.Scene.HasValue)
                {
                    var scenes = r.Scenes.Count > 0 ? r.Scenes : [r.Segment.Scene];
                    if (!scenes.Contains(filter.Scene.Value)) return false;
                }
                if (!string.IsNullOrEmpty(filter.DeviceName) && r.DeviceName != filter.DeviceName) return false;
                return true;
            })
            .OrderByDescending(r => r.Segment.StartTime)
            .Select(r => new SegmentView(
                r.Segment.Id, r.Segment.SessionId, r.Segment.StartTime, r.Segment.EndTime,
                EffectiveDuration(r), r.Segment.AppName, r.Segment.WindowTitle, r.Segment.Scene,
                r.Segment.Confirmed, r.Segment.Note,
                r.Scenes.Count > 0 ? r.Scenes : [r.Segment.Scene]))
            .ToList();
    }

    /// <summary>修改明细：改主场景、全量重写并发标签、备注、确认状态。</summary>
    public void UpdateSegment(long segmentId, Scene scene, IReadOnlyList<Scene> allScenes, string? note, bool confirmed)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE segments SET scene = $scene, confirmed = $confirmed, note = $note
                    WHERE id = $id;
                    DELETE FROM segment_scenes WHERE segment_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$scene", scene.ToString());
                cmd.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", segmentId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO segment_scenes (segment_id, scene) VALUES ($sid, $scene);";
                var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
                var pScene = cmd.Parameters.Add("$scene", SqliteType.Text);
                pSid.Value = segmentId;
                foreach (var tag in allScenes.Distinct())
                {
                    pScene.Value = tag.ToString();
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public int GetSessionCount() => LoadSessions().Count;

    public IReadOnlyList<string> GetDeviceNames() =>
        LoadSessions()
            .Select(s => string.IsNullOrEmpty(s.DeviceName) ? "未知设备" : s.DeviceName)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

    private List<Session> LoadSessions()
    {
        var list = new List<Session>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, device_id, device_name, start_time, end_time, duration_sec FROM sessions;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Session
            {
                Id = reader.GetInt64(0),
                DeviceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                StartTime = TimeFmt.ParseUtc(reader.GetString(3)),
                EndTime = reader.IsDBNull(4) ? null : TimeFmt.ParseUtc(reader.GetString(4)),
                DurationSec = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            });
        }
        return list;
    }

    private static Scene ParseScene(string s) =>
        Enum.TryParse<Scene>(s, out var scene) ? scene : Scene.Other;

    private static DateTime StartOfWeek(DateTime d)
    {
        var diff = (7 + ((int)d.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
        return d.Date.AddDays(-diff);
    }

    private static int WeekdayIndex(DateTime d) => ((int)d.DayOfWeek + 6) % 7; // Mon=0
}
