using System;
using System.Collections.Generic;
using KeyInsight.Core.Policy;

namespace KeyInsight.Stats
{
    public sealed class KeyProfileView
    {
        public int Vk;
        public string Label;
        // 使用（30 天窗口）
        public long ObservedDown;
        public long SuppressedDown;
        public double Rate;
        // 诊断（LEG 旧口径，标注“基于拦截”）
        public int LegacyScore;
        public int LegacyStatus;
        public int Swallowed7;
        public int SwallowedPrev7;
        public bool InsufficientData;
        // V2 诊断（基于直方图的候选观察）
        public long CandidateEvents30d;
        public bool HasGapHistogram;
        // 策略
        public bool PerKeyEnabled;
        public int PerKeyThresholdMs;
        public PolicyMode Mode;
        // 完整性
        public string Completeness = "complete";
    }

    /// <summary>
    /// 按键画像投影（P1-02）：把 StatsSnapshot + KeyPolicyStore 组装成单一按键视图。
    /// 只读查询投影，不持有可变状态；旧健康分明确标注“基于拦截”，样本不足时显式标注。
    /// </summary>
    public static class KeyProfileProjector
    {
        public static KeyProfileView Project(int vk, StatsSnapshot snap, KeyPolicyStore store, PolicyMode mode)
        {
            KeyProfileView v = new KeyProfileView();
            v.Vk = vk;
            v.Label = UI.KeyLayout.LabelFor(vk);
            v.Mode = mode;

            KeyAgg agg;
            if (snap.Range.Keys.TryGetValue(vk, out agg))
            {
                v.ObservedDown = agg.Count;
                v.SuppressedDown = agg.Swallowed;
                v.Rate = agg.Swallowed / (double)Math.Max(1, agg.Count + agg.Swallowed);
            }

            foreach (KeyHealthRow row in snap.KeyHealth)
            {
                if (row.Vk == vk)
                {
                    v.LegacyScore = row.Score;
                    v.LegacyStatus = row.Status;
                    v.Swallowed7 = row.Swallowed7;
                    v.SwallowedPrev7 = row.SwallowedPrev7;
                    break;
                }
            }
            if (v.ObservedDown + v.SuppressedDown < 50)
            {
                v.InsufficientData = true;
                v.Completeness = "insufficient-sample";
            }

            SwallowGapHist hist;
            if (snap.GapHistograms.TryGetValue(vk, out hist))
            {
                v.HasGapHistogram = true;
                v.CandidateEvents30d = hist.Count;
            }

            if (store != null)
            {
                int scan = Core.Input.InputIdentityResolver.CanonicalScanFor(vk);
                KeyPolicyEntry e = store.Find(vk, scan, false);
                if (e == null) e = store.FindByVirtualKey(vk);
                if (e != null)
                {
                    v.PerKeyEnabled = e.Enabled;
                    v.PerKeyThresholdMs = e.ThresholdMs;
                }
            }
            return v;
        }
    }
}
