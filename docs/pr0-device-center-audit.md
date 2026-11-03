# PR-0 代码与数据审计 —— Device Center（综合外设管理平台）

审查基线：commit `d0d9a02`（2026-11-04），方案《KeyInsight_综合外设管理平台_完整架构与实施方案_含新Logo.docx》。
本审计按方案 §13.2 逐项核对真实代码，形成文件级改造清单；本 PR 不改业务代码（品牌资产替换除外，见 §6）。

## 1. 项目结构 / Target Framework / 语言版本

- 无 .csproj/.sln：`tools/build.ps1` 用 `csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`）直接编译全仓库 glob（排除 `\Assets\|\Tests\|artifacts\|\Updater\`）。
- Target Framework：**.NET Framework 4.8**（引用 `System.IO.Compression.dll` 等 4.8 程序集）；语言版本 **C# 5**。
  - 约束落地：不可用字符串插值、`out var`、`is T name`、`?.`、`record`、NRE 引用类型标注、`IAsyncEnumerable`。
  - 方案 §5.3 的 `Task`/`async` 接口草案需调整为**同步接口**（枚举为快速 P/Invoke，调用方在后台线程执行后经既有的 `ISynchronizeInvoke.Post` 回 UI；本仓库 UI 回调模式为 `ISync`/`SynchronizationContext.Post`）。
- 平台目标：AnyCPU（csc 默认）；P/Invoke 需按 IntPtr 尺寸写结构（`Marshal.SizeOf(typeof(X))`）。
- 新增目录会被 build.ps1 glob 自动纳入编译（`Core/`、`Adapters/`、`Services/`、`Storage/DeviceStore` 均可行），无需改构建脚本。

## 2. KDS 键鼠数据 / Hook / 统计链路（必须不动）

- 采集：`Hook/DebounceHook.cs`、`Hook/MouseHook.cs`（含 PR-1 新增 `Core/Input/WheelAccumulator.cs` 滚轮累计器）、`Hook/RawInputTracker.cs`（设备画像 devices.v1.json）。
- 存储：`Stats/MonthFile.cs`（KDS1 月文件，# 头 + D/M/K/A/AK/MB/AMB 行）、`Stats/UsageSidecar.cs`（usage-YYYY-MM.jsonl）、`Stats/DataVersionManager.cs`（Event/History/Diagnostic/Device/Epoch 五版本计数）。
- 结论：**KDS 格式与写入路径零改动**。方案 §10.1 的 DeviceCenter 独立数据域与本仓现状一致（现有键鼠统计不迁移，经服务层映射）。

## 3. 自动更新体系（必须不回归）

- `Update/Routing/RouteCatalog.cs`：官方 + 4 公共镜像 + raw 兜底、allowlist、健康/熔断。
- `Update/Orchestration/UpdateOrchestrator.cs`：`CheckOnly`/`CheckAndDownload`；`KeyInsight.cs` 托盘链路已改道编排器。
- `Update/Download/ResumableDownloader.cs`：Range 续传 + 整文件 SHA-256 + 镜像 URL 展开；`Updater/UpdaterMain.cs`：备份/回滚/健康心跳。
- 结论：方案 §12.4 的更新兼容要求**已满足**，本轮不触碰 Update/ 与 Updater/。

## 4. UI 框架 / 导航 / 线程调度 / DPI

- 主窗体：`UI/DashboardForm.cs`（自绘、TitleHeight=58、SidebarWidth=204；`_pages[]` + `_groupPages` 五组导航；`LayoutPages()` 统一布局；窗口按钮自绘）。
- 页面基类：`UI/Pages/PageBase.cs`（viewport + PageCanvas 子画布滚动、`MinContentHeight`、`ContentH/ContentW`）。
- 主题：`UI/Theme.cs`（蓝色系 Palette；方案 §11.2 要求的蓝色设计系统已具备，无需重做）。
- 新页面接入点：`DashboardForm` 构造器 `_pages` 数组 + `_groupPages`/`_navLabels`（当前 5 组 9 页；新增"设备中心/显示设备"需扩组）。
- 线程模式：后台 `ThreadPool.QueueUserWorkItem` + `_sync.Post` 回 UI；`IShellActions`（`UI/IShellActions.cs`）是页面与壳的边界 —— 设备服务经 Shell 注入页面，不在 UI 线程做枚举。
- DPI：窗口最小 1000×680；新页面必须给出 `MinContentHeight` 覆盖（Health 780/Mouse 800/Advanced 780 先例）。

## 5. 设备相关现状（复用与缺口）

| 能力 | 现状 | Device Center 处置 |
|---|---|---|
| 输入设备画像 | `Hook/RawInputTracker.cs`（VID/PID、devices.v1.json schema 2） | 保留；Adapter 桥接为输入设备档案 |
| 键鼠使用统计 | `Stats/StatsEngine.cs`（KDS/CSV/聚合） | 经统计映射为 UsageAggregate，不重复采集 |
| PnP 发现 | 无 | 新建 `Adapters/Pnp/`（SetupAPI/CM_ 枚举 + WM_DEVICECHANGE） |
| 显示 | 无（仅自绘 UI） | 新建 `Adapters/Display/`（EDID 注册表 + EnumDisplaySettings + QueryDisplayConfig + HDR） |
| 音频/USB/摄像头 | 无 | 本轮不实施（方案 P3-P5，接口预留） |
| 诊断 | `DiagnosisWizardForm`（键盘防抖专用） | 保留；显示器体检独立（`MonitorTestForm`） |
| 存储 | JSON 原子写先例：`Core/Policy/KeyPolicyStore.cs`（BeginEdit/Revert/原子替换） | 新 `Storage/DeviceStore/` 复用该模式（SQLite 成本高，按方案 §10.1 允许保持抽象） |

## 6. 品牌资产（方案 B）

- 方案提供概念 PNG（`brand-icon-concept.png`，1024²）。已生成圆角透明 256² PNG 与 16-256 七尺寸 ICO，替换：
  - `Assets/app.ico`（窗体图标、托盘图标 —— 两处均读内嵌资源 `app.ico`；`Assets/app256.png` 为标题栏 logo）
  - `Assets/app256.png`
  - `installer/KeyInsight.iss` 的 `SetupIconFile` 指向 `..\Assets\app.ico`，自动生效。
- 限制记录：托盘 16px 为概念稿直接缩放，方案 §2.3 要求的"独立简化单色版"留待矢量重绘后替换（已在品牌清单标注为待办）。

## 7. 文件级改造清单（PR-1..PR-7）

| PR | 新增/修改 | 说明 |
|---|---|---|
| PR-1 | `Core/Devices/*.cs`、`Core/Devices/DeviceIdentityResolver.cs`、`Core/Devices/DeviceLifecycle.cs`、`Core/Devices/DeviceEventCoalescer.cs` | 统一模型/身份/生命周期/事件合并；纯逻辑，不依赖 WinForms |
| PR-2 | `Adapters/Pnp/PnpDeviceAdapter.cs`、`Adapters/Pnp/PnpInterop.cs`、`Services/DeviceCenter/DeviceCenterService.cs` | SetupAPI/CM 枚举 + WM_DEVICECHANGE 消息窗 + 防抖合并 |
| PR-3 | `Storage/DeviceStore/DeviceStore.cs`、`DeviceEventLog.cs`、`DiagnosticStore.cs` | JSON 原子写 + SchemaVersion + 备份迁移；KDS 零接触 |
| PR-4 | `UI/Pages/DeviceCenterPage.cs`、`UI/DeviceDetailForm.cs` + `UI/DashboardForm.cs`（导航接线）+ `UI/IShellActions.cs` | 缓存优先列表 + 六段式详情骨架 |
| PR-5 | `Adapters/Display/DisplayAdapter.cs`、`DisplayInterop.cs`、`EdidParser.cs` | EDID/模式/拓扑/HDR；来源字段化 |
| PR-6 | `UI/Pages/DisplayCenterPage.cs` | 多屏拓扑 + 显示器档案卡（当前模式 ≠ 支持模式） |
| PR-7 | `UI/MonitorTestForm.cs` | 六类全屏图案向导 + DiagnosticSession/Finding（UserConfirmed） |
| 测试 | `Tests/Storage/DeviceStoreTests.cs`、`Tests/Core/DeviceCoreTests.cs` | 模型/身份/防抖合并/存储回滚；不依赖真实设备 |

## 8. 风险清单（方案 §14.2 对照本仓）

1. **C#5**：所有新代码禁用 C#6+ 语法（已列入执行规范）。
2. **身份误合并**：`DeviceIdentityResolver` 输出置信度，低于阈值不自动合并（两台同型号显示器场景测试覆盖）。
3. **WMI 不使用**：方案 §7.1 边界 —— 枚举全走 SetupAPI/CM_/GDI Display API，避免 WMI 卡顿面。
4. **设备风暴**：`DeviceEventCoalescer` 按 InstanceKey 分组 quiet window 350ms / settle 1200ms / max coalesce 3s / backoff 0.5-5s。
5. **QueryDisplayConfig 结构体较大**：interop 独立文件，失败降级为 GDI 路径信息，能力缺失显示"未知/未读取"。
6. **显示体检主观性**：Finding `EvidenceSource=UserConfirmed`，Confidence 置空（方案 §15.2）。
7. **便携版**：新存储仅 JSON 文件（`data/devicecenter/`），随目录走；无新运行库依赖。
