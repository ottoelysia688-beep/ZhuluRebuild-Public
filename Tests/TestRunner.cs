using System;
using System.Collections.Generic;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 零依赖迷你测试运行器（L2 自测层）。
    /// 不引入 xUnit/NUnit，避免 NuGet 离线 restore 失败；退出码 0=全过，1=有失败。
    /// </summary>
    public static class TestRunner
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> _failures = new List<string>();

        public static void AssertEqual<T>(T expected, T actual, string caseName)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                _passed++;
            }
            else
            {
                _failed++;
                _failures.Add($"{caseName}: 期望 [{expected}]，实际 [{actual}]");
            }
        }

        public static void AssertTrue(bool condition, string caseName)
        {
            if (condition) { _passed++; }
            else { _failed++; _failures.Add($"{caseName}: 条件为 false"); }
        }

        public static void AssertClose(float expected, float actual, string caseName, float tolerance = 0.0001f)
        {
            if (Math.Abs(expected - actual) <= tolerance) { _passed++; }
            else { _failed++; _failures.Add($"{caseName}: 期望 [{expected}]，实际 [{actual}]（容差 {tolerance}）"); }
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("═══ ZhuluRebuild Domain 层单元测试 ═══");

            RunSuite("OfficialRatingManager", DomainTests.TestOfficialRating);
            RunSuite("RegimeManager", DomainTests.TestRegime);
            RunSuite("KingdomExt.StripSuffix", DomainTests.TestStripSuffix);
            RunSuite("StabilityManager 等级", DomainTests.TestStabilityGrades);
            RunSuite("OrthodoxMandateManager 等级", DomainTests.TestMandateGrades);
            RunSuite("SuccessionManager 继承评分", DomainTests.TestSuccessionScoring);
            RunSuite("地基：派系命名/身份枚举/个人史册快照", DomainTests.TestGroundwork);
            RunSuite("党争：亲信度/冷却/世仇/政变判定/国策系数/快照", DomainTests.TestFactionPolitics);
            RunSuite("朝议×政令联动映射", DomainTests.TestDebatePolicyMap);
            RunSuite("阶段2V：大一统/谥法v3/党派显示名按位阶", DomainTests.TestUnifiedAndFactionDisplayNames);
            RunSuite("阶段3F1：谱牒数据层", DomainTests.TestFamilyRegistry);
            RunSuite("阶段3：天命史评丰富化", DomainTests.TestVerdictRichness);
            // VassalRules 独立触发/联合独立：方法体引用 UnityEngine.Random，纯.NET环境无法加载，跳过
            RunSuite("VassalRules 外交规则", RulesTests.TestVassalDiplomacyRules);
            RunSuite("VassalRules 常量", RulesTests.TestVassalConstants);
            RunSuite("ClaimRules 常量", RulesTests.TestClaimConstants);
            RunSuite("逐鹿特质体系：官职映射/清单/互斥规则", RulesTests.TestZhuluTraitRules);
            RunSuite("P0-2 快照往返（JSON 契约保真）", SnapshotRoundtripTests.Run);
            RunSuite("批次10-A1 SchemaV5 引擎", SchemaV5Tests.Run);
            RunSuite("P1-7c 姓氏尾缀净化", NamingHealTests.TestStripSuffix);
            RunSuite("P1-7c 书香门第状态机", NamingHealTests.TestNotifyExamPassJinshi);
            RunSuite("P1-A 战争分量/白和公式/确定性骰", WarAndDiceTests.TestWarScoreComponents);
            RunSuite("P1-A 白和概率公式", WarAndDiceTests.TestWhitePeaceProbability);
            RunSuite("P1-A 确定性哈希骰", WarAndDiceTests.TestDeterministicRandom);
            RunSuite("Interop 外部联动契约守护", InteropContractTests.Run);
            RunSuite("禅让机制开关契约", AbdicationSwitchTests.Run);
            RunSuite("F6 数值管道缓存等价性", ModifierPipelineEquivalenceTests.Run);
            RunSuite("F6 数值管道并发安全", ModifierPipelineEquivalenceTests.RunConcurrency);
            RunSuite("回滚开关契约（FeatureFlags 解析/写回）", FeatureFlagRoundtripTests.Run);
            RunSuite("补丁治理层（登记完整性/唯一性/结构）", PatchRegistryTests.Run);
            RunSuite("反射字段解析（自动属性后备字段）", ReflectionFieldTests.Run);
            RunSuite("快照存储层压缩（往返/旧档兼容/回滚）", SnapshotCodecTests.Run);
            RunSuite("无嗣继承判定（远亲/篡位/分裂）", NoHeirResolverTests.Run);
            RunSuite("谥字候选集（类别/分档/跨类/去重）", ShiCandidateTests.Run);
            RunSuite("在位轨迹采样（间隔/峰值/形态/抽稀）", EmperorTrackTests.Run);
            RunSuite("谥法条件池（语义/确定性/用尽兜底）", ShiConditionPoolTests.Run);
            RunSuite("大模型客户端（三协议/退避/熔断/JSON容错）", LlmClientTests.Run);
            RunSuite("AI 评价存储（键/覆盖/快照往返）", LlmCommentStoreTests.Run);
            RunSuite("AI 评价服务（幂等/上限/合并兜底）", LlmEmperorServiceTests.Run);
            RunSuite("大模型配置读写（协议解析/脱敏）", LlmConfigStoreTests.Run);
            RunSuite("断代史存储（期段键/兜底/快照往返）", LlmChronicleStoreTests.Run);

            Console.WriteLine($"\n结果: {_passed} 通过, {_failed} 失败");
            foreach (var f in _failures)
                Console.WriteLine($"  ✗ {f}");

            return _failed == 0 ? 0 : 1;
        }

        private static void RunSuite(string name, Action suite)
        {
            int before = _failed;
            try
            {
                suite();
                Console.WriteLine($"  [{(_failed == before ? "PASS" : "FAIL")}] {name}");
            }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add($"{name}: 套件异常 {ex.GetType().Name}: {ex.Message} | INNER: {ex.GetBaseException()}");
                Console.WriteLine($"  [ERROR] {name}: {ex.Message}");
            }
        }
    }
}
