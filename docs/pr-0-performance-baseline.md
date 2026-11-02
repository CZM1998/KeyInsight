# KeyInsight 性能基线报告

- 生成时间：2026-11-02 23:19:39
- 环境：Microsoft Windows NT 10.0.26200.0 · .NET 4.0.30319.42000 · 32 核
- 方法：合成 1/12/36 个月 KDS 数据；GetSnapshot 各范围连续 5 次取 P50；每页离屏 DrawToBitmap 3 次取 P50。

## Snapshot 构建（GetSnapshot，含锁）

| 数据量 | 范围 | P50 ms | P95 ms | 备注 |
|---|---|---|---|---|
| 1 个月 | Today | 0.1 | 17.7 | |
| 1 个月 | Week | 0.1 | 0.3 | |
| 1 个月 | Month | 0.1 | 0.1 | |
| 1 个月 | All | 0.1 | 0.7 | |
| 12 个月 | Today | 0.1 | 1.3 | |
| 12 个月 | Week | 0.2 | 0.7 | |
| 12 个月 | Month | 0.4 | 0.4 | |
| 12 个月 | All | 4.1 | 4.3 | |
| 36 个月 | Today | 0.1 | 2.7 | |
| 36 个月 | Week | 0.2 | 0.2 | |
| 36 个月 | Month | 0.4 | 0.5 | |
| 36 个月 | All | 12.5 | 12.6 | |

## 页面绘制（离屏渲染 P50，36 个月数据，1200×780）

| 页面 | P50 ms | GDI 对象 |
|---|---|---|
| overview | 8.7 | 33 |
| keyboard | 12.1 | 33 |
| mouse | 9.6 | 33 |
| apps | 9.3 | 33 |
| time | 9.6 | 33 |
| health | 11.5 | 33 |
| debounce | 10.6 | 33 |
| settings | 9.5 | 33 |
| advanced | 7.5 | 33 |

## 资源

- WorkingSet：112 MB
- PrivateBytes：95 MB
- GC Heap：67 MB
- 数据目录：C:\Users\admin\AppData\Local\Temp\KeyInsightPerf_4d56c481b9034cb9a915852d26564576\36mo（36 个月）

## 埋点口径

- UI tick：DashboardForm Timer handler 耗时（P95 目标 < 5ms）。
- 快照构建：GetSnapshot 整体耗时（含锁）。
- 锁等待/持有：StatsEngine._gate（P95 记录）。
- 绘制：PageBase.OnPaint → Draw（P95 目标 < 16.7ms）。

> 数值为工程目标对照，非 Windows 保证指标；改造前后须在同一机器、同一数据集下对比。
