using System;
using System.Collections.Generic;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public class TreasuryReport
    {
        public string KingdomId { get; set; }
        public long Treasury { get; set; }      // 国库结余（可为负 = 亏空）
        public long Income { get; set; }        // 去年岁入
        public long Salaries { get; set; }      // 去年百官俸禄
        public long PolicyUpkeep { get; set; }  // 去年政策维护费
        public long WarCost { get; set; }       // 去年战争军费（阶段2S-2）
        public long ExamCost { get; set; }      // 最近一次开科花费（阶段2S-3，一次性）
        public long WarDebt { get; set; }       // 战争赔款欠款余额（年追缴 10%）
        public int DeficitYears { get; set; }   // 连续亏空年数
        public float RevenueEfficiency { get; set; } = 1f; // 户部官效系数
        public long CourtCost { get; set; }     // 去年宫廷用度（阶段3：规模伸缩支出）
        public long Balance => Income - Salaries - PolicyUpkeep - WarCost - CourtCost;
    }

    /// <summary>
    /// 逐鹿国库（影子账本）：只读原版城市产出与税率，不碰原版库存。
    /// 岁入 = 全国人口 × 税率(含政策/官效加成) × 人均系数 × 户部官效；
    /// 岁出 = 百官俸禄 + 政策维护费；亏空连年 → 天命承压（由 MandateManager 读取）。
    /// 全部账目持久化到 custom_data（zlr_tre_*）。
    /// </summary>
    public class TreasuryManager : ISnapshotSection<TreasuryManager.TreasuryState>
    {
        public static TreasuryManager Instance { get; } = new TreasuryManager();

        // ── 可调平衡参数（阶段1 财政参数化集中区，后续统一迁 Configs/balance.json）──
        private const float IncomePerPop = 0.30f;       // 人均岁入系数（岁入 = 人口 × 税率 × 本系数 × 官效）
        private const float ScaleMin = 0.35f;           // 规模系数下限（50人小国人均产出低）
        private const float ScaleMax = 1.0f;            // 规模系数上限（1万人大国封顶）
        private const float ScaleLogBase = 4f;          // 规模系数 = Lerp(Min,Max,log10(人口)/4)
        private const float SalaryPmPct = 0.05f;        // 丞相俸禄 = 岁入×5%（支出随国力伸缩）
        private const float SalaryMinPct = 0.03f;       // 尚书俸禄 = 岁入×3%
        private const float SalaryLocalPct = 0.01f;     // 阶段2S-4：地方官俸禄 = 岁入×1%
        private const long SalaryPmCap = 15;            // 丞相俸禄封顶
        private const long SalaryMinCap = 10;           // 尚书俸禄封顶
        private const long SalaryLocalCap = 5;          // 阶段2S-4：地方官俸禄封顶
        private const long SalaryFloor = 1;             // 俸禄下限（至少发 1 金）
        private const float WarTaxPenaltyPerWar = 0.10f;// 每场战争岁入衰减 10%
        private const float WarTaxPenaltyMax = 0.30f;   // 战时衰减上限 30%
        private const long UpkeepTierBase = 5;          // 子国政策维护
        private const long UpkeepTierBo = 7;            // 伯国政策维护
        private const long UpkeepTierHou = 10;          // 侯国政策维护
        private const long UpkeepTierGong = 15;         // 公国政策维护
        private const long UpkeepMandate = 20;          // 天命政策维护（天命国半价 = 10）
        private const long InitialTreasury = 100;       // 新王开国初始国库
        private const long DebtFloor = -200;            // 亏空下限（防无限负滚）
        private const long RichThresholdBase = 500;     // 充盈阈值基数
        private const float RichIncomeYears = 5f;       // 充盈阈值 = max(500, 5 年岁入)
        private const float WarDebtPayPct = 0.10f;      // 赔款欠款每年追缴 10%
        private const float WarDebtForcedPct = 0.05f;   // C1：破产强征 = 岁入 5%（下限 1）[PLACEHOLDER] 待回归
        private const long WarReparationBase = 20;      // 战争赔款基数
        private const float WarReparationStatMult = 2f; // 赔款 = 20 + (国王智+外)×2 + 敌死亡×2
        private const float WarReparationDeathMult = 2f;
        // ── 阶段3 财政平衡（大国膨胀/小国归零双治）──
        private const float CourtCostPct = 0.10f;       // 宫廷用度 = 岁入×10%（天命国 15%——随规模伸缩）
        private const float CourtCostPctMandate = 0.15f;
        private const float HoardingIncomeCut = 0.5f;   // 国库超盈阈值后新增岁入减半（积财奢靡吞噬）
        private const string KEY_WAR_DEBT = "tre_debt"; // 战争赔款欠款（败方国库不足时记账，年追缴10%）
        private const string KEY_WAR_DEBT_CREDITOR = "tre_debt_creditor"; // 阶段3H-20：债权国id（追缴真入其库——旧版追缴扣了钱凭空消失）
        private const string KEY_COURT = "tre_court";   // 宫廷用度（快照第 10 段）

        private const string KEY_TREASURY = "tre";
        private const string KEY_INCOME = "tre_in";
        private const string KEY_OUT_SALARY = "tre_sal";
        private const string KEY_OUT_UPKEEP = "tre_upk";
        private const string KEY_OUT_WAR = "tre_war";   // 阶段2S-2：年战争军费（快照第 8 段）
        private const string KEY_OUT_EXAM = "tre_exam"; // 阶段2S-3：最近一次开科花费（快照第 9 段）
        private const string KEY_DEFICIT = "tre_def";
        private const string KEY_REVEFF = "tre_reveff";

        // ── 阶段2S：一次性费用（颁布费/开科费）──
        private const long IssueCostFloor = 2;      // 颁布费下限（最少 2 金）
        private const float IssueCostYears = 2f;    // 颁布费 = 2 年维护费（用户方案：可调 2~3 年）

        // ── 阶段2S-9：军费（士兵数口径）──
        private const float ArmyCostPerSoldier = 0.05f;    // 常备军饷：每兵 0.05 金/年
        private const float ArmyCostWarPerSoldier = 0.03f; // 战时加成：每兵每场战争 +0.03 金/年
        private const long ArmyCostMin = 1;                // 有兵即耗饷（下限 1 金）
        private const int ArmyCostMaxWars = 3;             // 战时加成最多计 3 场（防多线大国瞬间破产）

        private TreasuryManager() { }

        /// <summary>政策颁布一次性费用（阶段2S-1）：按去年岁入口径的 2 年维护费，下限 2 金。</summary>
        public long CalcIssueCost(string kingdomId, string policyId)
        {
            try
            {
                long income = GetReport(kingdomId).Income;
                // 阶段3H-19：花费差异化——军事最贵（军兴靡费）、经济次之、社会便宜；个别政策再乘个体系数
                var def = PolicyManager.Instance.GetDefinition(policyId);
                float catMult = def?.Category switch
                {
                    "military" => 1.5f,
                    "economy" => 1.25f,
                    "social" => 0.85f,
                    "mandate" => 2.0f,
                    _ => 1.0f
                };
                catMult *= PolicyManager.GetIssueCostMult(policyId);
                return Math.Max(IssueCostFloor, (long)Math.Round(PolicyUpkeepFor(policyId, income) * IssueCostYears * catMult));
            }
            catch { return IssueCostFloor; }
        }

        /// <summary>一次性支出（阶段2S）：国库足够则扣费返回 true；不足返回 false（不动账）。</summary>
        public bool TryPayOneTime(Kingdom k, long cost, string reason)
        {
            if (k == null || !k.isAlive()) return false;
            if (cost <= 0) return true;
            long treasury = KingdomDataExt.GetLong(k, KEY_TREASURY, InitialTreasury);
            if (treasury < cost) return false;
            KingdomDataExt.SetLong(k, KEY_TREASURY, treasury - cost);
            ZhuluContext.Instance.LogInfo($"[TreasuryManager] 【{k.name ?? "本国"}】一次性支出 {cost} 金：{reason}（余 {treasury - cost}）");
            return true;
        }

        /// <summary>
        /// 战争赔款真转账（2026-08-19 方案6）：付款方国库扣除，收款方国库入账。
        /// 付款方国库不足则赔到 0（倾库求和）。返回实际转账金额（与弹窗/日志文案一致）。
        /// </summary>
        public static long TransferIndemnity(Kingdom payer, Kingdom receiver, long amount)
        {
            try
            {
                if (payer == null || receiver == null || amount <= 0) return 0;
                long payerTreasury = KingdomDataExt.GetLong(payer, KEY_TREASURY, InitialTreasury);
                long actual = Math.Min(amount, Math.Max(0, payerTreasury));
                if (actual <= 0) return 0;
                KingdomDataExt.SetLong(payer, KEY_TREASURY, payerTreasury - actual);
                long receiverTreasury = KingdomDataExt.GetLong(receiver, KEY_TREASURY, InitialTreasury);
                KingdomDataExt.SetLong(receiver, KEY_TREASURY, receiverTreasury + actual);
                ZhuluContext.Instance.LogInfo(
                    $"[TreasuryManager] 战争赔款转账: 【{payer.name ?? "付款方"}】→【{receiver.name ?? "收款方"}】{actual} 金（付款方余 {payerTreasury - actual}）");
                return actual;
            }
            catch { return 0; }
        }

        /// <summary>记录最近一次开科花费（阶段2S-3，快照第 9 段；停科不更新）。</summary>
        public void RecordExamCost(Kingdom k, long cost)
        {
            try { if (k != null && k.isAlive()) KingdomDataExt.SetLong(k, KEY_OUT_EXAM, cost); } catch { }
        }

        public TreasuryReport GetReport(string kingdomId)
        {
            var report = new TreasuryReport { KingdomId = kingdomId, Treasury = InitialTreasury };
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return report;

            report.Treasury = KingdomDataExt.GetLong(k, KEY_TREASURY, InitialTreasury);
            report.Income = KingdomDataExt.GetLong(k, KEY_INCOME, 0);
            report.Salaries = KingdomDataExt.GetLong(k, KEY_OUT_SALARY, 0);
            report.PolicyUpkeep = KingdomDataExt.GetLong(k, KEY_OUT_UPKEEP, 0);
            report.WarCost = KingdomDataExt.GetLong(k, KEY_OUT_WAR, 0);
            report.ExamCost = KingdomDataExt.GetLong(k, KEY_OUT_EXAM, 0);
            report.WarDebt = KingdomDataExt.GetLong(k, KEY_WAR_DEBT, 0);
            report.DeficitYears = (int)KingdomDataExt.GetLong(k, KEY_DEFICIT, 0);
            report.RevenueEfficiency = KingdomDataExt.GetFloat(k, KEY_REVEFF, 1f);
            report.CourtCost = KingdomDataExt.GetLong(k, KEY_COURT, 0);
            return report;
        }

        /// <summary>府库充盈（阶段1 F4：阈值随岁入动态 = max(500, 5年岁入)，避免大图必触发失真）。</summary>
        public static bool IsRich(long treasury, long income) => treasury > Mathf.Max(RichThresholdBase, income * RichIncomeYears);

        // ── C4（2026-10-01）：当年已付维护费台账——内存态、不入快照、不触发 Schema 校验 ──
        // 同年同政策覆盖写（一年一付）；跨年由 OnKingdomYear 的年份切换检测清空。
        private readonly Dictionary<string, Dictionary<string, long>> _paidUpkeep = new Dictionary<string, Dictionary<string, long>>();
        private int _paidUpkeepYear = -1;

        private void RecordPaidUpkeep(string kingdomId, string policyId, long amount)
        {
            if (amount <= 0) return;
            if (!_paidUpkeep.TryGetValue(kingdomId, out var d)) { d = new Dictionary<string, long>(); _paidUpkeep[kingdomId] = d; }
            d[policyId] = amount;
        }

        /// <summary>C4：取出并返回该政策当年已付维护费（取出即清）。未付款项不在台账，天然返回 0。</summary>
        public long ConsumePaidUpkeep(string kingdomId, string policyId)
        {
            if (_paidUpkeep.TryGetValue(kingdomId, out var d) && d.TryGetValue(policyId, out var amt))
            {
                d.Remove(policyId);
                return amt;
            }
            return 0;
        }

        /// <summary>C4：政策年中废止时退还当年已付维护费（直写国库键）。
        /// 仅财政 Tick 外的废止路径会退到非零值（Tick 内付不起废止未付款），无 Tick 结尾覆盖风险。</summary>
        public void RefundUpkeep(string kingdomId, long amount)
        {
            if (amount <= 0) return;
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;
            long treasury = KingdomDataExt.GetLong(k, KEY_TREASURY, InitialTreasury);
            KingdomDataExt.SetLong(k, KEY_TREASURY, treasury + amount);
        }

        /// <summary>年度结算入口：可高频调用，内部按游戏年去重。</summary>
        /// <summary>按国年度处理（由 YearDriver 每国每年调用一次）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (k == null || !k.isAlive()) return;
            if (_paidUpkeepYear != year) { _paidUpkeep.Clear(); _paidUpkeepYear = year; }
            TickKingdom(k, year);
        }

        private void TickKingdom(Kingdom k, int currentYear)
        {
            string kingdomId = k.id.ToString();
            string kingdomName = k.name ?? "本国";

            // ── 1. 岁入：人口 × 税率 × 人均系数 × 户部官效 ──
            // 注意：不能读 getTaxRateLocal() —— 其内部 _cached_tax_local 只在 recalcBaseStats 时
            // 初始化，实测 AI 王国恒为 0（岁入全灭的真凶）。改走 SimGlobals 公开基线税率，
            // 再乘我们自己的 gold_income_multiplier 管道（户部/盐铁专卖等政策效果从这里真实进入岁入）。
            float baseTax = 0.5f;
            try { if (SimGlobals.m != null) baseTax = SimGlobals.m.base_tax_rate_local; } catch { }
            float pipeMult = Gameplay.ModifierPipeline.Instance.EvaluateChannelMultiplier(Gameplay.ModifierChannels.GoldIncomeMultiplier, kingdomId, 1.0f);
            float taxRate = baseTax * pipeMult;

            int population = k.units != null ? k.units.Count : 0;
            float revEff = CalcRevenueEfficiency(kingdomId);

            // 规模系数（阶段1 F2：治愈小国亏空 + 防大国膨胀）：50人→0.35 / 500人→0.70 / 2000人→0.86 / 1万人→1.0
            float scaleFactor = ScaleMin + (ScaleMax - ScaleMin)
                * Mathf.Clamp01(Mathf.Log10(Mathf.Max(1, population)) / ScaleLogBase);

            // 战时衰减（阶段1 F5）：每场战争岁入 -10%，上限 -30%，和平恢复
            float warFactor = 1f;
            int activeWarCount = 0;
            try
            {
                var wars = k.getWars();
                if (wars != null) { foreach (var _ in wars) { activeWarCount++; if (activeWarCount >= 3) break; } }
                warFactor = 1f - Mathf.Min(WarTaxPenaltyMax, activeWarCount * WarTaxPenaltyPerWar);
            }
            catch { }

            long income = (long)Math.Round(population * taxRate * IncomePerPop * revEff * scaleFactor * warFactor);
            // 阶段3 小国保底：人口>0 岁入至少 1 金（曾 round 归零致小国永久零收入钉死亏空下限）
            if (population > 0 && income < 1) income = 1;

            // ── 2. 俸禄（阶段1 F1：按岁入比例，支出随国力伸缩）：丞相 5% 封顶15 / 尚书 3% 封顶10，下限 1 ──
            long salaries = 0;
            foreach (var officeId in OfficeManager.OfficesList)
            {
                var app = OfficeManager.Instance.GetAppointment(kingdomId, officeId);
                if (app != null && !string.IsNullOrEmpty(app.PersonId))
                {
                    long baseSalary = (officeId == "prime_minister")
                        ? (long)Mathf.Clamp(income * SalaryPmPct, SalaryFloor, SalaryPmCap)
                        : (long)Mathf.Clamp(income * SalaryMinPct, SalaryFloor, SalaryMinCap);
                    salaries += baseSalary;
                }
            }

            // 阶段2S-4：地方官 3 席（县丞/郡守/刺史）入俸——每席岁入 1% 封顶 5，下限 1。
            // 城主按食邑制自留地方财赋，不领中央俸（防 20 城大国俸禄爆炸——定案口径）。
            foreach (var localOffice in OfficeManager.LocalOffices)
            {
                var app = OfficeManager.Instance.GetAppointment(kingdomId, localOffice);
                if (app != null && !string.IsNullOrEmpty(app.PersonId))
                {
                    salaries += (long)Mathf.Clamp(income * SalaryLocalPct, SalaryFloor, SalaryLocalCap);
                }
            }

            // ── 2b. 军费（阶段2S-9 重做：士兵数口径替代岁入比例口径）──
            // 常备军饷 = 士兵数 × 0.05 金/年（平时也耗饷——原版口径大国和平期军费恒 0 不合理）
            // 战时加成 = 每场活跃战争追加士兵数 × 0.03，至多 3 场；与岁入端战时衰减（-10%/场）并行
            long armyCost = 0;
            int soldiers = 0;
            try { soldiers = Math.Max(0, k.countTotalWarriors()); } catch { }
            if (soldiers > 0)
            {
                long standing = (long)Math.Ceiling(soldiers * ArmyCostPerSoldier);
                long warSurcharge = (long)Math.Ceiling(soldiers * ArmyCostWarPerSoldier) * Math.Min(activeWarCount, ArmyCostMaxWars);
                armyCost = Math.Max(ArmyCostMin, standing + warSurcharge);
            }

            // ── 2c. 宫廷用度（阶段3：岁入比例支出——治大国无限膨胀。支出端此前全是封顶小额，
            //    岁入线性涨 → 净结余无上限堆积）：普通国 10%，天命国 15%（宫廷更奢）──
            bool isMandateKingdom = false;
            try { isMandateKingdom = PolicyManager.IsMandateHolderForKingdom(k); } catch { }
            long courtCost = (long)Math.Round(income * (isMandateKingdom ? CourtCostPctMandate : CourtCostPct));

            // ── 3. 账面结算：先入后支 ──
            long treasury = KingdomDataExt.GetLong(k, KEY_TREASURY, InitialTreasury);

            // 阶段3 软上限：国库已超盈阈值（max(500, 5年岁入)）→ 新增岁入减半入库
            // （库满则奢靡吞噬——与 IsRich 充盈判定同口径，防大国积财无限膨胀）
            long richCap = (long)Mathf.Max(RichThresholdBase, income * RichIncomeYears);
            long effectiveIncome = treasury > richCap
                ? Math.Max(1, (long)(income * HoardingIncomeCut))
                : income;

            treasury += effectiveIncome;
            treasury -= salaries;
            treasury -= armyCost;
            treasury -= courtCost;

            // ── 4. 政策维护费（阶段1 F3：按政策等级分档）：付不起的政策当场废止（史册由 RevokePolicy 记录）──
            long upkeepPaid = 0;
            var activePolicies = PolicyManager.Instance.GetActivePolicies(kingdomId);
            foreach (var p in activePolicies)
            {
                long upkeep = PolicyUpkeepFor(p.PolicyId, income);
                // 阶段3D：游牧持续开销覆盖层（千户兵制×0.70/全民骑战×1.25——仅游牧国）
                try
                {
                    float um = PolicyManager.GetNomadicUpkeepMult(p.PolicyId) * PolicyManager.GetNomadicCategoryUpkeepMult(p.Category);
                    if (um != 1f)
                    {
                        var upkK = KingdomDataExt.FindKingdomById(kingdomId);
                        if (upkK != null && KingdomCivilizationService.Instance.IsNomadic(upkK))
                            upkeep = (long)Math.Round(upkeep * um);
                    }
                }
                catch { }
                if (treasury >= upkeep)
                {
                    treasury -= upkeep;
                    upkeepPaid += upkeep;
                    RecordPaidUpkeep(kingdomId, p.PolicyId, upkeep);
                }
                else if ((p.Category == "economy" || p.Category == "social") && treasury > DebtFloor)
                {
                    // C3 亏空缓付（2026-10-01）：自救类政策挂账不废止——
                    // 打破「亏空→全废政策→失去恢复buff→更亏」死亡螺旋（与 PolicyManager 亏空自救同口径）；
                    // DebtFloor 前置防无限透支 [PLACEHOLDER] 若长期 -200 压底且政策照挂 → 加连续3年上限
                    treasury -= upkeep;
                    upkeepPaid += upkeep;
                    RecordPaidUpkeep(kingdomId, p.PolicyId, upkeep);
                }
                else
                {
                    PolicyManager.Instance.RevokePolicy(kingdomId, kingdomName, p.PolicyId, currentYear);
                    ZhuluContext.Instance.LogWarning($"[TreasuryManager] 【{kingdomName}】国库不足，无力维持政策，自动废止：{p.PolicyId}");
                }
            }

            // ── 5. 亏空统计 ──
            int deficitYears = (int)KingdomDataExt.GetLong(k, KEY_DEFICIT, 0);
            if (treasury < 0) deficitYears++;
            else deficitYears = 0;

            // ── 5a. 2026-08-19 串联：亏空动摇社稷（-2/年；连续亏空≥2年 -4/年）——
            // 财政危机传导为稳定度危机（与天命承压-5、欠俸离心-3 构成亏空三联后果）──
            if (treasury < 0)
            {
                try
                {
                    float stabHit = deficitYears >= 2 ? -4f : -2f;
                    StabilityManager.Instance.AddExternalBonus(kingdomId, stabHit);
                }
                catch { }
            }

            if (treasury < DebtFloor) treasury = DebtFloor;

            // ── 5b. 战争赔款欠款追缴（阶段1 F5：正常追缴 10%）──
            // C1 修复（2026-10-01）：原「国库为正时才扣」使库存归零国的战债永久死锁
            // （实测第 341 年存档：田国 战债 104 库存 0 常年挂账）——破产国改按岁入 5% 强制征缴
            long warDebt = KingdomDataExt.GetLong(k, KEY_WAR_DEBT, 0);
            if (warDebt > 0)
            {
                long debtPay;
                if (treasury > 0)
                {
                    debtPay = Math.Min(warDebt, Math.Max(1, (long)(warDebt * WarDebtPayPct)));
                }
                else
                {
                    // 破产强征：岁入 5% 下限 1；贴 DebtFloor 时按剩余空间截断 [PLACEHOLDER] 0.05 待回归
                    debtPay = Math.Min(warDebt, Math.Max(1, (long)(income * WarDebtForcedPct)));
                    if (treasury - debtPay < DebtFloor) debtPay = treasury - DebtFloor;
                }
                if (debtPay > 0)
                {
                    treasury -= debtPay;
                    KingdomDataExt.SetLong(k, KEY_WAR_DEBT, warDebt - debtPay);
                    // 阶段3H-20：追缴真入债权国财库（旧版扣了钱凭空消失）；债权国亡 → 债务一笔勾销
                    try
                    {
                        long credId = KingdomDataExt.GetLong(k, KEY_WAR_DEBT_CREDITOR, -1);
                        Kingdom creditor = credId > 0 ? KingdomDataExt.FindKingdomById(credId.ToString()) : null;
                        if (creditor != null && creditor.isAlive())
                        {
                            long ct = KingdomDataExt.GetLong(creditor, KEY_TREASURY, InitialTreasury);
                            KingdomDataExt.SetLong(creditor, KEY_TREASURY, ct + debtPay);
                        }
                        else
                        {
                            KingdomDataExt.SetLong(k, KEY_WAR_DEBT, 0);
                        }
                    }
                    catch { }
                }
            }

            // ── 6. 持久化 ──
            KingdomDataExt.SetLong(k, KEY_TREASURY, treasury);
            KingdomDataExt.SetLong(k, KEY_INCOME, income);
            KingdomDataExt.SetLong(k, KEY_OUT_SALARY, salaries);
            KingdomDataExt.SetLong(k, KEY_OUT_UPKEEP, upkeepPaid);
            KingdomDataExt.SetLong(k, KEY_OUT_WAR, armyCost);
            KingdomDataExt.SetLong(k, KEY_COURT, courtCost);
            KingdomDataExt.SetLong(k, KEY_DEFICIT, deficitYears);
            KingdomDataExt.SetFloat(k, KEY_REVEFF, revEff);

            ZhuluContext.Instance.LogInfo($"[TreasuryManager] 【{kingdomName}】年度财政: 岁入={income}(税率{taxRate:F2}x户效{revEff:F2}{(effectiveIncome != income ? $"x盈库减半入库{effectiveIncome}" : "")}), 俸禄={salaries}, 军费={armyCost}(兵{soldiers}x战{activeWarCount}), 宫廷={courtCost}, 维护={upkeepPaid}, 国库={treasury}, 连亏={deficitYears}年");
        }

        /// <summary>政策维护费分档（阶段1 F3）：天命政策 20 半价=10；公 15 / 侯 10 / 伯 7 / 子 5。
        /// 阶段2D ⑤：UpkeepPct>0 的政策按岁入比例（募兵 -2%/府兵 -1.5%/精兵 -1%）。</summary>
        private long PolicyUpkeepFor(string policyId, long income)
        {
            var def = PolicyManager.Instance.GetDefinition(policyId);
            if (def == null) return UpkeepTierHou;
            if (def.UpkeepPct > 0f) return (long)Math.Round(income * def.UpkeepPct);
            if (def.RequiresMandate) return UpkeepMandate / 2; // 天命政策仅天命国可开，半价
            if (def.MinTier == "gong") return UpkeepTierGong;
            if (def.MinTier == "hou") return UpkeepTierHou;
            if (def.MinTier == "bo") return UpkeepTierBo;
            return UpkeepTierBase;
        }

        /// <summary>
        /// 战争赔款（阶段1 F5）：赔款 = 20 + (胜方国王智+外)×2 + 败方死亡×2。
        /// 从败方国库扣，不足部分记欠款（每年 10% 追缴，见 TickKingdom 5b）。
        /// </summary>
        public static void ApplyWarReparation(Kingdom winner, Kingdom loser, int winnerIntDip, int loserDeaths)
        {
            try
            {
                if (winner == null || loser == null || winner == loser || !winner.isAlive()) return;
                long amount = WarReparationBase
                    + (long)(Mathf.Max(0, winnerIntDip) * WarReparationStatMult)
                    + (long)(Mathf.Max(0, loserDeaths) * WarReparationDeathMult);
                if (amount <= 0) amount = 1;

                long loserTreasury = KingdomDataExt.GetLong(loser, KEY_TREASURY, InitialTreasury);
                long actual = Math.Min(amount, Math.Max(0, loserTreasury));
                if (actual > 0)
                {
                    KingdomDataExt.SetLong(loser, KEY_TREASURY, loserTreasury - actual);
                    long winnerTreasury = KingdomDataExt.GetLong(winner, KEY_TREASURY, InitialTreasury);
                    KingdomDataExt.SetLong(winner, KEY_TREASURY, winnerTreasury + actual);
                }
                long remain = amount - actual;
                if (remain > 0)
                {
                    long oldDebt = KingdomDataExt.GetLong(loser, KEY_WAR_DEBT, 0);
                    KingdomDataExt.SetLong(loser, KEY_WAR_DEBT, oldDebt + remain);
                    // 阶段3H-20：登记债权国——年度追缴按此真转账
                    KingdomDataExt.SetLong(loser, KEY_WAR_DEBT_CREDITOR, winner.id);
                }
            }
            catch { }
        }

        /// <summary>户部官效：以户部尚书考评分映射执行系数（无户部尚书 = 基准 1.0）。
        /// 阶段3 性能：原全量 CaptureKingdomCitizens 只为找一人（每国每年 O(人口) 采集）——改直接按 ActorId 单人评分。</summary>
        private float CalcRevenueEfficiency(string kingdomId)
        {
            var app = OfficeManager.Instance.GetAppointment(kingdomId, "revenue");
            if (app == null || string.IsNullOrEmpty(app.PersonId)) return 1f;

            try
            {
                if (!long.TryParse(app.PersonId, out long pid)) return 1f;
                Actor a = World.world?.units?.get(pid);
                if (a == null || !a.isAlive()) return 1f;
                var (score, _) = OfficialRatingManager.CalcOfficialScore(a, OfficeManager.GetOfficeIndex("revenue"), a.getAge());
                return OfficialRatingManager.GetExecutionFactor(score);
            }
            catch { return 1f; }
        }
    
        // ═══════════ v2 快照存档接口 ═══════════
        public void MigrateFromCustomData() { }
        public void ResetRuntime() { }

        // ═══════════ 批次10-A1：Schema v5 分区（替代上方竖线位置协议——字段名即语义，加字段只加属性） ═══════════

        /// <summary>财政快照状态（强类型 POCO——属性名即 JSON 键）。</summary>
        public sealed class TreasuryState
        {
            public long Balance;
            public long Income;
            public long Salary;
            public long Upkeep;
            public long Deficit;
            public float Reveff = 1f;
            public long WarDebt;
            public long WarCost;
            public long ExamCost;
            public long CourtCost;
        }

        public string SectionName => "treasury";

        public Dictionary<string, TreasuryState> CaptureStates()
        {
            var d = new Dictionary<string, TreasuryState>();
            if (World.world?.kingdoms?.list == null) return d;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                d[k.id.ToString()] = new TreasuryState
                {
                    Balance  = KingdomDataExt.GetLong(k, KEY_TREASURY, InitialTreasury),
                    Income   = KingdomDataExt.GetLong(k, KEY_INCOME, 0),
                    Salary   = KingdomDataExt.GetLong(k, KEY_OUT_SALARY, 0),
                    Upkeep   = KingdomDataExt.GetLong(k, KEY_OUT_UPKEEP, 0),
                    Deficit  = KingdomDataExt.GetLong(k, KEY_DEFICIT, 0),
                    Reveff   = KingdomDataExt.GetFloat(k, KEY_REVEFF, 1f),
                    WarDebt  = KingdomDataExt.GetLong(k, KEY_WAR_DEBT, 0),
                    WarCost  = KingdomDataExt.GetLong(k, KEY_OUT_WAR, 0),
                    ExamCost = KingdomDataExt.GetLong(k, KEY_OUT_EXAM, 0),
                    CourtCost= KingdomDataExt.GetLong(k, KEY_COURT, 0),
                };
            }
            return d;
        }

        public void ApplyStates(Dictionary<string, TreasuryState> data)
        {
            if (data == null) return;
            foreach (var kv in data)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                if (k == null || !k.isAlive() || kv.Value == null) continue;
                var s = kv.Value;
                KingdomDataExt.SetLong(k, KEY_TREASURY, s.Balance);
                KingdomDataExt.SetLong(k, KEY_INCOME, s.Income);
                KingdomDataExt.SetLong(k, KEY_OUT_SALARY, s.Salary);
                KingdomDataExt.SetLong(k, KEY_OUT_UPKEEP, s.Upkeep);
                KingdomDataExt.SetLong(k, KEY_DEFICIT, s.Deficit);
                KingdomDataExt.SetFloat(k, KEY_REVEFF, s.Reveff);
                KingdomDataExt.SetLong(k, KEY_WAR_DEBT, s.WarDebt);
                KingdomDataExt.SetLong(k, KEY_OUT_WAR, s.WarCost);
                KingdomDataExt.SetLong(k, KEY_OUT_EXAM, s.ExamCost);
                KingdomDataExt.SetLong(k, KEY_COURT, s.CourtCost);
            }
        }
    }
}
