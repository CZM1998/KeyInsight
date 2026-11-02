using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace KeyInsight
{
    /// <summary>
    /// 版本化配置（设计文档 §6.3，data/config/KeyInsight.v2.json）。
    /// - 读取：v2 存在且 schema 可识别 → 以其为准；否则回退旧 ini（Load 侧处理）；
    /// - 写入：Config.Save 同时写 ini（回滚兼容）与本文件（schema=1）；
    /// - 未知字段保持容忍（忽略未知键，不清空）；未来 schema 保守拒绝解析并保留文件。
    /// </summary>
    public static class ConfigV2
    {
        public const int SchemaVersion = 1;

        public static string GetPath()
        {
            try
            {
                return Path.Combine(Stats.DataPathResolver.ResolveConfigDir(
                    AppDomain.CurrentDomain.BaseDirectory), "KeyInsight.v2.json");
            }
            catch
            {
                return "";
            }
        }

        public static void SaveTo(string path, Config c)
        {
            if (string.IsNullOrEmpty(path)) return;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaVersion;
            root["savedUtcTicks"] = DateTime.UtcNow.Ticks;
            root["thresholdMs"] = c.ThresholdMs;
            root["enabled"] = c.FilterEnabled;              // 旧键名保留，便于跨版本理解
            root["filterEnabled"] = c.FilterEnabled;
            root["captureEnabled"] = c.CaptureEnabled;
            root["diagnosticsEnabled"] = c.DiagnosticsEnabled;
            root["floatingSummary"] = c.FloatingSummary;
            root["floatingSummaryX"] = c.FloatingSummaryX;
            root["floatingSummaryY"] = c.FloatingSummaryY;
            root["policyMode"] = c.PolicyMode;
            root["filterAppWhitelist"] = c.FilterAppWhitelist ?? "";
            root["skipInjected"] = c.SkipInjected;
            root["logSwallowed"] = c.LogSwallowed;
            root["logAllKeys"] = c.LogAllKeys;
            root["themeMode"] = c.ThemeMode;
            root["keyboardLayout"] = c.KeyboardLayout;
            root["autoCheckUpdates"] = c.AutoCheckUpdates;
            root["lastUpdateCheck"] = c.LastUpdateCheckTicks;

            string json = new JavaScriptSerializer().Serialize(root);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch
                {
                    try { File.Copy(tmp, path, true); File.Delete(tmp); }
                    catch { }
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>读取 v2 配置到目标对象；结构不可识别返回 false（调用方回退 ini）。</summary>
        public static bool TryRead(string path, Config target)
        {
            try
            {
                string json = File.ReadAllText(path);
                IDictionary root = new JavaScriptSerializer().DeserializeObject(json) as IDictionary;
                if (root == null) return false;
                if (root.Contains("schema") && Convert.ToInt32(root["schema"]) > SchemaVersion)
                {
                    return false; // 未来版本：保守不解析
                }
                if (root.Contains("thresholdMs")) target.ThresholdMs = Clamp(Convert.ToInt32(root["thresholdMs"]), 1, 100);
                if (root.Contains("filterEnabled")) target.FilterEnabled = Convert.ToBoolean(root["filterEnabled"]);
                else if (root.Contains("enabled")) target.FilterEnabled = Convert.ToBoolean(root["enabled"]);
                if (root.Contains("captureEnabled")) target.CaptureEnabled = Convert.ToBoolean(root["captureEnabled"]);
                if (root.Contains("diagnosticsEnabled")) target.DiagnosticsEnabled = Convert.ToBoolean(root["diagnosticsEnabled"]);
                if (root.Contains("floatingSummary")) target.FloatingSummary = Convert.ToBoolean(root["floatingSummary"]);
                if (root.Contains("floatingSummaryX")) target.FloatingSummaryX = Convert.ToInt32(root["floatingSummaryX"]);
                if (root.Contains("floatingSummaryY")) target.FloatingSummaryY = Convert.ToInt32(root["floatingSummaryY"]);
                if (root.Contains("policyMode")) target.PolicyMode = Clamp(Convert.ToInt32(root["policyMode"]), 0, 2);
                if (root.Contains("filterAppWhitelist")) target.FilterAppWhitelist = Convert.ToString(root["filterAppWhitelist"]);
                if (root.Contains("skipInjected")) target.SkipInjected = Convert.ToBoolean(root["skipInjected"]);
                if (root.Contains("logSwallowed")) target.LogSwallowed = Convert.ToBoolean(root["logSwallowed"]);
                if (root.Contains("logAllKeys")) target.LogAllKeys = Convert.ToBoolean(root["logAllKeys"]);
                if (root.Contains("themeMode")) target.ThemeMode = Clamp(Convert.ToInt32(root["themeMode"]), 0, 2);
                if (root.Contains("keyboardLayout")) target.KeyboardLayout = Clamp(Convert.ToInt32(root["keyboardLayout"]), 0, 3);
                if (root.Contains("autoCheckUpdates")) target.AutoCheckUpdates = Convert.ToBoolean(root["autoCheckUpdates"]);
                if (root.Contains("lastUpdateCheck")) target.LastUpdateCheckTicks = Convert.ToInt64(root["lastUpdateCheck"]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
