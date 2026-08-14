using Microsoft.Win32;

namespace HeadphoneLogger.App;

/// <summary>开机自启：HKCU Run 键开关。</summary>
public sealed class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HeadphoneLogger";

    public bool IsEnabled()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value &&
               value.Equals($"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
    }

    public void Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{exe}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
