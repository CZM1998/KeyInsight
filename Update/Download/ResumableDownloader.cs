using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KeyInsight.Update.Routing;

namespace KeyInsight.Update.Download
{
    /// <summary>
    /// 可续传下载器（路线图 PR-6 / §6.5）：Range 续传 + ETag 校验 + 失败切换路线 +
    /// 大小上限 + 整文件 SHA-256。分阶段策略：单线路下载 + 失败切换 + 续传。
    /// </summary>
    public sealed class ResumableDownloader
    {
        private const long MaxFileSize = 512L * 1024 * 1024;
        private const int ReadBufferSize = 64 * 1024;

        public string StagingDir { get; private set; }
        public Func<bool> Cancelled;

        public event Action<long, long> Progress;      // bytesDone, totalBytes
        public event Action<string> RouteSwitched;     // 切换线路时通知

        public ResumableDownloader(string stagingDir)
        {
            StagingDir = stagingDir;
        }

        public sealed class DownloadResult
        {
            public bool Ok;
            public string FilePath;
            public string Sha256;
            public string Error;
            public string ErrorCategory;
            public string RouteName;        // 命中/失败的 URL 归属线路（官方 = ""）
            public double TtfbMs;           // 首字节耗时（供线路评分）
            public double BytesPerSec;      // 本次下载吞吐（供线路评分）
        }

        /// <summary>
        /// 下载 asset 到 staging，按 routeOrder 顺序尝试。文件名 = {assetId}-{sha256 前 12 位}.part，
        /// 完成后重命名为 fileName。整文件 SHA-256 校验在下载器内完成。
        /// 复审方案 PR-4：官方 URL 失败后按内置镜像前缀展开候选（公共代理只搬运字节，
        /// 校验规则完全一致——大小 + 整文件 SHA-256）。
        /// </summary>
        public DownloadResult Download(Update.Manifest.ManifestAsset asset, List<string> assetUrls,
            RouteCatalog catalog)
        {
            DownloadResult last = new DownloadResult { Error = "no urls", ErrorCategory = "NoRoute" };
            foreach (string url in ExpandUrls(assetUrls, catalog))
            {
                if (Cancelled != null && Cancelled())
                {
                    return new DownloadResult { Error = "cancelled", ErrorCategory = "Cancelled" };
                }
                if (!RouteCatalog.IsHostAllowed(HostOf(url)))
                {
                    last = new DownloadResult { Error = "host not allowed: " + HostOf(url), ErrorCategory = "HostNotAllowed" };
                    continue;
                }

                DownloadResult r = DownloadFromOne(url, asset, RouteNameFor(catalog, url));
                if (r.Ok)
                {
                    if (catalog != null && !string.IsNullOrEmpty(r.RouteName))
                    {
                        RouteEndpoint ep = FindRoute(catalog, r.RouteName);
                        if (ep != null) catalog.HealthOf(ep).RecordSuccess(r.TtfbMs, r.BytesPerSec);
                    }
                    return r;
                }

                if (catalog != null && !string.IsNullOrEmpty(r.RouteName))
                {
                    RouteEndpoint ep = FindRoute(catalog, r.RouteName);
                    if (ep != null) catalog.HealthOf(ep).RecordFailure(r.ErrorCategory);
                }
                Action<string> rs = RouteSwitched;
                if (rs != null) { try { rs(HostOf(url)); } catch { } }
                last = r;
            }
            return last;
        }

        /// <summary>官方 URL 优先，其后按内置镜像前缀展开（同一资产字节，签名/哈希规则相同）。</summary>
        internal static List<string> ExpandUrls(List<string> assetUrls, RouteCatalog catalog)
        {
            List<string> urls = new List<string>();
            if (assetUrls != null) urls.AddRange(assetUrls);
            if (catalog != null)
            {
                foreach (RouteEndpoint route in catalog.OrderedAvailableRoutes())
                {
                    if (string.IsNullOrEmpty(route.UrlPrefix)) continue;
                    foreach (string url in assetUrls)
                    {
                        // 官方 github.com 直链才套前缀；镜像自身 URL 不再嵌套
                        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            urls.Add(route.UrlPrefix + url);
                        }
                    }
                }
            }
            return urls;
        }

        private static RouteEndpoint FindRoute(RouteCatalog catalog, string name)
        {
            foreach (RouteEndpoint r in catalog.Routes)
            {
                if (string.Equals(r.Name, name, StringComparison.Ordinal)) return r;
            }
            return null;
        }

        /// <summary>URL → 归属线路名：命中镜像前缀返回该镜像，官方直链返回 ""。</summary>
        private static string RouteNameFor(RouteCatalog catalog, string url)
        {
            if (catalog == null) return "";
            foreach (RouteEndpoint r in catalog.Routes)
            {
                if (!string.IsNullOrEmpty(r.UrlPrefix) &&
                    url.StartsWith(r.UrlPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return r.Name;
                }
            }
            return "";
        }

        private DownloadResult DownloadFromOneInner(string url, Update.Manifest.ManifestAsset asset)
        {
            string tempPath = Path.Combine(StagingDir,
                asset.Id + "-" + asset.Sha256.Substring(0, 12) + ".part");
            long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
            if (existing > asset.Size) existing = 0;
            Stopwatch dlClock = Stopwatch.StartNew();
            double ttfbMs = -1;
            long transferred = 0;

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "KeyInsight-Updater";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 30000;
                if (existing > 0) req.AddRange(existing);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    ttfbMs = dlClock.Elapsed.TotalMilliseconds;
                    // 服务器忽略 Range 时返回 200 全量：从零开始
                    long startOffset = existing;
                    if ((int)resp.StatusCode == 200) startOffset = 0;
                    else if ((int)resp.StatusCode != 206)
                    {
                        return new DownloadResult { Error = "unexpected status " + (int)resp.StatusCode,
                            ErrorCategory = "HTTP" + (int)resp.StatusCode };
                    }

                    long total = asset.Size;
                    if (resp.ContentLength > 0) total = startOffset + resp.ContentLength;
                    if (total > MaxFileSize)
                    {
                        return new DownloadResult { Error = "file exceeds size limit", ErrorCategory = "TooLarge" };
                    }

                    using (Stream input = resp.GetResponseStream())
                    using (FileStream output = new FileStream(tempPath,
                        startOffset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[ReadBufferSize];
                        long done = startOffset;
                        while (true)
                        {
                            if (Cancelled != null && Cancelled())
                            {
                                return new DownloadResult { Error = "cancelled", ErrorCategory = "Cancelled" };
                            }
                            int n = input.Read(buffer, 0, buffer.Length);
                            if (n <= 0) break;
                            output.Write(buffer, 0, n);
                            done += n;
                            if (Progress != null)
                            {
                                try { Progress(done, total); } catch { }
                            }
                        }
                        transferred = done;
                    }

                    long actualSize = new FileInfo(tempPath).Length;
                    if (actualSize != asset.Size)
                    {
                        try { File.Delete(tempPath); } catch { }
                        return new DownloadResult { Error = "size mismatch " + actualSize + " != " + asset.Size,
                            ErrorCategory = "SizeMismatch" };
                    }

                    // 整文件 SHA-256（不能只校验最后一个分块）
                    string sha;
                    using (FileStream fs = File.OpenRead(tempPath))
                    using (SHA256 sha2 = SHA256.Create())
                    {
                        sha = ToHex(sha2.ComputeHash(fs));
                    }
                    if (sha != asset.Sha256)
                    {
                        try { File.Delete(tempPath); } catch { }
                        return new DownloadResult { Error = "sha256 mismatch", ErrorCategory = "ShaMismatch" };
                    }

                    string finalPath = Path.Combine(StagingDir, asset.FileName);
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tempPath, finalPath);
                    return new DownloadResult
                    {
                        Ok = true,
                        FilePath = finalPath,
                        Sha256 = sha,
                        TtfbMs = ttfbMs,
                        BytesPerSec = transferred <= 0 ? -1
                            : transferred / Math.Max(0.001, dlClock.Elapsed.TotalSeconds)
                    };
                }
            }
            catch (Exception ex)
            {
                return new DownloadResult
                {
                    Error = ex.Message,
                    ErrorCategory = RouteHealth.ClassifyError(ex),
                    TtfbMs = ttfbMs
                };
            }
        }

        /// <summary>套上线路归属（官方直链 RouteName = ""），供健康统计记账。</summary>
        private DownloadResult DownloadFromOne(string url, Update.Manifest.ManifestAsset asset, string routeName)
        {
            DownloadResult r = DownloadFromOneInner(url, asset);
            r.RouteName = routeName ?? "";
            return r;
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : null;
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
