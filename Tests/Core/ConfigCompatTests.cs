using System;
using System.IO;
using KeyInsight;

namespace KeyInsight.Tests.Core
{
    /// <summary>
    /// P0-02 配置兼容（STO-008）：旧 ini 键 enabled= 继续读取；新增 filterenabled=/captureenabled=；
    /// Save 同时写旧键（用户回滚 v2.1.x 后设置不丢）。
    /// </summary>
    internal static class ConfigCompatTests
    {
        public static void Register()
        {
            TestRegistry.Add("Core", "Config_OldEnabledKeyRead", Config_OldEnabledKeyRead);
            TestRegistry.Add("Core", "Config_NewKeysRead", Config_NewKeysRead);
            TestRegistry.Add("Core", "Config_LegacyIniMode", Config_LegacyIniMode);
            TestRegistry.Add("Core", "Config_SaveRoundTripWritesBothKeys", Config_SaveRoundTripWritesBothKeys);
        }

        private static string WriteTempIni(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "KeyInsightTests_" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllText(path, content.Replace("\n", "\r\n"));
            return path;
        }

        private static void Config_OldEnabledKeyRead()
        {
            string path = WriteTempIni("# comment\nThresholdMs=30\nEnabled=0\nSkipInjected=0\n");
            try
            {
                Config c = new Config();
                c.Read(path, false);
                Check.True(c.FilterEnabled == false, "old key enabled=0 must map to FilterEnabled=false");
                Check.Equal(30, c.ThresholdMs, "threshold preserved");
                Check.True(c.SkipInjected == false, "skipInjected preserved");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void Config_NewKeysRead()
        {
            string path = WriteTempIni("FilterEnabled=1\nCaptureEnabled=0\n");
            try
            {
                Config c = new Config();
                c.Read(path, false);
                Check.True(c.FilterEnabled == true, "filterenabled=1 read");
                Check.True(c.CaptureEnabled == false, "captureenabled=0 read");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void Config_LegacyIniMode()
        {
            // 旧 KeyDebounce.ini 迁移路径：legacy 模式忽略 enabled= 键（Load 里已强制 FilterEnabled=true）
            string path = WriteTempIni("enabled=0\nthresholdms=15\n");
            try
            {
                Config c = new Config();
                c.Read(path, true);
                Check.True(c.FilterEnabled == true, "legacy ini must not disable filter");
                Check.Equal(15, c.ThresholdMs, "legacy threshold applied");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void Config_SaveRoundTripWritesBothKeys()
        {
            // Save 写到 BaseDirectory\KeyInsight.ini（静态路径）；测试后清理，避免污染测试 exe 目录
            string iniPath = Config.ConfigPath;
            try
            {
                Config c = new Config();
                c.FilterEnabled = false;
                c.CaptureEnabled = false;
                c.ThresholdMs = 20;
                c.Save();

                Check.True(File.Exists(iniPath), "ini written");
                string text = File.ReadAllText(iniPath);
                Check.True(text.IndexOf("Enabled=0", StringComparison.Ordinal) >= 0,
                    "old key Enabled= must still be written for rollback compat");
                Check.True(text.IndexOf("FilterEnabled=0", StringComparison.Ordinal) >= 0, "new filter key written");
                Check.True(text.IndexOf("CaptureEnabled=0", StringComparison.Ordinal) >= 0, "capture key written");

                Config back = new Config();
                back.Read(iniPath, false);
                Check.True(back.FilterEnabled == false, "roundtrip filter off");
                Check.True(back.CaptureEnabled == false, "roundtrip capture off");
                Check.Equal(20, back.ThresholdMs, "roundtrip threshold");
            }
            finally
            {
                try { File.Delete(iniPath); } catch { }
            }
        }
    }
}
