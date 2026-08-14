using System.Reflection;
using Microsoft.Win32;

namespace HeadphoneLogger.App;

/// <summary>开机自启：HKCU Run 键开关。所有方法失败时不抛异常（返回结果/静默），避免拖垮托盘菜单。</summary>
public sealed class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HeadphoneLogger";

    public bool IsEnabled()
    {
        var cmd = ResolveStartCommand();
        if (cmd is null)
            return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value &&
                   value.Equals(cmd, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool Enable()
    {
        var cmd = ResolveStartCommand();
        if (cmd is null)
            return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, cmd);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析自启命令。apphost 启动时进程路径即本应用 exe；若通过 <c>dotnet</c> 启动
    /// 或路径为空，则注册 <c>dotnet + 程序集路径</c>，保证自启可真正拉起应用。
    /// </summary>
    private static string? ResolveStartCommand()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) &&
            !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return $"\"{exe}\"";

        var assembly = typeof(AutoStart).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
            return null;
        return $"\"dotnet\" \"{assembly}\"";
    }
}
