using KeyInsight.Core.Policy;
using KeyInsight.Hook;
using KeyInsight.Stats;

namespace KeyInsight.UI
{
    public interface IShellActions
    {
        Config Cfg { get; }
        bool AutoStart { get; set; }
        bool AutoCheckUpdates { get; set; }
        IStatsSource Source { get; }
        KeyPolicyStore KeyPolicies { get; }
        PolicySnapshot PolicyStatus { get; }
        RawInputTracker RawInput { get; }
        HookSupervisor Supervisor { get; }
        KeyInsight.Update.Orchestration.UpdateOrchestrator Updater { get; }
        KeyInsight.Update.Routing.RouteCatalog RouteCatalog { get; }
        long TelemetryDropped { get; }
        bool CaptureEnabled { get; }
        bool DiagnosticsEnabled { get; }
        bool FloatingSummaryOn { get; }
        void SetCaptureEnabled(bool on);
        void SetDiagnosticsEnabled(bool on);
        void SetFloatingSummary(bool on);
        long MouseRawWheelDelta { get; }
        long MouseCycleCandidates { get; }
        string MaintenanceDir { get; }
        void SetEnabled(bool on);
        void SetThreshold(int ms);
        void SetSkipInjected(bool on);
        void SetThemeMode(ThemeMode mode);
        void SetPolicyMode(int mode);
        void RefreshPolicy();
        void ReinstallHook();
        void CheckUpdates();
        /// <summary>更新检查进行中（按钮动画用）。</summary>
        bool UpdateCheckBusy { get; }
        /// <summary>最近一次更新检查结果摘要（面板反馈行）；null = 尚未检查。</summary>
        string LastUpdateCheckResult { get; }
        void RefreshSnapshot();
    }
}
