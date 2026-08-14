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
        Store = new SessionStore(Conn);
        Stats = new StatsRepository(Conn, nowLocal);
    }

    public void Dispose()
    {
        Conn.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
