using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using KeyInsight.Update.Download;
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
        private const int MetadataRequestTimeoutMs = 3000;
        private const int MetadataOverallBudgetMs = 5500;

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

        /// <summary>CheckOnly 结果：Ok=true 表示清单验签并解析成功，或稳定版元数据读取成功。</summary>
        public sealed class CheckResult
        {
            public bool Ok;
            public bool UpToDate;
            public bool UpdateAvailable;
            /// <summary>true = 仅凭稳定版 REST 元数据得出；不得据此下载或安装。</summary>
            public bool MetadataOnly;
            public string VersionText = "";
            /// <summary>metadata-only 时始终为本地构造的官方 release 页面。</summary>
            public string ReleaseUrl = "";
            public string Error = "";
            public string ErrorCategory = "";
            public UpdateManifest Manifest;
            public string RouteName = "";
            public double TtfbMs = -1;
        }

        /// <summary>
        /// 测试注入点：默认走 RouteProbe（真实 HTTP + 内置公钥验签）。
        /// 注入结果仍必须由调用方明确标记 Verified 且携带已解析清单。
        /// </summary>
        internal Func<RouteCatalog, Action<string>, RouteProbe.ManifestFetchResult> ManifestFetcher;

        /// <summary>测试注入点：稳定版 REST 元数据（默认安全 HTTPS + allowlist 请求）。</summary>
        internal Func<string, int, MetadataResponse> MetadataFetcher;

        internal sealed class MetadataResponse
        {
            public string Json;
            public double TtfbMs = -1;
        }

        public UpdateOrchestrator(RouteCatalog catalog, string stagingDir, Func<bool> cancelled)
        {
            _catalog = catalog;
            _stagingDir = stagingDir;
            _cancelled = cancelled;
            State = UpdateState.Idle;
            ManifestFetcher = delegate(RouteCatalog routeCatalog, Action<string> progress)
            {
                return RouteProbe.FetchManifestWithHedging(routeCatalog, progress);
            };
            MetadataFetcher = FetchMetadataDefault;
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

        private static void EmitProgress(Action<string> onProgress, string message)
        {
            if (onProgress == null) return;
            try { onProgress(message); } catch { }
        }

        /// <summary>
        /// 只检查不下载。签名线路全部失败时，回退到只读的稳定版 Release 元数据：
        /// 仅报告版本与本地构造的官方发布页，不下载、不安装、不信任 unsigned 资产 URL。
        /// 保持既有三参数调用兼容；四参数重载用于 UI 阶段文本。
        /// </summary>
        public CheckResult CheckOnly(Version currentVersion, string channel, int rolloutCohort)
        {
            return CheckOnly(currentVersion, channel, rolloutCohort, null);
        }

        public CheckResult CheckOnly(Version currentVersion, string channel, int rolloutCohort,
            Action<string> onProgress)
        {
            CheckResult result = new CheckResult();
            // 每次检查开始都清除旧清单：metadata-only 结果绝不能被后续 DownloadVerified 误用。
            CurrentManifest = null;
            ReadyInstallerPath = null;
            LastError = null;
            LastErrorCategory = null;
            LastRouteName = "";
            LastManifestTtfbMs = -1;

            if (currentVersion == null)
            {
                result.Error = "current version unavailable";
                result.ErrorCategory = "InvalidVersion";
                EmitProgress(onProgress, "检查失败：无法读取当前版本");
                return result;
            }

            EmitProgress(onProgress, "正在获取签名更新清单…");
            EmitProgress(onProgress, "正在验证签名清单…");
            RouteProbe.ManifestFetchResult fetch = FetchSignedManifest(onProgress);
            UpdateManifest manifest = GetVerifiedManifest(fetch);
            if (manifest != null)
            {
                EmitProgress(onProgress, "签名清单验证通过，正在检查渠道、版本与分批规则…");
                return EvaluateSignedManifest(currentVersion, channel, rolloutCohort, fetch, manifest,
                    onProgress);
            }

            string signedError = fetch == null ? "manifest fetch failed" : fetch.Error;
            string signedCategory = fetch == null ? "RouteProbe" : fetch.ErrorCategory;
            if (string.IsNullOrEmpty(signedCategory)) signedCategory = "SignedUnavailable";
            if (string.IsNullOrEmpty(signedError)) signedError = signedCategory;
            EmitProgress(onProgress, "签名线路不可用（" + signedCategory +
                "），正在查询稳定版 Release 元数据…");

            CheckResult metadata = CheckStableReleaseMetadata(currentVersion, onProgress);
            if (metadata.Ok) return metadata;

            metadata.MetadataOnly = false;
            metadata.Ok = false;
            metadata.ErrorCategory = signedCategory;
            metadata.Error = "signed update channel unavailable (" + signedCategory +
                ": " + signedError + "); release metadata unavailable (" +
                (metadata.Error ?? "unknown") + ")";
            EmitProgress(onProgress, "检查失败：" + metadata.ErrorCategory + " · " + metadata.Error);
            return metadata;
        }

        private RouteProbe.ManifestFetchResult FetchSignedManifest(Action<string> onProgress)
        {
            try
            {
                Func<RouteCatalog, Action<string>, RouteProbe.ManifestFetchResult> fetcher =
                    ManifestFetcher;
                if (fetcher == null)
                {
                    return RouteProbe.FetchManifestWithHedging(_catalog, onProgress);
                }
                RouteProbe.ManifestFetchResult result = fetcher(_catalog, onProgress);
                if (result != null) return result;
                return new RouteProbe.ManifestFetchResult
                {
                    Error = "manifest fetcher returned null",
                    ErrorCategory = "RouteProbe"
                };
            }
            catch (Exception ex)
            {
                return new RouteProbe.ManifestFetchResult
                {
                    Error = ex.Message,
                    ErrorCategory = RouteHealth.ClassifyError(ex)
                };
            }
        }

        /// <summary>
        /// RouteProbe 只在 JSON 解析 + 内置公钥验签成功后返回 Verified。
        /// 若调用方只提供原始字节，则此处再次走现有解析与验签逻辑（KeyId 来自 TryParse）。
        /// </summary>
        private static UpdateManifest GetVerifiedManifest(RouteProbe.ManifestFetchResult fetch)
        {
            if (fetch == null || !fetch.Ok) return null;
            if (fetch.ManifestBytes != null && fetch.Signature != null)
            {
                UpdateManifest manifest;
                string parseError;
                if (!UpdateManifest.TryParse(fetch.ManifestBytes, out manifest, out parseError))
                {
                    return null;
                }
                ManifestVerifier.VerifyResult verified = ManifestVerifier.Verify(
                    fetch.ManifestBytes, fetch.Signature, manifest.KeyId);
                return verified.Ok ? manifest : null;
            }
            // RouteProbe 已将 Verified 设为 true 且携带解析后的清单（测试注入路径）。
            if (fetch.Verified && fetch.Manifest != null) return fetch.Manifest;
            return null;
        }

        private CheckResult EvaluateSignedManifest(Version currentVersion, string channel,
            int rolloutCohort, RouteProbe.ManifestFetchResult fetch, UpdateManifest manifest,
            Action<string> onProgress)
        {
            CheckResult result = new CheckResult();
            result.Ok = true;
            result.Manifest = manifest;
            result.RouteName = fetch.RouteName ?? "";
            result.TtfbMs = fetch.TtfbMs;
            result.VersionText = ManifestVersionText(manifest);
            result.ReleaseUrl = RouteCatalog.CanonicalReleaseUrl(manifest.Version);
            CurrentManifest = manifest;
            LastRouteName = result.RouteName;
            LastManifestTtfbMs = fetch.TtfbMs;

            if (manifest.Version == null)
            {
                result.Error = "manifest version missing";
                result.ErrorCategory = "ManifestInvalid";
                EmitProgress(onProgress, "检查失败：清单版本缺失");
                return result;
            }

            if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
            {
                result.Error = "channel mismatch: " + manifest.Channel;
                result.ErrorCategory = "ChannelMismatch";
                EmitProgress(onProgress, "检查失败：渠道不匹配（" + manifest.Channel + "）");
                return result;
            }

            Version current = RouteCatalog.NormalizeVersion(currentVersion);
            Version manifestVersion = RouteCatalog.NormalizeVersion(manifest.Version);
            if (manifestVersion <= current)
            {
                result.UpToDate = true;
                EmitProgress(onProgress, "检查完成：已是最新版本 " + result.VersionText);
                return result;
            }
            if (manifest.MinUpgradeFrom != null &&
                current < RouteCatalog.NormalizeVersion(manifest.MinUpgradeFrom))
            {
                result.Error = "current version below minUpgradeFrom";
                result.ErrorCategory = "BelowMinimum";
                EmitProgress(onProgress, "检查失败：当前版本低于最低升级版本");
                return result;
            }
            if (manifest.RolloutPercent < 100 && rolloutCohort >= manifest.RolloutPercent)
            {
                result.UpToDate = true;
                result.VersionText += " (rollout " + manifest.RolloutPercent + "%)";
                EmitProgress(onProgress, "检查完成：本轮分批未命中（" +
                    manifest.RolloutPercent + "%）");
                return result;
            }

            result.UpdateAvailable = true;
            EmitProgress(onProgress, "检查完成：发现新版本 " + result.VersionText);
            return result;
        }

        /// <summary>
        /// 稳定版元数据兜底：官方 REST + 已验证 gh-proxy REST 镜像。
        /// 严格解析稳定 tag；拒绝 draft/prerelease；release 页面本地构造。
        /// </summary>
        private CheckResult CheckStableReleaseMetadata(Version currentVersion,
            Action<string> onProgress)
        {
            CheckResult result = new CheckResult();

            string[] urls = new string[]
            {
                RouteCatalog.OfficialReleaseApiUrl,
                RouteCatalog.VerifiedMirrorReleaseApiUrl
            };
            string[] names = new string[] { "github-release-api", "gh-proxy-release-api" };
            string firstErrorCategory = null;
            StringBuilder errors = new StringBuilder();
            Stopwatch clock = Stopwatch.StartNew();

            for (int i = 0; i < urls.Length; i++)
            {
                int remaining = MetadataOverallBudgetMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int timeout = Math.Min(MetadataRequestTimeoutMs, remaining);
                EmitProgress(onProgress, "正在查询稳定版 Release 元数据（" + names[i] + "）…");

                MetadataResponse response;
                try
                {
                    Func<string, int, MetadataResponse> fetcher = MetadataFetcher;
                    response = fetcher == null ? null : fetcher(urls[i], timeout);
                }
                catch (Exception ex)
                {
                    string category = RouteHealth.ClassifyError(ex);
                    if (firstErrorCategory == null) firstErrorCategory = category;
                    AppendMetadataError(errors, names[i], category, ex.Message);
                    continue;
                }

                if (response == null || string.IsNullOrEmpty(response.Json))
                {
                    if (firstErrorCategory == null) firstErrorCategory = "Empty";
                    AppendMetadataError(errors, names[i], "Empty", "empty response");
                    continue;
                }

                Version remoteVersion;
                string versionText;
                string parseError;
                if (!TryParseStableReleaseMetadata(response.Json, out remoteVersion,
                    out versionText, out parseError))
                {
                    if (firstErrorCategory == null) firstErrorCategory = "MetadataInvalid";
                    AppendMetadataError(errors, names[i], "MetadataInvalid", parseError);
                    continue;
                }

                // metadata-only：绝不解析/采信资产 URL，也绝不设置 CurrentManifest。
                result.Ok = true;
                result.MetadataOnly = true;
                result.RouteName = names[i];
                result.TtfbMs = response.TtfbMs;
                result.VersionText = versionText;
                result.ReleaseUrl = RouteCatalog.CanonicalReleaseUrl(remoteVersion);
                LastRouteName = names[i];
                LastManifestTtfbMs = response.TtfbMs;
                CurrentManifest = null;

                if (RouteCatalog.NormalizeVersion(remoteVersion) >
                    RouteCatalog.NormalizeVersion(currentVersion))
                {
                    result.UpdateAvailable = true;
                    EmitProgress(onProgress, "元数据检查完成：最新稳定版 " + versionText +
                        "；仅提供官方发布页（签名清单不可用，未下载）");
                }
                else
                {
                    result.UpToDate = true;
                    EmitProgress(onProgress, "元数据检查完成：已是最新稳定版 " + versionText);
                }
                return result;
            }

            result.Ok = false;
            result.MetadataOnly = false;
            result.ErrorCategory = firstErrorCategory ??
                (clock.ElapsedMilliseconds >= MetadataOverallBudgetMs ? "Timeout" : "MetadataInvalid");
            result.Error = "release metadata unavailable: " +
                (errors.Length == 0 ? result.ErrorCategory : errors.ToString());
            EmitProgress(onProgress, "Release 元数据检查失败（" + result.ErrorCategory + "）");
            return result;
        }

        private static void AppendMetadataError(StringBuilder errors, string source,
            string category, string message)
        {
            if (errors.Length > 0) errors.Append("; ");
            errors.Append(source).Append(':').Append(category);
            if (!string.IsNullOrEmpty(message) &&
                !string.Equals(message, category, StringComparison.Ordinal))
            {
                errors.Append(" (").Append(message).Append(')');
            }
        }

        private static MetadataResponse FetchMetadataDefault(string url, int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = MetadataRequestTimeoutMs;
            double ttfbMs;
            using (CancellationTokenSource cts = new CancellationTokenSource())
            using (Timer timer = new Timer(delegate(object state)
            {
                try { cts.Cancel(); } catch { }
            }, null, timeoutMs, Timeout.Infinite))
            {
                byte[] bytes = RouteProbe.HttpGetBytes(url, timeoutMs, cts.Token, out ttfbMs);
                return new MetadataResponse
                {
                    Json = Encoding.UTF8.GetString(bytes),
                    TtfbMs = ttfbMs
                };
            }
        }

        /// <summary>
        /// 轻量 REST 解析：只接受 draft=false、prerelease=false 的稳定 tag。
        /// </summary>
        internal static bool TryParseStableReleaseMetadata(string json, out Version version,
            out string versionText, out string error)
        {
            version = null;
            versionText = null;
            error = null;
            if (string.IsNullOrEmpty(json) || json.Length > 512 * 1024)
            {
                error = "response size invalid";
                return false;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                IDictionary root = serializer.DeserializeObject(json) as IDictionary;
                if (root == null)
                {
                    error = "root not object";
                    return false;
                }

                bool draft;
                string boolError;
                if (!TryReadOptionalBool(root, "draft", out draft, out boolError))
                {
                    error = boolError;
                    return false;
                }
                if (draft)
                {
                    error = "draft release rejected";
                    return false;
                }

                bool prerelease;
                if (!TryReadOptionalBool(root, "prerelease", out prerelease, out boolError))
                {
                    error = boolError;
                    return false;
                }
                if (prerelease)
                {
                    error = "prerelease rejected";
                    return false;
                }

                string tag = root.Contains("tag_name") ? Convert.ToString(root["tag_name"]) : null;
                if (string.IsNullOrEmpty(tag))
                {
                    error = "tag_name missing";
                    return false;
                }
                if (!TryParseStableTag(tag, out version, out versionText, out error))
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadOptionalBool(IDictionary root, string key, out bool value,
            out string error)
        {
            value = false;
            error = null;
            if (!root.Contains(key) || root[key] == null) return true;
            object raw = root[key];
            if (raw is bool)
            {
                value = (bool)raw;
                return true;
            }
            if (!bool.TryParse(Convert.ToString(raw), out value))
            {
                error = key + " must be boolean";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 严格稳定版本：可选 v 前缀 + 3 或 4 段纯数字，不接受 prerelease/build 后缀。
        /// 3.4.0 与 3.4.0.0 都规范化为 3.4.0。
        /// </summary>
        internal static bool TryParseStableTag(string tag, out Version version,
            out string versionText, out string error)
        {
            version = null;
            versionText = null;
            error = null;
            string text = (tag ?? "").Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (text.Length == 0)
            {
                error = "version tag empty";
                return false;
            }

            string[] parts = text.Split('.');
            if (parts.Length < 3 || parts.Length > 4)
            {
                error = "stable version must have 3 or 4 numeric parts";
                return false;
            }

            int[] values = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0) { error = "empty version part"; return false; }
                for (int c = 0; c < part.Length; c++)
                {
                    if (part[c] < '0' || part[c] > '9')
                    {
                        error = "unstable version suffix rejected";
                        return false;
                    }
                }
                if (!int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                {
                    error = "version part out of range";
                    return false;
                }
            }

            version = new Version(values[0], values[1], values[2], values[3]);
            versionText = values[0] + "." + values[1] + "." + values[2] +
                (values[3] > 0 ? "." + values[3] : "");
            return true;
        }

        private static string ManifestVersionText(UpdateManifest manifest)
        {
            if (manifest == null) return "";
            if (!string.IsNullOrEmpty(manifest.VersionText)) return manifest.VersionText;
            return manifest.Version == null ? "" : manifest.Version.ToString(3);
        }

        /// <summary>
        /// 执行一次完整检查+下载（阻塞；调用方应放入后台线程）。
        /// 与 CheckOnly 共用同一签名路线/验签逻辑；签名不可用时绝不回退到 unsigned 下载。
        /// </summary>
        public bool CheckAndDownload(Version currentVersion, string channel, int rolloutCohort,
            string assetId)
        {
            SetState(UpdateState.Checking, null);
            CurrentManifest = null;
            ReadyInstallerPath = null;
            LastError = null;
            LastErrorCategory = null;
            if (currentVersion == null)
            {
                LastError = "current version unavailable";
                LastErrorCategory = "InvalidVersion";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            SetState(UpdateState.VerifyingManifest, "正在验证签名清单…");
            Action<string> progress = delegate(string detail)
            {
                SetState(UpdateState.VerifyingManifest, detail);
            };
            RouteProbe.ManifestFetchResult fetch = FetchSignedManifest(progress);
            UpdateManifest manifest = GetVerifiedManifest(fetch);
            if (manifest == null)
            {
                LastError = fetch == null ? "manifest fetch failed" : fetch.Error;
                LastErrorCategory = fetch == null ? "RouteProbe" : fetch.ErrorCategory;
                if (string.IsNullOrEmpty(LastErrorCategory)) LastErrorCategory = "Signature";
                if (string.IsNullOrEmpty(LastError)) LastError = LastErrorCategory;
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            LastRouteName = fetch.RouteName ?? "";
            LastManifestTtfbMs = fetch.TtfbMs;
            return ApplyVerifiedManifestAndDownload(manifest, currentVersion, channel, rolloutCohort,
                assetId);
        }

        /// <summary>
        /// 只使用最近一次 CheckOnly/CheckAndDownload 验签成功并缓存的 CurrentManifest。
        /// 不联网获取清单，不回退 metadata；重新执行全部渠道/版本/最低版本/rollout 检查。
        /// </summary>
        public bool DownloadVerified(Version currentVersion, string channel, int rolloutCohort,
            string assetId)
        {
            LastError = null;
            LastErrorCategory = null;
            ReadyInstallerPath = null;
            UpdateManifest manifest = CurrentManifest;
            if (manifest == null)
            {
                LastError = "no verified signed manifest available";
                LastErrorCategory = "NoVerifiedManifest";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }
            if (currentVersion == null)
            {
                LastError = "current version unavailable";
                LastErrorCategory = "InvalidVersion";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            SetState(UpdateState.Checking, "使用已验证签名清单");
            return ApplyVerifiedManifestAndDownload(manifest, currentVersion, channel, rolloutCohort,
                assetId);
        }

        private bool ApplyVerifiedManifestAndDownload(UpdateManifest manifest, Version currentVersion,
            string channel, int rolloutCohort, string assetId)
        {
            CurrentManifest = manifest;
            if (manifest.Version == null)
            {
                LastError = "manifest version missing";
                LastErrorCategory = "ManifestInvalid";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }
            if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
            {
                LastError = "channel mismatch: " + manifest.Channel;
                LastErrorCategory = "ChannelMismatch";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            Version current = RouteCatalog.NormalizeVersion(currentVersion);
            Version manifestVersion = RouteCatalog.NormalizeVersion(manifest.Version);
            if (manifestVersion <= current)
            {
                LastError = null;
                LastErrorCategory = null;
                SetState(UpdateState.UpToDate, ManifestVersionText(manifest));
                return false;
            }
            if (manifest.MinUpgradeFrom != null &&
                current < RouteCatalog.NormalizeVersion(manifest.MinUpgradeFrom))
            {
                LastError = "current version below minUpgradeFrom";
                LastErrorCategory = "BelowMinimum";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            // 本地版本被撤回时仍允许正常升级流程（UI 负责提示紧急性）。
            if (manifest.RolloutPercent < 100 && rolloutCohort >= manifest.RolloutPercent)
            {
                LastError = null;
                LastErrorCategory = null;
                SetState(UpdateState.UpToDate, "rollout " + manifest.RolloutPercent +
                    "% (cohort " + rolloutCohort + ")");
                return false;
            }

            SetState(UpdateState.UpdateAvailable, ManifestVersionText(manifest));
            return DownloadAsset(manifest, assetId);
        }

        private bool DownloadAsset(UpdateManifest manifest, string assetId)
        {
            ManifestAsset asset = manifest.Assets == null ? null : manifest.FindAsset(assetId);
            if (asset == null && manifest.Assets != null && manifest.Assets.Count > 0)
                asset = manifest.Assets[0];
            if (asset == null)
            {
                LastError = "no downloadable asset in verified manifest";
                LastErrorCategory = "NoAsset";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            SetState(UpdateState.Downloading, asset.FileName);
            ResumableDownloader downloader = new ResumableDownloader(_stagingDir);
            downloader.Cancelled = _cancelled;
            if (DownloadProgress != null)
            {
                downloader.Progress += delegate(long done, long total)
                {
                    Action<long, long> handler = DownloadProgress;
                    try { handler(done, total); } catch { }
                };
            }
            ResumableDownloader.DownloadResult result = downloader.Download(asset, asset.Urls, _catalog);
            if (!result.Ok)
            {
                LastError = result.Error;
                LastErrorCategory = result.ErrorCategory;
                SetState(UpdateState.Failed, result.ErrorCategory);
                return false;
            }

            SetState(UpdateState.VerifyingArtifact, result.Sha256);
            // 下载器已完成整文件 SHA-256；这里确认 staging 文件仍存在。
            if (!File.Exists(result.FilePath))
            {
                LastError = "staged file missing";
                LastErrorCategory = "StagingMissing";
                SetState(UpdateState.Failed, LastErrorCategory);
                return false;
            }

            LastError = null;
            LastErrorCategory = null;
            ReadyInstallerPath = result.FilePath;
            SetState(UpdateState.ReadyToInstall, result.FilePath);
            return true;
        }

        public void SaveRouteHealth()
        {
            _catalog.SaveHealth();
        }
    }
}
