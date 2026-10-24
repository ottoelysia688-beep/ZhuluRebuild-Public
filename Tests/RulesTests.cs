using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// Rules 层纯判定逻辑测试。只调用无游戏世界依赖的纯函数/确定性分支。
    /// 含概率的分支只测试"必定触发"和"必定不触发"的边界。
    /// </summary>
    public static class RulesTests
    {
        // ── VassalRules：独立战争人口比例判定（只测确定性分支，概率分支需Unity运行时）──
        public static void TestVassalIndependenceTrigger()
        {
            // 宗主人口为0 → 不触发（防除零）——阶段2B ⑥：签名加兵力比（附庸兵力<宗主60%不独立）
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerIndependence(100, 0, 50, 100), "宗主0人不触发独立");

            // 附庸人口 >= 宗主人口 → 必定触发（ratio >= 1.0 直接返回 true，不走随机）
            TestRunner.AssertEqual(true, VassalRules.ShouldTriggerIndependence(100, 100, 80, 100), "人口相等必定独立");
            TestRunner.AssertEqual(true, VassalRules.ShouldTriggerIndependence(200, 100, 80, 100), "人口2倍必定独立");

            // 附庸人口 < 80% 宗主 → 必定不触发（ratio < 0.8 直接返回 false，不走随机）
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerIndependence(79, 100, 80, 100), "79%不触发独立");
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerIndependence(0, 100, 80, 100), "0%不触发独立");
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerIndependence(50, 100, 80, 100), "50%不触发独立");

            // 阶段2B ⑥：人口达标但兵力悬殊（<60%）→ 不独立
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerIndependence(200, 100, 50, 100), "兵力<60%不独立");

            // 注意：80%-99% 区间走 UnityEngine.Random，纯.NET环境无法测试
        }

        // ── VassalRules：联合独立判定（只测确定性前置条件）──
        public static void TestVassalJointIndependence()
        {
            // 附庸数 < 2 → 不触发（在随机数之前就返回）
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerJointIndependence(200, 100, 1), "单附庸不联合独立");
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerJointIndependence(200, 100, 0), "0附庸不联合独立");

            // 总附庸人口 < 宗主×70% → 不触发（阶段2B ⑥：门槛从100%放宽到70%）
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerJointIndependence(50, 100, 3), "总人口不足不联合独立");
            TestRunner.AssertEqual(false, VassalRules.ShouldTriggerJointIndependence(69, 100, 5), "69%总人口不联合独立");

            // 注意：满足前置条件后走 UnityEngine.Random，纯.NET环境无法测试
        }

        // ── VassalRules：结盟/互战规则 ──
        public static void TestVassalDiplomacyRules()
        {
            // 附庸不能结盟
            TestRunner.AssertEqual(false, VassalRules.CanCreateAlliance(true), "附庸不能结盟");
            TestRunner.AssertEqual(true, VassalRules.CanCreateAlliance(false), "非附庸可以结盟");

            // 同宗主附庸不能互战
            TestRunner.AssertEqual(false, VassalRules.CanVassalsFight(true), "同宗主不能互战");
            TestRunner.AssertEqual(true, VassalRules.CanVassalsFight(false), "不同宗主可以互战");
        }

        // ── VassalRules：常量一致性 ──
        public static void TestVassalConstants()
        {
            TestRunner.AssertEqual(5, VassalRules.IndependenceCheckInterval, "独立检查间隔=5年");
            TestRunner.AssertEqual(20, VassalRules.IndependenceCooldownYears, "独立冷却=20年");
        }

        // ── ClaimRules：常量一致性 ──
        public static void TestClaimConstants()
        {
            TestRunner.AssertEqual(5, ClaimRules.ClaimWarMaxYears, "宣称战争上限=5年");
        }

        // ── 2026-08-20 逐鹿特质体系规则（纯静态：ID 映射/清单完整性/互斥设计）──
        public static void TestZhuluTraitRules()
        {
            // ① 官职特质 ID 映射：中央七席 → zlr_office_{officeId}
            TestRunner.AssertEqual("zlr_office_prime_minister", TraitManager.GetOfficeTraitId("prime_minister"), "丞相特质ID");
            TestRunner.AssertEqual("zlr_office_military", TraitManager.GetOfficeTraitId("military"), "兵部特质ID");

            // ② 地方官三席统一映射为 zlr_office_local
            TestRunner.AssertEqual("zlr_office_local", TraitManager.GetOfficeTraitId(OfficeManager.LOCAL_XIANCHENG), "县丞→地方长吏");
            TestRunner.AssertEqual("zlr_office_local", TraitManager.GetOfficeTraitId(OfficeManager.LOCAL_JUNSHOU), "郡守→地方长吏");
            TestRunner.AssertEqual("zlr_office_local", TraitManager.GetOfficeTraitId(OfficeManager.LOCAL_CISHI), "刺史→地方长吏");

            // ③ 官职特质清单完整性：16 席（东方 7 中央 + 1 地方，游牧镜像同构——v1.14.0 双体系扩容）
            TestRunner.AssertEqual(16, TraitManager.OfficeTraitIds.Length, "官职特质数=16（东8+牧8）");
            foreach (var officeId in OfficeManager.OfficesList)
                TestRunner.AssertTrue(
                    System.Array.IndexOf(TraitManager.OfficeTraitIds, TraitManager.GetOfficeTraitId(officeId)) >= 0,
                    $"中央官席 {officeId} 有对应特质");

            // ③b 游牧镜像对齐：每个东方官职特质必有同名 nomad 变体（防双体系漂移）
            foreach (var tid in TraitManager.OfficeTraitIds)
            {
                if (!tid.StartsWith("zlr_office_")) continue;
                string nomadTid = "zlr_nomad_office_" + tid.Substring("zlr_office_".Length);
                TestRunner.AssertTrue(
                    System.Array.IndexOf(TraitManager.OfficeTraitIds, nomadTid) >= 0,
                    $"游牧镜像 {nomadTid} 与东方 {tid} 对齐");
            }

            // ④ 官职特质无重复
            var idSet = new System.Collections.Generic.HashSet<string>(TraitManager.OfficeTraitIds);
            TestRunner.AssertEqual(TraitManager.OfficeTraitIds.Length, idSet.Count, "官职特质无重复");

            // ⑤ 君主风评清单：仁/明/昏/暴/守成 各一（互斥链组）
            TestRunner.AssertEqual(5, TraitManager.RulerVerdictTraits.Length, "君主风评特质数=5");
            TestRunner.AssertTrue(System.Array.IndexOf(TraitManager.RulerVerdictTraits, TraitManager.TRAIT_BENEVOLENT) >= 0, "仁君在风评组");
            TestRunner.AssertTrue(System.Array.IndexOf(TraitManager.RulerVerdictTraits, TraitManager.TRAIT_TYRANT) >= 0, "暴君在风评组");
            TestRunner.AssertTrue(System.Array.IndexOf(TraitManager.RulerVerdictTraits, TraitManager.TRAIT_FOOLISH) >= 0, "昏君在风评组");

            // ⑥ 身份特质 ID 唯一前缀（zlr_ 家族防撞原版）
            string[] identityTraits =
            {
                TraitManager.TRAIT_CROWN_PRINCE, TraitManager.TRAIT_HEIR_APPARENT, TraitManager.TRAIT_CITY_LORD,
                TraitManager.TRAIT_FALLEN_KING, TraitManager.TRAIT_RESTORED_KING,
                TraitManager.TRAIT_SAGE_RULER, TraitManager.TRAIT_STEADY_RULER
            };
            foreach (var tid in identityTraits)
                TestRunner.AssertTrue(tid.StartsWith("zlr_"), $"特质 {tid} 带 zlr_ 前缀");

            // ⑦ 分组常量：逐鹿专属栏 id 非空且与 locale key 约定一致
            TestRunner.AssertEqual("zhulu", TraitManager.GROUP_ZHULU, "逐鹿专属分组ID");
        }
    }
}
