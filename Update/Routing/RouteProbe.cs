using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KeyInsight.Update.Manifest;

namespace KeyInsight.Update.Routing
{
    /// <summary>
    /// 线路探测：有界 hedged 请求 + 固定名签名清单获取。
    /// 主线路立即发起，备线路在 hedge 间隔后启动；按完成顺序取首个
    /// “JSON 可解析 + 签名通过”的结果，而不是按启动顺序等待。
    /// 不做大文件测速，不依赖 UI。
    /// </summary>
    public static class RouteProbe
    {
        public sealed class ManifestFetchResult
        {
            public bool Ok;
            /// <summary>true = 已完成 JSON 解析 + 内置公钥验签。</summary>
            public bool Verified;
            public byte[] ManifestBytes;
            public byte[] Signature;
            public UpdateManifest Manifest;
            public string Error;
            public string ErrorCategory;
            public string RouteName;
            public double TtfbMs;
        }

        private const int RequestTimeoutMs = 4000;
        private const int HedgeDelayMs = 350;
        private const int OverallBudgetMs = 8000;
        private const int MaxHedgedRoutes = 3;
        private const int MaxRedirects = 5;
        private const int MaxBodyBytes = 256 * 1024;

        static RouteProbe()
        {
            // 保留系统证书校验，仅确保旧系统启用 TLS 1.2。
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>
        /// 测试/诊断可注入；生产默认使用真实 HTTP 与内置公钥验签。
        /// </summary>
        internal sealed class RouteProbeOptions
        {
            public int RequestTimeoutMs = RouteProbe.RequestTimeoutMs;
            public int HedgeDelayMs = RouteProbe.HedgeDelayMs;
            public int OverallBudgetMs = RouteProbe.OverallBudgetMs;
            public int MaxConcurrentRoutes = MaxHedgedRoutes;
            public Action<string> Progress;
            public Func<string, int, CancellationToken, byte[]> HttpGet = null;
            public Func<byte[], byte[], string, ManifestVerifier.VerifyResult> Verify = null;
        }

        private sealed class FetchJob
        {
            public Task<ManifestFetchResult> Task;
            public RouteEndpoint Route;
        }

        private sealed class RouteFailure
        {
            public string RouteName;
            public string Category;
            public string Error;
        }

        /// <summary>
        /// 兼容既有调用：只调整默认 hedge 间隔 / 总预算，仍返回验签后的结果。
        /// </summary>
        public static ManifestFetchResult FetchManifestWithHedging(RouteCatalog catalog,
            int hedgeDelayMs = HedgeDelayMs)
        {
            RouteProbeOptions options = new RouteProbeOptions();
            options.HedgeDelayMs = hedgeDelayMs < 0 ? 0 : hedgeDelayMs;
            return FetchManifestWithHedging(catalog, options);
        }

        /// <summary>阶段回调版本（中文），供更新检查面板显示进度，不改变 UI 线程模型。</summary>
        public static ManifestFetchResult FetchManifestWithHedging(RouteCatalog catalog,
            Action<string> progress)
        {
            RouteProbeOptions options = new RouteProbeOptions();
            options.Progress = progress;
            return FetchManifestWithHedging(catalog, options);
        }

        internal static ManifestFetchResult FetchManifestWithHedging(RouteCatalog catalog,
            RouteProbeOptions options)
        {
            if (options == null) options = new RouteProbeOptions();
            if (catalog == null)
            {
                return new ManifestFetchResult
                {
                    Error = "route catalog unavailable",
                    ErrorCategory = "NoRoute"
                };
            }

            List<RouteEndpoint> routes = catalog.OrderedAvailableRoutes();
            if (routes.Count == 0)
            {
                return new ManifestFetchResult
                {
                    Error = "no available routes",
                    ErrorCategory = "NoRoute"
                };
            }

            int budgetMs = options.OverallBudgetMs > 0 ? options.OverallBudgetMs : OverallBudgetMs;
            int hedgeMs = options.HedgeDelayMs >= 0 ? options.HedgeDelayMs : HedgeDelayMs;
            int maxWorkers = options.MaxConcurrentRoutes > 0 ? options.MaxConcurrentRoutes : MaxHedgedRoutes;
            if (maxWorkers > routes.Count) maxWorkers = routes.Count;

            List<FetchJob> running = new List<FetchJob>();
            List<RouteFailure> failures = new List<RouteFailure>();
            CancellationTokenSource cts = new CancellationTokenSource();
            Stopwatch clock = Stopwatch.StartNew();
            DateTime nextLaunchUtc = DateTime.UtcNow;
            int launched = 0;
            bool budgetExpired = false;
            ManifestFetchResult winner = null;
            FetchJob winnerJob = null;

            try
            {
                while (true)
                {
                    while (launched < routes.Count && running.Count < maxWorkers &&
                           DateTime.UtcNow >= nextLaunchUtc)
                    {
                        RouteEndpoint route = routes[launched];
                        launched++;
                        Emit(options.Progress, "正在尝试线路 " + route.Name);

                        FetchJob job = new FetchJob();
                        job.Route = route;
                        job.Task = new Task<ManifestFetchResult>(delegate
                        {
                            return FetchOne(route, options, cts.Token);
                        });
                        job.Task.Start();
                        running.Add(job);
                        nextLaunchUtc = DateTime.UtcNow.AddMilliseconds(hedgeMs);
                    }

                    if (running.Count == 0)
                    {
                        if (launched >= routes.Count) break;
                        // 正常情况下不会到这里（失败后立即补位）；保险等待下一 hedge 时刻。
                        int untilLaunch = (int)(nextLaunchUtc - DateTime.UtcNow).TotalMilliseconds;
                        if (untilLaunch > 0) Thread.Sleep(Math.Min(untilLaunch, 50));
                        continue;
                    }

                    int remainingMs = budgetMs - (int)clock.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        budgetExpired = true;
                        break;
                    }

                    int waitMs = remainingMs;
                    if (launched < routes.Count && running.Count < maxWorkers)
                    {
                        int untilLaunch = (int)Math.Max(0,
                            (nextLaunchUtc - DateTime.UtcNow).TotalMilliseconds);
                        if (untilLaunch < waitMs) waitMs = untilLaunch;
                    }

                    Task<ManifestFetchResult>[] tasks =
                        new Task<ManifestFetchResult>[running.Count];
                    for (int i = 0; i < running.Count; i++) tasks[i] = running[i].Task;
                    int completedIndex = Task.WaitAny(tasks, Math.Max(1, waitMs));
                    if (completedIndex < 0) continue;

                    FetchJob completed = running[completedIndex];
                    running.RemoveAt(completedIndex);
                    ManifestFetchResult result = ReadResult(completed.Task);
                    if (result != null && result.Ok && result.Verified && result.Manifest != null)
                    {
                        winner = result;
                        winnerJob = completed;
                        break;
                    }

                    if (result == null)
                    {
                        result = new ManifestFetchResult
                        {
                            RouteName = completed.Route.Name,
                            Error = "route task produced no result",
                            ErrorCategory = "Other"
                        };
                    }
                    if (string.IsNullOrEmpty(result.RouteName)) result.RouteName = completed.Route.Name;
                    if (string.IsNullOrEmpty(result.ErrorCategory)) result.ErrorCategory = "Other";
                    failures.Add(new RouteFailure
                    {
                        RouteName = result.RouteName,
                        Category = result.ErrorCategory,
                        Error = result.Error
                    });
                    catalog.HealthOf(completed.Route).RecordFailure(result.ErrorCategory);
                    Emit(options.Progress, "线路 " + result.RouteName + " 失败（" +
                         result.ErrorCategory + "），切换下一线路");

                    // 失败后立即补位，不等待 hedge 计时；成功则在本轮返回。
                    nextLaunchUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                // 尽力取消仍在进行的 HTTP 请求；不等待慢线路，保证整体有界。
                try { cts.Cancel(); } catch { }
            }

            if (winner != null)
            {
                if (winnerJob != null) catalog.HealthOf(winnerJob.Route).RecordSuccess(winner.TtfbMs, -1);
                Emit(options.Progress, "线路 " + winner.RouteName + " 签名验证通过");
                return winner;
            }

            if (budgetExpired)
            {
                foreach (FetchJob job in running)
                {
                    failures.Add(new RouteFailure
                    {
                        RouteName = job.Route.Name,
                        Category = "Timeout",
                        Error = "overall budget exhausted"
                    });
                    catalog.HealthOf(job.Route).RecordFailure("Timeout");
                }
            }

            return BuildFailure(failures, budgetExpired, budgetMs);
        }

        private static ManifestFetchResult ReadResult(Task<ManifestFetchResult> task)
        {
            try
            {
                return task.Result;
            }
            catch (Exception ex)
            {
                return new ManifestFetchResult
                {
                    Error = ex.Message,
                    ErrorCategory = RouteHealth.ClassifyError(ex)
                };
            }
        }

        private static ManifestFetchResult BuildFailure(List<RouteFailure> failures,
            bool budgetExpired, int budgetMs)
        {
            StringBuilder detail = new StringBuilder();
            for (int i = 0; i < failures.Count; i++)
            {
                if (i > 0) detail.Append("; ");
                detail.Append(failures[i].RouteName).Append(':').Append(failures[i].Category);
                if (!string.IsNullOrEmpty(failures[i].Error) &&
                    !string.Equals(failures[i].Error, failures[i].Category, StringComparison.Ordinal))
                {
                    detail.Append(" (").Append(failures[i].Error).Append(')');
                }
            }
            if (budgetExpired)
            {
                if (detail.Length > 0) detail.Append("; ");
                detail.Append("overall budget ").Append(budgetMs).Append("ms exhausted");
            }
            if (detail.Length == 0) detail.Append("no signed route completed");

            return new ManifestFetchResult
            {
                Error = "no signed update channel succeeded: " + detail,
                ErrorCategory = SelectFailureCategory(failures, budgetExpired)
            };
        }

        private static string SelectFailureCategory(List<RouteFailure> failures, bool budgetExpired)
        {
            bool signature = false, manifestInvalid = false, noSignature = false;
            bool http429 = false, timeout = false, http403 = false;
            string first = null;
            bool allSame = true;
            for (int i = 0; i < failures.Count; i++)
            {
                string c = failures[i].Category ?? "Other";
                if (first == null) first = c;
                else if (!string.Equals(first, c, StringComparison.Ordinal)) allSame = false;
                if (c == "Signature") signature = true;
                if (c == "ManifestInvalid") manifestInvalid = true;
                if (c == "NoSignature") noSignature = true;
                if (c == "HTTP429") http429 = true;
                if (c == "HTTP403") http403 = true;
                if (c == "Timeout") timeout = true;
            }
            if (allSame && first != null) return first;
            if (signature) return "Signature";
            if (manifestInvalid) return "ManifestInvalid";
            if (noSignature) return "NoSignature";
            if (http429) return "HTTP429";
            if (timeout || budgetExpired) return "Timeout";
            if (http403) return "HTTP403";
            return first ?? "AllRoutesFailed";
        }

        private static ManifestFetchResult FetchOne(RouteEndpoint route, RouteProbeOptions options,
            CancellationToken token)
        {
            ManifestFetchResult result = new ManifestFetchResult { RouteName = route.Name };
            try
            {
                if (!RouteCatalog.IsAllowedUrl(route.ManifestUrl))
                {
                    throw new RouteFetchException("HostNotAllowed",
                        "manifest URL is not HTTPS/allowlisted: " + route.ManifestUrl);
                }

                double ttfbMs;
                byte[] manifestBytes = FetchBytes(route.ManifestUrl, options, token, out ttfbMs);
                result.TtfbMs = ttfbMs;
                if (manifestBytes == null || manifestBytes.Length == 0)
                {
                    result.Error = "empty manifest";
                    result.ErrorCategory = "Empty";
                    return result;
                }

                UpdateManifest manifest;
                string parseError;
                // KeyId 必须来自正式解析，不能用脆弱的字符串截取。
                if (!UpdateManifest.TryParse(manifestBytes, out manifest, out parseError))
                {
                    result.Error = "manifest parse: " + parseError;
                    result.ErrorCategory = "ManifestInvalid";
                    return result;
                }

                List<string> signatureUrls = BuildSignatureUrls(route.ManifestUrl);
                bool fetchedAnySignature = false;
                string lastSignatureError = null;
                for (int i = 0; i < signatureUrls.Count; i++)
                {
                    string signatureUrl = signatureUrls[i];
                    if (!RouteCatalog.IsAllowedUrl(signatureUrl))
                    {
                        lastSignatureError = "signature URL is not HTTPS/allowlisted: " + signatureUrl;
                        continue;
                    }

                    double signatureTtfb;
                    byte[] signatureBytes;
                    try
                    {
                        signatureBytes = FetchBytes(signatureUrl, options, token, out signatureTtfb);
                    }
                    catch (WebException we)
                    {
                        lastSignatureError = RouteHealth.ClassifyError(we) + ": " + we.Message;
                        continue;
                    }
                    catch (RouteFetchException rfe)
                    {
                        lastSignatureError = rfe.Category + ": " + rfe.Message;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        lastSignatureError = RouteHealth.ClassifyError(ex) + ": " + ex.Message;
                        continue;
                    }

                    if (signatureBytes == null || signatureBytes.Length == 0)
                    {
                        lastSignatureError = "empty signature";
                        continue;
                    }

                    fetchedAnySignature = true;
                    byte[] usedSignature;
                    ManifestVerifier.VerifyResult verified =
                        VerifySignature(manifestBytes, manifest, signatureBytes, options, out usedSignature);
                    if (verified != null && verified.Ok)
                    {
                        result.ManifestBytes = manifestBytes;
                        result.Signature = usedSignature;
                        result.Manifest = manifest;
                        result.Verified = true;
                        result.Ok = true;
                        return result;
                    }
                    lastSignatureError = (verified == null ? "verification unavailable" : verified.Error);
                }

                result.Error = fetchedAnySignature
                    ? "signature invalid: " + (lastSignatureError ?? "verification failed")
                    : "missing signed manifest (" + (lastSignatureError ?? "no signature source") + ")";
                result.ErrorCategory = fetchedAnySignature ? "Signature" : "NoSignature";
                return result;
            }
            catch (RouteFetchException ex)
            {
                result.Error = ex.Message;
                result.ErrorCategory = ex.Category;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "request cancelled";
                result.ErrorCategory = "Cancelled";
                return result;
            }
            catch (WebException ex)
            {
                result.Error = ex.Message;
                result.ErrorCategory = RouteHealth.ClassifyError(ex);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.ErrorCategory = RouteHealth.ClassifyError(ex);
                return result;
            }
        }

        private static ManifestVerifier.VerifyResult VerifySignature(byte[] manifestBytes,
            UpdateManifest manifest, byte[] signatureBytes, RouteProbeOptions options,
            out byte[] usedSignature)
        {
            usedSignature = signatureBytes;
            byte[] decoded = TryDecodeBase64(signatureBytes);
            ManifestVerifier.VerifyResult firstFailure = null;
            if (decoded != null && decoded.Length > 0)
            {
                ManifestVerifier.VerifyResult decodedResult =
                    InvokeVerify(manifestBytes, decoded, manifest.KeyId, options);
                if (decodedResult != null && decodedResult.Ok)
                {
                    usedSignature = decoded;
                    return decodedResult;
                }
                firstFailure = decodedResult;
            }

            // 兼容历史裸 RSA 签名字节文件。
            ManifestVerifier.VerifyResult rawResult =
                InvokeVerify(manifestBytes, signatureBytes, manifest.KeyId, options);
            if (rawResult != null && rawResult.Ok)
            {
                usedSignature = signatureBytes;
                return rawResult;
            }
            return firstFailure ?? rawResult;
        }

        private static ManifestVerifier.VerifyResult InvokeVerify(byte[] manifestBytes,
            byte[] signatureBytes, string keyId, RouteProbeOptions options)
        {
            if (options.Verify != null) return options.Verify(manifestBytes, signatureBytes, keyId);
            return ManifestVerifier.Verify(manifestBytes, signatureBytes, keyId);
        }

        private static byte[] TryDecodeBase64(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            try
            {
                string text = Encoding.UTF8.GetString(raw).Trim();
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1).Trim();
                if (text.Length == 0) return null;
                byte[] decoded = Convert.FromBase64String(text);
                return decoded.Length == 0 ? null : decoded;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> BuildSignatureUrls(string manifestUrl)
        {
            List<string> urls = new List<string>();
            int query = manifestUrl.IndexOf('?');
            string withoutQuery = query >= 0 ? manifestUrl.Substring(0, query) : manifestUrl;
            AddUnique(urls, withoutQuery + ".sig");

            int slash = withoutQuery.LastIndexOf('/');
            int dot = withoutQuery.LastIndexOf('.');
            if (dot > slash) AddUnique(urls, withoutQuery.Substring(0, dot) + ".sig");
            else AddUnique(urls, withoutQuery + ".sig");
            return urls;
        }

        private static void AddUnique(List<string> urls, string url)
        {
            for (int i = 0; i < urls.Count; i++)
            {
                if (string.Equals(urls[i], url, StringComparison.Ordinal)) return;
            }
            urls.Add(url);
        }

        private static byte[] FetchBytes(string url, RouteProbeOptions options,
            CancellationToken token, out double ttfbMs)
        {
            if (!RouteCatalog.IsAllowedUrl(url))
            {
                throw new RouteFetchException("HostNotAllowed",
                    "URL is not HTTPS/allowlisted: " + url);
            }
            if (options.HttpGet != null)
            {
                Stopwatch injectedClock = Stopwatch.StartNew();
                byte[] data = options.HttpGet(url, options.RequestTimeoutMs, token);
                ttfbMs = injectedClock.Elapsed.TotalMilliseconds;
                return data;
            }
            return HttpGetBytes(url, options.RequestTimeoutMs, token, out ttfbMs);
        }

        /// <summary>小文件 GET（上限 256KB），供元数据兜底复用。</summary>
        internal static byte[] HttpGetBytes(string url, int timeoutMs, out double ttfbMs)
        {
            return HttpGetBytes(url, timeoutMs, CancellationToken.None, out ttfbMs);
        }

        internal static byte[] HttpGetBytes(string url, int timeoutMs, CancellationToken token,
            out double ttfbMs)
        {
            ttfbMs = -1;
            if (!RouteCatalog.IsAllowedUrl(url))
            {
                throw new RouteFetchException("HostNotAllowed",
                    "URL is not HTTPS/allowlisted: " + url);
            }
            if (timeoutMs <= 0) timeoutMs = RequestTimeoutMs;

            Uri current = new Uri(url);
            Stopwatch clock = Stopwatch.StartNew();
            int redirects = 0;
            while (true)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(current);
                request.UserAgent = "KeyInsight-Updater";
                request.Accept = "application/json, text/plain, */*";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.AllowAutoRedirect = false; // 每跳手动校验 allowlist + HTTPS
                request.KeepAlive = false;
                // 不覆盖 Proxy：沿用 Windows 系统代理（WinHTTP/IE 配置）。

                using (token.Register(delegate
                {
                    try { request.Abort(); } catch { }
                }))
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        if (++redirects > MaxRedirects)
                        {
                            throw new RouteFetchException("TooManyRedirects",
                                "redirect limit exceeded for " + url);
                        }
                        string location = Convert.ToString(response.Headers["Location"]);
                        Uri next;
                        if (string.IsNullOrEmpty(location) ||
                            !Uri.TryCreate(current, location, out next) ||
                            !RouteCatalog.IsAllowedUrl(next.AbsoluteUri))
                        {
                            throw new RouteFetchException("UnsafeRedirect",
                                "redirect target rejected: " + (location ?? ""));
                        }
                        current = next;
                        continue;
                    }

                    if (status != 200)
                    {
                        throw new RouteFetchException("HTTP" + status,
                            "unexpected HTTP status " + status + " for " + current);
                    }

                    if (ttfbMs < 0) ttfbMs = clock.Elapsed.TotalMilliseconds;
                    if (response.ContentLength > MaxBodyBytes)
                    {
                        throw new RouteFetchException("TooLarge",
                            "response exceeds " + MaxBodyBytes + " bytes");
                    }

                    using (Stream stream = response.GetResponseStream())
                    {
                        List<byte> data = new List<byte>(4096);
                        byte[] buffer = new byte[8192];
                        while (true)
                        {
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            if (data.Count + read > MaxBodyBytes)
                            {
                                throw new RouteFetchException("TooLarge",
                                    "response exceeds " + MaxBodyBytes + " bytes");
                            }
                            data.AddRange(new ArraySegment<byte>(buffer, 0, read));
                        }
                        return data.ToArray();
                    }
                }
            }
        }

        private static void Emit(Action<string> progress, string message)
        {
            if (progress == null) return;
            try { progress(message); } catch { }
        }
    }

    /// <summary>带可分类错误的路线请求异常（重定向/状态码/大小限制等）。</summary>
    internal sealed class RouteFetchException : Exception
    {
        public readonly string Category;

        public RouteFetchException(string category, string message) : base(message)
        {
            Category = category;
        }
    }
}
