using HeadphoneLogger.Storage;
using Microsoft.Data.Sqlite;

namespace HeadphoneLogger.Tests;

/// <summary>每个测试独立临时库，用完即删。</summary>
public sealed class TestDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hpl-{Guid.NewGuid():N}.db");

    public SqliteConnection Conn { get; }
    public SessionStore Store { get; }
    public StatsRepository Stats { get; }

    public TestDb(Func<DateTime>? nowLocal = null)
    {
        Conn = AppDatabase.Open(_path);
        var gate = new object(); // 读写侧共享同一把锁，与生产装配一致
        Store = new SessionStore(Conn, gate);
        Stats = new StatsRepository(Conn, nowLocal, gate);
    }

    public void Dispose()
    {
        Conn.Dispose();
        // WAL 模式会产生 -wal/-shm 伴生文件，一并清理
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _path + suffix;
            if (File.Exists(f))
                File.Delete(f);
        }
    }
}
