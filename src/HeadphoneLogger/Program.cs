using System.Runtime.CompilerServices;
using System.Windows.Forms;
using HeadphoneLogger.App;
using HeadphoneLogger.Audio;
using HeadphoneLogger.Core;
using HeadphoneLogger.Storage;
using HeadphoneLogger.UI;

[assembly: InternalsVisibleTo("HeadphoneLogger.Tests")]

namespace HeadphoneLogger;

internal static class Program
{
    private const string MutexName = "HeadphoneLogger_SingleInstance";
    private static Mutex? _mutex;

    [STAThread]
    private static void Main()
    {
        // 全局未捕获异常：托盘/气泡/Timer/COM 回调线程的异常都记日志，避免静默崩溃
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Write(e.ExceptionObject is Exception ex ? ex : new Exception(e.ExceptionObject?.ToString()));

        ApplicationConfiguration.Initialize();

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("耳机使用记录已在运行。", "耳机使用记录",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var conn = AppDatabase.Open(AppDatabase.DefaultPath);
            // 读写两侧共享同一把锁，保护同一 SqliteConnection 不被并发访问
            var gate = new object();
            var store = new SessionStore(conn, gate);
            var stats = new StatsRepository(conn, gate: gate);
            var engine = new SceneRuleEngine(stats.GetSceneRules());
            var monitor = new NAudioDeviceMonitor();
            var scanner = new ForegroundScanner();
            var sessionManager = new SessionManager(store, monitor, engine, scanner);
            var tray = new TrayApp(sessionManager, stats, monitor, scanner, engine, new AutoStart());
            tray.Start();
            Application.Run(new TrayContext(tray));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show($"启动失败：{ex.Message}", "耳机使用记录",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>托盘常驻的 ApplicationContext：消息泵退出时释放全部资源。</summary>
internal sealed class TrayContext : ApplicationContext
{
    public TrayContext(TrayApp tray)
    {
        Application.ApplicationExit += (_, _) => tray.Dispose();
    }
}
