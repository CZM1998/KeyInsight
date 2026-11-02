# UI 与性能专项改造报告（PR-0 ~ PR-5）

> 实施依据：`KeyInsight_UI_Performance_Refactor_Guide.docx`（2026-11-02）。
> 基线差异：文档基于公开 master（v2.1.2）；实际实施仓库已含 v2.2–v3.0 的可靠性/策略/导航改动，
> 因此部分页面结构（滚动体系、5 组导航、标签页）与文档插图不同，布局类要求以当前代码为准。
> 本轮未改动 Hook 语义、防抖算法语义、KDS1 格式与 CSV 导出。

## 变更总览

| PR | 内容 | 关键落点 |
|---|---|---|
| PR-0 | 基线 + 埋点 | `Stats/UiMetrics`（tick/快照/锁等待/锁持有/绘制分位）、`Stats/PerfDataGenerator`（1/12/36 月合成数据）、`Perf/PerfRunner`（`--perf-report`）、`--gen-data` CLI；基线存 `docs/pr-0-performance-baseline.md` |
| PR-1 | 快照异步化 | `StatsEngine.RequestSnapshot`：worker 线程构建、请求合并（只保留最新）、generation 过期丢弃、同 key 缓存；`DashboardForm` 改用异步请求 + 1s 节流 + DataVersion 判断；截图/测试保留同步路径 |
| PR-2 | 绘制缓存 | `UI/Rendering/RenderResources`（Brush/Pen 复用，主题切换 DisposeAll）；`Charts` 键盘几何 `LayoutCache`（绘制与命中同源）；TopN/排序/汇总移到 `SetSnapshot`（Overview/Debounce/Health/Mouse/Apps/Keyboard）；`Ui.Prep` 去掉全局 HighQualityBicubic；柱状/迷你柱/趋势区渐变改为平涂 |
| PR-3 | 布局修复 | TimePage 宽度 ≥860 时双列图表 + 全宽趋势，Compact 堆叠；MousePage 趣味换算按可用高度收敛行数；DebouncePage 最近拦截行数按高度收敛；KeyboardPage 应用筛选 Chips 按可用宽度截断 |
| PR-4 | 视觉系统 | 石墨中性色（#17191B/#25292D/#353A3F 等）+ 低饱和**蓝色**主色（按用户偏好保留蓝基调：暗 #7FA3C8、亮 #4A6F94）；热力图改蓝色单色阶去品红；卡片圆角 14→10；字号：正文 9.75、区块 11.5、页面标题 21、KPI 24；状态色仅保留语义（成功绿/警告琥珀/异常砖红）；修复导航高亮误用页索引的缺陷；修复 24pt KPI 千分位被行高裁切的缺陷 |
| PR-5 | 回归验收 | `Perf/ScreenshotMatrixRunner`：尺寸 1000×680/1200×780/1440×900/1600×1000 × 深浅 × 9 页 + 1000×680 滚动状态 + 键盘悬停/87 键特写（共 102 张）；性能复测 `docs/pr-5-performance-final.md`；测试增至 **89 项** |

## 性能对比（同机同数据集，`--perf-report`）

| 指标 | 改造前 | 改造后 | 说明 |
|---|---|---|---|
| UI Tick P95 | 1000ms 定时器**同步**执行完整 GetSnapshot（36 月 All≈12.5ms/次，期间 UI 阻塞） | **0.25ms**（只发异步请求） | 达标 <5ms |
| 异步快照构建 | — | 0.26ms（小范围/缓存命中） | 36 月 All 构建仍在 12–13ms 量级，但发生在 worker 线程 |
| 统计锁等待 P95 | 未分离（GetSnapshot 长锁内） | **0.00ms**（采样 73 次） | 事件线程不再与 UI 抢锁 |
| 页面绘制 P95（埋点） | 未埋点（DrawToBitmap P50 7.5–12.1ms） | **4.5–9.4ms** | 均低于 16.7ms 目标 |
| GDI 对象 | 33（DrawToBitmap 后） | 33（复用画笔后稳定） | 无增长 |
| 测试 | 87 | **89**（+异步快照等价、分位埋点） | 全过 |

> DrawToBitmap 整体渲染 P50 在改造后个别页面略高（如 keyboard 12.1→14.9ms），原因是字号增大与
> 截图包含窗口合成开销；实际页面绘制埋点（不含窗口合成）P95 均 <10ms，属更可信指标。
> 文档要求的目标值为工程对照，非 Windows 保证。

## 兼容与回滚

- Hook / 防抖语义 / KDS1 / CSV 未变：78 项存量测试与 selftest 全部保持通过。
- 回滚：`git revert` 本轮提交即可；无配置格式破坏（v2 JSON 与 ini 并存，旧 exe 可读 ini）。
- 视觉回滚点：`UI/Theme.cs` 调色板集中管理；如需恢复旧蓝紫渐变，可回退该文件。

## 未完成 / 限制（如实声明）

- **DPI 多档真机验证**：1200×780 等尺寸矩阵已覆盖；125/150/200% DPI 与多显示器切换需真机人工确认
  （进程内无法切换 DeviceDpi，截图矩阵不模拟 DPI）。
- **UI 线程阻塞 >50ms 诊断日志**：埋点已记录 tick/绘制分位，阈值告警日志未实现。
- 快照缓存仅保留最近 8 个 (range, dataVersion) 键；高频数据变化下命中率有限（设计如此，节流优先）。
- `LayoutCache` 仅覆盖键盘几何；图表坐标轴与鼠标示意图未缓存（当前绘制耗时已达标）。
