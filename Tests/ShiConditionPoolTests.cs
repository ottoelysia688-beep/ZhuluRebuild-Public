using System.Collections.Generic;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 条件池契约守护（历代天命接入 第3步）。
    ///
    /// 这一步的意义：不推翻旧条件链，只把"每条件一个字"换成"每条件一个有序字池"。
    /// 因此必须守住两条：
    ///   ① 语义不跑偏——拓土之君只能在武/桓/襄/烈/威/庄里选，不能给「惠」
    ///   ② 确定性——同一皇帝恒同谥，读档不翻变
    /// </summary>
    public static class ShiConditionPoolTests
    {
        private static EmperorFacts Base()
        {
            return new EmperorFacts
            {
                ReignYears = 10, PolicyCount = 0, CityDelta = 0, TreasDelta = 0,
                MandDelta = 0f, StabDelta = 0f, VassalDelta = 0, EndAge = 50,
                EndMandate = 60f, EndStability = 60f, PopPct = 5f, MandateCollected = true
            };
        }

        public static void Run()
        {
            // ── 1. 情形判定与旧链一致 ──
            var t = Base(); t.Tyrant = true;
            TestRunner.AssertEqual(ShiCandidateProvider.ShiCondition.Tyrant,
                ShiCandidateProvider.ConditionOf(t), "暴君 → Tyrant");

            var c = Base(); c.CityDelta = 3; c.StabDelta = 4f;
            TestRunner.AssertEqual(ShiCandidateProvider.ShiCondition.Conquest,
                ShiCandidateProvider.ConditionOf(c), "拓土 → Conquest");

            var cult = Base(); cult.PolicyCount = 5; cult.StabDelta = 2f;
            TestRunner.AssertEqual(ShiCandidateProvider.ShiCondition.Culture,
                ShiCandidateProvider.ConditionOf(cult), "文治 → Culture");

            var lost = Base(); lost.LostThrone = true;
            TestRunner.AssertEqual(ShiCandidateProvider.ShiCondition.LostThrone,
                ShiCandidateProvider.ConditionOf(lost), "失国 → LostThrone");

            // ── 2. 池内语义必须与类别一致 ──
            var conquestPool = ShiCandidateProvider.PoolFor(ShiCandidateProvider.ShiCondition.Conquest);
            TestRunner.AssertTrue(conquestPool != null && conquestPool.Length >= 5, "拓土池至少 5 字");
            TestRunner.AssertTrue(System.Array.IndexOf(conquestPool, "武") >= 0, "拓土池含「武」");
            TestRunner.AssertTrue(System.Array.IndexOf(conquestPool, "惠") < 0,
                "拓土池不得含「惠」（民丰，语义不符）");

            var culturePool = ShiCandidateProvider.PoolFor(ShiCandidateProvider.ShiCondition.Culture);
            TestRunner.AssertTrue(System.Array.IndexOf(culturePool, "文") >= 0, "文治池含「文」");
            TestRunner.AssertTrue(System.Array.IndexOf(culturePool, "厉") < 0, "文治池不得含恶谥");

            // ── 3. 池内取字：排除已用 ──
            var used = new HashSet<string> { "武", "桓" };
            string picked = ShiCandidateProvider.PickFromCondition(
                ShiCandidateProvider.ShiCondition.Conquest, used, 12345);
            TestRunner.AssertTrue(!string.IsNullOrEmpty(picked), "池内有可用字");
            TestRunner.AssertTrue(!used.Contains(picked), "取字不含已用字");
            TestRunner.AssertTrue(System.Array.IndexOf(conquestPool, picked) >= 0, "取字必在池内");

            // ── 4. 确定性 ──
            string a = ShiCandidateProvider.PickFromCondition(
                ShiCandidateProvider.ShiCondition.Conquest, null, 999);
            string b = ShiCandidateProvider.PickFromCondition(
                ShiCandidateProvider.ShiCondition.Conquest, null, 999);
            TestRunner.AssertEqual(a, b, "同种子恒同字");

            // 不同种子应能分散到不同字（抽样验证，不做强断言）
            var seen = new HashSet<string>();
            for (int i = 0; i < 40; i++)
                seen.Add(ShiCandidateProvider.PickFromCondition(
                    ShiCandidateProvider.ShiCondition.Conquest, null, i));
            TestRunner.AssertTrue(seen.Count >= 3, "不同种子能分散取字（实际 " + seen.Count + " 种）");

            // ── 5. 池用尽 → 返回 null（调用方保留旧链原字）──
            var allUsed = new HashSet<string>(conquestPool);
            string none = ShiCandidateProvider.PickFromCondition(
                ShiCandidateProvider.ShiCondition.Conquest, allUsed, 1);
            TestRunner.AssertTrue(none == null, "池用尽返回 null");

            // ── 6. 无池情形 → null ──
            TestRunner.AssertTrue(
                ShiCandidateProvider.PickFromCondition(ShiCandidateProvider.ShiCondition.None, null, 1) == null,
                "None 情形无池");

            // ── 7. 集成：真实路径（带 usedShi）确实从条件池取字 ──
            // 拓土之君：城 5→9，应落在 Conquest 池内，而不是旧链固定的「武」
            var e = new EmperorRecord
            {
                Name = "李承乾", Era = "承平", StartYear = 100, EndYear = 121,
                StartCities = 5, EndCities = 9, PolicyCount = 2
            };
            var usedSet = new HashSet<string>();
            PosthumousNameGenerator.Generate(e, isFirstOfReign: false, lostThrone: false,
                usedTemples: null, isRestoration: false, indexInDynasty: -1,
                rand01: null, usedShi: usedSet);
            TestRunner.AssertTrue(conquestPool.Length > 0, "拓土池非空");
            TestRunner.AssertTrue(System.Array.IndexOf(conquestPool, e.PosthumousName) >= 0,
                "带朝代上下文时，拓土之君必落在 Conquest 池内（实际：" + e.PosthumousName + "）");
            TestRunner.AssertTrue(usedSet.Contains(e.PosthumousName), "取到的字已登记进已用集");

            // 同一皇帝重复生成 → 恒同谥（确定性）
            var e2 = new EmperorRecord
            {
                Name = "李承乾", Era = "承平", StartYear = 100, EndYear = 121,
                StartCities = 5, EndCities = 9, PolicyCount = 2
            };
            PosthumousNameGenerator.Generate(e2, isFirstOfReign: false, lostThrone: false,
                usedTemples: null, isRestoration: false, indexInDynasty: -1,
                rand01: null, usedShi: new HashSet<string>());
            TestRunner.AssertEqual(e.PosthumousName, e2.PosthumousName, "同一皇帝恒同谥");
        }
    }
}
