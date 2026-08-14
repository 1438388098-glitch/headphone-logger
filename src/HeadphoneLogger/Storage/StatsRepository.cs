using HeadphoneLogger.Core;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Storage;

public sealed record OverviewStats(long TodaySec, long YesterdaySec, long WeekSec, long PrevWeekSec, long MonthSec, long PrevMonthSec);
public sealed record SceneCount(Scene Scene, long Sec);
public sealed record SceneAllocation(long TotalSec, IReadOnlyList<SceneCount> PerScene);
public sealed record DailyDuration(DateTime LocalDate, long TotalSec);
public sealed record DeviceDuration(string DeviceName, long TotalSec);
public sealed record HourCell(int Weekday, int Hour, long Sec); // Weekday: Mon=0..Sun=6
public sealed record HourSec(int Hour, long Sec);
public sealed record SegmentView(
    long Id, long SessionId, DateTimeOffset StartTime, DateTimeOffset? EndTime, long DurationSec,
    string? AppName, string? WindowTitle, Scene Scene, bool Confirmed, string? Note,
    IReadOnlyList<Scene> Scenes, string? DeviceName);
public sealed record SegmentFilter(
    DateTime? FromLocal = null, DateTime? ToLocalExclusive = null, Scene? Scene = null, string? DeviceName = null, int? MaxCount = null);

/// <summary>
/// 统计聚合（读侧）：以段为单位在 C# 内存聚合，全部按**本地时区日界**切分。
/// 场景分配按「每段时长同时计入每个场景」的重叠口径。
/// 所有按「日」聚合的方法都把跨午夜的段按真实小时拆分到各自所在日（与热力图口径一致）。
/// </summary>
public sealed class StatsRepository
{
    private readonly SqliteConnection _conn;
    private readonly Func<DateTime> _nowLocal;
    private readonly object _gate;

    /// <param name="gate">与写侧共享的锁对象。两个仓储共享同一连接，必须用同一把锁互斥。</param>
    public StatsRepository(SqliteConnection conn, Func<DateTime>? nowLocal = null, object? gate = null)
    {
        _conn = conn;
        _nowLocal = nowLocal ?? (() => DateTime.Now);
        _gate = gate ?? new object();
    }

    private sealed class Row
    {
        public required Segment Segment { get; init; }
        public string? DeviceName { get; init; }
        public IReadOnlyList<Scene> Scenes { get; set; } = [];
    }

    /// <summary>
    /// 只加载与 [sinceLocal, untilLocalExclusive) 有交集的段（进行中段 end_time 为空始终包含）。
    /// 窗口化避免全表扫描，随数据增长保持恒定成本。
    /// </summary>
    private List<Row> LoadSegments(DateTime? sinceLocal = null, DateTime? untilLocalExclusive = null)
    {
        lock (_gate)
        {
            var since = sinceLocal.HasValue ? TimeFmt.Utc(new DateTimeOffset(sinceLocal.Value)) : null;
            var until = untilLocalExclusive.HasValue ? TimeFmt.Utc(new DateTimeOffset(untilLocalExclusive.Value)) : null;

            var rows = new List<Row>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT s.id, s.session_id, s.start_time, s.end_time, s.duration_sec,
                           s.app_name, s.window_title, s.scene, s.confirmed, s.note, sess.device_name
                    FROM segments s
                    JOIN sessions sess ON sess.id = s.session_id
                    WHERE ($since IS NULL OR s.end_time IS NULL OR s.end_time >= $since)
                      AND ($until IS NULL OR s.start_time < $until);
                    """;
                cmd.Parameters.AddWithValue("$since", (object?)since ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$until", (object?)until ?? DBNull.Value);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // 防御：坏行（如损坏的时间串）跳过，不让聚合整体失败
                    if (!TimeFmt.TryParseUtc(reader.GetString(2), out var start))
                        continue;
                    rows.Add(new Row
                    {
                        Segment = new Segment
                        {
                            Id = reader.GetInt64(0),
                            SessionId = reader.GetInt64(1),
                            StartTime = start,
                            EndTime = reader.IsDBNull(3) || !TimeFmt.TryParseUtc(reader.GetString(3), out var end)
                                ? null : end,
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

            // 并发标签只对窗口内的段加载（可能含跨窗口段的标签，数量有限）
            var groups = new Dictionary<long, List<Scene>>();
            if (rows.Count > 0)
            {
                var ids = string.Join(",", rows.Select(r => r.Segment.Id));
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"SELECT segment_id, scene FROM segment_scenes WHERE segment_id IN ({ids});";
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
    }

    private long EffectiveDuration(Row row)
    {
        var seg = row.Segment;
        if (seg.EndTime.HasValue)
            return Math.Max(0, seg.DurationSec ?? (long)((seg.EndTime.Value - seg.StartTime).TotalSeconds));
        // 进行中段：算到当前本地时刻
        return Math.Max(0, (long)(_nowLocal() - seg.StartTime.LocalDateTime).TotalSeconds);
    }

    /// <summary>把一段按本地日界拆成 (日, 秒) 块。进行中段算到当前时刻。
    /// 口径与 GetTodayHourly/GetHourlyHeatmap 完全一致：跨午夜段计入各自所在日。</summary>
    private IEnumerable<(DateTime day, long sec)> SplitByLocalDay(Row row)
    {
        var now = _nowLocal();
        var start = row.Segment.StartTime.LocalDateTime;
        var end = row.Segment.EndTime.HasValue ? row.Segment.EndTime.Value.LocalDateTime : now;
        if (end <= start)
            yield break;
        var cur = start;
        while (cur < end)
        {
            var dayEnd = cur.Date.AddDays(1);
            var clamp = end < dayEnd ? end : dayEnd;
            yield return (cur.Date, (long)(clamp - cur).TotalSeconds);
            cur = clamp;
        }
    }

    public OverviewStats GetOverview()
    {
        var now = _nowLocal().Date;
        var weekStart = StartOfWeek(now);
        // 窗口化：需覆盖上月整月（PrevMonthSec）+ 本周，进行中段由 end_time IS NULL 兜住
        var prevMonthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
        var from = prevMonthStart.AddDays(-1);
        var rows = LoadSegments(from, now.AddDays(1));
        var byDay = new Dictionary<DateTime, long>();
        foreach (var row in rows)
            foreach (var (day, sec) in SplitByLocalDay(row))
                byDay[day] = byDay.GetValueOrDefault(day) + sec;

        long Sum(Func<DateTime, bool> pred) => byDay.Where(kv => pred(kv.Key)).Sum(kv => kv.Value);

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
        // 窗口化：含前一日以保证跨午夜段覆盖 from 当天，进行中段兜底
        var rows = LoadSegments(fromLocal.AddDays(-1), toLocalExclusive);
        var perDayScenes = new Dictionary<(DateTime, Scene), long>();
        long total = 0;

        foreach (var row in rows)
        {
            var scenes = row.Scenes.Count > 0 ? row.Scenes : [row.Segment.Scene];
            foreach (var (day, sec) in SplitByLocalDay(row))
            {
                if (day < fromLocal.Date || day >= toLocalExclusive.Date)
                    continue;
                total += sec;
                foreach (var scene in scenes)
                {
                    var key = (day, scene);
                    perDayScenes[key] = perDayScenes.GetValueOrDefault(key) + sec;
                }
            }
        }

        var perScene = new Dictionary<Scene, long>();
        foreach (var ((_, scene), sec) in perDayScenes)
            perScene[scene] = perScene.GetValueOrDefault(scene) + sec;

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
        var rows = LoadSegments(from.AddDays(-1), now.AddDays(1));

        var byDay = new Dictionary<DateTime, long>();
        for (var d = from; d <= now; d = d.AddDays(1))
            byDay[d] = 0;
        foreach (var row in rows)
            foreach (var (day, sec) in SplitByLocalDay(row))
                if (byDay.ContainsKey(day))
                    byDay[day] = byDay.GetValueOrDefault(day) + sec;
        return byDay.OrderBy(kv => kv.Key).Select(kv => new DailyDuration(kv.Key, kv.Value)).ToList();
    }

    /// <summary>一周七天 × 24 小时热力图（周一起）。进行中段计入到当前时刻。取最近 90 天聚合（代表近期作息）。</summary>
    public IReadOnlyList<HourCell> GetHourlyHeatmap()
    {
        var now = _nowLocal();
        var from = now.Date.AddDays(-90);
        var grid = new long[7, 24];
        foreach (var row in LoadSegments(from, now.AddDays(1)))
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

    /// <summary>今日各小时时长（本地日界 0-23 时，含零值）。进行中段算到当前时刻。跨午夜段只计今日部分。</summary>
    public IReadOnlyList<HourSec> GetTodayHourly()
    {
        var now = _nowLocal();
        var dayStart = now.Date;
        var dayEnd = dayStart.AddDays(1);
        var hours = new long[24];

        foreach (var row in LoadSegments(dayStart.AddDays(-1), dayEnd))
        {
            var start = row.Segment.StartTime.LocalDateTime;
            var end = row.Segment.EndTime.HasValue ? row.Segment.EndTime.Value.LocalDateTime : now;
            if (end <= dayStart || start >= dayEnd)
                continue; // 与今日无交集

            // 裁到今日 [dayStart, dayEnd) 内再按小时拆分
            var cur = start < dayStart ? dayStart : start;
            var e = end > dayEnd ? dayEnd : end;
            while (cur < e)
            {
                var hourEnd = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
                if (hourEnd > dayEnd)
                    hourEnd = dayEnd;
                var clamp = e < hourEnd ? e : hourEnd;
                hours[cur.Hour] += (long)(clamp - cur).TotalSeconds;
                cur = clamp;
            }
        }

        var list = new List<HourSec>(24);
        for (var h = 0; h < 24; h++)
            list.Add(new HourSec(h, hours[h]));
        return list;
    }

    /// <summary>各耳机使用时长：按会话设备名分组，按会话开始本地日过滤（会话墙钟口径，含无声）。</summary>
    public IReadOnlyList<DeviceDuration> GetDeviceDurations(DateTime fromLocal, DateTime toLocalExclusive)
    {
        var sessions = LoadSessionsByStart(fromLocal.Date, toLocalExclusive.Date);
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
        // 窗口化：含前一日保证跨午夜段不漏，进行中段兜底
        var from = filter.FromLocal ?? new DateTime(2000, 1, 1);
        var until = filter.ToLocalExclusive ?? now.Date.AddDays(1);
        var rows = LoadSegments(from.AddDays(-1), until);
        var result = new List<SegmentView>();

        foreach (var r in rows)
        {
            var d = r.Segment.StartTime.LocalDateTime.Date;
            if (filter.FromLocal.HasValue && d < filter.FromLocal.Value.Date) continue;
            if (filter.ToLocalExclusive.HasValue && d >= filter.ToLocalExclusive.Value.Date) continue;
            if (filter.Scene.HasValue)
            {
                var scenes = r.Scenes.Count > 0 ? r.Scenes : [r.Segment.Scene];
                if (!scenes.Contains(filter.Scene.Value)) continue;
            }
            if (!string.IsNullOrEmpty(filter.DeviceName) && r.DeviceName != filter.DeviceName) continue;
            result.Add(new SegmentView(
                r.Segment.Id, r.Segment.SessionId, r.Segment.StartTime, r.Segment.EndTime,
                EffectiveDuration(r), r.Segment.AppName, r.Segment.WindowTitle, r.Segment.Scene,
                r.Segment.Confirmed, r.Segment.Note,
                r.Scenes.Count > 0 ? r.Scenes : [r.Segment.Scene], r.DeviceName));
        }

        result.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
        if (filter.MaxCount.HasValue && result.Count > filter.MaxCount.Value)
            result.RemoveRange(filter.MaxCount.Value, result.Count - filter.MaxCount.Value);
        return result;
    }

    /// <summary>修改明细：改主场景、全量重写并发标签、备注、确认状态。主场景始终并入标签。</summary>
    public void UpdateSegment(long segmentId, Scene scene, IReadOnlyList<Scene> allScenes, string? note, bool confirmed)
    {
        lock (_gate)
        {
            // 主场景强制并入；并发标签剔除「未标注/其他」等无效场景（主场景本身可为 Other，故保留）
            var tags = new HashSet<Scene> { scene };
            foreach (var s in allScenes)
                if (s is not Scene.Unmarked and not Scene.Other)
                    tags.Add(s);

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
                foreach (var tag in tags)
                {
                    pScene.Value = tag.ToString();
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public int GetSessionCount() => LoadSessions().Count;

    /// <summary>按会话开始日过滤（跨午夜会话整段计入开始日，与设备口径一致）。</summary>
    private List<Session> LoadSessionsByStart(DateTime sinceLocal, DateTime untilLocalExclusive)
    {
        lock (_gate)
        {
            var since = TimeFmt.Utc(new DateTimeOffset(sinceLocal));
            var until = TimeFmt.Utc(new DateTimeOffset(untilLocalExclusive));
            var list = new List<Session>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, device_id, device_name, start_time, end_time, duration_sec FROM sessions
                WHERE start_time >= $since AND start_time < $until;
                """;
            cmd.Parameters.AddWithValue("$since", since);
            cmd.Parameters.AddWithValue("$until", until);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // 防御：坏行（如损坏的时间串）跳过
                if (!TimeFmt.TryParseUtc(reader.GetString(3), out var start))
                    continue;
                list.Add(new Session
                {
                    Id = reader.GetInt64(0),
                    DeviceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    StartTime = start,
                    EndTime = reader.IsDBNull(4) || !TimeFmt.TryParseUtc(reader.GetString(4), out var end)
                        ? null : end,
                    DurationSec = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                });
            }
            return list;
        }
    }

    /// <summary>未确认段数量（待核对治理：供总览显示待核对 chip）。</summary>
    public int GetUnconfirmedCount()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM segments WHERE confirmed = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<SceneRule> GetSceneRules()
    {
        lock (_gate)
        {
            var list = new List<SceneRule>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, app_pattern, title_pattern, scene, enabled FROM scene_rules ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SceneRule
                {
                    Id = reader.GetInt64(0),
                    AppPattern = reader.GetString(1),
                    TitlePattern = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Scene = ParseScene(reader.GetString(3)),
                    Enabled = reader.GetInt64(4) != 0,
                });
            }
            return list;
        }
    }

    /// <summary>按应用进程名写入一条场景规则（覆盖同 app 的旧规则），供明细「此应用固定为某场景」用。</summary>
    public void UpsertSceneRule(string appPattern, Scene scene)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE scene_rules SET scene = $scene, enabled = 1, title_pattern = NULL
                WHERE app_pattern = $app AND (title_pattern IS NULL OR title_pattern = '');
                INSERT INTO scene_rules (app_pattern, title_pattern, scene, enabled)
                SELECT $app, NULL, $scene, 1
                WHERE NOT EXISTS (SELECT 1 FROM scene_rules WHERE app_pattern = $app AND title_pattern IS NULL);
                """;
            cmd.Parameters.AddWithValue("$app", appPattern.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$scene", scene.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<string> GetDeviceNames() =>
        LoadSessions()
            .Select(s => string.IsNullOrEmpty(s.DeviceName) ? "未知设备" : s.DeviceName)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

    private List<Session> LoadSessions(DateTime? sinceLocal = null, DateTime? untilLocalExclusive = null)
    {
        lock (_gate)
        {
            var since = sinceLocal.HasValue ? TimeFmt.Utc(new DateTimeOffset(sinceLocal.Value)) : null;
            var until = untilLocalExclusive.HasValue ? TimeFmt.Utc(new DateTimeOffset(untilLocalExclusive.Value)) : null;
            var list = new List<Session>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, device_id, device_name, start_time, end_time, duration_sec FROM sessions
                WHERE ($since IS NULL OR end_time IS NULL OR end_time >= $since)
                  AND ($until IS NULL OR start_time < $until);
                """;
            cmd.Parameters.AddWithValue("$since", (object?)since ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$until", (object?)until ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // 防御：坏行（如损坏的时间串）跳过
                if (!TimeFmt.TryParseUtc(reader.GetString(3), out var start))
                    continue;
                list.Add(new Session
                {
                    Id = reader.GetInt64(0),
                    DeviceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    StartTime = start,
                    EndTime = reader.IsDBNull(4) || !TimeFmt.TryParseUtc(reader.GetString(4), out var end)
                        ? null : end,
                    DurationSec = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                });
            }
            return list;
        }
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
