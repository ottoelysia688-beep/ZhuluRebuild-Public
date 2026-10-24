using System;
using System.Collections.Generic;
using System.Linq;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 派系管理器（地基骨架——空壳可跑，党争完整规则在阶段2接入）。
    /// 固定 4 党：皇权党/文官派/武官派/太子党（永不灭，无成员=空壳党）。
    /// 自建 X党：权势≥60 可建（阶段2 完整触发，骨架提供结构）。
    /// 成员聚合（骨架基础规则）：
    ///   皇权党 = 皇帝本人（亲信度≥60 与皇帝提拔在阶段2 接入）
    ///   文官派 = 文官身份（IdentityClassifier.Civil）
    ///   武官派 = 武将身份（IdentityClassifier.Military）
    ///   太子党 = 储君 + 同族宗藩（阶段2 加野心家特质聚合）
    /// 影响力 = 成员权势和（骨架阶段用成员数+官职加成占位，阶段2 接 CorruptionManager.Power）。
    /// 旧档不生成派系（用户定案）；新世界首次 Tick 建 4 固定党空壳。
    /// </summary>
    public class FactionManager : ISnapshotSection<FactionManager.FactionsState>
    {
        public static FactionManager Instance { get; } = new FactionManager();

        public enum FactionType { HuangQuan, WenGuan, WuGuan, TaiZi, Custom }

        // ── 2026-08-19 串联重构：党派立场向量轴索引（0~1 浮点）──
        public const int ST_LOYAL = 0;       // 忠君（君权 vs 相权）
        public const int ST_LINEAGE = 1;     // 门第（宗法血缘 vs 寒门庶族）
        public const int ST_LIVELIHOOD = 2;  // 民生（养民 vs 役民）
        public const int ST_WAR = 3;         // 战争（主战 vs 主和）
        public const int ST_REFORM = 4;      // 变法（革新 vs 守旧）
        public const int STANCE_COUNT = 5;

        /// <summary>党派默认立场向量（按类型常量；自党基线不含党魁特质微调——纯计算，单测可测）。</summary>
        public static float[] DefaultStanceByType(FactionType type)
        {
            switch (type)
            {
                case FactionType.HuangQuan: return new float[] { 1.0f, 0.6f, 0.5f, 0.4f, 0.3f };
                case FactionType.TaiZi: return new float[] { 0.8f, 0.9f, 0.5f, 0.3f, 0.4f };
                case FactionType.WenGuan: return new float[] { 0.4f, 0.3f, 0.8f, 0.3f, 0.7f };
                case FactionType.WuGuan: return new float[] { 0.5f, 0.3f, 0.3f, 0.9f, 0.5f };
                default: return new float[] { 0.1f, 0.2f, 0.5f, 0.5f, 0.8f };  // 自党基线：野心家倾向（低忠君·高变法）
            }
        }

        /// <summary>党派默认立场向量（自建党按党魁特质微调；Actor 世界查询隔离见 ApplyLeaderTraits）。</summary>
        public static float[] DefaultStance(FactionType type, Actor leader = null)
        {
            var s = DefaultStanceByType(type);
            if (leader != null) ApplyLeaderTraits(leader, s);
            return s;
        }

        /// <summary>按党魁特质微调自党立场（方法体含 Actor/TraitManager 世界查询——纯 .NET 单测环境不可 JIT）。</summary>
        private static void ApplyLeaderTraits(Actor leader, float[] s)
        {
            try
            {
                if (TraitManager.Instance.HasTrait(leader, TraitManager.TRAIT_AMBITIOUS)) { s[ST_WAR] = 0.7f; s[ST_REFORM] = 0.9f; }
                else if (TraitManager.Instance.HasTrait(leader, TraitManager.TRAIT_WISE_MINISTER)) { s[ST_LIVELIHOOD] = 0.9f; s[ST_REFORM] = 0.6f; s[ST_LOYAL] = 0.3f; }
                else if (TraitManager.Instance.HasTrait(leader, TraitManager.TRAIT_BENEVOLENT)) { s[ST_LIVELIHOOD] = 0.85f; s[ST_WAR] = 0.2f; }
                else if (TraitManager.Instance.HasTrait(leader, TraitManager.TRAIT_TYRANT)) { s[ST_WAR] = 0.85f; s[ST_LOYAL] = 0.3f; }
                else if (TraitManager.Instance.HasTrait(leader, TraitManager.TRAIT_CORRUPT)) { s[ST_LOYAL] = 0.2f; s[ST_LIVELIHOOD] = 0.2f; }
            }
            catch { }
        }

        /// <summary>取党派立场（未初始化按类型补默认；防旧档 null）。</summary>
        public static float[] GetStance(FactionRecord f, Kingdom k = null)
        {
            if (f == null) return new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
            if (f.Stance == null || f.Stance.Length != STANCE_COUNT)
            {
                // World 访问隔离到独立方法：纯 .NET 测试环境（无 UnityEngine）加载失败由 catch 吸收，
                // 走 leader=null 默认立场——生产环境行为不变（先例：VassalRules 因 UnityEngine.Random 不可测）
                Actor leader = null;
                try { leader = ResolveLeaderActor(f.LeaderId); } catch { }
                f.Stance = DefaultStance(f.Type, leader);
            }
            return f.Stance;
        }

        /// <summary>按 ID 查党魁 Actor（找不到/异常返回 null）。</summary>
        private static Actor ResolveLeaderActor(long leaderId)
        {
            try { if (leaderId != 0) return World.world?.units?.get(leaderId); } catch { }
            return null;
        }

        /// <summary>党派某轴立场（0~1；异常回退 0.5）。</summary>
        public static float GetStanceAxis(FactionRecord f, int axis)
        {
            try
            {
                var s = GetStance(f);
                if (axis < 0 || axis >= s.Length) return 0.5f;
                return s[axis];
            }
            catch { return 0.5f; }
        }

        public class FactionRecord
        {
            public string Name;                 // 皇权党/文官派/武官派/太子党/叶党
            public FactionType Type;
            public long LeaderId;
            public List<long> MemberIds = new List<long>();
            public int FoundedYear;
            public bool IsCustom;
            // ── 2026-08-19 串联重构：立场向量 + 账面党势（快照序列化，旧档缺字段自动补默认）──
            public float[] Stance;              // 五轴立场（null=未初始化，GetStance 兜底）
            public float TraditionBonus = 0f;   // 账面党势（传统分量：执政+2/年封顶40，非执政-1/年归零——防成员死亡党势跳水）
            public int RulingSince = 0;         // 本届执政起始年（0=在野；翻转时更新）
        }

        public class KingdomFactions
        {
            public List<FactionRecord> Factions = new List<FactionRecord>();
        }

        private readonly Dictionary<string, KingdomFactions> _states = new Dictionary<string, KingdomFactions>();
        private bool _freshWorld = false; // 新世界标志（ResetForNewWorld 置真）
        /// <summary>测试窥视：新世界标志（批次10-A1 单测断言用）。</summary>
        public bool FreshWorldForTest => _freshWorld;

        private FactionManager() { }

        private bool Enabled => ZhuluConfig.Instance.GroundworkEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        public KingdomFactions GetFactions(string kingdomId)
        {
            _states.TryGetValue(kingdomId, out var st);
            return st;
        }

        public List<FactionRecord> GetAllFactions(string kingdomId)
        {
            var st = GetFactions(kingdomId);
            return st?.Factions ?? new List<FactionRecord>();
        }

        public static string GetFactionName(FactionType type, string leaderName = null)
        {
            switch (type)
            {
                case FactionType.HuangQuan: return "皇权党";
                case FactionType.WenGuan: return "文官派";
                case FactionType.WuGuan: return "武官派";
                case FactionType.TaiZi: return "太子党";
                default: return NameCustomFaction(leaderName);
            }
        }

        // ── 阶段2V：党派显示名按国家位阶（存储 Name 恒为固定名——世仇/安置/史册 key 稳定不受升降等影响）──

        /// <summary>党派显示名：天子→皇权党/太子党，公→公室党/世子党，侯→侯府党/世子党；文武派通用。</summary>
        public static string GetFactionDisplayNameByTier(OfficeManager.KingdomTier tier, FactionType type)
        {
            switch (type)
            {
                case FactionType.HuangQuan:
                    if (tier == OfficeManager.KingdomTier.Tianming) return "皇权党";
                    if (tier == OfficeManager.KingdomTier.Gong) return "公室党";
                    return "侯府党";
                case FactionType.TaiZi:
                    return tier == OfficeManager.KingdomTier.Tianming ? "太子党" : "世子党";
                case FactionType.WenGuan: return "文官派";
                case FactionType.WuGuan: return "武官派";
                default: return "";
            }
        }

        /// <summary>按王国实时位阶取党派显示名（自建党返回存储名）。</summary>
        public static string GetFactionDisplayName(Kingdom k, FactionRecord f)
        {
            if (f == null) return "";
            if (f.Type == FactionType.Custom) return f.Name;
            var tier = GetKingdomTierOf(k);
            if (k != null && KingdomCivilizationService.Instance.IsNomadic(k))
            {
                switch (f.Type)
                {
                    case FactionType.HuangQuan: return tier == OfficeManager.KingdomTier.Tianming ? "王庭派" : "汗庭派";
                    case FactionType.TaiZi: return "继承派";
                    case FactionType.WenGuan: return "长老派";
                    case FactionType.WuGuan: return "军功派";
                }
            }
            string dn = GetFactionDisplayNameByTier(tier, f.Type);
            return string.IsNullOrEmpty(dn) ? f.Name : dn;
        }

        /// <summary>王国当前位阶（天命国=Tianming；其余按有效城数）。</summary>
        public static OfficeManager.KingdomTier GetKingdomTierOf(Kingdom k)
        {
            if (k == null) return OfficeManager.KingdomTier.Hou;
            try
            {
                bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                return OfficeManager.GetTierWithMandate(k, isMandate);
            }
            catch { return OfficeManager.KingdomTier.Hou; }
        }

        /// <summary>自建派系命名：领袖姓 + 党（复姓安全，如「司马党」）。</summary>
        public static string NameCustomFaction(string leaderName)
        {
            if (string.IsNullOrEmpty(leaderName)) return "新党";
            string first = leaderName.Trim();
            if (first.Length == 0) return "新党";
            string[] compounds = { "欧阳", "司马", "诸葛", "上官", "公孙", "夏侯", "东方", "皇甫", "尉迟", "令狐", "宇文", "长孙", "慕容", "司徒", "司空" };
            foreach (string surname in compounds)
                if (first.StartsWith(surname)) return surname + "党";
            return first.Substring(0, 1) + "党";
        }

        // 2026-08-17 清理：删除骨架版 ComputePower/GetRulingFaction（零消费者，且与
        // FactionPoliticsManager.ComputeFactionPower/GetRulingFaction 双算法并存易误用——UI 全部走 Politics 版）。

        /// <summary>党魁（影响力最高成员自动接任——用户定案）。</summary>
        public long DetectLeader(string kingdomId, FactionRecord faction)
        {
            if (faction == null || faction.MemberIds.Count == 0) return 0;
            // 骨架：取权势估算最高的成员（官职加成优先），阶段2 用真实 Power
            long best = faction.MemberIds[0];
            return best;
        }

        // ═══════════════════════ 年度处理 ═══════════════════════

        /// <summary>按国年度处理：建空壳/聚合成员/补党魁。
        /// 阶段2W：党争门槛=侯级以上（与 FactionPoliticsManager 同口径）；叛军无党争；
        /// 位阶不足国清除残留骨架（旧档/旧版曾给小国建党的数据一并清理——存档实锤 62 国全有4党）。
        /// 2026-08-19 修复：旧档同样建四党（原"旧档不生成派系"致公侯国只剩自建党空壳——用户反馈）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            try
            {
                var tier = GetKingdomTierOf(k);
                bool eligible = tier == OfficeManager.KingdomTier.Hou
                    || tier == OfficeManager.KingdomTier.Gong
                    || tier == OfficeManager.KingdomTier.Tianming;
                if (!eligible)
                {
                    // 小国清残留（邑/子/伯国不该有党争——用户反馈"邑国还是皇权党和太子党"）
                    if (_states.TryGetValue(kId, out var old) && old.Factions.Count > 0)
                    {
                        old.Factions.Clear();
                        ZhuluContext.Instance.LogInfo($"[Faction] 清除小国党争残留: {k.name}（位阶未至侯）");
                    }
                    return;
                }

                if (!_states.TryGetValue(kId, out var st))
                {
                    st = new KingdomFactions();
                    _states[kId] = st;
                    BuildFreshFactions(st, year);
                }
                else
                {
                    // 旧档/旧版数据只有自建党无固定四党 → 补建（防"公侯国仅两空壳自建党"）
                    EnsureBaseFactions(st, year);
                }

                AggregateMembers(k, kId, year);
                UpdateLeaders(k, kId, year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Faction] OnKingdomYear 异常: {ex.Message}");
            }
        }

        /// <summary>固定四党齐全性检查：缺哪个补哪个（旧档自建党残留数据修复用）。</summary>
        private void EnsureBaseFactions(KingdomFactions st, int year)
        {
            if (st == null) return;
            bool hasHuang = false, hasWen = false, hasWu = false, hasTai = false;
            foreach (var f in st.Factions)
            {
                if (f == null) continue;
                if (f.Type == FactionType.HuangQuan) hasHuang = true;
                else if (f.Type == FactionType.WenGuan) hasWen = true;
                else if (f.Type == FactionType.WuGuan) hasWu = true;
                else if (f.Type == FactionType.TaiZi) hasTai = true;
            }
            if (!hasHuang) st.Factions.Add(new FactionRecord { Name = "皇权党", Type = FactionType.HuangQuan, FoundedYear = year });
            if (!hasWen) st.Factions.Add(new FactionRecord { Name = "文官派", Type = FactionType.WenGuan, FoundedYear = year });
            if (!hasWu) st.Factions.Add(new FactionRecord { Name = "武官派", Type = FactionType.WuGuan, FoundedYear = year });
            if (!hasTai) st.Factions.Add(new FactionRecord { Name = "太子党", Type = FactionType.TaiZi, FoundedYear = year });
        }

        /// <summary>建固定 4 党空壳（新世界初始化用）。</summary>
        private void BuildFreshFactions(KingdomFactions st, int year)
        {
            st.Factions.Add(new FactionRecord { Name = "皇权党", Type = FactionType.HuangQuan, FoundedYear = year });
            st.Factions.Add(new FactionRecord { Name = "文官派", Type = FactionType.WenGuan, FoundedYear = year });
            st.Factions.Add(new FactionRecord { Name = "武官派", Type = FactionType.WuGuan, FoundedYear = year });
            st.Factions.Add(new FactionRecord { Name = "太子党", Type = FactionType.TaiZi, FoundedYear = year });
        }

        /// <summary>兜底补初始化后立即为现存王国建 4 党空壳（不等年度，读档瞬间生效）。
        /// 2026-08-19 修复：旧档同样生效（原 _freshWorld 限制致旧档公侯国无四党）。</summary>
        public void EnsureFreshFactionsForExistingWorld()
        {
            try
            {
                if (World.world == null || World.world.kingdoms == null) return;
                foreach (var k in World.world.kingdoms)
                {
                    if (k == null || !k.isAlive()) continue;
                    // 阶段2W：兜底初始化同样按位阶（侯级以上——与年度门槛一致）
                    var tier = GetKingdomTierOf(k);
                    if (tier != OfficeManager.KingdomTier.Hou
                        && tier != OfficeManager.KingdomTier.Gong
                        && tier != OfficeManager.KingdomTier.Tianming) continue;
                    string kId = k.id.ToString();
                    if (_states.TryGetValue(kId, out var st))
                    {
                        EnsureBaseFactions(st, ZhuluTime.CurrentYear);   // 已有状态（旧档自建党残留）补四党
                        continue;
                    }
                    st = new KingdomFactions();
                    _states[kId] = st;
                    BuildFreshFactions(st, ZhuluTime.CurrentYear);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Faction] EnsureFreshFactions 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 按身份聚合成员（阶段2T-1 重建式 + 唯一党籍裁决）。
        /// 归属优先级：皇帝→仅皇权党 ＞ 储君→仅太子党 ＞ 政治安置（提拔/转投/政变）＞ 身份自然归属。
        /// 替代原增量合并（A2-4）——其"只增不减"导致皇帝多党籍/前太子残留/提拔与唯一性两难。
        /// </summary>
        private void AggregateMembers(Kingdom k, string kId, int year)
        {
            var st = _states[kId];
            var huangQuan = st.Factions.Find(f => f.Type == FactionType.HuangQuan);
            var wenGuan = st.Factions.Find(f => f.Type == FactionType.WenGuan);
            var wuGuan = st.Factions.Find(f => f.Type == FactionType.WuGuan);
            var taiZi = st.Factions.Find(f => f.Type == FactionType.TaiZi);
            if (huangQuan == null || wenGuan == null || wuGuan == null || taiZi == null) return;

            long kingId = (k.king != null && k.king.isAlive()) ? k.king.getID() : 0;
            string heirIdStr = Persistence.KingdomDataExt.GetStr(k, "heir_id");
            long.TryParse(heirIdStr, out long heirId);

            var huangQuanSet = new HashSet<long>();
            var wenGuanSet = new HashSet<long>();
            var wuGuanSet = new HashSet<long>();
            var taiZiSet = new HashSet<long>();

            // ① 皇权党：皇帝本人 + 亲信（Favor≥60 自动入）
            if (kingId != 0) huangQuanSet.Add(kingId);
            // ② 太子党：储君 + 同族宗藩
            if (heirId != 0 && heirId != kingId) taiZiSet.Add(heirId);

            if (k.units != null)
            {
                foreach (var u in k.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    long uid = u.getID();
                    if (uid == 0 || uid == kingId) continue;

                    // 亲信自动入皇权党
                    try
                    {
                        if (FactionPoliticsManager.Instance.GetFavor(kId, uid) >= FactionPoliticsManager.FAVOR_AUTO_JOIN)
                        { huangQuanSet.Add(uid); continue; }
                    }
                    catch { }

                    // 同族宗藩入太子党（与储君同 Clan；无储君时宗藩不入——无主可从）
                    if (heirId != 0 && u.clan != null && GetActor(heirId)?.clan == u.clan
                        && IdentityClassifier.Instance.GetIdentity(uid) == IdentityClassifier.IdentityType.Imperial)
                    { taiZiSet.Add(uid); continue; }

                    // 2026-08-19 串联：功名倾向优先——文举功名（进士/举人/秀才）入文官派、
                    // 武举功名（武进士/武举人/武秀才）入武官派（覆盖身份判定——科场出身是天然政治标签）
                    try
                    {
                        if (u.hasTrait(TraitManager.TRAIT_JINSHI) || u.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)
                            || u.hasTrait(TraitManager.TRAIT_JUREN) || u.hasTrait(TraitManager.TRAIT_XIUCAI)
                            || u.hasTrait(TraitManager.NOMAD_COURT_SCHOLAR) || u.hasTrait(TraitManager.NOMAD_FIRST_SCHOLAR)
                            || u.hasTrait(TraitManager.NOMAD_LEAGUE_SCHOLAR) || u.hasTrait(TraitManager.NOMAD_TRIBE_SCHOLAR))
                        { wenGuanSet.Add(uid); continue; }
                        if (u.hasTrait(TraitManager.TRAIT_WUJINSHI) || u.hasTrait(TraitManager.TRAIT_WUJUREN)
                            || u.hasTrait(TraitManager.TRAIT_WUXIUCAI) || u.hasTrait(TraitManager.NOMAD_CHILIARCH)
                            || u.hasTrait(TraitManager.NOMAD_CENTURION) || u.hasTrait(TraitManager.NOMAD_WARRIOR))
                        { wuGuanSet.Add(uid); continue; }
                    }
                    catch { }

                    // 身份归属
                    var idType = IdentityClassifier.Instance.GetIdentity(uid);
                    if (idType == IdentityClassifier.IdentityType.Civil) wenGuanSet.Add(uid);
                    else if (idType == IdentityClassifier.IdentityType.Military) wuGuanSet.Add(uid);
                }
            }

            // ③ 政治安置覆盖（提拔/转投/政变入伙——覆盖身份自然归属，不覆盖皇帝/储君铁律）
            var placements = FactionPoliticsManager.Instance.GetPlacements(kId);
            foreach (var kv in placements)
            {
                long pid = kv.Key;
                if (pid == 0 || pid == kingId || pid == heirId) continue;
                var a = GetActor(pid);
                if (a == null || !a.isAlive()) continue;
                // 目标党须存在（固定党恒在；自建党可能已亡——跳过即回归身份归属）
                var target = st.Factions.Find(f => f.Name == kv.Value);
                if (target == null) continue;
                huangQuanSet.Remove(pid); wenGuanSet.Remove(pid); wuGuanSet.Remove(pid); taiZiSet.Remove(pid);
                if (target == huangQuan) huangQuanSet.Add(pid);
                else if (target == wenGuan) wenGuanSet.Add(pid);
                else if (target == wuGuan) wuGuanSet.Add(pid);
                else if (target == taiZi) taiZiSet.Add(pid);
                else
                {
                    // 自建党：直接重建其成员（自建党不参与自然聚合）
                    if (!target.MemberIds.Contains(pid)) target.MemberIds.Add(pid);
                }
            }

            // ④ 重建固定党成员（唯一党籍由集合互斥保证；阶段2W：国籍校验——叛变/流亡者出党）
            huangQuan.MemberIds = FilterAlive(huangQuanSet, k);
            wenGuan.MemberIds = FilterAlive(wenGuanSet, k);
            wuGuan.MemberIds = FilterAlive(wuGuanSet, k);
            taiZi.MemberIds = FilterAlive(taiZiSet, k);

            // ⑤ 自建党剥离：成员已被固定党拥有者（皇帝/储君/安置他党）移出；死亡/叛变出籍同样剔除
            var owned = new HashSet<long>(huangQuan.MemberIds);
            owned.UnionWith(wenGuan.MemberIds);
            owned.UnionWith(wuGuan.MemberIds);
            owned.UnionWith(taiZi.MemberIds);
            foreach (var f in st.Factions)
            {
                if (!f.IsCustom) continue;
                f.MemberIds.RemoveAll(id => owned.Contains(id));
                f.MemberIds.RemoveAll(id =>
                {
                    var a = GetActor(id);
                    if (a == null || !a.isAlive()) return true;
                    try { return a.kingdom != k; } catch { return false; } // 阶段2W：国籍校验
                });
            }

            // ⑥ 空自建党解散（阶段2W：党魁亡故/出籍后无传承——存档实锤 8 个 1 人空壳党残留）
            for (int i = st.Factions.Count - 1; i >= 0; i--)
            {
                var f = st.Factions[i];
                if (f.IsCustom && (f.MemberIds == null || f.MemberIds.Count == 0))
                {
                    ZhuluContext.Instance.LogInfo($"[Faction] 空自建党解散: {k.name}「{f.Name}」");
                    st.Factions.RemoveAt(i);
                }
            }
        }

        /// <summary>存活 + 国籍过滤（阶段2W：叛变/流亡者即除党籍——原只查存活，城主叛乱带走的人仍挂原国党籍）。</summary>
        private static List<long> FilterAlive(HashSet<long> set, Kingdom k)
        {
            var list = new List<long>();
            foreach (var id in set)
            {
                var a = GetActor(id);
                if (a == null || !a.isAlive()) continue;
                try { if (k != null && a.kingdom != k) continue; } catch { }
                list.Add(id);
            }
            return list;
        }

        private static Actor GetActor(long id)
        {
            try { return World.world.units.get(id); } catch { return null; }
        }

        private void UpdateLeaders(Kingdom k, string kId, int year)
        {
            var st = _states[kId];
            long kingId = (k.king != null && k.king.isAlive()) ? k.king.getID() : 0;
            foreach (var f in st.Factions)
            {
                if (f.MemberIds.Count == 0) { f.LeaderId = 0; continue; }
                // 阶段2T-1：皇权党党魁恒为皇帝（君亲掌皇权）
                if (f.Type == FactionType.HuangQuan && kingId != 0 && f.MemberIds.Contains(kingId))
                {
                    f.LeaderId = kingId;
                    continue;
                }
                long cur = f.LeaderId;
                // 阶段2A A2-1：党魁 = 党内权势最高者（太子党由权臣任魁推动政变；储君为天然成员见 UI 徽章）。
                long bestId = cur;
                float bestPower = -1f;
                foreach (var mid in f.MemberIds)
                {
                    float p = GetMemberPowerForFaction(kId, mid);
                    if (p > bestPower) { bestPower = p; bestId = mid; }
                }
                if (bestId != 0) f.LeaderId = bestId;
            }
        }

        /// <summary>阶段2A A2-4：新君即位即时入皇权党并清其他党籍（唯一党籍铁律）。</summary>
        public void EnsureKingInFaction(string kId, long kingId)
        {
            try
            {
                var st = _states[kId];
                if (st == null) return;
                foreach (var f in st.Factions)
                {
                    if (f.Type == FactionType.HuangQuan) continue;
                    if (f.MemberIds.Contains(kingId)) f.MemberIds.Remove(kingId); // 即位脱旧党
                }
                var hq = st.Factions.Find(f => f.Type == FactionType.HuangQuan);
                if (hq != null && kingId != 0 && !hq.MemberIds.Contains(kingId))
                {
                    hq.MemberIds.Add(kingId);
                    hq.LeaderId = kingId;
                }
            }
            catch { }
        }

        /// <summary>阶段2T-1：某人是否在指定党（按党名精确匹配）。</summary>
        public bool IsActorInFaction(string kId, long actorId, string factionName)
        {
            try
            {
                var st = _states[kId];
                if (st == null || string.IsNullOrEmpty(factionName)) return false;
                var f = st.Factions.Find(x => x.Name == factionName);
                return f != null && f.MemberIds != null && f.MemberIds.Contains(actorId);
            }
            catch { return false; }
        }

        /// <summary>
        /// 阶段2A A2-2：创建自建党（权势≥60 在野权臣，年度检查触发）。
        /// 每国自建党上限 2 个，防膨胀。返回实际党名（含防碰撞后缀），失败返回 null。
        /// </summary>
        public string CreateCustomFaction(string kId, long leaderId, string leaderName, int year)
        {
            try
            {
                var st = _states[kId];
                if (st == null) return null;
                int customCount = st.Factions.Count(f => f.IsCustom);
                if (customCount >= 2) return null;
                // 重复党名防碰撞
                string name = NameCustomFaction(leaderName);
                int suffix = 2;
                while (st.Factions.Any(f => f.Name == name))
                {
                    name = NameCustomFaction(leaderName) + suffix;
                    suffix++;
                }
                st.Factions.Add(new FactionRecord
                {
                    Name = name,
                    Type = FactionType.Custom,
                    IsCustom = true,
                    LeaderId = leaderId,
                    MemberIds = new List<long> { leaderId },
                    FoundedYear = year,
                    // 2026-08-19 串联：自建党立场按党魁特质生成
                    Stance = DefaultStance(FactionType.Custom, GetActor(leaderId))
                });
                ZhuluContext.Instance.LogInfo($"[FactionManager] 新党成立: {name}（{leaderName}）");
                return name;
            }
            catch { return null; }
        }

        /// <summary>
        /// 阶段2T-1：为旧档启用党争（用户在党争窗口按钮触发）。
        /// 为全部现存侯国+王国补建 4 党骨架并立即聚合一次；此后与新生世界同规则运行。
        /// </summary>
        public void BootstrapForExistingKingdoms()
        {
            try
            {
                _freshWorld = true;
                FactionPoliticsManager.Instance.EnableForExistingWorld();
                if (World.world == null || World.world.kingdoms == null) return;
                int year = ZhuluTime.CurrentYear;
                int built = 0;
                foreach (var k in World.world.kingdoms)
                {
                    if (k == null || !k.isAlive()) continue;
                    // 阶段2W：旧档启用同样按位阶（侯级以上才建党——与年度门槛一致）
                    var tier = GetKingdomTierOf(k);
                    if (tier != OfficeManager.KingdomTier.Hou
                        && tier != OfficeManager.KingdomTier.Gong
                        && tier != OfficeManager.KingdomTier.Tianming) continue;
                    string kId = k.id.ToString();
                    if (_states.ContainsKey(kId)) continue;
                    var st = new KingdomFactions();
                    _states[kId] = st;
                    BuildFreshFactions(st, year);
                    AggregateMembers(k, kId, year);
                    UpdateLeaders(k, kId, year);
                    built++;
                }
                ZhuluContext.Instance.LogInfo($"[FactionManager] 旧档启用党争：{built} 国建骨架（存量档案即时生效）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FactionManager] Bootstrap 异常: {ex.Message}");
            }
        }

        /// <summary>阶段2A A2-2：判断角色是否已在任何党。</summary>
        public bool IsActorInAnyFaction(string kId, long actorId)
        {
            try
            {
                var st = _states[kId];
                if (st == null) return false;
                foreach (var f in st.Factions)
                {
                    if (f.MemberIds != null && f.MemberIds.Contains(actorId)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>党魁选举用权势（复用 FactionPoliticsManager 的权势口径；失败回退 0）。</summary>
        private float GetMemberPowerForFaction(string kId, long actorId)
        {
            try { return FactionPoliticsManager.GetMemberPowerPublic(kId, actorId); }
            catch { return 0f; }
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class FactionsState
        {
            public Dictionary<string, KingdomFactions> States = new Dictionary<string, KingdomFactions>();
            public bool FreshWorld;
        }
        public string SectionName => "factions";
        public Dictionary<string, FactionsState> CaptureStates()
        {
            return new Dictionary<string, FactionsState>
            {
                ["__global__"] = new FactionsState { States = new Dictionary<string, KingdomFactions>(_states), FreshWorld = _freshWorld }
            };
        }
        public void ApplyStates(Dictionary<string, FactionsState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _states.Clear();
            _freshWorld = false;
            if (st.States != null) foreach (var kv in st.States) if (kv.Value != null) _states[kv.Key] = kv.Value;
            _freshWorld = st.FreshWorld;
        }



        public void MigrateFromCustomData() { }

        public void ResetForNewWorld()
        {
            _states.Clear();
            _freshWorld = true; // 新世界：首次年度建 4 党空壳
        }

        public void ResetRuntime()
        {
            _states.Clear();
            _freshWorld = false;
        }

        /// <summary>测试辅助：清空。</summary>
        public void ClearForTest() { _states.Clear(); _freshWorld = false; }

        /// <summary>测试辅助：直接注入王国党争状态（绕过世界对象；与 ClearForTest 对称）。</summary>
        public void SetStateForTest(string kingdomId, KingdomFactions st) { _states[kingdomId] = st; }

        public int StateCount => _states.Count;
    }
}
