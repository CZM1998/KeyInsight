using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace KeyInsight.Updater
{
    /// <summary>
    /// 独立更新器 Helper（路线图 PR-6 / §7.4）：主程序退出后执行安装事务，
    /// 不在主程序进程内覆盖使用中的文件。
    /// 流程：等待主进程退出 → 备份程序文件 → 应用安装包
    /// → 启动新版本 → 等待健康心跳 → 成功清理 / 失败回滚。
    /// 绝不触碰用户统计数据（KDS / 侧车 / 配置 / 维护记录）。
    /// </summary>
    internal static class UpdaterMain
    {
        private static readonly string[] PackageFiles = { "KeyInsight.exe", "KeyInsight.Updater.exe", "README.md", "LICENSE" };

        private static int Main(string[] args)
        {
            // 参数规范：
            //   --wait-pid <pid>          等待退出的主进程
            //   --installer <path>        安装包路径（Setup exe）或 portable zip
            //   --package <setup|portable> 包形态
            //   --target <dir>            目标程序目录
            //   --health-nonce <text>     健康检查 nonce
            //   --health-path <path>      主程序写入心跳的文件
            //   --health-timeout <sec>    健康检查超时
            string installer = "", package = "setup", target = "", nonce = "", healthPath = "";
            int waitPid = 0, healthTimeoutSec = 60;
            for (int i = 0; i < args.Length - 1; i++)
            {
                string a = args[i];
                if (a == "--wait-pid") int.TryParse(args[++i], out waitPid);
                else if (a == "--installer") installer = args[++i];
                else if (a == "--package") package = args[++i];
                else if (a == "--target") target = args[++i];
                else if (a == "--health-nonce") nonce = args[++i];
                else if (a == "--health-path") healthPath = args[++i];
                else if (a == "--health-timeout") int.TryParse(args[++i], out healthTimeoutSec);
            }
            if (string.IsNullOrEmpty(installer) || string.IsNullOrEmpty(target) ||
                string.IsNullOrEmpty(nonce) || waitPid <= 0)
            {
                return 2;
            }

            if (string.IsNullOrEmpty(healthPath)) healthPath = Path.Combine(target, "data", "v2", "health.json");
            string backupDir = target + ".backup-" + Guid.NewGuid().ToString("N");
            Process restartedApp = null;
            bool backedUp = false;
            try
            {
                WaitForExit(waitPid, 15000);
                BackupProgramFiles(target, backupDir);
                backedUp = true;

                if (string.Equals(package, "portable", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZip(installer, target);
                }
                else
                {
                    RunInstaller(installer, target);
                }

                string newExe = Path.Combine(target, "KeyInsight.exe");
                if (!File.Exists(newExe)) throw new IOException("new exe missing after apply: " + newExe);
                ProcessStartInfo psi = new ProcessStartInfo(newExe,
                    "--health-nonce \"" + nonce + "\" --health-timeout " + healthTimeoutSec);
                psi.WorkingDirectory = target;
                restartedApp = Process.Start(psi);

                if (WaitHealthHeartbeat(healthPath, nonce, healthTimeoutSec, restartedApp))
                {
                    WriteResult(healthPath, "ok");
                    return 0;
                }

                throw new IOException("health heartbeat timeout");
            }
            catch (Exception ex)
            {
                try
                {
                    if (backedUp)
                    {
                        if (restartedApp != null && !restartedApp.HasExited)
                        {
                            restartedApp.Kill();
                            if (!restartedApp.WaitForExit(5000)) throw new IOException("updated process did not stop");
                        }
                        Rollback(target, backupDir);
                    }
                    WriteResult(healthPath, (backedUp ? "rolled-back: " : "not applied: ") + ex.Message);
                }
                catch (Exception rbEx)
                {
                    WriteResult(healthPath, "ROLLBACK-FAILED: " + rbEx.Message + " (orig: " + ex.Message + ")");
                    return 3;
                }
                return 1;
            }
            finally
            {
                if (restartedApp != null) restartedApp.Dispose();
            }
        }

        private static void WaitForExit(int pid, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (p == null || p.HasExited) return;
                }
                catch (ArgumentException)
                {
                    return;
                }
                Thread.Sleep(300);
            }
            throw new IOException("main process did not exit in time");
        }

        private static System.Collections.Generic.IEnumerable<string> ProgramFiles(string directory)
        {
            foreach (string name in PackageFiles)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
            foreach (string path in Directory.GetFiles(directory, "unins*.exe")) yield return path;
            foreach (string path in Directory.GetFiles(directory, "unins*.dat")) yield return path;
        }

        private static void BackupProgramFiles(string target, string backupDir)
        {
            Directory.CreateDirectory(backupDir);
            foreach (string path in ProgramFiles(target))
                File.Copy(path, Path.Combine(backupDir, Path.GetFileName(path)));
        }

        private static void RunInstaller(string installer, string target)
        {
            ProcessStartInfo psi = new ProcessStartInfo(installer,
                "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"" + target + "\"");
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(300000)) throw new IOException("installer timeout");
                if (p.ExitCode != 0) throw new IOException("installer exit code " + p.ExitCode);
            }
        }

        private static void ExtractZip(string zipPath, string target)
        {
            string staging = Path.Combine(Path.GetTempPath(), "KeyInsight-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging);
                if (!File.Exists(Path.Combine(staging, "KeyInsight.exe")))
                    throw new IOException("portable package is missing KeyInsight.exe");
                foreach (string name in PackageFiles)
                {
                    string source = Path.Combine(staging, name);
                    if (File.Exists(source)) File.Copy(source, Path.Combine(target, name), true);
                }
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
            }
        }

        private static bool WaitHealthHeartbeat(string healthPath, string nonce, int timeoutSec, Process restartedApp)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (DateTime.UtcNow < end)
            {
                try
                {
                    if (File.Exists(healthPath))
                    {
                        string text = File.ReadAllText(healthPath);
                        if (text.Contains(nonce)) return true;
                    }
                }
                catch { }
                if (restartedApp.HasExited) return false;
                Thread.Sleep(500);
            }
            return false;
        }

        private static void Rollback(string target, string backupDir)
        {
            foreach (string path in ProgramFiles(target))
            {
                if (!File.Exists(Path.Combine(backupDir, Path.GetFileName(path)))) File.Delete(path);
            }
            foreach (string path in ProgramFiles(backupDir))
                File.Copy(path, Path.Combine(target, Path.GetFileName(path)), true);
        }

        private static void WriteResult(string healthPath, string result)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(healthPath), "last-update-result.txt"),
                    DateTime.UtcNow.ToString("o") + " " + result);
            }
            catch { }
        }
    }
}
