using System;
using System.Collections.Generic;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;

namespace ZhuluRebuild.Domain
{
    public enum PowerBand
    {
        Collapse,     // 0~20   王室衰微
        Shared,       // 21~40  贵族共治
        Centered,     // 41~60  中央主导
        Centralized,  // 61~80  君主集权
        Absolute      // 81~100 极权郡县
    }

    public static class RegimeManager
    {
        public const int POWER_CHECK_INTERVAL_YEARS = 1; // 年度渐进（2026-08-18 重构：原10年跳变与稳定/权臣年变脱节，长年趋0失真）
        private static int _lastCheckYear = -1;
        private static bool _traceOverflowWarned; // C2a：power_trace.csv 超 2MB 只告警一次

        public static PowerBand GetBand(int power)
        {
            if (power <= 20) return PowerBand.Collapse;
            if (power <= 40) return PowerBand.Shared;
            if (power <= 60) return PowerBand.Centered;
            if (power <= 80) return PowerBand.Centralized;
            return PowerBand.Absolute;
        }

        public static string GetBandName(int power)
        {
            return GetBand(power) switch
            {
                PowerBand.Collapse => "王室衰微",
                PowerBand.Shared => "贵族共治",
                PowerBand.Centered => "中央主导",
                PowerBand.Centralized => "君主集权",
                PowerBand.Absolute => "极权郡县",
                _ => "未知政体"
            };
        }

        /// <summary>
        /// 政体五档国策效能系数（阶段2N W6 实装）：温和映射 ±15% 内，接入 PolicyManager 颁行放缩主路径。
        /// C6（2026-10-01）删除死代码 GetPolicyMultiplier（0.3~1.6 档差版，全库 0 调用者）——
        /// 该档差直接乘入政策放缩会与贤相1.15/评级1.35叠乘爆炸，实际生效用本系数。
        /// </summary>
        public static float GetPolicyEfficiencyFactor(int power)
        {
            return GetBand(power) switch
            {
                PowerBand.Collapse => 0.85f,
                PowerBand.Shared => 0.95f,
                PowerBand.Centered => 1.00f,
                PowerBand.Centralized => 1.05f,
                PowerBand.Absolute => 1.10f,
                _ => 1.0f
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // 先秦封建爵制后缀与尊称 (邑国 / 子国 / 伯国 / 侯国 / 公国 / 天朝)
        // 2026-08-18 口径统一：Kingdom 版 = 特质权威（ZhuluTierService.GetKingdomTierId，
        // 含叛军/天命感知）；int 城数版仅作无 Kingdom 句柄时的回退近似（阈值 11/7/4/2
        // 全函数对齐——原 GetTierName 12/6/3 与 GetKingTitle 11/7/4 互不一致，
        // "侯国配伯君"错位即源于此）。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>爵位特质 id（Kingdom 版统一入口；叛军→null 由 For 版特判）。</summary>
        private static string TierIdOf(Kingdom k)
        {
            if (k == null) return ZhuluTierService.TIER_NAN;
            return KingdomCivilizationService.Instance.IsNomadic(k)
                ? NomadicTierService.GetKingdomTierId(k)
                : ZhuluTierService.GetKingdomTierId(k);
        }

        private static bool IsMandate(Kingdom k)
        {
            return k != null && PolicyManager.IsMandateHolderForKingdom(k);
        }

        /// <summary>位阶名（特质权威，叛军/天命感知）。</summary>
        public static string GetTierName(Kingdom k)
        {
            if (ZhuluRebelService.IsRebel(k)) return ZhuluRebelService.REBEL_TIER_NAME;
            if (KingdomCivilizationService.Instance.IsNomadic(k))
                return IsMandate(k) ? "王庭" : NomadicTierService.GetTierName(TierIdOf(k));
            if (IsMandate(k)) return "天朝";
            return ZhuluTierService.GetSuffix(TierIdOf(k));
        }

        /// <summary>君主称号（特质权威，叛军/天命感知）。</summary>
        public static string GetKingTitle(Kingdom k)
        {
            if (ZhuluRebelService.IsRebel(k)) return ZhuluRebelService.REBEL_LEADER_TITLE;
            if (KingdomCivilizationService.Instance.IsNomadic(k))
                return IsMandate(k) ? "天可汗" : NomadicTierService.GetKingTitle(TierIdOf(k));
            if (IsMandate(k)) return "皇帝";
            return ZhuluTierService.GetKingTitle(TierIdOf(k));
        }

        /// <summary>储君称号（特质权威）：天命=太子，公/侯=世子（对齐党派名 世子党），伯子邑=世子。</summary>
        public static string GetHeirTitle(Kingdom k)
        {
            if (ZhuluRebelService.IsRebel(k)) return "副帅";
            if (KingdomCivilizationService.Instance.IsNomadic(k)) return "继承人";
            return IsMandate(k) ? "太子" : "世子";
        }

        // 2026-10-06 移出：GetKingdomSuffix(Kingdom)/(int) 国名爵位后缀（国名后缀取消，
        // 随 KingdomExt.GetFormattedName 拼接段一并移出，无存活调用点）——
        // 完整代码见 docs/移出代码存档-称号后缀.md。

        /// <summary>王后称号（特质权威）。</summary>
        public static string GetQueenTitle(Kingdom k)
        {
            if (KingdomCivilizationService.Instance.IsNomadic(k)) return "可敦";
            if (IsMandate(k)) return "皇后";
            string tier = TierIdOf(k);
            if (tier == ZhuluTierService.TIER_GONG || tier == ZhuluTierService.TIER_HOU) return "君夫人";
            return "夫人";
        }

        /// <summary>城守称号（特质权威）。</summary>
        public static string GetCityLordTitle(Kingdom k)
        {
            if (KingdomCivilizationService.Instance.IsNomadic(k)) return "千户长";
            if (IsMandate(k)) return "太守";
            string tier = TierIdOf(k);
            if (tier == ZhuluTierService.TIER_GONG) return "郡守";
            if (tier == ZhuluTierService.TIER_HOU) return "县令";
            if (tier == ZhuluTierService.TIER_BO) return "城大夫";
            return "邑宰";
        }

        /// <summary>队长称号（特质权威）。</summary>
        public static string GetCaptainTitle(Kingdom k)
        {
            if (IsMandate(k)) return "都尉";
            if (KingdomCivilizationService.Instance.IsNomadic(k)) return "千夫长";
            string tier = TierIdOf(k);
            if (tier == ZhuluTierService.TIER_GONG) return "校尉";
            if (tier == ZhuluTierService.TIER_HOU) return "军候";
            if (tier == ZhuluTierService.TIER_BO) return "队正";
            return "伍长";
        }

        // ── 城数回退版（无 Kingdom 句柄时近似；阈值 11/7/4/2 与国力点城数主导一致）──

        public static string GetTierName(int cityCount)
        {
            return ZhuluTierService.GetSuffix(TierIdByCities(cityCount));
        }

        private static string TierIdByCities(int cityCount)
        {
            // 近似：1城=邑，2-3城=子，4-6城=伯，7-10城=侯，11+城=公
            if (cityCount >= 11) return ZhuluTierService.TIER_GONG;
            if (cityCount >= 7) return ZhuluTierService.TIER_HOU;
            if (cityCount >= 4) return ZhuluTierService.TIER_BO;
            if (cityCount >= 2) return ZhuluTierService.TIER_ZI;
            return ZhuluTierService.TIER_NAN;
        }

        /// <summary>国王称号（城数回退版；权威判定读君主特质见 ZhuluTierService.GetActorTier）。</summary>
        public static string GetKingTitle(int cityCount)
        {
            return ZhuluTierService.GetKingTitle(TierIdByCities(cityCount));
        }

        /// <summary>国王称号（读君主爵位特质，权威；无特质回退城数近似）。</summary>
        public static string GetKingTitle(Actor king, int cityCount)
        {
            string tier = ZhuluTierService.GetActorTier(king);
            if (tier == null) tier = TierIdByCities(cityCount);
            return ZhuluTierService.GetKingTitle(tier);
        }

        public static string GetHeirTitle(int cityCount)
        {
            // 城数回退：太子仅天命国（调用方特判），诸侯储君通称世子
            return "世子";
        }

        public static string GetQueenTitle(int cityCount)
        {
            return cityCount >= 7 ? "君夫人" : "夫人";
        }

        public static string GetCityLordTitle(int cityCount)
        {
            if (cityCount >= 11) return "郡守";
            if (cityCount >= 7) return "县令";
            if (cityCount >= 4) return "城大夫";
            return "邑宰";
        }

        public static string GetCaptainTitle(int cityCount)
        {
            if (cityCount >= 11) return "校尉";
            if (cityCount >= 7) return "军候";
            if (cityCount >= 4) return "队正";
            return "伍长";
        }

        public static string GetInheritanceMethod(int power)
        {
            return GetBand(power) switch
            {
                PowerBand.Collapse => "贵族推举 (声望最高者)",
                PowerBand.Shared => "声望最高子女继承",
                PowerBand.Centered => "长子+能力并行",
                PowerBand.Centralized => "长子强制继承",
                PowerBand.Absolute => "长子强制继承",
                _ => "长子继承"
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // 权力值年度渐进计算（2026-08-18 重构）
        // 原每10年跳变 ±4：① 结算频率与稳定/权臣的年变脱节 ② 微扰负偏 + 承平 −1 使长命王朝
        // 权力趋 0（用户实测"时间长基本上0"）。新公式：年度合成"目标值"（时间/国情/政策九因素），
        // 现值以 15% 速率渐进逼近（年幅 ±5 上限），既平滑又能对篡位/权臣等突变快速响应。
        // ══════════════════════════════════════════════════════════════════

        public static void TickPowerValues(int currentYear)
        {
            if (_lastCheckYear < 0) _lastCheckYear = currentYear;
            if (currentYear - _lastCheckYear < POWER_CHECK_INTERVAL_YEARS) return;
            _lastCheckYear = currentYear;

            var worldSnap = WorldBoxReadOnlyAdapter.Instance.CaptureWorldSnapshot();
            if (worldSnap == null || worldSnap.Kingdoms == null) return;

            foreach (var kSnap in worldSnap.Kingdoms)
            {
                string kId = kSnap.KingdomId;
                int power = KingdomExt.GetPowerValue(kId);
                float target = 50f;
                string why = "";

                // ① 民心（稳定）：对称线性 ±8
                try
                {
                    float stabilityVal = Domain.StabilityManager.Instance.GetStability(kId);
                    float stab = Mathf.Clamp((stabilityVal - 50f) * 0.16f, -8f, 8f);
                    if (!Mathf.Approximately(stab, 0f)) { target += stab; why += $"稳定{stab:+0;-0} "; }
                }
                catch { }

                // ② 财政（动态阈值）：库 ≥ 5年岁入 +6 / 亏空 −6
                try
                {
                    var treReport = Domain.TreasuryManager.Instance.GetReport(kId);
                    if (treReport.Income > 0 && treReport.Treasury >= treReport.Income * 5) { target += 6f; why += "府库充盈+6 "; }
                    else if (treReport.Treasury < 0) { target -= 6f; why += "国库亏空-6 "; }
                }
                catch { }

                // ③ 国祚（时间因素）：新朝（<50）锐意 +5 / 承平日久（>150）浸衰 −4
                int dynastyAge = KingdomExt.GetDynastyAge(kId, currentYear);
                if (dynastyAge < 50) { target += 5f; why += "新朝锐意+5 "; }
                else if (dynastyAge > 150) { target -= 4f; why += "承平浸衰-4 "; }

                // ④ 战争：交战 −5
                try
                {
                    Kingdom kk = Persistence.KingdomDataExt.FindKingdomById(kId);
                    bool atWar = false;
                    if (kk != null) { var wars = kk.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } }
                    if (atWar) { target -= 5f; why += "兵戈-5 "; }
                }
                catch { }

                // ⑤ 君主才具：等级≥8 +4 / ≤3 −4 / 其余按 (level−5)×0.8 微调
                int rulerLevel = kSnap.RulerLevel;
                if (rulerLevel >= 8) { target += 4f; why += "雄主+4 "; }
                else if (rulerLevel <= 3) { target -= 4f; why += "幼弱-4 "; }
                else target += (rulerLevel - 5) * 0.8f;

                // ⑥ 天命（仅天命国）：天命≥70 +5 / <30 −5
                try
                {
                    Kingdom kk = Persistence.KingdomDataExt.FindKingdomById(kId);
                    if (kk != null && PolicyManager.IsMandateHolderForKingdom(kk))
                    {
                        float mandate = MandateManager.Instance.GetMandateValue(kId);
                        if (mandate >= 70f) { target += 5f; why += "天命眷顾+5 "; }
                        else if (mandate < 30f) { target -= 5f; why += "天命式微-5 "; }
                    }
                }
                catch { }

                // ⑦ 权臣：权势≥90 −12 / ≥70 −6 / <40 +4（乾纲独断）
                try
                {
                    var (_, _, topPower) = CorruptionManager.Instance.GetTopPowerfulOfficial(kId);
                    if (topPower >= 90f) { target -= 12f; why += "权臣窃命-12 "; }
                    else if (topPower >= 70f) { target -= 6f; why += "权臣掣肘-6 "; }
                    else if (topPower < 40f) { target += 4f; why += "乾纲独断+4 "; }
                }
                catch { }

                // ⑧ 党争：烈度≥0.6 −4（朝堂倾轧损耗皇权）
                try
                {
                    float intensity = Domain.FactionPoliticsManager.Instance.GetIntensity(
                        Persistence.KingdomDataExt.FindKingdomById(kId), kId);
                    if (intensity >= 0.6f) { target -= 4f; why += "党争掣肘-4 "; }
                }
                catch { }

                // ⑨ 政策（2026-08-18 集权政策体系——各等级均有加权力政策，可实际推高集权目标值）：
                //    侯+ 中央集权 +10 / 公+ 强干弱枝 +8 / 伯 大夫集权 +5 / 邑子 集权于宗 +4 /
                //    天命 乾纲独断 +12 / 郡国并行 −4（封建分权）/ 朝堂整肃 +2 / 重文轻武 +2
                try
                {
                    if (PolicyManager.Instance.IsPolicyActive(kId, "gov_central")) { target += 10f; why += "中央集权+10 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "gong_qianggan")) { target += 8f; why += "强干弱枝+8 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "bo_jiquan")) { target += 5f; why += "大夫集权+5 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "zi_jiquan")) { target += 4f; why += "集权于宗+4 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "mand_qiangan")) { target += 12f; why += "乾纲独断+12 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "gov_feudal")) { target -= 4f; why += "郡国分权-4 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "gov_purge")) { target += 2f; why += "整肃朝纲+2 "; }
                    if (PolicyManager.Instance.IsPolicyActive(kId, "mil_civilian")) { target += 2f; why += "重文抑武+2 "; }
                }
                catch { }

                // 体制被动：公国「三公制衡」+2（公国爵位且七席满员；2026-08-18 特质权威口径）
                try
                {
                    Kingdom regimeK = Persistence.KingdomDataExt.FindKingdomById(kId);
                    if (regimeK != null && OfficeManager.GetTier(regimeK) == OfficeManager.KingdomTier.Gong)
                    {
                        string[] gongSeats = { "prime_minister", "personnel", "revenue", "military", "justice", "rites", "works" }; // 公国七席
                        bool gongAllFilled = true;
                        foreach (var seat in gongSeats)
                        {
                            var seatApp = OfficeManager.Instance.GetAppointment(kId, seat);
                            if (seatApp == null || string.IsNullOrEmpty(seatApp.PersonId)) { gongAllFilled = false; break; }
                        }
                        if (gongAllFilled) { target += 2f; why += "三公制衡+2 "; }
                    }
                }
                catch { }

                // ⑩ 规模补偿（C2b-i，2026-10-01 轨迹数据定稿 [PLACEHOLDER]）：大国治理成本换权威——
                // 对冲老档 341 年的权臣恒在（tenure 必然堆满 → 权臣-6/-12）与长命国 target 均值偏低（实测 55-64）
                try
                {
                    Kingdom kx = Persistence.KingdomDataExt.FindKingdomById(kId);
                    if (kx != null)
                    {
                        int effCities = KingdomExt.GetEffectiveCities(kId, kx.cities != null ? kx.cities.Count : 0);
                        int seatCount = OfficeManager.GetExpectedSeats(effCities).Length;
                        if (seatCount >= 6) { target += 6f; why += "官僚齐备+6 "; }
                        else if (seatCount >= 4) { target += 3f; why += "官制初备+3 "; }
                    }
                }
                catch { }

                target = Mathf.Clamp(target, 5f, 95f);

                // 渐进逼近：年幅 ±5（突变靠 SetPowerValue 直写——篡位重构/摄政流失等）
                float step = Mathf.Clamp((target - power) * 0.15f, -5f, 5f);
                int newPower = Mathf.Clamp(Mathf.RoundToInt(power + step), 0, 100);
                KingdomExt.SetPowerValue(kId, newPower);

                // C2a 插桩：权力值轨迹落盘（.runtime，非快照——不改 Schema、不触发 23 分区校验）
                // 数据回收流程见 docs/落地改动方案-20261001.md B1.5；走直写 IO，不经 debug_logging 开关
                try
                {
                    string tracePath = System.IO.Path.Combine(ZhuluContext.Instance.ModPath, ".runtime", "power_trace.csv");
                    var traceFile = new System.IO.FileInfo(tracePath);
                    if (traceFile.Exists && traceFile.Length > 2L * 1024 * 1024)
                    {
                        if (!_traceOverflowWarned)
                        {
                            ZhuluContext.Instance.LogWarning("[RegimeManager] power_trace.csv 超 2MB 停写，请归档删除后重跑");
                            _traceOverflowWarned = true;
                        }
                    }
                    else
                    {
                        bool head = !traceFile.Exists || traceFile.Length == 0;
                        System.IO.File.AppendAllText(tracePath,
                            (head ? "year,kingdom,power,target,newpower,why\n" : "")
                            + $"{currentYear},{kSnap.KingdomId},{power},{target:F1},{newPower},\"{why.Trim()}\"\n");
                    }
                }
                catch { } // IO 失败不影响主逻辑

                // 十年一志（防年度刷屏）
                if (currentYear % 10 == 0 && !Mathf.Approximately(step, 0f))
                {
                    ZhuluContext.Instance.LogInfo($"[RegimeManager] 《{kSnap.KingdomName}》权力渐进: {power} → {newPower} (目标{target:F0}) 政体:{GetBandName(newPower)} [{why.Trim()}]");
                }
            }
        }
    }
}
