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
