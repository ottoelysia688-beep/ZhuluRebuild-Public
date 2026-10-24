using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：姓氏（选姓/冠姓/氏族姓同步） ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        public void AdoptSurname(Actor actor, Actor surnameSource)
        {
            EnsureLoaded();
            if (actor == null || surnameSource == null) return;
            string newSurname = GetPreferredSurname(surnameSource);
            if (string.IsNullOrEmpty(newSurname)) newSurname = ExtractSurname(ActorAccess.GetName(surnameSource));
            if (string.IsNullOrEmpty(newSurname)) return;

            string oldSurname = GetPreferredSurname(actor);
            ActorAccess.SetString(actor, ActorSurnameKey, newSurname);
            ActorAccess.SetString(actor, SharedFamilyNameKey, newSurname);

            string authoritative = GetTrackedName(actor);
            if (string.IsNullOrEmpty(authoritative)) return;

            string givenName = authoritative;
            if (!string.IsNullOrEmpty(oldSurname) && authoritative.StartsWith(oldSurname) && authoritative.Length > oldSurname.Length)
                givenName = authoritative.Substring(oldSurname.Length);
            else
            {
                string parsed = ExtractSurname(authoritative);
                if (!string.IsNullOrEmpty(parsed) && authoritative.Length > parsed.Length)
                    givenName = authoritative.Substring(parsed.Length);
            }
            if (string.IsNullOrEmpty(givenName)) givenName = Pick(_config.neutralGiven);
            string updated = newSurname + givenName;
            _actorNames[actor] = updated;
            _usedPersonNames.Add(updated);
            ActorAccess.SetString(actor, ActorNameKey, updated);
            ActorAccess.SetName(actor, updated);
        }

        public string GetPreferredSurname(Actor actor)
        {
            EnsureLoaded();
            if (actor == null) return "";
            string surname = ActorAccess.GetString(actor, ActorSurnameKey);
            if (!string.IsNullOrEmpty(surname)) return surname;
            surname = ActorAccess.GetString(actor, SharedFamilyNameKey);
            if (!string.IsNullOrEmpty(surname)) return surname;
            return "";
        }

        /// <summary>供谱牒/氏族规则复用的复姓安全解析入口。</summary>
        public string ResolveSurname(string source)
        {
            EnsureLoaded();
            return ExtractSurname(source);
        }

        /// <summary>
        /// 获取氏族权威姓。当前正式氏族名优先（例如“晋戚氏”解析为“戚”），
        /// 其次才读取持久化字段和创始人姓名。这样可以自动修复旧版个人姓字段污染。
        /// </summary>
        public string GetClanSurname(Clan clan)
        {
            EnsureLoaded();
            if (clan == null) return "";
            string currentName = "";
            string currentSurname = "";
            string storedSurname = "";
            try
            {
                currentName = clan.data?.name ?? "";
                currentSurname = ExtractClanSurname(currentName);
                storedSurname = ReadPersistentValue(clan.data, ClanSurnameKey);
            }
            catch { }

            if (!string.IsNullOrEmpty(storedSurname))
            {
                // 有新版本权威标记后，当前正式氏族名仍优先：玩家手改“晋戚氏→晋杨氏”应被接受，
                // 并由后续同步把全族改为杨姓，而不是被旧 storedSurname 改回。
                if (!string.IsNullOrEmpty(currentSurname) && currentSurname != storedSurname)
                {
                    WritePersistentValue(clan.data, ClanSurnameKey, currentSurname);
                    return currentSurname;
                }
                return storedSurname;
            }

            // 旧档没有 ClanSurnameKey，必须区分“旧机制错误生成”与“玩家已改名”。
            // 当前名与旧权威缓存不同代表玩家编辑，保留当前名；否则以创始人的真实姓名修正氏族姓。
            string savedClanName = ReadPersistentName(clan.data);
            bool currentLooksPlayerEdited = !string.IsNullOrWhiteSpace(savedClanName)
                && !string.IsNullOrWhiteSpace(currentName) && currentName != savedClanName;
            string surname = currentLooksPlayerEdited ? currentSurname : "";
            if (string.IsNullOrEmpty(surname))
            {
                try
                {
                    Actor founder = World.world?.units?.get(clan.data.founder_actor_id);
                    if (founder != null) surname = ExtractSurname(ActorAccess.GetRawName(founder));
                    if (string.IsNullOrEmpty(surname)) surname = ExtractSurname(clan.data?.founder_actor_name);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(surname)) surname = currentSurname;
            if (!currentLooksPlayerEdited && !string.IsNullOrEmpty(surname)
                && !string.IsNullOrEmpty(currentSurname) && currentSurname != surname)
            {
                RenameClanSurname(clan, currentSurname, surname);
            }
            if (!string.IsNullOrEmpty(surname)) WritePersistentValue(clan.data, ClanSurnameKey, surname);
            return surname;
        }

        /// <summary>按氏族权威姓同步一个成员；保留其名字部分和玩家改过的名字。</summary>
        /// <summary>阶段3C：写游牧全名（氏族姓·本名）到权威字段与 data.name。</summary>
        private void ApplyNomadFullName(Actor actor, string clanSurname, string given)
        {
            string full = clanSurname + "·" + given;
            _actorNames[actor] = full;
            _usedPersonNames.Add(full);
            ActorAccess.SetString(actor, ActorSurnameKey, clanSurname);
            ActorAccess.SetString(actor, SharedFamilyNameKey, clanSurname);
            ActorAccess.SetString(actor, ActorNameKey, full);
            ActorAccess.SetName(actor, full);
        }

        /// <summary>阶段3C：游牧名查重选取（池耗尽则允许重复——世界同类对象远小于池容）。</summary>
        private string PickUniqueNomad(string[] pool, System.Collections.Generic.ICollection<string> used)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                string cand = Pick(pool);
                bool dup = false;
                foreach (var u in used) { if (u == cand) { dup = true; break; } }
                if (!dup) return cand;
            }
            return Pick(pool);
        }

        /// <summary>阶段3C：游牧汗号年号（历代天命记录用）。</summary>
        public string GetNomadicEraName()
        {
            return Pick(NomadicEraNames);
        }

        /// <summary>阶段3C：游牧家族堂名（外部 FamilyRegistry 用；无则空）。</summary>
        public string GetNomadicFamilyHall()
        {
            return Pick(NomadicFamilyHalls);
        }

        /// <summary>阶段3H-2：游牧家族名显示规整——剥离旧档误拼的东方堂号/支派尾缀（"乌珠穆沁部·积善堂"→"乌珠穆沁部"）。</summary>
        public string NormalizeNomadFamilyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            int i = name.IndexOf('·');
            if (i <= 0) return name;
            string tail = name.Substring(i + 1);
            if (Array.IndexOf(NomadicFamilyHalls, tail) >= 0) return name;   // 新版草原堂名保留
            string[] eastBranch = { "东支", "西支", "南支", "北支", "长房", "二房", "三房", "季房" };
            if (Array.IndexOf(eastBranch, tail) >= 0) return name.Substring(0, i);
            try
            {
                if (_config != null && _config.familyHallNames != null)
                    foreach (string h in _config.familyHallNames)
                        if (tail == h) return name.Substring(0, i);
            }
            catch { }
            return name;
        }

        /// <summary>阶段3C：游牧亚种名（外部亚种命名用）。</summary>
        public string GetNomadicSubspeciesName()
        {
            return Pick(NomadicSubspeciesNames);
        }

        /// <summary>阶段3H-22d：本名合法性——体制振荡时代多层姓堆叠（皇甫裴酒泉酒泉契苾氏部…）
        /// 的根因是 ExtractSurname 每次只剥一层姓，剩余整体被当本名拼回。本名校验：
        /// 东方 1~3 个汉字、游牧 1~4 个；含 ·/氏/部/族 尾或再含姓氏池姓 = 污染，弃用换新。</summary>
        private bool IsCleanGiven(string g, bool nomad)
        {
            if (string.IsNullOrEmpty(g)) return false;
            int max = nomad ? 4 : 3;
            if (g.Length > max) return false;
            if (g.IndexOf('·') >= 0) return false;
            foreach (char c in g) if (!ContainsCjk(c.ToString())) return false;
            if (g.EndsWith("氏") || g.EndsWith("部") || g.EndsWith("族") || g.EndsWith("家")) return false;
            return true;
        }

        private void ApplySurname(Actor actor, string surname)
        {
            if (actor == null || string.IsNullOrEmpty(surname)) return;
            string raw = ActorAccess.GetRawName(actor);
            if (string.IsNullOrWhiteSpace(raw)) raw = GetTrackedName(actor);
            string oldSurname = GetPreferredSurname(actor);
            if (string.IsNullOrEmpty(oldSurname)) oldSurname = ExtractSurname(raw);

            // 名制按种族：游牧名制——保留·后本名，重组为新氏族姓·本名
            int ndot = (raw ?? "").IndexOf('·');
            if (ndot >= 0)
            {
                if (IsNomadicActor(actor))
                {
                    string nGiven = raw.Substring(ndot + 1).Trim();
                    if (!IsCleanGiven(nGiven, nomad: true))
                    {
                        bool mm = true;
                        try { mm = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                        nGiven = Pick(mm ? NomadicMaleGiven : NomadicFemaleGiven);   // 污染本名弃用换新
                    }
                    ApplyNomadFullName(actor, surname, nGiven);
                    return;
                }
                // 阶段3H-14：东方名制却带·（跨族旧档污染）——取·后本名走东方重组，格式强制归位
                raw = raw.Substring(ndot + 1).Trim();
                if (string.IsNullOrWhiteSpace(raw)) raw = null;
            }

            string given = raw ?? "";
            if (!string.IsNullOrEmpty(oldSurname) && given.StartsWith(oldSurname) && given.Length > oldSurname.Length)
                given = given.Substring(oldSurname.Length);
            else
            {
                string parsed = ExtractSurname(given);
                if (!string.IsNullOrEmpty(parsed) && given.StartsWith(parsed) && given.Length > parsed.Length)
                    given = given.Substring(parsed.Length);
            }
            // 阶段3H-14 根修：名制只看种族（人类=姓+名，兽人=氏族姓·本名）。
            // 旧"姓∈游牧池也算游牧名制"是软信号——东方国兽人姓了游牧姓就被写成·格式，
            // 游牧国人姓了汉姓就被写成东方名，同族混出"乔·马哈木/乔声/乔蒲黄"。
            bool nomadNaming = IsNomadicActor(actor);
            if (nomadNaming)
            {
                if (!IsCleanGiven(given, nomad: true)) given = "";
                if (string.IsNullOrEmpty(given))
                {
                    bool m = true;
                    try { m = ActorAccess.GetSex(actor) == ActorSex.Male; } catch { }
                    given = Pick(m ? NomadicMaleGiven : NomadicFemaleGiven);
                }
                ApplyNomadFullName(actor, surname, given);
                return;
            }
            // 阶段3H-22d：东方本名校验——剥一层姓后仍超长/含姓氏层=污染，弃用换新
            // （旧代码把"裴酒泉酒泉契苾氏部…"整段当本名拼回=雪球式堆叠）
            if (!IsCleanGiven(given, nomad: false)) given = "";
            if (string.IsNullOrEmpty(given)) given = Pick(_config.neutralGiven);

            string updated = surname + given;
            ActorAccess.SetString(actor, ActorSurnameKey, surname);
            ActorAccess.SetString(actor, SharedFamilyNameKey, surname);
            _actorNames[actor] = updated;
            _usedPersonNames.Add(updated);
            ActorAccess.SetString(actor, ActorNameKey, updated);
            ActorAccess.SetName(actor, updated);
        }

        /// <summary>同步氏族全体活人成员同姓；exclude 仅用于手动改姓触发者，仍会写回其权威字段。</summary>
        public void SynchronizeClanSurname(Clan clan, string surname = null, Actor exclude = null)
        {
            EnsureLoaded();
            if (clan == null) return;
            long clanId;
            try { clanId = clan.getID(); } catch { return; }
            if (_surnameSyncGuards.Contains(clanId)) return;

            // 先观察所有已追踪成员的真实数据名，避免年度同步先于 UI 读取而吞掉玩家刚做的改姓。
            try
            {
                if (clan.units != null)
                    foreach (Actor member in clan.units)
                        if (member != null && member.isAlive()) GetTrackedName(member);
            }
            catch { }
            if (string.IsNullOrEmpty(surname)) surname = GetClanSurname(clan);
            else
            {
                string currentSurname = GetClanSurname(clan);
                if (!string.IsNullOrEmpty(currentSurname)) surname = currentSurname;
            }
            if (string.IsNullOrEmpty(surname)) return;

            _surnameSyncGuards.Add(clanId);
            try
            {
                WritePersistentValue(clan.data, ClanSurnameKey, surname);
                var seen = new HashSet<long>();
                try
                {
                    if (clan.units != null)
                    {
                        foreach (Actor member in clan.units)
                        {
                            if (member == null || !member.isAlive() || !seen.Add(member.getID())) continue;
                            ApplySurname(member, surname);
                        }
                    }
                }
                catch { }

                // Clan.units 处于 dirty 状态时可能暂时不完整；从全图再兜底一次。
                try
                {
                    var all = World.world?.units?.getSimpleList();
                    if (all != null)
                    {
                        foreach (Actor member in all)
                        {
                            if (member == null || !member.isAlive() || member.clan != clan || !seen.Add(member.getID())) continue;
                            ApplySurname(member, surname);
                        }
                    }
                }
                catch { }
                if (exclude != null) ApplySurname(exclude, surname);
            }
            finally { _surnameSyncGuards.Remove(clanId); }
        }

        /// <summary>保留郡望/堂号/支派文本，只替换正式氏族名中的姓。</summary>
        /// <summary>
        /// P1-7c（2026-08-23）：剥离姓氏尾缀的"氏/部"（循环至稳定——防"X氏氏"式嵌套输入）。
        /// 实锤根因：玩家在改名框输入"訾氏"这类带后缀姓 → RenameClanSurname 拼接产出"敦煌訾氏氏"。
        /// 供改姓链路/氏族改名统一净化，public 供单测。
        /// </summary>
        public static string StripClanSuffix(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            while (s.Length > 1 && (s.EndsWith("氏") || s.EndsWith("部")))
                s = s.Substring(0, s.Length - 1);
            return s;
        }

        private void RenameClanSurname(Clan clan, string oldSurname, string newSurname)
        {
            if (clan == null || string.IsNullOrEmpty(newSurname)) return;
            // P1-7c 入口净化：剥尾缀防"訾氏"+"氏"=敦煌訾氏氏
            newSurname = StripClanSuffix(newSurname);
            oldSurname = StripClanSuffix(oldSurname);
            string current = clan.data?.name ?? "";
            string renamed = current;
            if (!string.IsNullOrEmpty(oldSurname) && current.Contains(oldSurname))
                renamed = ReplaceClanSurname(current, oldSurname, newSurname);
            else if (string.IsNullOrWhiteSpace(current)) renamed = newSurname + "氏";
            else renamed = GenerateClanName(newSurname, "");
            clan.data.name = renamed;
            _clanNames[clan] = renamed;
            WritePersistentName(clan.data, renamed);
            WritePersistentValue(clan.data, ClanSurnameKey, newSurname);
        }

        private static string ReplaceLast(string source, string oldValue, string newValue)
        {
            int index = source.LastIndexOf(oldValue, StringComparison.Ordinal);
            return index < 0 ? source : source.Substring(0, index) + newValue + source.Substring(index + oldValue.Length);
        }

        private static string ReplaceClanSurname(string source, string oldSurname, string newSurname)
        {
            if (string.IsNullOrEmpty(source)) return source ?? "";
            int shi = source.IndexOf('氏');
            int searchEnd = shi > 0 ? shi : source.Length;
            int index = source.LastIndexOf(oldSurname, searchEnd - 1, StringComparison.Ordinal);
            if (index < 0) return ReplaceLast(source, oldSurname, newSurname);
            return source.Substring(0, index) + newSurname + source.Substring(index + oldSurname.Length);
        }

        /// <summary>
        /// 出生/父母关系建立后的即时继承：王室母亲优先于父系；其次王室父亲；
    }
}
