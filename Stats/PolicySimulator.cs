using System;
using System.Collections.Generic;

namespace KeyInsight.Stats
{
    public sealed class SimulationRow
    {
        public int ThresholdMs;
        public long EstimatedFiltered;   // 预计被过滤事件数（来自直方图桶）
        public long TotalCandidateEvents;
        public double SharePercent;      // 占候选事件比例
        public string RiskNote = "";
    }

    public sealed class SimulationResult
    {
        public int Vk;
        public string Label;
        public bool HasData;
        public List<SimulationRow> Rows = new List<SimulationRow>();
        public int RecommendedThresholdMs = -1;
        public string Note = "";
    }

    /// <summary>
    /// 离线策略模拟（P2-01）：用 30 天拦截间隔直方图，估算不同阈值下的过滤事件量。
    /// 输出为预测（“如果改用 X ms，历史上 Y% 的拦截事件会被过滤”），明确不等于真实误拦率；
    /// 绝不自动写配置，只给建议，由用户确认（ADR-008）。
    /// </summary>
    public static class PolicySimulator
    {
        private static readonly int[] Candidates = new int[] { 10, 15, 20, 25, 30, 40 };

        public static SimulationResult Simulate(int vk, StatsSnapshot snap)
        {
            SimulationResult r = new SimulationResult();
            r.Vk = vk;
            r.Label = UI.KeyLayout.LabelFor(vk);

            SwallowGapHist hist;
            if (snap == null || !snap.GapHistograms.TryGetValue(vk, out hist) || hist.Count == 0)
            {
                r.Note = "数据不足：该键在最近 30 天没有拦截间隔样本，无法模拟。";
                return r;
            }
            r.HasData = true;

            // 桶边界（毫秒）：0,1,2,5,10,15,20,25,30,40,60,∞ —— 间隔 <= 阈值的事件会被过滤（严格 < 语义取保守近似）
            long total = hist.Count;
            long cumulative = 0;
            int bi = 0;
            foreach (int thr in Candidates)
            {
                while (bi < SwallowGapHist.BucketCount && SwallowGapHist.BucketUpper(bi) <= thr)
                {
                    cumulative += hist.GapDown[bi];
                    bi++;
                }
                SimulationRow row = new SimulationRow();
                row.ThresholdMs = thr;
                row.TotalCandidateEvents = total;
                row.EstimatedFiltered = cumulative;
                row.SharePercent = total > 0 ? cumulative * 100.0 / total : 0;
                if (thr >= 30) row.RiskNote = "较高误拦风险：合法快速连按也可能落窗内";
                else if (thr <= 10) row.RiskNote = "较保守：可能漏掉部分抖动";
                r.Rows.Add(row);
            }

            // 推荐：过滤 ≥70% 候选的最小阈值（经验起点，需专用测试确认）
            foreach (SimulationRow row in r.Rows)
            {
                if (row.SharePercent >= 70)
                {
                    r.RecommendedThresholdMs = row.ThresholdMs;
                    break;
                }
            }
            if (r.RecommendedThresholdMs < 0) r.RecommendedThresholdMs = 20;
            r.Note = "预测基于最近 30 天拦截间隔直方图（聚合摘要），不等于真实误拦率；应用前建议先做专用测试。";
            return r;
        }
    }
}
