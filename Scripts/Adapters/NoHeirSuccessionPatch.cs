using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// F9 无嗣继承接入点。
    ///
    /// 2026-10-23 探针实证（.runtime/succession_probe.log，66 次国王死亡样本）：
    ///   原版继位的唯一汇聚点是 `ai.behaviours.KingdomBehCheckKing.execute(Kingdom)`：
    ///     · 第 28 行 clearKingData()  —— 王位失效时先清空
    ///     · 第 42 行 setKing(...)     —— 选中新君时立君
    ///   调用链：KingdomManager.updateCivKingdoms → Kingdom.updateCiv → AiSystem.run → KingdomBehCheckKing.execute
    ///   且全部 setKing 发生时"旧君"均为空 —— 证实"先清空、后立君"两段式。
    ///
    /// 因此在这里做 Prefix 拦截，判定结果决定如何介入（**始终放行原版**）：
    ///   · 远亲继承 → 不介入（原版自己挑人，即现状）
    ///   · 权臣篡位 → 由我们 setKing(权臣)，原版随后检测到"已有君"自然早退
    ///   · 分崩离析 → 不阻断选王（避免国家永久无君），改为重挫稳定度，让既有分裂机制自然发酵
    ///
    /// 用反射挂载而非 [HarmonyPatch] 特性：该类型可能不是 public，
    /// 反射挂载可避免编译期依赖，也符合本工程"internal 目标必须手工 Mount"的既有约定。
    /// </summary>
    public static class NoHeirSuccessionPatch
    {
        private const string KEY_DECIDED_YEAR = "zlr_noheir_year";
        private const string KEY_OUTCOME = "zlr_noheir_outcome";
        private const string DecisionLogName = "no_heir.log";
        private const long MaxLogBytes = 128 * 1024;

        private static bool _mounted;
        private static string _logPath;

        public static void Mount(Harmony harmony)
        {
            if (_mounted) return;
            try
            {
                Type t = AccessTools.TypeByName("ai.behaviours.KingdomBehCheckKing");
                if (t == null)
                {
                    ZhuluContext.Instance.LogWarning("[NoHeir] 未找到 ai.behaviours.KingdomBehCheckKing，无嗣判定不接入");
                    return;
                }
                var execute = AccessTools.Method(t, "execute");
                if (execute == null)
                {
                    ZhuluContext.Instance.LogWarning("[NoHeir] 未找到 KingdomBehCheckKing.execute，无嗣判定不接入");
                    return;
                }
                harmony.Patch(execute, prefix: new HarmonyMethod(typeof(NoHeirSuccessionPatch), nameof(Prefix)));

                // 第二触发点：Kingdom.clearKingData 后置。
                // 理由：`execute` 是"先 clearKingData(第28行) 再 setKing(第42行)"同一个调用内完成，
                // Prefix 进入时国王字段可能尚未清空 → 看不到"无君"窗口。
                // 挂 clearKingData 后置可稳定捕获"刚刚变成无君"的那一刻。
                var clearKing = AccessTools.Method(typeof(Kingdom), nameof(Kingdom.clearKingData));
                if (clearKing != null)
                    harmony.Patch(clearKing, postfix: new HarmonyMethod(typeof(NoHeirSuccessionPatch), nameof(OnKingCleared)));
                else
                    ZhuluContext.Instance.LogWarning("[NoHeir] 未找到 clearKingData，仅依赖 execute 前置触发");

                // 挂载后自检（2026-10-23 教训）：`harmony.Patch` 调用成功 ≠ 补丁真的生效。
                // 首版因 `ref bool __result` 与 BehResult 返回类型不匹配，IL 编译期才失败，
                // 异常被本方法 catch 后只留一行 ERROR，功能实际完全没接上。
                // 这里显式回查 Harmony 补丁表，确认 owner 已登记。
                var info = Harmony.GetPatchInfo(execute);
                bool registered = info != null && info.Prefixes != null
                                  && info.Prefixes.Any(p => p.owner == harmony.Id);
                if (!registered)
                {
                    ZhuluContext.Instance.LogError("[NoHeir] 补丁未登记进 Harmony（挂载静默失败），无嗣判定不生效");
                    return;
                }

                _mounted = true;
                _logPath = System.IO.Path.Combine(ZhuluContext.Instance.ModPath ?? ".", ".runtime", DecisionLogName);
                ZhuluContext.Instance.LogInfo("[NoHeir] 无嗣继承判定已接入（KingdomBehCheckKing.execute，已回查确认）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[NoHeir] 接入失败", ex);
            }
        }

        /// <summary>
        /// 始终放行原版（返回 true）。
        ///
        /// 2026-10-23 教训：`execute` 的返回类型是 `ai.behaviours.BehResult`（枚举），
        /// 首版写成 `ref bool __result` → Harmony IL 编译失败（Cannot assign BehResult to Boolean），
        /// 补丁静默未挂载。现改为**不触碰返回值**：
        ///   · 篡位：先 setKing，原版随后检测到"已有君"自然早退；
        ///   · 分裂：不阻断选王（避免让国家永久无君），改为重挫稳定度让既有分裂机制发酵。
        /// 这样即使判定有误，最坏结果也只是"多一次换君/多扣稳定度"，不会卡死继位。
        /// </summary>
        public static bool Prefix(Kingdom __0)
        {
            try { TryResolve(__0); }
            catch (Exception ex) { ZhuluContext.Instance.LogRateLimited("NoHeir.Prefix", "[NoHeir] 判定异常(已放行原版): " + ex.Message, 30f); }
            return true;   // 永远放行：任何情况下都不阻断继位
        }

        /// <summary>第二触发点：clearKingData 之后（此刻国王字段必为空）。</summary>
        public static void OnKingCleared(Kingdom __instance)
        {
            try { TryResolve(__instance); }
            catch (Exception ex) { ZhuluContext.Instance.LogRateLimited("NoHeir.Cleared", "[NoHeir] 判定异常: " + ex.Message, 30f); }
        }

        private static void TryResolve(Kingdom k)
        {
            if (!ZhuluConfig.Instance.NoHeirResolution) return;
            if (k == null || k.data == null) return;
            if (!k.isAlive()) return;
            if (k.king != null) return;      // 仍有君 → 交给原版

            string kId = k.id.ToString();
            int year = ZhuluTime.CurrentYear;

            // 同一年只判定一次（execute 每帧都会跑，必须节流）
            if (KingdomDataExt.GetLong(k, KEY_DECIDED_YEAR, 0) == year) return;

            NoHeirInput input = CollectInput(k, kId);
            NoHeirDecision decision = NoHeirResolver.Decide(input);

            KingdomDataExt.SetLong(k, KEY_DECIDED_YEAR, year);
            KingdomDataExt.SetLong(k, KEY_OUTCOME, (long)decision.Outcome);

            ZhuluContext.Instance.LogInfo($"[NoHeir] {k.name} 无嗣判定={decision.Outcome} | {decision.Reason}");
            LogDecision(k, kId, input, decision, year);

            switch (decision.Outcome)
            {
                case NoHeirOutcome.Usurpation:
                    TryUsurp(k, kId, input, decision, year);
                    break;   // 篡位成功则原版会因"已有君"早退；失败则退回原版选王

                case NoHeirOutcome.Collapse:
                    try { StabilityManager.Instance.AddExternalBonus(kId, -15f); } catch { }
                    try
                    {
                        ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                            k.name ?? "", "",
                            $"<color=#CC4444>【{k.name}】国无嗣，主少国疑，诸城离心，社稷倾颓。</color>", false);
                    }
                    catch { }
                    ZhuluWorldTip.Show(k, $"【{k.name}】无嗣可立，诸城离心！", 6f, UI.ZhuluUIStyle.Hex.Negative);
                    break;   // 不阻断选王，避免国家永久无君
            }
        }

        // ── 输入采集（全部取自既有系统）──

        private static NoHeirInput CollectInput(Kingdom k, string kId)
        {
            var input = new NoHeirInput
            {
                CentralPower = SafeGet(() => KingdomExt.GetPowerValue(kId), 50f),
                Stability = SafeGet(() => StabilityManager.Instance.GetStability(kId), 60f),
                CityCount = SafeGet(() => k.cities != null ? k.cities.Count : 0, 0)
            };

            // 天命 / 天眷
            float mandate = 0f;
            bool holder = false;
            try
            {
                mandate = PolicyManager.GetMandateValueForKingdom(k);
                if (mandate > 0f) holder = true;
            }
            catch { }
            if (!holder)
            {
                try
                {
                    float nv = NomadicMandateManager.Instance.GetMandateValue(kId);
                    if (nv > 0f) { mandate = nv; holder = true; }
                }
                catch { }
            }
            input.Mandate = mandate;
            input.IsMandateHolder = holder;

            // 权势最高的在任官员
            try
            {
                var recs = OfficeManager.Instance.GetRecordsPublic(kId);
                if (recs != null)
                {
                    foreach (var r in recs)
                    {
                        if (r == null || string.IsNullOrEmpty(r.PersonId) || string.IsNullOrEmpty(r.OfficeId)) continue;
                        float p = CorruptionManager.Instance.GetPower(kId, r.OfficeId);
                        if (p <= input.TopOfficialPower) continue;
                        input.TopOfficialPower = p;
                        input.TopOfficialCorruption = CorruptionManager.Instance.GetCorruption(kId, r.OfficeId);
                        input.HasTopOfficial = true;
                    }
                }
            }
            catch { }

            return input;
        }

        // ── 篡位执行（复用既有仪式：标记 + 罢免 + 史册 + 播报）──

        private static bool TryUsurp(Kingdom k, string kId, NoHeirInput input, NoHeirDecision decision, int year)
        {
            try
            {
                var recs = OfficeManager.Instance.GetRecordsPublic(kId);
                if (recs == null) return false;

                OfficeAppointmentRecord top = null;
                float best = -1f;
                foreach (var r in recs)
                {
                    if (r == null || string.IsNullOrEmpty(r.PersonId) || string.IsNullOrEmpty(r.OfficeId)) continue;
                    float p = CorruptionManager.Instance.GetPower(kId, r.OfficeId);
                    if (p > best) { best = p; top = r; }
                }
                if (top == null) return false;

                Actor usurper = FindActor(k, top.PersonId);
                if (usurper == null || !usurper.isAlive()) return false;

                string usurperName = usurper.getName() ?? "权臣";
                string title = OfficeManager.GetOfficeTitleForKingdom(top.OfficeId, k);

                k.setKing(usurper, false);
                try { DynastyObserver.Instance.MarkUsurpation(kId); } catch { }
                try { OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", top.OfficeId, year, "篡位"); } catch { }
                try
                {
                    ChronicleManager.Instance.AddEntry(year, ChronicleEventType.KingChange, kId,
                        k.name ?? "", usurperName,
                        $"<color=#FF4500>国无嗣，【{title}】{usurperName} 废幼主自立，改朝换代。</color>", false);
                }
                catch { }
                ZhuluWorldTip.Show(k, $"【{k.name}】{usurperName} 篡位成功！", 6f, UI.ZhuluUIStyle.Hex.Negative);
                ZhuluContext.Instance.LogInfo($"[NoHeir] {k.name} 权臣篡位：{title}{usurperName}（{decision.Reason}）");
                return true;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("NoHeir.Usurp", "[NoHeir] 篡位执行异常: " + ex.Message, 30f);
                return false;
            }
        }

        private static Actor FindActor(Kingdom k, string personId)
        {
            try
            {
                if (string.IsNullOrEmpty(personId)) return null;
                var list = World.world?.units?.getSimpleList();
                if (list == null) return null;
                foreach (var a in list)
                {
                    if (a == null || !a.isAlive()) continue;
                    if (a.getID().ToString() == personId) return a;
                }
            }
            catch { }
            return null;
        }

        private static T SafeGet<T>(Func<T> f, T fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        /// <summary>
        /// 决策落盘（2026-10-23）。
        /// 为什么不用 LogInfo：debug_logging 默认关闭，判定过程在正式日志里完全不可见，
        /// 导致"F9 到底跑没跑、为什么没触发"无法回答。这里单独记一份低容量文件（每国每年至多一行），
        /// 既用于验证，也方便玩家/维护者回溯"这个国家为什么没亡/为什么被篡"。
        /// </summary>
        private static void LogDecision(Kingdom k, string kId, NoHeirInput i, NoHeirDecision d, int year)
        {
            try
            {
                if (string.IsNullOrEmpty(_logPath)) return;
                var dir = System.IO.Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                if (System.IO.File.Exists(_logPath) && new System.IO.FileInfo(_logPath).Length > MaxLogBytes) return;

                string line =
                    $"{DateTime.Now:HH:mm:ss} | 年{year} | {k.name}(id={kId}) | {d.Outcome} | " +
                    $"中央{i.CentralPower:F0} 权臣{i.TopOfficialPower:F0}/腐{i.TopOfficialCorruption:F0} 天命{i.Mandate:F0}{(i.IsMandateHolder ? "(持有)" : "")} " +
                    $"稳定{i.Stability:F0} 城{i.CityCount} | 篡位分{d.UsurpScore:F0} 分裂分{d.CollapseScore:F0}{Environment.NewLine}";
                System.IO.File.AppendAllText(_logPath, line, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
