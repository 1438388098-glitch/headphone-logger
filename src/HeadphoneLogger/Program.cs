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
            var store = new SessionStore(conn);
            var stats = new StatsRepository(conn);
            var engine = new SceneRuleEngine(stats.GetSceneRules());
            var monitor = new NAudioDeviceMonitor();
            var scanner = new ForegroundScanner();
            var sessionManager = new SessionManager(store, monitor, engine, scanner);
            var tray = new TrayApp(sessionManager, stats, monitor, scanner, new AutoStart());
            tray.Start();
            Application.Run(new TrayContext(tray));
        }
        catch (Exception ex)
        {
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
