using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public enum CivilizationType
    {
        Eastern = 0,
        Nomadic = 1
    }

    /// <summary>
    /// 国家体制唯一权威源。人类固定东方、兽人固定游牧；其他智慧种族按世界与种族生成
    /// 全图共用默认值。国家建立后锁定，普通继承不改；只有叛乱转正/明确篡位入口可重绑。
    /// </summary>
    public sealed class KingdomCivilizationService : ISnapshotSection<KingdomCivilizationService.CivilizationState>
    {
        public static KingdomCivilizationService Instance { get; } = new KingdomCivilizationService();

        private readonly Dictionary<string, CivilizationType> _kingdomTypes = new Dictionary<string, CivilizationType>();
        private readonly Dictionary<string, string> _foundingSpecies = new Dictionary<string, string>();
        private readonly Dictionary<string, CivilizationType> _speciesDefaults = new Dictionary<string, CivilizationType>();

        private KingdomCivilizationService() { }

        public static string GetActorSpeciesId(Actor actor)
        {
            try
            {
                string id = actor?.asset?.id;
                return string.IsNullOrEmpty(id) ? "" : id.ToLowerInvariant();
            }
            catch { return ""; }
        }

        public CivilizationType GetSpeciesDefault(string speciesId)
        {
            speciesId = NormalizeSpecies(speciesId);
            if (speciesId == "human") return CivilizationType.Eastern;
            if (speciesId == "orc") return CivilizationType.Nomadic;
            if (_speciesDefaults.TryGetValue(speciesId, out var saved)) return saved;

            string seed = (ZhuluSaveCore.Instance.WorldId ?? "") + "|" + speciesId + "|zlr_civilization";
            var generated = (StableHash(seed) & 1) == 0 ? CivilizationType.Eastern : CivilizationType.Nomadic;
            _speciesDefaults[speciesId] = generated;
            return generated;
        }

        /// <summary>只影响该种族以后新建国家；人类和兽人的固定绑定不可修改。</summary>
        public bool SetSpeciesDefaultForFuture(string speciesId, CivilizationType type)
        {
            speciesId = NormalizeSpecies(speciesId);
            if (string.IsNullOrEmpty(speciesId) || speciesId == "human" || speciesId == "orc") return false;
            _speciesDefaults[speciesId] = type;
            return true;
        }

        public CivilizationType Get(Kingdom kingdom)
        {
            if (kingdom == null) return CivilizationType.Eastern;
            string id = kingdom.id.ToString();
            if (_kingdomTypes.TryGetValue(id, out var type))
            {
                // 阶段3E-2 最终防线：缓存与 human/orc 固定硬绑定矛盾时即时自愈（不待读档）——
                // 无论污染来自旧档残留/异序锁定/篡位旧规则，人类国永远东方、兽人国永远游牧
                var pin = GetFixedSpeciesPin(kingdom);
                if (pin.HasValue && pin.Value != type)
                {
                    type = pin.Value;
                    _kingdomTypes[id] = type;
                    _foundingSpecies[id] = GetMajoritySpecies(kingdom);
                    ZhuluContext.Instance.LogWarning("[Civilization] 即时自愈: " + (kingdom.name ?? id) + " 缓存" + (type == CivilizationType.Eastern ? "Nomadic" : "Eastern") + "与固定种矛盾 → " + type);
                    // 阶段3H-22c：体制翻转立即重写该国名（东方/游牧命名体系切换——
                    // 不即时重写的话旧体制名会残留到年度同步，期间国名张冠李戴）
                    try
                    {
                        // GetStoredName 按 id+体制权威算出应有名，直接落 data.name（叛军命名国由该链自行跳过）
                        string expected = KingdomExt.GetStoredName(id, kingdom.data.name ?? "");
                        if (!string.IsNullOrEmpty(expected)) kingdom.data.name = expected;
                    }
                    catch { }
                }
                return type;
            }

            string species = DetectKingdomSpecies(kingdom);
            type = GetSpeciesDefault(species);
            var pin0 = GetFixedSpeciesPin(kingdom);
            if (pin0.HasValue) type = pin0.Value; // 首判即按主体种硬绑定
            _kingdomTypes[id] = type;
            _foundingSpecies[id] = GetMajoritySpecies(kingdom);
            ZhuluContext.Instance.LogInfo("[Civilization] 首判: " + (kingdom.name ?? id) + " 国君种=" + species + " 主体种=" + _foundingSpecies[id] + " → " + type);
            return type;
        }

        /// <summary>阶段3E-2：主体种族的固定绑定（human=Eastern/orc=Nomadic；其他种 null=不干预）。
        /// 阶段3H-22c 迟滞修复：主体种必须占统计样本的 ≥60% 且样本 ≥4 才判定——
        /// 50:50 混编国（战争吞并/联姻）主体种随战斗死亡来回翻转，曾导致体制缓存
        /// Eastern↔Nomadic 每帧振荡（日志8连翻转实锤），体制振荡又触发改名链=两国国名互换。</summary>
        private static CivilizationType? GetFixedSpeciesPin(Kingdom kingdom)
        {
            try
            {
                if (kingdom?.units == null || kingdom.units.Count < 4) return null;
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                int total = 0;
                foreach (Actor u in kingdom.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    string sid = GetActorSpeciesId(u);
                    if (string.IsNullOrEmpty(sid)) continue;
                    counts.TryGetValue(sid, out int n);
                    counts[sid] = ++n;
                    total++;
                }
                if (total < 4) return null;
                string best = ""; int bestN = 0;
                foreach (var kv in counts)
                    if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                if (bestN * 10 < total * 6) return null;   // 明确多数（≥60%）才固定绑定
                if (best == "human") return CivilizationType.Eastern;
                if (best == "orc") return CivilizationType.Nomadic;
                return null;
            }
            catch { return null; }
        }

        public bool IsEastern(Kingdom kingdom) => Get(kingdom) == CivilizationType.Eastern;
        public bool IsNomadic(Kingdom kingdom) => Get(kingdom) == CivilizationType.Nomadic;

        public string GetFoundingSpecies(Kingdom kingdom)
        {
            if (kingdom == null) return "";
            string id = kingdom.id.ToString();
            if (!_foundingSpecies.TryGetValue(id, out string species))
            {
                Get(kingdom);
                _foundingSpecies.TryGetValue(id, out species);
            }
            return species ?? "";
        }

        /// <summary>普通建国入口；重复调用不会覆盖现存国家已锁定体制。</summary>
        public void LockNewKingdom(Kingdom kingdom, Actor founder)
        {
            if (kingdom == null) return;
            string id = kingdom.id.ToString();
            if (_kingdomTypes.ContainsKey(id)) return;
            string species = GetActorSpeciesId(founder);
            if (string.IsNullOrEmpty(species)) species = DetectKingdomSpecies(kingdom);
            _foundingSpecies[id] = species;
            _kingdomTypes[id] = GetSpeciesDefault(species);
            var lockPin = GetFixedSpeciesPin(kingdom);
            if (lockPin.HasValue) _kingdomTypes[id] = lockPin.Value; // 阶段3E-2：建国锁定过固定种防线
            ZhuluContext.Instance.LogInfo($"[Civilization] {kingdom.name} 建国锁定: {species} / {_kingdomTypes[id]}");
        }

        /// <summary>叛乱成功转正或明确篡位入口；普通继承严禁调用。
        /// 阶段3E 根修（用户实锤"人类国变游牧"）：转制按【国民主体种族多数决】而非篡位者个人种族——
        /// 兽人窃据人类国君位不再把整个人类国拖入游牧体制；只有国民真以兽人为主体才转制。</summary>
        public CivilizationType RebindByRuler(Kingdom kingdom, Actor ruler, string reason)
        {
            if (kingdom == null) return CivilizationType.Eastern;
            string species = GetMajoritySpecies(kingdom);
            if (string.IsNullOrEmpty(species)) species = GetActorSpeciesId(ruler);
            if (string.IsNullOrEmpty(species)) species = DetectKingdomSpecies(kingdom);
            CivilizationType target = GetSpeciesDefault(species);
            string id = kingdom.id.ToString();
            CivilizationType old = Get(kingdom);
            _kingdomTypes[id] = target;
            _foundingSpecies[id] = species;
            if (old != target)
                ZhuluContext.Instance.LogInfo($"[Civilization] {kingdom.name} 体制转换: {old} → {target}（{reason}，{species}）");
            return target;
        }



        // ---- batch10-A1: schema v5 section ----
        public sealed class CivilizationState
        {
            public Dictionary<string, int> KingdomTypes = new Dictionary<string, int>();
            public Dictionary<string, string> FoundingSpecies = new Dictionary<string, string>();
            public Dictionary<string, int> SpeciesDefaults = new Dictionary<string, int>();
        }
        public string SectionName => "civilization";
        public Dictionary<string, CivilizationState> CaptureStates()
        {
            var st = new CivilizationState();
            foreach (var kv in _kingdomTypes)
            {
                st.KingdomTypes[kv.Key] = (int)kv.Value;
                _foundingSpecies.TryGetValue(kv.Key, out string sp);
                st.FoundingSpecies[kv.Key] = sp ?? "";
            }
            foreach (var kv in _speciesDefaults) st.SpeciesDefaults[kv.Key] = (int)kv.Value;
            return new Dictionary<string, CivilizationState> { ["__global__"] = st };
        }
        public void ApplyStates(Dictionary<string, CivilizationState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            ResetRuntime();
            if (st.KingdomTypes != null)
                foreach (var kv in st.KingdomTypes)
                {
                    if (kv.Value < 0 || kv.Value > 1) continue;
                    _kingdomTypes[kv.Key] = (CivilizationType)kv.Value;
                    st.FoundingSpecies.TryGetValue(kv.Key, out string sp);
                    _foundingSpecies[kv.Key] = NormalizeSpecies(sp ?? "");
                }
            if (st.SpeciesDefaults != null)
                foreach (var kv in st.SpeciesDefaults)
                    if (kv.Value >= 0 && kv.Value <= 1) _speciesDefaults[NormalizeSpecies(kv.Key)] = (CivilizationType)kv.Value;
            // v5 无旧档语义：空数据仍走存量世界迁移兜底 + 固定种族矛盾自愈（与 v4 行为一致）
            if (_kingdomTypes.Count == 0) MigrateLegacyExistingWorld();
            HealFixedSpeciesConflicts();
        }

        public void MigrateExistingWorld()
        {
            MigrateLegacyExistingWorld();
        }

        private void MigrateLegacyExistingWorld()
        {
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                string id = k.id.ToString();
                if (_kingdomTypes.ContainsKey(id)) continue;
                string species = DetectKingdomSpecies(k);
                _foundingSpecies[id] = species;
                _kingdomTypes[id] = species == "orc" ? CivilizationType.Nomadic : CivilizationType.Eastern;
            }
        }

        public void ResetRuntime()
        {
            _kingdomTypes.Clear();
            _foundingSpecies.Clear();
            _speciesDefaults.Clear();
        }

        /// <summary>阶段3E：国民主体种族（units 计数众数；空国回退国君种族）。</summary>
        public static string GetMajoritySpecies(Kingdom kingdom)
        {
            try
            {
                if (kingdom?.units != null && kingdom.units.Count > 0)
                {
                    var counts = new Dictionary<string, int>();
                    string best = ""; int bestN = 0;
                    foreach (Actor u in kingdom.units)
                    {
                        if (u == null || !u.isAlive()) continue;
                        string sid = GetActorSpeciesId(u);
                        if (string.IsNullOrEmpty(sid)) continue;
                        counts.TryGetValue(sid, out int n);
                        counts[sid] = ++n;
                        if (n > bestN) { bestN = n; best = sid; }
                    }
                    if (!string.IsNullOrEmpty(best)) return best;
                }
            }
            catch { }
            return GetActorSpeciesId(kingdom?.king);
        }

        /// <summary>阶段3E：读档自愈——缓存体制与 human/orc 固定绑定矛盾时按主体种修正
        /// （修复兽人篡位时代的污染存量；不影响其他种族的玩家自定义默认）。</summary>
        public void HealFixedSpeciesConflicts()
        {
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                int fixedCount = 0;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    string id = k.id.ToString();
                    if (!_kingdomTypes.TryGetValue(id, out var cached)) continue;
                    string majority = GetMajoritySpecies(k);
                    if (string.IsNullOrEmpty(majority)) continue;
                    CivilizationType? pin = majority == "human" ? CivilizationType.Eastern
                        : majority == "orc" ? (CivilizationType?)CivilizationType.Nomadic : null;
                    if (pin.HasValue && cached != pin.Value)
                    {
                        _kingdomTypes[id] = pin.Value;
                        _foundingSpecies[id] = majority;
                        fixedCount++;
                        ZhuluContext.Instance.LogInfo("[Civilization] 自愈: " + (k.name ?? id) + " 主体种" + majority + " → " + pin.Value + "（原缓存" + cached + "）");
                    }
                }
                if (fixedCount > 0) ZhuluContext.Instance.LogInfo("[Civilization] 读档体制自愈完成: 修正 " + fixedCount + " 国");
            }
            catch { }
        }

        private static string DetectKingdomSpecies(Kingdom kingdom)
        {
            string species = GetActorSpeciesId(kingdom?.king);
            if (!string.IsNullOrEmpty(species)) return species;
            try
            {
                if (kingdom?.units != null)
                    foreach (Actor unit in kingdom.units)
                    {
                        species = GetActorSpeciesId(unit);
                        if (!string.IsNullOrEmpty(species)) return species;
                    }
            }
            catch { }
            return "human";
        }

        private static string NormalizeSpecies(string value) => (value ?? "").Trim().ToLowerInvariant();

        private static int StableHash(string text)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < (text?.Length ?? 0); i++) hash = hash * 31 + text[i];
                return hash;
            }
        }
    }
}
