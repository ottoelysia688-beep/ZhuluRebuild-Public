using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：工具（随机/持久化/兜底） ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        /// <summary>战争命名主词库（2026-08-19 扩充：每类 6-8 式，先秦古风）。</summary>
        private string GenerateWarName(string a, string d, string typeId)
        {
            if (a == "无名" && d == "无名") return "";
            switch (typeId)
            {
                case "zlr_total_war": // 全面战争（灭国级）
                    return Pick(new[] { a + "灭" + d + "之战", a + "举国伐" + d, a + d + "覆灭之战", "天下讨" + d + "之役",
                        a + "问鼎" + d + "之师", a + d + "决战中原", a + "倾国之役" });
                case "zlr_claim_war": // 宣称战争（索土）
                    return Pick(new[] { a + "伐" + d + "之战", a + "索土之役", d + "边境之战", a + "请城之役",
                        a + d + "争城之战", a + "启衅于" + d, a + d + "疆埸之争" });
                case "zlr_vassal_war": // 附庸战争（征服从属）
                    return Pick(new[] { a + "征" + d + "之役", d + "臣服之战", a + d + "宗属之争", a + "服" + d + "之师",
                        a + "慑服" + d + "之役", d + "拒命之战", a + d + "君臣之衅" });
                case "zlr_independence_war": // 独立战争（脱离宗主）
                    return Pick(new[] { a + "独立之战", a + "脱藩之役", a + d + "离盟之争", a + "去宗自立之战",
                        a + "举义去宗之役", a + "自王之战", a + "叛宗兴兵" });
                case "zlr_mandate_war": // 夺天命之战（问鼎）
                    return Pick(new[] { a + "争天之战", a + "伐" + d + "承天之役", a + d + "天统之争",
                        a + "会盟问鼎之师", a + "奉天讨" + d + "之役", a + d + "鼎革之战" });
                case "rebellion":
                case "inspire": // 叛乱（城主兵变）
                    return Pick(new[] { d + "平乱之战", a + "起兵之役", d + "靖难之战", a + "兵变之祸",
                        d + "戡乱之役", a + "倡乱举兵", d + "讨逆之战" });
                case "spite": // 原版仇恨战争
                    return Pick(new[] { a + "讨" + d + "之战", a + d + "决战", a + "灭" + d + "之役",
                        a + "泄愤兴师", a + d + "交恶之衅", a + "攻" + d + "之役" });
                default: // 普通战争
                    return Pick(new[] { a + "伐" + d + "之战", d + "保卫战", a + d + "争衡之役", a + "征" + d + "之战",
                        a + "攻" + d + "之役", a + d + "构兵", a + "兴师犯" + d, a + d + "交兵之衅" });
            }
        }

        // ───────────────────────────── 辅助方法 ─────────────────────────────

        private string GetFounderHome(Actor founder)
        {
            if (founder == null) return "";
            string home = "";
            try { home = founder.city?.name; } catch { }
            if (string.IsNullOrEmpty(home)) try { home = founder.kingdom?.data?.name; } catch { }
            return CleanPlaceName(home);
        }

        private string GetRealmShortName(Kingdom kingdom)
        {
            if (kingdom == null) return "无名";
            string name = "";
            try { name = KingdomExt.GetBaseName(kingdom.id.ToString(), kingdom.data?.name ?? kingdom.name); } catch { }
            if (string.IsNullOrEmpty(name)) try { name = kingdom.data?.name ?? kingdom.name; } catch { }
            name = CleanPlaceName(name);
            return string.IsNullOrEmpty(name) ? "无名" : name;
        }

        private static string CleanPlaceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string result = name;
            string[] suffixes = { "帝国", "公国", "侯国", "伯国", "子国", "王国", "郡国", "附庸", "朝", "国", "府", "郡", "州", "城", "邑", "镇", "塞", "堡" };
            foreach (string suffix in suffixes)
            {
                if (result.EndsWith(suffix) && result.Length > suffix.Length)
                {
                    result = result.Substring(0, result.Length - suffix.Length);
                    break;
                }
            }
            int dash = result.IndexOf('-');
            if (dash > 0) result = result.Substring(0, dash);
            return result;
        }

        private string ExtractSurname(string source)
        {
            if (string.IsNullOrEmpty(source)) return "";
            // 阶段3C：游牧名制（氏族·本名）——·之前即氏族姓
            int dot = source.IndexOf('·');
            if (dot > 0) return source.Substring(0, dot).Trim();
            string clean = TitleSuffixPatches.StripKingSuffix(source);
            string[] clanSuffixes = { "世家", "宗族", "氏族", "家族", "氏", "家", "族", "门" };
            foreach (string suffix in clanSuffixes)
            {
                if (clean.EndsWith(suffix) && clean.Length > suffix.Length)
                {
                    clean = clean.Substring(0, clean.Length - suffix.Length);
                    break;
                }
            }
            // P1-7：索引化匹配替代 O(池) 双向全扫；语义不变（复姓先于单姓、同池取最小序号命中）
            EnsureSurnameIndexes();
            string hitCompound = MatchSurnamePool(_compoundByFirst, _compoundByLast, clean);
            if (hitCompound != null) return hitCompound;
            return MatchSurnamePool(_singleByFirst, _singleByLast, clean) ?? "";
        }

        /// <summary>P1-7：懒构建姓氏首/尾字索引（词库加载后内容不变，只建一次）。</summary>
        private void EnsureSurnameIndexes()
        {
            if (_surnameIndexesBuilt) return;
            _surnameIndexesBuilt = true;
            IndexSurnames(_config.compoundSurnames, _compoundByFirst, _compoundByLast);
            IndexSurnames(_config.surnames, _singleByFirst, _singleByLast);
        }

        private static void IndexSurnames(string[] pool, Dictionary<char, List<SurnameEntry>> byFirst, Dictionary<char, List<SurnameEntry>> byLast)
        {
            if (pool == null) return;
            for (int i = 0; i < pool.Length; i++)
            {
                string s = pool[i];
                if (string.IsNullOrEmpty(s)) continue;
                AddSurnameIndex(byFirst, s[0], s, i);
                AddSurnameIndex(byLast, s[s.Length - 1], s, i);
            }
        }

        private static void AddSurnameIndex(Dictionary<char, List<SurnameEntry>> map, char key, string name, int idx)
        {
            List<SurnameEntry> list;
            if (!map.TryGetValue(key, out list)) { list = new List<SurnameEntry>(); map[key] = list; }
            list.Add(new SurnameEntry { Name = name, Idx = idx });
        }

        /// <summary>
        /// P1-7：在首字桶+尾字桶的并集候选中找匹配，取最小池序号——
        /// 与旧实现 foreach(池){ StartsWith||EndsWith } 的首个命中语义等价。
        /// </summary>
        private static string MatchSurnamePool(
            Dictionary<char, List<SurnameEntry>> byFirst,
            Dictionary<char, List<SurnameEntry>> byLast,
            string clean)
        {
            if (string.IsNullOrEmpty(clean)) return null;
            int bestIdx = int.MaxValue;
            string best = null;
            if (byFirst.TryGetValue(clean[0], out var firstBucket))
            {
                foreach (var e in firstBucket)
                {
                    if (e.Idx < bestIdx && clean.StartsWith(e.Name)) { bestIdx = e.Idx; best = e.Name; }
                }
            }
            if (byLast.TryGetValue(clean[clean.Length - 1], out var lastBucket))
            {
                foreach (var e in lastBucket)
                {
                    if (e.Idx < bestIdx && clean.EndsWith(e.Name)) { bestIdx = e.Idx; best = e.Name; }
                }
            }
            return best;
        }

        /// <summary>
        /// 从正式氏族名解析姓。必须先截到第一个“氏”，再从郡望尾部匹配姓；
        /// 例如“琅琊王氏·弘农堂”应取“王”，不能因字符串以“琅”开头误判为琅姓。
        /// </summary>
        private string ExtractClanSurname(string source)
        {
            if (string.IsNullOrEmpty(source)) return "";
            string clean = TitleSuffixPatches.StripKingSuffix(source);
            // 阶段3H-7：游牧氏族名优先判——"{姓}·{地名}部·{堂}"或"{姓}部"，首段（去尾"部"）即氏族姓。
            // 东方名首段必含"氏"（琅琊王氏）；游牧名首段绝无"氏"。不做此判会把尾段堂号
            // （如"金帐宗"）按东方尾缀匹配误提为姓，导致全族被改成"宗·本名"。
            int nDot = clean.IndexOf('·');
            string firstSeg = nDot > 0 ? clean.Substring(0, nDot).Trim() : "";
            if (firstSeg.Length > 0 && firstSeg.IndexOf('氏') < 0)
            {
                string cand = firstSeg.EndsWith("部") && firstSeg.Length > 1 ? firstSeg.Substring(0, firstSeg.Length - 1) : firstSeg;
                if (System.Array.IndexOf(NomadicSurnames, cand) >= 0) return cand;
                return cand;   // 池外（玩家改姓等）：多段名仍取首段——尾段是地名/堂号，绝不作姓
            }
            if (nDot < 0 && clean.EndsWith("部") && clean.Length > 1)
            {
                string cand = clean.Substring(0, clean.Length - 1);
                if (System.Array.IndexOf(NomadicSurnames, cand) >= 0) return cand;
            }
            int shi = clean.IndexOf('氏');
            if (shi > 0) clean = clean.Substring(0, shi);

            // 复姓必须先匹配，避免“司马”被单姓“司”抢先。
            foreach (string surname in _config.compoundSurnames)
                if (!string.IsNullOrEmpty(surname) && clean.EndsWith(surname)) return surname;
            foreach (string surname in _config.surnames)
                if (!string.IsNullOrEmpty(surname) && clean.EndsWith(surname)) return surname;
            return ExtractSurname(source);
        }

        private bool IsCityNameUsed(string name)
        {
            if (_sessionCityNames.Contains(name)) return true;
            try
            {
                if (World.world?.kingdoms?.list == null) return false;
                foreach (Kingdom kingdom in World.world.kingdoms.list)
                {
                    if (kingdom?.cities == null) continue;
                    foreach (City city in kingdom.cities)
                        if (city?.data?.name == name) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>同一历史城市的异名归组；一个世界内只取其一，避免金陵/建康/秣陵同时出现。</summary>
        private static string GetCityAliasKey(string name)
        {
            switch (name)
            {
                case "金陵": case "建康": case "秣陵": case "建业": case "江宁": case "白下": return "南京古称";
                case "洛阳": case "洛邑": return "洛阳古称";
                case "蓟": case "蓟城": case "蓟州": case "燕京": return "蓟城古称";
                case "姑苏": case "苏州": case "吴县": return "姑苏古称";
                case "会稽": case "绍兴": return "会稽古称";
                case "彭城": case "徐州": return "彭城古称";
                case "邺城": case "临漳": case "相州": return "邺城古称";
                case "安邑": case "绛城": return "河东古都";
                default: return name;
            }
        }

        private bool IsRealmNameUsed(string name, Kingdom exceptKingdom = null)
        {
            if (_sessionRealmNames.Contains(name))
            {
                if (exceptKingdom == null || !_kingdomBaseNames.TryGetValue(exceptKingdom, out string own) || own != name)
                    return true;
            }
            try
            {
                if (World.world?.kingdoms?.list == null) return false;
                foreach (Kingdom kingdom in World.world.kingdoms.list)
                {
                    if (kingdom == null || ReferenceEquals(kingdom, exceptKingdom)) continue;
                    if (GetRealmShortName(kingdom) == name) return true;
                }
            }
            catch { }
            return false;
        }

        private string UniqueName(Func<string> factory, Func<string, bool> exists, HashSet<string> session, string fallback)
        {
            for (int i = 0; i < 60; i++)
            {
                string candidate = factory();
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (!exists(candidate)) { session.Add(candidate); return candidate; }
            }
            string[] modifiers = { "东", "西", "南", "北", "前", "后", "上", "下", "新", "故", "大", "小" };
            foreach (string modifier in modifiers)
            {
                string candidate = modifier + fallback;
                if (!exists(candidate)) { session.Add(candidate); return candidate; }
            }
            session.Add(fallback);
            return fallback;
        }

        private void PersistKingdomBaseName(Kingdom kingdom, string baseName)
        {
            if (kingdom == null || kingdom.data == null || string.IsNullOrWhiteSpace(baseName)) return;
            _kingdomBaseNames[kingdom] = baseName;
            _sessionRealmNames.Add(baseName);
            KingdomDataExt.SetStr(kingdom, KingdomBaseNameKey, baseName);
            KingdomExt.SetBaseName(kingdom.id.ToString(), baseName);
        }

        private void RehydratePersistentActorsAndKingdoms()
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom kingdom in World.world.kingdoms.list)
                {
                    if (kingdom == null || !kingdom.isAlive()) continue;
                    string baseName = KingdomDataExt.GetStr(kingdom, KingdomBaseNameKey);
                    if (!string.IsNullOrWhiteSpace(baseName))
                        PersistKingdomBaseName(kingdom, baseName);
                    _kingdomObservedNames[kingdom] = kingdom.data?.name ?? "";

                    if (kingdom.cities != null)
                        foreach (City city in kingdom.cities)
                        {
                            if (city == null || _cityNames.ContainsKey(city)) continue;
                            string cityName = ReadPersistentName(city.data);
                            if (!string.IsNullOrWhiteSpace(cityName))
                            {
                                _cityNames[city] = cityName;
                                _sessionCityNames.Add(cityName);
                                string aliasKey = GetCityAliasKey(cityName);
                                if (!string.IsNullOrEmpty(aliasKey)) _sessionCityAliasKeys.Add(aliasKey);
                            }
                        }

                    if (kingdom.units == null) continue;
                    foreach (Actor actor in kingdom.units)
                    {
                        if (actor == null) continue;
                        if (!_actorNames.ContainsKey(actor))
                        {
                            string name = ActorAccess.GetString(actor, ActorNameKey);
                            if (!string.IsNullOrWhiteSpace(name)) _actorNames[actor] = name;
                        }
                        try
                        {
                            Clan clan = actor.clan;
                            if (clan != null && !_clanNames.ContainsKey(clan))
                            {
                                string clanName = ReadPersistentName(clan.data);
                                string currentClanName = clan.data?.name ?? "";
                                // 数据中的当前名可能是玩家改名；当前名优先于旧权威缓存。
                                if (!string.IsNullOrWhiteSpace(currentClanName)) clanName = currentClanName;
                                if (!string.IsNullOrWhiteSpace(clanName))
                                {
                                    _clanNames[clan] = clanName;
                                    _sessionClanNames.Add(clanName);
                                    WritePersistentName(clan.data, clanName);
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 旧档加载即修复，不必等待下一年度：校正“晋戚氏十人仅三人姓戚”、
                // 创始人杨某却生成常氏等历史污染。同步前会先观察玩家手工改名，因此不会把玩家名字改回。
                foreach (Clan clan in new List<Clan>(_clanNames.Keys))
                {
                    try { SynchronizeClanSurname(clan); }
                    catch (Exception ex) { ZhuluContext.Instance.LogWarning($"[ChineseName] 旧档氏族姓修复失败: {ex.Message}"); }
                }

                if (World.world.wars != null)
                    foreach (War war in World.world.wars)
                    {
                        if (war == null || _warNames.ContainsKey(war)) continue;
                        string warName = ReadPersistentName(war.data);
                        if (!string.IsNullOrWhiteSpace(warName))
                        {
                            _warNames[war] = warName;
                            _sessionWarNames.Add(warName);
                        }
                    }
            }
            catch { }
        }

        private void ClearSessionTracking()
        {
            _actorNames.Clear();
            _kingdomBaseNames.Clear();
            _kingdomObservedNames.Clear();
            _cityNames.Clear();
            _clanNames.Clear();
            _familyNames.Clear();
            _warNames.Clear();
            _allianceNames.Clear();
            _cultureNames.Clear();
            _languageNames.Clear();
            _religionNames.Clear();
            _sessionCityNames.Clear();
            _sessionCityAliasKeys.Clear();
            _sessionRealmNames.Clear();
            _sessionClanNames.Clear();
            _sessionWarNames.Clear();
        }

        private string ReadPersistentName(object data)
        {
            return ReadPersistentValue(data, PersistentNameKey);
        }

        private string ReadPersistentValue(object data, string key)
        {
            if (data == null || string.IsNullOrEmpty(key)) return "";
            try
            {
                FieldInfo field = FindField(data.GetType(), "custom_data_string");
                if (field == null) return "";
                object container = field.GetValue(data);
                if (container == null) return "";
                MethodInfo tryGetValue = container.GetType().GetMethod("TryGetValue", new[] { typeof(string), typeof(string).MakeByRefType() });
                if (tryGetValue == null) return "";
                object[] args = { key, null };
                bool found = (bool)tryGetValue.Invoke(container, args);
                return found ? args[1] as string ?? "" : "";
            }
            catch { return ""; }
        }

        private void WritePersistentName(object data, string value)
        {
            WritePersistentValue(data, PersistentNameKey, value);
        }

        private void WritePersistentValue(object data, string key, string value)
        {
            if (data == null || string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) return;
            try
            {
                FieldInfo field = FindField(data.GetType(), "custom_data_string");
                if (field == null) return;
                object container = field.GetValue(data);
                if (container == null)
                {
                    container = Activator.CreateInstance(field.FieldType);
                    field.SetValue(data, container);
                }
                PropertyInfo indexer = container.GetType().GetProperty("Item");
                indexer?.SetValue(container, value, new object[] { key });
            }
            catch { }
        }

        private FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        private string PickDifferent(string[] values, string excluded)
        {
            if (values == null || values.Length == 0) return "";
            for (int i = 0; i < 8; i++)
            {
                string candidate = Pick(values);
                if (candidate != excluded) return candidate;
            }
            foreach (string candidate in values)
                if (!string.IsNullOrEmpty(candidate) && candidate != excluded) return candidate;
            return "";
        }

        private string Pick(string[] values)
        {
            if (values == null || values.Length == 0) return "";
            lock (_nameRandomLock)
                return values[_nameRandom.Next(0, values.Length)] ?? "";
        }

        private double NextDouble()
        {
            lock (_nameRandomLock) return _nameRandom.NextDouble();
        }

        private static int StableIndex(string key, int length)
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

        private void FillFallbacks()
        {
            if (Empty(_config.surnames)) _config.surnames = new[] { "赵", "钱", "孙", "李", "周", "吴", "郑", "王", "陈", "张", "刘", "杨" };
            if (Empty(_config.compoundSurnames)) _config.compoundSurnames = new[] { "欧阳", "司马", "诸葛", "上官", "公孙" };
            if (Empty(_config.maleGiven)) _config.maleGiven = new[] { "安", "承", "景", "昭", "文", "武", "宁", "远" };
            if (Empty(_config.femaleGiven)) _config.femaleGiven = new[] { "婉", "静", "清", "和", "华", "宁", "月", "兰" };
            if (Empty(_config.neutralGiven)) _config.neutralGiven = new[] { "安", "宁", "嘉", "和", "清", "明", "昭", "景" };
            if (Empty(_config.maleNameFirst)) _config.maleNameFirst = _config.maleGiven;
            if (Empty(_config.maleNameSecond)) _config.maleNameSecond = _config.maleGiven;
            if (Empty(_config.femaleNameFirst)) _config.femaleNameFirst = _config.femaleGiven;
            if (Empty(_config.femaleNameSecond)) _config.femaleNameSecond = _config.femaleGiven;
            if (Empty(_config.cityPrefixes)) _config.cityPrefixes = new[] { "安", "宁", "永", "长", "临", "广", "清", "平" };
            if (Empty(_config.cityStems)) _config.cityStems = new[] { "阳", "川", "原", "陵", "泽", "浦", "河", "山" };
            if (Empty(_config.citySuffixes)) _config.citySuffixes = new[] { "城", "邑", "郡", "府", "州", "关" };
            if (Empty(_config.realmNames)) _config.realmNames = new[] { "秦", "楚", "齐", "燕", "赵", "魏", "梁", "宁", "安", "景" };
            if (Empty(_config.clanSuffixes)) _config.clanSuffixes = new[] { "氏", "世家", "宗族", "门", "家" };
            if (Empty(_config.allianceSuffixes)) _config.allianceSuffixes = new[] { "之盟", "会盟", "同盟", "盟约" };
            if (Empty(_config.cultureRoots)) _config.cultureRoots = new[] { "河洛", "关中", "齐鲁", "荆楚", "吴越", "巴蜀" };
            if (Empty(_config.religionRoots)) _config.religionRoots = new[] { "太一", "玄天", "明德", "崇礼", "社稷" };
            if (Empty(_config.kingdomMottos)) _config.kingdomMottos = new[] { "敬天保民", "修德安邦", "守土靖民" };
            if (Empty(_config.clanMottos)) _config.clanMottos = new[] { "诗礼传家", "忠厚继世", "耕读为本" };
            if (Empty(_config.allianceMottos)) _config.allianceMottos = new[] { "同舟共济", "守望相助", "同心戮力" };
        }

        private bool Empty(string[] values) => values == null || values.Length == 0;
    }
}
