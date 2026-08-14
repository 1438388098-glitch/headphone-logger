using Microsoft.Win32;

namespace HeadphoneLogger.App;

/// <summary>
/// 应用主题偏好（浅色/深色）。持久化到 HKCU\Software\HeadphoneLogger\DarkMode。
/// 统计窗口（WebView2）与气泡（WinForms）共享此状态；主题变化通过 <see cref="ThemeChanged"/> 广播。
/// </summary>
public static class ThemeSettings
{
    private const string KeyPath = @"Software\HeadphoneLogger";
    private const string ValueName = "DarkMode";

    private static bool? _dark;

    /// <summary>当前是否深色主题（惰性读取注册表，缓存于进程内）。</summary>
    public static bool Dark
    {
        get
        {
            if (_dark is not null)
                return _dark.Value;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                _dark = key?.GetValue(ValueName) is int v && v != 0;
            }
            catch
            {
                _dark = false;
            }
            return _dark.Value;
        }
    }

    /// <summary>主题切换（统计窗/托盘调用），持久化并广播给正在显示的气泡。</summary>
    public static void Toggle()
    {
        SetDark(!Dark);
    }

    public static void SetDark(bool dark)
    {
        if (_dark == dark)
            return;
        _dark = dark;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, dark ? 1 : 0);
        }
        catch
        {
            // 注册表不可写：仅内存生效，不影响主流程
        }
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>主题变化事件：气泡等需要即时换肤的订阅方监听。</summary>
    public static event EventHandler? ThemeChanged;
}
