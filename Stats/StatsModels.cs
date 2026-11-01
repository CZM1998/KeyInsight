using System;
using System.Collections.Generic;

namespace KeyInsight.Stats
{
    public enum KeyEventType : byte
    {
        Down = 1,
        Swallow = 2,
        MouseDown = 3,
        MouseWheel = 4
    }

    public struct KeyEvent
    {
        public KeyEventType Type;
        public int Vk;
        public int Button;
        public int Delta;
        public DateTime At;
        public string App;
        public double GapDown;
        public double GapShortUp;
    }

    public static class MouseIds
    {
        public const int Left = 1;
        public const int Right = 2;
        public const int Middle = 3;
        public const int Back = 4;
        public const int Forward = 5;
        public const int WheelUp = 6;
        public const int WheelDown = 7;
        public const int WheelLeft = 8;
        public const int WheelRight = 9;

        public static bool IsWheel(int button)
        {
            return button >= WheelUp;
        }
    }

    public sealed class KeyAgg
    {
        public long Count;
        public long Swallowed;

        public KeyAgg() { }

        public KeyAgg(long count, long swallowed)
        {
            Count = count;
            Swallowed = swallowed;
        }
    }

    public sealed class AppAgg
    {
        public string Name;
        public long Total;
        public long Swallowed;
        public int[] Hours = new int[24];
        public Dictionary<int, KeyAgg> Keys = new Dictionary<int, KeyAgg>();
        public Dictionary<int, long> Mouse = new Dictionary<int, long>();

        public AppAgg(string name)
        {
            Name = name;
        }

        public AppAgg Clone()
        {
            AppAgg a = new AppAgg(Name);
            a.Total = Total;
            a.Swallowed = Swallowed;
            Array.Copy(Hours, a.Hours, 24);
            foreach (KeyValuePair<int, KeyAgg> kv in Keys)
            {
                a.Keys[kv.Key] = new KeyAgg(kv.Value.Count, kv.Value.Swallowed);
            }
            foreach (KeyValuePair<int, long> kv in Mouse)
            {
                a.Mouse[kv.Key] = kv.Value;
            }
            return a;
        }
    }

    public sealed class DayData
    {
        public int Day;
        public long Total;
        public long Swallowed;
        public int ActiveMinutes;
        public bool[] Minutes = new bool[1440];
        public int[] Hours = new int[24];
        public Dictionary<int, KeyAgg> Keys = new Dictionary<int, KeyAgg>();
        public Dictionary<string, AppAgg> Apps = new Dictionary<string, AppAgg>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, long> Mouse = new Dictionary<int, long>();
    }

    public sealed class MonthData
    {
        public int Year;
        public int Month;
        public Dictionary<int, DayData> Days = new Dictionary<int, DayData>();
        public long Total;
        public long Swallowed;

        public string Key
        {
            get { return Year.ToString("D4") + "-" + Month.ToString("D2"); }
        }
    }

    public sealed class RecentSwallow
    {
        public DateTime At;
        public int Vk;
        public string App;
        public double GapDown;
        public double GapShortUp;
    }

    public sealed class MonthInfo
    {
        public string Key;
        public int Year;
        public int Month;
        public string Path;
        public long Size;
        public bool Loaded;
        public long Total;
        public long Swallowed;
    }

    public sealed class DayView
    {
        public DateTime Date;
        public long Total;
        public long Swallowed;
        public int ActiveMinutes;
        public int[] Hours = new int[24];
        public Dictionary<int, KeyAgg> Keys = new Dictionary<int, KeyAgg>();
        public Dictionary<string, AppAgg> Apps = new Dictionary<string, AppAgg>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, long> Mouse = new Dictionary<int, long>();
        public double KpmAverage;
        public int KpmPeak;
    }

    public sealed class AppBar
    {
        public string Name;
        public long Total;
        public long Swallowed;
    }

    public sealed class DayPoint
    {
        public DateTime Date;
        public long Total;
        public long Swallowed;
    }

    public sealed class RangeView
    {
        public DateTime Start;
        public DateTime End;
        public long Total;
        public long Swallowed;
        public int ActiveDays;
        public int ActiveHours;
        public int ActiveMinutes;
        public int[] Hours = new int[24];
        public int[,] WeekdayHour = new int[7, 24];
        public Dictionary<int, KeyAgg> Keys = new Dictionary<int, KeyAgg>();
        public Dictionary<string, AppAgg> Apps = new Dictionary<string, AppAgg>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, long> Mouse = new Dictionary<int, long>();
        public List<DayPoint> Daily = new List<DayPoint>();
        public bool DailyComplete = true;
    }

    public sealed class KeyHealthRow
    {
        public int Vk;
        public long Total;
        public long Swallowed;
        public int FirstDay;
        public int LastDay;
        public int Swallowed7;
        public int SwallowedPrev7;
        public int Score;
        public int Status;

        public double Rate
        {
            get { return Swallowed / (double)Math.Max(1, Total + Swallowed); }
        }
    }

    public sealed class StatsSnapshot
    {
        public long TotalAll;
        public long SwallowedAll;
        public DayView Today;
        public RangeView Range;
        public long[] Last30Days = new long[30];
        public DateTime Last30Start;
        public List<RecentSwallow> RecentSwallows = new List<RecentSwallow>();
        public List<MonthInfo> Months = new List<MonthInfo>();
        public bool HistoryComplete;
        public double KpmNow;
        public List<KeyHealthRow> KeyHealth = new List<KeyHealthRow>();
        public Dictionary<int, int[]> KeySwallowTrend = new Dictionary<int, int[]>();
        public Dictionary<int, int[]> KeyPressTrend = new Dictionary<int, int[]>();
        public DateTime KeyTrendStart;
    }

    public interface IStatsSource
    {
        StatsSnapshot GetSnapshot(DateTime start, DateTime end);
        List<MonthInfo> GetMonths();
        void DeleteMonth(string key);
        void DeleteAllMonths();
        void ExportCsv(string basePath, IList<string> monthKeys);
        string DataDirectory { get; }
    }

    public static class DayKeys
    {
        public static int From(DateTime d)
        {
            return (d.Year * 10000) + (d.Month * 100) + d.Day;
        }

        public static DateTime ToDate(int key)
        {
            return new DateTime(key / 10000, (key / 100) % 100, key % 100);
        }

        public static string ToText(int key)
        {
            int y = key / 10000;
            int m = (key / 100) % 100;
            int d = key % 100;
            return y.ToString("D4") + "-" + m.ToString("D2") + "-" + d.ToString("D2");
        }

        public static string MonthKey(int year, int month)
        {
            return year.ToString("D4") + "-" + month.ToString("D2");
        }
    }
}
