using System;
using System.Collections.Generic;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 帝王在位轨迹的一个采样点（2026-10-24 历代天命接入 第2步）。
    /// 存在的理由：只看即位/退位两点会抹掉中间的高光——
    /// 某帝在位 29 年，第 18 年极盛、第 28 年崩坏，两点对比会把他记成一直平庸。
    /// </summary>
    public sealed class EmperorSample
    {
        /// <summary>即位第 N 年（1 = 即位当年）。</summary>
        public int Year { get; set; }
        public int Cities { get; set; }
        public int Population { get; set; }
        public long Treasury { get; set; }
        public float Mandate { get; set; }
        public float Stability { get; set; }
        public int Vassals { get; set; }
        /// <summary>世界国力排名（1=最强；-1=未采集）。</summary>
        public int Rank { get; set; } = -1;

        /// <summary>国力点：与天下总览同口径（城数×6 + 人口÷40）。</summary>
        public int PowerScore => PowerScoreOf(Cities, Population);

        public static int PowerScoreOf(int cities, int population)
        {
            if (cities < 0) cities = 0;
            if (population < 0) population = 0;
            return cities * 6 + population / 40;
        }
    }

    /// <summary>
    /// 在位轨迹采样与形态识别（纯函数，不读世界、不写状态，可完全单测）。
    ///
    /// 采样规则：
    ///   · 即位、退位强制采一次（首尾必有）
    ///   · 期间按间隔采：currentYear - lastYear >= interval
    ///   · interval &lt;= 0 表示关闭采样（退回只看首尾）
    /// </summary>
    public static class EmperorTrackSampler
    {
        /// <summary>是否需要采样。</summary>
        public static bool ShouldSample(IList<EmperorSample> track, int currentYear, int interval)
        {
            if (interval <= 0) return false;
            if (track == null || track.Count == 0) return true;      // 首点必采
            int last = track[track.Count - 1].Year;
            return currentYear - last >= interval;
        }

        /// <summary>
        /// 追加采样点。同年重复调用是幂等的（保留首个），返回是否真的追加。
        /// </summary>
        public static bool AddSample(IList<EmperorSample> track, EmperorSample sample)
        {
            if (track == null || sample == null) return false;
            for (int i = 0; i < track.Count; i++)
            {
                if (track[i].Year == sample.Year) return false;      // 幂等
                if (track[i].Year > sample.Year)
                {
                    track.Insert(i, sample);                          // 保持按年升序
                    return true;
                }
            }
            track.Add(sample);
            return true;
        }

        /// <summary>极盛之年（国力点最大的样本）；空轨迹返回 null。</summary>
        public static EmperorSample Peak(IList<EmperorSample> track)
        {
            if (track == null || track.Count == 0) return null;
            EmperorSample best = track[0];
            for (int i = 1; i < track.Count; i++)
            {
                if (track[i].PowerScore > best.PowerScore) best = track[i];
            }
            return best;
        }

        /// <summary>
        /// 形态识别。轨迹不足两点时返回 Unknown（调用方用首尾两点兜底判断）。
        /// </summary>
        public static EmperorShape Shape(IList<EmperorSample> track)
        {
            if (track == null || track.Count < 2) return EmperorShape.Unknown;

            var first = track[0];
            var last = track[track.Count - 1];
            var peak = Peak(track);
            if (peak == null) return EmperorShape.Unknown;

            int firstScore = first.PowerScore;
            int lastScore = last.PowerScore;
            int peakScore = peak.PowerScore;

            // 峰值即末年 → 一路向上
            if (peak.Year == last.Year && peakScore > firstScore)
                return EmperorShape.Rising;

            // 峰值即首年 → 一路下滑
            if (peak.Year == first.Year && peakScore > lastScore)
                return EmperorShape.Falling;

            // 峰在中段，末年较峰值明显回落 → 先升后降
            if (peak.Year != first.Year && peak.Year != last.Year && peakScore > 0
                && lastScore <= peakScore * 0.7)
                return EmperorShape.RiseThenFall;

            // 其余：末端较首端涨了两成以上算向上，否则平稳
            if (firstScore > 0 && lastScore >= firstScore * 1.2)
                return EmperorShape.Rising;

            return EmperorShape.Flat;
        }

        /// <summary>
        /// 两点兜底形态（旧档无轨迹时用首尾快照推断）。
        /// </summary>
        public static EmperorShape ShapeFromEndpoints(int startScore, int endScore)
        {
            if (startScore <= 0 && endScore <= 0) return EmperorShape.Unknown;
            if (endScore >= startScore * 1.2) return EmperorShape.Rising;
            if (startScore > 0 && endScore <= startScore * 0.7) return EmperorShape.Falling;
            return EmperorShape.Flat;
        }

        /// <summary>
        /// 给 LLM 的轨迹摘要：最多 keep 个点（超出抽稀，保留首尾与峰值），控制 token。
        /// </summary>
        public static List<EmperorSample> Condense(IList<EmperorSample> track, int keep = 8)
        {
            var result = new List<EmperorSample>();
            if (track == null || track.Count == 0) return result;
            if (track.Count <= keep)
            {
                result.AddRange(track);
                return result;
            }

            var peak = Peak(track);
            var picked = new HashSet<int> { 0, track.Count - 1 };
            for (int i = 0; i < track.Count; i++)
                if (ReferenceEquals(track[i], peak)) picked.Add(i);

            // 均匀补齐剩余名额
            int remain = keep - picked.Count;
            if (remain > 0)
            {
                double step = (track.Count - 1) / (double)(remain + 1);
                for (int k = 1; k <= remain; k++)
                {
                    int idx = (int)Math.Round(k * step);
                    if (idx > 0 && idx < track.Count - 1) picked.Add(idx);
                }
            }

            var sorted = new List<int>(picked);
            sorted.Sort();
            foreach (var i in sorted) result.Add(track[i]);
            return result;
        }
    }
}
