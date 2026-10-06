# 耳机使用记录器（Headphone Logger）

> **English**: A Windows tray app that automatically tracks how long you wear your headphones each day, which pair, and in what scenario (music / video / gaming / online class / meetings / coding).
> It records only while sound is playing, tells headphones apart by output device, tags concurrent scenes (e.g., music + gaming in one session), confirms scene switches with a bubble, and renders offline WebView2 + ECharts statistics — built on .NET 10 with 63 unit tests.
> **Run**: `dotnet run --project src/HeadphoneLogger` on Windows 11 (requires .NET 10 SDK); run tests with `dotnet test`.

> Windows 常驻后台，自动记录你**每天戴了多久耳机、哪副耳机、在做什么场景**（音乐 / 视频 / 游戏 / 网课 / 会议 / 编程）。

<p align="center">
  <img src="src/HeadphoneLogger/HeadphoneLogger.ico" width="72" alt="耳机图标">
</p>

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011-blue)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green)](#license)
[![Tests](https://img.shields.io/badge/Tests-65%20passing-brightgreen)](https://github.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen)](#contributing)

---

## 它是做什么的

一句话：**知道自己戴了多久耳机，以及这些时间花在了哪里。**

- 戴上耳机就开始记录（不需要手动打卡）
- 区分不同耳机（WH-1000XM4、AirPods、3.5mm 音箱……）
- 自动识别场景：音乐 / 视频 / 游戏 / 网课 / 会议 / 编程
- 一个段可以同时打多个标签（**一边听歌一边打游戏**）
- WebView2 + ECharts 统计页，本地离线查看

## 核心特性

| 特性 | 说明 |
|---|---|
| 🎧 **有声音才记录** | 没有声音不产生任何记录；声音开始按当前场景开段、声音停止即收尾。切到微信/QQ 等无声窗口**不记录、不打扰** |
| 💬 **气泡确认** | 跨场景切换且**切换到的窗口正在发声**才弹气泡：`✓ 是` / `✕ 让我改` / `稍后标`，3 分钟无操作自动落库 |
| 🏷️ **并发场景** | 一个段多个标签：听歌 + 打游戏 = 一个段两个标签，重叠如实呈现（场景之和可大于总时长） |
| 🎤 **分耳机统计** | 按 Windows 默认输出设备归属会话，看清每副耳机各用了多久 |
| 📊 **统计窗口** | 总览（卡片 + 场景饼图/时长表 + 今日各小时）· 时段（7×24 热力图 + 30 天每日）· 设备 · 明细（多条件筛选 + 编辑 + 删除 + 导出） |
| 🛡️ **防误记** | 拔除去抖（休眠唤醒不切会话）、音频迟滞（切歌不碎段）、气泡取消补开段不空洞、误插 <60s 自动丢弃 |

## 截图

_（截图待补充 —— 欢迎贡献）_

```
┌─────────────────────────────────────────────────────────────┐
│  耳机使用记录                    总览 · 时段 · 设备 · 明细   │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐                       │
│  │ 今日 2h │ │ 本周 9h │ │ 本月 30h│  待核对 3 段           │
│  └─────────┘ └─────────┘ └─────────┘                       │
│  场景分配                   今日各小时                      │
│  (饼图 + 时长表)            (24 小时柱状)                   │
└─────────────────────────────────────────────────────────────┘
```

## 快速开始

### 环境要求

- Windows 11（Win10 19041+ 也可，需支持 WebView2）
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（仅构建需要）
- WebView2 运行时（Win11 自带；Win10 需安装 [Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)）

### 构建与运行

```bash
dotnet restore
dotnet build -c Release

# 运行
.\src\HeadphoneLogger\bin\Release\net10.0-windows\HeadphoneLogger.exe
# 或
dotnet run --project src/HeadphoneLogger
```

### 测试

```bash
dotnet test
```

65 个测试，覆盖：场景规则引擎、会话状态机（假音频设备/前台驱动）、统计聚合（并发场景重叠、本地日界、设备分组）、明细编辑/删除。

### 使用

- 运行后常驻系统托盘（右下角耳机图标）
- **双击托盘图标** 或右键 → 「查看统计」打开统计窗口
- 托盘菜单：开机自启、打开数据文件夹、退出
- 会话进行中托盘图标变为**录音态**（绿色 + 红点），悬停显示当前耳机
- 首次运行会弹引导气泡

## 数据存储

数据全在本机，无任何云同步：

```
%USERPROFILE%\headphone-logger\
├── headphone-logger.db   # SQLite 数据库（会话 / 段 / 场景标签）
└── error.log             # 运行日志
```

- 数据库可用任意 SQLite 工具打开（[DB Browser for SQLite](https://sqlitebrowser.org/) 等）
- 明细页「导出 CSV」可随时导出备份，或托盘 →「打开数据文件夹」手动备份 `.db`
- 删除 `.db` 即清空所有记录（谨慎操作）

## 技术栈

| 层 | 技术 |
|---|---|
| 语言 / 框架 | C# .NET 10 + WinForms |
| 音频监听 | NAudio（`IMMNotificationClient` 监听默认输出设备变化） |
| 发声进程枚举 | Core Audio COM（`IAudioSessionManager2`） |
| 前台扫描 | Win32 API（`GetForegroundWindow` 轮询，忽略本进程窗口） |
| 存储 | Microsoft.Data.Sqlite（WAL 模式，本地日界聚合） |
| 统计页 | WebView2 + ECharts（本地化，离线可用，数据不出本机） |

## 数据口径说明

> 为什么「设备页」和「总览」数字对不上？这不是 bug。

| 指标 | 口径 | 位置 |
|---|---|---|
| 有声时长 | 只有发声的**段**时长 | 总览卡片、场景、每日、热力图 |
| 佩戴时长 | 会话墙钟（插上到拔下，**含无声**） | 设备页 |

其他口径：跨午夜段按**真实小时**拆分到各自所在日（各处口径一致）；未确认段计入统计，总览提供「待核对」入口与「只看已确认」开关。

## 工作原理

```
耳机插入 ──► 会话开始（静默，不打扰）
   │
   ▼
有声音开始 ──► 按当前场景开段（视频 / 游戏 / …）
   │
   ▼
切到发声窗口 ──► 气泡确认（✓是 / ✕改 / 稍后标）
   │                 ├─ 确认 → 旧段结束，开新段
   │                 └─ 超时/稍后标 → 自动落库（未确认）
   ▼
耳机拔出 ──► 会话结束，写入 SQLite
```

**场景规则**：进程名 + 窗口标题子串匹配，命中即映射到场景。规则存于 `scene_rules` 表，明细页「固定场景」可直接写入新规则并热生效（无需重启）。

## 项目结构

```
headphone-logger/
├── src/HeadphoneLogger/
│   ├── App/          # 开机自启、错误日志
│   ├── Audio/        # NAudio 插拔监听 + Core Audio COM 发声枚举
│   ├── Core/         # 会话状态机、场景规则引擎、前台扫描
│   ├── Storage/      # SQLite 存储 + 统计聚合
│   ├── UI/           # 托盘、气泡确认、WebView2 统计窗
│   └── Assets/       # 统计页 HTML（ECharts）
├── tests/HeadphoneLogger.Tests/  # 63 个 xUnit 测试
├── tools/            # 图标生成脚本
└── docs/superpowers/ # 设计文档与实现计划
```

## 已知简化（v1）

1. 气泡超时 / 忽略 / 「稍后标」一律标未确认（`confirmed=0`），统计仍计入
2. 并发标签在场景切换 / 声音开始时刻采样，不实时跟随（听歌中途停了，标签仍在）
3. 明细默认只下发最近 3000 条（防数据量增长后渲染与 IPC 失控）
4. 场景规则表暂无完整图形化管理界面（明细页「固定场景」为最小形态）
5. 记录的是**默认输出设备**——若你用音箱外放且它是默认端点，会被算作一次「耳机」会话

## Roadmap

- [x] 有声音才记录（无声切换零记录）
- [x] 明细筛选（时间/场景/设备/确认状态/关键词 + 自定义日期）
- [x] 明细编辑与删除（改主场景/并发标签/备注/确认状态；误记可删）
- [x] 待核对治理（chip 入口 + 只看已确认）
- [x] CSV 导出与数据文件夹入口
- [x] 规则热更新（明细「固定场景」）
- [ ] 数据对账时间线（24h 记录覆盖视图）
- [ ] 规则图形化编辑器
- [ ] 每日报告（本地摘要卡片）
- [ ] 设备合并 / 重命名（蓝牙重配对分裂）

## Contributing

欢迎提 Issue 报告 bug / 建议，或直接提 PR。

- 开发规范：改动贴合现有风格、行为敏感处先锁测试、动代码前先跑 `dotnet test` 确认绿色基线

## License

[MIT](LICENSE)
