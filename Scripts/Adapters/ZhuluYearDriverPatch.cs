using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 年度驱动补丁（主循环分帧 2026-08-15）：
    /// 挂载到游戏原生 Kingdom.updateAge 的 Postfix，每国每年自动触发一次。
    /// 跨年帧只做 O(1) 入队；按国年度结算由 Main.Update 每帧限时（4ms + 件数上限双停止）处理；
    /// 全局年度系统分 5 组，由 Main.Update 3 秒轮询逐组执行（跨年后约 15 秒摊完）。
    /// 设计来源：状态备忘分帧铁律 + stash rollback-save-20260814-final 分帧实现（修复其跨年重复结算缺陷）。
    /// </summary>
    public static class ZhuluYearDriverPatch
    {
        // ── 按国年度分帧队列 ──
        // 用 Dictionary 而非 Queue：跨年时同王国再次入队 = 同 key 覆盖（Coalesced 合并），
        // 保证「每年每国恰好结算一次」——修复 Queue 版跨年残留被新年重复入队导致的双重结算。
        private static readonly Dictionary<long, Kingdom> _pending = new Dictionary<long, Kingdom>();
        private static int _pendingYear = -1;          // 队列所属年（跨年时更新，处理按此年结算）
        private const int MaxKingdomsPerFrame = 8;     // 件数上限兜底：防单国处理过慢吃满整帧预算

        // ── 全局年度分帧（分 5 组，每 3 秒 tick 执行一组，跨年后 15 秒内完成）──
        private const int GLOBAL_PHASES = 5;
        private static int _lastGlobalYear = -1;
        private static int _globalPhase = 0;

        /// <summary>由 Main.cs 调用，显式挂载年度驱动补丁。</summary>
        public static void Mount(Harmony harmony)
        {
            var updateAgeMethod = AccessTools.Method(typeof(Kingdom), "updateAge");
            if (updateAgeMethod != null)
            {
                harmony.Patch(updateAgeMethod,
                    postfix: new HarmonyMethod(typeof(ZhuluYearDriverPatch), nameof(KingdomYearPostfix)));
                ZhuluContext.Instance.LogInfo("[YearDriver] Kingdom.updateAge 年度驱动补丁挂载成功");
            }
            else
            {
                ZhuluContext.Instance.LogWarning("[YearDriver] 未找到 Kingdom.updateAge 方法，年度驱动不可用");
            }
        }

        /// <summary>
        /// 每国每年触发一次（游戏原生 updateAge 的后置）。跨年帧只做 O(1) 入队：
        /// 14 个按国系统由 ProcessPendingKingdoms 在后续帧限时分摊执行（消除跨年单帧尖峰）。
        /// 同王国跨年再次入队 = 覆盖旧条目（合并为最新年），不重复结算。
        /// </summary>
        static void KingdomYearPostfix(Kingdom __instance)
        {
            try
            {
                if (__instance == null || __instance.isRekt()) return;
                int year = ZhuluTime.CurrentYear;
                _pendingYear = year;
                _pending[__instance.id] = __instance;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[YearDriver] 王国入队异常", ex);
            }
        }

        /// <summary>
        /// 每帧限时处理年度王国队列（由 Main.Update 每帧调用）。
        /// 帧预算 maxMsPerFrame 内尽可能多处理（默认 4ms/帧：60fps 下约占 25% 帧预算，无感），
        /// 件数上限 MaxKingdomsPerFrame 双停止。处理不完的残留跨年继续补完——
        /// 每年每国必结算一次，不丢数据。
        /// </summary>
        public static void ProcessPendingKingdoms(float maxMsPerFrame)
        {
            if (maxMsPerFrame <= 0f || _pending.Count == 0) return;
            int year = _pendingYear;

            // 快照键列表：防止遍历期间 Postfix 新入队导致修改枚举异常
            long[] ids = new long[_pending.Count];
            _pending.Keys.CopyTo(ids, 0);

            long start = Stopwatch.GetTimestamp();
            int processed = 0;
            foreach (long id in ids)
            {
                if (!_pending.TryGetValue(id, out Kingdom k)) continue;
                _pending.Remove(id);
                if (k == null || k.isRekt()) continue;
                try
                {
                    ProcessKingdomYear(k, year);
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogError($"[YearDriver] 按国系统处理异常 [{k.name}]", ex);
                }
                processed++;

                double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs >= maxMsPerFrame || processed >= MaxKingdomsPerFrame) break;
            }
        }

        /// <summary>单个王国的 13 个按国系统（保持原依赖顺序：归族→档案→继承）。
        /// 阶段2V：叛军国跳过官制系统（爵位/百官/城主/礼治/宣称/附庸/身份/党争/科举——叛军无位阶；转正后自动恢复）。</summary>
        private static void ProcessKingdomYear(Kingdom k, int year)
        {
            bool isRebel = Domain.ZhuluRebelService.IsRebel(k);

            MeasureKingdom("Treasury", k, year, () => TreasuryManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Stability", k, year, () => StabilityManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Corruption", k, year, () => CorruptionManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Trait", k, year, () => TraitManager.Instance.OnKingdomYear(k, year));

            // 家庭/氏族继承与王室子女归族（在继承制选人之前执行）。
            // 先做全体旧档修复与同姓同步，再保留 KinshipTracker 的现任君主直系兜底。
            MeasureKingdom("Kinship", k, year, () =>
            {
                Naming.ZhuluChineseNameService.Instance.EnforceFamilyRulesForKingdom(k);
                KinshipTracker.EnforceRoyalClan(k);
            });

            // 王室档案重建（归族之后：子女已归族，三代关系完整）
            MeasureKingdom("RoyalRegistry", k, year, () => RoyalRegistry.Instance.OnKingdomYear(k, year));

            // 家族谱牒年度重建（阶段3 F1：主家/分支/家声/家风——编年积累）
            MeasureKingdom("FamilyRegistry", k, year, () => FamilyRegistry.Instance.OnKingdomYear(k, year));

            MeasureKingdom("Succession", k, year, () => SuccessionManager.Instance.OnKingdomYear(k, year));
            // 厌战是国家与战争的通用状态，叛军也必须累积。
            // 旧顺序在 isRebel return 之后才调用，导致叛军厌战永远为 0，叛乱战争无法进入长期和谈。
            MeasureKingdom("WarExhaust", k, year, () => ZhuluWarService.TickWarExhaustion(k, year));
            if (isRebel) return; // ── 叛军体制：以下官制系统全部不适用（阶段2V）──
            if (ZhuluConfig.Instance.TierSystemEnabled) // 阶段2L：爵位开关
                MeasureKingdom("ZhuluTier", k, year, () => ZhuluTierService.EnsureTierTrait(k, year)); // 阶段2B 包1：爵位年度检查
            MeasureKingdom("LocalEffects", k, year, () => OfficeManager.Instance.RegisterLocalEffects(k.id.ToString())); // 阶段2D ③：地方官效果
            MeasureKingdom("Promotion", k, year, () => OfficeManager.Instance.TickPerformanceAndPromotion(k.id.ToString(), k.name ?? "本国", year)); // 阶段2D ④：政绩升迁
            if (ZhuluConfig.Instance.CityLeaderEnabled) // 阶段2L：城主开关
                MeasureKingdom("CityLeader", k, year, () => CityLeaderTick(k, year)); // 阶段2E ①：城主官职化
            if (ZhuluConfig.Instance.LiZhiEnabled) // 阶段2L：礼治开关
                MeasureKingdom("LiZhi", k, year, () => ZhuluLiZhiService.TickSacrifice(k.id.ToString(), k, year)); // 阶段2B 包2：礼治祭祀
            MeasureKingdom("Reparations", k, year, () => ZhuluWarService.TickReparations(k, year)); // 2026-08-20：赔款分期年度偿付
            MeasureKingdom("Claim", k, year, () => ClaimManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Vassal", k, year, () => VassalManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Identity", k, year, () => IdentityClassifier.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Faction", k, year, () => FactionManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("FactionPolitics", k, year, () => FactionPoliticsManager.Instance.OnKingdomYear(k, year));
            MeasureKingdom("Keju", k, year, () => KejuManager.Instance.OnKingdomYear(k, year));
        }

        /// <summary>按国系统计时并隔离异常，避免单个系统截断同年后续处理。</summary>
        /// <summary>城主年度：补位空缺 → 权势累积 → 退休检查 → AI 安全撤职（低概率）。</summary>
        private static void CityLeaderTick(Kingdom k, int year)
        {
            try
            {
                if (k?.cities == null) return;
                var usedThisTick = new System.Collections.Generic.HashSet<long>();
                foreach (var c in k.cities)
                {
                    if (c == null) continue;
                    // 补位空缺（本次年度每 actor 只补一次——防同一人补多城）
                    if (c.leader == null)
                    {
                        long? picked = Domain.CityLeaderService.FillCityLeader(k, c, usedThisTick);
                        if (picked.HasValue) usedThisTick.Add(picked.Value);
                    }
                    // 退休检查
                    Domain.CityLeaderService.TickRetirement(k, c);
                    // 任期限制（2026-08-18：45 年届满卸任）
                    Domain.CityLeaderService.TickTermLimit(k, c, year);
                }
                // 权势累积
                Domain.CityLeaderService.TickLeaderPower(k, year);
                // AI 安全撤职（低概率：权势高城主威胁王权——尾大不掉时君主犹豫；安全时偶尔换血）
                foreach (var c in k.cities)
                {
                    if (c?.leader == null) continue;
                    if (k.capital != null && c.id == k.capital.id) continue;
                    try
                    {
                        if (Domain.CityLeaderService.CanSafelyRemove(k, c)
                            && UnityEngine.Random.value < 0.02f
                            && Domain.CityLeaderService.GetLeaderPower(c, k) >= 30)
                        {
                            Domain.CityLeaderService.RemoveCityLeader(k, c, false);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void MeasureKingdom(string name, Kingdom k, int year, Action action)
        {
            try
            {
                ZhuluBenchmark.Measure(name, action);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[YearDriver] ✗ 按国系统 {name} [{k.name}] 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 全局年度处理（由 Main.Update 每 3 秒调用一次）：每年分 5 组逐组执行，
        /// 每组 1~2 个系统，分散到 5 个 tick（约 15 秒）完成——消除跨年单帧全局系统尖峰。
        /// </summary>
        public static void RunGlobalYearIfDue(int year)
        {
            if (year != _lastGlobalYear)
            {
                _lastGlobalYear = year;
                _globalPhase = 0;
                // 批次9a：跨年广播（UI 订阅制刷新——科举/天命等窗口不再每帧年检/3秒轮询）
                Core.ZhuluYearEvents.RaiseYearChanged(year);
            }
            if (_globalPhase >= GLOBAL_PHASES) return;

            switch (_globalPhase)
            {
                case 0:
                    // 兜底：0.51.2 主菜单「新世界」不触发 MapBox.generateNewMap（Postfix 未挂载生效），
                    // NewWorld() 从未执行 → _freshWorld=false → 派系永不生成（新档被当旧档）。
                    // 世界从未初始化（非读档/非 NewWorld）且刚开局（≤2年）→ 自动补新世界初始化。
                    MeasureGlobal("NewWorldFallback", () =>
                    {
                        if (!Persistence.ZhuluSaveCore.Instance.WorldInitialized && year <= 2)
                        {
                            Persistence.ZhuluSaveCore.Instance.NewWorld();
                            ZhuluContext.Instance.LogInfo($"[YearDriver] 兜底新世界初始化（generateNewMap 未触发）: world={Persistence.ZhuluSaveCore.Instance.WorldId}");
                        }
                    });
                    MeasureGlobal("Benchmark", () => ZhuluBenchmark.ReportIfNeeded(year));
                    break;
                case 1:
                    // MandateManager.TickAll 已废弃为空壳（2026-08-17 清理）——正统天命唯一入口是 OrthodoxMandateManager
                    MeasureGlobal("Orthodox", () => OrthodoxMandateManager.Instance.TickAll(year));
                    MeasureGlobal("NomadicMandate", () => NomadicMandateManager.Instance.TickAll(year));
                    break;
                case 2:
                    MeasureGlobal("Regime", () => RegimeManager.TickPowerValues(year));
                    MeasureGlobal("Office", () => OfficeManager.Instance.ProcessAutoSelectionAndAppointment(year));
                    break;
                case 3:
                    MeasureGlobal("Policy", () => PolicyManager.Instance.TickAutoEnact(year));
                    // 叛乱转正检测：标记为"叛乱命名"的王国如果不再打叛乱战争 → 改为正式国号
                    MeasureGlobal("RebelRename", CheckRebelKingdomRename);
                    break;
                default:
                    // 地基：个人史册出生/死亡年度扫描（全局每年一次）
                    MeasureGlobal("PersonHistory", () => PersonHistoryManager.Instance.OnGlobalYear(year));
                    // 2026-08-20 亡国之君检测（王国快照对比——灭国时最后的国王获特质）
                    MeasureGlobal("TraitFallenKing", () => TraitManager.Instance.OnGlobalYear(year));
                    // 2026-08-19 王室氏族特质年度兜底同步（王族99/普通18——即时切换在 RoyalClanTraitPatch）
                    MeasureGlobal("RoyalClanTrait", () => Domain.RoyalClanTraitManager.Instance.SyncAllClans());
                    // 氏族成员上限执行器 v3：温和迁移（同国收编→裂植新族），豁免族长/国王直系，永不禁族
                    MeasureGlobal("ClanCap", () => Domain.ClanMemberCapEnforcer.EnforceAll(year));
                    // 城市崩塌看门狗（诊断取证——人口骤降/城市移除年度对比）
                    MeasureGlobal("CityCrashWatch", () => Domain.CityCrashWatchdog.OnYear(year));
                    // 帧数优化：年度修剪死国残留修正计划（防 Evaluate 遍历逐年变慢）
                    MeasureGlobal("PlanPrune", () => Gameplay.ModifierPipeline.Instance.PruneDeadPlans(year));
                    // 阶段2B 包3：战争年度评估（分数/结算/求和——替代每帧 Check）
                    MeasureGlobal("WarEval", () => ZhuluWarService.TickAllWars(year));
                    // 阶段2B 包3：全面战争 AI 触发（五重门槛 + 年度概率）
                    MeasureGlobal("TotalWarAI", () => ZhuluWarService.TickTotalWarAI(year));
                    // 阶段2C 邦交：互不侵犯 AI 发起
                    if (ZhuluConfig.Instance.DiplomacyEnabled) // 阶段2L：邦交开关
                MeasureGlobal("DiplomacyAI", () => ZhuluDiplomacyService.TickNonAggressionAI(year));
                    break;
            }
            _globalPhase++;
        }

        private static void MeasureGlobal(string name, Action action)
        {
            try
            {
                ZhuluBenchmark.Measure(name, action);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[YearDriver] 全局系统 {name} 年度处理异常（已隔离）", ex);
            }
        }

        /// <summary>检查所有叛乱命名的王国，叛乱结束后给正式国号；同时兜底补命名漏网叛军（newWar 补丁未触发/inspire 等路径）。
        /// 2026-08-18 public：Main 高频节流调用（10s）——开局首年窗口期新叛军名不齐（年度扫描要到跨年才跑）。</summary>
        public static void CheckRebelKingdomRename()
        {
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                bool isRebel = Persistence.KingdomDataExt.GetStr(k, "zlr_rebel_named") == "1";

                // 是否仍在叛乱战争中（rebellion / inspire；war_type 取 data 优先，asset 兜底）
                bool inRebelWar = false;
                try
                {
                    foreach (War w in k.getWars())
                    {
                        if (w == null || w.hasEnded()) continue;
                        string wt = w.data?.war_type ?? "";
                        if (string.IsNullOrEmpty(wt))
                        {
                            var asset = w.getAsset();
                            if (asset != null) wt = asset.id;
                        }
                        if (wt == "rebellion" || wt == "inspire") { inRebelWar = true; break; }
                    }
                }
                catch { }

                if (isRebel)
                {
                    // 已标记叛军：叛乱结束 → 转正为正式国号
                    if (!inRebelWar)
                    {
                        // 叛乱期间只用临时叛军体制；转正时按此刻国王种族锁定正式体系。
                        Domain.KingdomCivilizationService.Instance.RebindByRuler(k, k.king, "叛乱成功转正");
                        // 阶段2B ⑤：天命国误标叛军时只清标记不改名（王朝名保持）
                        bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                        if (!isMandate)
                        {
                            Naming.ZhuluChineseNameService.Instance.SetKingdomName(k);
                        }
                        Persistence.KingdomDataExt.SetStr(k, "zlr_rebel_named", "");
                        ZhuluContext.Instance.LogInfo($"[YearDriver] 叛乱转正: {k.name}{(isMandate ? "（天命国仅清标记）" : "")}");
                    }
                }
                else if (inRebelWar)
                {
                    // 防误伤：如果本场叛乱战争的对侧参战方已有 zlr_rebel_named 标记，
                    // 说明叛乱已由 newWar 补丁命名，自己（如原国家）不是叛军，跳过。
                    if (RebelWarHasNamedOpponent(k)) continue;
                    // 阶段2E 修复（用户 2026-08-16 报告：原国家被误改"XX起义"）：
                    // 补命名只针对【守方】（叛军）——攻方（原国家）不补。
                    // 依据：rebellion（pDefender=叛军）/inspire（makeOwnKingdom 新国=守方）都是守方为叛军。
                    bool isDefender = false;
                    try
                    {
                        foreach (War w in k.getWars())
                        {
                            if (w == null || w.hasEnded()) continue;
                            string wt = w.data?.war_type ?? "";
                            if (wt == "rebellion" || wt == "inspire")
                            {
                                foreach (Kingdom d in w.getDefenders())
                                {
                                    if (d == k) { isDefender = true; break; }
                                }
                                if (isDefender) break;
                            }
                        }
                    }
                    catch { }
                    if (!isDefender) continue; // 攻方（原国家）不补命名

                    // 漏网叛军（叛乱命名补丁未触发/战争类型为 inspire 等）：立即补叛军命名
                    // 阶段2B ⑤：天命国跳过（防王朝名被覆盖）
                    try
                    {
                        bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                        if (isMandate)
                        {
                            ZhuluContext.Instance.LogInfo("[YearDriver] 补叛军命名跳过（天命国）");
                        }
                        else
                        {
                        string rebelName = Naming.ZhuluChineseNameService.Instance.GenerateRebelName(k);
                        if (!string.IsNullOrEmpty(rebelName) && k.data != null)
                        {
                            k.data.name = rebelName;
                            Persistence.KingdomDataExt.SetStr(k, "zlr_rebel_named", "1");
                            ZhuluContext.Instance.LogInfo($"[YearDriver] 补叛军命名: {rebelName}");
                        }
                        }
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[YearDriver] 补叛军命名异常: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 检查该王国参与的叛乱战争中，对侧参战方是否已有叛军命名标记。
        /// 已有 → 本场叛乱已命名，自己不是漏网叛军（可能是原国家），返回 true。
        /// </summary>
        private static bool RebelWarHasNamedOpponent(Kingdom k)
        {
            try
            {
                foreach (War w in k.getWars())
                {
                    if (w == null || w.hasEnded()) continue;
                    string wt = w.data?.war_type ?? "";
                    if (string.IsNullOrEmpty(wt))
                    {
                        var asset = w.getAsset();
                        if (asset != null) wt = asset.id;
                    }
                    if (wt != "rebellion" && wt != "inspire") continue;

                    // 2026-08-16 修复（用户报告多叛军命名 bug）：只检查"对侧"（k 不在的一侧）。
                    // 根因：多叛军 joinDefenders 同场战争——同侧队友叛军已有标记被误判"对侧已命名"，
                    // 导致第二/三个叛军跳过补命名（只有第一个是"XX起义"）。
                    bool kInAttackers = false;
                    foreach (Kingdom a in w.getAttackers())
                    {
                        if (a != null && a == k) { kInAttackers = true; break; }
                    }
                    if (kInAttackers)
                    {
                        // k 在攻方 → 检查守方（对侧）
                        foreach (Kingdom enemy in w.getDefenders())
                        {
                            if (enemy != null && enemy != k && enemy.isAlive()
                                && Persistence.KingdomDataExt.GetStr(enemy, "zlr_rebel_named") == "1")
                                return true;
                        }
                    }
                    else
                    {
                        // k 在守方（或未知）→ 检查攻方（对侧）；守方队友有标记不跳过（漏网叛军仍需命名）
                        foreach (Kingdom enemy in w.getAttackers())
                        {
                            if (enemy != null && enemy != k && enemy.isAlive()
                                && Persistence.KingdomDataExt.GetStr(enemy, "zlr_rebel_named") == "1")
                                return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>世界重载（读档/新世界）时重置分帧状态与全局年份标记。</summary>
        public static void ResetYear()
        {
            _lastGlobalYear = -1;
            _globalPhase = 0;
            _pending.Clear();
            _pendingYear = -1;
        }
    }
}
