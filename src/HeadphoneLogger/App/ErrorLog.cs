namespace HeadphoneLogger.App;

/// <summary>未捕获异常落盘（%USERPROFILE%\headphone-logger\error.log），保证后台程序异常可追溯。</summary>
public static class ErrorLog
{
    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "headphone-logger", "error.log");

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不再抛出，避免二次崩溃
        }
    }
}
