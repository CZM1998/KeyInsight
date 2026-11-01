using KeyInsight.Stats;

namespace KeyInsight.UI
{
    public interface IShellActions
    {
        Config Cfg { get; }
        bool AutoStart { get; set; }
        IStatsSource Source { get; }
        void SetEnabled(bool on);
        void SetThreshold(int ms);
        void SetSkipInjected(bool on);
        void SetThemeMode(ThemeMode mode);
        void ReinstallHook();
        void RefreshSnapshot();
    }
}
