using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Devices
{
    /// <summary>
    /// 设备生命周期状态机（方案 §6.2）：
    /// Unknown → Discovered → ResolvingIdentity → Connected ⇄ Disconnected → Archived。
    /// 连接状态不等于健康状态；本状态机只管生命周期相位。
    /// </summary>
    public enum LifecyclePhase
    {
        Unknown = 0,
        Discovered = 1,
        ResolvingIdentity = 2,
        Connected = 3,
        Degraded = 4,
        Disconnected = 5,
        Archived = 6
    }

    public static class DeviceLifecycle
    {
        /// <summary>允许的相位迁移表。Archived 只能由用户显式恢复。</summary>
        private static readonly Dictionary<LifecyclePhase, LifecyclePhase[]> Allowed =
            BuildAllowed();

        private static Dictionary<LifecyclePhase, LifecyclePhase[]> BuildAllowed()
        {
            Dictionary<LifecyclePhase, LifecyclePhase[]> map =
                new Dictionary<LifecyclePhase, LifecyclePhase[]>();
            map[LifecyclePhase.Unknown] = new LifecyclePhase[] { LifecyclePhase.Discovered };
            map[LifecyclePhase.Discovered] = new LifecyclePhase[]
            {
                LifecyclePhase.ResolvingIdentity, LifecyclePhase.Connected, LifecyclePhase.Archived
            };
            map[LifecyclePhase.ResolvingIdentity] = new LifecyclePhase[]
            {
                LifecyclePhase.Connected, LifecyclePhase.Disconnected, LifecyclePhase.Archived
            };
            map[LifecyclePhase.Connected] = new LifecyclePhase[]
            {
                LifecyclePhase.Degraded, LifecyclePhase.Disconnected, LifecyclePhase.Archived
            };
            map[LifecyclePhase.Degraded] = new LifecyclePhase[]
            {
                LifecyclePhase.Connected, LifecyclePhase.Disconnected, LifecyclePhase.Archived
            };
            map[LifecyclePhase.Disconnected] = new LifecyclePhase[]
            {
                LifecyclePhase.Connected, LifecyclePhase.Archived   // 重连 / 用户清理
            };
            map[LifecyclePhase.Archived] = new LifecyclePhase[]
            {
                LifecyclePhase.Connected, LifecyclePhase.Discovered // 用户显式恢复
            };
            return map;
        }

        public static bool CanTransition(LifecyclePhase from, LifecyclePhase to)
        {
            if (from == to) return true;
            LifecyclePhase[] targets;
            if (!Allowed.TryGetValue(from, out targets)) return false;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == to) return true;
            }
            return false;
        }

        /// <summary>
        /// 用快照驱动相位迁移。返回迁移后的相位；非法迁移保持原相位。
        /// </summary>
        public static LifecyclePhase Apply(LifecyclePhase current, DeviceSnapshot s)
        {
            if (s == null) return current;
            LifecyclePhase target = s.IsPresent
                ? (s.ProblemCode != 0 ? LifecyclePhase.Degraded : LifecyclePhase.Connected)
                : LifecyclePhase.Disconnected;
            if (current == LifecyclePhase.Unknown && s.IsPresent)
            {
                // 首次发现直接进入 Connected（身份解析随档案创建同步完成）
                return LifecyclePhase.Connected;
            }
            return CanTransition(current, target) ? target : current;
        }

        /// <summary>相位是否意味着「有意义的持久化变化」（避免插拔风暴重复写库）。</summary>
        public static bool IsMeaningfulChange(LifecyclePhase from, LifecyclePhase to,
            ConnectionState oldConn, ConnectionState newConn)
        {
            if (from != to) return true;
            return oldConn != newConn;
        }

        public static string PhaseText(LifecyclePhase p)
        {
            switch (p)
            {
                case LifecyclePhase.Discovered: return "已发现";
                case LifecyclePhase.ResolvingIdentity: return "识别中";
                case LifecyclePhase.Connected: return "已连接";
                case LifecyclePhase.Degraded: return "降级";
                case LifecyclePhase.Disconnected: return "已断开";
                case LifecyclePhase.Archived: return "已归档";
                default: return "未知";
            }
        }
    }
}
