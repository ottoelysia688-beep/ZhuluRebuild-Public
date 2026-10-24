using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    public enum SuccessionType
    {
        DirectInheritance,    // 顺位/直系继承
        CollateralBranch,     // 旁系继承
        UnexpectedElevation,  // 意外拥立
        ForeignClanCandidate, // 异宗/更迭
        Unknown               // 未知降级
    }

    public class KingChangeFact
    {
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public string OldKingName { get; set; }
        public string NewKingName { get; set; }
        public int Year { get; set; }
        public SuccessionType Category { get; set; }
        // 阶段2O：禅让/篡位旗标（分类枚举未含——谥法引擎 Deeds 位标记用）
        public bool IsAbdication { get; set; }
        public bool IsUsurpation { get; set; }
    }

    public class DynastyObserver
    {
        public static DynastyObserver Instance { get; } = new DynastyObserver();

        private DynastyObserver() { }

        // ── 阶段2A A1-6：禅让/篡位标记（由 Succession/Corruption/Faction 触发时打标，观察时读取清除）──
        private readonly HashSet<string> _pendingAbdication = new HashSet<string>();
        private readonly HashSet<string> _pendingUsurpation = new HashSet<string>();

        // ── 阶段2O：最近一次换君事实（供 OrthodoxMandateManager 关闭皇帝记录时读取结局——禅让/被篡位）──
        private readonly Dictionary<string, KingChangeFact> _lastFacts = new Dictionary<string, KingChangeFact>();

        /// <summary>取走并清除该王国最近一次换君事实（无则 null）。</summary>
        public KingChangeFact ConsumeLastFact(string kingdomId)
        {
            lock (_lastFacts)
            {
                if (_lastFacts.TryGetValue(kingdomId, out var f))
                {
                    _lastFacts.Remove(kingdomId);
                    return f;
                }
            }
            return null;
        }

        public void MarkAbdication(string kingdomId) { lock (_pendingAbdication) { _pendingAbdication.Add(kingdomId); } }
        public void MarkUsurpation(string kingdomId)
        {
            lock (_pendingUsurpation) { _pendingUsurpation.Add(kingdomId); }
            // 阶段2B 包2：篡位礼治重置（新朝从零积累，德政重建合法性）
            try { ZhuluLiZhiService.ResetOnUsurpation(kingdomId); } catch { }
        }

        /// <summary>2026-08-18：预埋换君事实（篡位路径在 ObserveKingChange 之前就要盖棺旧帝——CloseEmperor 读结局位）。</summary>
        public void PreMarkFact(string kingdomId, bool isUsurpation, bool isAbdication, string oldKingName, string newKingName, int year)
        {
            lock (_lastFacts)
            {
                _lastFacts[kingdomId] = new KingChangeFact
                {
                    KingdomId = kingdomId,
                    OldKingName = oldKingName ?? "",
                    NewKingName = newKingName ?? "",
                    Year = year,
                    Category = SuccessionType.UnexpectedElevation,
                    IsUsurpation = isUsurpation,
                    IsAbdication = isAbdication
                };
            }
        }

        /// <summary>
        /// 2026-08-18 篡位后全局处理（CorruptionManager/SuccessionManager 篡位成功后调用）：
        /// ① 异姓 → OrthodoxMandateManager.HandleUsurpation（天命国改朝换代：关旧朝+新朝卡片+保护期）
        /// ② 异姓 → 前朝宗室标记 + 谱牒隔离（旧王族归前朝望族，新王族次年重建）
        /// ③ 清洗旧朝中央官席（全员罢免+权势清零——提拔新人心腹自年度补位，防连环篡位）
        /// ④ 中央权力重置（篡位者实权在握，不继承旧朝衰微值）
        /// ⑤ 非天命国异姓篡位 → 国号改为新姓。
        /// </summary>
        public void ProcessDynastyChange(Kingdom k, string kId, Actor usurper, Actor oldKing, int year)
        {
            try
            {
                if (k == null || usurper == null) return;
                string usurperName = usurper.getName() ?? "新君";
                string oldKingName = oldKing != null ? (oldKing.getName() ?? "旧君") : "旧君";

                // 异姓判定（同姓篡位=家天下内部更迭，不改朝）
                bool clanChanged = true;
                try
                {
                    if (oldKing != null && oldKing.clan != null && usurper.clan != null && oldKing.clan == usurper.clan)
                        clanChanged = false;
                }
                catch { }

                // ① 体制转换只在明确篡位路径发生；普通继承绝不进入此处。
                CivilizationType oldCivilization = KingdomCivilizationService.Instance.Get(k);
                string usurperSpecies = KingdomCivilizationService.GetActorSpeciesId(usurper);
                CivilizationType targetCivilization = KingdomCivilizationService.Instance.GetSpeciesDefault(usurperSpecies);
                bool crossSystem = oldCivilization != targetCivilization;
                bool wasMandateHolder = oldCivilization == CivilizationType.Eastern
                    ? OrthodoxMandateManager.Instance.IsMandateHolder(kId)
                    : NomadicMandateManager.Instance.IsMandateHolder(kId);

                if (crossSystem)
                {
                    // 原体系天命先失去，再锁定新体系；新体系是否受命由各自现有资格检查决定。
                    if (oldCivilization == CivilizationType.Eastern
                        && OrthodoxMandateManager.Instance.IsMandateHolder(kId))
                        OrthodoxMandateManager.Instance.ForceTransferMandate(k, null, year, "异体系改朝换代");
                    else if (oldCivilization == CivilizationType.Nomadic
                        && NomadicMandateManager.Instance.IsMandateHolder(kId))
                        NomadicMandateManager.Instance.RevokeForRegimeChange(k, year);
                }
                KingdomCivilizationService.Instance.RebindByRuler(k, usurper, "篡位改朝换代");

                // 用户定稿：异体系人物篡夺原天命/天眷持有国后，若目标体系当前空悬且本国
                // 已满足该体系现有资格，立即转为目标体系持有国；否则只落到按国力计算的普通等级。
                if (crossSystem && wasMandateHolder)
                {
                    if (targetCivilization == CivilizationType.Eastern)
                        OrthodoxMandateManager.Instance.TryGrantVacantTo(k, year);
                    else
                        NomadicMandateManager.Instance.TryGrantVacantTo(k, year);
                }
                if (crossSystem)
                {
                    // 同帧清掉旧体系等级特质，避免人物面板在下一年度前仍显示旧爵位/部族等级。
                    if (targetCivilization == CivilizationType.Eastern) ZhuluTierService.ForceTierTraitForCurrentStatus(k);
                    else NomadicTierService.ForceTierTraitForCurrentStatus(k);
                }

                // ② 同为东方体系的异姓篡位沿用既有改朝换代史；跨体系不得先错误重授东方天命。
                string newDynasty = null;
                if (clanChanged && targetCivilization == CivilizationType.Eastern && !crossSystem)
                {
                    try { newDynasty = OrthodoxMandateManager.Instance.HandleUsurpation(k, usurper, year); } catch { }
                }

                // 预埋结局事实（旧帝盖棺定谥读 DEED_USURPED——ObserveKingChange 尚未跑到）
                PreMarkFact(kId, true, false, oldKingName, usurperName, year);

                // ③ 前朝宗室标记（异姓）
                if (clanChanged && oldKing != null)
                {
                    try { MarkFormerRoyalFamily(oldKing.getID().ToString(), year, kId, k.name ?? "", oldKingName, usurperName); } catch { }
                    // 谱牒隔离：旧王族档案归前朝，新王族次年按新君重建（防先祖录两朝混册）
                    try { FamilyRegistry.Instance.ResetOnDynastyChange(kId, oldKing); } catch { }
                }

                // ③ 清洗旧朝：罢免全部中央官席（新朝用人自年度补位/科举——旧权贵不再盘踞）
                try
                {
                    foreach (var officeId in OfficeManager.OfficesList)
                    {
                        var app = OfficeManager.Instance.GetAppointment(kId, officeId);
                        if (app != null && !string.IsNullOrEmpty(app.PersonId))
                        {
                            OfficeManager.Instance.DismissOfficer(kId, k.name ?? "", officeId, year, "新朝清洗");
                        }
                    }
                }
                catch { }

                // ④ 中央权力重置：篡位者兵权在握（原值多被权臣压至 <30——继承则新朝天生衰微）
                try { KingdomExt.SetPowerValue(kId, 60); } catch { }

                // ⑤ 非天命国异姓篡位：改国号（cn_base_name → 新姓，并立即写入纯国名）
                if (clanChanged && string.IsNullOrEmpty(newDynasty) && string.IsNullOrEmpty(KingdomExt.GetManualName(kId)))
                {
                    try
                    {
                        var naming = Naming.ZhuluChineseNameService.Instance;
                        string surname = usurper.clan != null ? naming.GetClanSurname(usurper.clan) : "";
                        if (string.IsNullOrEmpty(surname)) surname = naming.GetPreferredSurname(usurper);
                        if (string.IsNullOrEmpty(surname)) surname = naming.ResolveSurname(Naming.ActorAccess.GetRawName(usurper));
                        if (string.IsNullOrEmpty(surname)) surname = "新";
                        Persistence.KingdomDataExt.SetStr(k, "cn_base_name", surname);
                        string stored = KingdomExt.GetStoredName(kId, surname);
                        if (k.data != null) k.data.name = stored;
                        ZhuluContext.Instance.LogInfo($"[DynastyObserver] 篡位改号: {k.name} → {stored}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[DynastyObserver] ProcessDynastyChange 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 换君分类（阶段2A A1-6）：
        /// 禅让标记→禅位；篡位标记→篡位；亲子→直系继承；同 clan→旁系继承；异 clan→异宗更迭。
        /// </summary>
        public KingChangeFact ObserveKingChange(string kingdomId, string kingdomName, string oldKingName, string newKingName,
            string oldKingId, string newKingId, int year)
        {
            SuccessionType category = SuccessionType.Unknown;
            string text;

            bool isAbdication = false, isUsurpation = false;
            lock (_pendingAbdication) { isAbdication = _pendingAbdication.Remove(kingdomId); }
            lock (_pendingUsurpation) { isUsurpation = _pendingUsurpation.Remove(kingdomId); }

            if (isAbdication)
            {
                category = SuccessionType.DirectInheritance;
                text = $"<color={UI.ZhuluUIStyle.Hex.Imperial}>{oldKingName}</color> 禅位于<color=#6ca3fd>{newKingName}</color>。";
            }
            else if (isUsurpation)
            {
                category = SuccessionType.UnexpectedElevation;
                text = $"<color={UI.ZhuluUIStyle.Hex.Negative}>{newKingName}</color> 篡位称王，取代<color={UI.ZhuluUIStyle.Hex.Imperial}>{oldKingName}</color>。";
            }
            else
            {
                bool isChild = false;
                try
                {
                    if (!string.IsNullOrEmpty(oldKingId) && !string.IsNullOrEmpty(newKingId))
                        isChild = KinshipTracker.IsParent(oldKingId, newKingId);
                }
                catch { }

                bool sameClan = false;
                try
                {
                    if (!string.IsNullOrEmpty(oldKingId) && !string.IsNullOrEmpty(newKingId))
                    {
                        Actor oldK = World.world?.units?.get(long.Parse(oldKingId));
                        Actor newK = World.world?.units?.get(long.Parse(newKingId));
                        if (oldK?.clan != null && newK?.clan != null && oldK.clan == newK.clan)
                            sameClan = true;
                    }
                }
                catch { }

                if (isChild || sameClan)
                {
                    category = SuccessionType.DirectInheritance;
                    text = $"<color=#6ca3fd>{newKingName}</color> 登基，继承【{kingdomName}】王位。";
                }
                else
                {
                    category = SuccessionType.ForeignClanCandidate;
                    text = $"<color=#6ca3fd>{newKingName}</color> 入继大统，开创【{kingdomName}】新朝。";
                    // 阶段2A A1-7/A1-8：改朝换代——旧王室成员标记前朝宗室 + 史册 + 播报
                    MarkFormerRoyalFamily(oldKingId, year, kingdomId, kingdomName, oldKingName, newKingName);
                }
            }

            var fact = new KingChangeFact
            {
                KingdomId = kingdomId,
                KingdomName = kingdomName,
                OldKingName = oldKingName,
                NewKingName = newKingName,
                Year = year,
                Category = category,
                IsAbdication = isAbdication,   // 阶段2O：谥法引擎结局位标记
                IsUsurpation = isUsurpation
            };

            // 阶段2O：暂存最近换君事实（OrthodoxMandateManager 关闭皇帝记录时 Consume）
            lock (_lastFacts) { _lastFacts[kingdomId] = fact; }

            ZhuluContext.Instance.LogInfo($"[DynastyObserver] 观察到换君: 王国={kingdomName}, 旧君={oldKingName ?? "无"}, 新君={newKingName}, 分类={category}");

            // 联动记入史册（A4-2：统一由 DynastyObserver 记 KingChange，避免双记）
            ChronicleManager.Instance.AddEntry(
                year,
                ChronicleEventType.KingChange,
                kingdomId,
                kingdomName,
                newKingName,
                text,
                isMajor: true
            );

            return fact;
        }

        /// <summary>
        /// 阶段2A A1-7/A1-8：改朝换代处理——旧王室存活成员标记「前朝宗室」特质，
        /// 史册记 DynasticChange + 世界播报。
        /// </summary>
        private void MarkFormerRoyalFamily(string oldKingId, int year, string kingdomId, string kingdomName,
            string oldKingName, string newKingName)
        {
            try
            {
                if (string.IsNullOrEmpty(oldKingId)) return;
                Actor oldKing = World.world?.units?.get(long.Parse(oldKingId));
                if (oldKing?.clan == null) return;

                // 旧王室存活成员打前朝宗室标记
                int marked = 0;
                foreach (Actor member in oldKing.clan.units)
                {
                    if (member == null || !member.isAlive()) continue;
                    try
                    {
                        if (!member.hasTrait(TraitManager.TRAIT_FORMER_ROYAL))
                        {
                            member.addTrait(TraitManager.TRAIT_FORMER_ROYAL);
                            marked++;
                        }
                    }
                    catch { }
                }

                // 史册：改朝换代
                ChronicleManager.Instance.AddEntry(
                    year,
                    ChronicleEventType.DynasticChange,
                    kingdomId,
                    kingdomName,
                    newKingName,
                    $"<color={UI.ZhuluUIStyle.Hex.Negative}>改朝换代</color>：<color={UI.ZhuluUIStyle.Hex.Imperial}>{oldKingName}</color> 一脉失国，<color=#6ca3fd>{newKingName}</color> 开创新朝，前朝宗室{marked}人尚存于世。",
                    isMajor: true
                );
                try { WorldTip.showNow($"【{kingdomName}】改朝换代！{oldKingName} 失国，{newKingName} 开创{kingdomName}新朝！", false, "top", 8f, UI.ZhuluUIStyle.Hex.Imperial); } catch { }

                ZhuluContext.Instance.LogInfo($"[DynastyObserver] 改朝换代: {kingdomName} {oldKingName}→{newKingName}, 前朝宗室标记 {marked} 人");
            }
            catch { }
        }
    }
}
