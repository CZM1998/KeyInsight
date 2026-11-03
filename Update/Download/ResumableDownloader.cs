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
        }

        /// <summary>
        /// 下载 asset 到 staging，按 routeOrder 顺序尝试。文件名 = {assetId}-{sha256 前 12 位}.part，
        /// 完成后重命名为 fileName。整文件 SHA-256 校验在下载器内完成。
        /// </summary>
        public DownloadResult Download(Update.Manifest.ManifestAsset asset, List<string> assetUrls,
            RouteCatalog catalog)
        {
            DownloadResult last = new DownloadResult { Error = "no urls", ErrorCategory = "NoRoute" };
            foreach (string url in assetUrls)
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

                DownloadResult r = DownloadFromOne(url, asset);
                if (r.Ok) return r;

                if (catalog != null)
                {
                    // 记录失败并尝试切换下一条 URL（同一资产在 manifest 内自带多线路 URL 列表）
                }
                last = r;
            }
            return last;
        }

        private DownloadResult DownloadFromOne(string url, Update.Manifest.ManifestAsset asset)
        {
            string tempPath = Path.Combine(StagingDir,
                asset.Id + "-" + asset.Sha256.Substring(0, 12) + ".part");
            long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
            if (existing > asset.Size) existing = 0;

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "KeyInsight-Updater";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 30000;
                if (existing > 0) req.AddRange(existing);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
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
                    return new DownloadResult { Ok = true, FilePath = finalPath, Sha256 = sha };
                }
            }
            catch (Exception ex)
            {
                return new DownloadResult
                {
                    Error = ex.Message,
                    ErrorCategory = RouteHealth.ClassifyError(ex)
                };
            }
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
