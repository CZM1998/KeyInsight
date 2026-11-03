using System;
using System.Collections.Generic;

namespace KeyInsight.Core.Devices
{
    /// <summary>
    /// 统一设备数据模型（方案 §5.1/§6.1）。
    /// 核心规则：连接状态 ≠ 健康状态，四个状态维度不得合并成一个字段；
    /// 未知值不显示为 0；能力按设备动态注册。
    /// 纯模型层，不依赖 WinForms。
    /// </summary>
    public enum DeviceCategory
    {
        Other = 0,
        Input = 1,        // 键盘、鼠标、手柄、数位板
        Display = 2,      // 显示器
        Audio = 3,        // 耳机、音箱、麦克风、声卡
        Video = 4,        // 摄像头、采集卡
        UsbHub = 5,       // USB Hub / 扩展坞
        Office = 6        // 打印机、扫描仪、读卡器
    }

    public enum ConnectionState { Unknown = 0, Connected = 1, Disconnected = 2 }

    public enum OperationalState { Unknown = 0, Available = 1, Degraded = 2, Unavailable = 3 }

    public enum HealthState { Unknown = 0, NoIssueObserved = 1, Attention = 2, ConfirmedIssue = 3 }

    public enum DiagnosticRunState { NotTested = 0, Queued = 1, Running = 2, Completed = 3, Failed = 4 }

    public enum FindingSeverity { Info = 0, Attention = 1, Warning = 2 }

    public enum FindingStatus { Candidate = 0, UserConfirmed = 1, Resolved = 2, Dismissed = 3 }

    public enum EvidenceSource { WindowsApi = 0, Heuristic = 1, UserObserved = 2, UserConfirmed = 3 }

    public enum DeviceEventType
    {
        Connected = 0,
        Disconnected = 1,
        IdentityChanged = 2,
        DisplayModeChanged = 3,
        DefaultEndpointChanged = 4,
        AttentionCandidate = 5,
        HealthChanged = 6,
        Archived = 7
    }

    /// <summary>Adapter 输出的设备快照（单次枚举的事实，无档案语义）。</summary>
    public sealed class DeviceSnapshot
    {
        public string InstanceKey;          // 稳定实例键（PnP InstancePath / 显示器 EDID 键）
        public DeviceCategory Category;
        public string Name;
        public string Manufacturer;
        public string Model;
        public string DeviceClass;          // Windows 设备类（DISPLAY / MEDIA / USB / ...）
        public string InstanceId;           // Windows 实例 ID
        public string ContainerId;          // 容器 ID（可能为空）
        public string ParentKey;            // 上游设备键（拓扑用，可为空）
        public string SerialNumber;         // 可能为空
        public bool IsPresent;              // 系统当前是否可见
        public int ProblemCode;             // CM 问题码（0 = 正常）
        public string Source;               // 数据来源（Pnp / Display / ...）
        public string AttributesJson;       // Adapter 专属扩展（如显示器 EDID/模式）
    }

    /// <summary>设备档案（持久化）。DeviceId 为应用生成的稳定 GUID。</summary>
    public sealed class DeviceRecord
    {
        public string DeviceId;
        public string IdentityKey;
        public DeviceCategory Category;
        public string Name;
        public string UserAlias;
        public string UserNote;
        public string Manufacturer;
        public string Model;
        public string SerialNumber;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public ConnectionState Connection;
        public OperationalState Operational;
        public HealthState Health;
        public DiagnosticRunState Diagnostic;
        public double LastIdentityConfidence;
        public bool IdentityConfirmedByUser;
        public List<string> InstanceKeys = new List<string>();
        public string AttributesJson;       // 最近一次 Adapter 快照的扩展属性
        public string AttributesSource;     // 属性来源与更新时间（多来源不静默覆盖）
        public DateTime AttributesUpdatedUtc;
    }

    public enum CapabilityOperation { Read = 0, Observe = 1, Test = 2, Control = 3 }

    public enum CapabilitySupport { Unknown = 0, Unsupported = 1, Supported = 2 }

    public enum CapabilityAvailability { Unknown = 0, Available = 1, Unavailable = 2 }

    /// <summary>设备能力（方案 §5.1/§15.1）。未支持能力不得显示为可点击。</summary>
    public sealed class DeviceCapability
    {
        public string CapabilityId;         // 如 display.brightness.control
        public CapabilityOperation Operation;
        public CapabilitySupport Support;
        public CapabilityAvailability Availability;
        public string Source;               // DDC_CI / WindowsApi / Heuristic
        public bool RequiresUserAction;
        public string ParametersJson;
    }

    /// <summary>设备事件摘要（插拔、模式变化等；批量持久化）。</summary>
    public sealed class DeviceEventItem
    {
        public DeviceEventType EventType;
        public string DeviceId;
        public string InstanceKey;
        public DeviceCategory Category;
        public DateTime TimeUtc;
        public string EvidenceJson;
    }

    /// <summary>一次诊断任务。</summary>
    public sealed class DiagnosticSession
    {
        public string SessionId;
        public string DeviceId;
        public string DeviceName;
        public string TestType;             // display.dead_pixel / display.uniformity / ...
        public DateTime StartedAtUtc;
        public DateTime EndedAtUtc;
        public DiagnosticRunState Status;
        public string Note;
    }

    /// <summary>一次诊断发现（方案 §10.4/§15.2）。
    /// 用户确认类结果不伪造置信度 —— Confidence 为 null。</summary>
    public sealed class DiagnosticFinding
    {
        public string FindingId;
        public string SessionId;
        public string DeviceId;
        public string DiagnosticCode;
        public FindingSeverity Severity;
        public FindingStatus Status;
        public double? Confidence;          // 仅算法候选填写；用户确认类为 null
        public int SampleCount;
        public EvidenceSource EvidenceSource;
        public string EvidenceJson;
        public string Recommendation;
        public DateTime CreatedAtUtc;
        public DateTime? ResolvedAtUtc;
    }

    public static class DeviceCategoryNames
    {
        public static string Get(DeviceCategory c)
        {
            switch (c)
            {
                case DeviceCategory.Input: return "输入设备";
                case DeviceCategory.Display: return "显示设备";
                case DeviceCategory.Audio: return "音频设备";
                case DeviceCategory.Video: return "视频设备";
                case DeviceCategory.UsbHub: return "USB 与扩展";
                case DeviceCategory.Office: return "办公设备";
                default: return "其他设备";
            }
        }
    }

    public static class ConnectionStateNames
    {
        public static string Get(ConnectionState s)
        {
            switch (s)
            {
                case ConnectionState.Connected: return "已连接";
                case ConnectionState.Disconnected: return "已断开";
                default: return "未知";
            }
        }
    }

    public static class HealthStateNames
    {
        public static string Get(HealthState s)
        {
            switch (s)
            {
                case HealthState.NoIssueObserved: return "观察无异常";
                case HealthState.Attention: return "需要关注";
                case HealthState.ConfirmedIssue: return "已确认问题";
                default: return "尚未检测";
            }
        }
    }
}
