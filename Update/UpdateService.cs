using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace KeyInsight.Update
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string InstallerUrl;
        public string InstallerName;
        public string ChecksumUrl;
        public string ReleaseUrl;
        /// <summary>true = 远程无更新（本地版本已 >= 最新 release）。</summary>
        public bool UpToDate;
    }

    public static class UpdateService
    {
        public const string Owner = "CZM1998";
        public const string RepoName = "KeyInsight";
        private const string ApiUrl = "https://api.github.com/repos/CZM1998/KeyInsight/releases/latest";

        public static string CurrentVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
        }

        public static UpdateInfo Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApiUrl);
            request.UserAgent = "KeyInsight-Updater";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    return Parse(reader.ReadToEnd());
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
        }

        private static UpdateInfo Parse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
            string tag = root.ContainsKey("tag_name") ? root["tag_name"] as string : null;
            if (string.IsNullOrEmpty(tag)) return null;

            string versionText = tag.TrimStart('v', 'V');
            Version latest;
            if (!Version.TryParse(versionText, out latest)) return null;
            Version current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current != null && latest > current)
            {
                // 有更新：解析资产
            }
            else
            {
                // 本地版本 >= 远程：返回“本地更新”标记（开发版），UI 据此提示“已是最新/本地更新”
                UpdateInfo local = new UpdateInfo();
                local.Version = latest <= current ? current : latest;
                local.Tag = tag;
                local.ReleaseUrl = root.ContainsKey("html_url") ? root["html_url"] as string : null;
                local.UpToDate = true;
                return local;
            }

            UpdateInfo info = new UpdateInfo();
            info.Version = latest;
            info.Tag = tag;
            info.ReleaseUrl = root.ContainsKey("html_url") ? root["html_url"] as string : null;

            object assetsObject = root.ContainsKey("assets") ? root["assets"] : null;
            IEnumerable assets = assetsObject as IEnumerable;
            if (assets != null)
            {
                foreach (object asset in assets)
                {
                    Dictionary<string, object> item = asset as Dictionary<string, object>;
                    if (item == null) continue;
                    string name = item.ContainsKey("name") ? item["name"] as string : null;
                    string url = item.ContainsKey("browser_download_url") ? item["browser_download_url"] as string : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.StartsWith("KeyInsight-Setup-", StringComparison.OrdinalIgnoreCase))
                    {
                        info.InstallerName = name;
                        info.InstallerUrl = url;
                    }
                    else if (string.Equals(name, "sha256.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ChecksumUrl = url;
                    }
                }
            }

            if (string.IsNullOrEmpty(info.InstallerUrl)) return null;
            return info;
        }

        public static string Download(UpdateInfo info)
        {
            string path = Path.Combine(Path.GetTempPath(), info.InstallerName);
            using (WebClient client = new WebClient())
            {
                client.Headers.Add("User-Agent", "KeyInsight-Updater");
                client.DownloadFile(info.InstallerUrl, path);
            }

            if (!string.IsNullOrEmpty(info.ChecksumUrl))
            {
                string text;
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "KeyInsight-Updater");
                    text = client.DownloadString(info.ChecksumUrl);
                }

                string expected = FindHash(text, info.InstallerName);
                if (!string.IsNullOrEmpty(expected))
                {
                    string actual;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream stream = File.OpenRead(path))
                    {
                        actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    }
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(path); } catch { }
                        throw new InvalidOperationException("更新包 SHA256 校验失败");
                    }
                }
            }

            return path;
        }

        private static string FindHash(string text, string fileName)
        {
            string[] lines = text.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string hash = line.Substring(0, space).Trim();
                string name = line.Substring(space).Trim().TrimStart('*');
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash;
            }
            return null;
        }

        public static void RunInstaller(string path)
        {
            Process.Start(new ProcessStartInfo(path, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
            {
                UseShellExecute = true
            });
        }
    }
}
