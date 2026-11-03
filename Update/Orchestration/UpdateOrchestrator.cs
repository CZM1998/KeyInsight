using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KeyInsight.Update.Download;
using KeyInsight.Update.Orchestration;
using KeyInsight.Update.Manifest;
using KeyInsight.Update.Routing;

namespace KeyInsight.Update.Orchestration
{
    public enum UpdateState
    {
        Idle = 0,
        Checking = 1,
        VerifyingManifest = 2,
        UpdateAvailable = 3,
        Downloading = 4,
        VerifyingArtifact = 5,
        ReadyToInstall = 6,
        Failed = 7,
        Cancelled = 8,
        UpToDate = 9
    }

    /// <summary>
    /// 更新编排状态机（路线图 PR-4/5/6 客户端侧）：
    /// Idle → Checking → hedged manifest → VerifyingManifest → UpdateAvailable/UpToDate
    /// → Downloading → VerifyingArtifact → ReadyToInstall。
    /// 安装事务（备份/替换/回滚）由独立 KeyInsight.Updater.exe 负责。
    /// 所有回调在后台线程触发；UI 必须自行 Post。
    /// </summary>
    public sealed class UpdateOrchestrator
    {
        private readonly RouteCatalog _catalog;
        private readonly string _stagingDir;
        private readonly Func<bool> _cancelled;

        public UpdateState State { get; private set; }
        public string LastError { get; private set; }
        public string LastErrorCategory { get; private set; }
        public UpdateManifest CurrentManifest { get; private set; }
        public string ReadyInstallerPath { get; private set; }
        public string LastRouteName { get; private set; }      // 最近一次成功清单获取的线路
        public double LastManifestTtfbMs { get; private set; }

        public event Action<UpdateState, string> StateChanged;
        public event Action<long, long> DownloadProgress;
        public event Action<List<RouteEndpoint>> RoutesProbed;

        public UpdateOrchestrator(RouteCatalog catalog, string stagingDir, Func<bool> cancelled)
        {
            _catalog = catalog;
            _stagingDir = stagingDir;
            _cancelled = cancelled;
            State = UpdateState.Idle;
        }

        private void SetState(UpdateState state, string detail)
        {
            State = state;
            Action<UpdateState, string> h = StateChanged;
            if (h != null)
            {
                try { h(state, detail); } catch { }
            }
        }

        /// <summary>
        /// 只检查不下载（复审方案 §3.3/PR-3 验收：API 不可达时仍可经固定名 Release 资产发现版本）。
        /// hedged 多线路取清单 → 验签 → 解析 → 渠道/版本/撤回/最低升级版本/rollout 判定。
        /// </summary>
        public CheckResult CheckOnly(Version currentVersion, string channel, int rolloutCohort)
        {
            CheckResult r = new CheckResult();
            RouteProbe.ManifestFetchResult fetch = RouteProbe.FetchManifestWithHedging(_catalog);
            if (!fetch.Ok)
            {
                r.Error = fetch.Error;
                r.ErrorCategory = fetch.ErrorCategory;
                return r;
            }
            r.RouteName = fetch.RouteName;
            r.TtfbMs = fetch.TtfbMs;

            ManifestVerifier.VerifyResult vr = ManifestVerifier.Verify(
                fetch.ManifestBytes, fetch.Signature, ExtractKeyId(fetch.ManifestBytes));
            if (!vr.Ok)
            {
                r.Error = "manifest signature invalid: " + vr.Error;
                r.ErrorCategory = "Signature";
                return r;
            }

            UpdateManifest manifest;
            string parseError;
            if (!UpdateManifest.TryParse(fetch.ManifestBytes, out manifest, out parseError))
            {
                r.Error = "manifest parse: " + parseError;
                r.ErrorCategory = "ManifestInvalid";
                return r;
            }

            // 只有验签 + 解析成功的清单才算一次成功检查（调用方据此写 LastUpdateCheckTicks）
            r.Ok = true;
            r.Manifest = manifest;
            r.VersionText = manifest.VersionText;
            LastRouteName = fetch.RouteName;
            LastManifestTtfbMs = fetch.TtfbMs;
            CurrentManifest = manifest;

            if (manifest.Channel != channel)
            {
                r.Error = "channel mismatch: " + manifest.Channel;
                r.ErrorCategory = "ChannelMismatch";
                return r;
            }
            if (manifest.Version <= currentVersion)
            {
                r.UpToDate = true;
                return r;
            }
            if (manifest.MinUpgradeFrom != null && currentVersion < manifest.MinUpgradeFrom)
            {
                r.Error = "current version below minUpgradeFrom";
                r.ErrorCategory = "BelowMinimum";
                return r;
            }
            if (manifest.RolloutPercent < 100 && rolloutCohort >= manifest.RolloutPercent)
            {
                r.UpToDate = true;
                r.VersionText += " (rollout " + manifest.RolloutPercent + "%)";
                return r;
            }
            r.UpdateAvailable = true;
            return r;
        }

        /// <summary>CheckOnly 结果：Ok=true 表示清单验签并解析成功（与「有更新」区分）。</summary>
        public sealed class CheckResult
        {
            public bool Ok;
            public bool UpToDate;
            public bool UpdateAvailable;
            public string VersionText = "";
            public string Error = "";
            public string ErrorCategory = "";
            public UpdateManifest Manifest;
            public string RouteName = "";
            public double TtfbMs = -1;
        }

        /// <summary>
        /// 执行一次完整检查+下载（阻塞；调用方应放入后台线程）。
        /// currentVersion：本地版本；channel/rolloutCohort：渠道与分批（本地随机 0..99）。
        /// 返回 true 表示 ReadyToInstall（ReadyInstallerPath 有效）。
        /// </summary>
        public bool CheckAndDownload(Version currentVersion, string channel, int rolloutCohort,
            string assetId)
        {
            SetState(UpdateState.Checking, null);

            // PR-5：hedged 多线路 manifest 获取（含验签）
            SetState(UpdateState.VerifyingManifest, null);
            RouteProbe.ManifestFetchResult fetch = RouteProbe.FetchManifestWithHedging(_catalog);
            if (!fetch.Ok)
            {
                LastError = fetch.Error;
                LastErrorCategory = fetch.ErrorCategory;
                SetState(UpdateState.Failed, fetch.ErrorCategory);
                return false;
            }
            LastRouteName = fetch.RouteName;
            LastManifestTtfbMs = fetch.TtfbMs;

            // PR-4：验签（内置公钥 + KeyId）
            ManifestVerifier.VerifyResult vr = ManifestVerifier.Verify(
                fetch.ManifestBytes, fetch.Signature, ExtractKeyId(fetch.ManifestBytes));
            if (!vr.Ok)
            {
                LastError = "manifest signature invalid: " + vr.Error;
                LastErrorCategory = "Signature";
                SetState(UpdateState.Failed, "Signature");
                return false;
            }

            UpdateManifest manifest;
            string parseError;
            if (!UpdateManifest.TryParse(fetch.ManifestBytes, out manifest, out parseError))
            {
                LastError = "manifest parse: " + parseError;
                LastErrorCategory = "ManifestInvalid";
                SetState(UpdateState.Failed, "ManifestInvalid");
                return false;
            }
            CurrentManifest = manifest;

            if (manifest.Channel != channel)
            {
                LastError = "channel mismatch: " + manifest.Channel;
                LastErrorCategory = "ChannelMismatch";
                SetState(UpdateState.Failed, "ChannelMismatch");
                return false;
            }

            // 版本规则：升级 / 撤回 / 最低升级版本
            if (manifest.Version <= currentVersion)
            {
                LastError = null;
                SetState(UpdateState.UpToDate, manifest.VersionText);
                return false;
            }
            if (manifest.RevokedVersions.Contains(currentVersion.ToString()))
            {
                // 本地版本被撤回：允许升级（走正常流程），由 UI 提示紧急性
            }
            if (manifest.MinUpgradeFrom != null && currentVersion < manifest.MinUpgradeFrom)
            {
                LastError = "current version below minUpgradeFrom";
                LastErrorCategory = "BelowMinimum";
                SetState(UpdateState.Failed, "BelowMinimum");
                return false;
            }

            // rollout 分批：本地 cohort 不在当前百分比内则本轮跳过
            if (manifest.RolloutPercent < 100 && rolloutCohort >= manifest.RolloutPercent)
            {
                LastError = null;
                SetState(UpdateState.UpToDate, "rollout " + manifest.RolloutPercent + "% (cohort " + rolloutCohort + ")");
                return false;
            }

            SetState(UpdateState.UpdateAvailable, manifest.VersionText);

            // PR-6：下载（多 URL 顺序切换 + Range 续传 + SHA-256）
            ManifestAsset asset = manifest.FindAsset(assetId) ?? manifest.Assets[0];
            SetState(UpdateState.Downloading, asset.FileName);
            ResumableDownloader dl = new ResumableDownloader(_stagingDir);
            dl.Cancelled = _cancelled;
            if (DownloadProgress != null)
            {
                dl.Progress += delegate(long done, long total)
                {
                    Action<long, long> h = DownloadProgress;
                    try { h(done, total); } catch { }
                };
            }
            ResumableDownloader.DownloadResult result = dl.Download(asset, asset.Urls, _catalog);
            if (!result.Ok)
            {
                LastError = result.Error;
                LastErrorCategory = result.ErrorCategory;
                SetState(UpdateState.Failed, result.ErrorCategory);
                return false;
            }

            SetState(UpdateState.VerifyingArtifact, result.Sha256);
            // SHA-256 已在下载器内整文件校验；这里再确认 staging 文件存在
            if (!File.Exists(result.FilePath))
            {
                LastError = "staged file missing";
                LastErrorCategory = "StagingMissing";
                SetState(UpdateState.Failed, "StagingMissing");
                return false;
            }

            ReadyInstallerPath = result.FilePath;
            SetState(UpdateState.ReadyToInstall, result.FilePath);
            return true;
        }

        /// <summary>从清单原始字节中提取 keyId 字段（验签前需要 KeyId 选公钥）。</summary>
        private static string ExtractKeyId(byte[] manifestBytes)
        {
            // 简化实现：小 JSON 上做一次文本搜索（清单最大 256KB，风险可控）。
            // 严格模式可改为两次验签尝试所有内置 KeyId。
            string text = System.Text.Encoding.UTF8.GetString(manifestBytes);
            string marker = "\"keyId\":\"";
            int i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + marker.Length;
            int end = text.IndexOf('"', start);
            if (end < 0 || end <= start) return null;
            return text.Substring(start, end - start);
        }

        public void SaveRouteHealth()
        {
            _catalog.SaveHealth();
        }
    }
}
