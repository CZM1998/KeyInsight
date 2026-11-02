# 实施报告 — v2.2 P0-03/04/05/06 · v2.3 P1 · v2.4–2.5 P2 · v3.0 P3

> 承接 `docs/implementation-report-P0-01-P0-02.md`。按任务书顺序推进至路线图全部代码项；
> 尚未执行的真机验证在文末单独列出（不以自动化测试替代）。
> 最终验证：`tools/build.ps1` 通过；`tools/test.ps1 -Suite All` → **78/78 通过**；
> `--selftest` → SELFTEST OK；`--screenshot` 19 张全部渲染。

## P0-03 键身份与不可变策略快照

- 新增 `Core/Input/PhysicalKeyId.cs`：`(VK, ScanCode, Extended)` 值类型，稳定哈希与
  `vk=45;scan=12;ext=0` 文本往返。
- 新增 `Core/Input/InputIdentityResolver.cs`：主/小键盘 Enter、左右 Ctrl/Alt/Shift、方向键簇、
  AltGr 身份规则；`scanCode=0` 降级为独立身份槽（不与物理键混同）；VK→常见扫描码表（配置离线构造用）。
- 新增 `Core/Policy/PolicySnapshot.cs`（不可变）+ `PolicyController`（版本号单调递增、整体引用替换；
  启动时 `AttachStore` 挂接单键策略存储）。配置线程构建、钩子线程只读。
- 新增 `Core/Input/NormalizedKeyEvent.cs` + `InputDecision`（含 DecisionReason/版本/间隔诊断字段）。
- **旧口径零变化**：KDS1 统计仍用 `StatsVk`（小键盘 Enter=0x1000D）；CSS/CSV 快照测试全部保持。

## P0-04 状态机与三模式

- 新增 `Core/Policy/KeyStateMachine.cs`：`Idle/DeliveredDown/SuppressedPress/Unknown`；
  `DeliveredDown+Down` 默认放行（长按安全）；SuppressedPress 配对拦截；
  **过期故障边界**（`SuppressedPressStartedMs`，不随重复 Down 刷新 —— 修复了开发中发现的
  “重复 Down 续期导致按键永久锁死”缺陷，KSM-018）；时间倒退保守放行。
- 新增 `Core/Policy/KeyInsightPolicyEngine.cs`：
  - `ObserveOnly`：状态机只记录候选，绝不拦截；
  - `LegacyCompatible`（默认）：`LegacyPolicyAdapter` 决策（与 v2.1.x 逐事件一致），
    已配置单键规则并行跑影子对比（`ShadowLegacyPassConservativeSuppress` 等计数器）；
  - `PerKeyConservative`：只有显式启用规则的键被拦截，其余一律放行；
  - 应用白名单（命中永不拦截）、注入旁路、非法 VK、引擎异常 Fail Open（KSM-019，含测试注入点）。
- 测试：KSM-001~020 全部映射为确定性回放 + 白名单/legacy-parity 附加用例。

## P0-05 回调去 I/O、有界遥测、延迟采样

- 新增 `Core/Telemetry/AsyncFileLog.cs`：回调只入队（容量有界、丢弃计数），专职写线程批量落盘，
  Dispose 限时冲刷。`DebounceHook`/`MouseHook` 的同步 `StreamWriter`/`File.AppendAllText` 全部移除。
- 新增 `Core/Telemetry/BoundedEventQueue.cs`：CAS 预占容量再入队（两生产者一致性测试）。
- `StatsEngineSink` 改为“有界队列 + 泵线程”：钩子入队即返回；满载丢弃计入 `DroppedCount`
  （语义上标记“统计可能不完整”）；`Flush` 供退出/测试排空。
- `DebounceHook` 增加回调耗时直方图（2 的幂 µs 桶，每 256 事件采样一次），只读快照供 UI。
- 测试：TEL-001（过载丢事件不阻塞）、TEL-002（双生产者计数一致）、TEL-006/007（只读磁盘降级）、
  异步日志回序完整。

## P0-06 数据路径、迁移、活跃分钟、侧车

- 新增 `Stats/DataPathResolver.cs`：标记文件判定便携/安装模式；无标记 = 现行行为（exe 旁 data/）。
- 新增 `Stats/DataMigrator.cs`：`--migrate-data` 显式迁移；SHA-256 备份清单 + 逐文件校验 +
  `.partial` 原子落位 + 幂等（installed.marker 提交点）+ 失败不写标记；测试覆盖损坏/锁定/回滚路径。
- `MonthFile.Load` 增加**文件级完整性门**（非空文件首行必须是 `#` 头）：半截/二进制垃圾不再被
  静默误收，引擎隔离为 `.corrupt` 后空月继续（STO-004）。行级容错（未知记录忽略）保持（STO-003）。
- V2 口径：`DayData.MouseMinutes` 位图（不入 KDS1）+ `data/v2/usage-YYYY-MM.jsonl` 日摘要
  （键盘/鼠标/综合分钟、`mouseMinutesHex` 位图，加载时回填）；`ActiveMinutesCalculator` 合并口径。
- KDS1 格式与 CSV 四表字节级冻结（快照测试）。

## P1-01 单键策略持久化与配置 UI

- 新增 `Core/Policy/KeyPolicyStore.cs`：`profiles/key-policies.v1.json`，原子写、schema 校验、
  损坏留 `.corrupt` 回退空表。**注意**：`JavaScriptSerializer` 嵌套数组为 `ArrayList`（非泛型），
  Load 使用 `IDictionary/IEnumerable` 访问（已修复的死角）。
- UI：设置页新增「策略模式」三 chips 与「当前应用永不拦截」白名单开关；
  按键健康页详情新增单键开关、阈值 chips（10/15/20/25/30ms）、诊断/模拟/维护三个入口。
- 模式与白名单持久化到 `KeyInsight.ini`（`PolicyMode=`、`FilterAppWhitelist=`）。

## P1-02 按键画像投影与诊断侧车

- 新增 `Stats/KeyProfileProjector.cs`：使用/诊断（旧健康分**显式标注“基于拦截”**）/策略/完整性投影；
  样本 <50 标注 `insufficient-sample`。
- `StatsEngine` 新增按日按 VK 的拦截间隔直方图（11 桶），落盘 `data/diagnostics/kdiag-YYYY-MM.jsonl`
  （含 `algorithmVersion`）；启动加载全部月份（跨重启模拟可用）；快照暴露 30 天聚合 `GapHistograms`。

## P1-03 热力图三图层与按键详情

- `Charts.DrawKeyboardValues`：任意指标字典着色变体。
- 键盘页：`使用频率 / 疑似异常候选 / 实际拦截` 三图层 chips；点击任意键打开 `KeyDetailForm`
  （概览/健康/策略/维护 + 诊断入口）。

## P1-04 诊断测试向导

- 新增 `UI/Pages/DiagnosisWizardForm.cs`：单击/双击/长按采集（仅目标 VK 计数）；
  **采集期间全局暂停过滤**（临时快照），失焦停止采集，FormClosed 无条件恢复原策略；
  ≥8 个按压周期才分析；输出样本量/候选数/建议阈值；「应用建议」才写入单键策略并切到单键保守模式。

## P1-05 ISO 布局

- `KeyLayout` 新增 87/108 ISO 变体（左 Shift 1.25u + `<>` 键 0xE2 + 右 Shift 1.75u），
  布局 chips 扩为四项；`KeyboardLayout` 取值域 0..3。物理键历史 ID 不受布局影响。

## P2 系列

- **P2-01 离线策略模拟**：`Stats/PolicySimulator.cs` 用 30 天直方图估算 10/15/20/25/30/40ms 过滤量、
  推荐阈值与风险提示；明确“预测≠真实误拦率”；`SimulationForm` 仅“应用推荐”才写配置（ADR-008）。
- **P2-02 维护事件**：`Stats/MaintenanceStore.cs` + `maintenance.v1.jsonl`；健康页「记录维护」
  （清洁/换轴/换键盘/复测/备注，不含任何键序列）；前后 7 天拦截对比 + 样本不足显式提示。
- **P2-03 鼠标 Up / 微动检测（仅检测）**：`MouseHook` 捕获 `WM_*BUTTONUP`、配对按压周期，
  `<40ms` 记为微动候选（500ms 去抖窗口），累计 `UpCount/CycleCandidates/RawWheelDelta`；
  旧 KDS1 口径不变；鼠标页展示 V2 观察值。
- **P2-04 综合活跃分钟 + 快捷键聚合**：时间页「综合活跃分钟」卡片与「常用快捷键」Top 卡；
  `ShortcutObserver` 白名单组合（Ctrl/Alt/Win + 40 键）聚合计数，`shortcuts.v1.json` 持久化，
  仅组合名与次数。
- **P2-05 导出标注 + 周报**：导出 CSV 同目录写 `export-manifest.json`（区间/精度/文件清单）；
  设置页「生成本周报告」→ `data/exports/weekly-YYYY-MM-DD.md`（含精度声明与旧口径标注）。
  设计中的“可选悬浮摘要”未实现（可选项，留待后续；不影响门禁项）。

## P3 Raw Input 设备统计 + 应用白名单

- 新增 `Hook/RawInputTracker.cs`：message-only 窗口 + RIDEV_INPUTSINK 注册键盘/鼠标；
  按设备接口短名累计事件数（仅统计），`devices.v1.json` 持久化；注册失败自动降级不影响键盘钩子。
- 应用白名单已在 P1-01 落地（策略快照内匹配前台进程名，命中 Pass + 计数）。
- **明确未做**：设备级过滤（设计 ADR-005：`WH_KEYBOARD_LL` 无法可靠按设备拦截，独立研究项）。

## 自动化测试清单（78 项）

| 套件 | 覆盖 |
|---|---|
| Core（新） | 键身份 9 项、KSM-001~020 映射 22 项、配置兼容 4 项、策略模拟 2、维护 1、快捷键 1、画像 1、ISO 1、策略存储 1 |
| Core（原） | LEG 黄金轨迹 8、LEG-001 随机差分 1、过滤关语义 1、键隔离 1、40ms 下限 1、运行时改阈值 1 |
| Core/Telemetry | 有界队列过载/双生产者、sink 排空、只读磁盘日志降级、日志有界丢弃 |
| Storage | KDS1 黄金加载/往返/坏行/文件头、CSV 四表冻结、快照字段冻结、自进程排除、侧车往返、综合分钟、迁移、路径模式 |

## 尚未执行的真机验证（必须人工确认后才能灰度）

1. 三模式手感：`LegacyCompatible` 与 v2.1.2 打字/连击/长按手感对比（本机安装版已将默认模式保持为兼容）；
2. 单键保守模式实机：健康页启用 E 键后，快速双击与长按行为、误拦主观感受；
3. 托盘关过滤后统计继续增长（P0-02 已验证逻辑，真机复核一次）；
4. 诊断向导：采集期间过滤暂停、关闭后恢复（含 Alt+F4/任务管理器强杀后重启无残留临时策略）；
5. 睡眠唤醒 / RDP / 游戏（受保护进程）下钩子与 Raw Input 行为；
6. 安装版升级数据目录（本机已保持便携布局，`--migrate-data` 未自动触发）。

## 已知风险

- 钩子回调延迟直方图为内置采样，尚无真机 P50/P95/P99 报告（属 P0-05 验收项，需真机跑分）。
- `KeyHealth` 旧健康分依赖“今天”，黄金夹具固定日期保证稳定性（同 P0-01 报告）。
- 快捷键聚合依赖修饰键状态经 LL 钩子完整可见；RDP/特殊注入场景可能丢修饰键状态（只影响计数完整性）。
- Raw Input 设备名为接口路径短名（非 VID/PID 解析），同型号多设备可能同名合并。

## 补充修复（3.0.1）：页面滚动与窄窗口布局

用户实测反馈：窗口小于设计尺寸（1200×780）时，各页硬编码像素布局导致分区重叠、且无法滚动。

- `PageBase` 引入垂直滚动：`AutoScroll` + `AutoScrollMinSize = ContentH`（最小内容高度 720），
  绘制时按 `AutoScrollPosition` 平移；`ContentH` 取代页面内所有 `Height` 布局计算；
  鼠标命中/悬停统一经 `ToContent()/CursorToContent()` 换算为内容坐标；
  `DashboardForm.OnMouseWheel` 转发滚轮到当前页（页面不自绘滚动条、不获取焦点）。
- 滚动条占据非客户区导致右对齐元素被裁 9px：页面布局宽度改用 `ClientSize.Width`（`ContentW`）。
- 键盘页：布局 chips（87/108/ISO）移入「按键分布」卡片头部，修复与三图层 chips 同排重叠；
  图例按图层切换文案（图层 1/2 不再显示红点说明）；悬浮提示按可视区域钳制。
- 设置页底部三个操作按钮改为等分宽度（窄窗口不再互压）。
- `--screenshot` 增加 1000×680 最小窗口回归图（`small-*.png`），全部 8 页验证：无重叠、可滚动。
- **滚动条风格（3.0.2）**：弃用原生 `AutoScroll`（OS 灰条与自绘 UI 不匹配），改为
  `PageBase` 内部滚动偏移 + 壳层在页面右侧条带自绘滚动条（6px 圆角，跟随主题色，
  悬停/拖动变主题蓝，支持轨道点击跳转与拖拽）；`ContentW` 改用 `ClientSize.Width`。
  `MinContentHeight` 校准为默认窗口内容区高度 686：**默认 1200×780 无滚动条**，
  仅窗口更小时出现，且不再有宽度裁切。
- **滚动实现修正（3.0.3）**：3.0.2 用 `Graphics.TranslateTransform` 平移内容导致滚动后 UI 错乱——
  根因是 `TextRenderer`（GDI 文本）不跟随 GDI+ 世界变换，文字留在原位、与图形脱节。
  改为「视口 + 子画布」：`PageBase` 为视口，内部 `PageCanvas` 控件（高度=ContentH）承载全部绘制，
  滚动即移动子控件（`_canvas.Top = -scrollY`），无任何绘制变换；鼠标事件由画布转发回页面
  （画布坐标天然等于内容坐标）。`--screenshot` 新增 `small-scroll-*.png`
  （小窗口 + 滚动 180px）作为该缺陷的固定回归。
- 验证：78/78 测试通过、selftest OK、小窗口/滚动状态 8 页截图逐一目检；
  部署 3.0.3 至本机（ini/数据零影响）。

## 建议模块补齐（3.1.0）

按设计文档 §2.2/§5.1/§6/§8/§9 补齐全部"建议模块"与遗留门禁缺口：

| 模块 | 实现 |
|---|---|
| HookSupervisor（§5.1） | 键盘/鼠标钩子显式状态机（Stopped/Starting/Installed/Stopping/Failed）、`GetLastWin32Error` 错误码、`GetMessage` 返回 -1 记录、每钩子"最近事件时间"；托盘/高级页展示，手动重装入口 |
| 采集/诊断独立开关（§4.5） | `CaptureEnabled`/`DiagnosticsEnabled` 各自持久化（ini + v2）；诊断关闭时停止直方图采集；托盘状态行标注钩子状态与"统计不完整" |
| TelemetryEvent（§2.2） | 新传输模型入 `ITelemetrySink`（Down/Swallow/MouseDown/MouseWheel/Shortcut/WheelRaw）；`StatsEngineSink` 适配到旧 KeyEvent，KDS1 双表冻结不变；MouseHook 改走同一 sink |
| ClockAbstractions（§2.3） | `IMonotonicClock`/`IWallClock` + 系统实现；两钩子默认注入系统时钟，测试可替换 |
| 设备画像（§6.1） | `DeviceProfile`（VID/PID/类型/次数/首末时间）；Raw Input 接口路径解析 VID_/PID_；`devices.v1.json` schema=2（兼容旧计数格式）；「高级与设备」页列表 |
| raw 滚轮 delta（§8） | `WheelRaw` 事件 → 按日累计（delta/事件数）→ `usage-YYYY-MM.jsonl` 持久化 → 快照范围汇总，鼠标页/高级页展示；旧「滚轮格数」口径不变 |
| 版本化配置（§6.3） | `data/config/KeyInsight.v2.json`（schema=1）：v2 存在时优先读取，缺失自动从 ini 迁移；双写保留旧键回滚兼容；未来 schema 保守拒绝 |
| 悬浮摘要（P2-05 可选） | 置顶小窗（今日键/鼠标/拦截），位置持久化，开关在高级页；由托盘定时器随配置显隐 |
| 导航整合（§9） | 8 页整合为 5 个一级入口：总览 / 键盘 / 鼠标 / 分析与诊断 / 设置与数据；多页分组用顶部标签切换（键盘组：热力图/防抖；分析组：健康/时间/应用；设置组：设置/高级与设备） |
| 侧车一致性（STO-015） | 删除月份/清空全部数据时同步删除 usage/kdiag 侧车与内存直方图；无数据时写侧车改为删文件，避免空壳残留 |
| 策略回退（DIA-008） | `KeyPolicyStore.BeginEdit/Revert`（.undo 文件快照）；健康页新增「回退上次」按钮；向导/模拟/开关编辑前自动拍快照 |
| 崩溃恢复（STO-007） | `MonthFile.Load` 主文件缺失时按 `.bak` → `.tmp` 恢复并物化；残留 `.tmp` 自动清理 |
| 数据完整性提示（P0-05 验收） | 遥测丢弃数 > 0 时标题状态栏与托盘显示「统计不完整」，高级页显示丢弃明细（输入不受影响） |

测试增至 **87 项**（新增：STO-015 侧车清理、STO-007 .bak 恢复、DIA-008 回退、ConfigV2 往返/未来 schema 拒绝、
设备 VID/PID 解析、策略版本切换状态字典有界、TEL-005 Dispose 并发、STO-013 跨年排序、TEL-004 乱序跨月）。
`--screenshot` 覆盖 9 页 × 深浅色 × 小窗口 × 滚动状态。

**仍开放（需真机/后续）**：STO-006（`File.Replace`+`File.Copy` 双失败故障注入）、DST/时区注入、
150% DPI 截图、钩子回调 P99 实测与 1 小时压测、Win10/游戏/RDP/睡眠唤醒真机矩阵、设备级过滤（ADR-005 研究项）。
