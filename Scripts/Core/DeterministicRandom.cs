using System;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 确定性哈希骰（P1-A3，2026-08-23；手法源自玄鉴仙族 FNV Roll01）。
    /// 动机（设计决策#4"起名不污染模拟"的推广到一切概率事件）：
    /// UnityEngine.Random 读档重掷可刷结果，且消耗全局序列间接扰动战争/政变模拟；
    /// 改为 (种子, 年份, 盐) 的 FNV-1a 纯哈希后——同输入恒同输出：
    /// 天然防 SL、回放一致、零共享状态。
    /// 用法：DeterministicRandom.Roll01(actorId, ZhuluTime.CurrentYear, "keju") &lt; 0.25f
    /// </summary>
    public static class DeterministicRandom
    {
        /// <summary>核心入口：长整型种子（Actor/War id 等）+ 年份 + 盐 → [0,1) 确定性浮点。</summary>
        public static float Roll01(long seedA, int seedB, string salt)
        {
            unchecked
            {
                uint h = 2166136261u;                 // FNV-1a 32位偏移基
                h = MixSalt(h, salt ?? "");
                h ^= (uint)seedA; h *= 16777619u;
                h ^= (uint)(seedA >> 32); h *= 16777619u;
                h ^= (uint)seedB; h *= 16777619u;
                h ^= 0x9E3779B9u; h *= 16777619u;     // 黄金比例终混（雪崩）
                return (h & 0xFFFFFF) / 16777216f;    // 24 位定点精度 → [0,1)
            }
        }

        /// <summary>文本种子变体（如 "汉|刘邦|建元|300"）→ [0,1)。适合无稳定数字 id 的记录型对象。</summary>
        public static float Roll01Text(string seedText, int seedB, string salt)
        {
            unchecked
            {
                long a = 2166136261;
                string s = seedText ?? "";
                foreach (char c in s) { a ^= c; a *= 16777619; }
                return Roll01(a, seedB, salt);
            }
        }

        /// <summary>整数区间 [minInclusive, maxExclusive)；区间非法返回下界。</summary>
        public static int RollInt(long seedA, int seedB, string salt, int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(Roll01(seedA, seedB, salt) * (maxExclusive - minInclusive));
        }

        /// <summary>
        /// 对称扰动（P1-A4 科举卷面专用但通用）：返回 [-maxPercent, +maxPercent] 整数。
        /// 同 (seed, year, salt) 恒定——一次性临场发挥，不可重掷。
        /// </summary>
        public static int PerturbPercent(long seedA, int seedB, string salt, int maxPercent)
        {
            return RollInt(seedA, seedB, salt, -maxPercent, maxPercent + 1);
        }

        private static uint MixSalt(uint h, string salt)
        {
            unchecked
            {
                foreach (char c in salt) { h ^= c; h *= 16777619u; }
                return h;
            }
        }
    }
}
