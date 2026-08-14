# 耳机使用记录器（Headphone Logger）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 一个 Windows 常驻后台的耳机使用记录软件：自动按场景切换分段，气泡确认场景与并发标签，托盘 + WebView2 图表统计。

**Architecture:** 四模块单向依赖：AudioWatcher（设备插拔 + 发声进程）→ SessionManager（会话状态机，落库）→ ForegroundScanner（前台→场景）与 UI（气泡确认、统计窗口）。全部依赖抽象接口，可脱离硬件测试。

**Tech Stack:** C# .NET 10 + WinForms，NAudio（插拔监听），Core Audio COM（发声进程枚举），Microsoft.Data.Sqlite，WebView2 + ECharts，xUnit。

---

## 文件结构

```
D:\Claudeworkspace\headphone-logger\
├── HeadphoneLogger.sln
├── README.md
├── src\HeadphoneLogger\
│   ├── HeadphoneLogger.csproj           # WinForms, net10.0-windows
│   ├── Program.cs                        # 入口：Mutex + 装配 + 托盘
│   ├── App\
│   │   ├── AppConfig.cs                  # 数据库路径、常驻间隔常量
│   │   └── AutoStart.cs                  # 注册表 Run 键开关
│   ├── Audio\
│   │   ├── IAudioDeviceMonitor.cs        # 抽象：插拔事件 + GetSoundingProcessNames()
│   │   ├── NAudioDeviceMonitor.cs        # 实现（NAudio IMMNotificationClient + COM 枚举）
│   │   └── CoreAudioInterop.cs           # Core Audio COM 互操作定义（IAudioSessionEnumerator 等）
│   ├── Core\
│   │   ├── Scene.cs                      # 场景枚举 + 中文名
│   │   ├── Models.cs                     # Session, Segment, SceneDraft
│   │   ├── SceneRule.cs                  # 规则实体 + 初始种子
│   │   ├── SceneRuleEngine.cs            # app/title → scene 匹配
│   │   ├── ForegroundScanner.cs          # 前台轮询 + 场景变化事件
│   │   └── SessionManager.cs             # 会话状态机
│   ├── Storage\
│   │   ├── AppDatabase.cs                # 建表/完整性检查/CRUD
│   │   └── StatsRepository.cs            # 统计聚合 + 明细修改
│   └── UI\
│       ├── TrayApp.cs                    # NotifyIcon + 菜单 + 装配 UI
│       ├── SceneConfirmBubble.cs         # 无边框 TopMost 气泡
│       ├── StatsWindow.cs                # WebView2 + 桥接
│       └── Assets\index.html             # ECharts 统计页
└── tests\HeadphoneLogger.Tests\
    ├── HeadphoneLogger.Tests.csproj
    ├── SceneRuleEngineTests.cs
    ├── SessionManagerTests.cs
    └── StatsRepositoryTests.cs
```

## 关键接口契约（先锁，后实现）

```csharp
// Audio/IAudioDeviceMonitor.cs
public sealed record DeviceInfo(string DeviceId, string DeviceName);

public interface IAudioDeviceMonitor : IDisposable {
    event EventHandler<DeviceInfo> DeviceInserted;  // 携带设备 ID + 友好名
    event EventHandler DeviceRemoved;
    bool IsHeadphonesPresent { get; }        // 启动时基准状态
    DeviceInfo? CurrentDevice { get; }       // 当前默认渲染设备
    IReadOnlyList<string> GetSoundingProcessNames(); // 当前发声进程名集合（小写）
    void Start();
}

// Core/SessionManager.cs
public class SessionManager {
    // 注入：ISessionStore store, IAudioDeviceMonitor monitor, ISceneRuleEngine rules, IForegroundScanner scanner
    public event EventHandler<SceneChangeRequestedEventArgs>? SceneChangeRequested;
    public void ResolvePendingScene(Scene? chosenMain, IReadOnlySet<Scene> concurrent, bool confirmed);
    public IReadOnlyList<Scene> DefaultConcurrentFor(Scene main); // 供气泡勾选初始值
}

// Core/ForegroundScanner.cs
public interface IForegroundScanner : IDisposable {
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;
    ForegroundInfo Current { get; }
    void Start();
}
```

## 数据模型（存储为 UTC ISO8601 文本，聚合按本地日界）

`sessions(id, device_id, device_name, start_time, end_time, duration_sec)`、`segments(id, session_id, start_time, end_time, duration_sec, app_name, window_title, scene, confirmed, note)`、`segment_scenes(segment_id, scene, PK(segment_id,scene))`、`scene_rules(id, app_pattern, title_pattern, scene, enabled)`。

时间字符串格式：`"yyyy-MM-dd'T'HH:mm:ss'Z'"`。读取后 `.ToLocalTime()` 聚合。

---

### Task 1: 项目骨架 + 最小测试基建

- [x] **Step 1:** 创建 `HeadphoneLogger.sln`，`src/HeadphoneLogger`（WinForms `net10.0-windows`）、`tests/HeadphoneLogger.Tests`（xUnit）
- [x] **Step 2:** 加包：主项目 `NAudio`、`Microsoft.Data.Sqlite`、`Microsoft.Web.WebView2`；测试项目 `Microsoft.Data.Sqlite`、`Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`
- [x] **Step 3:** 写 `SmokeTest.cs` 一条真断言测试，`dotnet test` 跑绿
- [x] **Step 4:** 写 `README.md`（目标/运行/测试）
- [x] **Step 5:** 提交 `chore: 项目骨架与测试基建`

### Task 2: 核心模型 + 场景规则引擎

- [x] **Step 1:** `Scene.cs` 枚举 + `Models.cs`（Session/Segment/SceneDraft/SceneRule）
- [x] **Step 2:** `SceneRuleEngine.cs`：按序匹配第一条 enabled 规则，app/title 均为 OrdinalIgnoreCase 子串匹配，title 为空则只按 app；无匹配 → 其他
- [x] **Step 3:** `SceneRuleEngineTests.cs`：匹配/优先级/大小写/禁用/兜底
- [x] **Step 4:** 跑测试，提交 `feat: 场景枚举与规则引擎`

### Task 3: 存储层

- [x] **Step 1:** `AppDatabase.cs`：`Open(path)` 建表 + PRAGMA integrity_check 损坏改名 `.bak` 重建；`CreateSession/EndSession/CreateSegment/FinalizeSegment/AddSceneTags/DeleteSession`
- [x] **Step 2:** `StatsRepository.cs`：`GetOverview/GetSceneAllocation/GetDailyDurations/GetHourlyHeatmap/GetDeviceDurations/GetSegments/UpdateSegment/GetSceneRules`（SQL 见实现）
- [x] **Step 3:** `StatsRepositoryTests.cs`：种子数据 → 验证场景重叠计数、本地日界、明细修改
- [x] **Step 4:** 跑测试，提交 `feat: SQLite 存储与统计聚合`

### Task 4: SessionManager 状态机

- [x] **Step 1:** 实现状态机：插入建会话+首段（confirmed=0 不打扰）；场景切换 → 结束当前段 → 采样发声进程映射并发标签（排除与主场景相同者）→ 抛 `SceneChangeRequested`
- [x] **Step 2:** `ResolvePendingScene`：确认/改场景建新段 confirmed=1；超时（由 UI 调起）confirmed=0；pending 期间再切换 → 合并覆盖
- [x] **Step 3:** 拔出 → 结束段+会话；<60s 且无已确认段 → 删整会话
- [x] **Step 4:** `SessionManagerTests.cs`：假 monitor/假 scanner 驱动全部流转与兜底
- [x] **Step 5:** 跑测试，提交 `feat: 会话状态机`

### Task 5: AudioWatcher

- [x] **Step 1:** `CoreAudioInterop.cs` COM 定义（`IMMDeviceEnumerator/IMMDevice/IAudioSessionManager2/IAudioSessionEnumerator/IAudioSessionControl2`）
- [x] **Step 2:** `NAudioDeviceMonitor.cs`：IMMNotificationClient 监听默认渲染端点变化 → Inserted/Removed（携带 DeviceInfo：PropertyStore 取友好名）；GetSoundingProcessNames 枚举默认渲染设备 Active 会话取进程名
- [x] **Step 3:** 主项目编译通过（此模块不单测，靠手动冒烟 + 状态机假实现已测）
- [x] **Step 4:** 提交 `feat: NAudio 插拔监听与发声进程枚举`

### Task 6: ForegroundScanner

- [x] **Step 1:** Win32 `GetForegroundWindow/GetWindowThreadProcessId/GetWindowText`；Timer 1.5s 轮询
- [x] **Step 2:** 按规则映射主场景，与当前段主场景比较，跨场景 → 触发事件；同场景仅窗口变 → 不触发
- [x] **Step 3:** 提交 `feat: 前台场景扫描`

### Task 7: UI（托盘/气泡/统计窗口）

- [x] **Step 1:** `TrayApp.cs`：NotifyIcon + 菜单（查看统计/开机自启勾选/退出）；双击开统计窗
- [x] **Step 2:** `SceneConfirmBubble.cs`：无边框 TopMost 气泡，显示预填主场景 + 并发标签（可勾选）+「✓是」「✕让我改」（下拉改主场景）；5 分钟 Timer 超时自动确认
- [x] **Step 3:** `StatsWindow.cs` + `Assets/index.html`：ECharts 总览卡片/场景饼图/每日柱状/热力图/明细；明细修改经 `postMessage` ↔ `WebMessageReceived` 写库回刷
- [x] **Step 4:** 提交 `feat: 托盘气泡与统计窗口`

### Task 8: 应用胶水 + 收尾

- [x] **Step 1:** `AutoStart.cs` 注册表开关；`Program.cs` Mutex 单实例 + 装配 + 启动扫描器/监听器
- [x] **Step 2:** `dotnet build -c Release` + `dotnet test` 全绿
- [x] **Step 3:** 提交 `feat: 应用装配与自启`；最终提交 README 收尾

---

## 已知简化（v1 明确接受，README 注明）

1. 跨本地午夜的段，时长记入**段开始日**，不做跨日拆分。
2. 气泡超时/忽略一律 `confirmed=0`，统计仍计入。
3. 并发标签在切换时刻采样，不实时跟随。
4. 统计窗只读展示 + 明细修正，不做高级报表导出。
