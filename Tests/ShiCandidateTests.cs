using System.Collections.Generic;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 谥字候选集契约守护（历代天命接入 第1步）。
    ///
    /// 这些用例同时是**设计说明书**：类别怎么判、扩展档何时启用、跨类范围多大、
    /// 去重是否生效。改字表或改判定顺序时，用例会立刻指出行为漂移。
    /// </summary>
    public static class ShiCandidateTests
    {
        private static EmperorFacts Make()
        {
            // 默认：平庸之君（无功无过）
            return new EmperorFacts
            {
                ReignYears = 10,
                PolicyCount = 0,
                CityDelta = 0,
                TreasDelta = 0,
                MandDelta = 0,
                StabDelta = 0,
                VassalDelta = 0,
                EndAge = 50,
                EndMandate = 60f,
                EndStability = 60f,
                PopPct = 5f,
                MandateCollected = true,
                AllFlat = true
            };
        }

        public static void Run()
        {
            // ── 1. 类别判定 ──
            var tyrant = Make(); tyrant.Tyrant = true;
            TestRunner.AssertEqual(ShiCategory.Evil, ShiCandidateProvider.Classify(tyrant), "暴君 → 恶谥");

            var foolish = Make(); foolish.Foolish = true;
            TestRunner.AssertEqual(ShiCategory.Evil, ShiCandidateProvider.Classify(foolish), "昏君 → 恶谥");

            var lost = Make(); lost.LostThrone = true;
            TestRunner.AssertEqual(ShiCategory.Pity, ShiCandidateProvider.Classify(lost), "失国 → 悯谥");

            var usurped = Make(); usurped.Usurped = true;
            TestRunner.AssertEqual(ShiCategory.Pity, ShiCandidateProvider.Classify(usurped), "被篡 → 悯谥");

            var expand = Make(); expand.CityDelta = 3; expand.StabDelta = 4f;
            TestRunner.AssertEqual(ShiCategory.Fine, ShiCandidateProvider.Classify(expand), "拓土 → 美谥");

            var plain = Make();
            TestRunner.AssertEqual(ShiCategory.Plain, ShiCandidateProvider.Classify(plain), "平庸 → 平谥");

            var earlyDeath = Make(); earlyDeath.ReignYears = 3; earlyDeath.EndAge = 40;
            TestRunner.AssertEqual(ShiCategory.Pity, ShiCandidateProvider.Classify(earlyDeath), "早夭 → 悯谥");

            // 旧档未采集天命时，不得因 EndMandate<30 判"天命坠"
            var legacy = Make(); legacy.MandateCollected = false; legacy.EndMandate = 0f;
            TestRunner.AssertTrue(ShiCandidateProvider.Classify(legacy) != ShiCategory.Pity,
                "旧档未采集天命不判悯谥");

            // ── 2. 轨迹形态修正 ──
            var riseFall = Make(); riseFall.CityDelta = 3; riseFall.StabDelta = 4f;
            riseFall.Shape = EmperorShape.RiseThenFall;
            TestRunner.AssertEqual(ShiCategory.Plain, ShiCandidateProvider.Classify(riseFall),
                "先升后降 → 由美降为平（晚节不终）");

            var falling = Make(); falling.ReignYears = 15; falling.CityDelta = -1;
            falling.Shape = EmperorShape.Falling;
            TestRunner.AssertEqual(ShiCategory.Pity, ShiCandidateProvider.Classify(falling),
                "长期下滑且失土 → 升为悯");

            // ── 3. 候选集范围：不得越出 ±1 阶 ──
            var evilSet = ShiCandidateProvider.Build(tyrant, null, null, true);
            TestRunner.AssertTrue(evilSet.Shi.Count > 0, "恶谥有候选");
            TestRunner.AssertTrue(evilSet.Shi.Contains("厉"), "恶谥候选含「厉」");
            TestRunner.AssertTrue(evilSet.Shi.Contains("愍"), "恶谥候选可含相邻悯谥「愍」");
            TestRunner.AssertTrue(!evilSet.Shi.Contains("文"), "恶谥候选不得含美谥「文」（跨两阶）");

            var fineSet = ShiCandidateProvider.Build(expand, null, null, true);
            TestRunner.AssertTrue(fineSet.Shi.Contains("文"), "美谥候选含「文」");
            TestRunner.AssertTrue(!fineSet.Shi.Contains("厉"), "美谥候选不得含恶谥「厉」");

            // 不允许跨类时，只保留基础类别
            var noShift = ShiCandidateProvider.Build(expand, null, null, false);
            TestRunner.AssertTrue(noShift.Shi.Contains("文"), "不跨类时仍含美谥字");
            TestRunner.AssertTrue(!noShift.Shi.Contains("平"), "不跨类时不含平谥字");

            // ── 4. 去重 ──
            // 美谥核心档现为 11 字（含 2026-10-24 新增的「元」），全部占满才启用扩展档
            var used = new HashSet<string> { "文", "武", "景", "宣", "昭", "懿", "章", "康", "成", "惠", "元" };
            var afterUsed = ShiCandidateProvider.Build(expand, used, null, false);
            TestRunner.AssertTrue(afterUsed.Shi.Count > 0, "核心档用尽后仍有候选");
            TestRunner.AssertTrue(afterUsed.UsedExtendedShi, "核心档用尽 → 启用扩展档");
            TestRunner.AssertTrue(afterUsed.Shi.Contains("桓"), "扩展档启用后含「桓」");
            TestRunner.AssertTrue(!afterUsed.Shi.Contains("文"), "已用字被剔除");

            // 全部用尽 → 空集（调用方需兜底）
            var allUsed = new HashSet<string> { "文", "武", "景", "宣", "昭", "懿", "章", "康", "成", "惠",
                                                "桓", "襄", "烈", "威", "庄", "平", "穆", "简", "靖", "思",
                                                "安", "顺", "和", "让", "静", "定", "宪", "夷" };
            var empty = ShiCandidateProvider.Build(expand, allUsed, null, true);
            TestRunner.AssertTrue(empty.Shi.Count == 0 || !empty.Shi.Contains("文"), "全用尽时不返回已用字");

            // ── 5. 候选无重复 ──
            var dupCheck = ShiCandidateProvider.Build(expand, null, null, true);
            var seen = new HashSet<string>();
            bool dup = false;
            foreach (var s in dupCheck.Shi) { if (!seen.Add(s)) { dup = true; break; } }
            TestRunner.AssertTrue(!dup, "候选集内无重复字");

            // ── 6. 庙号 ──
            var founder = Make(); founder.IsFirstOfReign = true;
            var founderSet = ShiCandidateProvider.Build(founder, null, null, true);
            TestRunner.AssertTrue(founderSet.Temple.Contains("太祖"), "开国可得祖系庙号");
            TestRunner.AssertTrue(founderSet.Temple.Contains("太宗"), "开国也含宗系庙号");

            var normalSet = ShiCandidateProvider.Build(plain, null, null, true);
            TestRunner.AssertTrue(!normalSet.Temple.Contains("太祖"), "非开国不得「太祖」");
            TestRunner.AssertTrue(normalSet.Temple.Contains("太宗"), "非开国仍可用宗系");

            var usedTemple = new HashSet<string> { "太宗", "世宗", "高宗" };
            var templeSet = ShiCandidateProvider.Build(normalSet.Base == ShiCategory.Plain ? plain : plain,
                                                       null, usedTemple, true);
            TestRunner.AssertTrue(!templeSet.Temple.Contains("太宗"), "已用庙号被剔除");

            // ── 7. 确定性取字 ──
            var pool = new List<string> { "文", "景", "昭" };
            string a1 = ShiCandidateProvider.PickBySeed(pool, 12345);
            string a2 = ShiCandidateProvider.PickBySeed(pool, 12345);
            TestRunner.AssertEqual(a1, a2, "同种子恒同字");
            TestRunner.AssertTrue(pool.Contains(a1), "取字必在池内");
            TestRunner.AssertEqual("", ShiCandidateProvider.PickBySeed(new List<string>(), 1), "空池返回空串");
            TestRunner.AssertTrue(pool.Contains(ShiCandidateProvider.PickBySeed(pool, -7)), "负种子也不越界");
        }
    }
}
