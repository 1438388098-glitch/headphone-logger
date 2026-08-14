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

    private readonly StatsRepository _stats;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public StatsWindow(StatsRepository stats)
    {
        _stats = stats;
        Text = "耳机使用统计";
        Width = 1100;
        Height = 860;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFactory.CreateHeadphoneIcon();
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(null);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, AssetsDir, CoreWebView2HostResourceAccessKind.DenyCors);
            _web.CoreWebView2.WebMessageReceived += OnMessageReceived;
            _web.Source = new Uri($"https://{HostName}/index.html");
        }
        catch
        {
            // WebView2 运行时缺失：降级提示，不崩溃
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
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
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
            }
        }
        catch
        {
            // 前端消息格式异常：忽略，不打断统计窗口
        }
    }

    private void HandleUpdate(JsonElement root)
    {
        var id = root.GetProperty("id").GetInt64();
        var scene = ParseScene(root.GetProperty("scene").GetString());
        var scenes = root.GetProperty("scenes").EnumerateArray().Select(x => ParseScene(x.GetString())).ToList();
        var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var confirmed = root.GetProperty("confirmed").GetBoolean();
        _stats.UpdateSegment(id, scene, scenes, note, confirmed);
    }

    private void PostData()
    {
        var payload = BuildPayload();
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private object BuildPayload()
    {
        var from = new DateTime(2000, 1, 1);
        var to = new DateTime(2100, 1, 1);
        var overview = _stats.GetOverview();
        var sceneAlloc = _stats.GetSceneAllocation(from, to);
        var daily = _stats.GetDailyDurations(30);
        var heatmap = _stats.GetHourlyHeatmap();
        var devices = _stats.GetDeviceDurations(from, to);
        var segments = _stats.GetSegments(new SegmentFilter());

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
                sceneAllocation = new
                {
                    totalSec = sceneAlloc.TotalSec,
                    perScene = sceneAlloc.PerScene.Select(c => new { scene = c.Scene.ToString(), sec = c.Sec }),
                },
                daily = daily.Select(d => new { date = d.LocalDate.ToString("yyyy-MM-dd"), sec = d.TotalSec }),
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
                    scenes = s.Scenes.Select(x => x.ToString()),
                }),
            },
        };
    }

    private static Scene ParseScene(string? s) =>
        Enum.TryParse<Scene>(s, out var scene) ? scene : Scene.Other;
}
