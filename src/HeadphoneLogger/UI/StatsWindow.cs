using System.Text.Json;
using HeadphoneLogger.Core;
using HeadphoneLogger.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HeadphoneLogger.UI;

/// <summary>
/// 统计窗口：WebView2 内嵌 ECharts 页。明细修改经双向桥接——
/// 前端 postMessage → WebMessageReceived 写 SQLite → 回传刷新。
/// </summary>
public sealed class StatsWindow : Form
{
    private const string HostName = "app.local";
    private const int MaxDetailSegments = 3000; // 明细只下发最近 N 条，防止 WebView2 IPC 与渲染失控

    private readonly StatsRepository _stats;
    private readonly ISceneRuleEngine _engine;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };
    private int _refreshing; // 防重入：慢查询进行中跳过本次触发
    private SynchronizationContext? _syncContext;

    public StatsWindow(StatsRepository stats, ISceneRuleEngine engine)
    {
        _stats = stats;
        _engine = engine;
        _syncContext = SynchronizationContext.Current;
        Text = "耳机使用统计";
        Width = 1100;
        Height = 860;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFactory.CreateHeadphoneIcon();
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        // 窗口常开期间定时拉最新数据（后台新会话/段能及时呈现）
        _refresh.Tick += (_, _) => PostData();
        _refresh.Start();
    }

    private static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(null);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, AssetsDir, CoreWebView2HostResourceAccessKind.DenyCors);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            // 只允许 app.local 虚拟主机内导航，拦截一切外部/注入导航
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            _web.CoreWebView2.WebMessageReceived += OnMessageReceived;
            _web.Source = new Uri($"https://{HostName}/index.html");
        }
        catch
        {
            // WebView2 运行时缺失：降级提示，不崩溃；停止 30s 轮询（无 WebView2 可推送）
            _refresh.Stop();
            _web.Dispose();
            Controls.Clear();
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "无法初始化 WebView2（缺少 WebView2 运行时）。\n请安装后重试。",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 12f),
            });
        }
    }

    private void OnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = UnwrapWebMessage(e);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "load":
                    PostData();
                    break;
                case "update":
                    HandleUpdate(root);
                    PostData();
                    break;
                case "export":
                    HandleExport(root);
                    break;
                case "setRule":
                    HandleSetRule(root);
                    PostData();
                    break;
            }
        }
        catch
        {
            // 前端消息格式异常：忽略，不打断统计窗口
        }
    }

    /// <summary>导出当前筛选的明细为 CSV（UTF-8 BOM，Excel 中文不乱码）。</summary>
    private void HandleExport(JsonElement root)
    {
        try
        {
            var from = ParseDate(root.GetProperty("from").GetString());
            var to = ParseDate(root.GetProperty("to").GetString());
            var segs = _stats.GetSegments(new SegmentFilter(
                FromLocal: from, ToLocalExclusive: to?.AddDays(1)));

            using var dialog = new SaveFileDialog
            {
                Title = "导出明细",
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"耳机记录_{DateTime.Now:yyyyMMdd-HHmm}.csv",
                DefaultExt = "csv",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("开始时间,时长(秒),场景,并发标签,应用,窗口标题,已确认,备注,设备");
            foreach (var s in segs)
            {
                var scenes = string.Join("|", s.Scenes.Select(x => x.DisplayName()));
                sb.AppendLine(string.Join(",",
                    s.StartTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    s.DurationSec,
                    CsvEscape(s.Scene.DisplayName()),
                    CsvEscape(scenes),
                    CsvEscape(s.AppName ?? ""),
                    CsvEscape(s.WindowTitle ?? ""),
                    s.Confirmed ? "是" : "否",
                    CsvEscape(s.Note ?? ""),
                    CsvEscape(s.DeviceName ?? "")));
            }
            // UTF-8 BOM：Excel 直接打开不乱码
            File.WriteAllText(dialog.FileName, "\uFEFF" + sb, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            HeadphoneLogger.App.ErrorLog.Write(ex);
            MessageBox.Show($"导出失败：{ex.Message}", "耳机使用记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>「此应用固定为某场景」：写入规则并热更新引擎，后续该应用的段直接映射到该场景。</summary>
    private void HandleSetRule(JsonElement root)
    {
        var app = root.GetProperty("app").GetString();
        var scene = ParseScene(root.GetProperty("scene").GetString());
        if (!string.IsNullOrWhiteSpace(app) && app != "null" && app.Length <= 64)
        {
            _stats.UpsertSceneRule(app!, scene);
            _engine.UpdateRules(_stats.GetSceneRules());
        }
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, out var d) ? d : null;

    private static string CsvEscape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    private void HandleUpdate(JsonElement root)
    {
        var id = root.GetProperty("id").GetInt64();
        var scene = ParseScene(root.GetProperty("scene").GetString());
        var scenes = root.GetProperty("scenes").EnumerateArray().Select(x => ParseScene(x.GetString())).ToList();
        var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var confirmed = root.GetProperty("confirmed").GetBoolean();
        _stats.UpdateSegment(id, scene, scenes, note, confirmed);
    }

    /// <summary>外部（数据变化事件）触发的即时刷新。窗口不可见/已释放时安全跳过。</summary>
    public void RefreshNow() => PostData();

    private void PostData()
    {
        if (IsDisposed || !Visible || _web.CoreWebView2 is null)
            return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            return; // 上一次聚合未完成：跳过本次，避免 UI 线程堆积

        // 在 UI 线程先抓取 CoreWebView2 引用（跨线程读控件属性会抛 InvalidOperationException）
        var web = _web.CoreWebView2;
        try
        {
            // 聚合移到后台线程，避免阻塞 UI 交互与 2s 音频轮询；结果回封 UI 线程推送
            _ = Task.Run(() =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(BuildPayload());
                    if (_syncContext is not null)
                    {
                        _syncContext.Post(_ =>
                        {
                            try { web.PostWebMessageAsJson(payload); }
                            catch { /* 窗口已关闭窗口期：丢弃 */ }
                        }, null);
                    }
                    else
                    {
                        try { web.PostWebMessageAsJson(payload); }
                        catch { /* 窗口已关闭窗口期：丢弃 */ }
                    }
                }
                catch (Exception ex)
                {
                    HeadphoneLogger.App.ErrorLog.Write(ex); // 聚合失败可追溯（此前空 catch 静默吞掉）
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _refreshing, 0);
            HeadphoneLogger.App.ErrorLog.Write(ex);
        }
    }

    private object BuildPayload()
    {
        var from = new DateTime(2000, 1, 1);
        var to = new DateTime(2100, 1, 1);
        var overview = _stats.GetOverview();
        var sceneAlloc = _stats.GetSceneAllocation(from, to);
        var daily = _stats.GetDailyDurations(30);
        var todayHourly = _stats.GetTodayHourly();
        var heatmap = _stats.GetHourlyHeatmap();
        var devices = _stats.GetDeviceDurations(from, to);
        var segments = _stats.GetSegments(new SegmentFilter(MaxCount: MaxDetailSegments));
        var unconfirmed = _stats.GetUnconfirmedCount();

        return new
        {
            type = "data",
            data = new
            {
                overview = new
                {
                    todaySec = overview.TodaySec,
                    yesterdaySec = overview.YesterdaySec,
                    weekSec = overview.WeekSec,
                    prevWeekSec = overview.PrevWeekSec,
                    monthSec = overview.MonthSec,
                    prevMonthSec = overview.PrevMonthSec,
                },
                sessionCount = _stats.GetSessionCount(),
                deviceCount = devices.Count,
                unconfirmed = unconfirmed,
                segmentsTruncated = segments.Count == MaxDetailSegments,
                sceneAllocation = new
                {
                    totalSec = sceneAlloc.TotalSec,
                    perScene = sceneAlloc.PerScene.Select(c => new { scene = c.Scene.ToString(), sec = c.Sec }),
                },
                daily = daily.Select(d => new { date = d.LocalDate.ToString("yyyy-MM-dd"), sec = d.TotalSec }),
                todayHourly = todayHourly.Select(h => new { hour = h.Hour, sec = h.Sec }),
                heatmap = heatmap.Select(c => new { weekday = c.Weekday, hour = c.Hour, sec = c.Sec }),
                devices = devices.Select(d => new { name = d.DeviceName, sec = d.TotalSec }),
                segments = segments.Select(s => new
                {
                    id = s.Id,
                    start = TimeFmt.Utc(s.StartTime),
                    durationSec = s.DurationSec,
                    app = s.AppName,
                    title = s.WindowTitle,
                    scene = s.Scene.ToString(),
                    confirmed = s.Confirmed,
                    note = s.Note,
                    device = s.DeviceName ?? "未知设备",
                    scenes = s.Scenes.Select(x => x.ToString()),
                }),
            },
        };
    }

    /// <summary>
    /// postMessage(JSON 字符串) 时 WebMessageAsJson 是带引号的字符串；解一层拿到真正的 JSON。
    /// 若本版本直接给对象，则原样解析。
    /// </summary>
    private static JsonDocument UnwrapWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.WebMessageAsJson;
        using var probe = JsonDocument.Parse(json);
        if (probe.RootElement.ValueKind == JsonValueKind.String)
            return JsonDocument.Parse(probe.RootElement.GetString()!);
        return JsonDocument.Parse(json);
    }

    private static Scene ParseScene(string? s) =>
        Enum.TryParse<Scene>(s, out var scene) ? scene : Scene.Other;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refresh.Dispose();
        base.Dispose(disposing); // 基类负责释放 _web 等子控件
    }
}
