using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：人物权威名（追踪/改名捕获/格式） ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        /// <summary>快速查找已追踪角色的名字（O(1) 字典查找，无反射）。未追踪返回 null。</summary>
        public string GetTrackedName(Actor actor)
        {
            if (actor == null) return null;
            if (_actorNames.TryGetValue(actor, out string name))
            {
                return ObserveManualActorRename(actor, name);
            }
            return null;
        }

        /// <summary>
        /// 原版编辑器调用 Actor.setName 后的唯一接收入口。
        /// 玩家改“名”只修改本人；玩家改“姓”时按现有家族规则更新整个氏族，
        /// 从而同时满足“玩家改名不回滚”与“同族同姓”。
        /// </summary>
        public void AcceptManualActorRename(Actor actor, string requestedName)
        {
            EnsureLoaded();
            if (!IsEligibleActor(actor) || string.IsNullOrWhiteSpace(requestedName)) return;

            string edited = requestedName.Trim();
            string tracked = null;
            _actorNames.TryGetValue(actor, out tracked);
            if (string.IsNullOrWhiteSpace(tracked))
                tracked = ActorAccess.GetString(actor, ActorNameKey);

            // 未被逐鹿接管的角色也允许玩家先改名；改名本身即成为权威基础名。
            if (!string.IsNullOrEmpty(tracked)) _usedPersonNames.Remove(tracked);
            _usedPersonNames.Add(edited);
            _actorNames[actor] = edited;
            ActorAccess.SetString(actor, ActorManualNameKey, "1");
            ActorAccess.SetString(actor, ActorNameKey, edited);
            ActorAccess.SetName(actor, edited); // 阶段3：data.name 同步规范化（剥可能的" - 称号"残留，缓存与数据名永一致）

            try
            {
                Clan clan = actor.clan;
                if (clan == null) return;
                string clanSurname = GetClanSurname(clan);
                string oldSurname = ExtractSurname(tracked);
                string editedSurname = StripClanSuffix(ExtractSurname(edited)); // P1-7c 净化防毒进持久键

                // 只有输入的姓与旧姓/族姓明确不同时才触发整族改姓。
                // 普通的改名（姓未变）绝不影响其他成员。
                string baselineSurname = !string.IsNullOrEmpty(oldSurname) ? oldSurname : clanSurname;
                if (!string.IsNullOrEmpty(editedSurname) && !string.IsNullOrEmpty(baselineSurname)
                    && editedSurname != baselineSurname)
                {
                    RenameClanSurname(clan, clanSurname, editedSurname);
                    SynchronizeClanSurname(clan, editedSurname, actor);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 玩家改姓后氏族同步失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 接受玩家通过原版编辑器写入 ActorData.name 的改名。
        /// 同氏族成员改“名”只更新本人；若明确改了“姓”，则视为玩家在修改整个氏族姓，
        /// 同步更新氏族名和所有成员，保证“允许改姓”与“全族同姓”不互相冲突。
        /// </summary>
        private string ObserveManualActorRename(Actor actor, string tracked)
        {
            string raw = ActorAccess.GetRawName(actor);
            if (string.IsNullOrWhiteSpace(raw)) return tracked;
            // 批次14：包含匹配——外部mod在名字前后加称号/前后缀（如"金丹期·王守仁·玄门弟子"），
            // 只要包含我们的跟踪名就算合法，不触发改名/重写——解决与玄幻/修仙mod的名字权威战。
            if (raw == tracked || raw.Contains(tracked)) return tracked;
            // 剥显示后缀（编辑器输入框可能带" - 邑君"式称号——按纯名接受）
            try { raw = Adapters.TitleSuffixPatches.StripKingSuffix(raw).Trim(); } catch { }
            if (string.IsNullOrWhiteSpace(raw) || raw == tracked || raw.Contains(tracked)) return tracked;
            // 阶段3 改名放宽：原版编辑器并非所有路径都置 custom_name（用户实锤"马翰改李翰回退"）——
            // 双门判定：custom_name 标记 或 输入含汉字 均认定为玩家改名；原版自动名（拉丁字母）不误收
            if (!ActorAccess.IsCustomName(actor) && !ContainsCjk(raw)) return tracked;
            // 兼容直接写 ActorData.name 而不调 setName 的编辑器/Mod。
            AcceptManualActorRename(actor, raw);
            return _actorNames.TryGetValue(actor, out string accepted) ? accepted : raw;
        }

        /// <summary>是否含汉字（CJK 基本区）——用于区分玩家中文改名与原版拉丁自动名。</summary>
        private static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c >= '\u4E00' && c <= '\u9FFF') return true;
            }
            return false;
        }

        /// <summary>阶段3 改名功能：改名 UI 入口——玩家主动改人名（改姓自动联动全族，与编辑器路径同规则）。</summary>
        public void NotifyManualActorRename(Actor actor, string newName)
        {
            if (actor == null || string.IsNullOrWhiteSpace(newName)) return;
            try { newName = Adapters.TitleSuffixPatches.StripKingSuffix(newName).Trim(); } catch { }
            if (string.IsNullOrWhiteSpace(newName)) return;
            AcceptManualActorRename(actor, newName);
            ZhuluContext.Instance.LogInfo($"[ChineseName] 玩家改人名: {ActorAccess.GetRawName(actor) ?? "?"} → {newName}");
        }

        /// <summary>阶段3H-14：旧档越权名制自愈——人类带·格式/兽人无·格式时按种族重命名。
        /// 只修个体名（不动氏族姓），保留原本名尽量减少变化。</summary>
        private string EnforceSpeciesNameFormat(Actor actor, string currentName)
        {
            try
            {
                if (string.IsNullOrEmpty(currentName)) return currentName;
                bool nomad = IsNomadicActor(actor);
                bool hasDot = currentName.IndexOf('\u00b7') >= 0;
                // 批次15：跨族名检测——东方人不该有游牧本名，游牧人不该有纯东方双字名
                if (!nomad && !hasDot)
                {
                    string sn = GetPreferredSurname(actor);
                    if (!string.IsNullOrEmpty(sn) && currentName.StartsWith(sn) && currentName.Length > sn.Length)
                    {
                        string givenPart = currentName.Substring(sn.Length);
                        bool isNomadGiven = false;
                        foreach (var ng in NomadicMaleGiven) { if (givenPart == ng) { isNomadGiven = true; break; } }
                        if (!isNomadGiven) foreach (var ng in NomadicFemaleGiven) { if (givenPart == ng) { isNomadGiven = true; break; } }
                        if (isNomadGiven)
                        {
                            _usedPersonNames.Remove(currentName);
                            string regenerated = GenerateActorName(actor);
                            _actorNames[actor] = regenerated;
                            ActorAccess.SetString(actor, ActorNameKey, regenerated);
                            return regenerated;
                        }
                    }
                }
                if (nomad && !hasDot)
                {
                    // 兽人却无·：拆姓（去尾缀）取东方本名段重组为 氏族姓·本名
                    string sn = GetPreferredSurname(actor);
                    if (string.IsNullOrEmpty(sn)) sn = ExtractSurname(currentName);
                    if (string.IsNullOrEmpty(sn)) return currentName;
                    string given = currentName;
                    if (given.StartsWith(sn) && given.Length > sn.Length) given = given.Substring(sn.Length);
                    if (string.IsNullOrEmpty(given) || !ContainsCjk(given)) given = "";
                    if (string.IsNullOrEmpty(given))
                    {
                        bool m = true;
                        try { m = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                        given = Pick(m ? NomadicMaleGiven : NomadicFemaleGiven);
                    }
                    string fixedName = sn + "·" + given;
                    _usedPersonNames.Remove(currentName);
                    _actorNames[actor] = fixedName;
                    _usedPersonNames.Add(fixedName);
                    ActorAccess.SetString(actor, ActorNameKey, fixedName);
                    ActorAccess.SetName(actor, fixedName);
                    return fixedName;
                }
                if (!nomad && hasDot)
                {
                    // 人类却带·：取·后本名，按 姓+本名 重组
                    int dot = currentName.IndexOf('·');
                    string given = currentName.Substring(dot + 1).Trim();
                    string sn = currentName.Substring(0, dot).Trim();
                    if (string.IsNullOrEmpty(sn)) sn = GetPreferredSurname(actor);
                    if (string.IsNullOrEmpty(sn) || string.IsNullOrEmpty(given) || !ContainsCjk(given))
                        return currentName;   // 无法安全拆——不动（后续氏族同步会重整）
                    string fixedName = sn + given;
                    _usedPersonNames.Remove(currentName);
                    _actorNames[actor] = fixedName;
                    _usedPersonNames.Add(fixedName);
                    ActorAccess.SetString(actor, ActorNameKey, fixedName);
                    ActorAccess.SetName(actor, fixedName);
                    return fixedName;
                }
            }
            catch { }
            // 阶段3H-22d：污染名自愈——同格式但超长（多层姓堆叠遗留）→ 保留首段已知姓+新取本名
            try
            {
                bool nomad = IsNomadicActor(actor);   // v3.1 的 nomad 在上方 try 内作用域——此处重算
                int cap = nomad ? 9 : 6;
                if (currentName.Length > cap)
                {
                    string sn = ExtractSurname(currentName);
                    if (string.IsNullOrEmpty(sn)) sn = GetPreferredSurname(actor);
                    if (string.IsNullOrEmpty(sn)) sn = nomad ? Pick(NomadicSurnames) : Pick(_config.surnames);
                    bool m2 = true;
                    try { m2 = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                    string fresh = nomad
                        ? sn + "·" + Pick(m2 ? NomadicMaleGiven : NomadicFemaleGiven)
                        : sn + Pick(_config.neutralGiven);
                    _usedPersonNames.Remove(currentName);
                    _actorNames[actor] = fresh;
                    _usedPersonNames.Add(fresh);
                    ActorAccess.SetString(actor, ActorNameKey, fresh);
                    ActorAccess.SetName(actor, fresh);
                    ZhuluContext.Instance.LogWarning($"[Naming] 污染名自愈: {currentName} → {fresh}");
                    return fresh;
                }
            }
            catch { }
            return currentName;
        }

        public string EnsureActorName(Actor actor, bool forceNew)
        {
            EnsureLoaded();
            if (!IsEligibleActor(actor)) return null;
            SeedUsedPersonNamesOnce();

            // （批次18-A1：主题命名基础设施已随 ZhuluThemeNaming.cs 删除——用户确认不需要）
            if (_actorNames.TryGetValue(actor, out string tracked))
            {
                tracked = ObserveManualActorRename(actor, tracked);
                // 批次14：包含匹配——当前显示名已含跟踪名（外部mod加了称号/前后缀）则不重写
                try
                {
                    string curRaw = ActorAccess.GetRawName(actor);
                    if (!string.IsNullOrEmpty(curRaw) && curRaw.Contains(tracked))
                    {
                        _actorNames[actor] = tracked;
                        return tracked;
                    }
                }
                catch { }
                if (ActorAccess.GetString(actor, ActorManualNameKey) != "1")
                    tracked = EnforceSpeciesNameFormat(actor, tracked);
                ActorAccess.SetName(actor, tracked);
                return tracked;
            }

            string saved = ActorAccess.GetString(actor, ActorNameKey);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                string raw = ActorAccess.GetRawName(actor);
                // 新版本打过手动标记的角色，读档时以玩家保存的真实数据名为准。
                string accepted = ActorAccess.GetString(actor, ActorManualNameKey) == "1"
                    && !string.IsNullOrWhiteSpace(raw) ? raw : saved;
                // 阶段3H-14：读档后名制自愈（玩家手改名除外——尊重玩家输入）
                if (ActorAccess.GetString(actor, ActorManualNameKey) != "1")
                    accepted = EnforceSpeciesNameFormat(actor, accepted);
                _actorNames[actor] = accepted;
                _usedPersonNames.Add(accepted);
                ActorAccess.SetString(actor, ActorNameKey, accepted);
                ActorAccess.SetName(actor, accepted);
                return accepted;
            }

            if (!forceNew) return null;

            string generated = GenerateUniqueActorName(actor);
            if (string.IsNullOrWhiteSpace(generated)) return null;

            _actorNames[actor] = generated;
            _usedPersonNames.Add(generated);
            ActorAccess.SetString(actor, ActorNameKey, generated);
            ActorAccess.SetName(actor, generated);
            return generated;
        }

        // 2026-10-06 移出：FormatActorDisplayName（SuffixMap 君主后缀追加路径）——
        // 完整代码见 docs/移出代码存档-称号后缀.md；
        // 唯一调用点 ZhuluChineseNamePatches.ActorNamePostfix 改为直赋权威名。

    }
}
