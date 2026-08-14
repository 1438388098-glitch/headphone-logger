using System.Runtime.CompilerServices;
using System.Windows.Forms;

[assembly: InternalsVisibleTo("HeadphoneLogger.Tests")]

namespace HeadphoneLogger;

/// <summary>
/// 入口：真正装配逻辑在 Task 8（单实例 Mutex + 四大模块装配）。
/// 当前为骨架占位，编译通过、可启动消息泵即可。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run();
    }
}
