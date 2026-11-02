# PR P0-01 + P0-02 — 测试基线与采集/过滤解耦

> 对应任务书：`03_KeyInsight_Agent_Taskbook.md` Task P0-01、Task P0-02（v2.2 第一轮）。
> 基线 commit：`f74cedc`（v2.1.2，2026-11-02）。与设计文档研究的 v2.1.1 相比，
> v2.1.1→v2.1.2 仅改动 `KeyInsight.cs`（手动更新检查）、UI 页面与文档，**`Hook/`、`Stats/` 无变化**，
> 设计文档对该两层的审计结论仍然成立。

## 目标与不修改范围

**目标**
1. 为现有键盘防抖与 KDS1 统计建立可执行测试基线（黄金轨迹、黄金 KDS1、CSV 快照、差分回放）。
2. 将「采集键盘事件」与「是否启用防抖过滤」解耦：`FilterEnabled=false` 时仍记录键盘 Down 并全部放行。
3. 保留已启用防抖用户的输入行为（LEG-001 差分逐事件一致）。

**明确不修改**：UI 导航结构、8 个页面布局、KDS1 字段定义与 CSV 列、`MouseHook`（其统计本就不受
`Enabled` 门控；同步 `Log()` 移除属 P0-05）、未引入新诊断算法、未把任何新策略设为默认。

## 当前代码基线 / 受影响文件

| 文件 | 变更 |
|---|---|
| `Core/Policy/LegacyPolicyAdapter.cs` | **新增**：v2.1.x 防抖判定的纯逻辑复刻（无 Windows API / 时钟 / I/O） |
| `Core/Telemetry/TelemetrySink.cs` | **新增**：`ITelemetrySink`（P0-02 初期接口，签名按设计文档 `TryPublish`）+ `StatsEngineSink` 过渡适配 + `NullTelemetrySink` |
| `Config.cs` | **新增**：`Config`/`AppInfo` 自 `KeyInsight.cs` 纯文件搬移（无行为变化）；`Enabled` → `FilterEnabled` + `CaptureEnabled`；ini 仍读写旧键 `enabled=` |
| `Hook/DebounceHook.cs` | 判定迁入 `LegacyPolicyAdapter`；统计经 `ITelemetrySink`；回调改为「识别 → 过滤/采集 → 放行」；新增 `(cfg, ITelemetrySink)` 构造，旧 `(cfg, StatsEngine)` 构造保留 |
| `KeyInsight.cs` | 托盘开关迁至 `FilterEnabled`，文案标注「防抖过滤：已关闭（仍统计）」；装配显式 `StatsEngineSink` |
| `UI/DashboardForm.cs`、`UI/Pages/DebouncePage.cs`、`UI/Pages/SettingsPage.cs` | `Cfg.Enabled` → `Cfg.FilterEnabled`；开关说明文案改为「仅控制过滤；关闭后仍继续统计按键」 |
| `Tests/**` | **新增**：独立测试框架 + 24 个测试（Core 12 / Storage 7 / 其余 5，见下） |
| `tools/test.ps1` | **新增**：Core+Stats+Hook+Config+Tests 编译为独立 `artifacts/KeyInsight.Tests.exe` 并执行 |
| `tools/build.ps1` | glob 排除 `Tests/` 与 `artifacts/`，主 exe 不含测试代码 |
| `.github/workflows/build.yml`、`release.yml` | 新增 `Core and storage tests` 步骤（release 同样设门禁），`--selftest` 保留 |
| `.gitignore` | 增加 `artifacts/` |

## 新增接口与数据口径

- `ITelemetrySink.TryPublish(KeyEvent)`：钩子线程唯一统计出口；实现不得阻塞、不得抛异常。
  参数暂为旧 `KeyEvent` 模型，P0-05 换 `TelemetryEvent` + 有界队列时接口名保持。
- `Config.FilterEnabled`：即旧 `Enabled`（防抖开关，只控制过滤）。ini 兼容：读 `enabled=` 与
  `filterenabled=`（后写覆盖先写，两键值恒相同），写时两者都输出。
- `Config.CaptureEnabled`：默认 `true`；`false` 时钩子只放行不统计（本版无 UI 开关，预留语义）。
- 关闭过滤期间的口径：**不更新过滤状态字典**——重新开启过滤后从「无历史」安全起步，首个 Down 必放行。
- `LegacyPolicyAdapter` 决策边界（与 v2.1.x 一致）：`gapDown < ThresholdMs || gapUp < max(ThresholdMs, 40)`
  即拦截；被拦 Down 不刷新 `lastDown`；Up 恒放行并更新 `lastUp`（「被拦 Down 的 Up 泄露」为冻结的旧遗留语义，未悄悄修复）。

## 输入序列示例（修改前 / 后）

`Enabled=false`（旧）与 `FilterEnabled=false`（新），同一序列 `D(0) U(40) D(50) U(70)`：

```text
旧：HookCallback 整体被 _cfg.Enabled 短路 → 无统计、无 seen，事件全部放行
    （托盘「今日 N 键」在关闭期间冻结不动）
新：事件仍进入过滤分支判断 → 全部放行，但 Down 继续入统计与 seen
    （关闭期间「今日 N 键」继续累计；重新开启过滤后首按必放行）
```

`Enabled=true`（旧）与 `FilterEnabled=true`（新）：决策序列逐事件一致（LEG-001 随机差分 8 种子 × 3 阈值 × 300 事件全等）。

## 失败 / 降级 / 回滚策略

- 回调异常处理保持原样：捕获 → `LogLine` → `CallNextHookEx` 放行（Fail Open），未引入新的可抛异常路径。
- `StatsEngineSink.TryPublish` 内部 try/catch，统计层异常不会中断输入。
- 回滚方式：`git revert` 本轮提交即可回到 v2.1.2 行为；ini 中 `enabled=` 键全程保留，
  回滚后的旧 exe 能读到同样的开关状态；KDS1 文件本轮零改动。

## 自动化测试（命令与结果）

```powershell
powershell -ExecutionPolicy Bypass -File tools\test.ps1 -Suite All
# 结果：suite=All passed=24 failed=0（退出码 0）
```

| 套件 | 测试 | 覆盖 |
|---|---|---|
| Core | LEG_A~H（8 条黄金轨迹） | 普通点击/双击拦截/长按自动重复/up 窗口内重按/无配对 Up/注入旁路（含状态未污染对照）/阈值边界 `<=`/被拦 Down 不刷新 |
| Core | KSM001 / KSM004 | 过滤关全放行且状态零污染（重开首按必放行）；双键交错与单键回放一致 |
| Core | UpWindowFloor / ThresholdRuntimeChange | 40ms 下限；运行中改阈值立即生效 |
| Core | LEG001_DifferentialRandomTraces | 8 种子 × 3 阈值 × 300 事件，与独立移植的 v2.1.x 参考实现逐事件全等 |
| Core | Config_*（4 个） | 旧键 `enabled=` 读取、新键读取、legacy ini 模式、Save 往返且双键落盘 |
| Storage | KDS_GoldenLoad / RoundTrip / Malformed / ReadHeader | 黄金 KDS1 解析（含 65549 小键盘 Enter 保留、M 位图分钟）、Save→Load 全聚合相等、坏行忽略语义冻结、临时文件清理 |
| Storage | CsvSnapshot_Frozen | 四份 CSV 导出逐字节冻结（含自进程 `keyinsight` 排除、跨月排序） |
| Storage | Snapshot_FieldsFrozen | `TotalAll/SwallowedAll/Range/KeyHealth` 旧健康分公式输出冻结（vk65=20/状态3、vk65549=64/状态2、vk69=86/状态2） |
| Storage | Stats_SelfAppExcluded | 自进程大小写不敏感排除 |

重新基线化方式：设 `KI_UPDATE_GOLDEN=1` 运行 Storage 套件，打印当前输出回填常量（黄金轨迹为手工推导值，不适用该机制）。

## Windows 真机测试（本环境已执行 / 尚未执行）

本机为 Windows 11 x64 真机（非 CI 模拟），已执行：
- `tools/build.ps1 -Version 2.2.0` → 690,176 字节 exe，编译无警告错误；
- `KeyInsight.exe --selftest` → `SELFTEST OK`（存储往返/导出/删除回归）；
- `KeyInsight.exe --screenshot` → exit 0，8 页 × 深/浅主题全部渲染（UI 文案改动未破坏绘制）。

**尚未执行、需后续人工验证**（不得以本报告替代）：
1. 真实键盘长按 / 连击 / 输入法（IME）下 `FilterEnabled=true` 的手感与拦截率对比 v2.1.2；
2. 托盘关闭过滤后真实打字，确认「今日 N 键」继续增长且无按键丢失；
3. 托盘「重新安装输入钩子」与睡眠唤醒后的钩子状态；
4. 游戏等受保护进程下的钩子兼容性。

## 性能指标

本轮未改变回调内算法复杂度（字典查找 + 双精度比较，与旧实现同级）；钩子回调耗时直方图（P50/P95/P99）
按任务书属 P0-05 交付，本轮未实现也未声明。初步说明：新路径仅在原 `Enabled` 检查处增加一次
`bool` 分支与一次 `double` 相等比较，理论开销可忽略；无真机量化数据，不作承诺。

## 旧 KDS1 / 旧配置兼容验证

- `KDS_GoldenLoad/RoundTrip`：KDS1 读取与落盘字节级兼容，`65549`(0x1000D) 等 VK 历史显示不变；
- `CsvSnapshot_Frozen`：CSV 四表格式冻结；
- `Config_OldEnabledKeyRead/SaveRoundTrip`：旧 ini 键双向兼容（升级读旧键，回滚读新写的 `Enabled=`）；
- 本轮**零**数据文件格式变更、零字段语义变更。

## 隐私影响

无新增采集字段；不记录窗口标题、不记录按键内容序列（`debounce.log` 为既有机制，其异步化属 P0-05）。

## 风险与尚未解决问题

1. **浮点时间路径**：旧实现间隔为整数 tick 换算，新实现以毫秒 double 注入；全毫秒整点在两条
   路径上都精确表示，黄金轨迹与随机差分均逐事件一致，但理论上存在 <1e-9 ms 的边界表示差异。
2. **关闭过滤期间不更新过滤状态**：是有意选择（重开过滤从安全态起步），与旧代码关闭期间行为一致；
   若用户在关闭期间高速连按再立即开启，首按不会误拦（已测试覆盖）。
3. `StatsEngine` 无界队列、`DebounceHook.LogLine` 同步写盘、`MouseHook` 首事件同步写盘——
   均为既有风险，**按任务书属 P0-05，本轮未动**。
4. `Snapshot_FieldsFrozen` 中 `KeyHealth` 的 sw7/swPrev 依赖「今天」，黄金数据日期固定在
   2026-01/02，任何晚于 2026-02-18 的运行时间下结果稳定；如需彻底与时钟解耦需注入时钟抽象（P0-03+）。
5. Windows 真机输入体验验证项见上节，需项目维护者在常用环境人工确认后再考虑发布。
