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
    /// 流程：等待主进程退出 → 备份 → 应用（Setup 静默安装 / portable zip 原子替换）
    /// → 启动新版本 → 等待健康心跳 → 成功清理 / 失败回滚。
    /// 绝不触碰用户统计数据（KDS / 侧车 / 配置 / 维护记录）。
    /// </summary>
    internal static class UpdaterMain
    {
        private static int Main(string[] args)
        {
            // 参数规范：
            //   --wait-pid <pid>          等待退出的主进程
            //   --installer <path>        安装包路径（Setup exe）或 portable zip
            //   --package <setup|portable> 包形态
            //   --target <dir>            目标程序目录
            //   --health-nonce <text>     健康检查 nonce
            //   --health-timeout <sec>    健康检查超时
            string installer = "", package = "setup", target = "", nonce = "";
            int waitPid = 0, healthTimeoutSec = 60;
            for (int i = 0; i < args.Length - 1; i++)
            {
                string a = args[i];
                if (a == "--wait-pid") int.TryParse(args[++i], out waitPid);
                else if (a == "--installer") installer = args[++i];
                else if (a == "--package") package = args[++i];
                else if (a == "--target") target = args[++i];
                else if (a == "--health-nonce") nonce = args[++i];
                else if (a == "--health-timeout") int.TryParse(args[++i], out healthTimeoutSec);
            }
            if (string.IsNullOrEmpty(installer) || string.IsNullOrEmpty(target) || waitPid <= 0)
            {
                return 2;
            }

            string backupDir = target + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                WaitForExit(waitPid, 15000);
                CopyDirectory(target, backupDir, true);

                if (string.Equals(package, "portable", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZip(installer, target);
                }
                else
                {
                    RunInstaller(installer);
                }

                string newExe = Path.Combine(target, "KeyInsight.exe");
                if (!File.Exists(newExe)) throw new IOException("new exe missing after apply: " + newExe);
                ProcessStartInfo psi = new ProcessStartInfo(newExe,
                    "--health-nonce \"" + nonce + "\" --health-timeout " + healthTimeoutSec);
                psi.WorkingDirectory = target;
                Process.Start(psi);

                if (WaitHealthHeartbeat(target, nonce, healthTimeoutSec))
                {
                    WriteResult(target, "ok");
                    return 0;
                }

                throw new IOException("health heartbeat timeout");
            }
            catch (Exception ex)
            {
                try
                {
                    Rollback(target, backupDir);
                    WriteResult(target, "rolled-back: " + ex.Message);
                }
                catch (Exception rbEx)
                {
                    WriteResult(target, "ROLLBACK-FAILED: " + rbEx.Message + " (orig: " + ex.Message + ")");
                    return 3;
                }
                return 1;
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

        private static void CopyDirectory(string src, string dst, bool excludeDataDir)
        {
            if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length).TrimStart('\\', '/');
                if (excludeDataDir && rel.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) continue;
                string dstFile = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile));
                File.Copy(file, dstFile, true);
            }
        }

        private static void RunInstaller(string installer)
        {
            ProcessStartInfo psi = new ProcessStartInfo(installer, "/SILENT /NORESTART");
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(300000)) throw new IOException("installer timeout");
                if (p.ExitCode != 0) throw new IOException("installer exit code " + p.ExitCode);
            }
        }

        private static void ExtractZip(string zipPath, string target)
        {
            string staging = target + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zipPath, staging);
            string old = target + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            try
            {
                Directory.Move(staging, target);
                Directory.Delete(old, true);
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(target)) Directory.Move(old, target);
                throw;
            }
        }

        private static bool WaitHealthHeartbeat(string target, string nonce, int timeoutSec)
        {
            string healthPath = Path.Combine(target, "data", "v2", "health.json");
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
                Thread.Sleep(500);
            }
            return false;
        }

        private static void Rollback(string target, string backupDir)
        {
            if (!Directory.Exists(backupDir)) return;
            if (Directory.Exists(target))
            {
                string damaged = target + ".damaged";
                if (Directory.Exists(damaged)) Directory.Delete(damaged, true);
                Directory.Move(target, damaged);
            }
            Directory.Move(backupDir, target);
        }

        private static void WriteResult(string target, string result)
        {
            try
            {
                File.WriteAllText(Path.Combine(target, "data", "v2", "last-update-result.txt"),
                    DateTime.UtcNow.ToString("o") + " " + result);
            }
            catch { }
        }
    }
}
