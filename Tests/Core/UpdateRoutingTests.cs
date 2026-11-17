using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using KeyInsight.Update.Manifest;
using KeyInsight.Update.Orchestration;
using KeyInsight.Update.Routing;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// 更新网络/编排验收（全部使用注入传输，不访问互联网）：
    /// 完成序 hedging、验签后胜出、.json.sig/.sig 与 Base64/裸签名兼容、
    /// metadata-only 兜底、版本规范化、DownloadVerified 不使用 unsigned 清单。
    /// </summary>
    internal static class UpdateRoutingTests
    {
        private const string TestKeyId = "release-key-2026";
        // 测试用固定签名字节：不解密、不接触任何真实私钥；仅验证路由/解码/验签契约。
        private static readonly byte[] TestSignature = new byte[]
        {
            0x4B, 0x49, 0x2D, 0x54, 0x45, 0x53, 0x54, 0x01
        };

        public static void Register()
        {
            TestRegistry.Add("Core", "UPDATE001_HedgingWinsByCompletion", UPDATE001);
            TestRegistry.Add("Core", "UPDATE002_VerifyFailureTriesNextRouteAndLegacySig", UPDATE002);
            TestRegistry.Add("Core", "UPDATE003_InvalidSignatureNeverWins", UPDATE003);
            TestRegistry.Add("Core", "UPDATE004_OverallBudgetIsBounded", UPDATE004);
            TestRegistry.Add("Core", "UPDATE005_UrlPolicyAndCanonicalReleasePage", UPDATE005);
            TestRegistry.Add("Core", "UPDATE006_MetadataOnlyFallbackAndNoDownload", UPDATE006);
            TestRegistry.Add("Core", "UPDATE007_MetadataMirrorAndNormalizedVersion", UPDATE007);
            TestRegistry.Add("Core", "UPDATE008_MetadataRejectsDraftPrereleaseAndBadTag", UPDATE008);
            TestRegistry.Add("Core", "UPDATE009_SignedChannelPolicyAndStaleClear", UPDATE009);
            TestRegistry.Add("Core", "UPDATE010_CheckAndDownloadNoMetadataFallback", UPDATE010);
            TestRegistry.Add("Core", "UPDATE011_DownloadVerifiedUsesCachedManifest", UPDATE011);
            TestRegistry.Add("Core", "UPDATE012_InvalidJsonAndAllCandidatesTried", UPDATE012);
        }

        private static Func<byte[], byte[], string, ManifestVerifier.VerifyResult> VerifyWithTestKey()
        {
            return delegate(byte[] data, byte[] signature, string keyId)
            {
                ManifestVerifier.VerifyResult r = new ManifestVerifier.VerifyResult();
                if (!string.Equals(keyId, TestKeyId, StringComparison.Ordinal))
                {
                    r.Error = "unknown keyId: " + keyId;
                    return r;
                }
                try
                {
                    r.Ok = SignaturesEqual(signature, TestSignature);
                    if (!r.Ok) r.Error = "signature mismatch";
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                }
                return r;
            };
        }

        private static byte[] Sign(byte[] data)
        {
            return TestSignature;
        }

        private static bool SignaturesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        private static string ManifestJson(string version, string channel, int rolloutPercent,
            int? minUpgradeMinor)
        {
            string minUpgrade = minUpgradeMinor.HasValue
                ? "\"" + minUpgradeMinor.Value + ".0.0\""
                : "null";
            string sha = new string('a', 64);
            return "{\"schema\":1,\"product\":\"KeyInsight\",\"channel\":\"" + channel +
                "\",\"version\":\"" + version + "\",\"publishedAt\":\"2026-01-01T00:00:00Z\"," +
                "\"rolloutPercent\":" + rolloutPercent + ",\"minUpgradeFrom\":" + minUpgrade +
                ",\"revoked\":[],\"assets\":[{\"id\":\"windows-x64-portable\"," +
                "\"kind\":\"portable\",\"fileName\":\"KeyInsight-" + version +
                "-portable.zip\",\"size\":1,\"sha256\":\"" + sha +
                "\",\"urls\":[\"https://github.com/CZM1998/KeyInsight/releases/download/v" +
                version + "/KeyInsight-" + version + "-portable.zip\"]}]," +
                "\"keyId\":\"" + TestKeyId + "\"}";
        }

        private static byte[] ManifestBytes(string version, string channel, int rolloutPercent,
            int? minUpgradeMinor)
        {
            return new UTF8Encoding(false).GetBytes(
                ManifestJson(version, channel, rolloutPercent, minUpgradeMinor));
        }

        private static UpdateManifest ParseManifest(string version, string channel,
            int rolloutPercent, int? minUpgradeMinor)
        {
            UpdateManifest manifest;
            string error;
            bool ok = UpdateManifest.TryParse(
                ManifestBytes(version, channel, rolloutPercent, minUpgradeMinor),
                out manifest, out error);
            Check.True(ok, "test manifest must parse: " + error);
            return manifest;
        }

        private static RouteEndpoint AddRoute(RouteCatalog catalog, string name, string manifestUrl)
        {
            Check.True(catalog.AddRoute(name, manifestUrl, null), "route should be allowed: " + name);
            foreach (RouteEndpoint endpoint in catalog.Routes)
            {
                if (string.Equals(endpoint.Name, name, StringComparison.Ordinal)) return endpoint;
            }
            throw new Exception("route not found after add: " + name);
        }

        private static void Prioritize(RouteCatalog catalog, RouteEndpoint route)
        {
            catalog.HealthOf(route).RecordSuccess(5, 8 * 1024 * 1024);
        }

        private static void Prioritize(RouteCatalog catalog, RouteEndpoint route, double ttfbMs)
        {
            catalog.HealthOf(route).RecordSuccess(ttfbMs, 8 * 1024 * 1024);
        }

        private static UpdateOrchestrator NewOrchestrator()
        {
            return new UpdateOrchestrator(new RouteCatalog(null), TestPaths.NewTempDir(), null);
        }

        private static RouteProbe.ManifestFetchResult FailedFetch(string category)
        {
            return new RouteProbe.ManifestFetchResult
            {
                Ok = false,
                Error = "injected " + category,
                ErrorCategory = category
            };
        }

        private static RouteProbe.ManifestFetchResult SignedFetch(UpdateManifest manifest)
        {
            return new RouteProbe.ManifestFetchResult
            {
                Ok = true,
                Verified = true,
                Manifest = manifest,
                RouteName = "injected-signed",
                TtfbMs = 12
            };
        }

        private static string MetadataJson(string tag, bool draft, bool prerelease)
        {
            return "{\"tag_name\":\"" + tag + "\",\"draft\":" +
                (draft ? "true" : "false") + ",\"prerelease\":" +
                (prerelease ? "true" : "false") +
                ",\"html_url\":\"https://evil.example/KeyInsight/releases/tag/v9.9.9\"," +
                "\"assets\":[{\"name\":\"unsigned.exe\",\"browser_download_url\":" +
                "\"https://evil.example/unsigned.exe\"}]}";
        }

        private static UpdateOrchestrator.MetadataResponse MetadataResponse(string json, double ttfb)
        {
            return new UpdateOrchestrator.MetadataResponse { Json = json, TtfbMs = ttfb };
        }

        private static string LegacySigUrl(string manifestUrl)
        {
            return manifestUrl.Substring(0, manifestUrl.Length - ".json".Length) + ".sig";
        }

        private static void UPDATE001()
        {
            byte[] manifest = ManifestBytes("3.4.0", "stable", 100, null);
            byte[] signature = Sign(manifest);
            string slowUrl = "https://api.github.com/update/slow/KeyInsight-update.json";
            string fastUrl = "https://api.github.com/update/fast/KeyInsight-update.json";

            RouteCatalog catalog = new RouteCatalog(false);
            RouteEndpoint slow = AddRoute(catalog, "slow", slowUrl);
            AddRoute(catalog, "fast", fastUrl);
            Prioritize(catalog, slow); // 确保慢线路先启动

            List<string> progress = new List<string>();
            RouteProbe.RouteProbeOptions options = new RouteProbe.RouteProbeOptions();
            options.HedgeDelayMs = 20;
            options.OverallBudgetMs = 3000;
            options.RequestTimeoutMs = 1000;
            options.MaxConcurrentRoutes = 2;
            options.Verify = VerifyWithTestKey();
            options.Progress = delegate(string text)
            {
                lock (progress) progress.Add(text);
            };
            options.HttpGet = delegate(string url, int timeout, CancellationToken token)
            {
                if (string.Equals(url, slowUrl, StringComparison.Ordinal))
                {
                    Thread.Sleep(800);
                    throw new WebException("slow route failed", WebExceptionStatus.Timeout);
                }
                if (string.Equals(url, fastUrl, StringComparison.Ordinal)) return manifest;
                if (string.Equals(url, fastUrl + ".sig", StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes(Convert.ToBase64String(signature) + "\r\n");
                }
                throw new WebException("not found", WebExceptionStatus.ProtocolError);
            };

            Stopwatch clock = Stopwatch.StartNew();
            RouteProbe.ManifestFetchResult result =
                RouteProbe.FetchManifestWithHedging(catalog, options);
            clock.Stop();

            Check.True(result.Ok, "fast route should win: " + result.Error);
            Check.True(result.Verified, "winner must be verified");
            Check.Equal("fast", result.RouteName, "completion order wins, not launch order");
            Check.Equal("3.4.0", result.Manifest.VersionText, "parsed manifest");
            Check.True(clock.ElapsedMilliseconds < 600,
                "must not wait for slow route, elapsed=" + clock.ElapsedMilliseconds + "ms");
            Check.True(result.TtfbMs >= 0, "TTFB should be measured");
            lock (progress)
            {
                Check.True(progress.Exists(delegate(string s)
                {
                    return s.IndexOf("正在尝试线路", StringComparison.Ordinal) >= 0;
                }), "attempt progress should be reported");
            }
        }

        private static void UPDATE002()
        {
            byte[] manifest = ManifestBytes("3.4.0", "stable", 100, null);
            byte[] validSignature = Sign(manifest);
            byte[] wrongSignature = new byte[] { 0x09, 0x09, 0x09 };
            string badUrl = "https://api.github.com/update/bad/KeyInsight-update.json";
            string goodUrl = "https://api.github.com/update/good/KeyInsight-update.json";

            RouteCatalog catalog = new RouteCatalog(false);
            RouteEndpoint bad = AddRoute(catalog, "bad-signature", badUrl);
            AddRoute(catalog, "good-signature", goodUrl);
            Prioritize(catalog, bad);

            List<string> requests = new List<string>();
            List<string> progress = new List<string>();
            RouteProbe.RouteProbeOptions options = new RouteProbe.RouteProbeOptions();
            options.HedgeDelayMs = 5;
            options.OverallBudgetMs = 2000;
            options.RequestTimeoutMs = 1000;
            options.MaxConcurrentRoutes = 2;
            options.Verify = VerifyWithTestKey();
            options.Progress = delegate(string text) { lock (progress) progress.Add(text); };
            options.HttpGet = delegate(string url, int timeout, CancellationToken token)
            {
                lock (requests) requests.Add(url);
                if (string.Equals(url, badUrl, StringComparison.Ordinal)) return manifest;
                if (string.Equals(url, badUrl + ".sig", StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes(Convert.ToBase64String(wrongSignature) + "\n");
                }
                if (string.Equals(url, LegacySigUrl(badUrl), StringComparison.Ordinal))
                {
                    throw new WebException("missing", WebExceptionStatus.ProtocolError);
                }
                if (string.Equals(url, goodUrl, StringComparison.Ordinal)) return manifest;
                if (string.Equals(url, goodUrl + ".sig", StringComparison.Ordinal))
                {
                    throw new WebException("missing", WebExceptionStatus.ProtocolError);
                }
                if (string.Equals(url, LegacySigUrl(goodUrl), StringComparison.Ordinal))
                {
                    return validSignature; // 历史裸 RSA 签名字节
                }
                throw new WebException("not found", WebExceptionStatus.ProtocolError);
            };

            RouteProbe.ManifestFetchResult result =
                RouteProbe.FetchManifestWithHedging(catalog, options);

            Check.True(result.Ok, "second route should win after first signature failure: " +
                result.Error);
            Check.Equal("good-signature", result.RouteName, "bad signature must not win");
            Check.Equal(validSignature.Length, result.Signature.Length, "raw signature accepted");
            lock (requests)
            {
                Check.True(requests.Contains(LegacySigUrl(goodUrl)),
                    "legacy .sig path should be tried");
                Check.True(requests.Contains(goodUrl + ".sig"),
                    "new .json.sig path should be tried first");
            }
            lock (progress)
            {
                Check.True(progress.Exists(delegate(string s)
                {
                    return s.IndexOf("切换下一线路", StringComparison.Ordinal) >= 0;
                }), "failover progress should be reported");
            }
        }

        private static void UPDATE003()
        {
            byte[] manifest = ManifestBytes("3.4.0", "stable", 100, null);
            byte[] wrongSignature = new byte[] { 0x08, 0x08, 0x08 };
            string url = "https://api.github.com/update/invalid/KeyInsight-update.json";
            RouteCatalog catalog = new RouteCatalog(false);
            AddRoute(catalog, "invalid", url);

            RouteProbe.RouteProbeOptions options = new RouteProbe.RouteProbeOptions();
            options.HedgeDelayMs = 5;
            options.OverallBudgetMs = 1000;
            options.Verify = VerifyWithTestKey();
            options.HttpGet = delegate(string requestUrl, int timeout, CancellationToken token)
            {
                if (string.Equals(requestUrl, url, StringComparison.Ordinal)) return manifest;
                if (requestUrl.EndsWith(".sig", StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes(Convert.ToBase64String(wrongSignature));
                }
                throw new WebException("not found", WebExceptionStatus.ProtocolError);
            };

            RouteProbe.ManifestFetchResult result =
                RouteProbe.FetchManifestWithHedging(catalog, options);
            Check.True(!result.Ok, "invalid signature must not produce a winner");
            Check.Equal("Signature", result.ErrorCategory, "invalid signature category");
        }

        private static void UPDATE004()
        {
            string url = "https://api.github.com/update/slow-budget/KeyInsight-update.json";
            RouteCatalog catalog = new RouteCatalog(false);
            AddRoute(catalog, "slow-budget", url);

            RouteProbe.RouteProbeOptions options = new RouteProbe.RouteProbeOptions();
            options.HedgeDelayMs = 10;
            options.OverallBudgetMs = 150;
            options.RequestTimeoutMs = 5000;
            options.MaxConcurrentRoutes = 1;
            options.HttpGet = delegate(string requestUrl, int timeout, CancellationToken token)
            {
                Thread.Sleep(1200);
                throw new WebException("late", WebExceptionStatus.Timeout);
            };

            Stopwatch clock = Stopwatch.StartNew();
            RouteProbe.ManifestFetchResult result =
                RouteProbe.FetchManifestWithHedging(catalog, options);
            clock.Stop();

            Check.True(!result.Ok, "budget expiry must fail");
            Check.Equal("Timeout", result.ErrorCategory, "budget category");
            Check.True(clock.ElapsedMilliseconds < 800,
                "hedging must return promptly on budget expiry, elapsed=" +
                clock.ElapsedMilliseconds + "ms");
        }

        private static void UPDATE005()
        {
            Check.True(RouteCatalog.IsAllowedUrl(
                "https://api.github.com/repos/CZM1998/KeyInsight/releases/latest"),
                "official API host allowed");
            Check.True(RouteCatalog.IsAllowedUrl(
                "https://raw.githubusercontent.com/CZM1998/KeyInsight/master/update/manifest.json"),
                "raw host allowed");
            Check.True(!RouteCatalog.IsAllowedUrl(
                "http://api.github.com/repos/CZM1998/KeyInsight/releases/latest"),
                "plain HTTP rejected");
            Check.True(!RouteCatalog.IsAllowedUrl("https://evil.example/update.json"),
                "unknown host rejected");
            Check.True(!RouteCatalog.IsAllowedUrl(
                "https://api.github.com.evil.example/update.json"),
                "suffix host attack rejected");
            Check.True(!RouteCatalog.IsAllowedUrl(
                "https://api.github.com:8443/update.json"),
                "non-default HTTPS port rejected");
            Check.True(!RouteCatalog.IsAllowedUrl(
                "https://user:pass@api.github.com/update.json"),
                "userinfo rejected");

            Check.Equal("https://github.com/CZM1998/KeyInsight/releases/tag/v3.4.0",
                RouteCatalog.CanonicalReleaseUrl(new Version("3.4.0.0")),
                "3.4.0.0 canonical page normalizes to v3.4.0");
            Check.Equal("https://github.com/CZM1998/KeyInsight/releases/tag/v3.4.0.1",
                RouteCatalog.CanonicalReleaseUrl(new Version("3.4.0.1")),
                "fourth component retained when non-zero");
        }

        private static void UPDATE006()
        {
            UpdateOrchestrator orchestrator = NewOrchestrator();
            List<string> metadataCalls = new List<string>();
            List<string> progress = new List<string>();
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return FailedFetch("HTTP404");
            };
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                metadataCalls.Add(url);
                return MetadataResponse(MetadataJson("v3.4.0", false, false), 25);
            };

            UpdateOrchestrator.CheckResult result = orchestrator.CheckOnly(
                new Version("3.3.0"), "stable", 0, delegate(string text)
                {
                    lock (progress) progress.Add(text);
                });

            Check.True(result.Ok, "metadata-only check should succeed: " + result.Error);
            Check.True(result.MetadataOnly, "result must be marked metadata-only");
            Check.True(result.UpdateAvailable, "newer stable version should be offered");
            Check.Equal("3.4.0", result.VersionText, "stable version text");
            Check.Equal("https://github.com/CZM1998/KeyInsight/releases/tag/v3.4.0",
                result.ReleaseUrl, "release page constructed locally");
            Check.True(result.Manifest == null, "metadata-only must not expose a manifest");
            Check.True(orchestrator.CurrentManifest == null,
                "metadata-only must not populate CurrentManifest");
            Check.True(metadataCalls.Count > 0, "metadata endpoint should be queried");
            Check.Equal(RouteCatalog.OfficialReleaseApiUrl, metadataCalls[0],
                "official REST queried first");
            Check.Near(25, result.TtfbMs, 0.1, "metadata TTFB");
            lock (progress)
            {
                Check.True(progress.Exists(delegate(string s)
                {
                    return s.IndexOf("签名更新清单", StringComparison.Ordinal) >= 0;
                }), "fetch stage should be reported");
                Check.True(progress.Exists(delegate(string s)
                {
                    return s.IndexOf("元数据", StringComparison.Ordinal) >= 0;
                }), "fallback/result stage should be reported");
            }

            bool ready = orchestrator.DownloadVerified(
                new Version("3.3.0"), "stable", 0, "windows-x64-portable");
            Check.True(!ready, "metadata-only must never enable download");
            Check.Equal("NoVerifiedManifest", orchestrator.LastErrorCategory,
                "download without verified manifest is rejected");
        }

        private static void UPDATE007()
        {
            UpdateOrchestrator orchestrator = NewOrchestrator();
            List<string> calls = new List<string>();
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return FailedFetch("Timeout");
            };
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                calls.Add(url);
                if (string.Equals(url, RouteCatalog.OfficialReleaseApiUrl, StringComparison.Ordinal))
                {
                    throw new WebException("official API blocked",
                        WebExceptionStatus.NameResolutionFailure);
                }
                return MetadataResponse(MetadataJson("v3.4.0.0", false, false), 11);
            };

            UpdateOrchestrator.CheckResult result =
                orchestrator.CheckOnly(new Version("3.4.0"), "stable", 0);

            Check.True(result.Ok, "mirror metadata should succeed: " + result.Error);
            Check.True(result.MetadataOnly, "mirror result is metadata-only");
            Check.True(result.UpToDate, "3.4.0 equals 3.4.0.0");
            Check.Equal("3.4.0", result.VersionText, "normalized stable version text");
            Check.Equal("https://github.com/CZM1998/KeyInsight/releases/tag/v3.4.0",
                result.ReleaseUrl, "normalized canonical release page");
            Check.Equal("gh-proxy-release-api", result.RouteName, "verified mirror source");
            Check.Equal(2, calls.Count, "direct REST then gh-proxy mirror");
            Check.Equal(RouteCatalog.OfficialReleaseApiUrl, calls[0], "direct first");
            Check.Equal(RouteCatalog.VerifiedMirrorReleaseApiUrl, calls[1], "mirror second");
        }

        private static void UPDATE008()
        {
            string[] badResponses = new string[]
            {
                MetadataJson("v3.4.0", true, false),
                MetadataJson("v3.4.0", false, true),
                MetadataJson("v3.4.0-beta.1", false, false),
                MetadataJson("v3.4", false, false),
                "<html>not json</html>",
                "{}"
            };

            for (int i = 0; i < badResponses.Length; i++)
            {
                UpdateOrchestrator orchestrator = NewOrchestrator();
                orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
                {
                    return FailedFetch("HTTP404");
                };
                string responseJson = badResponses[i];
                orchestrator.MetadataFetcher = delegate(string url, int timeout)
                {
                    return MetadataResponse(responseJson, 1);
                };

                UpdateOrchestrator.CheckResult result =
                    orchestrator.CheckOnly(new Version("3.3.0"), "stable", 0);
                Check.True(!result.Ok, "bad metadata response must fail, case " + i);
                Check.True(!result.MetadataOnly, "failed metadata is not metadata-only, case " + i);
                Check.True(orchestrator.CurrentManifest == null,
                    "bad metadata must not populate manifest, case " + i);
            }
        }

        private static void UPDATE009()
        {
            UpdateOrchestrator orchestrator = NewOrchestrator();
            int metadataCalls = 0;
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                metadataCalls++;
                return MetadataResponse(MetadataJson("v9.9.9", false, false), 1);
            };

            UpdateManifest upToDateManifest = ParseManifest("3.4.0", "stable", 100, null);
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return SignedFetch(upToDateManifest);
            };
            UpdateOrchestrator.CheckResult upToDate =
                orchestrator.CheckOnly(new Version("3.4.0.0"), "stable", 0);
            Check.True(upToDate.Ok && upToDate.UpToDate, "3.4.0.0 == signed 3.4.0");
            Check.True(orchestrator.CurrentManifest == upToDateManifest,
                "signed manifest cached for DownloadVerified");
            Check.Equal(0L, metadataCalls, "metadata fallback not used when signed route works");

            UpdateManifest betaManifest = ParseManifest("3.4.0", "beta", 100, null);
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return SignedFetch(betaManifest);
            };
            UpdateOrchestrator.CheckResult channelMismatch =
                orchestrator.CheckOnly(new Version("3.3.0"), "stable", 0);
            Check.True(channelMismatch.Ok, "verified manifest remains Ok with app-level error");
            Check.Equal("ChannelMismatch", channelMismatch.ErrorCategory,
                "channel mismatch explicitly signalled");

            UpdateManifest rolloutManifest = ParseManifest("3.4.0", "stable", 10, null);
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return SignedFetch(rolloutManifest);
            };
            UpdateOrchestrator.CheckResult rollout =
                orchestrator.CheckOnly(new Version("3.3.0"), "stable", 50);
            Check.True(rollout.Ok && rollout.UpToDate, "cohort outside rollout is up to date");
            Check.True(rollout.VersionText.IndexOf("rollout 10%", StringComparison.Ordinal) >= 0,
                "rollout detail retained");

            UpdateManifest minManifest = ParseManifest("3.4.0", "stable", 100, 3);
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return SignedFetch(minManifest);
            };
            UpdateOrchestrator.CheckResult belowMin =
                orchestrator.CheckOnly(new Version("2.9.0"), "stable", 0);
            Check.True(belowMin.Ok, "verified manifest remains Ok below minimum");
            Check.Equal("BelowMinimum", belowMin.ErrorCategory,
                "minimum upgrade explicitly signalled");

            // 新的检查开始必须清除上一次验签清单。
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                return FailedFetch("HTTP404");
            };
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                throw new WebException("metadata unavailable", WebExceptionStatus.ConnectFailure);
            };
            UpdateOrchestrator.CheckResult failed =
                orchestrator.CheckOnly(new Version("3.3.0"), "stable", 0);
            Check.True(!failed.Ok, "both signed and metadata unavailable");
            Check.True(orchestrator.CurrentManifest == null,
                "stale signed manifest cleared at new CheckOnly start");
        }

        private static void UPDATE010()
        {
            UpdateOrchestrator orchestrator = NewOrchestrator();
            int manifestCalls = 0;
            int metadataCalls = 0;
            List<UpdateState> states = new List<UpdateState>();
            orchestrator.StateChanged += delegate(UpdateState state, string detail)
            {
                lock (states) states.Add(state);
            };
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                manifestCalls++;
                return FailedFetch("HTTP404");
            };
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                metadataCalls++;
                return MetadataResponse(MetadataJson("v3.4.0", false, false), 1);
            };

            bool ready = orchestrator.CheckAndDownload(
                new Version("3.3.0"), "stable", 0, "windows-x64-portable");

            Check.True(!ready, "signed route failure must not download");
            Check.Equal(1L, manifestCalls, "CheckAndDownload uses signed route logic");
            Check.Equal(0L, metadataCalls, "CheckAndDownload never uses metadata fallback");
            Check.Equal("Failed", orchestrator.State.ToString(), "failure state");
            Check.Equal("HTTP404", orchestrator.LastErrorCategory, "classified error retained");
            lock (states)
            {
                Check.True(states.Contains(UpdateState.Checking), "Checking state emitted");
                Check.True(states.Contains(UpdateState.VerifyingManifest),
                    "VerifyingManifest state emitted");
                Check.True(states.Contains(UpdateState.Failed), "Failed state emitted");
            }
            Check.True(orchestrator.CurrentManifest == null,
                "failed CheckAndDownload leaves no trusted manifest");
        }

        private static void UPDATE011()
        {
            UpdateOrchestrator orchestrator = NewOrchestrator();
            int manifestCalls = 0;
            int metadataCalls = 0;
            List<UpdateState> states = new List<UpdateState>();
            orchestrator.StateChanged += delegate(UpdateState state, string detail)
            {
                lock (states) states.Add(state);
            };
            UpdateManifest manifest = ParseManifest("3.4.0", "stable", 100, null);
            orchestrator.ManifestFetcher = delegate(RouteCatalog catalog, Action<string> reporter)
            {
                manifestCalls++;
                return SignedFetch(manifest);
            };
            orchestrator.MetadataFetcher = delegate(string url, int timeout)
            {
                metadataCalls++;
                return MetadataResponse(MetadataJson("v9.9.9", false, false), 1);
            };

            UpdateOrchestrator.CheckResult check =
                orchestrator.CheckOnly(new Version("3.3.0"), "stable", 0);
            Check.True(check.Ok && check.UpdateAvailable, "signed update available");
            Check.Equal(1L, manifestCalls, "one signed manifest fetch");

            bool ready = orchestrator.DownloadVerified(
                new Version("3.5.0"), "stable", 0, "windows-x64-portable");
            Check.True(!ready, "cached manifest older than current -> no download");
            Check.Equal("UpToDate", orchestrator.State.ToString(),
                "cached manifest version checked");
            Check.Equal(1L, manifestCalls, "DownloadVerified does not refetch manifest");
            Check.Equal(0L, metadataCalls, "DownloadVerified does not use metadata");
            lock (states)
            {
                Check.True(states.Contains(UpdateState.UpToDate), "UpToDate state emitted");
            }

            ready = orchestrator.DownloadVerified(
                new Version("3.3.0"), "beta", 0, "windows-x64-portable");
            Check.True(!ready, "channel mismatch blocks cached manifest download");
            Check.Equal("Failed", orchestrator.State.ToString(), "channel mismatch failed state");
            Check.Equal("ChannelMismatch", orchestrator.LastErrorCategory,
                "cached manifest channel rechecked");
        }

        private static void UPDATE012()
        {
            byte[] manifest = ManifestBytes("3.4.0", "stable", 100, null);
            byte[] signature = Sign(manifest);
            string firstUrl = "https://api.github.com/update/first/KeyInsight-update.json";
            string secondUrl = "https://api.github.com/update/second/KeyInsight-update.json";
            string thirdUrl = "https://api.github.com/update/third/KeyInsight-update.json";
            string fourthUrl = "https://api.github.com/update/fourth/KeyInsight-update.json";

            RouteCatalog catalog = new RouteCatalog(false);
            RouteEndpoint first = AddRoute(catalog, "first", firstUrl);
            RouteEndpoint second = AddRoute(catalog, "second", secondUrl);
            RouteEndpoint third = AddRoute(catalog, "third", thirdUrl);
            AddRoute(catalog, "fourth", fourthUrl);
            Prioritize(catalog, first, 5);
            Prioritize(catalog, second, 100);
            Prioritize(catalog, third, 1000);

            RouteProbe.RouteProbeOptions options = new RouteProbe.RouteProbeOptions();
            options.HedgeDelayMs = 5;
            options.OverallBudgetMs = 3000;
            options.RequestTimeoutMs = 1000;
            options.MaxConcurrentRoutes = 2;
            options.Verify = VerifyWithTestKey();
            options.HttpGet = delegate(string url, int timeout, CancellationToken token)
            {
                if (string.Equals(url, fourthUrl, StringComparison.Ordinal)) return manifest;
                if (string.Equals(url, fourthUrl + ".sig", StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes(Convert.ToBase64String(signature));
                }
                if (string.Equals(url, firstUrl, StringComparison.Ordinal) ||
                    string.Equals(url, secondUrl, StringComparison.Ordinal) ||
                    string.Equals(url, thirdUrl, StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes("{\"not\":\"an update manifest\"}");
                }
                throw new WebException("not found", WebExceptionStatus.ProtocolError);
            };

            RouteProbe.ManifestFetchResult result =
                RouteProbe.FetchManifestWithHedging(catalog, options);

            Check.True(result.Ok, "a fourth candidate must still be tried: " + result.Error);
            Check.Equal("fourth", result.RouteName, "invalid JSON must not be a winner");
        }
    }
}
