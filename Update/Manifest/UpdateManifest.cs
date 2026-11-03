using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KeyInsight.Update.Manifest
{
    /// <summary>
    /// 签名更新清单 DTO（路线图 §5.2）。签名覆盖 manifest 原始 UTF-8 字节，
    /// 本类只负责解析与校验规则，不涉及 JSON 重排。
    /// </summary>
    public sealed class UpdateManifest
    {
        public int Schema;
        public string Product;
        public string Channel;
        public Version Version;
        public string VersionText;
        public DateTime PublishedAtUtc;
        public string MinOs;
        public long RolloutPercent = 100;
        public string KeyId;
        public List<ManifestAsset> Assets = new List<ManifestAsset>();

        /// <summary>最低可升级版本（低于该版本才允许升级；为 null = 不限制）。</summary>
        public Version MinUpgradeFrom;

        /// <summary>被撤回的版本列表（客户端不得安装/保持这些版本）。</summary>
        public List<string> RevokedVersions = new List<string>();

        public static bool TryParse(byte[] rawBytes, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            error = null;
            if (rawBytes == null || rawBytes.Length == 0 || rawBytes.Length > 256 * 1024)
            {
                error = "manifest size invalid";
                return false;
            }
            try
            {
                string json = Encoding.UTF8.GetString(rawBytes);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                IDictionary root = ser.DeserializeObject(json) as IDictionary;
                if (root == null) { error = "root not object"; return false; }

                UpdateManifest m = new UpdateManifest();
                m.Schema = root.Contains("schema") ? Convert.ToInt32(root["schema"]) : 0;
                if (m.Schema != 1) { error = "schema " + m.Schema + " unsupported"; return false; }

                m.Product = GetString(root, "product");
                if (m.Product != "KeyInsight") { error = "product mismatch"; return false; }

                m.Channel = GetString(root, "channel") ?? "stable";
                m.VersionText = GetString(root, "version");
                if (!Version.TryParse(m.VersionText, out m.Version) || m.Version.Revision == -1)
                {
                    // 允许三段版本号
                    if (!Version.TryParse(m.VersionText + ".0", out m.Version))
                    {
                        error = "version invalid";
                        return false;
                    }
                }

                string published = GetString(root, "publishedAt");
                if (published != null && !DateTime.TryParse(published, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out m.PublishedAtUtc))
                {
                    m.PublishedAtUtc = DateTime.UtcNow;
                }

                m.MinOs = GetString(root, "minOs");
                if (root.Contains("rolloutPercent")) m.RolloutPercent = Convert.ToInt64(root["rolloutPercent"]);
                m.KeyId = GetString(root, "keyId") ?? "";

                Version minUp;
                if (root.Contains("minUpgradeFrom") && Version.TryParse(Convert.ToString(root["minUpgradeFrom"]), out minUp))
                {
                    m.MinUpgradeFrom = minUp;
                }
                if (root.Contains("revoked") && root["revoked"] is IEnumerable)
                {
                    foreach (object rv in (IEnumerable)root["revoked"])
                    {
                        m.RevokedVersions.Add(Convert.ToString(rv));
                    }
                }

                if (root.Contains("assets") && root["assets"] is IEnumerable)
                {
                    foreach (object item in (IEnumerable)root["assets"])
                    {
                        IDictionary a = item as IDictionary;
                        if (a == null) continue;
                        ManifestAsset asset = new ManifestAsset();
                        asset.Id = GetString(a, "id");
                        asset.Kind = GetString(a, "kind") ?? "setup";
                        asset.FileName = GetString(a, "fileName");
                        asset.Sha256 = (GetString(a, "sha256") ?? "").ToLowerInvariant();
                        if (a.Contains("size")) asset.Size = Convert.ToInt64(a["size"]);
                        if (a["urls"] is IEnumerable)
                        {
                            foreach (object u in (IEnumerable)a["urls"])
                            {
                                string url = Convert.ToString(u);
                                if (!string.IsNullOrEmpty(url)) asset.Urls.Add(url);
                            }
                        }
                        if (asset.Id != null && asset.FileName != null && asset.Sha256.Length == 64)
                        {
                            m.Assets.Add(asset);
                        }
                    }
                }

                if (m.Assets.Count == 0) { error = "no valid assets"; return false; }
                manifest = m;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetString(IDictionary o, string key)
        {
            return o.Contains(key) ? Convert.ToString(o[key]) : null;
        }

        public ManifestAsset FindAsset(string id)
        {
            for (int i = 0; i < Assets.Count; i++)
            {
                if (string.Equals(Assets[i].Id, id, StringComparison.OrdinalIgnoreCase)) return Assets[i];
            }
            return null;
        }
    }

    public sealed class ManifestAsset
    {
        public string Id;
        public string Kind;
        public string FileName;
        public long Size;
        public string Sha256;
        public List<string> Urls = new List<string>();
    }
}
