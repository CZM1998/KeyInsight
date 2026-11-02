# KeyInsight 性能基线报告

- 生成时间：2026-11-02 23:43:35
- 环境：Microsoft Windows NT 10.0.26200.0 · .NET 4.0.30319.42000 · 32 核
- 方法：合成 1/12/36 个月 KDS 数据；GetSnapshot 各范围连续 5 次取 P50；每页离屏 DrawToBitmap 3 次取 P50。

## Snapshot 构建（GetSnapshot，含锁）

| 数据量 | 范围 | P50 ms | P95 ms | 备注 |
|---|---|---|---|---|
| 1 个月 | Today | 0.1 | 15.5 | |
| 1 个月 | Week | 0.1 | 0.3 | |
| 1 个月 | Month | 0.1 | 0.1 | |
| 1 个月 | All | 0.1 | 0.7 | |
| 12 个月 | Today | 0.1 | 1.2 | |
| 12 个月 | Week | 0.2 | 0.7 | |
| 12 个月 | Month | 0.4 | 0.4 | |
| 12 个月 | All | 3.7 | 3.9 | |
| 36 个月 | Today | 0.1 | 2.3 | |
| 36 个月 | Week | 0.2 | 0.2 | |
| 36 个月 | Month | 0.4 | 0.5 | |
| 36 个月 | All | 12.5 | 12.8 | |

## 页面绘制（离屏渲染 P50，36 个月数据，1200×780）

| 页面 | P50 ms | GDI 对象 |
|---|---|---|
| overview | 9.8 | 33 |
| keyboard | 14.9 | 33 |
| mouse | 9.5 | 33 |
| apps | 11.4 | 33 |
| time | 9.3 | 33 |
| health | 10.3 | 33 |
| debounce | 9.5 | 33 |
| settings | 9.6 | 33 |
| advanced | 7.4 | 33 |

## 运行期 UI 指标（埋点）

| 指标 | P50 | P95 | P99 | Max | 样本 |
|---|---|---|---|---|---|
| UI tick (ms) | 0.25 | 0.25 | 0.25 | 0.25 | 2 |
| 异步快照构建 (ms) | 0.26 | 0.26 | 0.26 | 0.26 | 1 |
| 统计锁等待 (ms) | 0.00 | 0.00 | 0.00 | 0.00 | 73 |

绘制埋点（按页面 P95 ms）：

| 页面 | P50 | P95 | 样本 |
|---|---|---|---|
| Advanced | 1.3 | 4.5 | 3 |
| Apps | 4.9 | 9.4 | 3 |
| Debounce | 3.5 | 7.0 | 3 |
| Health | 4.1 | 14.9 | 3 |
| Keyboard | 6.1 | 16.6 | 3 |
| Mouse | 3.7 | 17.3 | 3 |
| Overview | 3.3 | 12.1 | 3 |
| Settings | 3.4 | 9.5 | 3 |
| Time | 3.1 | 8.9 | 3 |

快照请求：1 · 合并：0 · 过期丢弃：0


## 资源

- WorkingSet：112 MB
- PrivateBytes：96 MB
- GC Heap：67 MB
- 数据目录：C:\Users\admin\AppData\Local\Temp\KeyInsightPerf_2341a36456a748c8b96a87050319e9f4\36mo（36 个月）

## 埋点口径

- UI tick：DashboardForm Timer handler 耗时（P95 目标 < 5ms）。
- 快照构建：GetSnapshot 整体耗时（含锁）。
- 锁等待/持有：StatsEngine._gate（P95 记录）。
- 绘制：PageBase.OnPaint → Draw（P95 目标 < 16.7ms）。

> 数值为工程目标对照，非 Windows 保证指标；改造前后须在同一机器、同一数据集下对比。
