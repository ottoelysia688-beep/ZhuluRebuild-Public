using System.Collections.Generic;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 在位轨迹采样契约守护（历代天命接入 第2步）。
    ///
    /// 核心是那个"29 年在位"的场景：只看首尾会判成平庸，
    /// 有了轨迹才能识别「先升后降」，进而定出「景」并写出晚节之叹。
    /// </summary>
    public static class EmperorTrackTests
    {
        private static EmperorSample S(int year, int cities, int population)
        {
            return new EmperorSample
            {
                Year = year,
                Cities = cities,
                Population = population,
                Treasury = 0,
                Mandate = 50f,
                Stability = 60f,
                Vassals = 0
            };
        }

        public static void Run()
        {
            // ── 0a. 轨迹序列化往返（LlmV2-0：此前 Track 不进快照，存档即丢）──
            var track0 = new List<EmperorSample>
            {
                new EmperorSample { Year = 1, Cities = 5, Population = 1200, Treasury = 300, Mandate = 55.5f, Stability = 60.2f, Vassals = 2, Rank = 3 },
                new EmperorSample { Year = 6, Cities = 9, Population = 2600, Treasury = -50, Mandate = 66f, Stability = 71.9f, Vassals = 0, Rank = 1 },
                new EmperorSample { Year = 11, Cities = 4, Population = 1100, Treasury = 0, Mandate = 30f, Stability = 40f, Vassals = 0, Rank = -1 }
            };
            string enc = OrthodoxMandateManager.SerializeTrack(track0);
            TestRunner.AssertTrue(!enc.Contains(";") && !enc.Contains(","), "轨迹串不含行内分隔符（;,）");
            var back = OrthodoxMandateManager.ParseTrack(enc);
            TestRunner.AssertEqual(3, back.Count, "轨迹往返条数一致");
            TestRunner.AssertEqual(6, back[1].Year, "往返年份");
            TestRunner.AssertEqual(9, back[1].Cities, "往返城数");
            TestRunner.AssertEqual(-50L, back[1].Treasury, "往返国库（负数）");
            TestRunner.AssertEqual(66f, back[1].Mandate, "往返天命");
            TestRunner.AssertEqual(1, back[1].Rank, "往返排名");
            TestRunner.AssertEqual(-1, back[2].Rank, "未采集排名保持 -1");
            TestRunner.AssertEqual(0, OrthodoxMandateManager.ParseTrack("").Count, "空串→空轨迹（旧档兼容）");
            TestRunner.AssertEqual("", OrthodoxMandateManager.SerializeTrack(null), "空轨迹→空串");
            // 新皇帝行 25 段（23 旧段 + PeakYear + Track），旧 23 段行解析不受影响
            TestRunner.AssertTrue(enc.Split('/').Length == 3, "样本间以 / 分隔");

            // ── 0b. 详情页柱状：抽稀与归一化（LlmV2-2）──
            var big = new List<EmperorSample>();
            for (int i = 0; i < 85; i++)
                big.Add(S(425 + i, 3 + i / 9, 149 + i * 9));
            var thin = ZhuluRebuild.UI.Windows.ZhuluMandateWindow.ThinTrack(big, 40);
            TestRunner.AssertTrue(thin.Count <= 40 && thin.Count >= 3, "85 样本抽稀至 ≤40");
            TestRunner.AssertEqual(425, thin[0].Year, "抽稀保首年");
            TestRunner.AssertEqual(425 + 84, thin[thin.Count - 1].Year, "抽稀保末年");
            int peakOrigin = 0; int peakScore = -1;
            for (int i = 0; i < big.Count; i++)
                if (big[i].PowerScore > peakScore) { peakScore = big[i].PowerScore; peakOrigin = big[i].Year; }
            bool peakKept = false;
            foreach (var s in thin) if (s.Year == peakOrigin) { peakKept = true; break; }
            TestRunner.AssertTrue(peakKept, "抽稀保留极盛样本");
            TestRunner.AssertEqual(big.Count, ZhuluRebuild.UI.Windows.ZhuluMandateWindow.ThinTrack(big, 100).Count,
                "不超上限不抽稀");

            int pi;
            float[] bars = ZhuluRebuild.UI.Windows.ZhuluMandateWindow.NormalizeBars(thin, out pi);
            TestRunner.AssertEqual(thin.Count, bars.Length, "归一化柱数一致");
            TestRunner.AssertEqual(1f, bars[pi], "峰值柱高为 1");
            TestRunner.AssertTrue(thin[pi].Year == peakOrigin, "峰值下标指向极盛样本");
            TestRunner.AssertTrue(bars[0] < 1f, "非峰值柱 < 1");

            // ── 0. PeakYear 回写（LlmFix-6：采样接线后「极盛 N 年」的数据源）──
            var peakRec = new EmperorRecord
            {
                Track = new List<EmperorSample>
                {
                    S(1, 5, 1200), S(6, 9, 2600), S(11, 4, 1100)
                }
            };
            OrthodoxMandateManager.RefreshPeakYear(peakRec);
            TestRunner.AssertEqual(6, peakRec.PeakYear, "极盛之年=国力点最大的样本年");
            peakRec.Track.Clear();
            OrthodoxMandateManager.RefreshPeakYear(peakRec);
            TestRunner.AssertEqual(0, peakRec.PeakYear, "空轨迹极盛归 0（不臆断）");

            // ── 1. 采样间隔 ──
            var track = new List<EmperorSample>();
            TestRunner.AssertTrue(EmperorTrackSampler.ShouldSample(track, 1, 10), "空轨迹首点必采");
            EmperorTrackSampler.AddSample(track, S(1, 5, 1200));
            TestRunner.AssertTrue(!EmperorTrackSampler.ShouldSample(track, 5, 10), "未到间隔不采");
            TestRunner.AssertTrue(EmperorTrackSampler.ShouldSample(track, 11, 10), "到间隔应采");
            TestRunner.AssertTrue(!EmperorTrackSampler.ShouldSample(track, 11, 0), "间隔 0 = 关闭采样");

            // ── 2. 幂等与排序 ──
            var t2 = new List<EmperorSample>();
            TestRunner.AssertTrue(EmperorTrackSampler.AddSample(t2, S(10, 6, 1500)), "追加成功");
            TestRunner.AssertTrue(!EmperorTrackSampler.AddSample(t2, S(10, 9, 9999)), "同年重复不追加");
            TestRunner.AssertEqual(6, t2[0].Cities, "同年重复保留首值");
            EmperorTrackSampler.AddSample(t2, S(1, 5, 1200));
            TestRunner.AssertEqual(1, t2[0].Year, "乱序插入后仍按年升序");

            // ── 3. 极盛识别 ──
            var arc = new List<EmperorSample>
            {
                S(1, 5, 1200), S(10, 7, 1800), S(19, 9, 2600), S(28, 5, 1400), S(29, 4, 1200)
            };
            var peak = EmperorTrackSampler.Peak(arc);
            TestRunner.AssertTrue(peak != null, "有峰值");
            TestRunner.AssertEqual(19, peak.Year, "极盛在第 19 年");

            // ── 4. 形态识别 ──
            TestRunner.AssertEqual(EmperorShape.RiseThenFall, EmperorTrackSampler.Shape(arc),
                "29 年在位先升后降 → RiseThenFall");

            var rising = new List<EmperorSample> { S(1, 3, 900), S(10, 5, 1400), S(20, 8, 2100) };
            TestRunner.AssertEqual(EmperorShape.Rising, EmperorTrackSampler.Shape(rising), "一路向上");

            var falling = new List<EmperorSample> { S(1, 9, 2600), S(10, 6, 1600), S(20, 3, 800) };
            TestRunner.AssertEqual(EmperorShape.Falling, EmperorTrackSampler.Shape(falling), "一路下滑");

            var flat = new List<EmperorSample> { S(1, 5, 1200), S(10, 5, 1250), S(20, 5, 1180) };
            TestRunner.AssertEqual(EmperorShape.Flat, EmperorTrackSampler.Shape(flat), "平稳");

            // 单点 / 空 → Unknown
            TestRunner.AssertEqual(EmperorShape.Unknown,
                EmperorTrackSampler.Shape(new List<EmperorSample> { S(1, 5, 1200) }), "单点 → Unknown");
            TestRunner.AssertEqual(EmperorShape.Unknown,
                EmperorTrackSampler.Shape(new List<EmperorSample>()), "空轨迹 → Unknown");

            // ── 5. 两点兜底（旧档）──
            TestRunner.AssertEqual(EmperorShape.Falling,
                EmperorTrackSampler.ShapeFromEndpoints(100, 50), "两点兜底：明显回落 → Falling");
            TestRunner.AssertEqual(EmperorShape.Rising,
                EmperorTrackSampler.ShapeFromEndpoints(50, 100), "两点兜底：明显上升 → Rising");
            TestRunner.AssertEqual(EmperorShape.Unknown,
                EmperorTrackSampler.ShapeFromEndpoints(0, 0), "两点兜底：全 0 → Unknown");

            // ── 6. 国力点口径 ──
            TestRunner.AssertEqual(30 + 30, EmperorSample.PowerScoreOf(5, 1200), "国力点 = 城×6 + 人口÷40");
            TestRunner.AssertEqual(0, EmperorSample.PowerScoreOf(-1, -5), "负值不越界");

            // ── 7. 抽稀（控制 token）──
            var many = new List<EmperorSample>();
            for (int y = 1; y <= 40; y += 2) many.Add(S(y, 3 + y % 5, 800 + y * 20));
            var condensed = EmperorTrackSampler.Condense(many, 8);
            TestRunner.AssertTrue(condensed.Count <= 8, "抽稀后不超过 8 点");
            TestRunner.AssertEqual(many[0].Year, condensed[0].Year, "抽稀保留首点");
            TestRunner.AssertEqual(many[many.Count - 1].Year, condensed[condensed.Count - 1].Year, "抽稀保留末点");

            // 抽稀后仍能识别形态（首尾与峰值都在）
            TestRunner.AssertTrue(EmperorTrackSampler.Shape(condensed) != EmperorShape.Unknown,
                "抽稀后形态仍可识别");
        }
    }
}
