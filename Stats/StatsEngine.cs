using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace KeyInsight.Stats
{
    public sealed class StatsEngine : IStatsSource, IDisposable
    {
        private readonly string _dataDir;
        private readonly string _selfApp;
        private readonly ConcurrentQueue<KeyEvent> _queue = new ConcurrentQueue<KeyEvent>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ManualResetEvent _idle = new ManualResetEvent(true);
        private readonly object _gate = new object();
        private readonly Dictionary<string, MonthData> _months = new Dictionary<string, MonthData>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RecentSwallow> _recent = new List<RecentSwallow>();
        private readonly long[] _secStamp = new long[60];
        private readonly int[] _secCount = new int[60];
        private readonly int[] _minuteCounts = new int[1440];

        private MonthData _current;
        private Thread _worker;
        private volatile bool _stop;
        private DateTime _lastSaveUtc = DateTime.UtcNow;
        private volatile bool _allLoaded;
        private int _minuteDay;
        private long _version;

        private List<MonthInfo> _monthCache;
        private DateTime _monthCacheAt = DateTime.MinValue;

        public string DataDirectory { get { return _dataDir; } }

        public StatsEngine(string dataDir, string selfApp)
        {
            _dataDir = dataDir;
            _selfApp = NormalizeApp(selfApp);
            if (_selfApp == "unknown") _selfApp = "";

            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);

            LoadCurrentMonth(DateTime.Now);

            _worker = new Thread(Worker);
            _worker.IsBackground = true;
            _worker.Name = "KeyInsightStats";
            _worker.Start();

            ThreadPool.QueueUserWorkItem(delegate { LoadAllMonths(); });
        }

        public void Enqueue(KeyEvent e)
        {
            _idle.Reset();
            _queue.Enqueue(e);
            _signal.Set();
        }

        public bool WaitIdle(int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                _signal.Set();
                if (_queue.IsEmpty && _idle.WaitOne(30))
                {
                    if (_queue.IsEmpty) return true;
                }
            }
            return _queue.IsEmpty;
        }

        private void Worker()
        {
            while (!_stop)
            {
                _signal.WaitOne(200);
                Drain();
                if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= 60) SaveCurrent();
            }
            Drain();
            SaveCurrent();
        }

        private void Drain()
        {
            KeyEvent e;
            while (_queue.TryDequeue(out e))
            {
                try { Apply(e); }
                catch (Exception ex) { LogError(ex); }
            }
            _idle.Set();
        }

        private void Apply(KeyEvent e)
        {
            string app = NormalizeApp(e.App);
            if (_selfApp.Length > 0 && string.Equals(app, _selfApp, StringComparison.OrdinalIgnoreCase)) return;

            lock (_gate)
            {
                EnsureMonth(e.At);
                int dayKey = DayKeys.From(e.At);
                DayData day = GetDay(_current, dayKey);
                int hour = e.At.Hour;
                int minute = (e.At.Hour * 60) + e.At.Minute;

                if (e.Type == KeyEventType.Down)
                {
                    _current.Total++;
                    day.Total++;
                    day.Hours[hour]++;
                    if (!day.Minutes[minute])
                    {
                        day.Minutes[minute] = true;
                        day.ActiveMinutes++;
                    }

                    KeyAgg key = GetKey(day, e.Vk);
                    key.Count++;

                    AppAgg agg = GetApp(day, app);
                    agg.Total++;
                    agg.Hours[hour]++;
                    GetKey(agg, e.Vk).Count++;

                    if (dayKey != _minuteDay)
                    {
                        Array.Clear(_minuteCounts, 0, _minuteCounts.Length);
                        _minuteDay = dayKey;
                    }
                    _minuteCounts[minute]++;

                    long sec = ToUnixSeconds(e.At);
                    int idx = (int)(sec % 60);
                    if (_secStamp[idx] != sec)
                    {
                        _secStamp[idx] = sec;
                        _secCount[idx] = 0;
                    }
                    _secCount[idx]++;
                }
                else if (e.Type == KeyEventType.Swallow)
                {
                    _current.Swallowed++;
                    day.Swallowed++;

                    GetKey(day, e.Vk).Swallowed++;

                    AppAgg agg = GetApp(day, app);
                    agg.Swallowed++;
                    GetKey(agg, e.Vk).Swallowed++;

                    RecentSwallow r = new RecentSwallow();
                    r.At = e.At;
                    r.Vk = e.Vk;
                    r.App = app;
                    r.GapDown = e.GapDown;
                    r.GapShortUp = e.GapShortUp;
                    _recent.Add(r);
                    if (_recent.Count > 240) _recent.RemoveAt(0);
                }
                else
                {
                    if (e.Button > 0)
                    {
                        long add = e.Type == KeyEventType.MouseWheel ? Math.Max(1, e.Delta) : 1;
                        AddMouse(day.Mouse, e.Button, add);
                        AppAgg agg = GetApp(day, app);
                        AddMouse(agg.Mouse, e.Button, add);
                    }
                }

                _version++;
            }
        }

        private void EnsureMonth(DateTime at)
        {
            string key = DayKeys.MonthKey(at.Year, at.Month);
            if (_current != null && _current.Key == key) return;

            if (_current != null)
            {
                try { MonthFile.Save(PathFor(_current.Key), _current); }
                catch (Exception ex) { LogError(ex); }
            }

            MonthData md;
            if (!_months.TryGetValue(key, out md))
            {
                try
                {
                    md = MonthFile.Load(PathFor(key), at.Year, at.Month);
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    try { File.Move(PathFor(key), PathFor(key) + ".corrupt"); }
                    catch { }
                    md = new MonthData();
                    md.Year = at.Year;
                    md.Month = at.Month;
                }
                _months[key] = md;
            }
            _current = md;
        }

        private void LoadCurrentMonth(DateTime at)
        {
            lock (_gate)
            {
                string key = DayKeys.MonthKey(at.Year, at.Month);
                MonthData md;
                if (!_months.TryGetValue(key, out md))
                {
                    try
                    {
                        md = MonthFile.Load(PathFor(key), at.Year, at.Month);
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                        try { File.Move(PathFor(key), PathFor(key) + ".corrupt"); }
                        catch { }
                        md = new MonthData();
                        md.Year = at.Year;
                        md.Month = at.Month;
                    }
                    _months[key] = md;
                }
                _current = md;
            }
        }

        public void LoadAllMonths()
        {
            try
            {
                string[] files = Directory.GetFiles(_dataDir, "*.kds");
                foreach (string file in files)
                {
                    string key = Path.GetFileNameWithoutExtension(file);
                    int y, m;
                    if (!TryParseMonth(key, out y, out m)) continue;

                    lock (_gate)
                    {
                        if (_months.ContainsKey(key)) continue;
                        try
                        {
                            _months[key] = MonthFile.Load(file, y, m);
                        }
                        catch (Exception ex)
                        {
                            LogError(ex);
                        }
                    }
                }
                _allLoaded = true;
            }
            catch
            {
            }
            InvalidateMonthCache();
        }

        public void SaveCurrent()
        {
            lock (_gate)
            {
                if (_current != null)
                {
                    try { MonthFile.Save(PathFor(_current.Key), _current); }
                    catch (Exception ex) { LogError(ex); }
                }
                _lastSaveUtc = DateTime.UtcNow;
            }
        }

        public void SaveAll()
        {
            lock (_gate)
            {
                foreach (MonthData md in _months.Values)
                {
                    try { MonthFile.Save(PathFor(md.Key), md); }
                    catch (Exception ex) { LogError(ex); }
                }
                _lastSaveUtc = DateTime.UtcNow;
            }
            InvalidateMonthCache();
        }

        public void GetTodayCounts(out long total, out long swallowed)
        {
            lock (_gate)
            {
                DayData day = FindDay(DateTime.Today);
                total = day == null ? 0 : day.Total;
                swallowed = day == null ? 0 : day.Swallowed;
            }
        }

        public long GetTodayMouseCount()
        {
            lock (_gate)
            {
                DayData day = FindDay(DateTime.Today);
                if (day == null) return 0;
                long n = 0;
                foreach (long v in day.Mouse.Values) n += v;
                return n;
            }
        }

        public StatsSnapshot GetSnapshot(DateTime start, DateTime end)
        {
            lock (_gate)
            {
                StatsSnapshot s = new StatsSnapshot();
                DateTime today = DateTime.Today;

                s.Today = BuildDayView(today);
                s.TotalAll = 0;
                s.SwallowedAll = 0;
                foreach (MonthData md in _months.Values)
                {
                    s.TotalAll += md.Total;
                    s.SwallowedAll += md.Swallowed;
                }

                DateTime rangeEnd = (end == DateTime.MaxValue) ? today : end.Date;
                DateTime rangeStart = (start == DateTime.MinValue) ? EarliestDay() : start.Date;
                if (rangeStart > rangeEnd)
                {
                    DateTime tmp = rangeStart;
                    rangeStart = rangeEnd;
                    rangeEnd = tmp;
                }

                s.Range = BuildRange(rangeStart, rangeEnd);
                BuildKeyHealth(s);

                s.Last30Start = today.AddDays(-29);
                for (int i = 0; i < 30; i++) s.Last30Days[i] = DayTotal(today.AddDays(i - 29));

                for (int i = _recent.Count - 1; i >= 0 && s.RecentSwallows.Count < 20; i--)
                {
                    RecentSwallow r = _recent[i];
                    RecentSwallow copy = new RecentSwallow();
                    copy.At = r.At;
                    copy.Vk = r.Vk;
                    copy.App = r.App;
                    copy.GapDown = r.GapDown;
                    copy.GapShortUp = r.GapShortUp;
                    s.RecentSwallows.Add(copy);
                }

                s.Months = GetMonthsLocked();
                s.HistoryComplete = _allLoaded;

                long nowSec = ToUnixSeconds(DateTime.Now);
                long sum = 0;
                for (int i = 0; i < 60; i++)
                {
                    if (_secStamp[i] > 0 && nowSec - _secStamp[i] < 60) sum += _secCount[i];
                }
                s.KpmNow = sum;
                return s;
            }
        }

        private DayView BuildDayView(DateTime date)
        {
            DayView v = new DayView();
            v.Date = date;
            DayData day = FindDay(date);
            if (day == null) return v;

            v.Total = day.Total;
            v.Swallowed = day.Swallowed;
            v.ActiveMinutes = day.ActiveMinutes;
            Array.Copy(day.Hours, v.Hours, 24);
            foreach (KeyValuePair<int, KeyAgg> kv in day.Keys)
            {
                v.Keys[kv.Key] = new KeyAgg(kv.Value.Count, kv.Value.Swallowed);
            }
            foreach (KeyValuePair<string, AppAgg> kv in day.Apps)
            {
                v.Apps[kv.Key] = kv.Value.Clone();
            }
            foreach (KeyValuePair<int, long> kv in day.Mouse)
            {
                v.Mouse[kv.Key] = kv.Value;
            }
            v.KpmAverage = day.Total / Math.Max(1.0, day.ActiveMinutes);
            if (DayKeys.From(date) == _minuteDay)
            {
                int peak = 0;
                for (int i = 0; i < 1440; i++) if (_minuteCounts[i] > peak) peak = _minuteCounts[i];
                v.KpmPeak = peak;
            }
            return v;
        }

        private RangeView BuildRange(DateTime start, DateTime end)
        {
            RangeView v = new RangeView();
            v.Start = start;
            v.End = end;

            List<DayData> days = new List<DayData>();
            foreach (MonthData md in _months.Values)
            {
                foreach (DayData d in md.Days.Values)
                {
                    DateTime date = DayKeys.ToDate(d.Day);
                    if (date < start || date > end) continue;
                    days.Add(d);
                }
            }
            days.Sort(delegate(DayData a, DayData b) { return a.Day.CompareTo(b.Day); });

            int maxDays = 400;
            v.DailyComplete = days.Count <= maxDays;

            foreach (DayData d in days)
            {
                DateTime date = DayKeys.ToDate(d.Day);
                v.Total += d.Total;
                v.Swallowed += d.Swallowed;
                v.ActiveMinutes += d.ActiveMinutes;
                if (d.Total > 0) v.ActiveDays++;

                int activeHours = 0;
                for (int h = 0; h < 24; h++)
                {
                    v.Hours[h] += d.Hours[h];
                    if (d.Hours[h] > 0) activeHours++;
                }
                v.ActiveHours += activeHours;

                int weekday = ((int)date.DayOfWeek + 6) % 7;
                for (int h = 0; h < 24; h++) v.WeekdayHour[weekday, h] += d.Hours[h];

                foreach (KeyValuePair<int, KeyAgg> kv in d.Keys) MergeKey(v.Keys, kv.Key, kv.Value);
                foreach (KeyValuePair<string, AppAgg> kv in d.Apps) MergeApp(v.Apps, kv.Value);
                MergeMouse(v.Mouse, d.Mouse);

                if (v.DailyComplete)
                {
                    DayPoint p = new DayPoint();
                    p.Date = date;
                    p.Total = d.Total;
                    p.Swallowed = d.Swallowed;
                    v.Daily.Add(p);
                }
            }

            return v;
        }

        private void BuildKeyHealth(StatsSnapshot s)
        {
            DateTime today = DateTime.Today;
            s.KeyTrendStart = today.AddDays(-29);

            HashSet<int> affected = new HashSet<int>();
            foreach (KeyValuePair<int, KeyAgg> kv in s.Range.Keys)
            {
                if (kv.Value.Swallowed > 0) affected.Add(kv.Key);
            }
            if (affected.Count == 0) return;

            Dictionary<int, int> first = new Dictionary<int, int>();
            Dictionary<int, int> last = new Dictionary<int, int>();
            foreach (MonthData md in _months.Values)
            {
                foreach (DayData d in md.Days.Values)
                {
                    DateTime date = DayKeys.ToDate(d.Day);
                    if (date < s.Range.Start || date > s.Range.End) continue;
                    foreach (KeyValuePair<int, KeyAgg> kv in d.Keys)
                    {
                        if (kv.Value.Swallowed <= 0) continue;
                        int f;
                        if (!first.TryGetValue(kv.Key, out f) || d.Day < f) first[kv.Key] = d.Day;
                        int l;
                        if (!last.TryGetValue(kv.Key, out l) || d.Day > l) last[kv.Key] = d.Day;
                    }
                }
            }

            Dictionary<int, int> sw7 = new Dictionary<int, int>();
            Dictionary<int, int> swPrev = new Dictionary<int, int>();
            for (int i = 0; i < 14; i++)
            {
                DateTime date = today.AddDays(-i);
                DayData d = FindDay(date);
                if (d == null) continue;
                foreach (KeyValuePair<int, KeyAgg> kv in d.Keys)
                {
                    if (kv.Value.Swallowed <= 0) continue;
                    Dictionary<int, int> target = i < 7 ? sw7 : swPrev;
                    int cur;
                    target.TryGetValue(kv.Key, out cur);
                    target[kv.Key] = cur + (int)Math.Min(int.MaxValue, kv.Value.Swallowed);
                }
            }

            foreach (int vk in affected)
            {
                KeyAgg agg = s.Range.Keys[vk];
                KeyHealthRow row = new KeyHealthRow();
                row.Vk = vk;
                row.Total = agg.Count;
                row.Swallowed = agg.Swallowed;
                int f;
                first.TryGetValue(vk, out f);
                row.FirstDay = f;
                int l;
                last.TryGetValue(vk, out l);
                row.LastDay = l;
                int a;
                sw7.TryGetValue(vk, out a);
                row.Swallowed7 = a;
                int b;
                swPrev.TryGetValue(vk, out b);
                row.SwallowedPrev7 = b;

                int score = 100 - Math.Min(90, (int)(row.Rate * 100 * 4 + row.Swallowed7 * 1.2));
                row.Score = Math.Max(0, score);
                row.Status = row.Score >= 90 ? 1 : row.Score >= 60 ? 2 : 3;
                s.KeyHealth.Add(row);
            }

            s.KeyHealth.Sort(delegate(KeyHealthRow a, KeyHealthRow b)
            {
                if (a.Score != b.Score) return a.Score.CompareTo(b.Score);
                return b.Swallowed.CompareTo(a.Swallowed);
            });

            HashSet<int> trendKeys = new HashSet<int>(affected);
            for (int i = 0; i < 30; i++)
            {
                DayData d = FindDay(today.AddDays(i - 29));
                if (d == null) continue;
                foreach (KeyValuePair<int, KeyAgg> kv in d.Keys)
                {
                    if (kv.Value.Swallowed > 0) trendKeys.Add(kv.Key);
                }
            }
            foreach (int vk in trendKeys)
            {
                s.KeySwallowTrend[vk] = new int[30];
                s.KeyPressTrend[vk] = new int[30];
            }
            for (int i = 0; i < 30; i++)
            {
                DayData d = FindDay(today.AddDays(i - 29));
                if (d == null) continue;
                foreach (int vk in trendKeys)
                {
                    KeyAgg agg;
                    if (!d.Keys.TryGetValue(vk, out agg)) continue;
                    s.KeySwallowTrend[vk][i] = (int)Math.Min(int.MaxValue, agg.Swallowed);
                    s.KeyPressTrend[vk][i] = (int)Math.Min(int.MaxValue, agg.Count);
                }
            }
        }

        private static void MergeKey(Dictionary<int, KeyAgg> target, int vk, KeyAgg src)
        {
            KeyAgg t;
            if (!target.TryGetValue(vk, out t))
            {
                t = new KeyAgg();
                target[vk] = t;
            }
            t.Count += src.Count;
            t.Swallowed += src.Swallowed;
        }

        private static void MergeApp(Dictionary<string, AppAgg> target, AppAgg src)
        {
            AppAgg t;
            if (!target.TryGetValue(src.Name, out t))
            {
                t = new AppAgg(src.Name);
                target[src.Name] = t;
            }
            t.Total += src.Total;
            t.Swallowed += src.Swallowed;
            for (int i = 0; i < 24; i++) t.Hours[i] += src.Hours[i];
            foreach (KeyValuePair<int, KeyAgg> kv in src.Keys) MergeKey(t.Keys, kv.Key, kv.Value);
            MergeMouse(t.Mouse, src.Mouse);
        }

        private static void AddMouse(Dictionary<int, long> target, int button, long add)
        {
            long cur;
            target.TryGetValue(button, out cur);
            target[button] = cur + add;
        }

        private static void MergeMouse(Dictionary<int, long> target, Dictionary<int, long> source)
        {
            foreach (KeyValuePair<int, long> kv in source) AddMouse(target, kv.Key, kv.Value);
        }

        private DayData FindDay(DateTime date)
        {
            string key = DayKeys.MonthKey(date.Year, date.Month);
            MonthData md;
            if (!_months.TryGetValue(key, out md)) return null;
            DayData day;
            md.Days.TryGetValue(DayKeys.From(date), out day);
            return day;
        }

        private long DayTotal(DateTime date)
        {
            DayData day = FindDay(date);
            return day == null ? 0 : day.Total;
        }

        private DateTime EarliestDay()
        {
            DateTime min = DateTime.MaxValue;
            foreach (MonthData md in _months.Values)
            {
                foreach (int dk in md.Days.Keys)
                {
                    DateTime d = DayKeys.ToDate(dk);
                    if (d < min) min = d;
                }
            }
            if (min == DateTime.MaxValue) return DateTime.Today;
            return min;
        }

        public List<MonthInfo> GetMonths()
        {
            lock (_gate) { return GetMonthsLocked(); }
        }

        private List<MonthInfo> GetMonthsLocked()
        {
            if (_monthCache != null && (DateTime.UtcNow - _monthCacheAt).TotalSeconds < 5) return _monthCache;

            List<MonthInfo> list = new List<MonthInfo>();
            bool currentIncluded = false;

            try
            {
                string[] files = Directory.GetFiles(_dataDir, "*.kds");
                foreach (string file in files)
                {
                    string key = Path.GetFileNameWithoutExtension(file);
                    int y, m;
                    if (!TryParseMonth(key, out y, out m)) continue;

                    MonthInfo info = new MonthInfo();
                    info.Key = key;
                    info.Year = y;
                    info.Month = m;
                    info.Path = file;
                    try { info.Size = new FileInfo(file).Length; } catch { }

                    MonthData md;
                    if (_months.TryGetValue(key, out md))
                    {
                        info.Loaded = true;
                        info.Total = md.Total;
                        info.Swallowed = md.Swallowed;
                    }
                    else
                    {
                        MonthFile.ReadHeader(file, out info.Total, out info.Swallowed);
                    }

                    if (_current != null && _current.Key == key) currentIncluded = true;
                    list.Add(info);
                }
            }
            catch
            {
            }

            if (!currentIncluded && _current != null)
            {
                MonthInfo info = new MonthInfo();
                info.Key = _current.Key;
                info.Year = _current.Year;
                info.Month = _current.Month;
                info.Loaded = true;
                info.Total = _current.Total;
                info.Swallowed = _current.Swallowed;
                info.Path = PathFor(_current.Key);
                try { if (File.Exists(info.Path)) info.Size = new FileInfo(info.Path).Length; } catch { }
                list.Add(info);
            }

            list.Sort(delegate(MonthInfo a, MonthInfo b) { return string.Compare(b.Key, a.Key, StringComparison.Ordinal); });
            _monthCache = list;
            _monthCacheAt = DateTime.UtcNow;
            return list;
        }

        public void DeleteMonth(string key)
        {
            lock (_gate)
            {
                string path = PathFor(key);
                DeleteFile(path);
                DeleteFile(path + ".bak");
                DeleteFile(path + ".tmp");

                MonthData md;
                if (_current != null && _current.Key == key)
                {
                    int y, m;
                    TryParseMonth(key, out y, out m);
                    _current = new MonthData();
                    _current.Year = y;
                    _current.Month = m;
                    _months[key] = _current;
                    Array.Clear(_minuteCounts, 0, _minuteCounts.Length);
                    _minuteDay = 0;
                }
                else if (_months.TryGetValue(key, out md))
                {
                    _months.Remove(key);
                }
            }
            InvalidateMonthCache();
        }

        public void DeleteAllMonths()
        {
            lock (_gate)
            {
                try
                {
                    string[] files = Directory.GetFiles(_dataDir, "*.kds*");
                    foreach (string file in files) DeleteFile(file);
                }
                catch
                {
                }

                _months.Clear();
                _recent.Clear();
                Array.Clear(_minuteCounts, 0, _minuteCounts.Length);
                _minuteDay = 0;

                int y = DateTime.Now.Year;
                int m = DateTime.Now.Month;
                _current = new MonthData();
                _current.Year = y;
                _current.Month = m;
                _months[_current.Key] = _current;
            }
            InvalidateMonthCache();
        }

        public void ExportCsv(string basePath, IList<string> monthKeys)
        {
            lock (_gate)
            {
                List<MonthData> months = new List<MonthData>();
                if (monthKeys == null || monthKeys.Count == 0)
                {
                    foreach (MonthData md in _months.Values) months.Add(md);
                    try
                    {
                        string[] files = Directory.GetFiles(_dataDir, "*.kds");
                        foreach (string file in files)
                        {
                            string key = Path.GetFileNameWithoutExtension(file);
                            int y, m;
                            if (!TryParseMonth(key, out y, out m)) continue;
                            bool exists = false;
                            foreach (MonthData md in months) if (md.Key == key) { exists = true; break; }
                            if (!exists) months.Add(MonthFile.Load(file, y, m));
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    foreach (string key in monthKeys)
                    {
                        MonthData md;
                        if (_months.TryGetValue(key, out md)) { months.Add(md); continue; }
                        int y, m;
                        if (!TryParseMonth(key, out y, out m)) continue;
                        string file = PathFor(key);
                        if (File.Exists(file)) months.Add(MonthFile.Load(file, y, m));
                    }
                }

                months.Sort(delegate(MonthData a, MonthData b) { return string.Compare(a.Key, b.Key, StringComparison.Ordinal); });

                using (StreamWriter w = new StreamWriter(basePath + "-days.csv", false, new UTF8Encoding(true)))
                {
                    w.WriteLine("date,total,swallowed,active_minutes,h0,h1,h2,h3,h4,h5,h6,h7,h8,h9,h10,h11,h12,h13,h14,h15,h16,h17,h18,h19,h20,h21,h22,h23");
                    foreach (MonthData md in months)
                    {
                        List<int> keys = new List<int>(md.Days.Keys);
                        keys.Sort();
                        foreach (int dk in keys)
                        {
                            DayData d = md.Days[dk];
                            w.Write(DayKeys.ToText(dk));
                            w.Write(',');
                            w.Write(d.Total);
                            w.Write(',');
                            w.Write(d.Swallowed);
                            w.Write(',');
                            w.Write(d.ActiveMinutes);
                            for (int h = 0; h < 24; h++) { w.Write(','); w.Write(d.Hours[h]); }
                            w.WriteLine();
                        }
                    }
                }

                using (StreamWriter w = new StreamWriter(basePath + "-apps.csv", false, new UTF8Encoding(true)))
                {
                    w.WriteLine("date,app,total,swallowed,h0,h1,h2,h3,h4,h5,h6,h7,h8,h9,h10,h11,h12,h13,h14,h15,h16,h17,h18,h19,h20,h21,h22,h23");
                    foreach (MonthData md in months)
                    {
                        List<int> keys = new List<int>(md.Days.Keys);
                        keys.Sort();
                        foreach (int dk in keys)
                        {
                            DayData d = md.Days[dk];
                            List<string> apps = new List<string>(d.Apps.Keys);
                            apps.Sort(StringComparer.OrdinalIgnoreCase);
                            foreach (string app in apps)
                            {
                                AppAgg a = d.Apps[app];
                                w.Write(DayKeys.ToText(dk));
                                w.Write(',');
                                w.Write(Csv(app));
                                w.Write(',');
                                w.Write(a.Total);
                                w.Write(',');
                                w.Write(a.Swallowed);
                                for (int h = 0; h < 24; h++) { w.Write(','); w.Write(a.Hours[h]); }
                                w.WriteLine();
                            }
                        }
                    }
                }

                using (StreamWriter w = new StreamWriter(basePath + "-keys.csv", false, new UTF8Encoding(true)))
                {
                    w.WriteLine("date,app,vk,vk_hex,count,swallowed");
                    foreach (MonthData md in months)
                    {
                        List<int> keys = new List<int>(md.Days.Keys);
                        keys.Sort();
                        foreach (int dk in keys)
                        {
                            DayData d = md.Days[dk];
                            List<int> vks = new List<int>(d.Keys.Keys);
                            vks.Sort();
                            foreach (int vk in vks)
                            {
                                KeyAgg k = d.Keys[vk];
                                w.Write(DayKeys.ToText(dk));
                                w.Write(",,");
                                w.Write(vk);
                                w.Write(",0x");
                                w.Write(vk.ToString("X2"));
                                w.Write(',');
                                w.Write(k.Count);
                                w.Write(',');
                                w.WriteLine(k.Swallowed);
                            }
                            List<string> apps = new List<string>(d.Apps.Keys);
                            apps.Sort(StringComparer.OrdinalIgnoreCase);
                            foreach (string app in apps)
                            {
                                AppAgg a = d.Apps[app];
                                List<int> ak = new List<int>(a.Keys.Keys);
                                ak.Sort();
                                foreach (int vk in ak)
                                {
                                    KeyAgg k = a.Keys[vk];
                                    w.Write(DayKeys.ToText(dk));
                                    w.Write(',');
                                    w.Write(Csv(app));
                                    w.Write(',');
                                    w.Write(vk);
                                    w.Write(",0x");
                                    w.Write(vk.ToString("X2"));
                                    w.Write(',');
                                    w.Write(k.Count);
                                    w.Write(',');
                                    w.WriteLine(k.Swallowed);
                                }
                            }
                        }
                    }
                }

                using (StreamWriter w = new StreamWriter(basePath + "-mouse.csv", false, new UTF8Encoding(true)))
                {
                    w.WriteLine("date,app,button,count");
                    foreach (MonthData md in months)
                    {
                        List<int> keys = new List<int>(md.Days.Keys);
                        keys.Sort();
                        foreach (int dk in keys)
                        {
                            DayData d = md.Days[dk];
                            List<int> buttons = new List<int>(d.Mouse.Keys);
                            buttons.Sort();
                            foreach (int button in buttons)
                            {
                                w.Write(DayKeys.ToText(dk));
                                w.Write(",,");
                                w.Write(button);
                                w.Write(',');
                                w.WriteLine(d.Mouse[button]);
                            }
                            List<string> apps = new List<string>(d.Apps.Keys);
                            apps.Sort(StringComparer.OrdinalIgnoreCase);
                            foreach (string app in apps)
                            {
                                AppAgg a = d.Apps[app];
                                List<int> ab = new List<int>(a.Mouse.Keys);
                                ab.Sort();
                                foreach (int button in ab)
                                {
                                    w.Write(DayKeys.ToText(dk));
                                    w.Write(',');
                                    w.Write(Csv(app));
                                    w.Write(',');
                                    w.Write(button);
                                    w.Write(',');
                                    w.WriteLine(a.Mouse[button]);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static string Csv(string text)
        {
            if (text == null) return "";
            if (text.IndexOf(',') >= 0 || text.IndexOf('"') >= 0 || text.IndexOf('\n') >= 0)
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return text;
        }

        private static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private void InvalidateMonthCache()
        {
            _monthCache = null;
            _monthCacheAt = DateTime.MinValue;
        }

        private string PathFor(string key)
        {
            return Path.Combine(_dataDir, key + ".kds");
        }

        private static bool TryParseMonth(string key, out int year, out int month)
        {
            year = 0;
            month = 0;
            if (key == null || key.Length != 7 || key[4] != '-') return false;
            return int.TryParse(key.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
                && int.TryParse(key.Substring(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
                && month >= 1 && month <= 12;
        }

        private static long ToUnixSeconds(DateTime at)
        {
            return (long)(at.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static string NormalizeApp(string app)
        {
            if (string.IsNullOrEmpty(app)) return "unknown";
            string s = app.Trim();
            int slash = s.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0 && slash + 1 < s.Length) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            s = s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            if (s.Length > 64) s = s.Substring(0, 64);
            if (s.Length == 0) return "unknown";
            return s.ToLowerInvariant();
        }

        private static DayData GetDay(MonthData md, int dayKey)
        {
            DayData day;
            if (!md.Days.TryGetValue(dayKey, out day))
            {
                day = new DayData();
                day.Day = dayKey;
                md.Days[dayKey] = day;
            }
            return day;
        }

        private static AppAgg GetApp(DayData day, string app)
        {
            AppAgg agg;
            if (!day.Apps.TryGetValue(app, out agg))
            {
                agg = new AppAgg(app);
                day.Apps[app] = agg;
            }
            return agg;
        }

        private static KeyAgg GetKey(DayData day, int vk)
        {
            KeyAgg k;
            if (!day.Keys.TryGetValue(vk, out k))
            {
                k = new KeyAgg();
                day.Keys[vk] = k;
            }
            return k;
        }

        private static KeyAgg GetKey(AppAgg app, int vk)
        {
            KeyAgg k;
            if (!app.Keys.TryGetValue(vk, out k))
            {
                k = new KeyAgg();
                app.Keys[vk] = k;
            }
            return k;
        }

        private void LogError(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dataDir, "stats-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\r\n");
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            if (_worker != null)
            {
                _worker.Join(3000);
                _worker = null;
            }
            _idle.Set();
        }
    }
}
