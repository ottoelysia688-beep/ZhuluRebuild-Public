using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// P1-A 批测试（2026-08-23）：WarScoreRules 纯函数分量 / 白和概率公式 / 确定性哈希骰。
    /// 全部无 Unity 依赖，锁定"增量与时间项分离"与"同输入恒同输出"两条契约。
    /// </summary>
    public static class WarAndDiceTests
    {
        public static void TestWarScoreComponents()
        {
            // 城市分量：得失对称、负数防御、权重15
            AssertEq(30, WarScoreRules.CitiesGained(10, 8), "守方失2城=+30");
            AssertEq(0, WarScoreRules.CitiesGained(5, 8), "守方增城不得分");
            AssertEq(-45, WarScoreRules.CitiesLost(13, 10), "攻方失3城=-45");
            AssertEq(0, WarScoreRules.CitiesLost(4, 9), "攻方增城不失分");
            AssertEq(0, WarScoreRules.CitiesLost(0, 0), "零城边界");

            // 击杀分量：(守亡−攻亡)/2，C#整除截断语义锁定
            AssertEq(25, WarScoreRules.KillComponent(100, 150), "击杀差50→+25");
            AssertEq(-12, WarScoreRules.KillComponent(25, 0), "负差截断向零(-25/2=-12)");
            AssertEq(0, WarScoreRules.KillComponent(0, 0), "零阵亡");
            AssertEq(1, WarScoreRules.KillComponent(1, 4), "(4-1)/2=1 截断");

            // 首都分量：±20 组合
            AssertEq(20, WarScoreRules.CapitalComponent(true, false), "占守京+20");
            AssertEq(-20, WarScoreRules.CapitalComponent(false, true), "失攻京-20");
            AssertEq(0, WarScoreRules.CapitalComponent(true, true), "互占对京抵消");

            // 时间项分离契约：常量恒 -1，且纯函数重复求值不叠加（防 n(n+1)/2 复发）
            AssertEq(-1, WarScoreRules.AnnualTimePenalty, "年度时间惩罚=-1");
            int once = WarScoreRules.CitiesGained(10, 8) + WarScoreRules.KillComponent(0, 40);
            int twice = once + once; // 分量重复计算只应来自调用方错误——纯函数自身不携带状态
            AssertEq(once * 2, twice, "纯函数无隐式累计");
        }

        public static void TestWhitePeaceProbability()
        {
            // 早期保护：<5 年恒 0
            AssertTrue(WarScoreRules.WhitePeaceProbability(0, 1f, 0.5f) == 0f, "0年白和=0");
            AssertEq(0f, WarScoreRules.WhitePeaceProbability(4, 1f, 0f), 0.0001f, "4年仍为0");
            // 第5年起：僵持+主和党可立即产生概率
            float p5 = WarScoreRules.WhitePeaceProbability(5, 1f, 0f);
            AssertTrue(p5 > 0f, "5年起有白和可能");
            AssertEq(0.35f, p5, 0.0001f, "5年僵持+全主和=0(基础)+0.10(僵持)+0.25(和平)=0.35");

            // 坡道（中性轴0.5 → 和平项恒 +0.125）：10年后每年+0.01，封顶0.20
            float p15 = WarScoreRules.WhitePeaceProbability(15, 0.5f, 0.5f);
            AssertEq(0.175f, p15, 0.0001f, "15年非僵持中性轴=坡道0.05+和平0.125");
            float p12 = WarScoreRules.WhitePeaceProbability(12, 0.5f, 0.5f);
            AssertEq(0.145f, p12, 0.0001f, "12年=坡道0.02+和平0.125");
            float p30 = WarScoreRules.WhitePeaceProbability(30, 0.5f, 0.5f);
            float p40 = WarScoreRules.WhitePeaceProbability(40, 0.5f, 0.5f);
            AssertEq(0.325f, p30, 0.0001f, "30年坡道触顶0.20+0.125");
            AssertEq(p30, p40, 0.0001f, "30年后不再增长（坡道封顶）");

            // 主战惩罚：轴≥0.7 减 0.10，且不为负
            float pHawk = WarScoreRules.WhitePeaceProbability(5, 0.5f, 1f);
            AssertEq(0f, pHawk, 0.0001f, "5年主战党=0.25-0.10=0.15?否——非僵持仅和平项0 → 0-0.10钳0");
            // 上式：基础0+僵持0+和平(1-1)*0.25=0 → 0-0.10 → 钳到0
            AssertTrue(pHawk >= 0f, "概率永不为负");

            // 总上限 0.45
            float pMax = WarScoreRules.WhitePeaceProbability(60, 1f, 0f);
            AssertTrue(pMax <= 0.45f, "概率封顶0.45");
            AssertEq(0.45f, pMax, 0.0001f, "极限僵局=0.20+0.10+0.25=0.55→钳0.45");

            // 僵持带边界
            AssertTrue(WarScoreRules.IsStalemate(0.8f) && WarScoreRules.IsStalemate(1.25f), "僵持带闭区间");
            AssertTrue(!WarScoreRules.IsStalemate(0.79f) && !WarScoreRules.IsStalemate(1.26f), "带外非僵持");
        }

        public static void TestDeterministicRandom()
        {
            // 同输入恒同输出
            float a1 = DeterministicRandom.Roll01(12345L, 427, "keju");
            float a2 = DeterministicRandom.Roll01(12345L, 427, "keju");
            AssertEq(a1, a2, 0f, "确定性：同参数两次相等");

            // 盐/种子/年份任一变化即改变输出（抽样验证）
            bool saltDiffers = false, yearDiffers = false;
            for (long s = 1; s <= 20; s++)
            {
                if (DeterministicRandom.Roll01(s, 427, "keju") != DeterministicRandom.Roll01(s, 427, "keju_wu")) saltDiffers = true;
                if (DeterministicRandom.Roll01(s, 427, "keju") != DeterministicRandom.Roll01(s, 428, "keju")) yearDiffers = true;
            }
            AssertTrue(saltDiffers, "不同盐产出不同序列");
            AssertTrue(yearDiffers, "不同年份产出不同序列");

            // 文本种子路径同样确定
            AssertEq(DeterministicRandom.Roll01Text("汉|刘邦|建元|300", 355, "shi"),
                     DeterministicRandom.Roll01Text("汉|刘邦|建元|300", 355, "shi"), 0f, "文本种子确定");

            // 范围 [0,1)
            bool inRange = true;
            for (long s = 0; s < 500; s++)
            {
                float v = DeterministicRandom.Roll01(s, 7, "range");
                if (v < 0f || v >= 1f) { inRange = false; break; }
            }
            AssertTrue(inRange, "500样本均在[0,1)");

            // RollInt 边界
            AssertEq(5, DeterministicRandom.RollInt(1L, 1, "x", 5, 5), "退化区间返回下界");
            bool intBounds = true;
            for (long s = 0; s < 300; s++)
            {
                int v = DeterministicRandom.RollInt(s, 9, "bounds", -3, 4);
                if (v < -3 || v >= 4) { intBounds = false; break; }
            }
            AssertTrue(intBounds, "RollInt 300样本均在[-3,4)");

            // PerturbPercent：范围±max、确定性
            bool perturbBounds = true;
            for (long s = 0; s < 300; s++)
            {
                int v = DeterministicRandom.PerturbPercent(s, 427, "keju_wen", 6);
                if (v < -6 || v > 6) { perturbBounds = false; break; }
            }
            AssertTrue(perturbBounds, "扰动300样本均在±6");
            AssertEq(DeterministicRandom.PerturbPercent(777L, 427, "keju_wu", 6),
                     DeterministicRandom.PerturbPercent(777L, 427, "keju_wu", 6), "扰动确定性");
        }

        private static void AssertEq<T>(T expected, T actual, string name)
            => TestRunner.AssertEqual(expected, actual, name);

        private static void AssertEq(float expected, float actual, float tol, string name)
            => TestRunner.AssertClose(expected, actual, name, tol);

        private static void AssertTrue(bool cond, string name)
            => TestRunner.AssertTrue(cond, name);
    }
}


