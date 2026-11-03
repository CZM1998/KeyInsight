using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace KeyInsight.Update.Routing
{
    public enum RouteKind
    {
        Manifest = 0,
        Asset = 1,
        Both = 2
    }

    public enum RouteHealthState
    {
        Unknown = 0,
        Probing = 1,
        Healthy = 2,
        Degraded = 3,
        OpenCircuit = 4,
        Quarantined = 5
    }

    /// <summary>可信下载/元数据端点。URL 必须 HTTPS 且 Host 命中 allowlist。</summary>
    public sealed class RouteEndpoint
    {
        public string Name;
        public string ManifestUrl;          // manifest.json（元数据线路）
        public string AssetBaseUrl;         // 资产基础路径（大文件线路）
        public RouteKind Kind;
        public bool IsOfficialSource;       // GitHub 官方源（回源）

        public string HostFor(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            return u.Host;
        }
    }

    /// <summary>线路健康与评分（路线图 §6.2/§6.3）。线程安全。</summary>
    public sealed class RouteHealth
    {
        private const int HistoryCapacity = 20;

        public RouteHealthState State = RouteHealthState.Unknown;
        public readonly List<bool> SuccessHistory = new List<bool>();
        public double LastTtfbMs = -1;
        public double LastThroughputBytesPerSec = -1;
        public DateTime LastSuccessUtc = DateTime.MinValue;
        public DateTime CircuitOpenUntilUtc = DateTime.MinValue;
        public int ConsecutiveFailures;
        public string LastErrorCategory = "";

        public void RecordSuccess(double ttfbMs, double bytesPerSec)
        {
            lock (this)
            {
                SuccessHistory.Add(true);
                Trim();
                ConsecutiveFailures = 0;
                LastTtfbMs = ttfbMs;
                if (bytesPerSec > 0) LastThroughputBytesPerSec = bytesPerSec;
                LastSuccessUtc = DateTime.UtcNow;
                LastErrorCategory = "";
                if (State == RouteHealthState.OpenCircuit || State == RouteHealthState.Quarantined)
                {
                    State = RouteHealthState.Healthy;
                }
                else if (State == RouteHealthState.Unknown || State == RouteHealthState.Probing)
                {
                    State = RouteHealthState.Healthy;
                }
                else if (State == RouteHealthState.Degraded)
                {
                    State = RouteHealthState.Healthy;
                }
            }
        }

        public void RecordFailure(string errorCategory)
        {
            lock (this)
            {
                SuccessHistory.Add(false);
                Trim();
                ConsecutiveFailures++;
                LastErrorCategory = errorCategory ?? "";
                // 熔断：连续 3 次失败打开 5 分钟；隔离：连续 6 次失败 15 分钟
                if (ConsecutiveFailures >= 6)
                {
                    State = RouteHealthState.Quarantined;
                    CircuitOpenUntilUtc = DateTime.UtcNow.AddMinutes(15);
                }
                else if (ConsecutiveFailures >= 3)
                {
                    State = RouteHealthState.OpenCircuit;
                    CircuitOpenUntilUtc = DateTime.UtcNow.AddMinutes(5);
                }
                else if (State == RouteHealthState.Healthy || State == RouteHealthState.Unknown)
                {
                    State = RouteHealthState.Degraded;
                }
            }
        }

        /// <summary>线路当前是否可用（熔断过期自动半开）。</summary>
        public bool IsAvailable()
        {
            lock (this)
            {
                if (State == RouteHealthState.Quarantined || State == RouteHealthState.OpenCircuit)
                {
                    if (DateTime.UtcNow >= CircuitOpenUntilUtc)
                    {
                        State = RouteHealthState.Probing; // 半开：允许试探
                        return true;
                    }
                    return false;
                }
                return true;
            }
        }

        public static string ClassifyError(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    if (code == 429) return "HTTP429";
                    if (code == 404) return "HTTP404";
                    if (code == 401 || code == 403) return "HTTP" + code;
                    if (code >= 500) return "HTTP5xx";
                    return "HTTP" + code;
                }
                switch (we.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: return "DNS";
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.ConnectionClosed: return "Connect";
                    case WebExceptionStatus.SecureChannelFailure: return "TLS";
                    case WebExceptionStatus.Timeout: return "Timeout";
                    default: return we.Status.ToString();
                }
            }
            if (ex is System.Security.Cryptography.CryptographicException) return "Signature";
            if (ex is InvalidOperationException) return "Signature";
            if (ex is UnauthorizedAccessException) return "DiskAccess";
            if (ex is IOException) return "IO";
            return "Other";
        }

        private void Trim()
        {
            while (SuccessHistory.Count > HistoryCapacity) SuccessHistory.RemoveAt(0);
        }
    }

    /// <summary>路线评分（§6.3 公式）。值域 0..1。</summary>
    public static class RouteScorer
    {
        public static double Score(RouteHealth h)
        {
            if (h == null) return 0;
            double success = SuccessRate(h);
            double throughput = Norm(h.LastThroughputBytesPerSec, 0, 8 * 1024 * 1024);
            double ttfb = 1.0 - Norm(h.LastTtfbMs, 0, 3000);
            double reliability = RecentReliability(h);
            return 0.35 * success + 0.30 * Math.Max(0, throughput) + 0.20 * Math.Max(0, ttfb) + 0.15 * reliability;
        }

        private static double SuccessRate(RouteHealth h)
        {
            lock (h)
            {
                if (h.SuccessHistory.Count == 0) return 0.5; // Unknown 线路给中间分，保留探索机会
                int ok = 0;
                foreach (bool b in h.SuccessHistory) if (b) ok++;
                return ok / (double)h.SuccessHistory.Count;
            }
        }

        private static double RecentReliability(RouteHealth h)
        {
            lock (h)
            {
                double penalty = h.ConsecutiveFailures * 0.25;
                if (h.State == RouteHealthState.OpenCircuit) penalty += 0.5;
                if (h.State == RouteHealthState.Quarantined) penalty += 1.0;
                return Math.Max(0, 1.0 - penalty);
            }
        }

        private static double Norm(double value, double min, double max)
        {
            if (value < min) return 0;
            if (value > max) return 1;
            return (value - min) / (max - min);
        }
    }

    /// <summary>
    /// 路线目录（PR-5）：内置 bootstrap 路线 + 持久化健康统计。
    /// Host allowlist：所有 URL 的 Host 必须命中此表（文档 §7.2）。
    /// </summary>
    public sealed class RouteCatalog
    {
        /// <summary>可信 Host allowlist（全部 HTTPS）。用户配置的自有 CDN 域名可经配置追加。</summary>
        public static readonly string[] AllowedHosts = new string[]
        {
            "raw.githubusercontent.com",
            "github.com",
            "objects.githubusercontent.com",
            "api.github.com",
            "release-assets.githubusercontent.com"
        };

        private readonly List<RouteEndpoint> _routes = new List<RouteEndpoint>();
        private readonly Dictionary<string, RouteHealth> _health = new Dictionary<string, RouteHealth>();
        private readonly string _statePath;

        public RouteCatalog(string statePath)
        {
            _statePath = statePath;
            AddBootstrapRoutes();
            LoadHealth();
        }

        private void AddBootstrapRoutes()
        {
            // 内置 bootstrap 路线（路线图附录 B：客户端保留内置路线，避免远端移除所有有效路线）。
            // 自有域名/OSS 镜像上线后在此追加或通过受签名清单下发。
            _routes.Add(new RouteEndpoint
            {
                Name = "github-raw",
                ManifestUrl = "https://raw.githubusercontent.com/CZM1998/KeyInsight/master/update/manifest.json",
                AssetBaseUrl = "https://github.com/CZM1998/KeyInsight/releases/download/",
                Kind = RouteKind.Both,
                IsOfficialSource = true
            });
        }

        public List<RouteEndpoint> Routes
        {
            get { return new List<RouteEndpoint>(_routes); }
        }

        /// <summary>追加用户配置的镜像路线（仍须 HTTPS + allowlist + 清单验签）。</summary>
        public bool AddRoute(string name, string manifestUrl, string assetBaseUrl)
        {
            Uri u;
            if (string.IsNullOrEmpty(manifestUrl) || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out u)
                || u.Scheme != Uri.UriSchemeHttps || !IsHostAllowed(u.Host)) return false;
            if (!string.IsNullOrEmpty(assetBaseUrl))
            {
                Uri a;
                if (!Uri.TryCreate(assetBaseUrl, UriKind.Absolute, out a)
                    || a.Scheme != Uri.UriSchemeHttps || !IsHostAllowed(a.Host)) return false;
            }
            _routes.Add(new RouteEndpoint { Name = name, ManifestUrl = manifestUrl, AssetBaseUrl = assetBaseUrl, Kind = RouteKind.Both });
            return true;
        }

        public static bool IsHostAllowed(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant();
            for (int i = 0; i < AllowedHosts.Length; i++)
            {
                if (h == AllowedHosts[i]) return true;
            }
            return false;
        }

        public RouteHealth HealthOf(RouteEndpoint route)
        {
            lock (_health)
            {
                RouteHealth h;
                if (!_health.TryGetValue(route.Name, out h))
                {
                    h = new RouteHealth();
                    _health[route.Name] = h;
                }
                return h;
            }
        }

        /// <summary>按评分降序返回当前可用路线（熔断/隔离路线排除或半开）。</summary>
        public List<RouteEndpoint> OrderedAvailableRoutes()
        {
            List<RouteEndpoint> available = new List<RouteEndpoint>();
            foreach (RouteEndpoint r in _routes)
            {
                if (HealthOf(r).IsAvailable()) available.Add(r);
            }
            available.Sort(delegate(RouteEndpoint a, RouteEndpoint b)
            {
                double sa = RouteScorer.Score(HealthOf(a));
                double sb = RouteScorer.Score(HealthOf(b));
                // 官方源同分优先（权威）
                if (Math.Abs(sa - sb) < 0.001)
                {
                    if (a.IsOfficialSource != b.IsOfficialSource) return a.IsOfficialSource ? -1 : 1;
                }
                return sb.CompareTo(sa);
            });
            return available;
        }

        public void SaveHealth()
        {
            try
            {
                if (string.IsNullOrEmpty(_statePath)) return;
                string dir = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                lock (_health)
                {
                    sb.Append("{\"schema\":1,\"savedUtc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"routes\":[");
                    bool first = true;
                    foreach (KeyValuePair<string, RouteHealth> kv in _health)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        RouteHealth h = kv.Value;
                        sb.Append("{\"name\":\"").Append(Escape(kv.Key))
                          .Append("\",\"state\":\"").Append(h.State)
                          .Append("\",\"ttfbMs\":").Append(h.LastTtfbMs.ToString("F0"))
                          .Append(",\"bps\":").Append(h.LastThroughputBytesPerSec.ToString("F0"))
                          .Append(",\"fails\":").Append(h.ConsecutiveFailures)
                          .Append('}');
                    }
                    sb.Append("]}");
                }
                string tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
                if (File.Exists(_statePath)) File.Replace(tmp, _statePath, null);
                else File.Move(tmp, _statePath);
            }
            catch
            {
            }
        }

        private void LoadHealth()
        {
            try
            {
                if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return;
                string text = File.ReadAllText(_statePath);
                // 简化恢复：仅恢复熔断状态与失败计数（分数冷启动重新积累）
                if (text.IndexOf("\"state\":\"OpenCircuit\"", StringComparison.Ordinal) < 0 &&
                    text.IndexOf("\"state\":\"Quarantined\"", StringComparison.Ordinal) < 0) return;
                // 详细解析成本高于收益：熔断窗口 5-15 分钟，跨重启价值有限，跳过恢复。
            }
            catch
            {
            }
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
