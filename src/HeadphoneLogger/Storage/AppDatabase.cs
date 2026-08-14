using HeadphoneLogger.App;
using HeadphoneLogger.Core;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Storage;

/// <summary>数据库初始化：完整性检查（损坏改名 .bak 重建）、建表、种子规则。</summary>
public static class AppDatabase
{
    /// <summary>默认数据库路径：%USERPROFILE%\headphone-logger\headphone-logger.db</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "headphone-logger",
            "headphone-logger.db");

    public static SqliteConnection Open(string path)
    {
        EnsureIntegrity(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Pooling=False：应用只持一条长连接；关闭即可释放文件句柄（便于测试清理与备份轮换）
        var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA foreign_keys=ON;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=3000;
                """;
            pragma.ExecuteNonQuery();
        }
        CreateSchema(conn);
        SeedRulesIfEmpty(conn);
        return conn;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT,
                device_name TEXT,
                start_time TEXT NOT NULL,
                end_time TEXT,
                duration_sec INTEGER
            );

            CREATE TABLE IF NOT EXISTS segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                start_time TEXT NOT NULL,
                end_time TEXT,
                duration_sec INTEGER,
                app_name TEXT,
                window_title TEXT,
                scene TEXT NOT NULL,
                confirmed INTEGER NOT NULL DEFAULT 0,
                note TEXT
            );

            CREATE TABLE IF NOT EXISTS segment_scenes (
                segment_id INTEGER NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
                scene TEXT NOT NULL,
                PRIMARY KEY (segment_id, scene)
            );

            CREATE INDEX IF NOT EXISTS idx_segments_session ON segments(session_id);
            CREATE INDEX IF NOT EXISTS idx_segments_start ON segments(start_time);

            CREATE TABLE IF NOT EXISTS scene_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                app_pattern TEXT NOT NULL,
                title_pattern TEXT,
                scene TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1
            );

            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void SeedRulesIfEmpty(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM scene_rules;";
        var count = Convert.ToInt64(check.ExecuteScalar());
        if (count > 0)
            return;

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO scene_rules (id, app_pattern, title_pattern, scene, enabled)
            VALUES ($id, $app, $title, $scene, 1);
            """;
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pApp = insert.Parameters.Add("$app", SqliteType.Text);
        var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
        var pScene = insert.Parameters.Add("$scene", SqliteType.Text);

        foreach (var rule in SceneRuleSeed.DefaultRules())
        {
            pId.Value = rule.Id;
            pApp.Value = rule.AppPattern;
            pTitle.Value = rule.TitlePattern is null ? DBNull.Value : rule.TitlePattern;
            pScene.Value = rule.Scene.ToString();
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>完整性检查：损坏则原文件改名 .bak 后重建空库（数据防丢，问题留待人工处理）。</summary>
    private static void EnsureIntegrity(string path)
    {
        if (!File.Exists(path))
            return;

        SqliteConnection? conn = null;
        try
        {
            // 只读打开跑 integrity_check，避免在坏库上先建连失败
            conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
        }
        catch (SqliteException)
        {
            conn?.Dispose();
            Quarantine(path);
            return;
        }

        string? result;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;"; // 比 integrity_check 快约 5 倍，百万行启动不卡顿
            result = (string?)cmd.ExecuteScalar();
        }
        finally
        {
            // 必须先释放连接（文件句柄）才能改名；否则 Windows 上 File.Move 因共享冲突抛异常
            conn.Dispose();
        }

        if (result != "ok")
            Quarantine(path);
    }

    /// <summary>损坏库改名保留，毫秒+随机后缀避免同秒重名；失败则降级（尝试原路径继续打开）。</summary>
    private static void Quarantine(string path)
    {
        var backup = $"{path}.{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        try
        {
            File.Move(path, backup);
        }
        catch (Exception ex)
        {
            // 文件被占用/权限不足等情况：记日志降级，交由 Open 路径处理，不让启动崩溃
            ErrorLog.Write(ex);
        }
    }
}
