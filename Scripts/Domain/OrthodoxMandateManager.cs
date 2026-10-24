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
    /// 正统天命系统。天下唯一正统身份 + 天命值(0-100) + 转移逻辑 + 年号 + 历史。
    /// 天命值每 10 年结算（极慢惯性 0.90/0.10），转移条件每年检测。
    /// </summary>
    public class OrthodoxMandateManager : ISnapshotSection<OrthodoxMandateManager.OrthodoxState>
    {
        public static OrthodoxMandateManager Instance { get; } = new OrthodoxMandateManager();

        private const string KEY_HAS_MANDATE = "has_mandate";
        private const string KEY_MANDATE_VALUE = "mandate_value";
        private const string KEY_BELOW6_SINCE = "below6_since";
        private const string KEY_ERA_NAME = "era_name";
        private const string KEY_EMPEROR_NAME = "emperor_name";
        private const string KEY_DYNASTY_NAME = "dynasty_name";
        private const string KEY_HISTORY = "mandate_history";
        // 2026-08-18 保护期：受命之初国势未稳，保护期内不触发"衰微失命"（防一年后即掉天命）
        private const string KEY_PROTECT_UNTIL = "zlr_mandate_protect_until";
        private const int PROTECT_YEARS = 8;

        private const int BELOW6_YEARS_LIMIT = 50;

        private int _lastMandateCalcYear = -1;
        private int _lastTransferCheckYear = -1;

        // ═══════════════ 天命历史记录 ═══════════════
        private readonly List<DynastyRecord> _history = new List<DynastyRecord>();

        private OrthodoxMandateManager() { }

        // ═══════════════════════ 公开查询接口 ═══════════════════════

        /// <summary>当前天命国 ID（null = 天命空悬）。实时扫描不缓存——缓存可能在世界加载早期被初始化为 null 永久失效，曾致空悬误判/朝代不关闭/换帝记错朝代。</summary>
        public string GetMandateHolderId() => ScanMandateHolderId();

        public float GetMandateValue(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 0f;
            return KingdomDataExt.GetFloat(k, KEY_MANDATE_VALUE, 50f);
        }

        /// <summary>一次性调整天命值（如罪己诏 -5）。</summary>
        public void AdjustMandateValue(string kingdomId, float delta)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;
            float cur = KingdomDataExt.GetFloat(k, KEY_MANDATE_VALUE, 50f);
            float val = UnityEngine.Mathf.Clamp(cur + delta, 0f, 100f);
            KingdomDataExt.SetFloat(k, KEY_MANDATE_VALUE, val);
        }

        public string GetEraName(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return "";
            return KingdomDataExt.GetStr(k, KEY_ERA_NAME, "");
        }

        public string GetDynastyName(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return "";
            return KingdomDataExt.GetStr(k, KEY_DYNASTY_NAME, "");
        }

        public bool IsMandateHolder(string kingdomId)
        {
            string holder = GetMandateHolderId();
            return holder != null && holder == kingdomId;
        }

        public string GetMandateGrade(float val)
        {
            // 阶段2O：六档文案润色（气运消长感——隆盛/昌隆/安定/式微/将倾/欲坠）
            if (val >= 90f) return "天命隆盛";
            if (val >= 70f) return "天命昌隆";
            if (val >= 50f) return "天命安定";
            if (val >= 30f) return "天命式微";
            if (val >= 10f) return "天命将倾";
            return "天命欲坠";
        }

        public string GetMandateColor(float val)
        {
            if (val >= 90f) return "#FFD700";
            if (val >= 70f) return "#FFA500";
            if (val >= 50f) return "#90EE90";
            if (val >= 30f) return "#DDD060";
            if (val >= 10f) return "#FF8C00";
            return "#FF4500";
        }

        /// <summary>获取天命等级对应的稳定度加成。</summary>
        public float GetStabilityBonus(float val)
        {
            if (val >= 90f) return 15f;
            if (val >= 70f) return 10f;
            if (val >= 50f) return 5f;
            if (val >= 30f) return -5f;
            if (val >= 10f) return -10f;
            return -20f;
        }

        // ═══════════════════════ 主循环入口 ═══════════════════════

        public void TickAll(int currentYear)
        {
            try
            {
                // 每年：检测转移条件
                if (currentYear != _lastTransferCheckYear)
                {
                    _lastTransferCheckYear = currentYear;
                    CheckTransferConditions(currentYear);
                    // 阶段2V：大一统检测（与转移同帧——同一天下遍历周期）
                    CheckUnityStatus(currentYear);
                }

                // 每 N 年（可配置，默认 10）：结算天命值
                if (_lastMandateCalcYear < 0 || currentYear - _lastMandateCalcYear >= ZhuluConfig.Instance.MandateTickInterval)
                {
                    _lastMandateCalcYear = currentYear;
                    CalcMandateValue(currentYear);
                }

                // 每年：应用管道效果
                ApplyMandateEffects(currentYear);

                // LlmFix-6（M1）：在位帝王轨迹采样——采样器第2步写好后一直没接线，
                // 「极盛 N 年」与 AI 轨迹形态全部依赖它。间隔 0=关闭（内部自判）
                SampleActiveEmperors(currentYear);

                // LlmV2-4：断代史——每 N 年一段（配置 chronicle_digest_interval，0=关）
                TryDigestChronicle(currentYear);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OrthodoxMandate] TickAll 异常", ex);
            }
        }

        // ═══════════════════════ 大一统检测（阶段2V） ═══════════════════════

        /// <summary>大一统阈值：本国+附庸 城市或人口占天下比例 ≥ 90%。</summary>
        public const float UNIFIED_RATIO = 0.90f;

        /// <summary>大一统判定（纯函数，供测试）：城市比或人口比任一达标。</summary>
        public static bool IsUnifiedRatio(float cityRatio, float popRatio)
        {
            return cityRatio >= UNIFIED_RATIO || popRatio >= UNIFIED_RATIO;
        }

        /// <summary>
        /// 计算天命国（含附庸）占天下的城市/人口比例。
        /// 天下 = 全部存活王国（叛军、附庸皆算天下的一部分——未被王化即未统一）。
        /// </summary>
        public static (float cityRatio, float popRatio) CalcUnityRatio(Kingdom holder)
        {
            if (holder == null || World.world?.kingdoms?.list == null) return (0f, 0f);
            int ownCities = 0, ownPop = 0, worldCities = 0, worldPop = 0;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                int c = k.cities != null ? k.cities.Count : 0;
                int p = k.units != null ? k.units.Count : 0;
                worldCities += c;
                worldPop += p;
                if (k == holder) { ownCities += c; ownPop += p; continue; }
                // 附庸计入王化疆域
                try
                {
                    var suz = VassalManager.Instance.GetSuzerain(k);
                    if (suz != null && suz.id == holder.id) { ownCities += c; ownPop += p; }
                }
                catch { }
            }
            if (worldCities <= 0 && worldPop <= 0) return (0f, 0f);
            return (
                worldCities > 0 ? (float)ownCities / worldCities : 0f,
                worldPop > 0 ? (float)ownPop / worldPop : 0f);
        }

        /// <summary>每年检测：达成大一统 → 在位皇帝 Unified=1（一次达成终身保留）+ 史册昭告。</summary>
        private void CheckUnityStatus(int currentYear)
        {
            try
            {
                string holderId = GetMandateHolderId();
                if (holderId == null) return;
                Kingdom holder = KingdomDataExt.FindKingdomById(holderId);
                if (holder == null || !holder.isAlive()) return;

                var current = _history.FindLast(d => d.EndYear == -1 && d.KingdomId == holderId);
                var emperor = current?.Emperors.FindLast(e => e.EndYear == -1);
                if (emperor == null || emperor.Unified == 1) return; // 已达成不重复记

                var (cityRatio, popRatio) = CalcUnityRatio(holder);
                if (!IsUnifiedRatio(cityRatio, popRatio)) return;

                emperor.Unified = 1;
                string empName = emperor.Name;
                ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.MandateGranted,
                    holderId, holder.name ?? "", empName,
                    $"<color={UI.ZhuluUIStyle.Hex.Imperial}>【大一统】{holder.name} 混一寰宇！四海归一，威加海内——{empName} 成就千古一统之业。</color>", true);
                try { WorldTip.showNow($"【{holder.name}】混一寰宇，成就大一统！", false, "top", 6f, UI.ZhuluUIStyle.Hex.Imperial); } catch { }
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 大一统达成: {holder.name} 城{cityRatio:P0}·民{popRatio:P0} ({currentYear}年)");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OrthodoxMandate] 大一统检测异常", ex);
            }
        }

        // ═══════════════════════ 天命值计算（每 10 年） ═══════════════════════

        private void CalcMandateValue(int currentYear)
        {
            string holderId = GetMandateHolderId();
            if (holderId == null) return;

            Kingdom k = KingdomDataExt.FindKingdomById(holderId);
            if (k == null || !k.isAlive()) return;

            float old = KingdomDataExt.GetFloat(k, KEY_MANDATE_VALUE, 50f);
            int cityCount = k.cities != null ? k.cities.Count : 0;

            // 目标值计算（2026-08-19 串联：天人感应明细——各项加减随手记录，UI"天命观察"卡展示）
            var detail = new System.Text.StringBuilder();
            void Note(string name, float delta)
            {
                if (Mathf.Abs(delta) < 0.01f) return;
                detail.Append(name).Append(delta >= 0f ? "+" : "").Append(delta.ToString("F0")).Append("　");
            }

            float target = 40f;
            float dCities = Mathf.Min(20f, cityCount * 2f);
            target += dCities;
            Note("城池", dCities);

            // 阶段3H-13：王朝周期律精确化（受命起逐年累计：0-30 年 +0.2/年，30-80 年 +0.3/年，
            // 80-150 年 +0.1/年，150-240 年不增不减，240-320 年 -0.1/年；320 年后停摆——
            // 国祚修正终止，天命值纯看国家自身行为（战争胜负/政策/即位/党争等事件修正））
            float dDynasty = 0f;
            int dynastyAge = KingdomExt.GetDynastyAge(holderId, currentYear);
            if (dynastyAge < 320)
            {
                if (dynastyAge < 30) dDynasty = dynastyAge * 0.2f;             // 兴起：累计 +6
                else if (dynastyAge < 80) dDynasty = 6f + (dynastyAge - 30) * 0.3f;   // 鼎盛：累计至 +21
                else if (dynastyAge < 150) dDynasty = 21f + (dynastyAge - 80) * 0.1f; // 平稳：累计至 +28
                else if (dynastyAge < 240) dDynasty = 28f;                      // 持平
                else dDynasty = 28f - (dynastyAge - 240) * 0.1f;                // 衰落：累计回 +18
                target += dDynasty;
                Note("国祚", dDynasty);
            }

            // 稳定度贡献
            float stability = StabilityManager.Instance.GetStability(holderId);
            float dStab = stability > 70f ? 10f : (stability < 30f ? -10f : 0f);
            target += dStab;
            Note("稳定", dStab);

            // 权力值贡献（阶段2A A4-1：±5→±3，降权平衡三方联动——天命→稳定±15 保留为天命核心体现）
            int power = KingdomExt.GetPowerValue(holderId);
            float dPower = power > 70 ? 3f : (power < 30 ? -3f : 0f);
            target += dPower;
            Note("权力", dPower);

            // 战争惩罚
            bool atWar = false;
            try { var wars = k.getWars(); if (wars != null) { foreach (var _ in wars) { atWar = true; break; } } } catch { }
            float dWar = atWar ? -5f : 0f;
            target += dWar;
            Note("交战", dWar);

            // B3（阶段1）：财政联动——连亏 3 年以上 → 天命承压 -5（长明火式财政-正统权衡）
            float dDeficit = 0f;
            try
            {
                var treReport = TreasuryManager.Instance.GetReport(holderId);
                if (treReport.DeficitYears >= 3) dDeficit = -5f;
            }
            catch { }
            target += dDeficit;
            Note("亏空", dDeficit);

            // 阶段2B 包2：礼崩乐坏——净礼治 <0 → 天命承压 -2/年（失德失天命新路径）
            float dLiZhi = ZhuluLiZhiService.GetNetLiZhi(holderId) < 0 ? -2f : 0f;
            target += dLiZhi;
            Note("礼治", dLiZhi);

            // 2026-08-19 串联：党争修正——烈度 ≥0.7（朝堂倾轧不休）→ 天命承压 -2
            float dFaction = 0f;
            try
            {
                if (FactionPoliticsManager.Instance.GetIntensity(k, holderId) >= 0.7f) dFaction = -2f;
            }
            catch { }
            target += dFaction;
            Note("党争", dFaction);

            // 2026-08-19 串联：民心修正——人口年增长 ≥2% → +2（生齿日繁）；负增长 → -2（户口凋敝）
            float dPop = 0f;
            try
            {
                int pop = k.getPopulationPeople();
                long lastPop = KingdomDataExt.GetLong(k, "mandate_pop_last", 0);
                KingdomDataExt.SetLong(k, "mandate_pop_last", pop);
                if (lastPop > 0 && pop > 0)
                {
                    float growth = (pop - lastPop) / (float)lastPop;
                    if (growth >= 0.02f) dPop = 2f;
                    else if (growth < 0f) dPop = -2f;
                }
            }
            catch { }
            target += dPop;
            Note("民心", dPop);

            // 惯性滑动
            float val = old * 0.90f + target * 0.10f;
            val = Mathf.Clamp(val, 0f, 100f);

            // 2026-08-18 受命保护期下限：保护期内天命值不低于 50（新受命根基未稳，防数值滑落至"欲坠"档）
            long protectUntil = KingdomDataExt.GetLong(k, KEY_PROTECT_UNTIL, 0);
            if (currentYear < protectUntil) val = Mathf.Max(val, 50f);

            KingdomDataExt.SetFloat(k, KEY_MANDATE_VALUE, val);

            // 2026-08-19 串联：天人感应明细持久化（UI"天命观察"卡读取——玩家首次可见天命为何涨跌）
            try { KingdomDataExt.SetStr(k, "mandate_detail", detail.ToString().TrimEnd('　')); } catch { }

            // 记录趋势（滚动最近 3 次结算值）
            RecordTrend(holderId, val);

            ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 天命值结算: {k.name} {old:F1}→{val:F1} (目标{target:F0}, {GetMandateGrade(val)})");
        }

        // ═══════════════════════ 天命值趋势（最近 3 次结算） ═══════════════════════

        private const string KEY_TREND_0 = "mandate_trend0"; // 最近一次
        private const string KEY_TREND_1 = "mandate_trend1"; // 上期
        private const string KEY_TREND_2 = "mandate_trend2"; // 更早

        private void RecordTrend(string holderId, float val)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(holderId);
            if (k == null) return;
            float t0 = KingdomDataExt.GetFloat(k, KEY_TREND_0, -1f);
            float t1 = KingdomDataExt.GetFloat(k, KEY_TREND_1, -1f);
            KingdomDataExt.SetFloat(k, KEY_TREND_2, t1);
            KingdomDataExt.SetFloat(k, KEY_TREND_1, t0);
            KingdomDataExt.SetFloat(k, KEY_TREND_0, val);
        }

        /// <summary>获取天命值趋势（[最近, 上期, 更早]，无数据为 -1）</summary>
        public List<float> GetTrend(string holderId)
        {
            var list = new List<float>();
            Kingdom k = KingdomDataExt.FindKingdomById(holderId);
            if (k == null) return list;
            list.Add(KingdomDataExt.GetFloat(k, KEY_TREND_0, -1f));
            list.Add(KingdomDataExt.GetFloat(k, KEY_TREND_1, -1f));
            list.Add(KingdomDataExt.GetFloat(k, KEY_TREND_2, -1f));
            return list;
        }

        // ═══════════════════════ 转移条件检测（每年） ═══════════════════════

        private void CheckTransferConditions(int currentYear)
        {
            string holderId = GetMandateHolderId();

            if (holderId != null)
            {
                Kingdom holder = KingdomDataExt.FindKingdomById(holderId);

                // 条件 1：天命国灭亡
                if (holder == null || !holder.isAlive())
                {
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 天命国灭亡，触发天命转移");
                    // 阶段2A A4-3：天命崩绝入史册（MandateCollapsed 类型此前定义但从未使用）
                    try
                    {
                        ChronicleManager.Instance.AddEntry(currentYear, ChronicleEventType.MandateCollapsed,
                            holderId, holder != null ? holder.name ?? "" : "", "",
                            "<color=#f73939>天命崩绝</color>：天命之国亡于兵燹，正统移于天下。", true);
                    }
                    catch { }
                    TransferMandate(null, currentYear, "亡国失命");
                    return;
                }

                // 条件 2：连续 50 年 < 6 城
                int cityCount = holder.cities != null ? holder.cities.Count : 0;
                if (cityCount < 6)
                {
                    // 保护期内不触发衰微失命（受命之初未稳——2026-08-18）
                    long protectUntil = KingdomDataExt.GetLong(holder, KEY_PROTECT_UNTIL, 0);
                    if (currentYear < protectUntil)
                    {
                        KingdomDataExt.SetLong(holder, KEY_BELOW6_SINCE, 0); // 保护期持续重置计数
                    }
                    else
                    {
                        long since = KingdomDataExt.GetLong(holder, KEY_BELOW6_SINCE, 0);
                        if (since == 0)
                        {
                            KingdomDataExt.SetLong(holder, KEY_BELOW6_SINCE, currentYear);
                        }
                        else if (currentYear - since >= BELOW6_YEARS_LIMIT)
                        {
                            ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 天命国连续{BELOW6_YEARS_LIMIT}年不足6城，天命转移");
                            TransferMandate(holder, currentYear, "衰微失命");
                            return;
                        }
                    }
                }
                else
                {
                    // 恢复 6 城以上，重置计数
                    KingdomDataExt.SetLong(holder, KEY_BELOW6_SINCE, 0);
                }
            }
            else
            {
                // ⚠️ 自愈：天命国被灭（removeObject 直接移除王国）时 ScanMandateHolderId 变 null，
                // 走不到上方"亡国失命"分支 → 历史里残留 active 朝代永远显示"至今"（用户反馈：天命国灭亡还显示至今）
                var stale = _history.FindLast(d => d.EndYear == -1 && d.Dynasty != "空悬");
                if (stale != null)
                {
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 自愈：关闭残留 active 朝代「{stale.Dynasty}」亡国失命({currentYear}年)");
                    RecordDynastyEnd(stale.KingdomId, currentYear, "亡国失命");
                }

                // 天命空悬：检查是否有 >6 城国家出现
                Kingdom candidate = FindBestCandidate();
                // B1（阶段1）：世界从未有过天命（历史空）且无 6 城国 → 开局受命
                // （根因：大图每国 1 城且原版扩张被战争网关限制，城市不增长 → 6 城门槛永不可达 → 天命永远空悬）
                if (candidate == null && _history.Count == 0)
                {
                    candidate = FindInitialMandateCandidate();
                    if (candidate != null)
                    {
                        ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 开局受命：{candidate.name} 立国受命于天");
                        GrantMandate(candidate, currentYear);
                        return; // 阶段2U 修复：开局受命即完成（原继续落底部分支——同年双 GrantMandate，两条同国 active 朝代）
                    }
                }
                if (candidate != null)
                {
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 天命空悬结束，{candidate.name} 国力冠绝诸侯且礼备，受命于天");
                    GrantMandate(candidate, currentYear);
                }
                else if (stale != null)
                {
                    // 关闭了残留朝代但无继承者 → 确认空悬（若没有未封口的空悬记录则补一条，历史连续）
                    if (_history.FindLast(d => d.Dynasty == "空悬" && d.EndYear == -1) == null)
                    {
                        RecordVacantStart(currentYear);
                    }
                }
            }
        }

        // ═══════════════════════ 天命转移/授予 ═══════════════════════

        private void TransferMandate(Kingdom oldHolder, int year, string reason)
        {
            // 记录旧朝代终结（按王国匹配；天命国已亡时传 null 走兜底关闭最后 active）
            RecordDynastyEnd(oldHolder != null ? oldHolder.id.ToString() : null, year, reason);

            // 清除旧持有者
            if (oldHolder != null)
            {
                KingdomDataExt.SetLong(oldHolder, KEY_HAS_MANDATE, 0);
            }

            // 找新持有者
            Kingdom newHolder = FindBestCandidate();
            if (newHolder != null)
            {
                GrantMandate(newHolder, year);
            }
            else
            {
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 无合格继承者，天命空悬");
                RecordVacantStart(year);
                StoreHistoryToStrongest(year);
            }
        }

        /// <summary>夺天命之战胜利：天命强制转移给指定挑战者（2026-08-19 方案5——不走 FindBestCandidate 择优，
        /// 谁打赢谁受命；挑战者已亡则天命空悬）。</summary>
        public void ForceTransferMandate(Kingdom oldHolder, Kingdom newHolder, int year, string reason)
        {
            try
            {
                RecordDynastyEnd(oldHolder != null ? oldHolder.id.ToString() : null, year, reason);
                if (oldHolder != null && oldHolder.isAlive())
                {
                    KingdomDataExt.SetLong(oldHolder, KEY_HAS_MANDATE, 0);
                }
                if (newHolder != null && newHolder.isAlive())
                {
                    GrantMandate(newHolder, year);
                }
                else
                {
                    ZhuluContext.Instance.LogInfo("[OrthodoxMandate] 夺鼎者已亡，天命空悬");
                    RecordVacantStart(year);
                    StoreHistoryToStrongest(year);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OrthodoxMandate] ForceTransferMandate 异常", ex);
            }
        }

        private void GrantMandate(Kingdom k, int year, string dynastyOverride = null)
        {
            // ⚠️ 前置清理：若已存在其他天命国（旧天命未清——缓存失效/双天命并存场景），
            // 先关闭其朝代记录并清除标记，保证天下唯一正统
            CloseAnyOtherMandateHolder(k, year);

            string kId = k.id.ToString();

            // 2026-08-18 防历史分裂：该国已有 active 朝代记录（天命标记曾丢失但历史健在——
            // 读档时序/标记未恢复场景）→ 复用既有记录，只刷新元数据，绝不新建（否则现任皇帝
            // 变"第一任"、旧朝皇帝序列被拆档——用户实锤的"点进存档现任变第一任"根因）。
            var existingActive = _history.FindLast(d => d.EndYear == -1 && d.KingdomId == kId && d.Dynasty != "空悬");
            if (existingActive != null)
            {
                EnsureHistoryLoaded(); // 幂等（历史已在此前流程加载）
                KingdomDataExt.SetLong(k, KEY_HAS_MANDATE, 1);
                KingdomDataExt.SetLong(k, KEY_BELOW6_SINCE, 0);
                KingdomDataExt.SetLong(k, KEY_PROTECT_UNTIL, year + PROTECT_YEARS);
                string keepDyn = KingdomDataExt.GetStr(k, KEY_DYNASTY_NAME, "");
                if (string.IsNullOrEmpty(keepDyn)) keepDyn = existingActive.Dynasty;
                KingdomDataExt.SetStr(k, KEY_DYNASTY_NAME, keepDyn);
                string curKingName = (k.king != null && k.king.isAlive()) ? k.king.getName() : "未知";
                KingdomDataExt.SetStr(k, KEY_EMPEROR_NAME, curKingName);
                // 末帶若为在位（EndYear==-1）保持；否则补一条现任皇帝记录
                var lastEmp = existingActive.Emperors.Count > 0 ? existingActive.Emperors[existingActive.Emperors.Count - 1] : null;
                if (lastEmp == null || lastEmp.EndYear != -1 || lastEmp.Name != curKingName)
                {
                    if (lastEmp != null && lastEmp.EndYear == -1)
                    {
                        // 在位者与现任君名不同（换帝未经 RecordNewEmperor——补盖棺）
                        CloseEmperor(existingActive, lastEmp, kId, k, year, lostThrone: false, null);
                    }
                    string eraNow = EraNameService.Instance.GetRandomEraName();
                    KingdomDataExt.SetStr(k, KEY_ERA_NAME, eraNow);
                    int cities = k.cities != null ? k.cities.Count : 0;
                    long treasury = TreasuryManager.Instance.GetReport(kId).Treasury;
                    existingActive.Emperors.Add(new EmperorRecord
                    {
                        Name = curKingName,
                        Era = eraNow,
                        StartYear = year,
                        StartCities = cities,
                        StartTreasury = treasury,
                        StartVassals = CountVassalsOf(kId),
                        StartMandate = MandateValueNow(kId),
                        StartStability = GetStabilityOf(kId),
                        StartPopulation = k.units != null ? k.units.Count : 0,
                        EndCities = cities,
                        EndTreasury = treasury
                    });
                }
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 复用既有 active 朝代记录「{existingActive.Dynasty}朝」（防历史分裂）");
                return;
            }

            string kName = k.name ?? "未知国";

            // 设置天命标记
            KingdomDataExt.SetLong(k, KEY_HAS_MANDATE, 1);
            KingdomDataExt.SetFloat(k, KEY_MANDATE_VALUE, 60f); // 新受命起始值
            KingdomDataExt.SetLong(k, KEY_BELOW6_SINCE, 0);
            // 受命保护期（2026-08-18）：前 PROTECT_YEARS 年不触发衰微失命
            KingdomDataExt.SetLong(k, KEY_PROTECT_UNTIL, year + PROTECT_YEARS);

            // 朝代名 = 覆盖值（篡位者姓氏）或 国名去后缀的第一个字
            string dynastyName = !string.IsNullOrEmpty(dynastyOverride) ? dynastyOverride : ExtractDynastyName(kName);
            // 2026-08-18 同字朝代区分：前朝已用同字 → 加方位/时序前缀（西汉/东汉/后汉/蜀汉式）
            dynastyName = DisambiguateDynastyName(dynastyName, kId);
            KingdomDataExt.SetStr(k, KEY_DYNASTY_NAME, dynastyName);

            // 年号
            string eraName = EraNameService.Instance.GetRandomEraName();
            KingdomDataExt.SetStr(k, KEY_ERA_NAME, eraName);

            // 皇帝名
            string kingName = (k.king != null && k.king.isAlive()) ? k.king.getName() : "未知";
            KingdomDataExt.SetStr(k, KEY_EMPEROR_NAME, kingName);

            // 史册 + WorldTip
            ChronicleManager.Instance.AddEntry(year, ChronicleEventType.MandateGranted,
                kId, kName, kingName,
                $"<color={UI.ZhuluUIStyle.Hex.Imperial}>【{dynastyName}朝】受天命！</color>{kingName} 称帝，改元「{eraName}」，正统归于【{dynastyName}朝】。",
                true);

            try { WorldTip.showNow($"天命所归！【{dynastyName}朝】{kingName} 受命称帝，改元「{eraName}」", false, "top", 5f, UI.ZhuluUIStyle.Hex.Imperial); } catch { }

            // 阶段3：个人史册（君临类——开国之君人生高光）
            if (k.king != null && k.king.isAlive())
                PersonHistoryManager.Instance.RecordEvent(k.king.getID(), PersonHistoryManager.CAT_JUNLIN,
                    $"受天命称帝，开【{dynastyName}朝】，改元「{eraName}」", year);

            ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 授予天命: {kName}, 朝代={dynastyName}, 年号={eraName}, 皇帝={kingName}");

            // 历史记录
            RecordVacantEnd(year);
            RecordNewDynasty(dynastyName, kId, kingName, eraName, year);

            // ⚠️ 历史清理：新天命国受命时，关闭历史中所有其他 active 朝代（含标记已清但记录残留的旧国——
            // 淮朝 223~今 实锤场景：CloseAnyOtherMandateHolder 只按 has_mandate 标记找，标记已清就漏网）。
            // 用授命年关闭（不是当前年），保证历史年份正确。
            bool histChanged = false;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var d = _history[i];
                if (d.EndYear == -1 && d.Dynasty != "空悬" && d.KingdomId != kId)
                {
                    d.EndYear = year;
                    d.EndReason = "天命转移";
                    histChanged = true;
                }
            }
            if (histChanged)
            {
                SaveHistory();
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 受命后历史清理：关闭残留 active 旧朝代（{year}年）");
            }
        }

        /// <summary>
        /// 2026-08-18 篡位改朝换代：天命国发生异姓篡位时——
        /// ① 关闭旧朝代记录（篡位失国，旧帝盖棺定谥）
        /// ② 以篡位者姓氏为新朝代名，同国再受命（新朝代卡片与旧朝分离）
        /// ③ 新朝受命保护期自动生效。
        /// 返回新朝代名（失败/非天命国返回 null——调用方据此决定是否改国名）。
        /// </summary>
        public string HandleUsurpation(Kingdom k, Actor usurper, int year)
        {
            try
            {
                if (k == null || usurper == null || !k.isAlive() || !usurper.isAlive()) return null;
                string kId = k.id.ToString();
                if (!IsMandateHolder(kId)) return null;

                // 新朝代名 = 篡位者完整姓氏（复姓安全；司马氏不能截成“司朝”）
                string newDynasty = "新";
                try
                {
                    var naming = Naming.ZhuluChineseNameService.Instance;
                    string surname = usurper.clan != null ? naming.GetClanSurname(usurper.clan) : "";
                    if (string.IsNullOrEmpty(surname)) surname = naming.GetPreferredSurname(usurper);
                    if (string.IsNullOrEmpty(surname)) surname = naming.ResolveSurname(Naming.ActorAccess.GetRawName(usurper));
                    if (!string.IsNullOrEmpty(surname)) newDynasty = surname;
                }
                catch { }

                // ① 关闭旧朝代（按王国匹配；旧帝由 RecordDynastyEnd→CloseEmperor 盖棺）
                string oldDynasty = KingdomDataExt.GetStr(k, KEY_DYNASTY_NAME, "");
                RecordDynastyEnd(kId, year, "篡位失国");
                KingdomDataExt.SetLong(k, KEY_HAS_MANDATE, 0);

                // ② 同国再受命（新朝卡片；GrantMandate 内含 CloseAnyOtherMandateHolder 幂等清理）
                GrantMandate(k, year, newDynasty);

                ChronicleManager.Instance.AddEntry(year, ChronicleEventType.DynasticChange, kId,
                    k.name ?? "", usurper.getName() ?? "",
                    $"<color={UI.ZhuluUIStyle.Hex.Negative}>改朝换代</color>：【{oldDynasty}朝】失统于篡，【{newDynasty}朝】继天受命，国祚更始。", true);
                try { WorldTip.showNow($"【{k.name}】{oldDynasty}朝气数已尽，{usurper.getName()} 开创【{newDynasty}朝】！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Imperial); } catch { }

                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 篡位改朝: {k.name} {oldDynasty}朝→{newDynasty}朝 ({year}年)");
                return newDynasty;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] HandleUsurpation 异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 受命前清理：关闭其他仍持有天命标记的旧天命国（缓存失效/双天命并存场景），保证唯一正统。
        /// </summary>
        private void CloseAnyOtherMandateHolder(Kingdom newHolder, int year)
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom other in World.world.kingdoms.list)
                {
                    if (other == null || !other.isAlive() || other == newHolder) continue;
                    if (KingdomDataExt.GetLong(other, KEY_HAS_MANDATE, 0) != 1) continue;

                    // 关闭旧天命国朝代记录（按王国匹配，防关闭错朝代）
                    RecordDynastyEnd(other.id.ToString(), year, "天命转移");
                    KingdomDataExt.SetLong(other, KEY_HAS_MANDATE, 0);
                    KingdomDataExt.SetLong(other, KEY_BELOW6_SINCE, 0);
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 受命前清理旧天命国: {other.name} → 天命转移({year}年)");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] 清理旧天命国异常: {ex.Message}");
            }
        }

        /// <summary>换帝时改元（由 Main.ObserveKingChanges 调用）。</summary>
        public void OnKingChange(string kingdomId, string newKingName, int year, string oldKingId = null)
        {
            // 实时扫描天命国（不能依赖 GetMandateHolderId 缓存——缓存可能失效导致换帝从不记录）
            string holderId = ScanMandateHolderId();
            if (kingdomId != holderId)
            {
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 非天命国换帝忽略: {kingdomId} (天命国={holderId ?? "无"})");
                return;
            }

            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return;

            string oldEmperor = KingdomDataExt.GetStr(k, KEY_EMPEROR_NAME, "");
            if (oldEmperor == newKingName) return; // 同一人，不改元

            // 改元
            string newEra = EraNameService.Instance.GetRandomEraName();
            KingdomDataExt.SetStr(k, KEY_ERA_NAME, newEra);
            KingdomDataExt.SetStr(k, KEY_EMPEROR_NAME, newKingName);

            // 阶段2A A4-2：史册去重——换君已由 DynastyObserver 统一记录（含分类），
            // 此处不再重复记 KingChange；改元信息保留在历史记录（RecordNewEmperor）供天命窗口展示。

            ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 天命国换帝: {oldEmperor}→{newKingName}, 改元「{newEra}」");

            // 历史记录（阶段2O：传旧君 id——关闭记录时取德行特质与寿数）
            RecordNewEmperor(kingdomId, newKingName, newEra, year, oldKingId);
        }

        // ═══════════════════════ 管道效果 ═══════════════════════

        private void ApplyMandateEffects(int currentYear)
        {
            string holderId = GetMandateHolderId();
            if (holderId == null) return;

            float val = GetMandateValue(holderId);
            string planId = $"orthodox_mandate_{holderId}";

            // 阶段3H-12：忠诚档位（75+ 每 5 分一档 +10 至 +70；100 直取 +100；以下 10 分一档 ±10）
            float loyaltyBonus = 0f;
            if (val >= 100f) loyaltyBonus = 100f;
            else if (val >= 95f) loyaltyBonus = 70f;
            else if (val >= 90f) loyaltyBonus = 60f;
            else if (val >= 85f) loyaltyBonus = 50f;
            else if (val >= 80f) loyaltyBonus = 40f;
            else if (val >= 75f) loyaltyBonus = 30f;
            else if (val >= 65f) loyaltyBonus = 20f;
            else if (val >= 55f) loyaltyBonus = 10f;
            else if (val >= 45f) loyaltyBonus = 0f;
            else if (val >= 35f) loyaltyBonus = -10f;
            else if (val >= 25f) loyaltyBonus = -20f;
            else if (val >= 15f) loyaltyBonus = -30f;
            else loyaltyBonus = -40f;

            if (!Mathf.Approximately(loyaltyBonus, 0f))
            {
                ModifierPipeline.Instance.RegisterPlan(new ModifierPlan
                {
                    SourceId = planId,
                    TargetKingdomId = holderId,
                    Channel = ModifierChannels.CityLoyaltyBonus,
                    Additive = loyaltyBonus,
                    Multiplicative = 1.0f,
                    Reason = $"正统天命·{GetMandateGrade(val)} (忠诚{(loyaltyBonus > 0 ? "+" : "")}{loyaltyBonus:F0})"
                });
            }
            else
            {
                ModifierPipeline.Instance.RemovePlan(planId);
            }

            // 天命欲绝警告
            if (val < 10f)
            {
                Kingdom k = KingdomDataExt.FindKingdomById(holderId);
                if (k != null && UnityEngine.Random.value < 0.05f) // 低概率播报避免刷屏
                {
                    try { WorldTip.showNow($"【{k.name}】天命欲绝，正统将移！", false, "top", 3f, "#FF4500"); } catch { }
                }
            }
        }

        // ═══════════════════════ 辅助方法 ═══════════════════════

        /// <summary>
        /// 受命候选（阶段2B 包2 重构，2026-08-15 设计定稿）：
        /// ① 资格：独立国（非附庸）+ 城≥6 或 国力点≥40 + 国祚≥15
        /// ② 礼治：净礼治分 ≥5（诸侯礼政策+祭祀+德行）
        /// ③ 排序：国力点优先（替代城多者胜）
        /// </summary>
        private Kingdom FindBestCandidate()
        {
            if (World.world?.kingdoms?.list == null) return null;
            Kingdom best = null;
            int bestScore = -1;
            int currentYear = ZhuluTime.CurrentYear;

            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                if (!KingdomCivilizationService.Instance.IsEastern(k)) continue;
                string kId = k.id.ToString();

                // ① 资格：独立国
                if (VassalManager.Instance.IsVassal(k)) continue;
                if (ZhuluRebelService.IsRebel(k)) continue; // 阶段2V：叛军不得受命（草莽僭伪，非正统之阶）
                int pp = ZhuluTierService.GetPowerPoints(k);
                // 阶段2O 定稿（用户拍板）：受命基业=国力点≥40（去掉"城≥6"路径——与空悬卡文案同口径）
                if (pp < 40) continue;
                // 国祚 ≥15
                if (k.getAge() < 15) continue;

                // ② 礼治 ≥8（2026-08-15 调参：原 5 太易——400 年 5 国受命，升 8 增仪式感）
                if (ZhuluLiZhiService.GetNetLiZhi(kId) < 8) continue;

                // ③ 国力点排序
                if (pp > bestScore)
                {
                    best = k;
                    bestScore = pp;
                }
            }
            return best;
        }

        /// <summary>按当前东方受命规则检查单一国家，不改变任何数据。</summary>
        public bool IsQualifiedCandidate(Kingdom k)
        {
            if (k == null || !k.isAlive() || !KingdomCivilizationService.Instance.IsEastern(k)) return false;
            if (VassalManager.Instance.IsVassal(k) || ZhuluRebelService.IsRebel(k)) return false;
            if (ZhuluTierService.GetPowerPoints(k) < 40 || k.getAge() < 15) return false;
            return ZhuluLiZhiService.GetNetLiZhi(k.id.ToString()) >= 8;
        }

        /// <summary>异体系篡夺原天眷国后：东方天命空悬且该国达标时立即受命。</summary>
        public bool TryGrantVacantTo(Kingdom k, int year)
        {
            if (!string.IsNullOrEmpty(GetMandateHolderId()) || !IsQualifiedCandidate(k)) return false;
            GrantMandate(k, year);
            return true;
        }

        /// <summary>开局受命候选（B1）：有效城≥3 或 人口≥300 的活国中取人口最多者。</summary>
        private Kingdom FindInitialMandateCandidate()
        {
            if (World.world?.kingdoms?.list == null) return null;
            Kingdom best = null;
            int bestPop = 0;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                if (!KingdomCivilizationService.Instance.IsEastern(k)) continue;
                string kId = k.id.ToString();
                // 阶段2B 包2：开局受命同样需资格（独立+国祚≥15）+ 礼治≥5（防开局自动受命）
                if (VassalManager.Instance.IsVassal(k)) continue;
                if (ZhuluRebelService.IsRebel(k)) continue; // 阶段2V：叛军不得受命
                if (k.getAge() < 15) continue;
                if (ZhuluLiZhiService.GetNetLiZhi(kId) < 8) continue; // 2026-08-15 调参 5→8
                int effCities = KingdomExt.GetEffectiveCities(kId, k.cities != null ? k.cities.Count : 0);
                int pop = k.units != null ? k.units.Count : 0;
                if (effCities >= 3 || pop >= 300)
                {
                    if (pop > bestPop) { best = k; bestPop = pop; }
                }
            }
            return best;
        }

        /// <summary>
        /// 2026-08-18 朝代名美化：经典朝代字池——受命国号优先取先秦两汉经典封国/朝代字。
        /// 原实现盲取国名首字，"宁朝""平朝""安朝"类组合缺乏历史质感（用户反馈朝代名不雅观）。
        /// 国名字恰在池中（秦/楚/齐/燕等）→ 尊重地域来源直接用；否则换经典字。
        /// </summary>
        private static readonly string[] ClassicDynastyChars =
        {
            "秦", "汉", "晋", "隋", "唐", "周", "魏", "齐", "楚", "燕", "赵", "韩", "梁", "陈", "吴", "越",
            "蜀", "夏", "商", "宋", "鲁", "郑", "卫", "蔡", "代", "岐", "雍", "凉", "幽", "冀", "青", "徐",
            "荆", "扬", "益", "陇", "朔", "汾", "渭", "洛"
        };

        private string ExtractDynastyName(string kingdomName)
        {
            if (string.IsNullOrEmpty(kingdomName)) return "天";
            // 去掉常见后缀，取第一个字作为朝代名
            string[] suffixes = { "帝国", "朝", "公国", "侯国", "伯国", "子国", "国", "郡", "邑" };
            string name = kingdomName;
            foreach (var s in suffixes)
            {
                if (name.EndsWith(s) && name.Length > s.Length)
                {
                    name = name.Substring(0, name.Length - s.Length);
                    break;
                }
            }
            string first = name.Length >= 1 ? name.Substring(0, 1) : "天";
            // 国号字在经典池 → 地域来源真实（如"秦国"受命即"秦朝"），直接用
            foreach (var c in ClassicDynastyChars)
            {
                if (first == c) return first;
            }
            // 不在池 → 按原国名稳定映射到经典池。既保持“一次生成后持久化”的语义，
            // 也避免单纯起名消耗 Unity 全局随机序列、间接改变战争或政变模拟结果。
            return ClassicDynastyChars[StableDynastyIndex(kingdomName, ClassicDynastyChars.Length)];
        }

        private static int StableDynastyIndex(string key, int length)
        {
            if (length <= 1) return 0;
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in key ?? "")
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)(hash % (uint)length);
            }
        }

        /// <summary>
        /// 2026-08-18 同字朝代区分（史家惯例）：前朝已用同字 → 加前缀。
        /// 汉第二次 → 东汉/西汉；第三次 → 后汉/前汉；第四次 → 蜀汉/南汉；第五次 → 北汉。
        /// 前缀从未用过的池中随机取（跨局多样）；全部用尽 → "季"（季汉——史称蜀汉之雅号）。
        /// 只统计已结束或他国的记录；本国 active 记录不算（复用场景走 existingActive 分支不会到这里）。
        /// </summary>
        private static readonly string[] DynastyQualifierPool = { "东", "西", "后", "前", "蜀", "南", "北", "成", "夏" };

        private string DisambiguateDynastyName(string baseName, string kId)
        {
            try
            {
                if (string.IsNullOrEmpty(baseName)) return baseName;
                EnsureHistoryLoaded();
                // 同底字历史朝代计数（底字 = 去前缀后的字：'东汉'→'汉'）
                int priorCount = 0;
                foreach (var d in _history)
                {
                    if (d == null || d.Dynasty == "空悬") continue;
                    if (d.KingdomId == kId && d.EndYear == -1) continue; // 本国 active 不计
                    string dyn = d.Dynasty ?? "";
                    string stem = dyn.Length > 1 && DynastyQualifierPool.Contains(dyn.Substring(0, 1))
                        ? dyn.Substring(1) : dyn;
                    if (stem == baseName) priorCount++;
                }
                if (priorCount == 0) return baseName;

                // 取未用过的前缀（"东X"已存在则跳过）
                var candidates = new List<string>();
                foreach (var q in DynastyQualifierPool)
                {
                    string cand = q + baseName;
                    bool used = false;
                    foreach (var d in _history)
                    {
                        if (d != null && d.Dynasty == cand) { used = true; break; }
                    }
                    if (!used) candidates.Add(cand);
                }
                if (candidates.Count == 0) return "季" + baseName; // 极端兜底（季汉）
                string picked = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 同字朝代区分：「{baseName}」已有 {priorCount} 朝 → 「{picked}」");
                return picked;
            }
            catch { return baseName; }
        }

        private void StoreHistoryToStrongest(int year)
        {
            // 空悬时把历史 JSON 存到城池最多的王国（确保不丢失）
            Kingdom strongest = FindBestCandidate();
            if (strongest == null)
            {
                // 真的没有任何 >6 城国家，找任意存活国
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        if (k != null && k.isAlive()) { strongest = k; break; }
                    }
                }
            }
            // 历史 JSON 的维护在后续版本完善（当前先确保天命标记正确）
        }

        /// <summary>初始化/读档后调用：恢复年号池已用标记。</summary>
        public void RehydrateEraNames()
        {
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string era = KingdomDataExt.GetStr(k, KEY_ERA_NAME, "");
                if (!string.IsNullOrEmpty(era))
                {
                    EraNameService.Instance.MarkUsed(era);
                }
            }
        }

        // ═══════════════════════ 天命历史记录管理 ═══════════════════════

        /// <summary>获取全部历史朝代记录（供窗口渲染）。</summary>
        public List<DynastyRecord> GetHistory()
        {
            return _history;
        }

        /// <summary>
        /// LlmFix-5（H2，用户已选"回写记录"）：AI 评语落库后，把它选的谥字/庙号写回帝王记录。
        /// 订阅 LlmEmperorService.OnCommentApplied（ZhuluBootstrap 注册，主线程触发）。
        /// 下游自然生效：下次盖棺的 usedShis/usedTemples 从记录重建 → AI 字进入去重池；
        /// 窗口徽章/谥法释义读记录 → 自动更新。
        /// 已知边界：个人史册盖棺条目保留死时的本地谥号，不回改；
        /// 盖棺到 AI 返回之间先显本地字（异步回填，属预期）。
        /// </summary>
        public void HandleAiCommentApplied(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;
                var c = Interop.LlmCommentStore.Instance.Get(key);
                if (c == null) return;
                if (ApplyAiChoiceToList(_history, key, c))
                {
                    ZhuluContext.Instance.LogRateLimited("AiChoice",
                        "[Llm] 谥号回写：" + key + " → " + c.Temple + c.Shi +
                        (string.IsNullOrEmpty(c.ShiftReason) ? "" : "（位移" + c.Shift + "：" + c.ShiftReason + "）"), 5f);
                }
            }
            catch { /* 回写失败不影响评语与游戏 */ }
        }

        /// <summary>
        /// 纯函数（可单测）：把 AI 选择写回记录。键=朝代|帝序(1起)|帝名|即位年|卒年；
        /// 名字/年份对不上（同名多期等）不写——评语仍然生效，只是不回写记录。
        /// 空 Shi/空 Temple 表示模型未选（候选池空），保留本地值。
        /// </summary>
        public static bool ApplyAiChoiceToList(List<DynastyRecord> history, string key, Interop.LlmEmperorComment c)
        {
            if (history == null || c == null || string.IsNullOrEmpty(key)) return false;
            string[] p = key.Split('|');
            if (p.Length != 5) return false;
            int idx;
            if (!int.TryParse(p[1], out idx) || idx < 1) return false;
            int startYear, endYear;
            if (!int.TryParse(p[3], out startYear) || !int.TryParse(p[4], out endYear)) return false;

            var d = history.Find(x => x != null && x.Dynasty == p[0]);
            if (d == null || d.Emperors == null || idx > d.Emperors.Count) return false;
            var e = d.Emperors[idx - 1];
            if (e == null || e.Name != p[2] || e.StartYear != startYear || e.EndYear != endYear)
                return false;

            bool changed = false;
            if (!string.IsNullOrEmpty(c.Shi) && c.Shi != e.PosthumousName)
            {
                e.PosthumousName = c.Shi;
                changed = true;
            }
            if (!string.IsNullOrEmpty(c.Temple) && c.Temple != e.TempleName)
            {
                e.TempleName = c.Temple;
                changed = true;
            }
            return changed;
        }

        // ═══════════════════════ 在位轨迹采样（LlmFix-6）═══════════════════════

        /// <summary>采样后回写极盛年（纯函数，可单测）。</summary>
        public static void RefreshPeakYear(EmperorRecord e)
        {
            if (e == null) return;
            var peak = EmperorTrackSampler.Peak(e.Track);
            e.PeakYear = peak != null ? peak.Year : 0;
        }

        /// <summary>
        /// LlmFix-6（M1）：给所有在位帝王按配置间隔采样（城/民/库/天命/稳定/附庸/排名），
        /// 回写 PeakYear 供「极盛 N 年」与 AI 轨迹形态。间隔 0=关闭。
        /// 排名口径=国力点（城×6+人口÷40，与天下总览一致），全图比较，每个采样年惰性构建一次。
        /// </summary>
        private void SampleActiveEmperors(int currentYear)
        {
            try
            {
                int interval = ZhuluConfig.Instance.EmperorSampleInterval;
                if (interval <= 0) return;
                if (_history == null || _history.Count == 0) return;

                Dictionary<string, int> powerById = null;   // 本年全图国力点（首个采样触发构建）

                foreach (var d in _history)
                {
                    if (d == null || d.EndYear != -1 || d.Emperors == null) continue;
                    var e = d.Emperors.FindLast(x => x != null && x.EndYear == -1);
                    if (e == null) continue;
                    if (!EmperorTrackSampler.ShouldSample(e.Track, currentYear, interval)) continue;

                    Kingdom k = KingdomDataExt.FindKingdomById(d.KingdomId);
                    if (k == null) continue;   // 王国已亡——本年无数据可采（首尾由盖棺兜底）

                    if (powerById == null)
                    {
                        powerById = new Dictionary<string, int>();
                        if (World.world?.kingdoms?.list != null)
                        {
                            foreach (Kingdom kk in World.world.kingdoms.list)
                            {
                                if (kk == null) continue;
                                powerById[kk.id.ToString()] = EmperorSample.PowerScoreOf(
                                    kk.cities != null ? kk.cities.Count : 0,
                                    kk.units != null ? kk.units.Count : 0);
                            }
                        }
                    }

                    int rank = -1;
                    string myId = d.KingdomId;
                    int myScore;
                    if (powerById.TryGetValue(myId, out myScore))
                    {
                        rank = 1;
                        foreach (var kv in powerById)
                            if (kv.Key != myId && kv.Value > myScore) rank++;
                    }

                    if (EmperorTrackSampler.AddSample(e.Track, new EmperorSample
                    {
                        Year = currentYear,
                        Cities = k.cities != null ? k.cities.Count : 0,
                        Population = k.units != null ? k.units.Count : 0,
                        Treasury = TreasuryManager.Instance.GetReport(myId).Treasury,
                        Mandate = MandateValueNow(myId),
                        Stability = GetStabilityOf(myId),
                        Vassals = CountVassalsOf(myId),
                        Rank = rank
                    }))
                    {
                        RefreshPeakYear(e);
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("EmperorSample",
                    "[OrthodoxMandate] 轨迹采样异常: " + ex.Message, 30f);
            }
        }

        // ═══════════════════════ 断代史（LlmV2-4）═══════════════════════

        /// <summary>年期是否届满断代史（纯函数）：返回 (start,end)；未届满/关闭/非法返回 null。</summary>
        public static Tuple<int, int> ChronicleSpanDue(int year, int interval)
        {
            if (interval <= 0 || year <= 0) return null;
            if (year % interval != 0) return null;
            return Tuple.Create(year - interval + 1, year);
        }

        /// <summary>
        /// 断代史触发（TickAll 每年调用）：届满且该段未生成过 → 采集事实 → 入队 LLM。
        /// 失败兜底在回调内完成（本地模板同样落库，防同段反复请求）。
        /// </summary>
        private void TryDigestChronicle(int currentYear)
        {
            try
            {
                var span = ChronicleSpanDue(currentYear, ZhuluConfig.Instance.ChronicleDigestInterval);
                if (span == null) return;
                if (_history == null || _history.Count == 0) return;

                var store = Interop.LlmChronicleStore.Instance;
                string key = Interop.LlmChronicleStore.BuildKey(span.Item1, span.Item2);
                if (store.Has(key)) return;

                var svc = Interop.LlmEmperorService.Instance;
                if (!svc.Config.Enabled) return;

                var facts = CollectChronicleFacts(span.Item1, span.Item2);
                if (facts.Count == 0) return;
                int maxChars = Math.Min(200, Math.Max(60, svc.Config.MaxChars));

                svc.Enqueue(new Interop.LlmTask
                {
                    Kind = Interop.LlmRequestKind.Chronicle,
                    Key = key,
                    SystemPrompt = Interop.LlmPromptBuilder.ChronicleSystemPromptFor(maxChars),
                    UserPayload = Interop.LlmPromptBuilder.BuildChroniclePayload(
                        span.Item1, span.Item2, facts, _history.Count),
                    OnResult = r =>
                    {
                        string digest = null;
                        if (r != null && r.Success && !string.IsNullOrEmpty(r.Text))
                            digest = Interop.LlmChronicleStore.TryParseDigest(r.Text, 20, maxChars);
                        bool fromLlm = digest != null;
                        if (!fromLlm) digest = BuildFallbackDigest(span.Item1, span.Item2, facts);
                        store.Put(key, new Interop.LlmChronicleEntry
                        {
                            SpanStart = span.Item1,
                            SpanEnd = span.Item2,
                            Digest = digest,
                            Source = fromLlm ? "llm" : "fallback"
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("LlmChronicle",
                    "[Llm] 断代史触发异常: " + ex.Message, 30f);
            }
        }

        /// <summary>采集段内朝代事实（朝代重叠 → 帝数/战事/施政/结局）。</summary>
        private List<Interop.LlmPromptBuilder.DynastyDigestFact> CollectChronicleFacts(int spanStart, int spanEnd)
        {
            var list = new List<Interop.LlmPromptBuilder.DynastyDigestFact>();
            foreach (var d in _history)
            {
                if (d == null || d.Emperors == null) continue;
                bool overlaps = d.StartYear <= spanEnd && (d.EndYear == -1 || d.EndYear >= spanStart);
                if (!overlaps) continue;

                int emperors = 0;
                foreach (var e in d.Emperors)
                    if (e != null && e.StartYear <= spanEnd && (e.EndYear == -1 || e.EndYear >= spanStart))
                        emperors++;

                int wars = 0, policies = 0;
                try
                {
                    var chronicle = ChronicleManager.Instance.GetKingdomChronicle(d.KingdomId);
                    if (chronicle != null)
                    {
                        foreach (var c in chronicle)
                        {
                            if (c == null || c.Year < spanStart || c.Year > spanEnd) continue;
                            if (c.Type == ChronicleEventType.WarDeclared) wars++;
                            else if (c.Type == ChronicleEventType.PolicyEnacted) policies++;
                        }
                    }
                }
                catch { }

                list.Add(new Interop.LlmPromptBuilder.DynastyDigestFact
                {
                    Name = d.Dynasty,
                    StartYear = d.StartYear,
                    EndYear = d.EndYear,
                    Emperors = emperors,
                    Wars = wars,
                    Policies = policies,
                    EndReason = d.EndReason
                });
            }
            return list;
        }

        /// <summary>本地模板兜底（纯函数可单测）：AI 不可用时的事实拼接史论。</summary>
        public static string BuildFallbackDigest(int spanStart, int spanEnd,
            List<Interop.LlmPromptBuilder.DynastyDigestFact> facts)
        {
            int emperors = 0;
            var names = new List<string>();
            if (facts != null)
            {
                foreach (var f in facts)
                {
                    if (f == null) continue;
                    emperors += f.Emperors;
                    names.Add(f.Name);
                }
            }
            string dynastyText = names.Count == 0 ? "天下无朝" : string.Join("、", names);
            return $"{PosthumousNameGenerator.ToChinese(spanStart)}至{PosthumousNameGenerator.ToChinese(spanEnd)}年间，"
                 + $"历{dynastyText}，凡{PosthumousNameGenerator.ToChinese(names.Count)}朝"
                 + $"{PosthumousNameGenerator.ToChinese(emperors)}帝。";
        }

        /// <summary>实时扫描天命国（不缓存——缓存可能在世界加载早期被初始化为 null 导致永久失效）</summary>
        private string ScanMandateHolderId()
        {
            if (World.world?.kingdoms?.list == null) return null;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                if (KingdomDataExt.GetLong(k, KEY_HAS_MANDATE, 0) == 1)
                {
                    return k.id.ToString();
                }
            }
            // 2026-08-20 读档兜底：custom_data 漏写/时序致扫不到持有者时，按历史 active 朝代恢复正统——
            // 防强者在正统尚存时误夺天命（用户实锤：读档后原天命国失天命、强公国夺位）
            try
            {
                var active = _history.FindLast(d => d.EndYear == -1 && d.Dynasty != "空悬");
                if (active != null)
                {
                    Kingdom hk = KingdomDataExt.FindKingdomById(active.KingdomId);
                    if (hk != null && hk.isAlive())
                    {
                        KingdomDataExt.SetLong(hk, KEY_HAS_MANDATE, 1);
                        ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] 历史active朝代兜底恢复持有: {active.Dynasty}（读档数据防护）");
                        return active.KingdomId;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>阶段2M-29：当前天命值（mandate 数据）。</summary>
        private float MandateValueNow(string kingdomId)
        {
            try
            {
                var md = MandateManager.Instance.GetMandateData(kingdomId);
                return md != null ? md.Value : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>阶段2M-29：国家附庸数。</summary>
        private int CountVassalsOf(string kingdomId)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return 0;
                int n = 0;
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom o in World.world.kingdoms.list)
                    {
                        if (o == null || o == k || !o.isAlive()) continue;
                        var suz = VassalManager.Instance.GetSuzerain(o);
                        if (suz != null && suz.id == k.id) n++;
                    }
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>阶段2M-29：国家稳定度。</summary>
        private float GetStabilityOf(string kingdomId)
        {
            try { return StabilityManager.Instance.GetStability(kingdomId); }
            catch { return 50f; }
        }

        private void RecordNewDynasty(string dynasty, string kingdomId, string emperorName, string era, int year)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            int cities = (k != null && k.cities != null) ? k.cities.Count : 0;
            long treasury = TreasuryManager.Instance.GetReport(kingdomId).Treasury;

            var record = new DynastyRecord
            {
                Dynasty = dynasty,
                KingdomId = kingdomId,
                StartYear = year,
                EndYear = -1,
                EndReason = "active"
            };
            record.Emperors.Add(new EmperorRecord
            {
                Name = emperorName,
                Era = era,
                StartYear = year,
                EndYear = -1,
                StartCities = cities,
                StartTreasury = treasury,
                EndCities = cities,
                EndTreasury = treasury,
                // 阶段2M-29：即位快照（附庸/天命/稳定）
                StartVassals = CountVassalsOf(kingdomId),
                EndVassals = CountVassalsOf(kingdomId),
                StartMandate = MandateValueNow(kingdomId),
                EndMandate = MandateValueNow(kingdomId),
                StartStability = GetStabilityOf(kingdomId),
                EndStability = GetStabilityOf(kingdomId),
                // 阶段2O：人口起卷
                StartPopulation = k != null && k.units != null ? k.units.Count : 0
            });
            // 阶段2M-29：期号（同国多次受命——一期/二期…）
            // 2026-08-19 修复：篡位改朝（EndReason="篡位失国"）天命未断、国祚连续——不计入 prior，
            // 防篡位新朝被误判为"二期国"；仅真正的失而复得（亡国失命/衰微失命/天命转移）递增期号
            int prior = 0;
            foreach (var d in _history)
            {
                if (d.KingdomId == kingdomId && d.EndYear != -1 && d.EndReason != "篡位失国") prior++;
            }
            record.ReignIndex = prior + 1;
            _history.Add(record);
            SaveHistory();
        }

        private void RecordNewEmperor(string kingdomId, string emperorName, string era, int year, string oldKingId = null)
        {
            // 确保历史已加载（否则 _history 为空，current==null 直接 return 导致换帝记录丢失！）
            EnsureHistoryLoaded();

            // 按王国匹配当前朝代（防 FindLast(active) 找到未关闭的旧朝代——曾致右朝皇帝被记进鲁朝）
            var current = _history.FindLast(d => d.EndYear == -1 && d.KingdomId == kingdomId);
            if (current == null)
            {
                // 找不到当前王国朝代：若该王国确是天命国（实时扫描），从当前状态重建朝代记录，防换帝丢失
                if (ScanMandateHolderId() == kingdomId)
                {
                    Kingdom holderK = KingdomDataExt.FindKingdomById(kingdomId);
                    string dynasty = holderK != null
                        ? KingdomDataExt.GetStr(holderK, KEY_DYNASTY_NAME, "未知")
                        : "未知";
                    // 2026-08-18 空名防御：key 存在但值为空串时 GetStr 返回 ""（不走默认值）——
                    // 序列化首字段空 → 解析回"未知朝"脏卡片；兜底为"未知"实体名
                    if (string.IsNullOrWhiteSpace(dynasty)) dynasty = "未知";
                    ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] 换帝时无当前朝代记录，自动重建 {dynasty}朝 ({emperorName} {year}年)");
                    RecordNewDynasty(dynasty, kingdomId, emperorName, era, year);
                    return;
                }
                ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] 换帝记录失败：无当前朝代记录 ({emperorName} {year}年)");
                return;
            }

            // 关闭上一位皇帝（阶段2O：统一走 CloseEmperor——终局快照+在位回溯+德行位标记+谥法引擎 v2）
            var lastEmperor = current.Emperors.Count > 0 ? current.Emperors[current.Emperors.Count - 1] : null;
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            int cities = (k != null && k.cities != null) ? k.cities.Count : 0;
            long treasury = TreasuryManager.Instance.GetReport(kingdomId).Treasury;

            if (lastEmperor != null)
            {
                CloseEmperor(current, lastEmperor, kingdomId, k, year, lostThrone: false, oldKingId);
            }

            // LlmFix-6：即位年强制采样（首点必采）+ 极盛年初始化
            var newRec = new EmperorRecord
            {
                Name = emperorName,
                Era = era,
                StartYear = year,
                EndYear = -1,
                StartCities = cities,
                StartTreasury = treasury,
                StartVassals = CountVassalsOf(kingdomId),
                StartMandate = MandateValueNow(kingdomId),
                StartStability = GetStabilityOf(kingdomId),
                StartPopulation = k != null && k.units != null ? k.units.Count : 0, // 阶段2O：人口起卷
                EndCities = cities,
                EndTreasury = treasury
            };
            EmperorTrackSampler.AddSample(newRec.Track, new EmperorSample
            {
                Year = year,
                Cities = cities,
                Population = newRec.StartPopulation,
                Treasury = treasury,
                Mandate = newRec.StartMandate,
                Stability = newRec.StartStability,
                Vassals = newRec.StartVassals,
                Rank = -1   // 即位年不排（采样年会补——省一次全图扫描）
            });
            RefreshPeakYear(newRec);
            current.Emperors.Add(newRec);
            SaveHistory();
            ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 换帝记录成功: {current.Dynasty}朝 {emperorName} {year}年即位 (共{current.Emperors.Count}帝)");
        }

        /// <summary>
        /// 阶段2O：关闭皇帝记录（正常换帝与朝代终结共用）——
        /// 终局快照（城/库/附庸/天命/稳定/人口）+ 在位功业回溯（史册统计战争/施政——旧档亦有效）
        /// + 德行/结局位标记（禅让/被篡 via DynastyObserver；仁/暴/昏/奇才 + 寿数 via 旧君 actor）+ 谥法引擎 v2。
        /// </summary>
        /// <summary>阶段3H-10：破敌天命判定——扫天下王国史册的 KingdomFallen 条目（在位年段内），
        /// 亡者名单含本国 且 被灭国当时持有对方体系天命（东方扫长生天眷持有国，反之亦然）。
        /// 盖棺时一次性回溯，运行时零开销。</summary>
        private static void DetectSlewHolder(EmperorRecord e, string kingdomId, Kingdom k, int closeYear)
        {
            if (k == null || World.world?.kingdoms?.list == null) return;
            string myName = k.name ?? "";
            if (string.IsNullOrEmpty(myName)) return;
            var slain = new System.Collections.Generic.List<string>();
            foreach (Kingdom other in World.world.kingdoms.list)
            {
                // 只看仍存国（亡国史册在归档里，拿不到"当年天命持有"判据——存活国史册足够覆盖绝大多数场景）
                if (other == null || !other.isAlive()) continue;
                string oId = other.id.ToString();
                var chron = ChronicleManager.Instance.GetKingdomChronicle(oId);
                if (chron == null) continue;
                foreach (var c in chron)
                {
                    if (c.Type != ChronicleEventType.KingdomFallen) continue;
                    if (c.Year < e.StartYear || c.Year > closeYear) continue;
                    string detail = c.ContentText ?? "";
                    if (!detail.Contains(myName)) continue;
                    // 被灭国当时是否持有对方体系天命/天眷：从两端管理器取历史持有区间
                    if (WasOppositeHolderAt(c.Year, kingdomId))
                    {
                        string vn = string.IsNullOrEmpty(c.PrimaryPersonName) ? "敌国" : c.PrimaryPersonName;
                        if (!slain.Contains(vn)) slain.Add(vn);
                    }
                }
            }
            if (slain.Count > 0)
            {
                e.SlewHolder = 1;
                e.SlewHolderNames = string.Join("、", slain);
            }
        }

        /// <summary>本国是否东方体系（判定"对面"是哪条天命轨道）。</summary>
        private static bool WasOppositeHolderAt(int year, string myKingdomId)
        {
            // 被灭国已不在 kingdoms 列表——用两轨历史记录核对：该年被灭者在对方轨道的持有区间内，
            // 且不是本国自己（跨体系灭国只可能出自对面轨道的持有国）。
            // 东方轨道：检查 Orthodox 历史；游牧轨道：Nomadic 历史。
            try
            {
                // 本国是东方体系 → 对面=长生天眷轨道
                Kingdom me = KingdomDataExt.FindKingdomById(myKingdomId);
                bool meEastern = me == null || !KingdomCivilizationService.Instance.IsNomadic(me);
                var courts = NomadicMandateManager.Instance.GetCourts();
                var hist = OrthodoxMandateManager.Instance.GetHistory();
                if (meEastern)
                {
                    foreach (var ct in courts)
                        if (ct.StartYear <= year && (ct.EndYear == -1 || ct.EndYear >= year)) return true;
                }
                else
                {
                    foreach (var d in hist)
                        if (d.Dynasty != "空悬" && d.StartYear <= year && (d.EndYear == -1 || d.EndYear >= year)) return true;
                }
            }
            catch { }
            return false;
        }

        private void CloseEmperor(DynastyRecord dynasty, EmperorRecord e, string kingdomId, Kingdom k, int year,
            bool lostThrone, string oldKingId)
        {
            e.EndYear = year;
            if (k != null)
            {
                try
                {
                    e.EndCities = k.cities != null ? k.cities.Count : e.EndCities;
                    e.EndTreasury = TreasuryManager.Instance.GetReport(kingdomId).Treasury;
                    e.EndVassals = CountVassalsOf(kingdomId);
                    e.EndMandate = MandateValueNow(kingdomId);
                    e.EndStability = GetStabilityOf(kingdomId);
                    e.EndPopulation = k.units != null ? k.units.Count : 0;

                    // LlmFix-6：退位年强制采样（尾点必采）+ 极盛年定稿
                    EmperorTrackSampler.AddSample(e.Track, new EmperorSample
                    {
                        Year = year,
                        Cities = e.EndCities,
                        Population = e.EndPopulation,
                        Treasury = e.EndTreasury,
                        Mandate = e.EndMandate,
                        Stability = e.EndStability,
                        Vassals = e.EndVassals,
                        Rank = -1
                    });
                    RefreshPeakYear(e);
                }
                catch { }
            }

            // 在位功业回溯（王国史册按年段统计——零新增运行时采集，旧档未来死亡的皇帝即有）
            try
            {
                var chronicle = ChronicleManager.Instance.GetKingdomChronicle(kingdomId);
                int wars = 0, pols = 0;
                if (chronicle != null)
                {
                    foreach (var c in chronicle)
                    {
                        if (c.Year < e.StartYear || c.Year > e.EndYear) continue;
                        if (c.Type == ChronicleEventType.WarDeclared) wars++;
                        else if (c.Type == ChronicleEventType.PolicyEnacted) pols++;
                    }
                }
                e.WarCount = wars;
                e.PolicyCount = pols;
                // 阶段3H-10：破敌天命判定——在位年间灭掉对方体系的天命持有国
                // （自己史册里的 KingdomFallen 条目=自己被灭，不含他国；遍历天下史册的覆灭记录，
                //   亡者名单含本国名 且 该亡者国当年持有对方体系天命/天眷）
                try { DetectSlewHolder(e, kingdomId, k, year); }
                catch { }
            }
            catch { e.WarCount = -1; e.PolicyCount = -1; }

            // 结局位标记（禅让/被篡——DynastyObserver 最近换君事实）
            try
            {
                var fact = DynastyObserver.Instance.ConsumeLastFact(kingdomId);
                if (fact != null)
                {
                    if (fact.IsAbdication) e.Deeds |= PosthumousNameGenerator.DEED_ABDICATION;
                    if (fact.IsUsurpation) e.Deeds |= PosthumousNameGenerator.DEED_USURPED;
                }
            }
            catch { }

            // 德行特质与寿数（退位/驾崩的旧君——actor 或已被回收，取不到则保持未采集）
            try
            {
                if (!string.IsNullOrEmpty(oldKingId) && long.TryParse(oldKingId, out long okId))
                {
                    var ok = World.world?.units?.get(okId);
                    if (ok != null)
                    {
                        try { e.EndAge = ok.getAge(); } catch { }
                        if (ok.hasTrait(TraitManager.TRAIT_BENEVOLENT)) e.Deeds |= PosthumousNameGenerator.DEED_BENEVOLENT;
                        if (ok.hasTrait(TraitManager.TRAIT_TYRANT)) e.Deeds |= PosthumousNameGenerator.DEED_TYRANT;
                        if (ok.hasTrait(TraitManager.TRAIT_FOOLISH)) e.Deeds |= PosthumousNameGenerator.DEED_FOOLISH;
                        if (ok.hasTrait(TraitManager.TRAIT_GENIUS)) e.Deeds |= PosthumousNameGenerator.DEED_GENIUS;
                    }
                }
            }
            catch { }

            // 谥法引擎 v2（阶段2V：孝谥随机源注入——游戏侧 Unity 随机）
            var usedTemples = new System.Collections.Generic.HashSet<string>();
            var usedShis = new System.Collections.Generic.HashSet<string>();
            foreach (var pe in dynasty.Emperors)
            {
                if (!string.IsNullOrEmpty(pe.TempleName)) usedTemples.Add(pe.TempleName);
                // 2026-08-18 谥号去重种子：已定谥皇帝（含本条前的记录；本条首写后加入）
                if (!string.IsNullOrEmpty(pe.PosthumousName) && !ReferenceEquals(pe, e))
                {
                    string key = pe.PosthumousName.StartsWith("孝") && pe.PosthumousName.Length > 1
                        ? pe.PosthumousName.Substring(1) : pe.PosthumousName;
                    usedShis.Add(key);
                }
            }
            bool isFirst = dynasty.Emperors.IndexOf(e) == 0;
            bool isRestoration = isFirst && dynasty.ReignIndex > 1;
            // P1-A3（2026-08-23）：孝谥随机源改确定性哈希骰——以"朝代|帝名|年号|即位年"为种子、
            // 驾崩年为年份轴：同一皇帝恒同谥（读档重开不再翻变），且不消耗 Unity 全局随机序列。
            string shiSeedKey = (dynasty != null ? dynasty.Dynasty : "") + "|" + e.Name + "|" + e.Era + "|" + e.StartYear;
            // LlmFix-4 L6：前任谥号/庙号带给 AI 评语作上下文（与手动重评路径对齐）
            int eIdx = dynasty.Emperors.IndexOf(e);
            var prevRec = eIdx > 0 ? dynasty.Emperors[eIdx - 1] : null;
            PosthumousNameGenerator.Generate(e, isFirst, lostThrone, usedTemples, isRestoration, eIdx,
                () => DeterministicRandom.Roll01Text(shiSeedKey, e.EndYear, "shi_xiao"), usedShis, dynasty != null ? dynasty.Dynasty : "",
                prevRec != null ? prevRec.PosthumousName : "",
                prevRec != null ? prevRec.TempleName : "");

            // 阶段3：个人史册盖棺条目（君临类——谥号庙号定论入册；同年同类同文去重保护）
            try
            {
                if (!string.IsNullOrEmpty(oldKingId) && long.TryParse(oldKingId, out long deadEmpId)
                    && !string.IsNullOrEmpty(e.PosthumousName))
                {
                    string miao = string.IsNullOrEmpty(e.TempleName) ? "" : $"，庙号{e.TempleName}";
                    PersonHistoryManager.Instance.RecordEvent(deadEmpId, PersonHistoryManager.CAT_JUNLIN,
                        $"帝崩，谥「{e.PosthumousName}」{miao}", e.EndYear > 0 ? e.EndYear : year);
                }
            }
            catch { }
        }

        /// <summary>确保天命历史已加载（v2 快照模式：历史由读档快照/新世界初始化填充，无需自动恢复）</summary>
        private void EnsureHistoryLoaded()
        {
            // v2 快照模式：历史由 ZhuluSaveCore 恢复或新世界清空，运行中不再自动加载
        }

        private void RecordDynastyEnd(string kingdomId, int year, string reason)
        {
            EnsureHistoryLoaded();

            // 按王国匹配当前朝代（防 FindLast(active) 关闭错朝代——曾致鲁朝永不关闭）
            var current = _history.FindLast(d => d.EndYear == -1 && d.Dynasty != "空悬" && (string.IsNullOrEmpty(kingdomId) || d.KingdomId == kingdomId));
            if (current == null)
            {
                // 兜底：找不到该王国记录时，关闭最后一条 active（历史错乱时至少不留下永远开放的朝代）
                current = _history.FindLast(d => d.EndYear == -1 && d.Dynasty != "空悬");
            }
            if (current == null) return;
            current.EndYear = year;
            current.EndReason = reason;

            // 关闭末代皇帝（阶段2O：统一走 CloseEmperor——失位 lostThrone=true）
            if (current.Emperors.Count > 0)
            {
                Kingdom kk = string.IsNullOrEmpty(kingdomId) ? null : KingdomDataExt.FindKingdomById(kingdomId);
                var last = current.Emperors[current.Emperors.Count - 1];
                CloseEmperor(current, last, kingdomId, kk, year, lostThrone: true, oldKingId: null);
            }
            SaveHistory();
        }

        private void RecordVacantStart(int year)
        {
            _history.Add(new DynastyRecord
            {
                Dynasty = "空悬",
                KingdomId = "",
                StartYear = year,
                EndYear = -1,
                EndReason = "vacant"
            });
            SaveHistory();
        }

        private void RecordVacantEnd(int year)
        {
            var vacant = _history.FindLast(d => d.Dynasty == "空悬" && d.EndYear == -1);
            if (vacant != null)
            {
                vacant.EndYear = year;
                SaveHistory();
            }
        }

        // ═══════════════ 持久化（简化格式存于天命国 custom_data） ═══════════════

        private void SaveHistory()
        {
            // v2 快照模式：运行时无需落盘（历史在 _history 内存，保存时由 ZhuluSaveCore 统一收集进快照）
        }

        private string SerializeHistory()
        {
            var parts = new List<string>();
            foreach (var d in _history)
            {
                var emperors = new List<string>();
                foreach (var e in d.Emperors)
                {
                    // 阶段2M-29：序列化扩展（附庸/天命/稳定快照 + 谥号庙号）
                    // 阶段2O：再扩展 6 段（人口起止/战争数/施政数/位标记/驾崩年龄）——16→22 段
                    // 阶段2V：再扩展 1 段（大一统标记）——22→23 段
                    // LlmV2-0：再扩展 2 段（极盛年 + 轨迹紧凑串）——23→25 段。
                    // 此前采样只写内存、存档即丢——「极盛 N 年」读档后消失的根因
                    emperors.Add($"{e.Name},{e.Era},{e.StartYear},{e.EndYear},{e.StartCities},{e.StartTreasury},{e.EndCities},{e.EndTreasury},{e.StartVassals},{e.EndVassals},{e.StartMandate:F1},{e.EndMandate:F1},{e.StartStability:F1},{e.EndStability:F1},{e.PosthumousName},{e.TempleName},{e.StartPopulation},{e.EndPopulation},{e.WarCount},{e.PolicyCount},{e.Deeds},{e.EndAge},{e.Unified},{e.PeakYear},{SerializeTrack(e.Track)}");
                }
                parts.Add($"{d.Dynasty}|{d.KingdomId}|{d.StartYear}|{d.EndYear}|{d.EndReason}|{d.ReignIndex}|{string.Join(";", emperors)}");
            }
            return string.Join("||", parts);
        }

        /// <summary>
        /// 轨迹紧凑串（LlmV2-0）：样本间用 /（不能用 ;——皇帝行分隔符已占用，不能用 ,——字段分隔符已占用），
        /// 样本内字段用 :（year:cities:pop:treasury:mandate:stab:vassals:rank）。纯函数，可单测。
        /// </summary>
        public static string SerializeTrack(IList<EmperorSample> track)
        {
            if (track == null || track.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < track.Count; i++)
            {
                if (i > 0) sb.Append('/');
                var s = track[i];
                sb.Append(s.Year).Append(':').Append(s.Cities).Append(':').Append(s.Population).Append(':')
                  .Append(s.Treasury).Append(':').Append(s.Mandate.ToString("F1")).Append(':')
                  .Append(s.Stability.ToString("F1")).Append(':').Append(s.Vassals).Append(':').Append(s.Rank);
            }
            return sb.ToString();
        }

        /// <summary>轨迹紧凑串 → 样本列表（脏段防御：字段不足 8 段跳过；空串返回空列表）。</summary>
        public static List<EmperorSample> ParseTrack(string raw)
        {
            var list = new List<EmperorSample>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var chunk in raw.Split('/'))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                var f = chunk.Split(':');
                if (f.Length < 8) continue;
                list.Add(new EmperorSample
                {
                    Year = int.TryParse(f[0], out int y) ? y : 0,
                    Cities = int.TryParse(f[1], out int c) ? c : 0,
                    Population = int.TryParse(f[2], out int p) ? p : 0,
                    Treasury = long.TryParse(f[3], out long t) ? t : 0,
                    Mandate = float.TryParse(f[4], out float m) ? m : 0f,
                    Stability = float.TryParse(f[5], out float s) ? s : 0f,
                    Vassals = int.TryParse(f[6], out int v) ? v : 0,
                    Rank = int.TryParse(f[7], out int r) ? r : -1
                });
            }
            return list;
        }

                // ═══════════════ v2 快照存档接口（唯一持久化路径——不再依赖 custom_data 时序/档案库文件） ═══════════════

        /// <summary>保存前：收集天命数据进快照（历史 + 天命国数据）。</summary>
        private void LegacyCollectToSnapshot(Persistence.ZhuluSnapshot snap)
        {
            snap.MandateHistory = SerializeHistory();
            snap.MandateData.Clear();
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                long has = KingdomDataExt.GetLong(k, KEY_HAS_MANDATE, 0);
                if (has != 1) continue;
                string kId = k.id.ToString();
                snap.MandateData[kId] = string.Join("|",
                    has.ToString(),
                    KingdomDataExt.GetFloat(k, KEY_MANDATE_VALUE, 0f).ToString("F2"),
                    KingdomDataExt.GetLong(k, KEY_BELOW6_SINCE, 0).ToString(),
                    KingdomDataExt.GetStr(k, KEY_DYNASTY_NAME, ""),
                    KingdomDataExt.GetStr(k, KEY_ERA_NAME, ""),
                    KingdomDataExt.GetStr(k, KEY_EMPEROR_NAME, ""),
                    KingdomDataExt.GetFloat(k, KEY_TREND_0, -1f).ToString("F2"),
                    KingdomDataExt.GetFloat(k, KEY_TREND_1, -1f).ToString("F2"),
                    KingdomDataExt.GetFloat(k, KEY_TREND_2, -1f).ToString("F2"));
            }
        }
        private void LegacyRestoreFromSnapshot(Persistence.ZhuluSnapshot snap)
        {
            // 阶段3H-15：读档重置年度结算标记（同会话先玩后读档，_lastMandateCalcYear 可能落在未来年份）
            _lastMandateCalcYear = -1;
            _lastTransferCheckYear = -1;
            try
            {
                _history.Clear();
                if (!string.IsNullOrEmpty(snap.MandateHistory))
                {
                    _history.AddRange(ParseHistoryString(snap.MandateHistory));
                }
                HealResidualActive();

                foreach (var kv in snap.MandateData)
                {
                    Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                    if (k == null || !k.isAlive()) continue;
                    var f = kv.Value.Split('|');
                    if (f.Length < 3) continue;
                    KingdomDataExt.SetLong(k, KEY_HAS_MANDATE, long.TryParse(f[0], out long hm) ? hm : 0);
                    KingdomDataExt.SetFloat(k, KEY_MANDATE_VALUE, float.TryParse(f[1], out float mv) ? mv : 0f);
                    KingdomDataExt.SetLong(k, KEY_BELOW6_SINCE, long.TryParse(f[2], out long b6) ? b6 : 0);
                    if (f.Length >= 4) KingdomDataExt.SetStr(k, KEY_DYNASTY_NAME, f[3]);
                    if (f.Length >= 5) KingdomDataExt.SetStr(k, KEY_ERA_NAME, f[4]);
                    if (f.Length >= 6) KingdomDataExt.SetStr(k, KEY_EMPEROR_NAME, f[5]);
                    if (f.Length >= 7) KingdomDataExt.SetFloat(k, KEY_TREND_0, float.TryParse(f[6], out float t0) ? t0 : -1f);
                    if (f.Length >= 8) KingdomDataExt.SetFloat(k, KEY_TREND_1, float.TryParse(f[7], out float t1) ? t1 : -1f);
                    if (f.Length >= 9) KingdomDataExt.SetFloat(k, KEY_TREND_2, float.TryParse(f[8], out float t2) ? t2 : -1f);
                }
                // 2026-08-20 兜底：历史 active 朝代若持有者 has_mandate 未恢复（读档漏写），补写——防正统旁落
                var staleActive = _history.FindLast(d => d.EndYear == -1 && d.Dynasty != "空悬");
                if (staleActive != null)
                {
                    Kingdom hk = KingdomDataExt.FindKingdomById(staleActive.KingdomId);
                    if (hk != null && hk.isAlive() && KingdomDataExt.GetLong(hk, KEY_HAS_MANDATE, 0) != 1)
                    {
                        KingdomDataExt.SetLong(hk, KEY_HAS_MANDATE, 1);
                        ZhuluContext.Instance.LogWarning($"[OrthodoxMandate] 读档恢复后补写天命持有: {staleActive.Dynasty}");
                    }
                }
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 快照恢复天命: {_history.Count} 条朝代记录");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OrthodoxMandate] 快照恢复异常", ex);
            }
        }

        // ---- batch10-A1: schema v5 section ----
        public sealed class OrthodoxState
        {
            public string History;   // scroll protocol kept verbatim (B5 will revisit)
            public Dictionary<string, string> Data = new Dictionary<string, string>();
        }
        public string SectionName => "orthodox_mandate";
        public Dictionary<string, OrthodoxState> CaptureStates()
        {
            var v4 = new Persistence.ZhuluSnapshot();
            LegacyCollectToSnapshot(v4);   // delegate to proven path (incl. save-time semantics)
            return new Dictionary<string, OrthodoxState>
            {
                ["__global__"] = new OrthodoxState { History = v4.MandateHistory, Data = v4.MandateData }
            };
        }
        public void ApplyStates(Dictionary<string, OrthodoxState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            var v4 = new Persistence.ZhuluSnapshot
            {
                MandateHistory = st.History ?? "",
                MandateData = st.Data ?? new Dictionary<string, string>()
            };
            LegacyRestoreFromSnapshot(v4);   // delegate: heal/stale-fallback/log all preserved
        }


        /// <summary>
        /// 读档后：从快照同步恢复（历史反序列化 + 纯数据修正 + 天命数据写回 custom_data）。
        /// 写 custom_data 是即时内存操作（读档 Postfix 时 World 已加载），运行中读取立即正确——时序问题根除。
        /// </summary>

        /// <summary>旧档迁移（快照机制前的老存档）：从天命国 custom_data 读历史进内存，随后保存时生成快照。</summary>
        public void MigrateFromCustomData()
        {
            try
            {
                _history.Clear();
                if (World.world?.kingdoms?.list == null) return;
                string raw = "";
                string scanHolder = ScanMandateHolderId();
                if (scanHolder != null)
                {
                    Kingdom holder = KingdomDataExt.FindKingdomById(scanHolder);
                    if (holder != null) raw = KingdomDataExt.GetStr(holder, KEY_HISTORY, "");
                }
                if (string.IsNullOrEmpty(raw))
                {
                    string bestRaw = ""; int bestScore = -1;
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        if (k == null || !k.isAlive()) continue;
                        string h = KingdomDataExt.GetStr(k, KEY_HISTORY, "");
                        if (string.IsNullOrEmpty(h)) continue;
                        int score = 0;
                        foreach (var chunk in h.Split(new[] { "||" }, StringSplitOptions.None))
                        {
                            if (string.IsNullOrEmpty(chunk)) continue;
                            var fc = chunk.Split('|');
                            if (fc.Length >= 4)
                            {
                                if (fc[3] == "-1") score += 10000;
                                if (int.TryParse(fc[2], out int sy)) score += sy;
                            }
                        }
                        if (score > bestScore) { bestScore = score; bestRaw = h; }
                    }
                    raw = bestRaw;
                }
                if (!string.IsNullOrEmpty(raw))
                {
                    _history.AddRange(ParseHistoryString(raw));
                }
                HealResidualActive();
                ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 旧档迁移完成: {_history.Count} 条朝代记录");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[OrthodoxMandate] 旧档迁移异常", ex);
            }
        }

        /// <summary>新世界/串档拒绝：清空天命内存与全部天命标记。</summary>
        public void ResetRuntime()
        {
            _history.Clear();
            // 阶段3H-15 根修：结算/转移年份标记必须归位——读更早年份的档后
            // year - _lastMandateCalcYear 恒负，天命值结算永不触发（恒60、趋势死寂的根因）
            _lastMandateCalcYear = -1;
            _lastTransferCheckYear = -1;
            try
            {
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        if (k == null || !k.isAlive()) continue;
                        KingdomDataExt.SetLong(k, KEY_HAS_MANDATE, 0);
                        KingdomDataExt.SetLong(k, KEY_BELOW6_SINCE, 0);
                        KingdomDataExt.SetFloat(k, KEY_MANDATE_VALUE, 0f);
                    }
                }
            }
            catch { }
            ZhuluContext.Instance.LogInfo("[OrthodoxMandate] 天命已重置（新世界/串档拒绝）");
        }

        /// <summary>纯数据修正：多条 active 保留历史顺序最后一条（=当前天命国），残留用下一条非空悬开始年关闭；未封口空悬封口。
        /// 2026-08-18 幽灵记录清理（用户实锤：存档中存在「未知朝/历0帝/天命80~278/-1」卡片——
        /// 旧版字段错位产生的存量脏数据：KingdomId 存了朝代名而非数字王国ID、EndReason="-1"、无任何皇帝）。</summary>
        private void HealResidualActive()
        {
            bool changed = false;

            // ① 幽灵记录清理：KingdomId 非数字（字段错位铁证）或 无皇帝的已关闭非空悬记录（幽灵朝代）
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var d = _history[i];
                bool badId = !long.TryParse(d.KingdomId, out _);
                bool ghost = d.Dynasty != "空悬" && d.EndYear > 0 && d.Emperors.Count == 0;
                if (badId || ghost)
                {
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 幽灵记录清理: 「{d.Dynasty}朝」(国={d.KingdomId}, {d.StartYear}~{d.EndYear}, 帝{d.Emperors.Count})");
                    _history.RemoveAt(i);
                    changed = true;
                }
            }

            var actives = new List<DynastyRecord>();
            foreach (var d in _history)
                if (d.EndYear == -1 && d.Dynasty != "空悬") actives.Add(d);
            if (actives.Count > 1)
            {
                var keep = actives[actives.Count - 1];
                foreach (var d in actives)
                {
                    if (d == keep) continue;
                    int idx = _history.IndexOf(d);
                    int closeYear = 0;
                    for (int j = idx + 1; j < _history.Count; j++)
                    {
                        if (_history[j].Dynasty != "空悬" && _history[j].StartYear > 0) { closeYear = _history[j].StartYear; break; }
                    }
                    if (closeYear <= 0) continue;
                    d.EndYear = closeYear;
                    d.EndReason = "天命转移";
                    changed = true;
                    ZhuluContext.Instance.LogInfo($"[OrthodoxMandate] 数据修正：{d.Dynasty}朝 残留 active → 关闭于 {closeYear}年");
                }
            }
            for (int i = 0; i < _history.Count; i++)
            {
                var d = _history[i];
                if (d.Dynasty == "空悬" && d.EndYear == -1)
                {
                    for (int j = i + 1; j < _history.Count; j++)
                    {
                        if (_history[j].Dynasty != "空悬" && _history[j].EndYear == -1 && _history[j].StartYear > 0)
                        {
                            d.EndYear = _history[j].StartYear;
                            changed = true;
                            break;
                        }
                    }
                }
            }
            // ④ 2026-08-19 期号自愈：篡位改朝不计期（天命未断），修复旧档篡位新朝被误判为"二期国"
            var reignCounters = new Dictionary<string, int>();
            foreach (var d in _history)
            {
                if (string.IsNullOrEmpty(d.KingdomId)) continue;
                reignCounters.TryGetValue(d.KingdomId, out int prior);
                int correct = prior + 1;
                if (d.ReignIndex != correct)
                {
                    d.ReignIndex = correct;
                    changed = true;
                }
                if (d.EndYear != -1 && d.EndReason != "篡位失国") reignCounters[d.KingdomId] = prior + 1;
            }

            if (changed) ZhuluContext.Instance.LogInfo("[OrthodoxMandate] 数据修正完成（幽灵记录/残留 active/空悬/期号已处理）");
        }

        /// <summary>解析朝代历史串 → 记录列表（脏数据防御：空名→未知/空悬补回、结束年倒挂跳过）。</summary>
        private static List<DynastyRecord> ParseHistoryString(string raw)
        {
            var result = new List<DynastyRecord>();
            if (string.IsNullOrEmpty(raw)) return result;
            foreach (var dynastyChunk in raw.Split(new[] { "||" }, StringSplitOptions.None))
            {
                if (string.IsNullOrEmpty(dynastyChunk)) continue;
                var fields = dynastyChunk.Split('|');
                if (fields.Length < 5) continue;
                string dynName = string.IsNullOrEmpty(fields[0])
                    ? (fields[4] == "vacant" ? "空悬" : "未知")
                    : fields[0];
                int startYear = int.TryParse(fields[2], out int sy) ? sy : 0;
                int endYear = int.TryParse(fields[3], out int ey) ? ey : -1;
                if (endYear != -1 && endYear < startYear) continue;
                var record = new DynastyRecord
                {
                    Dynasty = dynName,
                    KingdomId = fields[1],
                    StartYear = startYear,
                    EndYear = endYear,
                    EndReason = fields[4],
                ReignIndex = fields.Length > 6 && int.TryParse(fields[5], out int rIdx) ? rIdx : 1
                };
                // 阶段2M-29：新格式 ReignIndex 在 fields[5]（emperors 移 fields[6]）；旧格式 emperors 在 fields[5]
                int empIdx = (fields.Length > 6 && !string.IsNullOrEmpty(fields[6])) ? 6 : 5;
                if (fields.Length > empIdx && !string.IsNullOrEmpty(fields[empIdx]))
                {
                    foreach (var empChunk in fields[empIdx].Split(';'))
                    {
                        if (string.IsNullOrEmpty(empChunk)) continue;
                        var ef = empChunk.Split(',');
                        if (ef.Length < 8) continue;
                        record.Emperors.Add(new EmperorRecord
                        {
                            Name = ef[0],
                            Era = ef[1],
                            StartYear = int.TryParse(ef[2], out int esy) ? esy : 0,
                            EndYear = int.TryParse(ef[3], out int eey) ? eey : -1,
                            StartCities = int.TryParse(ef[4], out int esc) ? esc : 0,
                            StartTreasury = long.TryParse(ef[5], out long est) ? est : 0,
                            EndCities = int.TryParse(ef[6], out int eec) ? eec : 0,
                            EndTreasury = long.TryParse(ef[7], out long eet) ? eet : 0,
                            // 阶段2M-29：新字段（旧档默认 0——兼容）
                            StartVassals = ef.Length > 8 && int.TryParse(ef[8], out int esv) ? esv : 0,
                            EndVassals = ef.Length > 9 && int.TryParse(ef[9], out int eev) ? eev : 0,
                            StartMandate = ef.Length > 10 && float.TryParse(ef[10], out float esm) ? esm : 0f,
                            EndMandate = ef.Length > 11 && float.TryParse(ef[11], out float eem) ? eem : 0f,
                            StartStability = ef.Length > 12 && float.TryParse(ef[12], out float ess) ? ess : 0f,
                            EndStability = ef.Length > 13 && float.TryParse(ef[13], out float ees) ? ees : 0f,
                            PosthumousName = ef.Length > 14 ? ef[14] : "",
                            TempleName = ef.Length > 15 ? ef[15] : "",
                            // 阶段2O：新字段（旧 16 段档案默认——未采集，UI 自动省略）
                            StartPopulation = ef.Length > 16 && int.TryParse(ef[16], out int espop) ? espop : 0,
                            EndPopulation = ef.Length > 17 && int.TryParse(ef[17], out int eepop) ? eepop : 0,
                            WarCount = ef.Length > 18 && int.TryParse(ef[18], out int ewc) ? ewc : -1,
                            PolicyCount = ef.Length > 19 && int.TryParse(ef[19], out int epc) ? epc : -1,
                            Deeds = ef.Length > 20 && int.TryParse(ef[20], out int edeeds) ? edeeds : 0,
                            EndAge = ef.Length > 21 && int.TryParse(ef[21], out int eage) ? eage : 0,
                            // 阶段2V：大一统标记（旧 22 段默认 0——未达成）
                            Unified = ef.Length > 22 && int.TryParse(ef[22], out int euni) ? euni : 0,
                            // LlmV2-0：极盛年 + 轨迹（旧 23 段默认无——UI 兜底首尾两点）
                            PeakYear = ef.Length > 23 && int.TryParse(ef[23], out int epk) ? epk : 0,
                            Track = ParseTrack(ef.Length > 24 ? ef[24] : "")
                        });
                    }
                }
                result.Add(record);
            }
            return result;
        }

        /// <summary>将当前历史写入档案库（v2 已废弃——由 ZhuluSaveCore 快照取代；保留空壳兼容旧调用）。</summary>
        public void FlushToArchive()
        {
        }

        /// <summary>从档案库恢复历史（v2 已废弃——由快照 RestoreFromSnapshot 取代；保留空壳兼容旧调用）。</summary>
        public void RehydrateFromArchive()
        {
        }

        /// <summary>重置天命持有者缓存（v2 无缓存，保留兼容调用）。</summary>
        public void ResetCache()
        {
        }
    }

    // ═══════════════ 数据类 ═══════════════

    public class DynastyRecord
    {
        public string Dynasty { get; set; } = "";
        public int ReignIndex { get; set; } = 1; // 阶段2M-29：期号（同国多次受命——一期/二期…）
        public string KingdomId { get; set; } = "";
        public int StartYear { get; set; }
        public int EndYear { get; set; } = -1; // -1 = 当前
        public string EndReason { get; set; } = "active"; // active/亡国失命/衰微失命/vacant
        public List<EmperorRecord> Emperors { get; set; } = new List<EmperorRecord>();
    }

    public class EmperorRecord
    {
        public string Name { get; set; } = "";
        public string Era { get; set; } = "";
        public int StartYear { get; set; }
        public int EndYear { get; set; } = -1;
        public int StartCities { get; set; }
        public long StartTreasury { get; set; }
        public int EndCities { get; set; }
        public long EndTreasury { get; set; }
        // 阶段2M-29：即位/退位快照扩展（附庸/天命/稳定）
        public int StartVassals { get; set; }
        public int EndVassals { get; set; }
        public float StartMandate { get; set; }
        public float EndMandate { get; set; }
        public float StartStability { get; set; }
        public float EndStability { get; set; }
        // 阶段2M-29：谥号/庙号（退位/驾崩时生成）
        public string PosthumousName { get; set; } = "";  // 谥号（武/景/哀…）
        public string TempleName { get; set; } = "";      // 庙号（太祖/太宗/末帝…）
        // ── 阶段2O：功过评价系统扩展（序列化 16→22 段；旧档默认值=未采集，UI 自动省略）──
        public int StartPopulation { get; set; }    // 即位时人口（0=未采集——旧档）
        public int EndPopulation { get; set; }      // 退位时人口
        public int WarCount { get; set; } = -1;     // 在位攻伐次数（王国史册回溯；-1=未采集）
        public int PolicyCount { get; set; } = -1;  // 在位施政次数（王国史册回溯；-1=未采集）
        public int Deeds { get; set; }              // 位标记（PosthumousNameGenerator.DEED_*）
        public int EndAge { get; set; }             // 驾崩年龄（0=未采集）
        // 阶段2V：大一统标记（在位期间本国+附庸 城市或人口占天下≥90%——一次达成终身保留；0=未达成）
        public int Unified { get; set; }
        // 阶段3H-10：在位期间灭掉对方体系天命国（对方持有天命/天眷时被灭——盖棺回溯史册判定；1=达成）
        public int SlewHolder { get; set; }
        public string SlewHolderNames { get; set; } = "";   // 被灭天命国名（史评展示）

        // ── 2026-10-24 历代天命接入 第2步：在位轨迹 ──
        // 只看即位/退位两点会抹掉中间高光（29 年在位、18 年极盛、28 年崩坏者会被记成一直平庸），
        // 故在位期间按间隔采样。旧档无此字段 → 空列表，调用方用 Start*/End* 兜底。
        public System.Collections.Generic.List<EmperorSample> Track { get; set; }
            = new System.Collections.Generic.List<EmperorSample>();
        /// <summary>极盛之年（即位第 N 年；0 = 未采集）。</summary>
        public int PeakYear { get; set; }
    }

    /// <summary>
    /// 阶段2O：谥法引擎 v2（替代 2M 简化版）。
    /// 设计：死亡判定 + 庙号分支 + 首写生效 + 多维评分定谥，
    /// 补上两者共同缺陷——"字义→条件"映射：每个谥字/庙号都有明确触发条件。
    /// 谥号优先级：恶谥（特质实锤）＞悯谥（结局）＞开国特例＞功绩字（最高功维）＞平谥。
    /// 庙号门槛制（2O-D3 定稿）：功业达标方立庙，平庸者不祔庙只称"X帝"；失国=末帝；复国期首=世祖。
    /// </summary>
    public static class PosthumousNameGenerator
    {
        // ── 德行/结局位标记（EmperorRecord.Deeds）──
        public const int DEED_BENEVOLENT = 1;     // 仁君
        public const int DEED_TYRANT = 2;         // 暴君
        public const int DEED_FOOLISH = 4;        // 昏君
        public const int DEED_GENIUS = 8;         // 天纵奇才
        public const int DEED_ABDICATION = 16;    // 禅让退位
        public const int DEED_USURPED = 32;       // 被篡位
        public const int DEED_RESTORATION = 64;   // 复国期首（同国再受命）

        /// <summary>皇帝退位/驾崩时生成谥号与庙号（2O v2——已死皇帝不重算，首写生效；阶段2V 扩展：大一统/孝谥/新谥字）。
        /// rand01：孝谥随机源注入（游戏传 UnityEngine.Random.value；测试不传=纯函数——net48 测试运行时 JIT 到 Unity ECall 会崩）。
        /// 2026-08-18 谥号去重（usedShi）：同朝已用谥字 → 同义谥字替代（悼→殇/冲/质、让→逊/禅——
        /// 史家惯例：同类谥各帝异字，殇帝/冲帝/质帝皆早夭之君）；同义字尽 → 两字谥兜底（"后X"）。</summary>
        public static void Generate(EmperorRecord e, bool isFirstOfReign, bool lostThrone,
            System.Collections.Generic.HashSet<string> usedTemples = null, bool isRestoration = false, int indexInDynasty = -1,
            Func<float> rand01 = null, System.Collections.Generic.HashSet<string> usedShi = null,
            string dynastyName = null, string prevShi = null, string prevTemple = null)
        {
            if (e == null) return;
            if (e.EndYear < 0) return; // 在位不生成
            if (isRestoration) e.Deeds |= DEED_RESTORATION;

            int cityDelta = e.EndCities - e.StartCities;
            long treasDelta = e.EndTreasury - e.StartTreasury;
            float mandDelta = e.EndMandate - e.StartMandate;
            float stabDelta = e.EndStability - e.StartStability;
            int reignYears = e.EndYear - e.StartYear;
            bool benevolent = (e.Deeds & DEED_BENEVOLENT) != 0;
            bool tyrant = (e.Deeds & DEED_TYRANT) != 0;
            bool foolish = (e.Deeds & DEED_FOOLISH) != 0;
            bool unified = e.Unified == 1; // 阶段2V：大一统
            bool abdicated = (e.Deeds & DEED_ABDICATION) != 0;   // 阶段3：禅让退位
            bool usurped = (e.Deeds & DEED_USURPED) != 0;        // 阶段3：被篡位

            // ── 禅让功绩分流（2026-08-18：高功禅让者按功绩定谥定庙——乾隆式禅位不失其功；
            // 平庸禅让者固定"让帝"不祔庙——原版"禅让一律让帝"抹杀功绩）──
            bool abdicatedHighMerit = abdicated && !lostThrone && !tyrant && !foolish
                && (cityDelta >= 3 || unified || mandDelta >= 8f
                    || (e.PolicyCount >= 4 && stabDelta >= 0f) || (e.PolicyCount >= 6));

            // ── 庙号（门槛制）──
            string temple = "";
            if (lostThrone) temple = "末帝";                                            // 失国无庙
            else if (abdicated && !abdicatedHighMerit) temple = "";                    // 平庸禅让：不祔庙（让帝）
            else if (unified && !isFirstOfReign) temple = "世祖";                        // 阶段2V：大一统混一天下（优先于复国）
            else if (isRestoration) temple = "世祖";                                     // 复国期首
            else if (isFirstOfReign) temple = cityDelta >= 2 ? "太祖" : "高祖";           // 开国
            else if (cityDelta >= 4) temple = "世宗";                                    // 武功之最
            else if (e.PolicyCount >= 4 && stabDelta >= 5f) temple = "高宗";             // 文治
            else if (benevolent) temple = "仁宗";                                        // 德治
            else if (mandDelta >= 10f) temple = "中宗";                                  // 中兴
            else if (indexInDynasty == 1 && cityDelta >= 0 && stabDelta >= -3f && !tyrant && !foolish) temple = "太宗"; // 二世守成
            // 去重（宗字辈不重复；首末除外——2026-08-19 修复："世祖"两个来源（复国首帝+大一统非首帝）
            // 可跨皇帝撞车，纳入去重；太祖/高祖仅首帝可得天然唯一）
            if (!string.IsNullOrEmpty(temple) && usedTemples != null && usedTemples.Contains(temple)
                && temple != "末帝" && temple != "太祖" && temple != "高祖")
            {
                string[] fb = { "太宗", "宣宗", "神宗", "哲宗", "英宗", "成宗", "孝宗", "圣宗", "睿宗", "穆宗", "代宗", "宪宗" };
                temple = "";
                foreach (var f in fb) { if (!usedTemples.Contains(f)) { temple = f; break; } }
            }
            if (!string.IsNullOrEmpty(temple) && usedTemples != null) usedTemples.Add(temple);
            e.TempleName = temple;

            // ── 谥号（优先级：恶谥＞悯谥＞结局特谥（禅/篡）＞开国/大一统特例＞功绩字＞平谥）──
            int popPct = PopDeltaPercent(e);
            bool mandateCollected = e.StartMandate > 0f || e.EndMandate > 0f; // 未采集豁免（阶段2S-14——旧档 0 值不得判"天命坠"）
            string shi;
            if (tyrant) shi = "厉";                                                      // 恶谥：暴
            else if (foolish) shi = "灵";                                                // 恶谥：昏
            else if (lostThrone) shi = (mandateCollected && e.EndMandate < 10f) ? "愍" : "哀"; // 悯：失国
            else if (usurped) shi = "幽";                                                // 悯谥·结局特谥：被篡（壅遏不通曰幽）
            else if (abdicated && !abdicatedHighMerit) shi = "让";                        // 平庸禅让：让帝（推功尚善曰让）
            // 高功禅让者继续走功绩谥（武/文/宣…——禅让事实由评语行【禅让】徽章体现）
            else if (cityDelta <= -4 && treasDelta <= -800) shi = "炀";                   // 恶谥：失德失土
            else if (mandateCollected && e.EndMandate < 30f) shi = "愍";                  // 悯：天命坠
            // 2026-08-19 "悼"修正：本义"年中早夭"——只给真早夭者（非开国 + 驾崩<50 岁）。
            // 开国皇帝受命时年高、在位虽短但功绩高者（用户实测场景）不再误判"悼"，改走功绩链。
            // 二次收紧：EndAge 未采集（旧档=0）不再臆断早夭——高寿短祚者落功绩链。
            else if (reignYears < 5 && !isFirstOfReign && e.EndAge > 0 && e.EndAge < 50) shi = "悼"; // 悯：早夭（短在位且壮年崩）
            else if (cityDelta < 0) shi = "怀";                                          // 悯：失土 1~3
            else if (unified) shi = cityDelta >= 3 ? "武"                                 // 阶段2V：大一统特谥（拓土归一曰武）
                : (e.PolicyCount >= 4 ? "文" : "圣");                                     // 文治归一曰文 / 混一海内曰圣
            else if (isFirstOfReign) shi = cityDelta >= 2 ? "武"                          // 开国特例（谥法字表）
                : ((e.PolicyCount >= 4 && stabDelta > 0f) ? "文" : "元");
            else if (cityDelta >= 3) shi = "武";                                          // 功绩：拓土
            else if (cityDelta == 2) shi = "桓";                                          // 功绩：拓土次
            else if (mandDelta >= 8f && stabDelta >= 0f) shi = "宣";                      // 功绩：中兴
            else if (e.PolicyCount >= 6 && stabDelta >= 0f) shi = "章";                   // 功绩：制度
            else if (e.PolicyCount >= 4 && stabDelta > 0f) shi = "文";                    // 功绩：文治
            else if (e.EndVassals - e.StartVassals >= 2) shi = "襄";                      // 功绩：辟地绥远（2V 新谥字）
            else if (stabDelta >= 12f) shi = "庄";                                        // 功绩：胜敌志强（2V 新谥字）
            else if (e.EndVassals - e.StartVassals >= 1) shi = "成";                      // 功绩：四夷
            else if (e.EndStability >= 80f && stabDelta >= 5f) shi = "肃";                // 功绩：朝纲肃穆（2V 新谥字）
            else if (e.EndStability >= 70f && stabDelta >= 0f) shi = "康";                // 功绩：国泰
            else if (stabDelta >= 3f) shi = "景";                                         // 功绩：治平
            else if (benevolent) shi = "懿";                                              // 功绩：温柔贤善（2V 新谥字）
            else if (cityDelta == 0 && popPct >= 20) shi = "昭";                          // 功绩：民丰
            else if (popPct >= 20) shi = "惠";                                            // 平谥：民增无功
            else if (popPct >= 10) shi = "安";                                            // 平谥：安民（2V 新谥字）
            else if (IsAllFlat(e)) shi = "平";                                            // 平谥：无功无过
            else shi = "穆";                                                              // 平谥：填充

            // ── 2026-10-24 第3步：条件池内取字 ──
            // 旧链每条件只出一个字（26 字），同朝五代平庸就撞车（后平帝/前平帝）。
            // 这里不推翻旧链：先用链判定"属于哪种情形"，再在**该情形的有序字池**内按确定性哈希取字。
            // 语义不跑偏（拓土仍从武/桓/襄/烈/威/庄里选，不会给「惠」），但同朝可分散。
            //
            // 触发条件：仅当传入 usedShi（有朝代去重上下文）时启用。
            // 无该上下文（单测/独立调用）沿用旧链原字——这样"大一统无殊功=圣"等既有契约不受影响，
            // 而真实游戏中总是带 usedShis，变化照常生效。
            if (usedShi != null)
            {
                try
                {
                    var facts = BuildFactsForPool(e, isFirstOfReign, lostThrone, isRestoration);
                    var cond = ShiCandidateProvider.ConditionOf(facts);
                    int pickSeed = StableHash32(e.Name + "|" + e.Era + "|" + e.StartYear + "|" + e.EndYear);
                    string pooled = ShiCandidateProvider.PickFromCondition(cond, usedShi, pickSeed);
                    if (!string.IsNullOrEmpty(pooled)) shi = pooled;
                }
                catch { /* 取字失败一律保留旧链结果，绝不影响盖棺 */ }
            }
            // ── 2026-08-18 谥号去重：同朝已用 → 同义谥字（按在位状态同类取字）──
            // 2026-08-19 修复"三个后悼帝"：兜底谥（后X）本身也要查重——
            // 原实现 alt=null 时无条件 "后"+shi，第二个人再撞车还是"后悼"。
            // 新链：同义字 → 前缀池（后X/前X/出X——历史成例：前/后废帝、前/后少帝）→ 全尽保留原字。
            if (usedShi != null && shi.Length == 1 && usedShi.Contains(shi))
            {
                string alt = null;
                if (ShiSynonyms.TryGetValue(shi, out var pool))
                {
                    foreach (var s in pool) { if (!usedShi.Contains(s)) { alt = s; break; } }
                }
                if (alt == null)
                {
                    foreach (var p in PrefixShiFallback)
                    {
                        if (!usedShi.Contains(p + shi)) { alt = p + shi; break; }
                    }
                }
                if (alt != null) shi = alt;
                // 池全尽（同谥 7+ 帝）→ 保留原字（史实同谥本身存在，由评语年代区分）
            }
            // ── "孝"前缀（汉制：非恶谥/悯谥/开国/大一统，仁德或治平之君 25% 加"孝"——如 孝文/孝武）──
            // 2026-08-19 与"悼"新口径同步：高龄短在位/开国短在位不算悯（可加孝）
            bool isEvilOrPity = tyrant || foolish || lostThrone || usurped
                || (cityDelta <= -4 && treasDelta <= -800)
                || (mandateCollected && e.EndMandate < 30f)
                || (reignYears < 5 && !isFirstOfReign && e.EndAge < 50) || cityDelta < 0;
            if (!isEvilOrPity && !isFirstOfReign && !unified && shi.Length == 1)
            {
                bool xiao = benevolent || stabDelta >= 5f;
                if (xiao && rand01 != null && rand01() < 0.25f) shi = "孝" + shi;
            }
            // 2026-08-19 key 口径统一：入池键剥"孝"前缀（与种子采集一致——原直接 Add("孝X")，
            // 下代取"X"时 Contains("X") 不命中 → 孝武/武 并存撞车）
            if (usedShi != null && !string.IsNullOrEmpty(shi))
            {
                string finalKey = shi.StartsWith("孝") && shi.Length > 1 ? shi.Substring(1) : shi;
                usedShi.Add(finalKey);
            }
            e.PosthumousName = shi;

            // ── 2026-10-24 第6步：把盖棺结果交给 AI 评价队列 ──
            // 只在启用且自动模式时入队；异步执行，绝不阻塞盖棺流程。
            // LlmFix-4 L6：补传前任谥号/庙号——与手动重评路径给模型的资料对齐
            try
            {
                TryEnqueueAiEvaluation(e, isFirstOfReign, lostThrone, dynastyName, indexInDynasty,
                                       usedShi, usedTemples, prevShi, prevTemple);
            }
            catch { }
        }

        /// <summary>
        /// 盖棺路径的 AI 评价键。indexInDynasty 是 Emperors 列表 0 基下标，
        /// 而历代天命窗口（ZhuluMandateWindow）的 idx 从 1 数——必须 +1 对齐，
        /// 否则自动评语写进窗口永远读不到的键（LlmFix-1 H1）。
        /// </summary>
        public static string BuildAutoAiKey(string dynastyName, int index0Based, EmperorRecord e)
        {
            return Interop.LlmCommentStore.BuildKey(dynastyName, index0Based + 1,
                e.Name, e.StartYear, e.EndYear);
        }

        /// <summary>构造并投递一次 AI 评价请求（失败静默——盖棺流程绝不因此受影响）。</summary>
        private static void TryEnqueueAiEvaluation(EmperorRecord e, bool isFirstOfReign, bool lostThrone,
            string dynastyName, int indexInDynasty, System.Collections.Generic.HashSet<string> usedShi,
            System.Collections.Generic.HashSet<string> usedTemples, string prevShi, string prevTemple)
        {
            var svc = Interop.LlmEmperorService.Instance;
            if (!svc.Config.Enabled || !svc.Config.AutoEval) return;

            var facts = BuildFactsForPool(e, isFirstOfReign, lostThrone, false);
            var set = ShiCandidateProvider.Build(facts, usedShi, usedTemples);

            string key = BuildAutoAiKey(dynastyName, indexInDynasty, e);
            string payload = Interop.LlmPromptBuilder.BuildUserPayload(
                facts, dynastyName, e.Name, e.Era, indexInDynasty, prevShi ?? "", prevTemple ?? "",
                set.Shi, set.Temple, usedShi != null ? new List<string>(usedShi) : null, e.Track,
                Interop.LlmPromptBuilder.PickStyleOf(key, set.Base));   // LlmV2-12：笔法随帝轮换+谥类门控

            svc.Enqueue(new Interop.LlmEmperorRequest
            {
                Key = key,
                SystemPrompt = Interop.LlmPromptBuilder.SystemPromptFor(svc.Config.MaxChars),
                UserPayload = payload,
                ShiCandidates = set.Shi,
                TempleCandidates = set.Temple,
                EndYear = e.EndYear,
                Manual = false
            });
        }

        /// <summary>2026-10-24 第3步：把帝王记录转成候选集判定所需的纯数据。</summary>
        private static EmperorFacts BuildFactsForPool(
            EmperorRecord e, bool isFirstOfReign, bool lostThrone, bool isRestoration)
        {
            var f = new EmperorFacts
            {
                CityDelta = e.EndCities - e.StartCities,
                TreasDelta = e.EndTreasury - e.StartTreasury,
                MandDelta = e.EndMandate - e.StartMandate,
                StabDelta = e.EndStability - e.StartStability,
                ReignYears = e.EndYear - e.StartYear,
                PolicyCount = e.PolicyCount,
                WarCount = e.WarCount,
                VassalDelta = e.EndVassals - e.StartVassals,
                EndAge = e.EndAge,
                EndMandate = e.EndMandate,
                EndStability = e.EndStability,
                PopPct = PopDeltaPercent(e),

                IsFirstOfReign = isFirstOfReign,
                Unified = e.Unified == 1,
                Tyrant = (e.Deeds & DEED_TYRANT) != 0,
                Foolish = (e.Deeds & DEED_FOOLISH) != 0,
                Benevolent = (e.Deeds & DEED_BENEVOLENT) != 0,
                Abdicated = (e.Deeds & DEED_ABDICATION) != 0,
                Usurped = (e.Deeds & DEED_USURPED) != 0,
                LostThrone = lostThrone,
                MandateCollected = e.StartMandate > 0f || e.EndMandate > 0f,
                AllFlat = IsAllFlat(e)
            };

            // 轨迹形态（第2步产出；旧档无 Track 时用首尾两点兜底）
            if (e.Track != null && e.Track.Count >= 2)
                f.Shape = EmperorTrackSampler.Shape(e.Track);
            else
                f.Shape = EmperorTrackSampler.ShapeFromEndpoints(
                    EmperorSample.PowerScoreOf(e.StartCities, e.StartPopulation),
                    EmperorSample.PowerScoreOf(e.EndCities, e.EndPopulation));

            return f;
        }

        /// <summary>
        /// 32 位稳定哈希（FNV-1a）。不能用 string.GetHashCode——.NET Core 下每次进程启动都不同，
        /// 会导致同一皇帝在不同会话定出不同谥号，读档即翻变。
        /// LlmV2-11 起 public：LLM 长评笔法轮换复用（同帝恒定笔法）。
        /// </summary>
        public static int StableHash32(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }

        /// <summary>同义谥字池（2026-08-18 谥号去重——同类谥各帝异字，史家惯例）：
        /// 悼(早夭)→殇/冲/质；让(禅让)→逊/禅；愍/哀(遭忧)→闵；厉/灵/炀(恶)→戾/缪/荒；
        /// 武(辟土)→威/烈；文(文治)→昭/懿；平/穆(平谥)→靖/思。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string[]> ShiSynonyms =
            new System.Collections.Generic.Dictionary<string, string[]>
        {
            { "悼", new[] { "殇", "冲", "质", "哀" } },
            { "让", new[] { "逊", "禅" } },
            { "愍", new[] { "闵", "哀", "悼" } },
            { "哀", new[] { "闵", "怀", "悼" } },
            { "厉", new[] { "戾", "缪", "灵" } },
            { "灵", new[] { "荒", "戾", "厉" } },
            { "炀", new[] { "厉", "荒", "幽" } },
            { "幽", new[] { "厉", "灵", "炀" } },
            { "武", new[] { "威", "烈", "桓" } },
            { "文", new[] { "昭", "懿", "章" } },
            { "宣", new[] { "威", "烈", "章" } },
            { "平", new[] { "安", "靖", "惠" } },
            { "穆", new[] { "靖", "思", "康" } },
            { "元", new[] { "章", "成", "宣" } },
            { "康", new[] { "安", "平", "景" } },
            { "怀", new[] { "悼", "闵", "哀" } },
            { "惠", new[] { "安", "平", "钦" } },
            { "景", new[] { "成", "康", "庄" } },
        };

        /// <summary>兜底谥前缀池（2026-08-19"三个后悼帝"修复：同义字尽后按序取——
        /// 后X/前X/出X；历史成例：前/后废帝、前/后少帝、出帝）。</summary>
        private static readonly string[] PrefixShiFallback = { "后", "前", "出" };

        /// <summary>谥法释义（"XX曰X"古义——评语行显示，让玩家看懂为何得此谥）。阶段3 完善化。</summary>
        public static string GetShifaMeaning(string shi)
        {
            if (string.IsNullOrEmpty(shi)) return "";
            // 孝前缀/兜底前缀（后X/前X/出X）取末字释义
            string key = shi.StartsWith("孝") && shi.Length > 1 ? shi.Substring(1) : shi;
            if (key.Length > 1 && (key.StartsWith("后") || key.StartsWith("前") || key.StartsWith("出"))) key = key.Substring(1);
            switch (key)
            {
                // 上谥（美）
                case "武": return "刚强直理曰武，辟土服远曰武";
                case "文": return "经纬天地曰文，道德博闻曰文";
                case "圣": return "扬善赋简曰圣，敬宾厚礼曰圣";
                case "元": return "能思辩众曰元，行义说民曰元";
                case "桓": return "辟土服远曰桓，克敬勤民曰桓";
                case "宣": return "圣善周闻曰宣，重光丽日曰宣";
                case "章": return "温克令仪曰章，法度大明曰章";
                case "襄": return "辟地有德曰襄，甲胄有劳曰襄";
                case "庄": return "胜敌志强曰庄，死于原野曰庄";
                case "成": return "安民立政曰成";
                case "肃": return "刚德克就曰肃，执心决断曰肃";
                case "康": return "安乐抚民曰康，合民安乐曰康";
                case "景": return "布义行刚曰景，耆意大虑曰景";
                case "懿": return "温柔贤善曰懿";
                case "昭": return "容仪恭美曰昭，圣闻周达曰昭";
                case "让": return "推功尚善曰让，德性宽柔曰让";
                // 中谥（平）
                case "惠": return "柔质慈民曰惠";
                case "安": return "好和不争曰安";
                case "平": return "治而无眚曰平，布纲治纪曰平";
                case "穆": return "布德执义曰穆，中情见貌曰穆";
                // 下谥（恶/悯）
                case "厉": return "杀戮无辜曰厉，暴慢无亲曰厉";
                case "灵": return "不勤成名曰灵，死而志成曰灵";
                case "炀": return "好内远礼曰炀，去礼远众曰炀";
                case "愍": return "在国遭忧曰愍，祸乱方作曰愍";
                case "哀": return "恭仁短折曰哀，遭难已甚曰哀";
                case "悼": return "恐惧从处曰悼，年中早夭曰悼";
                case "怀": return "失位而死曰怀，慈仁短折曰怀";
                case "幽": return "壅遏不通曰幽，动祭乱常曰幽";
                // 2026-08-18 谥号去重同义字（同类谥各帝异字）
                case "殇": return "短折不成曰殇，未家短折曰殇";
                case "冲": return "幼少在位曰冲";
                case "质": return "名实不爽曰质";
                case "逊": return "逊位出宫曰逊";
                case "禅": return "让位达人曰禅";
                case "闵": return "在国遭忧曰闵，祸乱方作曰闵";
                case "戾": return "不悔前过曰戾";
                case "缪": return "名与实爽曰缪";
                case "荒": return "好乐怠政曰荒，昏乱纪度曰荒";
                case "威": return "猛以刚果曰威，强毅信正曰威";
                case "烈": return "有功安民曰烈，秉德遵业曰烈";
                case "靖": return "柔德安众曰靖，恭己鲜言曰靖";
                case "思": return "追悔前过曰思，不眚兆民曰思";
                case "钦": return "威仪德备曰钦，夙夜祗惧曰钦";
                default: return "";
            }
        }

        /// <summary>
        /// 盖棺定论（阶段2O 起，阶段2S-14 加权分制）：展示层纯函数——不落盘，旧档已死皇帝亦可由 5 维快照生成。
        /// 返回 (等第序 0-8, 等第名, 史评句)。
        /// </summary>
        public static (int rank, string label, string verdict) BuildVerdict(EmperorRecord e, bool lostThrone)
        {
            if (e == null || e.EndYear < 0) return (-1, "", "");
            CountMerits(e, out int merit, out int fault);
            int score = ScoreReign(e);

            string[] ranks = { "圣君", "明君", "贤主", "令主", "守成", "中主", "庸主", "暗主", "昏乱" };
            int rank; string label;
            // 2026-08-19 评价体系丰富化：等第之上按功业/德行/结局细分称号（同一等第不再千人一面）
            if (lostThrone) { rank = 8; label = FlavorLabel(e, 8, true); }
            else if (fault >= 3 || score <= -9) { rank = 8; label = FlavorLabel(e, 8, false); }
            else if (fault == 2 || score <= -6) { rank = 7; label = FlavorLabel(e, 7, false); }
            else if (fault == 1 || score <= -3) { rank = 6; label = FlavorLabel(e, 6, false); }
            else if (score >= 10) { rank = 0; label = FlavorLabel(e, 0, false); }
            else if (score >= 7) { rank = 1; label = FlavorLabel(e, 1, false); }
            else if (score >= 4) { rank = 2; label = FlavorLabel(e, 2, false); }
            else if (score >= 2) { rank = 3; label = FlavorLabel(e, 3, false); }
            else if (score >= 0) { rank = 4; label = FlavorLabel(e, 4, false); }
            else { rank = 5; label = FlavorLabel(e, 5, false); }

            int reignYears = e.EndYear - e.StartYear;
            string ageText = e.EndAge > 0 ? $"，享年{ToChinese(e.EndAge)}" : "";
            // 阶段3：史评丰富化——多维功过（各取前2）+ 短语池稳定变体 + 特殊结尾句，杜绝千篇一律
            var merits = MeritPhrases(e);
            var faults = FaultPhrases(e, lostThrone);
            if (e.SlewHolder == 1)
            {
                string slainTxt = string.IsNullOrEmpty(e.SlewHolderNames) ? "敌国" : e.SlewHolderNames;
                merits.Insert(0, $"亲灭{slainTxt}之天命，克成不世之功");
            }
            string meritText = merits.Count > 0 ? string.Join("，", merits.Take(2)) : "";
            string faultText = faults.Count > 0 ? string.Join("；", faults.Take(2)) : "";
            string closing = ClosingPhrase(e, rank, reignYears);

            string verdict;
            if (rank >= 6)
                verdict = $"在位{ToChinese(reignYears)}年{ageText}。{meritText}，然{faultText}。{closing}";
            else
                verdict = $"在位{ToChinese(reignYears)}年{ageText}。{meritText}；{faultText}。{closing}";
            return (rank, label, verdict);
        }

        /// <summary>等第细分称号（2026-08-19 评价体系丰富化）：功业/德行/结局 flavored。</summary>
        private static string FlavorLabel(EmperorRecord e, int rank, bool lostThrone)
        {
            bool tyrant = (e.Deeds & DEED_TYRANT) != 0;
            bool foolish = (e.Deeds & DEED_FOOLISH) != 0;
            bool benevolent = (e.Deeds & DEED_BENEVOLENT) != 0;
            int cityDelta = e.EndCities - e.StartCities;
            int reignYears = e.EndYear - e.StartYear;
            switch (rank)
            {
                case 0: // 圣君
                    if (e.Unified == 1) return "千古一帝";
                    if (cityDelta >= 5) return "开疆圣主";
                    if (benevolent) return "仁圣之君";
                    if (e.PolicyCount >= 6) return "垂宪圣主";
                    return "圣君";
                case 1: // 明君
                    if (cityDelta >= 3) return "鹰扬明君";
                    if (e.PolicyCount >= 6) return "文治明君";
                    if (reignYears >= 30) return "长治明君";
                    return "明君";
                case 2: // 贤主（2026-08-19 二次丰富：中等三等第接入细分）
                    if (cityDelta >= 2) return "拓土贤主";
                    if (benevolent) return "仁惠贤主";
                    if (e.PolicyCount >= 4) return "敷政贤主";
                    return "贤主";
                case 3: // 令主
                    if (e.PolicyCount >= 4) return "立制令主";
                    if (cityDelta >= 1) return "拓边令主";
                    if (reignYears >= 20) return "持重令主";
                    return "令主";
                case 5: // 中主
                    if (cityDelta < 0) return "蹙国中主";
                    if (reignYears >= 30) return "安静中主";
                    return "中主";
                case 4: // 守成
                    if (benevolent) return "恭俭守成";
                    if (reignYears >= 30) return "久安守成";
                    return "守成";
                case 6: // 庸主
                    if (foolish) return "昏庸之主";
                    if (reignYears >= 30) return "因循庸主";
                    return "庸主";
                case 7: // 暗主
                    if (foolish) return "昏聩之主";
                    if (tyrant) return "苛暗之主";
                    return "暗主";
                case 8: // 昏乱/末世
                    if (lostThrone && tyrant) return "亡国暴君";
                    if (lostThrone && foolish) return "傀儡废主";
                    if (tyrant) return "暴虐之君";
                    if (foolish) return "昏乱之主";
                    if (lostThrone) return "末世之主";
                    return "昏乱之主";
                default: return null;
            }
        }

        /// <summary>稳定伪随机变体选择（同一皇帝每次打开窗口文案一致——用记录特征做种子。
        /// 系数须与 count=3 互质（37+19=56 不可被 3 整除——2026-08-17 教训：31+17=48 被 3 整除致 20 帝同文案）。</summary>
        private static int StablePick(EmperorRecord e, int salt, int count)
        {
            if (count <= 1) return 0;
            int h = e.StartYear * 37 + e.EndYear * 19 + e.EndCities * 13 + e.EndAge * 7 + e.PolicyCount * 5 + salt * 101;
            return ((h % count) + count) % count;
        }

        /// <summary>多维功业短语（按功业权重排序，每维 3 变体稳定挑选）。阶段3 丰富化：替代旧单条 BestMeritPhrase。</summary>
        private static List<string> MeritPhrases(EmperorRecord e)
        {
            var list = new List<string>();
            int cityDelta = e.EndCities - e.StartCities;
            long treasDelta = e.EndTreasury - e.StartTreasury;
            float mandDelta = e.EndMandate - e.StartMandate;
            float stabDelta = e.EndStability - e.StartStability;
            int popPct = PopDeltaPercent(e);
            int reignYears = e.EndYear - e.StartYear;
            bool mandateCollected = e.StartMandate > 0f || e.EndMandate > 0f;

            if (e.Unified == 1)
                list.Add(Pick(e, 1, $"混一寰宇，威加四海", "混一寰宇，天下混同", "混一寰宇，功盖千秋"));
            if (cityDelta >= 3)
                list.Add(Pick(e, 2, $"拓土{ToChinese(cityDelta)}城，武功赫赫", $"开疆拓土，新得{ToChinese(cityDelta)}城", $"连取{ToChinese(cityDelta)}城，威震四邻"));
            if (mandateCollected && mandDelta >= 8f)
                list.Add(Pick(e, 3, "天命归隆，人心复振", "受命于天，国势重振", "天命日隆，中兴气象"));
            if (popPct >= 30)
                list.Add(Pick(e, 4, "民生殷阜，户口滋殖", "休养生息，生齿日繁", "轻徭薄赋，民户大增"));
            if (e.EndVassals - e.StartVassals >= 1)
                list.Add(Pick(e, 5, "四夷宾服，万邦来朝", "恩威并施，远人来附", "绥抚四方，咸奉正朔"));
            if (e.PolicyCount >= 4 && stabDelta > 0f)
                list.Add(Pick(e, 6, $"文教大兴，施政{ToChinese(e.PolicyCount)}端", "崇文兴教，法度彰明", "更立章程，百度维新"));
            if (treasDelta >= 1000)
                list.Add(Pick(e, 7, "府库充盈，财用有余", "理财有方，库藏丰实", "量入为出，府库殷实"));
            if (stabDelta >= 8f)
                list.Add(Pick(e, 8, "朝纲整肃，政令畅通", "肃正朝纲，百职修举", "整顿纲纪，内外肃然"));
            if (e.WarCount >= 3 && cityDelta > 0)
                list.Add(Pick(e, 9, $"亲历战阵{ToChinese(e.WarCount)}役，皆有克捷", $"攻伐{ToChinese(e.WarCount)}次，兵威大振", $"用兵{ToChinese(e.WarCount)}度，战多克胜"));
            if (cityDelta >= 1 && cityDelta < 3)
                list.Add(Pick(e, 10, "疆域渐拓，边圉日广", "拓地开边，渐启疆宇", "辟土开疆，边境渐宁"));
            // 2026-08-19 评价丰富化：德行/复国/久安三新维
            if ((e.Deeds & DEED_RESTORATION) != 0)
                list.Add(Pick(e, 12, "再造社稷，光复旧物", "重续国祚，宗庙复安", "危而复安，再造之功"));
            if ((e.Deeds & DEED_BENEVOLENT) != 0)
                list.Add(Pick(e, 13, "躬行仁俭，泽及黎庶", "宽刑爱民，仁闻播远", "慈惠爱民，天下归心"));
            if ((e.Deeds & DEED_GENIUS) != 0)
                list.Add(Pick(e, 14, "天纵睿智，聪察善断", "颖悟绝伦，明照万里", "才识过人，剖断如流"));
            if (reignYears >= 30 && stabDelta >= 0f)
                list.Add(Pick(e, 15, "久安之世，海宇承平", "长治久安，四境无虞", "垂拱三十载，天下晏然"));
            if (list.Count == 0)
            {
                // 2026-08-19 修复评语失实：丧地/民耗之君不当"垂拱守成/天下晏然"之美名
                if (cityDelta < 0 || popPct <= -10)
                    list.Add(Pick(e, 11, "谨守遗业，不改其度", "守文继体，无所兴作", "恭己临朝，勉支局面"));
                else
                    list.Add(Pick(e, 11, "垂拱守成，不扰不烦", "清静无为，与民休息", "谨守成宪，天下晏然"));
            }
            return list;
        }

        /// <summary>多维过失短语（优先级与旧 WorstPhrase 一致，每维 3 变体）。</summary>
        private static List<string> FaultPhrases(EmperorRecord e, bool lostThrone)
        {
            var list = new List<string>();
            bool tyrant = (e.Deeds & DEED_TYRANT) != 0;
            bool foolish = (e.Deeds & DEED_FOOLISH) != 0;
            bool usurped = (e.Deeds & DEED_USURPED) != 0;
            int cityDelta = e.EndCities - e.StartCities;
            long treasDelta = e.EndTreasury - e.StartTreasury;
            float stabDelta = e.EndStability - e.StartStability;
            bool mandateCollected = e.StartMandate > 0f || e.EndMandate > 0f;

            if (tyrant) list.Add(Pick(e, 21, "横征暴敛，民怨沸腾", "严刑峻法，残虐生民", "赋役无度，怨声载道"));
            if (foolish) list.Add(Pick(e, 22, "政出多门，纲纪废弛", "委任非人，朝政日非", "荒怠政务，威权下移"));
            if (usurped) list.Add(Pick(e, 23, "为权臣所废，赍恨终身", "大权旁落，终遭废黜", "拱手失位，为臣所迫"));
            if (lostThrone) list.Add(Pick(e, 24, "社稷倾覆，身死国灭", "宗庙隳颓，国祚遂终", "神器易主，宗祀为墟"));
            if (cityDelta <= -3) list.Add(Pick(e, 25, $"丧师失地{ToChinese(-cityDelta)}城", $"连丧{ToChinese(-cityDelta)}城，疆宇日蹙", $"兵败地削，失{ToChinese(-cityDelta)}城"));
            if (mandateCollected && e.EndMandate < 30f) list.Add(Pick(e, 26, "天命日蹙，人心思变", "天命将坠，众叛亲离", "德薄位尊，天命不佑"));
            if (PopDeltaPercent(e) <= -20) list.Add(Pick(e, 27, "民生凋敝，户口耗减", "兵连祸结，民不聊生", "饥馑荐臻，生民涂炭"));
            if (treasDelta <= -800) list.Add(Pick(e, 28, "府库空虚，财用匮乏", "奢靡无度，库藏一空", "用度不节，国用枯竭"));
            if (stabDelta <= -8f) list.Add(Pick(e, 29, "朝纲紊乱，政令不行", "纲纪荡然，朋党横行", "法度废弛，国政紊乱"));
            if (e.WarCount >= 3 && cityDelta < 0) list.Add(Pick(e, 30, $"频年用兵{ToChinese(e.WarCount)}役，师老无功", $"攻伐{ToChinese(e.WarCount)}次而寸土未得", $"穷兵黩武，丧师辱国"));
            if (e.PolicyCount == 0 && stabDelta < 0f) list.Add(Pick(e, 31, "怠于政事，一无所兴", "因循苟且，庶政不理", "无所作为，朝政荒疏"));
            // 2026-08-19 评价丰富化：久位无功
            int reignYearsF = e.EndYear - e.StartYear;
            if (reignYearsF >= 35 && ScoreReign(e) < 2) list.Add(Pick(e, 33, "享国虽久，建树了了", "久居大位而无所兴革", "在位日长，虚耗岁月"));
            if (list.Count == 0)
            {
                // 2026-08-19 修复评语自相矛盾：功业显著者（拓土/民阜/中兴/混一）不再用
                // "虽无大功，亦无大过"兜底——否则出现"连取八城…；虽无大功"的矛盾文本
                bool hasGreatMerit = cityDelta >= 3 || PopDeltaPercent(e) >= 30 || e.Unified == 1
                    || (e.Deeds & DEED_RESTORATION) != 0;
                if (hasGreatMerit)
                    list.Add(Pick(e, 32, "而终无阙失，克保令名", "在位无大过，朝政清平", "持盈保泰，善终其位"));
                else
                    list.Add(Pick(e, 32, "晚节犹完，善始善终", "无大过失，平稳而终", "虽无大功，亦无大过"));
            }
            return list;
        }

        /// <summary>特殊结尾句（短祚/长君/高寿/寿夭/禅让/被篡——只在特殊情形追加，避免冗长）。</summary>
        private static string ClosingPhrase(EmperorRecord e, int rank, int reignYears)
        {
            // 2026-08-19 评价丰富化：禅让善终 / 被篡含恨
            if ((e.Deeds & DEED_ABDICATION) != 0 && rank <= 3 && (e.Deeds & DEED_USURPED) == 0)
                return Pick(e, 43, "逊位于嗣，善始善终。", "禅位而终，功成身退。", "从容逊位，史称美谈。");
            if ((e.Deeds & DEED_USURPED) != 0 && rank <= 5)
                return Pick(e, 44, "终遭废黜，可为浩叹。", "大权旁落，赍志以殁。", "受制权臣，身不得自由。");
            if (reignYears >= 1 && reignYears <= 4 && rank >= 4)
                return Pick(e, 41, "天不假年，惜哉！", "享国日浅，志业未展。", "短祚而崩，人皆惜之。");
            if (reignYears >= 40)
                return Pick(e, 42, "享国日久，臣民安之。", "久于其位，海内承平。", "在位绵长，福泽及世。");
            if (e.EndAge > 0 && e.EndAge >= 80) return "寿逾八旬，福寿全归。";
            if (e.EndAge > 0 && e.EndAge <= 25) return "英年早逝，朝野痛惜。";
            return "";
        }

        /// <summary>三选一稳定变体（StablePick 做种子）。</summary>
        private static string Pick(EmperorRecord e, int salt, string a, string b, string c)
        {
            int i = StablePick(e, salt, 3);
            return i == 0 ? a : (i == 1 ? b : c);
        }

        /// <summary>
        /// 在位加权分（阶段2S-14）：功过维不再等权（拓土/中兴重于府库），特质与在位年限参与修正。
        /// 正分=功业，负分=过失；短祚（1~4 年）正分减半（影响力不足），长君（≥40 年）+2。
        /// </summary>
        /// <summary>
        /// 在位加权分（阶段2S-14）：功过维不再等权（拓土/中兴重于府库），特质与在位年限参与修正。
        /// </summary>

        public static int ScoreReign(EmperorRecord e)
        {
            int cityDelta = e.EndCities - e.StartCities;
            long treasDelta = e.EndTreasury - e.StartTreasury;
            float mandDelta = e.EndMandate - e.StartMandate;
            float stabDelta = e.EndStability - e.StartStability;
            int popPct = PopDeltaPercent(e);
            int reignYears = e.EndYear - e.StartYear;
            // 阶段2S-14：天命维未采集豁免（旧档 Start=End=0 表示未采集——不能当"天命坠"扣分）
            bool mandateCollected = e.StartMandate > 0f || e.EndMandate > 0f;

            int s = 0;
            // 功（加权：军事拓土 2+2 / 天命中兴 3 / 民生 2 / 朝纲 2 / 文治 2 / 附庸 1 / 府库 1）
            if (cityDelta >= 2) s += 2; if (cityDelta >= 5) s += 2;
            if (e.Unified == 1) s += 3; // 阶段2V：混一寰宇（千古功业）
            if (popPct >= 30) s += 2;
            if (treasDelta >= 500) s += 1;
            if (mandateCollected && mandDelta >= 8f) s += 3;
            if (stabDelta >= 8f) s += 2;
            if (e.EndVassals - e.StartVassals >= 1) s += 1;
            if (e.WarCount >= 3 && cityDelta > 0) s += 2;
            if (e.PolicyCount >= 4 && stabDelta > 0f) s += 2;
            // 过（加权：失土 2+2 / 天命坠 3 / 民凋 2 / 朝乱 2 / 丧师 2 / 附庸离 1 / 府空 1 / 怠政 1）
            if (cityDelta <= -2) s -= 2; if (cityDelta <= -5) s -= 2;
            if (popPct <= -20) s -= 2;
            if (treasDelta <= -500) s -= 1;
            if (mandateCollected && (mandDelta <= -8f || e.EndMandate < 30f)) s -= 3;
            if (stabDelta <= -8f) s -= 2;
            if (e.EndVassals - e.StartVassals <= -1) s -= 1;
            if (e.WarCount >= 3 && cityDelta < 0) s -= 2;
            if (e.PolicyCount == 0 && stabDelta < 0f) s -= 1;
            // 特质修正（生前德行直接入分——暴君恶评重罚）
            if ((e.Deeds & DEED_BENEVOLENT) != 0) s += 2;
            if ((e.Deeds & DEED_GENIUS) != 0) s += 1;
            if ((e.Deeds & DEED_TYRANT) != 0) s -= 4;
            if ((e.Deeds & DEED_FOOLISH) != 0) s -= 3;
            // 阶段3：结局修正（禅让=主动逊位古之美德+1；被篡=身不由己轻罚-1；复国=再造之功+2）
            if ((e.Deeds & DEED_ABDICATION) != 0) s += 1;
            if ((e.Deeds & DEED_USURPED) != 0) s -= 1;
            if ((e.Deeds & DEED_RESTORATION) != 0) s += 2;
            if (e.SlewHolder == 1) s += 4;   // 阶段3H-10：灭对方天命国（不世之功）
            // 年限修正：短祚正分减半（过不减免——防短祚暴君逃脱恶评）；长君加成
            if (reignYears >= 1 && reignYears <= 4 && s > 0) s /= 2;
            if (reignYears >= 40) s += 2;
            return s;
        }

        /// <summary>八维功过计数（功维数/过维数——阈值与讨论稿一致；分级兜底用）。</summary>
        private static void CountMerits(EmperorRecord e, out int merit, out int fault)
        {
            merit = 0; fault = 0;
            int cityDelta = e.EndCities - e.StartCities;
            long treasDelta = e.EndTreasury - e.StartTreasury;
            float mandDelta = e.EndMandate - e.StartMandate;
            float stabDelta = e.EndStability - e.StartStability;
            int popPct = PopDeltaPercent(e);
            bool mandateCollected = e.StartMandate > 0f || e.EndMandate > 0f; // 未采集豁免（阶段2S-14）

            if (cityDelta >= 2) merit++; if (cityDelta >= 5) merit++;
            if (cityDelta <= -2) fault++; if (cityDelta <= -5) fault++;
            if (popPct >= 30) merit++;
            if (popPct <= -20) fault++;
            if (treasDelta >= 500) merit++;
            if (treasDelta <= -500) fault++;
            if (mandateCollected && mandDelta >= 8f) merit++;
            if (mandateCollected && (mandDelta <= -8f || e.EndMandate < 30f)) fault++;
            if (stabDelta >= 8f) merit++;
            if (stabDelta <= -8f) fault++;
            if (e.EndVassals - e.StartVassals >= 1) merit++;
            if (e.EndVassals - e.StartVassals <= -1) fault++;
            if (e.WarCount >= 3 && cityDelta > 0) merit++;            // 武功：攻伐拓土
            if (e.WarCount >= 3 && cityDelta < 0) fault++;            // 丧师
            if (e.PolicyCount >= 4 && stabDelta > 0f) merit++;        // 文治
            if (e.PolicyCount == 0 && stabDelta < 0f) fault++;        // 怠政
        }

        // （阶段3 丰富化：旧单条 BestMeritPhrase/WorstPhrase 已由 MeritPhrases/FaultPhrases 多维短语池替代）

        /// <summary>人口变化百分比（未采集=0）。</summary>
        private static int PopDeltaPercent(EmperorRecord e)
        {
            if (e.StartPopulation <= 0) return 0;
            return (int)System.Math.Round((e.EndPopulation - e.StartPopulation) * 100.0 / e.StartPopulation);
        }

        /// <summary>全维皆平（无功无过）。</summary>
        private static bool IsAllFlat(EmperorRecord e)
        {
            return e.EndCities == e.StartCities
                && System.Math.Abs(e.EndTreasury - e.StartTreasury) < 200
                && System.Math.Abs(e.EndMandate - e.StartMandate) < 3f
                && System.Math.Abs(e.EndStability - e.StartStability) < 3f
                && e.EndVassals == e.StartVassals
                && System.Math.Abs(PopDeltaPercent(e)) < 10;
        }

        /// <summary>数字转中文（1~9999——在位年/城数/年龄用）。</summary>
        public static string ToChinese(int n)
        {
            if (n <= 0) return "零";
            string[] d = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            if (n < 10) return d[n];
            if (n < 20) return "十" + (n % 10 == 0 ? "" : d[n % 10]);
            if (n < 100) return d[n / 10] + "十" + (n % 10 == 0 ? "" : d[n % 10]);
            if (n < 1000)
            {
                int r = n % 100;
                string low = r == 0 ? "" : (r < 10 ? "零" + d[r] : d[r / 10] + "十" + (r % 10 == 0 ? "" : d[r % 10]));
                return d[n / 100] + "百" + low;
            }
            int r2 = n % 1000;
            string low2 = r2 == 0 ? "" : (r2 < 100 ? "零" + ToChinese(r2) : ToChinese(r2));
            return d[n / 1000] + "千" + low2;
        }
    }
}
