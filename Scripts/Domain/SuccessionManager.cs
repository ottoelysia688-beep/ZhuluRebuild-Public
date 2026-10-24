using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 继承制系统：继承人选定 + 退位/禅让 + 摄政 + 无嗣改朝换代。
    /// 嫡长子继承制设计。
    /// </summary>
    public class SuccessionManager : ISnapshotSection<SuccessionManager.SuccessionState>
    {
        public static SuccessionManager Instance { get; } = new SuccessionManager();

        private const string KEY_HEIR = "heir_id";
        private const string KEY_REGENCY = "regency";
        private const string KEY_REIGN_START = "reign_start";

        private const int ABDICATION_TENURE = 30;
        private const float ABDICATION_CHANCE = 0.10f;
        private const int ABDICATION_POWER_THRESHOLD = 90;
        private const int REGENCY_LEVEL_THRESHOLD = 5;

        private SuccessionManager() { }

        // ═══════════════════════ 主循环 ═══════════════════════

        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            EnsureRoyalFertility(k); // 2026-08-19：国王自动获"兴旺"（原版 fertile——国祚延续托底）
            EnsureHeir(k, kId, year);
            CheckAbdication(k, kId, year);
            TickRegency(k, kId, year);
        }

        // ═══ 2026-08-19 王室繁衍托底 ═══
        // 用户实测：国王常无子嗣 → 绝嗣改朝频繁。原版生育由 fertile/infertile 特质驱动，
        // 国王若不孕则基本断后。托底两步（幂等，新王登基一年内生效）：
        //   1) 移除"不孕"（infertile——不孕直接断根，与 fertile 并存也难翻身）；
        //   2) 赋予"兴旺"（fertile——原版多产特质，已验证存在于 0.51.2）。
        // 注：infertile 移除仅限国王本人（国祚延续 > 特质随机性；非国王不受影响）。
        private static void EnsureRoyalFertility(Kingdom k)
        {
            try
            {
                Actor king = k.king;
                if (king == null || !king.isAlive()) return;
                bool changed = false;
                if (king.hasTrait("infertile")) { try { king.removeTrait("infertile"); changed = true; } catch { } }
                if (!king.hasTrait("fertile")) { try { king.addTrait("fertile"); changed = true; } catch { } }
                if (changed)
                    ZhuluContext.Instance.LogInfo($"[Succession] 国王 {king.getName() ?? "某君"} 王室繁衍托底（移除不孕/赐兴旺）");
            }
            catch { }
        }

        // ═══════════════════════ 继承人选定 ═══════════════════════

        private void EnsureHeir(Kingdom k, string kId, int year)
        {
            if (k.king == null || !k.king.isAlive()) return;

            string heirId = KingdomDataExt.GetStr(k, KEY_HEIR);

            // 检查现有继承人是否仍存活
            if (!string.IsNullOrEmpty(heirId))
            {
                Actor heir = FindActorInKingdom(k, heirId);
                if (heir != null && heir.isAlive()) return; // 继承人健在，无需更换
                // 继承人已死，清除
                KingdomDataExt.SetStr(k, KEY_HEIR, "");
            }

            // 选定新继承人
            Actor newHeir = SelectHeir(k, kId);
            if (newHeir == null) return;

            string newHeirId = newHeir.getID().ToString();
            KingdomDataExt.SetStr(k, KEY_HEIR, newHeirId);

            string kingId = k.king.getID().ToString();
            string title = GetHeirTitle(kId, k, kingId, newHeirId);
            string heirName = newHeir.getName() ?? "某人";

            ZhuluContext.Instance.LogInfo($"[SuccessionManager] 册立继承人: 王国={k.name}, 继承人={heirName}, 称谓={title}");

            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                k.name ?? "", heirName,
                $"册立<color=#6ca3fd>{heirName}</color>为【{title}】，以承大统。", false);
        }

        /// <summary>按优先级选定继承人：子→孙→同族→全国最强。</summary>
        private Actor SelectHeir(Kingdom k, string kId)
        {
            if (k.king == null) return null;
            string kingId = k.king.getID().ToString();
            string kName = k.name ?? "";

            // ══ 批次11·继承完善：严格辈分阶梯（历史法统）══
            // 子(嫡长优先) → 孙 → 兄弟(兄终弟及) → 侄 → 同族 → 全国
            // 铁律：绝不传父辈/舅辈——所有候选若为今上尊长(其子女含今上)直接排除；
            //       同辈内按"成年者优先的长幼序"，避免婴儿/低能者继位。
            System.Func<List<Actor>, Actor> pickElder = list =>
            {
                Actor adult = null;
                foreach (var a in list)
                {
                    if (a == null || !a.isAlive() || WasFormerKing(k, a)) continue;
                    int age = 0; try { age = a.getAge(); } catch { }
                    if (age >= 12) return a;
                    if (adult == null) adult = a;
                }
                return adult;
            };

            System.Func<Actor, bool> notAncestor = cand =>
            {
                if (cand == null) return false;
                try
                {
                    // 候选的子女里包含今上 → 候选是今上的父/母 → 排除
                    foreach (var c in KinshipTracker.GetAliveChildren(k, cand.getID().ToString()))
                        if (c != null && c.getID().ToString() == kingId) return false;
                }
                catch { }
                return true;
            };

            // 1. 子（长子优先；未成年弟位由年长者顶替）
            var sons = new List<Actor>();
            try { sons = KinshipTracker.GetAliveChildren(k, kingId).Where(a => !WasFormerKing(k, a)).ToList(); } catch { }
            sons.Sort((a, b) =>
            {
                int aa = 0, bb = 0; try { aa = a.getAge(); } catch { } try { bb = b.getAge(); } catch { }
                return bb.CompareTo(aa);
            });
            Actor best = pickElder(sons);
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=子(长幼序), 候选={sons.Count}, 选中={best.getName()}");
                return best;
            }
            // 2. 孙（隔代继统：以祖辈支系最近为先，长幼序）
            var grandsons = new List<Actor>();
            try { grandsons = KinshipTracker.GetAliveGrandchildren(k, kingId).Where(a => !WasFormerKing(k, a)).ToList(); } catch { }
            grandsons.Sort((a, b) =>
            {
                int aa = 0, bb = 0; try { aa = a.getAge(); } catch { } try { bb = b.getAge(); } catch { }
                return bb.CompareTo(aa);
            });
            best = grandsons.Count > 0 ? grandsons[0] : null;
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=孙(长幼序), 选中={best.getName()}");
                return best;
            }

            // 2b. 全球反查漏网子嗣（须仍属本国且非他国国王）
            var worldSons = new List<Actor>();
            try
            {
                worldSons = KinshipTracker.GetAllChildrenWorldwide(kingId)
                    .Where(a => !WasFormerKing(k, a))
                    .Where(a => a.kingdom == k && !IsForeignKing(a))
                    .ToList();
            }
            catch { }
            worldSons.Sort((a, b) =>
            {
                int aa = 0, bb = 0; try { aa = a.getAge(); } catch { } try { bb = b.getAge(); } catch { }
                return bb.CompareTo(aa);
            });
            best = worldSons.Count > 0 ? worldSons[0] : null;
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=全球子(长幼序), 选中={best.getName()}");
                return best;
            }

            // 3. 兄弟（兄终弟及：取最年长成年弟）——父辈身份经原版 getParents() 取
            Actor father = null;
            try
            {
                var ps = k.king.getParents();
                if (ps != null)
                    foreach (var pp in ps) { if (pp != null) { father = pp; break; } }
            }
            catch { }
            if (father != null && father.isAlive() == false) { /* 已故父亦可作兄弟来源 */ }
            var brothers = new List<Actor>();
            try
            {
                if (father != null)
                    brothers = KinshipTracker.GetAliveChildren(k, father.getID().ToString())
                        .Where(a => a.getID().ToString() != kingId)
                        .Where(a => !WasFormerKing(k, a))
                        .ToList();
            }
            catch { }
            brothers.Sort((a, b) =>
            {
                int aa = 0, bb = 0; try { aa = a.getAge(); } catch { } try { bb = b.getAge(); } catch { }
                return bb.CompareTo(aa);   // 兄优先于弟
            });
            best = brothers.Count > 0 ? brothers[0] : null;
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=兄弟(兄终弟及), 选中={best.getName()}");
                return best;
            }

            // 4. 侄（兄弟之子，长幼序）
            var nephews = new List<Actor>();
            try
            {
                foreach (var br in brothers)
                {
                    foreach (var c in KinshipTracker.GetAliveChildren(k, br.getID().ToString()))
                        if (c != null && !WasFormerKing(k, c)) nephews.Add(c);
                }
            }
            catch { }
            nephews.Sort((a, b) =>
            {
                int aa = 0, bb = 0; try { aa = a.getAge(); } catch { } try { bb = b.getAge(); } catch { }
                return bb.CompareTo(aa);
            });
            best = nephews.Count > 0 ? nephews[0] : null;
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=侄, 选中={best.getName()}");
                return best;
            }

            // 3. 国王同 clan 成员中评分最高者（排除曾任君主）
            best = GetBestClanMember(k, k.king);
            if (best != null && !notAncestor(best)) best = null;   // 批次11：尊长不得入继
            if (best != null && IsSeniorToKing(k, best)) best = null;   // 明显尊长(年长于今上)排除
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=同族, 选中={best.getName()}");
                return best;
            }

            // 4. 全国评分最高 Actor（外族入继，排除曾任君主）
            best = GetBestInKingdom(k);
            if (best != null && !notAncestor(best)) best = null;
            if (best != null && IsSeniorToKing(k, best)) best = null;
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=全国(外族入继), 选中={best.getName()}");
                return best;
            }

            // 阶段2B 自检兜底（2026-08-15）：全部路径无候选（小国/孤王家族被排除规则清空）→
            // 放宽曾任君主/高龄限制重选——宁可旧王复辟也不绝嗣（日志实证：蔡邑国"无任何可用继承人"）
            best = GetBestClanMemberRelaxed(k, k.king);
            if (best != null)
            {
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=兜底同族(放宽曾任/高龄), 选中={best.getName()}");
                return best;
            }
            best = GetBestInKingdomRelaxed(k);
            if (best != null)
                ZhuluContext.Instance.LogInfo($"[SuccessionManager] {kName} 继承路径=兜底全国(放宽曾任/高龄), 选中={best.getName()}");
            else
                ZhuluContext.Instance.LogWarning($"[SuccessionManager] {kName} 无任何可用继承人！（兜底后仍无）");
            return best;
        }

        /// <summary>批次11：候选明显尊长判定（年龄大于今上即视为父辈级，禁止入继——舅/叔伯皆因此被挡）。</summary>
        private bool IsSeniorToKing(Kingdom k, Actor cand)
        {
            try
            {
                if (k?.king == null || cand == null) return false;
                int ka = 0, ca = 0;
                try { ka = k.king.getAge(); } catch { }
                try { ca = cand.getAge(); } catch { }
                return ca > ka + 2;
            }
            catch { return false; }
        }

        private Actor GetBestActor(List<Actor> candidates)
        {
            Actor best = null;
            int bestScore = -1;
            foreach (var a in candidates)
            {
                if (a == null || !a.isAlive()) continue;
                if (!IsRuleCapable(a)) continue;   // 阶段3H-22：船等载具不可为王
                int score = CalcScore(a);
                if (score > bestScore) { bestScore = score; best = a; }
            }
            return best;
        }

        private Actor GetBestClanMember(Kingdom k, Actor king)
        {
            if (king.clan == null) return null;
            Actor best = null;
            int bestScore = -1;
            try
            {
                foreach (Actor unit in king.clan.units)
                {
                    if (unit == null || !unit.isAlive() || unit == king) continue;
                    if (!IsRuleCapable(unit)) continue;   // 阶段3H-22：船等载具不可为王
                    if (unit.kingdom != k) continue;
                    // 阶段2A A1-2：排除曾任君主（退位老王不能再当选）
                    if (WasFormerKing(k, unit)) continue;
                    int score = CalcScore(unit);
                    // A1-3 同族晚辈优先：辈分低（年轻/后代）同分时优先——按年龄轻者优先微调
                    if (score > bestScore) { bestScore = score; best = unit; }
                    else if (score == bestScore && best != null)
                    {
                        int ageU = 0, ageB = 0;
                        try { ageU = unit.getAge(); ageB = best.getAge(); } catch { }
                        if (ageU < ageB) best = unit;
                    }
                }
            }
            catch { }
            return best;
        }

        /// <summary>阶段3H-22：王位适格过滤——船等载具 Actor（asset.is_boat）不可为王。
        /// 文明死绝时"全国择优"兜底曾把船选成国王（用户实锤"船只当上国王"）。</summary>
        private static bool IsRuleCapable(Actor unit)
        {
            if (unit == null || !unit.isAlive()) return false;
            try
            {
                // 阶段3H-22b：恢复原设定——只有智慧生物可入选（原版 fitToRule/isUnitFitToRule 同精神）。
                // 船等载具（asset.is_boat）与非智慧生物一律不得为王；boat 属性数值高，
                // '全国择优'时曾评分反超真市民（用户实锤：大量人口在时船当国王）。
                if (unit.asset != null && unit.asset.is_boat) return false;
                if (!unit.isSapient()) return false;
                if (!unit.asset.civ) return false;
            }
            catch { }
            return true;
        }

        /// <summary>兜底：同族（不排除曾任君主/高龄）——防绝嗣。</summary>
        private Actor GetBestClanMemberRelaxed(Kingdom k, Actor king)
        {
            if (king.clan == null) return null;
            Actor best = null;
            int bestScore = -1;
            try
            {
                foreach (Actor unit in king.clan.units)
                {
                    if (unit == null || !unit.isAlive() || unit == king) continue;
                    if (!IsRuleCapable(unit)) continue;   // 阶段3H-22：船等载具不可为王
                    if (unit.kingdom != k) continue;
                    if (!IsRuleCapable(unit)) continue;
                    int score = CalcScore(unit);
                    if (score > bestScore) { bestScore = score; best = unit; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>兜底：全国（不排除曾任君主/高龄）——防绝嗣。</summary>
        private Actor GetBestInKingdomRelaxed(Kingdom k)
        {
            Actor best = null;
            int bestScore = -1;
            try
            {
                if (k.units == null) return null;
                foreach (Actor unit in k.units)
                {
                    if (unit == null || !unit.isAlive() || unit == k.king) continue;
                    if (!IsRuleCapable(unit)) continue;
                    int score = CalcScore(unit);
                    if (score > bestScore) { bestScore = score; best = unit; }
                }
            }
            catch { }
            return best;
        }

        private Actor GetBestInKingdom(Kingdom k)
        {
            Actor best = null;
            int bestScore = -1;
            try
            {
                if (k.units == null) return null;
                foreach (Actor unit in k.units)
                {
                    if (unit == null || !unit.isAlive() || unit == k.king) continue;
                    // 阶段2A A1-2：排除曾任君主；A1-4：超高龄（>90）不再考虑
                    if (WasFormerKing(k, unit)) continue;
                    try { if (unit.getAge() > 90) continue; } catch { }
                    int score = CalcScore(unit);
                    if (score > bestScore) { bestScore = score; best = unit; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>2026-08-20：是否为他国在职国王（isKing 且非本国）。</summary>
        private static bool IsForeignKing(Actor a)
        {
            try { return a.isKing(); }
            catch { return false; }
        }

        /// <summary>阶段2A A1-2：是否曾任本国王位（原版 past_rulers 权威记录）。</summary>
        private bool WasFormerKing(Kingdom k, Actor actor)
        {
            if (actor == null || k?.data?.past_rulers == null) return false;
            try
            {
                long aid = actor.getID();
                foreach (var entry in k.data.past_rulers)
                {
                    if (entry != null && entry.id == aid) return true;
                }
            }
            catch { }
            return false;
        }

        private int CalcScore(Actor a)
        {
            try
            {
                int aAge = 0; try { aAge = a.getAge(); } catch { }
                // 阶段1 J1：太子用独立权重（officeIdx=7：偏治理外交，非丞相全能标准）
                var (score, _) = OfficialRatingManager.CalcOfficialScore(a, 7, aAge); // 阶段2B 方案3
                return score;
            }
            catch { return 0; }
        }

        // ═══════════════════════ 继承人称呼 ═══════════════════════

        public string GetHeirTitle(string kId, Kingdom k, string kingId, string heirId)
        {
            if (k != null && KingdomCivilizationService.Instance.IsNomadic(k)) return "继承人";
            bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
            int power = KingdomExt.GetPowerValue(kId);
            int cities = k.cities != null ? k.cities.Count : 0;

            if (isMandate)
            {
                if (power >= 60)
                {
                    bool isSon = KinshipTracker.IsParent(kingId, heirId);
                    return isSon ? "太子" : "储君";
                }
                return "储君";
            }
            else
            {
                return cities >= 6 ? "世子" : "继承人";
            }
        }

        // ═══════════════════════ 继承执行（国王死亡/变更时） ═══════════════════════

        /// <summary>国王变更时调用：清除旧继承人、处理新国王旧官职、判定摄政。</summary>
        public void OnKingChange(string kId, string newKingName, int year)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kId);
            if (k == null) return;

            // 清除旧继承人记录
            KingdomDataExt.SetStr(k, KEY_HEIR, "");

            // 记录新王在位起始年
            KingdomDataExt.SetLong(k, KEY_REIGN_START, year);

            // Bug修复：新国王若仍担任官职，清除之
            DismissKingFromOffice(k, kId, year);

            // 判定摄政
            if (k.king != null && k.king.isAlive())
            {
                int level = 1;
                try { level = k.king.level; } catch { }
                if (level < REGENCY_LEVEL_THRESHOLD)
                {
                    KingdomDataExt.SetLong(k, KEY_REGENCY, 1);
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                        k.name ?? "", newKingName,
                        $"<color=#FFA500>新君年幼，丞相摄政，辅佐朝政。</color>", false);
                }
                else
                {
                    KingdomDataExt.SetLong(k, KEY_REGENCY, 0);
                }

                // 立即为新王选定继承人（不等下一轮 Tick）
                EnsureHeir(k, kId, year);
                // 2026-08-19 修复：登基即托底繁衍——原只年度结算调用，新王登基至下个年度
                // Tick 间存在窗口期（最长约 1 年），不孕新王可能期间绝嗣
                EnsureRoyalFertility(k);
            }
        }

        /// <summary>Bug修复：检查新国王是否仍担任官职，若在则罢免。</summary>
        private void DismissKingFromOffice(Kingdom k, string kId, int year)
        {
            if (k.king == null) return;
            string kingPersonId = k.king.getID().ToString();
            string kName = k.name ?? "";

            var records = OfficeManager.Instance.GetRecordsPublic(kId);
            if (records == null) return;

            foreach (var rec in records)
            {
                if (rec.PersonId == kingPersonId)
                {
                    OfficeManager.Instance.DismissOfficer(kId, kName, rec.OfficeId, year, "即位");
                    break;
                }
            }
        }

        // ═══════════════════════ 退位机制 ═══════════════════════

        private void CheckAbdication(Kingdom k, string kId, int year)
        {
            // 2026-10-13：「逐鹿设置 → 功能开关 → 禅让机制」开关（默认 true = 原行为，零回归）。
            // 关闭后本路径完全停用 —— 君位只能靠病故继承（原版死亡换君）或篡位（党争/腐败触发）更替。
            // 用全限定名以免依赖 using。
            if (!ZhuluRebuild.Core.ZhuluConfig.Instance.AbdicationEnabled) return;
            if (k.king == null || !k.king.isAlive()) return;

            int power = KingdomExt.GetPowerValue(kId);
            if (power >= ABDICATION_POWER_THRESHOLD) return; // 雄主不退

            long reignStart = KingdomDataExt.GetLong(k, KEY_REIGN_START, year);
            int tenure = year - (int)reignStart;
            if (tenure < ABDICATION_TENURE) return;

            if (UnityEngine.Random.value >= ABDICATION_CHANCE) return;

            // 触发禅让
            string heirId = KingdomDataExt.GetStr(k, KEY_HEIR);
            Actor heir = string.IsNullOrEmpty(heirId) ? null : FindActorInKingdom(k, heirId);

            if (heir == null || !heir.isAlive())
            {
                // 无继承人，即时选出
                heir = SelectHeir(k, kId);
            }
            if (heir == null) return;

            string oldKingName = k.king.getName() ?? "旧君";
            string heirName = heir.getName() ?? "某人";

            try
            {
                k.setKing(heir, false);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[SuccessionManager] 禅让 setKing 失败: {ex.Message}");
                return;
            }
            // 阶段2A A1-6：标记禅让（换君观察分类用）
            DynastyObserver.Instance.MarkAbdication(kId);

            // 禅让后处理
            KingdomDataExt.SetStr(k, KEY_HEIR, "");
            KingdomDataExt.SetLong(k, KEY_REIGN_START, year);
            KingdomDataExt.SetLong(k, KEY_REGENCY, 0);
            DismissKingFromOffice(k, kId, year);
            StabilityManager.Instance.AddExternalBonus(kId, -5f);

            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                k.name ?? "", heirName,
                $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{oldKingName}</color>禅位于<color=#6ca3fd>{heirName}</color>，改元新纪。", true);
            WorldTip.showNow($"【{k.name}】{oldKingName}禅位于{heirName}！", pTranslate: false, "top", 6f, UI.ZhuluUIStyle.Hex.Azure);
            ZhuluContext.Instance.LogInfo($"[SuccessionManager] 禅让: {k.name} {oldKingName}→{heirName}");
        }

        // ═══════════════════════ 摄政机制 ═══════════════════════

        private void TickRegency(Kingdom k, string kId, int year)
        {
            long regency = KingdomDataExt.GetLong(k, KEY_REGENCY, 0);
            if (regency != 1) return;

            if (k.king == null || !k.king.isAlive())
            {
                KingdomDataExt.SetLong(k, KEY_REGENCY, 0);
                return;
            }

            int kingLevel = 1;
            try { kingLevel = k.king.level; } catch { }

            // 国王成长 → 亲政
            if (kingLevel >= REGENCY_LEVEL_THRESHOLD)
            {
                KingdomDataExt.SetLong(k, KEY_REGENCY, 0);
                string kingName = k.king.getName() ?? "帝";
                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                    k.name ?? "", kingName,
                    $"<color=#88FF88>{kingName}亲政，摄政结束，乾纲独断。</color>", false);
                return;
            }

            // 摄政期间：皇权流失（C2b-ii 2026-10-01 对冲：-1/年 → 两年-1 [PLACEHOLDER]）——
            // 轨迹实测「幼弱」占 29.5% 年份，-1/年 累积致多国 power≤5 深坑（渐进 +5/年 追不上）；
            // 减半保留"摄政损耗"语义，使渐进逼近可覆盖流失
            int power = KingdomExt.GetPowerValue(kId);
            if (power > 0 && year % 2 == 0) KingdomExt.SetPowerValue(kId, power - 1);

            // 稳定度每年 -2
            StabilityManager.Instance.AddExternalBonus(kId, -2f);

            // 权臣废立检查：丞相腐败>=70 且权势>=80 且皇权<40
            if (power < 40 && year % 10 == 0)
            {
                float pmCorr = CorruptionManager.Instance.GetCorruption(kId, "prime_minister");
                float pmPower = CorruptionManager.Instance.GetPower(kId, "prime_minister");

                if (pmCorr >= 70f && pmPower >= 65f && UnityEngine.Random.value < 0.30f) // 阶段2U：80→65 对齐权势增速
                {
                    TriggerRegentUsurpation(k, kId, year);
                }
            }
        }

        private void TriggerRegentUsurpation(Kingdom k, string kId, int year)
        {
            var app = OfficeManager.Instance.GetAppointment(kId, "prime_minister");
            if (app == null || string.IsNullOrEmpty(app.PersonId)) return;

            Actor usurper = FindActorInKingdom(k, app.PersonId);
            if (usurper == null || !usurper.isAlive()) return;

            string usurperName = usurper.getName() ?? "权臣";
            string oldKingName = k.king != null ? (k.king.getName() ?? "幼帝") : "幼帝";
            Actor oldKing = null;
            try { oldKing = k.king; } catch { }

            try
            {
                k.setKing(usurper, false);
            }
            catch
            {
                return;
            }

            // 阶段2A A1-6 对齐：标记篡位（换君观察分类用——此前漏打标，摄政篡位被分类为普通换君，
            // 且无礼治重置/前朝宗室标记；对照 CorruptionManager.TriggerUsurpation 同款）
            DynastyObserver.Instance.MarkUsurpation(kId);
            // 2026-08-18 改朝换代全套：天命新朝卡片/国号/前朝宗室/谱牒隔离/清洗旧臣/中央权力重置
            DynastyObserver.Instance.ProcessDynastyChange(k, kId, usurper, oldKing, year);

            // 清除旧官职
            OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", "prime_minister", year, "篡位");
            KingdomDataExt.SetLong(k, KEY_REGENCY, 0);
            KingdomDataExt.SetStr(k, KEY_HEIR, "");
            KingdomDataExt.SetLong(k, KEY_REIGN_START, year);
            StabilityManager.Instance.AddExternalBonus(kId, -20f);

            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                k.name ?? "", usurperName,
                $"<color=#FF0000>丞相{usurperName}废{oldKingName}自立！权臣篡位，改朝换代。</color>", true);
            WorldTip.showNow($"【{k.name}】丞相{usurperName}废帝自立！", pTranslate: false, "top", 8f, UI.ZhuluUIStyle.Hex.Negative);
            ZhuluContext.Instance.LogInfo($"[SuccessionManager] 权臣废立: {k.name} {usurperName}");
        }

        // ═══════════════════════ 公开接口 ═══════════════════════

        public bool IsRegency(string kId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kId);
            if (k == null) return false;
            return KingdomDataExt.GetLong(k, KEY_REGENCY, 0) == 1;
        }

        public string GetHeirId(string kId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kId);
            if (k == null) return null;
            return KingdomDataExt.GetStr(k, KEY_HEIR);
        }

        // ═══════════════════════ 辅助 ═══════════════════════

        private Actor FindActorInKingdom(Kingdom k, string actorId)
        {
            if (k?.units == null || string.IsNullOrEmpty(actorId)) return null;
            try
            {
                foreach (Actor unit in k.units)
                {
                    if (unit == null || !unit.isAlive()) continue;
                    if (!IsRuleCapable(unit)) continue;   // 阶段3H-22：船等载具不可为王
                    if (unit.getID().ToString() == actorId) return unit;
                }
            }
            catch { }
            return null;
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class SuccessionState
        {
            public string Heir;
            public long ReignStart;
            public long Regency;
        }
        public string SectionName => "succession";
        public Dictionary<string, SuccessionState> CaptureStates()
        {
            var d = new Dictionary<string, SuccessionState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                d[k.id.ToString()] = new SuccessionState
                {
                    Heir = KingdomDataExt.GetStr(k, KEY_HEIR, ""),
                    ReignStart = KingdomDataExt.GetLong(k, KEY_REIGN_START, 0),
                    Regency = KingdomDataExt.GetLong(k, KEY_REGENCY, 0),
                };
            }
            return d;
        }
        public void ApplyStates(Dictionary<string, SuccessionState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                KingdomDataExt.SetStr(k, KEY_HEIR, kv.Value.Heir ?? "");
                KingdomDataExt.SetLong(k, KEY_REIGN_START, kv.Value.ReignStart);
                KingdomDataExt.SetLong(k, KEY_REGENCY, kv.Value.Regency);
            }
        }

        public void MigrateFromCustomData() { }
        public void ResetRuntime() { }

}
}
