using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using KeyInsight.Stats;
using KeyInsight.Update.Manifest;

namespace KeyInsight.Update.Routing
{
    /// <summary>
    /// 路线探测（PR-5 §6.2）：元数据轻量探测 + Hedged Requests（主线路立即发起，
    /// 备选在超时预算后逐个启动），首个验签成功的清单胜出。不做大文件测速。
    /// </summary>
    public static class RouteProbe
    {
        public sealed class ManifestFetchResult
        {
            public bool Ok;
            public byte[] ManifestBytes;
            public byte[] Signature;
            public string Error;
            public string ErrorCategory;
            public string RouteName;
            public double TtfbMs;
        }

        private const int PrimaryTimeoutMs = 6000;
        private const int HedgeDelayMs = 800;
        private const int MaxHedgedRoutes = 3;

        /// <summary>
        /// 按评分顺序探测路线并取回清单字节 + 签名。Hedged：第 2/3 候选在
        /// 350/800ms 后启动（若主候选仍未完成）；任一候选验签成功即胜出。
        /// </summary>
        public static ManifestFetchResult FetchManifestWithHedging(RouteCatalog catalog,
            int hedgeDelayMs = HedgeDelayMs)
        {
            List<RouteEndpoint> routes = catalog.OrderedAvailableRoutes();
            if (routes.Count == 0)
            {
                return new ManifestFetchResult { Error = "no available routes", ErrorCategory = "NoRoute" };
            }

            List<ManifestFetchResult> failures = new List<ManifestFetchResult>();
            List<FetchJob> jobs = new List<FetchJob>();
            object gate = new object();
            bool decided = false;
            ManifestFetchResult winner = null;

            int launched = 0;
            for (int attempt = 0; attempt < Math.Min(MaxHedgedRoutes, routes.Count); attempt++)
            {
                if (attempt == 1) System.Threading.Thread.Sleep(hedgeDelayMs);
                if (decided) break;
                lock (gate) { if (decided) break; launched++; }

                RouteEndpoint route = routes[attempt];
                var job = new System.Threading.Tasks.Task<ManifestFetchResult>(delegate
                {
                    return FetchOne(route);
                });
                lock (gate) jobs.Add(new FetchJob { Task = job, Route = route });
                job.Start();
            }

            // 等待全部 launched 任务完成（已是 hedged 子集），取第一个成功者
            foreach (FetchJob job in jobs)
            {
                try
                {
                    job.Task.Wait();
                }
                catch
                {
                }
                ManifestFetchResult r = job.Task.Result;
                if (r != null && r.Ok)
                {
                    lock (gate)
                    {
                        if (!decided)
                        {
                            decided = true;
                            winner = r;
                        }
                        else
                        {
                            // 迟到的成功者按过期处理
                            UiMetrics.CountSnapshotStale();
                        }
                    }
                    catalog.HealthOf(routes.Find(x => x.Name == r.RouteName)).RecordSuccess(r.TtfbMs, -1);
                    break;
                }
                if (r != null) failures.Add(r);
            }

            if (winner != null) return winner;

            ManifestFetchResult best = new ManifestFetchResult
            {
                Error = "all routes failed",
                ErrorCategory = "AllRoutesFailed"
            };
            foreach (ManifestFetchResult f in failures)
            {
                catalog.HealthOf(routes.Find(x => x.Name == f.RouteName)).RecordFailure(f.ErrorCategory);
                if (best.ErrorCategory == "AllRoutesFailed") { best.ErrorCategory = f.ErrorCategory; best.Error = f.Error; }
            }
            return best;
        }

        private sealed class FetchJob
        {
            public System.Threading.Tasks.Task<ManifestFetchResult> Task;
            public RouteEndpoint Route;
        }

        private static ManifestFetchResult FetchOne(RouteEndpoint route)
        {
            ManifestFetchResult r = new ManifestFetchResult { RouteName = route.Name };
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                double ttfb;
                byte[] manifest = HttpGetBytes(route.ManifestUrl, PrimaryTimeoutMs, out ttfb);
                r.TtfbMs = ttfb > 0 ? ttfb : sw.Elapsed.TotalMilliseconds;
                if (manifest == null || manifest.Length == 0)
                {
                    r.Error = "empty manifest";
                    r.ErrorCategory = "Empty";
                    return r;
                }

                // 清单与签名同目录同 base 名：manifest.json → manifest.sig
                string sigUrl = route.ManifestUrl;
                int dot = sigUrl.LastIndexOf('.');
                if (dot > sigUrl.LastIndexOf('/')) sigUrl = sigUrl.Substring(0, dot);
                sigUrl += ".sig";
                double sigTtfb;
                byte[] sig = HttpGetBytes(sigUrl, PrimaryTimeoutMs, out sigTtfb);
                if (sig == null || sig.Length == 0)
                {
                    r.Error = "missing manifest.sig";
                    r.ErrorCategory = "NoSignature";
                    return r;
                }

                r.ManifestBytes = manifest;
                r.Signature = sig;
                r.Ok = true;
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                r.ErrorCategory = RouteHealth.ClassifyError(ex);
                return r;
            }
        }

        /// <summary>小文件 GET（bytes 上限 256KB）；返回 ttfb 供评分。</summary>
        internal static byte[] HttpGetBytes(string url, int timeoutMs, out double ttfbMs)
        {
            ttfbMs = -1;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "KeyInsight-Updater";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            {
                List<byte> data = new List<byte>(4096);
                byte[] buffer = new byte[8192];
                Stopwatch sw = Stopwatch.StartNew();
                bool firstRead = true;
                while (true)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    if (firstRead)
                    {
                        ttfbMs = sw.Elapsed.TotalMilliseconds;
                        firstRead = false;
                    }
                    data.AddRange(new ArraySegment<byte>(buffer, 0, n));
                    if (data.Count > 256 * 1024) throw new InvalidOperationException("response too large");
                }
                return data.ToArray();
            }
        }
    }
}
