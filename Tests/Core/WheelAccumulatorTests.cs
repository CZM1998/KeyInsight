using System;
using KeyInsight.Core.Input;
using KeyInsight.Update.Download;
using KeyInsight.Update.Routing;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// 复审方案 PR-1/PR-4 验收：
    /// - WHEEL：高分辨率滚轮累计器（余数保留、满 120 出格、方向抵消）。
    /// - ROUTE：固定名 Release 清单路线池、镜像 URL 展开、熔断/恢复状态机。
    /// </summary>
    internal static class WheelAccumulatorTests
    {
        public static void Register()
        {
            TestRegistry.Add("Core", "WHEEL001_HighResSubStepsAccumulate", WHEEL001);
            TestRegistry.Add("Core", "WHEEL002_FullDetentImmediate", WHEEL002);
            TestRegistry.Add("Core", "WHEEL003_DirectionCancelResidual", WHEEL003);
            TestRegistry.Add("Core", "WHEEL004_LargeDeltaMultiTick", WHEEL004);
            TestRegistry.Add("Core", "ROUTE001_BootstrapPool", ROUTE001);
            TestRegistry.Add("Core", "ROUTE002_MirrorUrlExpansion", ROUTE002);
            TestRegistry.Add("Core", "ROUTE003_CircuitBreakerCycle", ROUTE003);
            TestRegistry.Add("Core", "ROUTE004_OfficialTiebreakPreferred", ROUTE004);
        }

        private static void WHEEL001()
        {
            WheelAccumulator acc = new WheelAccumulator();
            // 高分辨率滚轮：每格拆成 3 个 delta=40 的事件；前两步不出刻度，第三步出 1
            Check.Equal(0L, acc.Feed(40), "first sub-step should not tick");
            Check.Equal(0L, acc.Feed(40), "second sub-step should not tick");
            Check.Equal(1L, acc.Feed(40), "third sub-step completes one detent");
            Check.Equal(0L, acc.Residual, "residual should be zero after exact detent");
        }

        private static void WHEEL002()
        {
            WheelAccumulator acc = new WheelAccumulator();
            // 常规 120 格滚轮：每事件立即 1 格
            Check.Equal(1L, acc.Feed(120), "standard wheel ticks immediately");
            Check.Equal(1L, acc.Feed(120), "second detent ticks immediately");
            Check.Equal(0L, acc.Residual, "no residual for standard wheel");
        }

        private static void WHEEL003()
        {
            WheelAccumulator acc = new WheelAccumulator();
            Check.Equal(0L, acc.Feed(60), "half detent pending");
            // 反向滚动：60 上 + 120 下 = 净 -60，仍未满一格 → 0 刻度，余数 -60 保留
            Check.Equal(0L, acc.Feed(-120), "net -60 stays below one detent");
            Check.Equal(-60L, acc.Residual, "residual keeps remainder after crossing zero");
            Check.Equal(-1L, acc.Feed(-60), "next -60 completes the downward detent");
            Check.Equal(0L, acc.Residual, "residual drained");
        }

        private static void WHEEL004()
        {
            WheelAccumulator acc = new WheelAccumulator();
            // 惯性滚动单事件大 delta：一次出多格，余数保留
            Check.Equal(2L, acc.Feed(240), "240 -> two detents");
            Check.Equal(3L, acc.Feed(380), "380 -> three detents, remainder 20");
            Check.Equal(20L, acc.Residual, "remainder preserved");
        }

        private static void ROUTE001()
        {
            RouteCatalog catalog = new RouteCatalog(null);
            var routes = catalog.Routes;
            Check.True(routes.Count >= 6, "official + 4 mirrors + raw fallback expected, got " + routes.Count);
            bool hasOfficial = false, hasRaw = false;
            foreach (var r in routes)
            {
                Check.True(r.ManifestUrl.StartsWith("https://", StringComparison.Ordinal),
                    "all routes must be HTTPS: " + r.Name);
                if (r.Name == "release-official") hasOfficial = true;
                if (r.Name == "github-raw") hasRaw = true;
                if (r.UrlPrefix != null)
                {
                    Check.True(r.ManifestUrl.StartsWith(r.UrlPrefix, StringComparison.Ordinal),
                        "mirror manifest must be prefixed official URL: " + r.Name);
                }
            }
            Check.True(hasOfficial, "fixed-name release manifest route missing");
            Check.True(hasRaw, "raw fallback route missing");
            Check.True(routes[0].ManifestUrl.EndsWith("KeyInsight-update.json", StringComparison.Ordinal),
                "official route must use the fixed-name release asset");
        }

        private static void ROUTE002()
        {
            RouteCatalog catalog = new RouteCatalog(null);
            var urls = new System.Collections.Generic.List<string>();
            urls.Add("https://github.com/CZM1998/KeyInsight/releases/download/v9.9.9/KeyInsight-Setup-9.9.9.exe");
            urls.Add("https://ghfast.top/https://github.com/CZM1998/KeyInsight/releases/download/v9.9.9/x.exe");
            var expanded = ResumableDownloader.ExpandUrls(urls, catalog);

            Check.True(expanded.Count > urls.Count, "mirror candidates should be appended");
            Check.Equal(urls[0], expanded[0], "official URL must stay first");
            // 每个镜像前缀都应产生一个「前缀+官方URL」候选；输入里的镜像 URL 原样保留、不再嵌套
            string expectedFast = "https://ghfast.top/" + urls[0];
            Check.True(expanded.Contains(expectedFast),
                "official URL must expand under the ghfast prefix");
            int nested = 0;
            foreach (var u in expanded)
            {
                if (u.StartsWith("https://ghfast.top/https://ghfast.top/", StringComparison.Ordinal)) nested++;
            }
            Check.Equal(0L, nested, "mirror URLs must never nest twice");
            foreach (var u in expanded)
            {
                Check.True(RouteCatalog.IsHostAllowed(new System.Uri(u).Host),
                    "expanded URL host must be allowlisted: " + u);
            }
        }

        private static void ROUTE003()
        {
            RouteCatalog catalog = new RouteCatalog(null);
            RouteEndpoint mirror = null;
            foreach (var r in catalog.Routes)
            {
                if (r.Name == "mirror-ghfast") { mirror = r; break; }
            }
            Check.True(mirror != null, "mirror-ghfast route missing");
            RouteHealth h = catalog.HealthOf(mirror);

            Check.True(h.IsAvailable(), "unknown route starts available");
            h.RecordFailure("HTTP404");
            h.RecordFailure("HTTP429");
            Check.True(h.IsAvailable(), "two failures stay available (degraded)");
            Check.Equal("Degraded", h.State.ToString(), "two failures = degraded");
            h.RecordFailure("Timeout");
            Check.Equal("OpenCircuit", h.State.ToString(), "three failures open the circuit");
            Check.True(!h.IsAvailable(), "open circuit rejects traffic");
            // 熔断过期由时间驱动（5 分钟），这里只验证成功会复位
            h.RecordSuccess(120, 1024 * 1024);
            Check.Equal("Healthy", h.State.ToString(), "success heals the route");
            Check.True(h.IsAvailable(), "healthy route available again");
        }

        private static void ROUTE004()
        {
            RouteCatalog catalog = new RouteCatalog(null);
            var ordered = catalog.OrderedAvailableRoutes();
            Check.True(ordered.Count > 0, "at least one route available");
            // 全部 Unknown 时官方源应排在最前（同分权威优先）
            Check.True(ordered[0].IsOfficialSource, "official source must be preferred on ties");
        }
    }
}
