using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 家族档案注册表（阶段3 F1 数据层：谱牒系统地基）。
    /// 按王国维护"主家 + 分支"双层家族结构（编年积累模式）：
    ///   主家 = 当朝君主直系血亲链（上溯父母/旁收兄弟姐妹/下延子孙）；
    ///   分支 = 君主旁系显赫血亲（叔伯/堂亲等）及其后代，可回查主家（MainFamilyId）。
    /// 成员收录"显赫链"：直系全收 + 旁系仅收有身份者（官职/君主/储君/城主/功名），
    /// 已故成员靠年度差分标记（本年缺席=卒年落册），名号冗余存快照——死后仍可显示。
    /// 家声（0-100）与家风（经学/将门/忠孝/奢靡）年度评估；家风带两年粘性防闪烁。
    /// 存储：内存 + 快照（ZhuluSnapshot.Families：王国ID → JSON）。
    /// </summary>
    public class FamilyRegistry : ISnapshotSection<FamilyRegistry.FamiliesState>
    {
        public static FamilyRegistry Instance { get; } = new FamilyRegistry();

        // ── 成员上限（防大家族爆表：世系表/UI 按此裁剪）──
        private const int MaxMainMembers = 18;   // batch11#4: user-registered normal family cap
        private const int MaxBranchMembers = 12;   // batch11#4: branch cap below main
        private const int MaxBranches = 4;
        private const int AscendGens = 3;   // 主家向上追的代数（君主的父母/祖父母/曾祖）
        private const int DescendGens = 3;  // 主家向下收的代数（子女/孙/曾孙）

        public class FamilyMember
        {
            public long ActorId;
            public int Gen;                // 世代（已故成员沿用旧编号，展示前统一重基准——见 RebaseGens）
            public long SpouseId;
            public bool Alive = true;
            public string Name = "";       // 冗余名（死后 Actor 可能被回收）
            public string Title = "";      // 冗余称号快照（采集时的官职/君主/功名）
            public int DiedYear;           // 卒年（0=在世）
            public int JoinYear;           // 入册年
            public List<long> ParentIds = new List<long>(); // 父母链（世代重基准+亲属称谓用）
            public bool Distinguished;     // 2026-08-18：显赫标记（先祖录收录门槛；旧档缺省 false 由称号白名单兜底）
            public int LastKnownKills;     // P2-30 串联③（2026-08-23）：生前击杀数快照（死亡后 Actor 被回收无法补读——采集时随行）
            public bool Martyr;            // P2-30 串联③：忠烈标记（生前击杀≥5 的成员亡故——世系树/先祖录展示用）
        }

        public class FamilyData
        {
            public string FamilyId = "main";       // 主家固定 "main"；分支 "br_{始祖ActorId}"
            public string KingdomId = "";
            public string Surname = "";             // 家族姓氏（取自族长）
            public long HeadId;                     // 族长（家中地位最高者，主家=君主）
            public string HeadName = "";
            public bool IsMainLine = true;          // 是否主家
            public string BranchFounderName = "";   // 分支始祖名（主家空）
            public int BranchYear;                  // 分家年（主家 0）
            public int Prestige = 40;               // 家声 0-100
            public string Style = "";               // 家风：jingxue/jiangmen/zhongxiao/shemi/""（暂无）
            public bool Shuxiang;                   // P2-30 串联④：书香门第标记（一门三进士一次性达成——家声+8）
            public int JinshiPassCount;             // P2-30 游牧适配：进士级及第累计数（特质等级判定，双体制通吃；随快照 JSON 自动序列化）
            public List<FamilyMember> Members = new List<FamilyMember>();
            public int UpdatedYear;
        }

        /// <summary>王国族系（主家 + 分支列表 + 望族引用）。</summary>
        public class KingdomFamilies
        {
            public string KingdomId = "";
            public FamilyData Main = new FamilyData();
            public List<FamilyData> Branches = new List<FamilyData>();
            /// <summary>国中望族（非王族的原版 Clan——官宦/世家/大族，阶段3）。</summary>
            public List<NotableFamilyRef> Notables = new List<NotableFamilyRef>();
            public int UpdatedYear;
        }

        /// <summary>望族引用（存原版 Clan ID + 家声快照，成员实时从 Clan.units 取）。</summary>
        public class NotableFamilyRef
        {
            public long ClanId;
            public string Name = "";
            public int MemberCount;
            public int Prestige;
            public string Style = "";
            public int UpdatedYear;
            public int Officials;               // 2026-08-19 串联：入仕人数（谱牒望族行显示；旧档缺省0）
        }

        private readonly Dictionary<string, KingdomFamilies> _data = new Dictionary<string, KingdomFamilies>();
        // 家风粘性：王国ID → (家风key → 连续满足年数)
        private readonly Dictionary<string, Dictionary<string, int>> _styleStreak = new Dictionary<string, Dictionary<string, int>>();

        private FamilyRegistry() { }

        private bool Enabled => ZhuluConfig.Instance.GroundworkEnabled;

        // ═══════════════════════ 公开查询 ═══════════════════════

        /// <summary>取王国族系（无则 null）。</summary>
        public KingdomFamilies GetKingdomFamilies(string kingdomId)
        {
            if (string.IsNullOrEmpty(kingdomId)) return null;
            _data.TryGetValue(kingdomId, out var f);
            return f;
        }

        /// <summary>按家族ID取家族（含主家/分支互查）。</summary>
        public FamilyData GetFamily(string kingdomId, string familyId)
        {
            var kf = GetKingdomFamilies(kingdomId);
            if (kf == null) return null;
            if (familyId == "main" || string.IsNullOrEmpty(familyId)) return kf.Main;
            foreach (var b in kf.Branches)
            {
                if (b.FamilyId == familyId) return b;
            }
            return null;
        }

        /// <summary>
        /// P2-30 串联④（2026-08-23）：科举及第（进士级，文武皆计）通知。
        /// 同门现役进士级成员达三人 → 首次触发"书香门第"：家声+8、Shuxiang 标记置位；
        /// 之后再及第不重复加成。无族籍者静默。
        /// </summary>
        public void NotifyExamPass(string kingdomId, long actorId, string gongmingTraitId, bool quietLogs = false)
        {
            try
            {
                if (string.IsNullOrEmpty(kingdomId)) return;
                // P2-30 游牧适配重写：按功名特质等级判定（KejuManager.GongmingRank==3 即进士级）——
                // 旧版 Title.Contains("进士") 对游牧"汗庭学士/魁首"系永远失效；现按特质等级计数，
                // 文武/双体制通吃。同门累计三门（JinshiPassCount==3）首次触发书香门第，之后静默。
                if (KejuManager.GongmingRank(gongmingTraitId) != 3) return;

                var kf = GetKingdomFamilies(kingdomId);
                if (kf == null) return;

                FamilyData hit = null;
                if (ContainsMember(kf.Main, actorId)) hit = kf.Main;
                else
                {
                    foreach (var b in kf.Branches)
                    {
                        if (b != null && ContainsMember(b, actorId)) { hit = b; break; }
                    }
                }
                if (hit == null) return; // 及第者无族籍（寒门未入册）不计
                if (hit.Shuxiang) return; // 已达成——完全静默（计数冻结在3，不再累加不加成）

                hit.JinshiPassCount++;
                if (hit.JinshiPassCount < 3) return; // 未达三门——静默
                hit.Shuxiang = true;
                hit.Prestige = Math.Max(0, Math.Min(100, hit.Prestige + 8));
                if (!quietLogs)
                {
                    ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 书香门第: [{kingdomId}] {hit.Surname}氏 一门三进士——家声+8");
                    ChronicleManager.Instance.AddEntry(ZhuluTime.CurrentYear, ChronicleEventType.ClanMilestone,
                        kingdomId, hit.Surname + "氏", "",
                        $"<color=#70d468>【书香门第】</color>{hit.Surname}氏一门三进士，文风鼎盛，乡里传为佳话。", false);
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogRateLimited("FamilyRegistry.NotifyExamPass",
                    "[FamilyRegistry] 及第通知异常: " + ex.Message, 30f);
            }
        }

        /// <summary>某人是否在指定王国的某家族显赫链中（单位窗口"族谱"按钮判定）。</summary>
        public FamilyData FindFamilyOfActor(string kingdomId, long actorId)
        {
            var kf = GetKingdomFamilies(kingdomId);
            if (kf == null) return null;
            if (ContainsMember(kf.Main, actorId)) return kf.Main;
            foreach (var b in kf.Branches)
            {
                if (ContainsMember(b, actorId)) return b;
            }
            return null;
        }

        /// <summary>家风显示名。</summary>
        public static string GetStyleName(string style, bool nomad = false)
        {
            if (nomad)
            {
                switch (style)
                {
                    case "jingxue": return "口传史诗";
                    case "jiangmen": return "骁骑世家";
                    case "zhongxiao": return "敬老尊长";
                    case "shemi": return "奢靡之风";
                    default: return "暂无部风";
                }
            }
            switch (style)
            {
                case "jingxue": return "经学传家";
                case "jiangmen": return "将门虎子";
                case "zhongxiao": return "忠孝传家";
                case "shemi": return "奢靡之风";
                default: return "无家风";
            }
        }

        /// <summary>家风效果描述（简介页显示）。</summary>
        public static string GetStyleEffect(string style, bool nomad = false)
        {
            if (nomad)
            {
                switch (style)
                {
                    case "jingxue": return "族内选贤评分 +5";
                    case "jiangmen": return "族内军职考评加成";
                    case "zhongxiao": return "族内腐败增速 ×0.8";
                    case "shemi": return "族内腐败增速 ×1.3，家声渐损";
                    default: return "暂无部风加成";
                }
            }
            switch (style)
            {
                case "jingxue": return "族内科举评分 +5";
                case "jiangmen": return "族内武职考评加成";
                case "zhongxiao": return "族内腐败增速 ×0.8";
                case "shemi": return "族内腐败增速 ×1.3，家声渐损";
                default: return "暂无族风加成";
            }
        }

        /// <summary>家声门第名（谱牒榜单用——数值转门第，更古风）。</summary>
        public static string GetPrestigeGrade(int prestige, bool nomad = false)
        {
            if (nomad)
            {
                if (prestige >= 80) return "黄金血脉";
                if (prestige >= 60) return "那颜贵种";
                if (prestige >= 40) return "控弦之门";
                if (prestige >= 20) return "牧奴之帐";
                return "罪奴之部";
            }
            if (prestige >= 80) return "簪缨世家";
            if (prestige >= 60) return "衣冠名族";
            if (prestige >= 40) return "诗礼之家";
            if (prestige >= 20) return "寒微之门";
            return "罪籍之家";
        }

        // ═══════════════════════ 年度重建（编年积累） ═══════════════════════

        /// <summary>
        /// 2026-08-18 改朝换代谱牒隔离：异姓篡位后旧王族档案整体归档——
        /// ① 旧王族 Clan 转记"前朝望族"引用（成员仍可从 Clan 实时查看）
        /// ② 清空主家/分支（防新君血脉与旧朝已故成员混册——先祖录两朝混乱根因）
        /// ③ 新王族次年 OnKingdomYear 按新君全新构建。
        /// </summary>
        public void ResetOnDynastyChange(string kingdomId, Actor oldKing)
        {
            try
            {
                if (string.IsNullOrEmpty(kingdomId)) return;
                if (!_data.TryGetValue(kingdomId, out var kf)) return;

                // ① 旧王族 → 前朝望族引用
                try
                {
                    if (oldKing != null && oldKing.clan != null)
                    {
                        var clan = oldKing.clan;
                        bool already = false;
                        foreach (var n in kf.Notables)
                        {
                            if (n != null && n.ClanId == clan.getID()) { already = true; break; }
                        }
                        if (!already)
                        {
                            int aliveCount = 0;
                            try { if (clan.units != null) foreach (Actor m in clan.units) { if (m != null && m.isAlive()) aliveCount++; } } catch { }
                            kf.Notables.Add(new NotableFamilyRef
                            {
                                ClanId = clan.getID(),
                                Name = (clan.data != null ? clan.data.name : null) ?? (oldKing.getName() ?? "前朝") + "氏",
                                MemberCount = aliveCount,
                                Prestige = kf.Main != null ? kf.Main.Prestige : 40,
                                UpdatedYear = ZhuluTime.CurrentYear
                            });
                        }
                    }
                }
                catch { }

                // ② 清空主家/分支（新朝次年重建；家风粘性一并清除）
                kf.Main = new FamilyData();
                kf.Branches = new List<FamilyData>();
                _styleStreak.Remove(kingdomId);
                _data[kingdomId] = kf;

                ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 改朝换代谱牒隔离: 王国{kingdomId} 旧王族归档，新朝次年重建");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyRegistry] ResetOnDynastyChange 异常: {ex.Message}");
            }
        }

        /// <summary>王国年度：重建主家/分支（活人重扫），合并已故成员（差分标卒年）。
        /// 在 RoyalRegistry 之后调用（归族完毕，血缘关系已完整）。</summary>
        public void OnKingdomYear(Kingdom k, int year)
        {
            if (!Enabled) return;
            if (k == null || !k.isAlive()) return;
            string kId = k.id.ToString();
            try
            {
                if (k.king == null || !k.king.isAlive())
                {
                    // 无君：保留档案（已故成员不丢），只更新年份
                    if (_data.TryGetValue(kId, out var keep)) keep.UpdatedYear = year;
                    return;
                }

                var kf = _data.TryGetValue(kId, out var old) ? old : new KingdomFamilies { KingdomId = kId };
                kf.UpdatedYear = year;

                // ── 主家重建 ──
                var prevMain = kf.Main;
                var main = BuildMainFamily(k, kId, year, prevMain);
                kf.Main = main;

                // ── 分支重建（基于旧分支始祖 + 君主旁系显赫血亲新识别）──
                kf.Branches = RebuildBranches(k, kId, year, main, kf.Branches);

                // ── 望族采集（阶段3：非王族的原版 Clan——官宦/世家/大族入谱牒榜单）──
                kf.Notables = CollectNotables(k, kId, year);

                _data[kId] = kf;

                // 家声+家风评估（主家与分支各自算）
                EvaluatePrestigeAndStyle(k, kId, main, year);
                foreach (var b in kf.Branches) EvaluatePrestigeAndStyle(k, kId, b, year);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyRegistry] OnKingdomYear 异常: {ex.Message}");
            }
        }

        /// <summary>构建主家：君主为基准，上溯父母链 AscendGens 代（旁收显赫兄弟姐妹），下延 DescendGens 代。</summary>
        private FamilyData BuildMainFamily(Kingdom k, string kId, int year, FamilyData prev)
        {
            long kingId = k.king.getID();
            var members = new Dictionary<long, FamilyMember>();
            int baseGen = AscendGens; // 君主世代基准（祖辈归一化后≈第1~2代）

            // 已故成员保留册（从旧档继承——编年积累核心）
            if (prev != null)
            {
                foreach (var m in prev.Members)
                {
                    if (!m.Alive)
                    {
                        var restored = CloneMember(m);
                        Actor living = FindActorWorldwide(m.ActorId);
                        if (living != null)
                        {
                            // 修复已经被旧版错误写死为“已故”的活人；不能只修展示，否则下次保存仍会污染谱牒。
                            restored.Alive = true;
                            restored.DiedYear = 0;
                            restored.Name = living.getName();
                            restored.ParentIds = SafeGetParentIds(living);
                            restored.SpouseId = GetSpouseIdSafe(living);
                        }
                        members[m.ActorId] = restored;
                    }
                }
            }

            // 君主本人
            CollectPerson(k, k.king, baseGen, year, members, true);

            // 上溯链：父母 → 祖父母（只沿活人）
            var frontier = new List<Actor> { k.king };
            for (int g = 1; g <= AscendGens; g++)
            {
                var nextFrontier = new List<Actor>();
                foreach (var person in frontier)
                {
                    var parents = SafeGetParents(person);
                    foreach (var p in parents)
                    {
                        if (p == null || !p.isAlive()) continue;
                        if (members.ContainsKey(p.getID())) continue;
                        CollectPerson(k, p, baseGen - g, year, members, true);
                        nextFrontier.Add(p);
                    }
                    // 兄弟姐妹（同代旁收：直系全收不够古风——兄弟姊妹也是天潢贵胄；显赫者收录）
                    var siblings = KinshipTrackerSafe.GetAliveSiblings(k, person);
                    foreach (var s in siblings)
                    {
                        if (s == null || !s.isAlive()) continue;
                        if (members.ContainsKey(s.getID())) continue;
                        if (IsDistinguished(k, kId, s, kingId))
                        {
                            CollectPerson(k, s, baseGen - g + 1, year, members, false);
                        }
                    }
                }
                frontier = nextFrontier;
                if (frontier.Count == 0) break;
            }

            // 下延链：子女/孙/曾孙（直系血脉全收——世系表主干）
            frontier = new List<Actor> { k.king };
            for (int g = 1; g <= DescendGens; g++)
            {
                var nextFrontier = new List<Actor>();
                foreach (var person in frontier)
                {
                    // 世系是血缘关系，不应因子女迁居、被俘或改属他国而断线。
                    var children = KinshipTracker.GetAllChildrenWorldwide(person.getID().ToString());
                    foreach (var c in children)
                    {
                        if (c == null || !c.isAlive()) continue;
                        if (members.ContainsKey(c.getID())) continue;
                        if (members.Count >= MaxMainMembers) break;
                        CollectPerson(k, c, baseGen + g, year, members, true);
                        nextFrontier.Add(c);
                    }
                }
                frontier = nextFrontier;
                if (frontier.Count == 0 || members.Count >= MaxMainMembers) break;
            }

            // 差分核生死：不在“当前王国/当前三代采集范围”不等于死亡。
            // 迁居、被俘、改属他国或换君后超出三代窗口的活人，必须继续以活人保留。
            var martyrsThisRebuild = new List<FamilyMember>();
            if (prev != null)
            {
                foreach (var oldM in prev.Members)
                {
                    if (!oldM.Alive) continue;
                    if (!members.ContainsKey(oldM.ActorId))
                    {
                        Actor living = FindActorWorldwide(oldM.ActorId);
                        var retained = CloneMember(oldM);
                        if (living != null && living.isAlive())
                        {
                            retained.Alive = true;
                            retained.DiedYear = 0;
                            retained.Name = living.getName();
                            retained.ParentIds = SafeGetParentIds(living);
                            retained.SpouseId = GetSpouseIdSafe(living);
                        }
                        else
                        {
                            retained.Alive = false;
                            retained.DiedYear = year;
                            // P2-30 串联③：忠烈判定——生前击杀≥5（军人特征）的成员亡故 → 记忠烈，
                            // 家声加成在 fam 构造后统一施加（将门家风折算见下）
                            if (retained.LastKnownKills >= 5)
                            {
                                retained.Martyr = true;
                                martyrsThisRebuild.Add(retained);
                            }
                        }
                        members[retained.ActorId] = retained;
                    }
                }
            }

            // 阶段3：跨代 Gen 重基准（修"第一代不显示"根因——已故成员沿用生前编号，
            // 换王后基准漂移致祖孙同辈。以当朝君主为 0，沿 ParentIds 链全族重算）。
            RebaseGens(members, kingId, baseGen);

            var fam = new FamilyData
            {
                FamilyId = "main",
                KingdomId = kId,
                Surname = ResolveFamilySurname(k.king),
                HeadId = kingId,
                HeadName = k.king.getName(),
                IsMainLine = true,
                Prestige = prev?.Prestige ?? 40,
                Style = prev?.Style ?? "",
                Members = new List<FamilyMember>(members.Values),
                UpdatedYear = year
            };

            // P2-30 串联③：忠烈家声加成——基础+2/员；将门家风折算×1.2 进位 +3（文档口径取整进位）。
            // 量纲依据：家声 0-100、年度自然波动±几，"丧一员忠烈"是家族大事件，+2/+3 合拍。
            if (martyrsThisRebuild.Count > 0)
            {
                int honor = 0;
                foreach (var mt in martyrsThisRebuild)
                    honor += fam.Style == "jiangmen" ? 3 : 2;
                fam.Prestige = Math.Max(0, Math.Min(100, fam.Prestige + honor));
                var names = string.Join("、", martyrsThisRebuild.ConvertAll(m => m.Name ?? ""));
                ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 忠烈入谱: [{kId}] {fam.Surname}氏 {names} 阵亡——家声+{honor}");
            }
            return fam;
        }

        /// <summary>分支重建：旧分支始祖若仍在君主旁系血亲网内则延续；君主显赫旁系（叔伯堂亲）无归属者立新支（上限 MaxBranches）。</summary>
        private List<FamilyData> RebuildBranches(Kingdom k, string kId, int year, FamilyData main, List<FamilyData> prevBranches)
        {
            var result = new List<FamilyData>();
            var mainIds = new HashSet<long>();
            foreach (var m in main.Members) mainIds.Add(m.ActorId);

            long kingId = k.king.getID();

            foreach (var prevB in prevBranches)
            {
                if (result.Count >= MaxBranches) break;
                // 分支始祖 ActorId（FamilyId "br_{id}"）
                if (!long.TryParse(prevB.FamilyId.Substring(3), out long founderId)) continue;
                Actor founder = FindActorInKingdom(k, founderId);

                // 始祖已故且无在册活人 → 分支绝灭，仅当有历史成员时保留供查阅（绝支不再占新名额）
                var branch = BuildBranchFromFounder(k, kId, year, founder, founderId, prevB, mainIds, kingId);
                if (branch != null) result.Add(branch);
            }

            // 新分支识别：君主的旁系显赫血亲（父母/兄弟姐妹/祖辈兄弟的后代），不在主家且不属既有分支
            if (result.Count < MaxBranches)
            {
                var candidateRoots = FindDistinguishedCollaterals(k, kId, mainIds, result);
                foreach (var root in candidateRoots)
                {
                    if (result.Count >= MaxBranches) break;
                    string fid = "br_" + root.getID();
                    bool exists = false;
                    foreach (var b in result) { if (b.FamilyId == fid) { exists = true; break; } }
                    if (exists) continue;

                    var nb = BuildBranchFromFounder(k, kId, year, root, root.getID(),
                        new FamilyData { FamilyId = fid, KingdomId = kId, Prestige = 35 }, mainIds, kingId);
                    if (nb != null && CountAlive(nb) > 0)
                    {
                        nb.IsMainLine = false;
                        nb.FamilyId = fid;
                        nb.BranchYear = year;
                        nb.BranchFounderName = root.getName();
                        nb.Surname = main.Surname; // 同姓分支
                        result.Add(nb);
                        ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 新分支立籍: {main.Surname}家·{root.getName()}支（{k.name}）");
                    }
                }
            }
            return result;
        }

        /// <summary>从分支始祖构建分支家族（始祖+其后代2代；始祖已故则靠旧册差分延续）。</summary>
        private FamilyData BuildBranchFromFounder(Kingdom k, string kId, int year, Actor founder, long founderId,
            FamilyData prev, HashSet<long> mainIds, long kingId)
        {
            var members = new Dictionary<long, FamilyMember>();
            // 已故继承
            if (prev != null)
            {
                foreach (var m in prev.Members)
                {
                    if (!m.Alive)
                    {
                        var restored = CloneMember(m);
                        Actor living = FindActorWorldwide(m.ActorId);
                        if (living != null)
                        {
                            restored.Alive = true;
                            restored.DiedYear = 0;
                            restored.Name = living.getName();
                            restored.ParentIds = SafeGetParentIds(living);
                            restored.SpouseId = GetSpouseIdSafe(living);
                        }
                        members[m.ActorId] = restored;
                    }
                }
            }

            int aliveBefore = 0;
            if (prev != null) foreach (var m in prev.Members) { if (m.Alive) aliveBefore++; }

            int gen0 = 1;
            if (founder != null && founder.isAlive() && !mainIds.Contains(founderId))
            {
                CollectPerson(k, founder, gen0, year, members, true);
                // 后代2代
                var frontier = new List<Actor> { founder };
                for (int g = 1; g <= 2; g++)
                {
                    var next = new List<Actor>();
                    foreach (var p in frontier)
                    {
                        var children = KinshipTracker.GetAllChildrenWorldwide(p.getID().ToString());
                        foreach (var c in children)
                        {
                            if (c == null || !c.isAlive() || members.ContainsKey(c.getID())) continue;
                            if (members.Count >= MaxBranchMembers) break;
                            if (mainIds.Contains(c.getID())) continue; // 已归主家（如回继大宗）
                            CollectPerson(k, c, gen0 + g, year, members, true);
                            next.Add(c);
                        }
                    }
                    frontier = next;
                }
            }

            // 差分核生死：分支成员离开当前王国/采集范围仍可能活着。
            // P2-30 串联③：分支忠烈同样记标记并入家声（与主家同口径：+2 / 将门进位+3）。
            var branchMartyrs = new List<FamilyMember>();
            if (prev != null)
            {
                foreach (var oldM in prev.Members)
                {
                    if (!oldM.Alive) continue;
                    if (!members.ContainsKey(oldM.ActorId))
                    {
                        Actor living = FindActorWorldwide(oldM.ActorId);
                        var retained = CloneMember(oldM);
                        if (living != null && living.isAlive())
                        {
                            retained.Alive = true;
                            retained.DiedYear = 0;
                            retained.Name = living.getName();
                            retained.ParentIds = SafeGetParentIds(living);
                            retained.SpouseId = GetSpouseIdSafe(living);
                        }
                        else
                        {
                            retained.Alive = false;
                            retained.DiedYear = year;
                            // P2-30 串联③：分支忠烈——判定与标记同主家，家声加成在 fam 返回前统一施加
                            if (retained.LastKnownKills >= 5)
                            {
                                retained.Martyr = true;
                                branchMartyrs.Add(retained);
                            }
                        }
                        members[retained.ActorId] = retained;
                    }
                }
            }

            int aliveNow = 0;
            foreach (var m in members.Values) { if (m.Alive) aliveNow++; }

            // 始祖不在册且无任何在世成员且无已故册 → 分支彻底消亡（返回 null）
            if (aliveNow == 0 && members.Count == 0) return null;

            var fam = prev ?? new FamilyData();
            fam.KingdomId = kId;
            fam.IsMainLine = false;
            fam.Members = new List<FamilyMember>(members.Values);
            fam.UpdatedYear = year;
            if (string.IsNullOrEmpty(fam.FamilyId)) fam.FamilyId = "br_" + founderId;
            if (string.IsNullOrEmpty(fam.Surname)) fam.Surname = "";
            if (string.IsNullOrEmpty(fam.BranchFounderName) && founder != null) fam.BranchFounderName = founder.getName();

            // 族长=分支内在世地位最高者（官职>功名>年长——简化：优先有官职者，其次第一个在世的）
            long head = 0; string headName = "";
            foreach (var m in fam.Members)
            {
                if (!m.Alive) continue;
                if (head == 0 || (!string.IsNullOrEmpty(m.Title) && string.IsNullOrEmpty(headName)))
                {
                    head = m.ActorId; headName = m.Title; // 先占位，下面用 Name 修正
                }
            }
            foreach (var m in fam.Members)
            {
                if (m.Alive && m.ActorId == head) { headName = m.Name; break; }
            }
            fam.HeadId = head; fam.HeadName = headName;

            // P2-30 串联③：分支忠烈家声加成（与主家同口径）
            if (branchMartyrs.Count > 0)
            {
                int honor = 0;
                foreach (var mt in branchMartyrs)
                    honor += fam.Style == "jiangmen" ? 3 : 2;
                fam.Prestige = Math.Max(0, Math.Min(100, fam.Prestige + honor));
                var names = string.Join("、", branchMartyrs.ConvertAll(m => m.Name ?? ""));
                ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 分支忠烈入谱: [{kId}] {names} 阵亡——家声+{honor}");
            }
            return fam;
        }

        /// <summary>找君主旁系显赫血亲（可立新支的：本身显赫且有自己的后代）。</summary>
        private List<Actor> FindDistinguishedCollaterals(Kingdom k, string kId, HashSet<long> mainIds, List<FamilyData> branches)
        {
            var roots = new List<Actor>();
            long kingId = k.king.getID();
            try
            {
                if (k.units == null) return roots;
                var kingParentsIds = new HashSet<long>();
                foreach (var p in SafeGetParents(k.king)) { if (p != null) kingParentsIds.Add(p.getID()); }
                // 祖辈
                foreach (var p in SafeGetParents(k.king))
                {
                    if (p == null) continue;
                    foreach (var gp in SafeGetParents(p)) { if (gp != null) kingParentsIds.Add(gp.getID()); }
                }

                foreach (Actor u in k.units)
                {
                    if (u == null || !u.isAlive()) continue;
                    long uid = u.getID();
                    if (uid == kingId || mainIds.Contains(uid)) continue;
                    bool inBranch = false;
                    foreach (var b in branches) { foreach (var m in b.Members) { if (m.ActorId == uid) { inBranch = true; break; } } if (inBranch) break; }
                    if (inBranch) continue;

                    // 血亲判定：共享父/祖
                    bool kin = false;
                    foreach (var p in SafeGetParents(u)) { if (p != null && kingParentsIds.Contains(p.getID())) { kin = true; break; } }
                    if (!kin) continue;

                    // 显赫 + 有后代（无后的孤人不立支——直接留在主家旁系已收或不收）
                    if (IsDistinguished(k, kId, u, kingId) && KinshipTracker.GetAllChildrenWorldwide(uid.ToString()).Count > 0)
                    {
                        roots.Add(u);
                        if (roots.Count >= 2) break; // 每年至多新立2支
                    }
                }
            }
            catch { }
            return roots;
        }

        // ═══════════════════════ 望族采集（阶段3） ═══════════════════════

        /// <summary>采集国中望族：收录国内全部非王族家族（用户定案：无门槛、无上限，按家声排序）。
        /// 家声=基线+官员+功名+丁口，仅用于排序与门第显示。</summary>
        private List<NotableFamilyRef> CollectNotables(Kingdom k, string kId, int year)
        {
            var result = new List<NotableFamilyRef>();
            try
            {
                Clan kingClan = k.king?.clan;
                long kingClanId = kingClan != null ? kingClan.getID() : -1;

                // 汇总国内各 Clan 成员（王族除外——王族走大宗/分支谱）
                var clanMembers = new Dictionary<long, List<Actor>>();
                if (k.units != null)
                {
                    foreach (Actor u in k.units)
                    {
                        if (u == null || !u.isAlive()) continue;
                        Clan c = u.clan;
                        if (c == null || c.getID() == kingClanId) continue;
                        long cid = c.getID();
                        if (!clanMembers.TryGetValue(cid, out var list)) { list = new List<Actor>(); clanMembers[cid] = list; }
                        list.Add(u);
                    }
                }

                foreach (var kv in clanMembers)
                {
                    Clan c = null;
                    try { c = World.world.clans.get(kv.Key); } catch { }
                    if (c == null) continue;

                    int officials = 0, degrees = 0;
                    foreach (var a in kv.Value)
                    {
                        if (FindOfficeRecordSafe(kId, a.getID()) != null) officials++;
                        if (a.hasTrait(TraitManager.TRAIT_XIUCAI) || a.hasTrait(TraitManager.TRAIT_JUREN)
                            || a.hasTrait(TraitManager.TRAIT_JINSHI) || a.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)
                            || a.hasTrait(TraitManager.TRAIT_WUXIUCAI) || a.hasTrait(TraitManager.TRAIT_WUJUREN)
                            || a.hasTrait(TraitManager.TRAIT_WUJINSHI)) degrees++;
                    }

                    // 家声：基线+官员+功名+丁口（仅排序/门第用——无收录门槛）
                    int p = 25 + Math.Min(35, officials * 8) + Math.Min(20, degrees * 5) + Math.Min(20, kv.Value.Count);
                    result.Add(new NotableFamilyRef
                    {
                        ClanId = kv.Key,
                        Name = (c.data != null ? c.data.name : null) ?? "",
                        MemberCount = kv.Value.Count,
                        Prestige = Math.Max(0, Math.Min(100, p)),
                        Style = degrees >= 3 ? "jingxue" : (officials >= 3 ? "zhongxiao" : ""),
                        UpdatedYear = year,
                        Officials = officials
                    });
                }

                result.Sort((a, b) => b.Prestige.CompareTo(a.Prestige));
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyRegistry] 望族采集异常: {ex.Message}");
            }
            return result;
        }

        /// <summary>按 Clan ID 取望族引用（原版家族窗口/族谱按钮定位用）。</summary>
        public NotableFamilyRef FindNotableByClan(long clanId)
        {
            foreach (var kf in _data.Values)
            {
                foreach (var n in kf.Notables)
                {
                    if (n.ClanId == clanId) return n;
                }
            }
            return null;
        }

        // ═══════════════════════ 家声与家风 ═══════════════════════

        /// <summary>年度评估家声（0-100）与家风（两年粘性切换）。</summary>
        private void EvaluatePrestigeAndStyle(Kingdom k, string kId, FamilyData fam, int year)
        {
            try
            {
                int p = 40; // 基线
                int officials = 0, jinshi = 0, wuke = 0, corrupt = 0;
                int ratedSum = 0, ratedCount = 0;

                foreach (var m in fam.Members)
                {
                    if (!m.Alive) continue;
                    Actor a = FindActorInKingdom(k, m.ActorId);
                    if (a == null || !a.isAlive()) continue;

                    // 官职判定（Title 快照含官名即可加分；精确判定走 OfficeManager）
                    var rec = FindOfficeRecordSafe(kId, m.ActorId);
                    if (rec != null)
                    {
                        officials++;
                        try
                        {
                            var (s, _) = OfficialRatingManager.CalcOfficialScore(a, OfficeManager.GetOfficeIndex(rec.OfficeId), 0);
                            ratedSum += s; ratedCount++;
                        }
                        catch { }
                    }
                    if (a.hasTrait(TraitManager.TRAIT_JINSHI) || a.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)) jinshi++;
                    else if (a.hasTrait(TraitManager.TRAIT_JUREN)) jinshi++; // 举人算半——简化并入文脉
                    if (a.hasTrait(TraitManager.TRAIT_WUJINSHI) || a.hasTrait(TraitManager.TRAIT_WUJUREN) || a.hasTrait(TraitManager.TRAIT_WUXIUCAI)) wuke++;
                    if (a.hasTrait(TraitManager.TRAIT_CORRUPT)) corrupt++;
                }

                if (ratedCount > 0) p += Math.Min(30, ratedSum / ratedCount / 4);
                p += Math.Min(30, officials * 5);
                if (fam.IsMainLine && fam.HeadId != 0 && k.king != null && k.king.getID() == fam.HeadId) p += 10; // 天潢贵胄
                p += Math.Min(15, jinshi * 4);
                p += Math.Min(10, wuke * 3);
                p -= Math.Min(25, corrupt * 8);

                fam.Prestige = Math.Max(0, Math.Min(100, p));

                // ── 家风评估（候选优先级：奢靡>经学>将门>忠孝；连续2年满足才切换）──
                string target = "";
                if (corrupt >= 2) target = "shemi";
                else if (jinshi >= 3) target = "jingxue";
                else if (wuke >= 3) target = "jiangmen";
                else if (corrupt == 0 && fam.Prestige >= 50) target = "zhongxiao";

                if (target != fam.Style)
                {
                    string streakKey = kId + "|" + fam.FamilyId + "|" + target;
                    if (!_styleStreak.TryGetValue(kId, out var dict))
                    {
                        dict = new Dictionary<string, int>();
                        _styleStreak[kId] = dict;
                    }
                    dict[streakKey] = dict.TryGetValue(streakKey, out int c) ? c + 1 : 1;
                    if (dict[streakKey] >= 2)
                    {
                        ZhuluContext.Instance.LogInfo($"[FamilyRegistry] 家风更易: {fam.Surname}家({fam.FamilyId}) {GetStyleName(fam.Style)} → {GetStyleName(target)}");
                        fam.Style = target;
                        dict[streakKey] = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyRegistry] 家声评估异常: {ex.Message}");
            }
        }

        // ═══════════════════════ 内部辅助 ═══════════════════════

        /// <summary>采集一个成员（冗余名+称号快照+父母链）。</summary>
        private void CollectPerson(Kingdom k, Actor a, int gen, int year, Dictionary<long, FamilyMember> bag, bool direct)
        {
            if (a == null || bag.ContainsKey(a.getID())) return;
            string title = BuildTitleSnapshot(k, k.id.ToString(), a);
            var m = new FamilyMember
            {
                ActorId = a.getID(),
                Gen = gen,
                SpouseId = GetSpouseIdSafe(a),
                Alive = true,
                Name = a.getName(),
                Title = title,
                JoinYear = year,
                ParentIds = SafeGetParentIds(a),
                // 2026-08-18 先祖录门槛：仅显赫者入录（官职/君主/储君/城主/功名/爵位/奇才）
                Distinguished = IsDistinguishedTitle(title),
                // P2-30 串联③：生前击杀快照——死亡差分时 Actor 已回收，忠烈判定只能靠采集期随行数据
                LastKnownKills = OfficialRatingManager.GetActorKills(a)
            };
            bag[m.ActorId] = m;
        }

        /// <summary>2026-08-18：称号是否显赫（先祖录收录判定——官职/君主/储君/城主/功名/爵位/奇才；
        /// 身份名如"国人/士人/隐士"不算——此前 Title≠"国人" 即入录致平民混入）。</summary>
        public static bool IsDistinguishedTitle(string title)
        {
            if (string.IsNullOrEmpty(title) || title == "国人") return false;
            // 功名
            switch (title)
            {
                case "状元":
                case "进士":
                case "武进士":
                case "举人":
                case "武举人":
                case "秀才":
                case "武秀才":
                case "城主":
                case "天纵奇才":
                    return true;
            }
            // 爵位（X国之君）
            if (title.EndsWith("国之君")) return true;
            // 君主/储君称号（王/公/侯/伯/子/邑君、太子/世子/储君等——含"君""帝""王"字样的封号）
            if (title.Length <= 4 && (title.EndsWith("君") || title.EndsWith("帝") || title.EndsWith("王")
                || title.EndsWith("太子") || title.EndsWith("世子") || title.EndsWith("储君"))) return true;
            // 中央/地方官职（称号快照来自 OfficeManager.GetOfficeTitleForKingdom——
            // 官职名清单动态化困难，改用反向排除：两字/三字且非身份词的按官职处理）
            string[] identityNames = { "士人", "庶民", "隐士", "富商", "农夫", "匠户", "猎户", "渔民", "族人", "平民", "游侠", "巫祝" };
            foreach (var idn in identityNames)
            {
                if (title == idn || title.Contains(idn)) return false;
            }
            // 其余（丞相/尚书/都尉/县丞等官职名）视为显赫
            return true;
        }

        private static List<long> SafeGetParentIds(Actor a)
        {
            var ids = new List<long>();
            try
            {
                var parents = a.getParents();
                if (parents != null)
                    foreach (var p in parents) { if (p != null) ids.Add(p.getID()); }
            }
            catch { }
            return ids;
        }

        /// <summary>
        /// 跨代 Gen 重基准：以 anchor（当朝君主）为 anchorGen，沿 ParentIds 双向 BFS 全族重算世代。
        /// 修"换王后辈分错乱/第一代不显示"——已故成员的 Gen 停留在其生前采集值，
        /// 不重基准会导致先王与今王同辈、祖辈被挤进孙辈组。未连通者保留原值（展示层兜底）。
        /// </summary>
        private static void RebaseGens(Dictionary<long, FamilyMember> members, long anchorId, int anchorGen)
        {
            try
            {
                if (!members.ContainsKey(anchorId)) return;

                // 反向索引：父 → 子列表
                var childrenOf = new Dictionary<long, List<long>>();
                foreach (var m in members.Values)
                {
                    if (m.ParentIds == null) continue;
                    foreach (var pid in m.ParentIds)
                    {
                        if (!childrenOf.TryGetValue(pid, out var list)) { list = new List<long>(); childrenOf[pid] = list; }
                        list.Add(m.ActorId);
                    }
                }

                var solved = new Dictionary<long, int> { { anchorId, anchorGen } };
                var queue = new Queue<long>();
                queue.Enqueue(anchorId);
                while (queue.Count > 0)
                {
                    long cur = queue.Dequeue();
                    int curGen = solved[cur];
                    // 向上：我的父母 = 我的 ParentIds
                    if (members.TryGetValue(cur, out var cm) && cm.ParentIds != null)
                    {
                        foreach (var pid in cm.ParentIds)
                        {
                            if (members.ContainsKey(pid) && !solved.ContainsKey(pid))
                            {
                                solved[pid] = curGen - 1;
                                queue.Enqueue(pid);
                            }
                        }
                    }
                    // 向下：以我为父的孩子
                    if (childrenOf.TryGetValue(cur, out var kids))
                    {
                        foreach (var kid in kids)
                        {
                            if (members.ContainsKey(kid) && !solved.ContainsKey(kid))
                            {
                                solved[kid] = curGen + 1;
                                queue.Enqueue(kid);
                            }
                        }
                    }
                }

                foreach (var m in members.Values)
                {
                    if (solved.TryGetValue(m.ActorId, out int g)) m.Gen = g;
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[FamilyRegistry] RebaseGens 异常: {ex.Message}");
            }
        }

        /// <summary>称号快照（域层版兜底链：官职→君主→储君→城主→功名→爵位→身份→国人）。公开供 UI 复用。</summary>
        public string BuildTitleSnapshot(Kingdom k, string kId, Actor a)
        {
            try
            {
                var rec = FindOfficeRecordSafe(kId, a.getID());
                if (rec != null)
                {
                    try { return OfficeManager.GetOfficeTitleForKingdom(rec.OfficeId, k); } catch { }
                }
                if (k.king != null && k.king.isAlive() && k.king.getID() == a.getID())
                {
                    return RegimeManager.GetKingTitle(k);
                }
                try
                {
                    string heirStr = KingdomDataExt.GetStr(k, "heir_id");
                    if (!string.IsNullOrEmpty(heirStr) && long.TryParse(heirStr, out long heirId) && heirId == a.getID())
                    {
                        return RegimeManager.GetHeirTitle(k);
                    }
                }
                catch { }
                if (k.cities != null)
                {
                    foreach (var c in k.cities)
                    {
                        if (c != null && c.leader != null && c.leader.getID() == a.getID()) return "城主";
                    }
                }
                // 功名
                if (a.hasTrait(TraitManager.TRAIT_ZHUANGYUAN)) return "状元";
                if (a.hasTrait(TraitManager.TRAIT_JINSHI)) return "进士";
                if (a.hasTrait(TraitManager.TRAIT_WUJINSHI)) return "武进士";
                if (a.hasTrait(TraitManager.TRAIT_JUREN)) return "举人";
                if (a.hasTrait(TraitManager.TRAIT_WUJUREN)) return "武举人";
                if (a.hasTrait(TraitManager.TRAIT_XIUCAI)) return "秀才";
                if (a.hasTrait(TraitManager.TRAIT_WUXIUCAI)) return "武秀才";
                // 爵位
                string[] tiers = { TraitManager.TIER_TIANZI, TraitManager.TIER_GONG, TraitManager.TIER_HOU, TraitManager.TIER_BO, TraitManager.TIER_ZI, TraitManager.TIER_NAN };
                string[] tierNames = { "天子", "公", "侯", "伯", "子", "邑" };
                for (int i = 0; i < tiers.Length; i++)
                {
                    if (a.hasTrait(tiers[i])) return tierNames[i] + "国之君";
                }
                if (a.hasTrait(TraitManager.TRAIT_GENIUS)) return "天纵奇才";
                try { return IdentityClassifier.GetIdentityName(IdentityClassifier.Instance.GetIdentity(a.getID())); } catch { }
                return "国人";
            }
            catch { return "国人"; }
        }

        private OfficeAppointmentRecord FindOfficeRecordSafe(string kId, long actorId)
        {
            try
            {
                var records = OfficeManager.Instance.GetRecordsPublic(kId);
                if (records == null) return null;
                string idStr = actorId.ToString();
                foreach (var r in records)
                {
                    if (r != null && r.PersonId == idStr) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>显赫判定（旁系收录门槛：官职/君主/储君/城主/功名/爵位）。</summary>
        private bool IsDistinguished(Kingdom k, string kId, Actor a, long kingId)
        {
            if (a == null || !a.isAlive()) return false;
            long aid = a.getID();
            if (aid == kingId) return true;
            if (FindOfficeRecordSafe(kId, aid) != null) return true;
            string title = BuildTitleSnapshot(k, kId, a);
            return title != "国人" && title != "";
        }

        private static FamilyMember CloneMember(FamilyMember m)
        {
            return new FamilyMember
            {
                ActorId = m.ActorId, Gen = m.Gen, SpouseId = m.SpouseId, Alive = m.Alive,
                Name = m.Name, Title = m.Title, DiedYear = m.DiedYear, JoinYear = m.JoinYear,
                ParentIds = m.ParentIds != null ? new List<long>(m.ParentIds) : new List<long>(),
                Distinguished = m.Distinguished,
                LastKnownKills = m.LastKnownKills,
                Martyr = m.Martyr
            };
        }

        private static bool ContainsMember(FamilyData f, long actorId)
        {
            if (f == null) return false;
            foreach (var m in f.Members) { if (m.ActorId == actorId) return true; }
            return false;
        }

        private static int CountAlive(FamilyData f)
        {
            int n = 0;
            foreach (var m in f.Members) { if (m.Alive) n++; }
            return n;
        }

        private static Actor FindActorInKingdom(Kingdom k, long actorId)
        {
            if (k?.units == null) return FindActorWorldwide(actorId);
            try
            {
                foreach (Actor u in k.units)
                {
                    if (u != null && u.isAlive() && u.getID() == actorId) return u;
                }
            }
            catch { }
            return FindActorWorldwide(actorId);
        }

        private static Actor FindActorWorldwide(long actorId)
        {
            try
            {
                Actor actor = World.world?.units?.get(actorId);
                return actor != null && actor.isAlive() ? actor : null;
            }
            catch { return null; }
        }

        private static List<Actor> SafeGetParents(Actor a)
        {
            var result = new List<Actor>();
            try
            {
                var parents = a.getParents();
                if (parents != null)
                {
                    foreach (var p in parents) { if (p != null) result.Add(p); }
                }
            }
            catch { }
            return result;
        }

        private static long GetSpouseIdSafe(Actor a)
        {
            try
            {
                var wife = HarmonyLib.Traverse.Create(a).Field("wife").GetValue<Actor>();
                if (wife != null) return wife.getID();
                var husband = HarmonyLib.Traverse.Create(a).Field("husband").GetValue<Actor>();
                if (husband != null) return husband.getID();
            }
            catch { }
            return 0;
        }

        /// <summary>复姓安全地取得家族姓；氏族正式名优先，人物持久化姓和真实名兜底。</summary>
        private static string ResolveFamilySurname(Actor actor)
        {
            try
            {
                var service = Naming.ZhuluChineseNameService.Instance;
                string surname = actor?.clan != null ? service.GetClanSurname(actor.clan) : "";
                if (string.IsNullOrEmpty(surname)) surname = service.GetPreferredSurname(actor);
                if (string.IsNullOrEmpty(surname)) surname = service.ResolveSurname(Naming.ActorAccess.GetRawName(actor));
                return string.IsNullOrEmpty(surname) ? "王" : surname;
            }
            catch { return "王"; }
        }

        // KinshipTracker 兄弟查询（RoyalRegistry 同款逻辑独立实现，避免跨类私有依赖）
        private static class KinshipTrackerSafe
        {
            public static List<Actor> GetAliveSiblings(Kingdom k, Actor person)
            {
                var result = new List<Actor>();
                try
                {
                    var parents = SafeGetParents(person);
                    if (parents.Count == 0 || k?.units == null) return result;
                    foreach (Actor u in k.units)
                    {
                        if (u == null || !u.isAlive() || u.getID() == person.getID()) continue;
                        var uParents = SafeGetParents(u);
                        foreach (var up in uParents)
                        {
                            foreach (var pp in parents)
                            {
                                if (up != null && pp != null && up.getID() == pp.getID())
                                {
                                    result.Add(u);
                                    break;
                                }
                            }
                            if (result.Count > 0 && result[result.Count - 1] == u) break;
                        }
                    }
                }
                catch { }
                return result;
            }
        }

        // ═══════════════════════ 快照 ═══════════════════════

        // ---- batch10-A1: schema v5 section ----
        public sealed class FamiliesState { public Dictionary<string, KingdomFamilies> Data = new Dictionary<string, KingdomFamilies>(); }
        public string SectionName => "families";
        public Dictionary<string, FamiliesState> CaptureStates()
        {
            return new Dictionary<string, FamiliesState>
            {
                ["__global__"] = new FamiliesState { Data = new Dictionary<string, KingdomFamilies>(_data) }
            };
        }
        public void ApplyStates(Dictionary<string, FamiliesState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _data.Clear();
            _styleStreak.Clear();
            if (st.Data == null) return;
            foreach (var kv in st.Data)
            {
                if (kv.Value == null) continue;
                // P1-7d sanitize: fold double suffixes on restore (same as v4 path)
                if (kv.Value.Notables != null)
                    foreach (var n in kv.Value.Notables)
                    {
                        if (n == null || string.IsNullOrEmpty(n.Name)) continue;
                        n.Name = n.Name.Replace("\u6c0f\u6c0f", "\u6c0f").Replace("\u90e8\u90e8", "\u90e8");
                    }
                _data[kv.Key] = kv.Value;
            }
        }



        /// <summary>运行时重置（新世界/串档拒绝——防谱牒跨档残留串档，SaveCore.ResetAllSystems 调用）。</summary>
        public void ResetRuntime()
        {
            _data.Clear();
            _styleStreak.Clear();
        }

        /// <summary>测试辅助：清空内存数据。</summary>
        public void ClearForTest()
        {
            _data.Clear();
            _styleStreak.Clear();
        }

        /// <summary>测试辅助：直接注入族系（绕过 World 依赖，验证快照往返）。</summary>
        public void UpsertForTest(KingdomFamilies kf)
        {
            if (kf != null) _data[kf.KingdomId] = kf;
        }
    }
}
