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
        // P1-02/P2-01：日 -> (vk -> 间隔直方图)；随月份保存到诊断侧车
        private readonly Dictionary<int, Dictionary<int, SwallowGapHist>> _gapHist =
            new Dictionary<int, Dictionary<int, SwallowGapHist>>();
        // P2-04：快捷键聚合（组合名 -> 次数），内存聚合 + 侧车持久化
        private readonly Dictionary<string, long> _shortcutCounts = new Dictionary<string, long>();

        /// <summary>诊断数据（拦截间隔直方图）采集开关：关闭后不再记录候选间隔聚合（V2 侧车）。</summary>
        public volatile bool DiagnosticsEnabled = true;
        private int _applyCounter;

        /// <summary>当前事件队列积压深度（诊断用）。</summary>
        public int PendingEvents { get { return _queue.Count; } }

        /// <summary>数据版本（每次 Apply 递增；UI 用于节流/过期判断）。</summary>
        public long DataVersion { get { return Interlocked.Read(ref _version); } }

        private static double TicksToMsLocal(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private List<MonthInfo> _monthCache;
        private DateTime _monthCacheAt = DateTime.MinValue;

        // PR-1：异步快照请求（合并 + generation + 缓存）。构建在 worker 线程完成，
        // UI 线程不再同步执行完整 GetSnapshot。
        private readonly object _reqGate = new object();
        private bool _reqPending;
        private DateTime _reqStart;
        private DateTime _reqEnd;
        private long _reqGeneration;
        private long _reqCounter;
        private readonly Dictionary<string, StatsSnapshot> _snapCache =
            new Dictionary<string, StatsSnapshot>(StringComparer.Ordinal);
        private readonly List<string> _snapCacheOrder = new List<string>();

        /// <summary>快照构建完成回调（worker 线程调用；UI 侧需 Post 回 UI 线程）。</summary>
        public Action<StatsSnapshot, long> SnapshotBuilt;

        public string DataDirectory { get { return _dataDir; } }

        public StatsEngine(string dataDir, string selfApp)
        {
            _dataDir = dataDir;
            _selfApp = NormalizeApp(selfApp);
            if (_selfApp == "unknown") _selfApp = "";

            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);

            LoadCurrentMonth(DateTime.Now);
            LoadSidecars(DateTime.Now);

            _worker = new Thread(Worker);
            _worker.IsBackground = true;
            _worker.Name = "KeyInsightStats";
            _worker.Start();

            ThreadPool.QueueUserWorkItem(delegate { LoadAllMonths(); });
        }

        /// <summary>加载 V2 侧车（诊断直方图 + 快捷键聚合）。KDS1 永远是唯一权威统计源。</summary>
        private void LoadSidecars(DateTime at)
        {
            // 诊断直方图按月分文件；全部加载（仅聚合摘要，体量有界），供跨重启的 30 天模拟
            try
            {
                string diagDir = Path.Combine(_dataDir, "diagnostics");
                if (Directory.Exists(diagDir))
                {
                    foreach (string file in Directory.GetFiles(diagDir, "kdiag-*.jsonl"))
                    {
                        string monthKey = Path.GetFileNameWithoutExtension(file).Substring("kdiag-".Length);
                        Dictionary<int, Dictionary<int, SwallowGapHist>> loaded =
                            DiagnosticsSidecar.TryLoad(diagDir, monthKey);
                        if (loaded == null) continue;
                        foreach (KeyValuePair<int, Dictionary<int, SwallowGapHist>> kv in loaded)
                        {
                            _gapHist[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            catch
            {
            }
            Dictionary<string, long> shortcuts = ShortcutStore.TryLoad(Path.Combine(_dataDir, "v2"));
            foreach (KeyValuePair<string, long> kv in shortcuts)
            {
                _shortcutCounts[kv.Key] = kv.Value;
            }
        }

        /// <summary>把当前月份的 V2 侧车落盘（用量 + 诊断 + 快捷键）。统计主链路失败不影响侧车。</summary>
        private void FlushSidecars(MonthData md)
        {
            if (md == null) return;
            UsageSidecar.Save(Path.Combine(_dataDir, "v2"), md);
            DiagnosticsSidecar.Save(Path.Combine(_dataDir, "diagnostics"), md.Key, _gapHist);
            ShortcutStore.Save(Path.Combine(_dataDir, "v2"), _shortcutCounts);
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
                TryBuildRequestedSnapshot();
                if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= 60) SaveCurrent();
            }
            Drain();
            TryBuildRequestedSnapshot();
            SaveCurrent();
        }

        /// <summary>
        /// 请求异步快照（PR-1）：只保留最新请求（合并），返回本次 generation。
        /// 若已有同 key 缓存（范围 + 数据版本一致）则直接复用；结果经 SnapshotBuilt 回调。
        /// </summary>
        public long RequestSnapshot(DateTime start, DateTime end)
        {
            UiMetrics.CountSnapshotRequest();
            long gen;
            lock (_reqGate)
            {
                if (_reqPending) UiMetrics.CountSnapshotCoalesced();
                _reqPending = true;
                _reqStart = start;
                _reqEnd = end;
                gen = ++_reqCounter;
                _reqGeneration = gen;
            }
            _signal.Set();
            return gen;
        }

        private void TryBuildRequestedSnapshot()
        {
            DateTime start, end;
            long gen;
            lock (_reqGate)
            {
                if (!_reqPending) return;
                _reqPending = false;
                start = _reqStart;
                end = _reqEnd;
                gen = _reqGeneration;
            }

            StatsSnapshot snap;
            long version = DataVersion;
            string key = start.Ticks.ToString() + "|" + end.Ticks.ToString() + "|" + version.ToString();
            if (_snapCache.TryGetValue(key, out snap))
            {
                Action<StatsSnapshot, long> cbHit = SnapshotBuilt;
                if (cbHit != null)
                {
                    try { cbHit(snap, gen); } catch { }
                }
                return;
            }

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                snap = GetSnapshot(start, end);
            }
            catch
            {
                return;
            }
            sw.Stop();
            UiMetrics.RecordSnapshot(sw.Elapsed.TotalMilliseconds);

            _snapCache[key] = snap;
            _snapCacheOrder.Add(key);
            if (_snapCacheOrder.Count > 8)
            {
                _snapCache.Remove(_snapCacheOrder[0]);
                _snapCacheOrder.RemoveAt(0);
            }

            Action<StatsSnapshot, long> cb = SnapshotBuilt;
            if (cb != null)
            {
                try { cb(snap, gen); } catch { }
            }
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

            bool sample = (Interlocked.Increment(ref _applyCounter) & 31) == 0;
            long t0 = sample ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            lock (_gate)
            {
                long t1 = sample ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                try
                {
                    ApplyLocked(e, app);
                }
                finally
                {
                    if (sample)
                    {
                        UiMetrics.RecordLockWait(TicksToMsLocal(t1 - t0));
                        UiMetrics.RecordLockHold(TicksToMsLocal(System.Diagnostics.Stopwatch.GetTimestamp() - t1));
                    }
                }
            }
        }

        private void ApplyLocked(KeyEvent e, string app)
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

                    // P1-02/P2-01：按日按 VK 的间隔直方图（仅聚合摘要；短间隔候选才是诊断关注点，
                    // 但全量直方图对阈值模拟更有用，保留全部桶）。诊断开关关闭时不采集。
                    if (!DiagnosticsEnabled)
                    {
                        _version++;
                        return;
                    }
                    Dictionary<int, SwallowGapHist> dayHist;
                    if (!_gapHist.TryGetValue(dayKey, out dayHist))
                    {
                        dayHist = new Dictionary<int, SwallowGapHist>();
                        _gapHist[dayKey] = dayHist;
                    }
                    SwallowGapHist vkHist;
                    if (!dayHist.TryGetValue(e.Vk, out vkHist))
                    {
                        vkHist = new SwallowGapHist();
                        dayHist[e.Vk] = vkHist;
                    }
                    vkHist.Add(e.GapDown, e.GapShortUp);
                }
                else if (e.Type == KeyEventType.Shortcut)
                {
                    // P2-04：仅聚合计数，不进 KDS1、不产生键序列记录
                    string combo = e.App;
                    if (!string.IsNullOrEmpty(combo) && combo != "unknown")
                    {
                        long cur;
                        _shortcutCounts.TryGetValue(combo, out cur);
                        _shortcutCounts[combo] = cur + 1;
                    }
                }
                else if (e.Type == KeyEventType.WheelRaw)
                {
                    // P2-03：原始滚轮 delta（可为负）；仅内存 + 侧车，不入 KDS1
                    day.RawWheelDelta += e.Delta;
                    day.RawWheelEvents++;
                }
                else
                {
                    if (e.Button > 0)
                    {
                        long add = e.Type == KeyEventType.MouseWheel ? Math.Max(1, e.Delta) : 1;
                        AddMouse(day.Mouse, e.Button, add);
                        AppAgg agg = GetApp(day, app);
                        AddMouse(agg.Mouse, e.Button, add);
                        // V2 口径：鼠标活跃分钟（仅内存位图 + 侧车，不入 KDS1）
                        if (!day.MouseMinutes[minute])
                        {
                            day.MouseMinutes[minute] = true;
                        }
                    }
                }

                _version++;
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

        /// <summary>把侧车里的鼠标分钟位图回填到已加载的月数据（KDS1 不含该口径）。</summary>
        private void ApplyMouseMinutes(MonthData md)
        {
            if (md == null) return;
            Dictionary<int, bool[]> mouseMinutes = UsageSidecar.TryLoadMouseMinutes(
                Path.Combine(_dataDir, "v2"), md.Key);
            if (mouseMinutes == null) return;
            foreach (KeyValuePair<int, bool[]> kv in mouseMinutes)
            {
                DayData day;
                if (md.Days.TryGetValue(kv.Key, out day))
                {
                    Array.Copy(kv.Value, day.MouseMinutes, 1440);
                }
            }
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
                        ApplyMouseMinutes(md);
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
                            MonthData loaded = MonthFile.Load(file, y, m);
                            ApplyMouseMinutes(loaded);
                            _months[key] = loaded;
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
                    FlushSidecars(_current);
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
                    FlushSidecars(md);
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
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_gate)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                UiMetrics.RecordLockWait(TicksToMsLocal(t1 - t0));
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

                // V2 附加口径：30 天拦截间隔直方图（P2-01 模拟输入）、综合活跃分钟、快捷键 Top
                DateTime histStart = today.AddDays(-29);
                foreach (KeyValuePair<int, Dictionary<int, SwallowGapHist>> kv in _gapHist)
                {
                    DateTime d = DayKeys.ToDate(kv.Key);
                    if (d < histStart || d > today) continue;
                    foreach (KeyValuePair<int, SwallowGapHist> vk in kv.Value)
                    {
                        SwallowGapHist agg;
                        if (!s.GapHistograms.TryGetValue(vk.Key, out agg))
                        {
                            agg = new SwallowGapHist();
                            s.GapHistograms[vk.Key] = agg;
                        }
                        for (int b = 0; b < SwallowGapHist.BucketCount; b++)
                        {
                            agg.GapDown[b] += vk.Value.GapDown[b];
                            agg.GapShortUp[b] += vk.Value.GapShortUp[b];
                        }
                        agg.Count += vk.Value.Count;
                    }
                }

                int combined = 0;
                foreach (MonthData md in _months.Values)
                {
                    foreach (DayData dd in md.Days.Values)
                    {
                        DateTime d = DayKeys.ToDate(dd.Day);
                        if (d >= rangeStart && d <= rangeEnd)
                        {
                            combined += ActiveMinutesCalculator.CombinedCount(dd);
                        }
                    }
                }
                s.CombinedActiveMinutes = combined;

                long rawDelta = 0;
                long rawEvents = 0;
                foreach (MonthData md in _months.Values)
                {
                    foreach (DayData dd in md.Days.Values)
                    {
                        DateTime d = DayKeys.ToDate(dd.Day);
                        if (d >= rangeStart && d <= rangeEnd)
                        {
                            rawDelta += dd.RawWheelDelta;
                            rawEvents += dd.RawWheelEvents;
                        }
                    }
                }
                s.RawWheelDeltaTotal = rawDelta;
                s.RawWheelEventsTotal = rawEvents;

                List<string> comboKeys = new List<string>(_shortcutCounts.Keys);
                comboKeys.Sort(delegate(string a, string b)
                {
                    long ca = _shortcutCounts[a];
                    long cb = _shortcutCounts[b];
                    if (ca != cb) return cb.CompareTo(ca);
                    return string.CompareOrdinal(a, b);
                });
                for (int i = 0; i < comboKeys.Count && s.TopShortcuts.Count < 10; i++)
                {
                    s.TopShortcuts.Add(new KeyValuePair<string, long>(comboKeys[i], _shortcutCounts[comboKeys[i]]));
                }

                long nowSec = ToUnixSeconds(DateTime.Now);
                long sum = 0;
                for (int i = 0; i < 60; i++)
                {
                    if (_secStamp[i] > 0 && nowSec - _secStamp[i] < 60) sum += _secCount[i];
                }
                s.KpmNow = sum;
                UiMetrics.RecordLockHold(TicksToMsLocal(System.Diagnostics.Stopwatch.GetTimestamp() - t1));
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
                // STO-015：侧车与主数据同生共死，删除月份时同步清理
                DeleteFile(Path.Combine(_dataDir, "v2", "usage-" + key + ".jsonl"));
                DeleteFile(Path.Combine(_dataDir, "diagnostics", "kdiag-" + key + ".jsonl"));
                RemoveGapHistForMonth(key);

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
                    // STO-015：清空全部数据时诊断/用量侧车一并清除
                    string v2Dir = Path.Combine(_dataDir, "v2");
                    if (Directory.Exists(v2Dir))
                    {
                        foreach (string f in Directory.GetFiles(v2Dir, "usage-*.jsonl")) DeleteFile(f);
                    }
                    string diagDir = Path.Combine(_dataDir, "diagnostics");
                    if (Directory.Exists(diagDir))
                    {
                        foreach (string f in Directory.GetFiles(diagDir, "kdiag-*.jsonl")) DeleteFile(f);
                    }
                }
                catch
                {
                }
                _gapHist.Clear();

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

        private void RemoveGapHistForMonth(string monthKey)
        {
            List<int> remove = new List<int>();
            foreach (int dayKey in _gapHist.Keys)
            {
                int y = dayKey / 10000;
                int m = (dayKey / 100) % 100;
                if (DayKeys.MonthKey(y, m) == monthKey) remove.Add(dayKey);
            }
            for (int i = 0; i < remove.Count; i++) _gapHist.Remove(remove[i]);
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
