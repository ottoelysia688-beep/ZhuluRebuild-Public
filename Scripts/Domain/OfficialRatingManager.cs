using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    public static class OfficialRatingManager
    {
        /// <summary>
        /// 根据官员职位序号(0~5为六部尚书，6为丞相)及原版4大统治属性(智谋,管理,军略,外交)+等级计算纯数值评分 (0 ~ 100 分)
        /// 新公式（2026-08-01 第三版，纯四维计算，无年龄/无等级项）：
        ///   score = clamp( 分段线性(Σw·属性), 0, 100 )
        /// 设计依据：
        ///   1. 世界盒子四维属性实测均值≈3、P90≈6、极值17~19（存档统计）；
        ///   2. 第二版(24+16√base+等级)仍被反馈"开局几年就有90分"——等级加成与属性随年龄成长
        ///      都让高分泛滥。第三版去除等级项与年龄项，纯四维：开局世界(属性1~5)最高分仅46，
        ///      实测存档 <40 占 82~92%（"大部分人40分以下"）；
        ///   3. 分段拐点（按存档属性分布标定）：
        ///      base=5.4(属性均值3)→35分、base=10.8(属性P90 6)→51分、base=18(属性10)→67分、
        ///      base=30(属性17)→82分、base=36(属性20)→89分——低段陡(区分庸才)、高段缓(顶尖稀缺)；
        ///   4. 四维全15(世所罕见)才到80分，四维全19~20(理论极限)才逼近90~100——90+只属于
        ///      属性15+的绝顶天才，普通世界几乎不可能出现。
        /// </summary>
        /// <summary>
        /// 考课评分（阶段1 重标定 2026-08-15）。
        /// 输入：四维（最终值）+ 等级 + 官职索引 + 年龄（0=不修正）。
        /// 新拐点按大图实测分布标定：国民 base 均值≈16（→35分）、P90≈30（→55分）、
        /// P99≈45（→75分）、max≈60（→90分）——消除旧标定（均值≈5.4）下的分数通胀
        /// （实测旧公式 38% 国民 B+ 以上、60+ 岁均值 A 级）。
        /// 年龄修正（v6 2026-08-15）：&lt;40 ×1.0 / 40-59 ×0.98 / 60+ ×0.92（防老人垄断，实测 60+ 岁均值 2.2 倍于 <20，修正后差距缩至 ~14 分）。
        /// </summary>
        /// <summary>
        /// Actor 重载（阶段2B 方案3，2026-08-15）：基础值×0.7 + 最终值×0.3 混合。
        /// 依据：存档实测基础值跨图稳定（mean 2.2），最终值（stats 合并含遗传/亚种随机）两图差 2.5 倍
        /// （旧图 6-7 vs 新图 16-17）——纯最终值标定导致官评随世界随机波动（新图 B+ 34.9% 通胀）。
        /// 基础值为主 = 稳定可预测；最终值小权重 = 捕捉特质/成长加成。
        /// </summary>
        /// <summary>读 Actor 基础四维（Actor.data 内部字段，反射访问；0.51.2 stats 合并源码确认 data 是基础层）。</summary>
        // ── 阶段3 性能：反射元数据静态缓存（原每次调用 Traverse.Create+GetProperty，
        //    年度全量采集 4.8 万次/年 反射调 GC 尖峰——缓存后快 1~2 个数量级）──
        private static System.Reflection.FieldInfo _actorDataField;
        private static System.Reflection.PropertyInfo _dataItemProp;
        private static bool _reflectResolved;

        public static int GetBaseStat(Actor a, string key)
        {
            try
            {
                if (a == null) return 0;
                if (!_reflectResolved)
                {
                    _actorDataField = HarmonyLib.AccessTools.Field(typeof(Actor), "data");
                    _dataItemProp = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("ActorData") ?? typeof(object), "Item");
                    _reflectResolved = true;
                }
                object data = _actorDataField != null ? _actorDataField.GetValue(a) : null;
                if (data == null) return 0;
                var prop = _dataItemProp;
                if (prop == null || prop.DeclaringType == null || !prop.DeclaringType.IsInstanceOfType(data))
                {
                    // 缓存缺失或与实际 data 类型不兼容（Item 声明于基类时 IsInstanceOfType 仍复用）时解析一次
                    prop = data.GetType().GetProperty("Item");
                    _dataItemProp = prop;
                }
                if (prop != null)
                {
                    object v = prop.GetValue(data, new object[] { key });
                    return v != null ? Convert.ToInt32(v) : 0;
                }
            }
            catch { }
            return 0;
        }

        // ── 战功访问（2026-08-18 武举/武将榜共享）：击杀数 + 伤害值 ──
        // ActorData.kills 为非公开属性（反射缓存）；伤害走 BaseStats.get("damage")（public 索引器）。
        private static System.Reflection.PropertyInfo _actorKillsProp;

        /// <summary>武举/榜单用：总击杀数（ActorData.kills，反射缓存）。</summary>
        public static int GetActorKills(Actor a)
        {
            try
            {
                if (a == null) return 0;
                if (!_reflectResolved)
                {
                    _actorDataField = HarmonyLib.AccessTools.Field(typeof(Actor), "data");
                    _dataItemProp = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("ActorData") ?? typeof(object), "Item");
                    _reflectResolved = true;
                }
                object data = _actorDataField != null ? _actorDataField.GetValue(a) : null;
                if (data == null) return 0;
                if (_actorKillsProp == null || _actorKillsProp.DeclaringType == null || !_actorKillsProp.DeclaringType.IsInstanceOfType(data))
                {
                    _actorKillsProp = data.GetType().GetProperty("kills");
                }
                if (_actorKillsProp != null && _actorKillsProp.GetValue(data, null) is int k) return k;
            }
            catch { }
            return 0;
        }

        /// <summary>武举/榜单用：当前伤害值（Actor.stats 非公开字段 + BaseStats["damage"]，反射缓存；失败 0）。</summary>
        private static System.Reflection.FieldInfo _actorStatsField;
        private static System.Reflection.MethodInfo _statsGetMethod;

        public static float GetActorDamage(Actor a)
        {
            try
            {
                if (a == null) return 0f;
                if (_actorStatsField == null)
                {
                    _actorStatsField = HarmonyLib.AccessTools.Field(typeof(Actor), "stats");
                }
                object stats = _actorStatsField != null ? _actorStatsField.GetValue(a) : null;
                if (stats == null) return 0f;
                if (_statsGetMethod == null || _statsGetMethod.DeclaringType == null || !_statsGetMethod.DeclaringType.IsInstanceOfType(stats))
                {
                    _statsGetMethod = stats.GetType().GetMethod("get", new[] { typeof(string) });
                }
                if (_statsGetMethod != null && _statsGetMethod.Invoke(stats, new object[] { "damage" }) is float d) return d;
            }
            catch { }
            return 0f;
        }

        /// <summary>战功加分（三榜武将榜与武举同口径）：每 8 杀 +1（封顶 25）+ 每 5 伤害 +1（封顶 10）。</summary>
        public static int GetWarMeritBonus(Actor a)
        {
            int kills = GetActorKills(a);
            float dmg = GetActorDamage(a);
            return Mathf.Min(25, kills / 8) + Mathf.Min(10, Mathf.RoundToInt(dmg / 5f));
        }

        public static (int score, string grade) CalcOfficialScore(Actor a, int officeIdx, int age = 0)
        {
            try
            {
                if (a == null) return (0, "D");
                int bInt = GetBaseStat(a, "intelligence");
                int bSte = GetBaseStat(a, "stewardship");
                int bWar = GetBaseStat(a, "warfare");
                int bDip = GetBaseStat(a, "diplomacy");
                // 0.5/0.5（2026-08-15 三次权重实测定稿）：wonders 期 B+ 5.7% 命中目标；
                // 纪元波动保留为设计特性（奇迹时代人才鼎盛/黑暗纪元凋零）
                int mInt = Mathf.RoundToInt(bInt * 0.5f + a.intelligence * 0.5f);
                int mSte = Mathf.RoundToInt(bSte * 0.5f + a.stewardship * 0.5f);
                int mWar = Mathf.RoundToInt(bWar * 0.5f + a.warfare * 0.5f);
                int mDip = Mathf.RoundToInt(bDip * 0.5f + a.diplomacy * 0.5f);
                return CalcOfficialScore(mInt, mSte, mWar, mDip, a.level, officeIdx, age);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OfficialRatingManager] Actor 评分异常: {ex.Message}");
                return (0, "D");
            }
        }

        /// <summary>纯数值评分（基础+最终混合值由调用方传入；兼容旧调用）。</summary>
        public static (int score, string grade) CalcOfficialScore(int intel, int stew, int war, int dip, int level, int officeIdx, int age = 0)
        {
            // 官职权重表（外交, 智力, 组织, 军事）——按职责侧重设计，权重和 1.7~2.0
            float wDip, wInt, wSte, wWar;
            switch (officeIdx)
            {
                case 0: // 吏部/人事 (智力为主, 组织为辅)
                    wDip = 0.2f; wInt = 0.8f; wSte = 0.6f; wWar = 0.1f; break;
                case 1: // 户部/财政 (组织为主, 智力为辅)
                    wDip = 0.1f; wInt = 0.6f; wSte = 0.9f; wWar = 0.1f; break;
                case 2: // 礼部/外交 (外交为主)
                    wDip = 0.9f; wInt = 0.5f; wSte = 0.2f; wWar = 0.1f; break;
                case 3: // 兵部/军事 (军事为主)
                    wDip = 0.1f; wInt = 0.5f; wSte = 0.3f; wWar = 0.9f; break;
                case 4: // 刑部/司法 (智力与组织并重)
                    wDip = 0.2f; wInt = 0.7f; wSte = 0.7f; wWar = 0.1f; break;
                case 5: // 工部/工程 (组织为主, 军事为辅)
                    wDip = 0.1f; wInt = 0.3f; wSte = 0.9f; wWar = 0.5f; break;
                case 7: // 太子/储君（阶段1 J1：偏治理+外交，非全能丞相标准）
                    wDip = 0.6f; wInt = 0.4f; wSte = 0.7f; wWar = 0.3f; break;
                default: // 6 首相/丞相 (全能大局, 偏治理)
                    wDip = 0.3f; wInt = 0.7f; wSte = 0.7f; wWar = 0.1f; break;
            }

            // 加权属性 base（实测：国民均值≈16、P90≈30、P99≈45、max≈60）
            float baseAttr = wDip * dip + wInt * intel + wSte * stew + wWar * war;
            if (baseAttr < 0f) baseAttr = 0f;

            // 分段线性映射（阶段1 重标定 v6，2026-08-15 三次实测迭代定稿）：
            //   base≤16   ×2.9        （国民均值16→46分，中下）
            //   16~30     46+(b-16)*1.2  （P90 30→63分）
            //   30~45     63+(b-30)*1.25 （P99 45→82分）
            //   45~60     82+(b-45)*1.0   （max 60→97分）
            //   >60       97+(b-60)*0.2   （100 封顶，绝世天才）
            // 实测目标分布（v6 验证 n=3146）：mean≈40、B+ 以上≈6%、A≈0.2%、S 极罕见
            float scoreFloat;
            if (baseAttr <= 16f)
            {
                scoreFloat = baseAttr * 2.9f;
            }
            else if (baseAttr <= 30f)
            {
                scoreFloat = 46f + (baseAttr - 16f) * 1.2f;
            }
            else if (baseAttr <= 45f)
            {
                scoreFloat = 63f + (baseAttr - 30f) * 1.25f;
            }
            else if (baseAttr <= 60f)
            {
                scoreFloat = 82f + (baseAttr - 45f) * 1.0f;
            }
            else
            {
                scoreFloat = 97f + (baseAttr - 60f) * 0.2f;
            }

            int score = Mathf.Clamp(Mathf.RoundToInt(scoreFloat), 0, 100);

            // 年龄修正（v6：轻微压制高龄防垄断——实测 60+ 均值 2.2 倍于 <20，修正后差距缩至 ~14 分）
            if (age > 0)
            {
                float ageFactor = age < 40 ? 1.0f : (age < 60 ? 0.98f : 0.92f);
                score = Mathf.Clamp(Mathf.RoundToInt(score * ageFactor), 0, 100);
            }

            string grade = GetGradeText(score);

            return (score, grade);
        }

        public static string GetGradeText(int score)
        {
            if (score >= 85) return "【上上 · 治世栋梁】";
            if (score >= 70) return "【上中 · 干练能臣】";
            if (score >= 55) return "【中上 · 恪尽职守】";
            if (score >= 50) return "【中中 · 循规蹈矩】";
            if (score >= 35) return "【中下 · 才疏学浅】";
            return "【下下 · 尸位素餐】";
        }

        /// <summary>
        /// 官员执行系数：考评分直接作为施政效能进入国库/政策等公式。
        /// S=1.3x, A=1.15x, B=1.0x, C=0.8x, D=0.5x —— 官员质量第一次有了真金白银的意义。
        /// </summary>
        public static float GetExecutionFactor(int score)
        {
            if (score >= 85) return 1.3f;
            if (score >= 70) return 1.15f;
            if (score >= 50) return 1.0f;
            if (score >= 35) return 0.8f;
            return 0.5f;
        }

        /// <summary>
        /// 统一评级系数表（阶段1 A3+H1：国策/政策放缩统一引用此表；S 级 1.35 防超原版修正幅度）。
        /// S=1.35x, A=1.2x, B=1.0x, C=0.8x, D=0.5x。
        /// </summary>
        public static float GetGradeFactor(int score)
        {
            if (score >= 85) return 1.35f;
            if (score >= 70) return 1.2f;
            if (score >= 50) return 1.0f;
            if (score >= 35) return 0.8f;
            return 0.5f;
        }
    }
}
