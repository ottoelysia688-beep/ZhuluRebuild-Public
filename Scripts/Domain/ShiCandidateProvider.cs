using System;
using System.Collections.Generic;

namespace ZhuluRebuild.Domain
{
    /// <summary>谥法褒贬类别（数值越大越褒）。</summary>
    public enum ShiCategory
    {
        Evil = 0,    // 恶谥：暴虐失德
        Pity = 1,    // 悯谥：遭难短折
        Plain = 2,   // 平谥：无大功过
        Fine = 3     // 美谥：功业德政
    }

    /// <summary>在位轨迹形态（由 EmperorTrackSampler 在步骤2 填充；未知时不影响判定）。</summary>
    public enum EmperorShape
    {
        Unknown = 0,
        Rising = 1,        // 一路向上
        Flat = 2,          // 平稳
        Falling = 3,       // 一路下滑
        RiseThenFall = 4   // 先升后降（极盛在中段）
    }

    /// <summary>
    /// 判定所需事实。与 EmperorRecord 解耦，便于单测直接构造。
    /// </summary>
    public struct EmperorFacts
    {
        public int CityDelta;
        public long TreasDelta;
        public float MandDelta;
        public float StabDelta;
        public int ReignYears;
        public int PolicyCount;
        public int WarCount;        // LlmFix-6：真实攻伐次数（EmperorRecord.WarCount；-1=未采集）
        public int VassalDelta;
        public int EndAge;
        public float EndMandate;
        public float EndStability;
        public float PopPct;

        public bool IsFirstOfReign;
        public bool Unified;
        public bool Tyrant;
        public bool Foolish;
        public bool Benevolent;
        public bool Abdicated;
        public bool Usurped;
        public bool LostThrone;
        public bool MandateCollected;   // 旧档未采集时不得判"天命坠"
        public bool AllFlat;

        public EmperorShape Shape;
    }

    /// <summary>候选集：模型只能在其中选择，不能造字。</summary>
    public sealed class ShiCandidateSet
    {
        public ShiCategory Base;
        public readonly List<string> Shi = new List<string>();
        public readonly List<string> Temple = new List<string>();
        /// <summary>是否启用了扩展档（核心档已被本朝占满）。</summary>
        public bool UsedExtendedShi;
    }

    /// <summary>
    /// 谥字/庙号候选集构造（2026-10-24 历代天命接入 第1步）。
    ///
    /// 设计要点：
    ///   · 字池分「核心档 / 扩展档」——核心档是常用字，扩展档仅在核心档被本朝占满时启用，
    ///     避免一开局就出现「丑帝」「慢帝」这类生僻字。
    ///   · 基础类别由旧条件链 + 轨迹形态判定；候选集 = 基础类别 ± 1 阶（跨两阶等于推翻规则）。
    ///   · 去重：剔除本朝已用谥字；庙号同理。
    ///   · **纯函数**：不读世界、不写状态，可被单测完全覆盖。
    /// </summary>
    public static class ShiCandidateProvider
    {
        // ── 恶谥 ──
        private static readonly string[] EvilCore = { "厉", "灵", "炀", "缪", "戾", "荒", "刺", "荡" };
        private static readonly string[] EvilExt = { "丑", "惑", "慢" };

        // ── 悯谥（"幽"归此：被篡之君，沿用现有语义）──
        private static readonly string[] PityCore = { "愍", "哀", "悼", "殇", "冲", "质", "怀", "闵" };
        private static readonly string[] PityExt = { "幽", "夭", "伤", "恻" };

        // ── 平谥（"让"保留：平庸禅让）──
        private static readonly string[] PlainCore = { "平", "穆", "简", "靖", "思", "安", "顺", "和", "让" };
        private static readonly string[] PlainExt = { "静", "定", "宪", "夷" };

        // ── 美谥 ──
        private static readonly string[] FineCore = { "文", "武", "景", "宣", "昭", "懿", "章", "康", "成", "惠", "元" };
        private static readonly string[] FineExt = { "桓", "襄", "烈", "威", "庄", "圣" };

        // ── 庙号 ──
        private static readonly string[] TempleZu = { "太祖", "高祖", "世祖", "圣祖", "成祖" };
        private static readonly string[] TempleZong =
        {
            "太宗", "世宗", "高宗", "仁宗", "中宗", "宣宗", "神宗", "哲宗", "英宗", "成宗",
            "孝宗", "圣宗", "睿宗", "穆宗", "代宗", "宪宗", "光宗", "宁宗", "理宗", "度宗",
            "恭宗", "顺宗", "德宗", "文宗", "武宗"
        };

        private static string[] CoreOf(ShiCategory c)
        {
            switch (c)
            {
                case ShiCategory.Evil: return EvilCore;
                case ShiCategory.Pity: return PityCore;
                case ShiCategory.Plain: return PlainCore;
                default: return FineCore;
            }
        }

        private static string[] ExtOf(ShiCategory c)
        {
            switch (c)
            {
                case ShiCategory.Evil: return EvilExt;
                case ShiCategory.Pity: return PityExt;
                case ShiCategory.Plain: return PlainExt;
                default: return FineExt;
            }
        }

        /// <summary>
        /// 判定基础类别。顺序与现有条件链一致（恶＞悯＞平＞美），
        /// 末尾叠加轨迹形态：先升后降者由美降为平（晚节不终）。
        /// </summary>
        public static ShiCategory Classify(EmperorFacts f)
        {
            ShiCategory cat;

            if (f.Tyrant) cat = ShiCategory.Evil;                                            // 暴
            else if (f.Foolish) cat = ShiCategory.Evil;                                      // 昏
            else if (f.LostThrone) cat = ShiCategory.Pity;                                   // 失国
            else if (f.Usurped) cat = ShiCategory.Pity;                                      // 被篡
            else if (f.Abdicated && !HighMeritAbdication(f)) cat = ShiCategory.Plain;        // 平庸禅让
            else if (f.CityDelta <= -4 && f.TreasDelta <= -800) cat = ShiCategory.Evil;      // 失德失土
            else if (f.MandateCollected && f.EndMandate < 30f) cat = ShiCategory.Pity;       // 天命坠
            else if (f.ReignYears < 5 && !f.IsFirstOfReign && f.EndAge > 0 && f.EndAge < 50)
                cat = ShiCategory.Pity;                                                      // 早夭
            else if (f.CityDelta < 0) cat = ShiCategory.Pity;                                // 失土
            else if (f.Unified) cat = ShiCategory.Fine;                                      // 大一统
            else if (f.IsFirstOfReign) cat = ShiCategory.Fine;                               // 开国
            else if (f.CityDelta >= 2) cat = ShiCategory.Fine;                               // 拓土
            else if (f.MandDelta >= 8f && f.StabDelta >= 0f) cat = ShiCategory.Fine;         // 中兴
            else if (f.PolicyCount >= 6 && f.StabDelta >= 0f) cat = ShiCategory.Fine;        // 制度
            else if (f.PolicyCount >= 4 && f.StabDelta > 0f) cat = ShiCategory.Fine;         // 文治
            else if (f.VassalDelta >= 1) cat = ShiCategory.Fine;                             // 四夷/绥远
            else if (f.StabDelta >= 12f) cat = ShiCategory.Fine;                             // 胜敌志强
            else if (f.EndStability >= 70f && f.StabDelta >= 0f) cat = ShiCategory.Fine;     // 国泰
            else if (f.StabDelta >= 3f) cat = ShiCategory.Fine;                              // 治平
            else if (f.Benevolent) cat = ShiCategory.Fine;                                   // 温柔贤善
            else if (f.PopPct >= 20f) cat = ShiCategory.Plain;                               // 民增无功
            else cat = ShiCategory.Plain;                                                    // 无功无过 / 填充

            // 轨迹形态修正（步骤2 才会填充 Shape；Unknown 时不介入）
            if (cat == ShiCategory.Fine && f.Shape == EmperorShape.RiseThenFall)
                cat = ShiCategory.Plain;      // 中叶极盛、晚节不终 → 降一等
            else if (cat == ShiCategory.Plain && f.Shape == EmperorShape.Falling
                     && f.ReignYears >= 10 && f.CityDelta < 0)
                cat = ShiCategory.Pity;       // 长期下滑且失土 → 升为悯

            return cat;
        }

        /// <summary>高功禅让（乾隆式）：虽禅位但不失其功，走功绩链而非"让"。</summary>
        private static bool HighMeritAbdication(EmperorFacts f)
        {
            if (f.Tyrant || f.Foolish || f.LostThrone) return false;
            return f.CityDelta >= 3 || f.Unified || f.MandDelta >= 8f
                || (f.PolicyCount >= 4 && f.StabDelta >= 0f) || f.PolicyCount >= 6;
        }

        /// <summary>
        /// 构造候选集。allowShift=true 时并入相邻类别（±1 阶），否则只用基础类别。
        /// </summary>
        public static ShiCandidateSet Build(EmperorFacts f, HashSet<string> usedShi,
                                            HashSet<string> usedTemple, bool allowShift = true)
        {
            var set = new ShiCandidateSet { Base = Classify(f) };

            int lo = (int)set.Base, hi = (int)set.Base;
            if (allowShift)
            {
                lo = Math.Max((int)ShiCategory.Evil, (int)set.Base - 1);
                hi = Math.Min((int)ShiCategory.Fine, (int)set.Base + 1);
            }

            // 先收各档核心字，全被占用时再收扩展字
            bool anyCore = false;
            for (int c = lo; c <= hi; c++)
            {
                foreach (var s in CoreOf((ShiCategory)c))
                {
                    if (usedShi != null && usedShi.Contains(s)) continue;
                    if (set.Shi.Contains(s)) continue;
                    set.Shi.Add(s);
                    anyCore = true;
                }
            }

            if (!anyCore)
            {
                for (int c = lo; c <= hi; c++)
                {
                    foreach (var s in ExtOf((ShiCategory)c))
                    {
                        if (usedShi != null && usedShi.Contains(s)) continue;
                        if (set.Shi.Contains(s)) continue;
                        set.Shi.Add(s);
                    }
                }
                set.UsedExtendedShi = set.Shi.Count > 0;
            }

            // 庙号：祖系仅开国/复国/大一统可得；宗系排除已用
            bool canUseZu = f.IsFirstOfReign || f.Unified;
            if (canUseZu)
            {
                foreach (var t in TempleZu)
                {
                    if (usedTemple != null && usedTemple.Contains(t)) continue;
                    set.Temple.Add(t);
                }
            }
            foreach (var t in TempleZong)
            {
                if (usedTemple != null && usedTemple.Contains(t)) continue;
                if (set.Temple.Contains(t)) continue;
                set.Temple.Add(t);
            }

            return set;
        }

        /// <summary>无模型时的确定性取字：同输入恒同字（沿用现有哈希思路，调用方传种子）。</summary>
        public static string PickBySeed(IList<string> candidates, int seed)
        {
            if (candidates == null || candidates.Count == 0) return "";
            int idx = (int)((uint)seed % (uint)candidates.Count);
            return candidates[idx];
        }

        // ═══════════ 条件池（2026-10-24 第3步）═══════════
        //
        // 四类只能决定褒贬，决定不了"语义贴切"：同为美谥，拓土之君得「惠」会很怪。
        // 所以再细一层——每个条件配一个**有序字池**（越靠前越贴切），
        // 取字时在该池内按确定性哈希挑，既保语义又出变化。
        //
        // 与旧条件链的关系：链先算出"这个皇帝属于哪种情形"，再用本池替换单字。
        // 池内找不到链给的旧字时（如元/圣等罕见情形），保留旧字不动。

        /// <summary>谥法触发情形（与旧条件链一一对应）。</summary>
        public enum ShiCondition
        {
            None = 0,
            Tyrant, Foolish, Ruin,                 // 恶
            LostThrone, Usurped, MandateFall, EarlyDeath, LostLand,   // 悯
            Abdication, PopGrow, Neutral,           // 平
            Unified, Founding, Conquest, Revival, Institution, Culture,
            Vassalage, Valor, Order, Peace, Benevolent   // 美
        }

        private static readonly Dictionary<ShiCondition, string[]> ConditionPools =
            new Dictionary<ShiCondition, string[]>
        {
            { ShiCondition.Tyrant,      new[] { "厉", "戾", "缪", "丑", "刺", "荡" } },
            { ShiCondition.Foolish,     new[] { "灵", "荒", "惑", "幽", "慢" } },
            { ShiCondition.Ruin,        new[] { "炀", "厉", "荒", "戾", "荡" } },

            { ShiCondition.LostThrone,  new[] { "愍", "哀", "闵", "殇", "怀" } },
            { ShiCondition.Usurped,     new[] { "幽", "愍", "悼", "哀", "闵" } },
            { ShiCondition.MandateFall, new[] { "愍", "哀", "闵", "伤", "恻" } },
            { ShiCondition.EarlyDeath,  new[] { "悼", "殇", "冲", "质", "夭" } },
            { ShiCondition.LostLand,    new[] { "怀", "愍", "思", "悼", "伤" } },

            { ShiCondition.Abdication,  new[] { "让", "简", "靖", "顺", "和" } },
            { ShiCondition.PopGrow,     new[] { "惠", "安", "康", "昭" } },
            { ShiCondition.Neutral,     new[] { "平", "穆", "简", "静", "和", "顺", "定" } },

            { ShiCondition.Unified,     new[] { "武", "文", "桓", "襄", "成", "圣" } },
            { ShiCondition.Founding,    new[] { "武", "文", "元", "桓", "惠", "成" } },
            { ShiCondition.Conquest,    new[] { "武", "桓", "襄", "烈", "威", "庄" } },
            { ShiCondition.Revival,     new[] { "宣", "景", "章", "烈", "威" } },
            { ShiCondition.Institution, new[] { "章", "宪", "文", "懿" } },
            { ShiCondition.Culture,     new[] { "文", "景", "昭", "懿", "惠", "章" } },
            { ShiCondition.Vassalage,   new[] { "襄", "成", "威", "烈", "桓" } },
            { ShiCondition.Valor,       new[] { "庄", "威", "烈", "武" } },
            { ShiCondition.Order,       new[] { "肃", "靖", "定", "静" } },
            { ShiCondition.Peace,       new[] { "康", "安", "靖", "和", "顺" } },
            { ShiCondition.Benevolent,  new[] { "懿", "惠", "文", "康" } }
        };

        /// <summary>条件池（有序，越靠前越贴切）；无对应池返回 null。</summary>
        public static string[] PoolFor(ShiCondition c)
        {
            string[] pool;
            return ConditionPools.TryGetValue(c, out pool) ? pool : null;
        }

        /// <summary>判定情形。顺序与 Classify 完全一致，保证"类别"与"情形"不打架。</summary>
        public static ShiCondition ConditionOf(EmperorFacts f)
        {
            if (f.Tyrant) return ShiCondition.Tyrant;
            if (f.Foolish) return ShiCondition.Foolish;
            if (f.LostThrone) return ShiCondition.LostThrone;
            if (f.Usurped) return ShiCondition.Usurped;
            if (f.Abdicated && !HighMeritAbdication(f)) return ShiCondition.Abdication;
            if (f.CityDelta <= -4 && f.TreasDelta <= -800) return ShiCondition.Ruin;
            if (f.MandateCollected && f.EndMandate < 30f) return ShiCondition.MandateFall;
            if (f.ReignYears < 5 && !f.IsFirstOfReign && f.EndAge > 0 && f.EndAge < 50)
                return ShiCondition.EarlyDeath;
            if (f.CityDelta < 0) return ShiCondition.LostLand;
            if (f.Unified) return ShiCondition.Unified;
            if (f.IsFirstOfReign) return ShiCondition.Founding;
            if (f.CityDelta >= 3) return ShiCondition.Conquest;
            if (f.MandDelta >= 8f && f.StabDelta >= 0f) return ShiCondition.Revival;
            if (f.PolicyCount >= 6 && f.StabDelta >= 0f) return ShiCondition.Institution;
            if (f.PolicyCount >= 4 && f.StabDelta > 0f) return ShiCondition.Culture;
            if (f.VassalDelta >= 2) return ShiCondition.Vassalage;
            if (f.StabDelta >= 12f) return ShiCondition.Valor;
            if (f.EndStability >= 80f && f.StabDelta >= 5f) return ShiCondition.Order;
            if (f.EndStability >= 70f && f.StabDelta >= 0f) return ShiCondition.Peace;
            if (f.StabDelta >= 3f) return ShiCondition.Culture;
            if (f.Benevolent) return ShiCondition.Benevolent;
            if (f.PopPct >= 20f) return ShiCondition.PopGrow;
            return ShiCondition.Neutral;
        }

        /// <summary>
        /// 在条件池内按种子取字（排除已用）。池内无可用字时返回 null，调用方保留原字。
        /// </summary>
        public static string PickFromCondition(ShiCondition cond, HashSet<string> usedShi, int seed)
        {
            var pool = PoolFor(cond);
            if (pool == null || pool.Length == 0) return null;

            var avail = new List<string>(pool.Length);
            foreach (var s in pool)
            {
                if (usedShi != null && usedShi.Contains(s)) continue;
                avail.Add(s);
            }
            if (avail.Count == 0) return null;
            return PickBySeed(avail, seed);
        }
    }
}
