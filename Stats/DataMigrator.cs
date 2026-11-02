using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KeyInsight.Stats
{
    public sealed class MigrationResult
    {
        public bool Ok;
        public bool AlreadyMigrated;
        public string Message = "";
        public List<string> Files = new List<string>();
        public string ManifestPath = "";
        public string BackupDir = "";
    }

    /// <summary>
    /// 便携 → 安装布局迁移（P0-06）。流程（设计文档 §6.5 迁移检查表）：
    /// 1. 迁移前对每个源文件计算 SHA-256 与大小，写入备份清单（manifest）；
    /// 2. 拷贝原文件到目标目录 + 完整只读备份目录，迁移成功前不删任何原件；
    /// 3. 校验拷贝后哈希一致才写 installed.marker（提交点）；
    /// 4. 幂等：已迁移（存在 installed.marker）则直接返回；
    /// 5. 失败：不写 marker，源文件保持原样，可重试；回滚 = 删 marker + 从备份拷回。
    /// 通过 `--migrate-data` CLI 调用；本版本不在 UI 自动触发。
    /// </summary>
    public static class DataMigrator
    {
        /// <summary>targetRootOverride 仅测试使用（默认真实 %LOCALAPPDATA%\KeyInsight）。</summary>
        public static MigrationResult MigratePortableToInstalled(string exeDir, string targetRootOverride = null)
        {
            MigrationResult r = new MigrationResult();

            if (DataPathResolver.DetectMode(exeDir) == AppMode.Installed)
            {
                r.AlreadyMigrated = true;
                r.Ok = true;
                r.Message = "already installed mode";
                return r;
            }

            string sourceIni = Path.Combine(exeDir, "KeyInsight.ini");
            string sourceData = Path.Combine(exeDir, "data");
            string targetRoot = string.IsNullOrEmpty(targetRootOverride)
                ? DataPathResolver.InstalledRoot
                : targetRootOverride;
            string targetData = Path.Combine(targetRoot, "data");
            string backupDir = Path.Combine(exeDir, "data", "backups",
                "migration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string v2Source = Path.Combine(sourceData, "v2");
            string diagSource = Path.Combine(sourceData, "diagnostics");
            string profilesSource = Path.Combine(sourceData, "profiles");

            try
            {
                List<KeyValuePair<string, string>> plan = new List<KeyValuePair<string, string>>(); // source -> relative target
                if (File.Exists(sourceIni)) plan.Add(new KeyValuePair<string, string>(sourceIni, "KeyInsight.ini"));
                if (Directory.Exists(sourceData))
                {
                    foreach (string f in Directory.GetFiles(sourceData, "*.kds"))
                        plan.Add(new KeyValuePair<string, string>(f,
                            Path.Combine("data", Path.GetFileName(f))));
                }
                if (Directory.Exists(v2Source))
                {
                    foreach (string f in Directory.GetFiles(v2Source, "*", SearchOption.AllDirectories))
                        plan.Add(new KeyValuePair<string, string>(f,
                            Path.Combine("v2", f.Substring(v2Source.Length).TrimStart('\\', '/'))));
                }
                if (Directory.Exists(diagSource))
                {
                    foreach (string f in Directory.GetFiles(diagSource, "*", SearchOption.AllDirectories))
                        plan.Add(new KeyValuePair<string, string>(f,
                            Path.Combine("diagnostics", f.Substring(diagSource.Length).TrimStart('\\', '/'))));
                }
                if (Directory.Exists(profilesSource))
                {
                    foreach (string f in Directory.GetFiles(profilesSource, "*", SearchOption.AllDirectories))
                        plan.Add(new KeyValuePair<string, string>(f,
                            Path.Combine("profiles", f.Substring(profilesSource.Length).TrimStart('\\', '/'))));
                }

                if (plan.Count == 0)
                {
                    // 无数据可迁：直接提交标记
                    Directory.CreateDirectory(targetRoot);
                    File.WriteAllText(Path.Combine(exeDir, DataPathResolver.InstalledMarker),
                        DateTime.UtcNow.ToString("u"));
                    r.Ok = true;
                    r.Message = "no data to migrate; marker written";
                    return r;
                }

                // 1. 备份清单（含哈希）
                Directory.CreateDirectory(backupDir);
                Directory.CreateDirectory(targetData);
                List<object> manifest = new List<object>();
                foreach (KeyValuePair<string, string> kv in plan)
                {
                    byte[] bytes = File.ReadAllBytes(kv.Key);
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["file"] = kv.Value;
                    o["sha256"] = Sha256(bytes);
                    o["size"] = bytes.Length;
                    manifest.Add(o);
                }
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["schema"] = 1;
                root["createdAtUtc"] = DateTime.UtcNow.ToString("u");
                root["from"] = exeDir;
                root["to"] = targetRoot;
                root["files"] = manifest;
                r.ManifestPath = Path.Combine(backupDir, "manifest.json");
                File.WriteAllText(r.ManifestPath,
                    new JavaScriptSerializer().Serialize(root), new System.Text.UTF8Encoding(false));
                r.BackupDir = backupDir;

                // 2. 拷贝到目标 + 备份（.partial 临时名，成功后改正式名）
                foreach (KeyValuePair<string, string> kv in plan)
                {
                    string targetPath = Path.Combine(targetRoot, kv.Value);
                    string targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    string partial = targetPath + ".partial";
                    File.Copy(kv.Key, partial, true);
                    if (File.Exists(targetPath)) File.Replace(partial, targetPath, null);
                    else File.Move(partial, targetPath);

                    string backupPath = Path.Combine(backupDir, kv.Value);
                    string backupDirForFile = Path.GetDirectoryName(backupPath);
                    if (!string.IsNullOrEmpty(backupDirForFile)) Directory.CreateDirectory(backupDirForFile);
                    File.Copy(kv.Key, backupPath, true);
                }

                // 3. 校验拷贝完整性
                foreach (KeyValuePair<string, string> kv in plan)
                {
                    string targetPath = Path.Combine(targetRoot, kv.Value);
                    byte[] srcBytes = File.ReadAllBytes(kv.Key);
                    byte[] dstBytes = File.ReadAllBytes(targetPath);
                    if (srcBytes.Length != dstBytes.Length || Sha256(srcBytes) != Sha256(dstBytes))
                    {
                        r.Ok = false;
                        r.Message = "verify failed: " + kv.Value;
                        return r; // 不写 marker，目标数据保留但以备份为准
                    }
                }

                // 4. 提交：写安装标记
                File.WriteAllText(Path.Combine(exeDir, DataPathResolver.InstalledMarker),
                    DateTime.UtcNow.ToString("u"));

                r.Ok = true;
                for (int i = 0; i < plan.Count; i++) r.Files.Add(plan[i].Value);
                r.Message = "migrated " + plan.Count + " files";
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Message = ex.Message;
                return r;
            }
        }

        public static string Sha256(byte[] bytes)
        {
            using (System.Security.Cryptography.SHA256Managed sha =
                new System.Security.Cryptography.SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
