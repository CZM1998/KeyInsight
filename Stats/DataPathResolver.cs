using System;
using System.IO;

namespace KeyInsight.Stats
{
    public enum AppMode
    {
        Portable = 0,
        Installed = 1
    }

    /// <summary>
    /// 数据路径解析（P0-06）。模式判定不依赖路径字符串，只认标记文件：
    /// - exe 旁存在 portable.marker → 便携模式；
    /// - exe 旁存在 installed.marker → 安装模式（数据在 %LOCALAPPDATA%\KeyInsight\data）；
    /// - 两者皆无 → 便携（与 v2.1.x 现行为完全一致：exe 旁 data/）。
    /// 迁移由 DataMigrator 显式执行（写 installed.marker 前 必须完成备份清单与校验），
    /// 不在本版本自动触发；用户回滚 = 删除 installed.marker 并把备份拷回。
    /// </summary>
    public static class DataPathResolver
    {
        public const string PortableMarker = "portable.marker";
        public const string InstalledMarker = "installed.marker";

        public static AppMode DetectMode(string exeDir)
        {
            if (File.Exists(Path.Combine(exeDir, PortableMarker))) return AppMode.Portable;
            if (File.Exists(Path.Combine(exeDir, InstalledMarker))) return AppMode.Installed;
            return AppMode.Portable;
        }

        public static string InstalledRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyInsight"); }
        }

        public static string ResolveDataRoot(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "data");
            }
            return Path.Combine(exeDir, "data");
        }

        /// <summary>V2 侧车与配置的根（在数据根旁，不与 KDS1 混放）。</summary>
        public static string ResolveV2Root(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "v2");
            }
            return Path.Combine(exeDir, "data", "v2");
        }

        public static string ResolveDiagnosticsDir(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "diagnostics");
            }
            return Path.Combine(exeDir, "data", "diagnostics");
        }

        public static string ResolveProfilesDir(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "profiles");
            }
            return Path.Combine(exeDir, "data", "profiles");
        }

        public static string ResolveMaintenanceDir(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "maintenance");
            }
            return Path.Combine(exeDir, "data", "maintenance");
        }

        /// <summary>版本化配置目录（设计 §6.3：data/config/KeyInsight.v2.json）。</summary>
        public static string ResolveConfigDir(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "config");
            }
            return Path.Combine(exeDir, "data", "config");
        }

        public static string ResolveConfigIniPath(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "KeyInsight.ini");
            }
            return Path.Combine(exeDir, "KeyInsight.ini");
        }

        /// <summary>设备中心数据域（方案 §10.1：DeviceCenter 独立目录，不与 KDS 混放）。</summary>
        public static string ResolveDeviceCenterDir(string exeDir)
        {
            if (DetectMode(exeDir) == AppMode.Installed)
            {
                return Path.Combine(InstalledRoot, "devicecenter");
            }
            return Path.Combine(exeDir, "data", "devicecenter");
        }
    }
}
