using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 [F9] 无嗣继承判定契约守护。
    ///
    /// 判定是纯函数，因此可以把"什么样的国该亡/该篡/该续"钉成用例表。
    /// 这些用例同时也是**设计说明书**——阈值改动导致行为漂移时，测试会立刻指出来。
    /// </summary>
    public static class NoHeirResolverTests
    {
        private static NoHeirInput Make(float central, float officialPower, float corruption,
                                        float mandate, float stability, int cities,
                                        bool isMandateHolder = false, bool hasOfficial = true)
        {
            return new NoHeirInput
            {
                CentralPower = central,
                TopOfficialPower = officialPower,
                TopOfficialCorruption = corruption,
                Mandate = mandate,
                Stability = stability,
                CityCount = cities,
                IsMandateHolder = isMandateHolder,
                HasTopOfficial = hasOfficial
            };
        }

        public static void Run()
        {
            // 1. 强中央 + 弱权臣 → 远亲继承（最常见）
            var d1 = NoHeirResolver.Decide(Make(80, 30, 10, 0, 80, 3));
            TestRunner.AssertEqual(NoHeirOutcome.DistantKin, d1.Outcome, "强中央弱权臣 → 远亲继承");

            // 2. 弱中央 + 强权臣 + 高腐败 → 权臣篡位
            var d2 = NoHeirResolver.Decide(Make(20, 80, 70, 0, 45, 4));
            TestRunner.AssertEqual(NoHeirOutcome.Usurpation, d2.Outcome, "弱中央强权臣 → 篡位");

            // 3. 弱中央 + 低稳定 + 多城 + 无天命 → 分崩离析
            var d3 = NoHeirResolver.Decide(Make(15, 25, 20, 10, 20, 12));
            TestRunner.AssertEqual(NoHeirOutcome.Collapse, d3.Outcome, "弱中央低稳定多城 → 分裂");

            // 4. 国内无在任官员 → 无人可篡，只能是远亲或分裂
            var d4 = NoHeirResolver.Decide(Make(20, 90, 90, 0, 70, 3, false, false));
            TestRunner.AssertTrue(d4.Outcome != NoHeirOutcome.Usurpation, "无在任官员时不判篡位");

            // 5. 天命镇压：同样的崩坏局面，高天命应压回远亲继承
            var lowMandate = NoHeirResolver.Decide(Make(20, 78, 65, 10, 45, 4, true, true));
            var highMandate = NoHeirResolver.Decide(Make(20, 78, 65, 95, 45, 4, true, true));
            TestRunner.AssertEqual(NoHeirOutcome.Usurpation, lowMandate.Outcome, "天命低时仍可篡位");
            TestRunner.AssertTrue(highMandate.Outcome != NoHeirOutcome.Usurpation,
                "天命极高时抑制篡位（实际：" + highMandate.Outcome + "）");
            TestRunner.AssertTrue(highMandate.UsurpScore < lowMandate.UsurpScore, "天命越高篡位分越低");

            // 6. 非天命国不参与天命抑制（Mandate 字段应被忽略）
            var nonHolder = NoHeirResolver.Decide(Make(20, 80, 70, 95, 45, 4, false, true));
            TestRunner.AssertEqual(NoHeirOutcome.Usurpation, nonHolder.Outcome, "非天命国不受天命抑制");

            // 7. 篡位必须同时满足「中央<45」——中央尚可时即便权臣很强也不篡
            var d7 = NoHeirResolver.Decide(Make(60, 85, 80, 0, 50, 4));
            TestRunner.AssertTrue(d7.Outcome != NoHeirOutcome.Usurpation, "中央尚可时不篡位");

            // 8. 分数恒在 0-100
            var extremes = new[]
            {
                Make(0, 0, 0, 0, 0, 0, false, true),
                Make(100, 100, 100, 100, 100, 40, true, true),
                Make(-50, -50, -50, -50, -50, -5, false, true),
                Make(999, 999, 999, 999, 999, 999, true, true)
            };
            foreach (var e in extremes)
            {
                var r = NoHeirResolver.Decide(e);
                TestRunner.AssertTrue(r.UsurpScore >= 0 && r.UsurpScore <= 100, "篡位分在 0-100");
                TestRunner.AssertTrue(r.CollapseScore >= 0 && r.CollapseScore <= 100, "分裂分在 0-100");
                TestRunner.AssertTrue(!string.IsNullOrEmpty(r.Reason), "判定必须给出理由");
            }

            // 9. 确定性：同输入必得同输出（纯函数）
            var a = NoHeirResolver.Decide(Make(30, 60, 55, 40, 50, 6, true, true));
            var b = NoHeirResolver.Decide(Make(30, 60, 55, 40, 50, 6, true, true));
            TestRunner.AssertEqual(a.Outcome, b.Outcome, "同输入同结局");
            TestRunner.AssertClose(a.UsurpScore, b.UsurpScore, "同输入同篡位分");
            TestRunner.AssertClose(a.CollapseScore, b.CollapseScore, "同输入同分裂分");
        }
    }
}
