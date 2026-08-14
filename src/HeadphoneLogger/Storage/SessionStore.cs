using HeadphoneLogger.Core;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Storage;

/// <summary>SessionManager 需要的落库能力（写入侧）。</summary>
public interface ISessionStore
{
    Session CreateSession(DeviceInfo? device, DateTimeOffset startTime);
    void EndSession(long sessionId, DateTimeOffset endTime);
    Segment CreateSegment(long sessionId, DateTimeOffset startTime, string? appName, string? windowTitle,
        Scene scene, bool confirmed, IReadOnlySet<Scene> scenes, string? note = null);
    void FinalizeSegment(long segmentId, DateTimeOffset endTime);
    void DeleteSession(long sessionId);
    /// <summary>启动时收尾：所有悬挂会话/段补 end_time（关机、睡眠、崩溃遗留）。</summary>
    void CloseHangingSessions(DateTimeOffset until);
}

public sealed class SessionStore : ISessionStore
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SessionStore(SqliteConnection conn) => _conn = conn;

    public Session CreateSession(DeviceInfo? device, DateTimeOffset startTime)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (device_id, device_name, start_time)
                VALUES ($did, $dname, $start);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$did", (object?)device?.DeviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dname", (object?)device?.DeviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", TimeFmt.Utc(startTime));
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            return new Session { Id = id, DeviceId = device?.DeviceId, DeviceName = device?.DeviceName, StartTime = startTime };
        }
    }

    public void EndSession(long sessionId, DateTimeOffset endTime)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sessions
                SET end_time = $end,
                    duration_sec = MAX(0, CAST(ROUND((julianday($end) - julianday(start_time)) * 86400) AS INTEGER))
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$end", TimeFmt.Utc(endTime));
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public Segment CreateSegment(long sessionId, DateTimeOffset startTime, string? appName, string? windowTitle,
        Scene scene, bool confirmed, IReadOnlySet<Scene> scenes, string? note = null)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            long id;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO segments (session_id, start_time, app_name, window_title, scene, confirmed, note)
                    VALUES ($sid, $start, $app, $title, $scene, $confirmed, $note);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$start", TimeFmt.Utc(startTime));
                cmd.Parameters.AddWithValue("$app", (object?)appName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$title", (object?)windowTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$scene", scene.ToString());
                cmd.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            InsertScenes(tx, id, scenes);
            tx.Commit();
            return new Segment
            {
                Id = id, SessionId = sessionId, StartTime = startTime, AppName = appName,
                WindowTitle = windowTitle, Scene = scene, Confirmed = confirmed, Note = note,
            };
        }
    }

    public void FinalizeSegment(long segmentId, DateTimeOffset endTime)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE segments
                SET end_time = $end,
                    duration_sec = MAX(0, CAST(ROUND((julianday($end) - julianday(start_time)) * 86400) AS INTEGER))
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$end", TimeFmt.Utc(endTime));
            cmd.Parameters.AddWithValue("$id", segmentId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteSession(long sessionId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    public void CloseHangingSessions(DateTimeOffset until)
    {
        lock (_gate)
        {
            var utc = TimeFmt.Utc(until);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE segments
                SET end_time = $until,
                    duration_sec = MAX(0, CAST(ROUND((julianday($until) - julianday(start_time)) * 86400) AS INTEGER))
                WHERE end_time IS NULL;

                UPDATE sessions
                SET end_time = $until,
                    duration_sec = MAX(0, CAST(ROUND((julianday($until) - julianday(start_time)) * 86400) AS INTEGER))
                WHERE end_time IS NULL;
                """;
            cmd.Parameters.AddWithValue("$until", utc);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertScenes(SqliteTransaction tx, long segmentId, IReadOnlySet<Scene> scenes)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO segment_scenes (segment_id, scene) VALUES ($sid, $scene);";
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pScene = cmd.Parameters.Add("$scene", SqliteType.Text);
        pSid.Value = segmentId;
        foreach (var scene in scenes)
        {
            pScene.Value = scene.ToString();
            cmd.ExecuteNonQuery();
        }
    }
}
