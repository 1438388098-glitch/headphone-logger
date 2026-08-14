using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HeadphoneLogger.Core;

public interface IForegroundScanner : IDisposable
{
    /// <summary>前台进程/窗口变化（去抖后）。是否算场景切换由 SessionManager 判定。</summary>
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    /// <summary>最近一次采样的前台信息（插入时用于首段预填）。</summary>
    ForegroundInfo Current { get; }

    void Start();

    /// <summary>会话空闲时暂停轮询（省 CPU）；恢复会话时再启用。启用即补一次采样。</summary>
    void SetActive(bool active);
}

public sealed class ForegroundChangedEventArgs : EventArgs
{
    public required ForegroundInfo Foreground { get; init; }
}

/// <summary>
/// 前台扫描：WinForms Timer 轮询前台窗口（进程名 + 窗口标题），变化即触发事件。
/// 忽略属于本进程的窗口（气泡/统计窗），避免自我干扰。UI 线程运行。
/// 是否算场景切换由 SessionManager 按规则判定。
/// </summary>
public sealed class ForegroundScanner : IForegroundScanner
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private ForegroundInfo _current = new(null, null);
    private bool _started;

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;
    public ForegroundInfo Current => _current;

    public ForegroundScanner(TimeSpan? interval = null)
    {
        _timer = new System.Windows.Forms.Timer
        {
            Interval = (int)(interval ?? TimeSpan.FromSeconds(1.5)).TotalMilliseconds,
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        Poll();
        _timer.Start();
    }

    /// <summary>会话空闲暂停轮询：省掉 1.5s 一次的 Win32 采样与进程查询。启用时补一次采样保证 Current 新鲜。</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Poll();
            if (!_timer.Enabled)
                _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private void Poll()
    {
        var info = CaptureForeground();
        if (info is null)
            return; // 前台是我们的气泡等，忽略本次采样

        if (info == _current)
            return; // 进程/标题均未变

        _current = info;
        ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs { Foreground = info });
    }

    private ForegroundInfo? CaptureForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid)
            return null; // 本进程窗口（气泡/统计窗）不视为场景

        var title = GetWindowText(hwnd);
        string? process = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch
        {
            // 进程刚好退出：保留标题，进程名留空
        }
        return new ForegroundInfo(process, title);
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
