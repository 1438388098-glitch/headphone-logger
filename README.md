# 耳机使用记录器（Headphone Logger）

Windows 常驻后台的耳机使用记录小软件：自动按**场景切换**分段记录使用时长，气泡确认场景，托盘 + 图表窗口看统计。

## 一句话目标

知道自己戴了多久耳机，以及这些时间花在音乐 / 视频 / 游戏 / 网课 / 会议 / 编程上各多少。

## 如何运行

前置：Windows 11、.NET 10 SDK（构建时）、WebView2 运行时（Win11 自带）。

```bash
dotnet restore
dotnet run --project src/HeadphoneLogger   # 或 release 构建后运行 exe
```

运行后常驻系统托盘：
- 双击托盘图标 → 统计窗口
- 托盘菜单 →「查看统计」「开机自启」「退出」

首次启动会在用户目录创建数据库（`%USERPROFILE%\headphone-logger\headphone-logger.db`）。

## 如何测试

```bash
dotnet test
```

覆盖：场景规则引擎、会话状态机（用假音频设备/前台驱动）、统计聚合（含并发场景重叠与本地日界）。

## 技术栈

C# .NET 10 + WinForms · NAudio（耳机插拔监听）· Core Audio COM（发声进程枚举）· Microsoft.Data.Sqlite · WebView2 + ECharts

## 已知简化（v1）

1. 跨本地午夜的段，时长记入**段开始日**，不做跨日拆分。
2. 气泡超时/忽略一律标未确认（`confirmed=0`），统计仍计入。
3. 并发标签在场景切换时刻采样，不实时跟随。
4. 统计窗为只读展示 + 明细修正，不做高级报表导出。
