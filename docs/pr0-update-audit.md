# PR-0 审计报告 — 更新模块与数据路径（综合路线图）

> 基线冻结：commit `22911220a8c272cc99e6844b69f32ed4750bd53d`（master），构建版本 3.2.3.0。
> 依据：《KeyInsight 综合发展与工程实施计划》(2026-11-03)。

## 1. Update/ 目录现状

| 文件 | 行数 | 能力 |
|---|---|---|
| UpdateService.cs | 187 | 单一来源：GitHub Releases API latest；解析 tag_name；比较本地 Assembly 版本；从 assets 中选 `KeyInsight-Setup-*.exe` 与 `sha256.txt`；无签名、无多线路、无续传、无独立更新器、无回滚 |

- 版本比较：`Version.TryParse` 数值比较（符合 SemVer 要求，OK）
- 下载：`UpdateService.Download`（下载 Setup 到临时目录；**未实现**：Range 续传、大小上限、总超时、暂存目录规范）
- 校验：`VerifyChecksum` 读取 sha256.txt 比对——**与安装包同源，只能证明一致性，不能证明发布者身份**
- 安装：`RunInstaller` 直接启动 Inno Setup（`/SILENT`）——主程序自身进程内触发，**无独立 Updater、无备份、无健康检查、无回滚**
- Portable：走同一 Setup 路径，**会转安装版**（路线图明确要求保持 portable 形态）
- 渠道/rollout：不存在
- 状态反馈：仅有托盘气泡；无"检查中"面板状态（3.2.3 已加按钮动画+结果行，但无线路/下载进度）

## 2. installer/ 与发布流水线现状

- `installer/KeyInsight.iss`：47 行；`{localappdata}\Programs\KeyInsight`；`CloseApplications=yes`；数据目录 `data\` 位于安装目录内（安装版数据与程序同目录）；**卸载默认保留数据**（无 UninstallDelete）——符合"卸载不删数据"，但程序/数据目录未分离，与路线图"DataPathProvider 区分程序目录与数据目录"有差距。注意：便携模式数据在 exe 旁 `data/`；安装版如果装到同一结构，两形态数据路径实际一致（都为 exe 旁 data/），`DataPathResolver` 目前按标记文件区分，无标记默认便携。
- `.github/workflows/release.yml`：tag 触发 → build.ps1 → selftest → Inno Setup → portable zip → sha256.txt → GitHub Release。**无 manifest 生成、无签名、无镜像同步、无镜像回验**。
- `.github/workflows/build.yml`：build + test.ps1 + selftest + artifact。

## 3. 数据路径现状

- 便携（默认无标记）：数据 = exe 旁 `data/`；安装标记文件 `installed.marker` 存在时 → `%LOCALAPPDATA%\KeyInsight\data`；v2 配置 `data/config/KeyInsight.v2.json`。
- 缺口：安装版安装器并未创建 `installed.marker`（installer/.iss 无此步骤）→ 安装版用户实际以便携模式运行。**需要修复：iss 添加 installed.marker，或 DataPathResolver 以"Program Files/Programs 目录特征"判断**。最稳妥：iss `[Files]` 增加 marker 文件。
- KDS/侧车/配置路径已集中（DataPathResolver）；升级/回滚不触碰数据 —— 现有 MonthFile/侧车均程序外独立文件。

## 4. 与目标方案的差异清单（PR 映射）

| 差异 | 目标 PR |
|---|---|
| DataVersion 单一计数器 → 需拆分 Event/History/Diagnostic/Device/Epoch 五维 | PR-1 |
| GetSnapshot 在 worker 全程持 _gate → 需 RawSnapshot 短锁复制 + 锁外聚合 | PR-2 |
| 主题为石墨灰+钢蓝（3.2.x）→ 需按新令牌改为蓝灰基底+明亮蓝 | PR-3 |
| 无 manifest、无签名、无 Host allowlist、无渠道/rollout | PR-4 |
| 单线路（GitHub API）→ 需 RouteCatalog/Probe/Selector/熔断/诊断页 | PR-5 |
| 无续传/staging/独立 Updater/健康检查/回滚；portable 转安装版 | PR-6 |
| release.yml 无 manifest 发布/镜像同步/回验 | PR-7 |
| 防抖实验室已有（模拟/推荐/回退）→ 缺置信度分档显示与阈值曲线图 | PR-8 |
| 诊断向导已有 → 缺 NKRO/卡键/漏松开专项与逐键报告导出 | PR-9 |
| 周报已有（Markdown）→ 缺 HTML/CSV 月报与自定义配列 | PR-10 |

## 5. 实施顺序（本计划）

按文档门禁逐 PR 推进；第 1 阶段（PR-0~2）与第 2 阶段（PR-3）先落地，随后第 3 阶段（PR-4~6 更新安全/多线路/事务）为重点，第 4 阶段（PR-7）提供发布脚本模板与工作流骨架（实际 OSS 域名与签名私钥需项目方在云端配置）。PR-8~10 在更新底座完成后推进。

## 6. 风险

- 签名私钥管理：开发期生成密钥对，公钥内置于客户端；生产私钥须放入 GitHub Secrets，**不得入库**。
- 无真实 OSS 域名前，多线路以 GitHub 官方 + raw.githubusercontent 两条可信线路先行，RouteCatalog 预留用户可配置镜像（HTTPS + allowlist + 验签）。
- portable marker 修复会改变安装版数据目录行为，需在 iss 中同步并保留迁移。
