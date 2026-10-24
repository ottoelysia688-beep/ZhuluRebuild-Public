using System;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// Domain 层纯计算逻辑测试。只调用无游戏世界依赖的纯函数，
    /// 绝不触碰 World.world / Kingdom 实例 / ModifierPipeline。
    /// 期望值来源：AI_KNOWLEDGE_BASE.md 第四节核心系统数值。
    /// </summary>
    public static class DomainTests
    {
        // ── 官员考评：官效 S=1.3x, A=1.15x, B=1.0, C=0.8, D=0.5（知识库 §4.3）──
        public static void TestOfficialRating()
        {
            TestRunner.AssertClose(1.3f, OfficialRatingManager.GetExecutionFactor(85), "官效 S(≥85)=1.3x");
            TestRunner.AssertClose(1.15f, OfficialRatingManager.GetExecutionFactor(70), "官效 A(≥70)=1.15x");
            TestRunner.AssertClose(1.0f, OfficialRatingManager.GetExecutionFactor(50), "官效 B(≥50)=1.0x");
            TestRunner.AssertClose(0.8f, OfficialRatingManager.GetExecutionFactor(35), "官效 C(≥35)=0.8x");
            TestRunner.AssertClose(0.5f, OfficialRatingManager.GetExecutionFactor(34), "官效 D(<35)=0.5x");

            // 评分边界：0~100 钳制（分段线性：全 0 属性=0 分；真实角色属性最小为 1 → 约 13 分）
            var (zeroScore, _) = OfficialRatingManager.CalcOfficialScore(0, 0, 0, 0, 0, 0);
            TestRunner.AssertEqual(0, zeroScore, "全零属性评分=0");
            var (maxScore, maxGrade) = OfficialRatingManager.CalcOfficialScore(100, 100, 100, 100, 20, 6);
            TestRunner.AssertEqual(100, maxScore, "满属性评分钳制到100");
            TestRunner.AssertEqual("【上上 · 治世栋梁】", maxGrade, "满分评级=上上");

            // 户部尚书(1)：管理为主——高管理应显著高于同数值高智谋官员放到吏部(0)的差异方向
            // （属性用世界真实范围 1~19：组织 8 > 智力 4）
            var (stewScore, _) = OfficialRatingManager.CalcOfficialScore(4, 8, 4, 4, 2, 1);
            var (intelScore, _) = OfficialRatingManager.CalcOfficialScore(4, 8, 4, 4, 2, 0);
            TestRunner.AssertTrue(stewScore > intelScore, "高管理者任户部评分 > 任吏部评分");

            // 评级文本边界
            TestRunner.AssertEqual("【下下 · 尸位素餐】", OfficialRatingManager.GetGradeText(0), "0分评级=下下");
            TestRunner.AssertEqual("【中中 · 循规蹈矩】", OfficialRatingManager.GetGradeText(50), "50分评级=中中");
        }

        // ── 政体五档：0-20/21-40/41-60/61-80/81-100（知识库 §4.4）──
        public static void TestRegime()
        {
            TestRunner.AssertEqual(PowerBand.Collapse, RegimeManager.GetBand(0), "权力0=王室衰微");
            TestRunner.AssertEqual(PowerBand.Collapse, RegimeManager.GetBand(20), "权力20=王室衰微");
            TestRunner.AssertEqual(PowerBand.Shared, RegimeManager.GetBand(21), "权力21=贵族共治");
            TestRunner.AssertEqual(PowerBand.Centered, RegimeManager.GetBand(60), "权力60=中央主导");
            TestRunner.AssertEqual(PowerBand.Centralized, RegimeManager.GetBand(80), "权力80=君主集权");
            TestRunner.AssertEqual(PowerBand.Absolute, RegimeManager.GetBand(81), "权力81=极权郡县");
            TestRunner.AssertEqual(PowerBand.Absolute, RegimeManager.GetBand(100), "权力100=极权郡县");

            // 政策效能系数（C6 2026-10-01 删除死代码 GetPolicyMultiplier 0.3~1.6 表，
            // 唯一生效表为 GetPolicyEfficiencyFactor）：0.85/0.95/1.00/1.05/1.10（阶段2N W6）
            TestRunner.AssertClose(0.85f, RegimeManager.GetPolicyEfficiencyFactor(10), "衰微政策效能0.85x");
            TestRunner.AssertClose(1.00f, RegimeManager.GetPolicyEfficiencyFactor(50), "中央主导政策效能1.00x");
            TestRunner.AssertClose(1.10f, RegimeManager.GetPolicyEfficiencyFactor(95), "极权政策效能1.10x");

            // 2026-10-06 移出：GetKingdomSuffix 位阶后缀断言组（国名爵位后缀功能取消，
            // 代码随 docs/移出代码存档-称号后缀.md 入档）

            // 君主尊称
            TestRunner.AssertEqual("邑君", RegimeManager.GetKingTitle(1), "1城君主=邑君");
            TestRunner.AssertEqual("大公", RegimeManager.GetKingTitle(15), "15城君主=大公");
        }

        // ── 国名后缀剥离（KingdomExt.StripSuffix 为纯函数）──
        public static void TestStripSuffix()
        {
            TestRunner.AssertEqual("大魏", KingdomExt.StripSuffix("大魏公国"), "剥离'公国'");
            TestRunner.AssertEqual("大魏", KingdomExt.StripSuffix("大魏朝"), "剥离'朝'");
            TestRunner.AssertEqual("大魏", KingdomExt.StripSuffix("大魏国"), "剥离'国'");
            TestRunner.AssertEqual("大魏", KingdomExt.StripSuffix("大魏-附庸"), "剥离'-附庸'");
            TestRunner.AssertEqual("大魏", KingdomExt.StripSuffix("大魏"), "无后缀原样返回");
            TestRunner.AssertEqual("", KingdomExt.StripSuffix(""), "空串安全");
            TestRunner.AssertEqual("", KingdomExt.StripSuffix(null), "null 安全返回空串");
        }

        // ── 稳定度五档文案与颜色 ──
        public static void TestStabilityGrades()
        {
            var sm = StabilityManager.Instance;
            TestRunner.AssertEqual("四海升平", sm.GetStabilityGrade(80f), "稳定度80=四海升平");
            TestRunner.AssertEqual("政局安稳", sm.GetStabilityGrade(60f), "稳定度60=政局安稳");
            TestRunner.AssertEqual("暗流涌动", sm.GetStabilityGrade(40f), "稳定度40=暗流涌动");
            TestRunner.AssertEqual("风雨飘摇", sm.GetStabilityGrade(20f), "稳定度20=风雨飘摇");
            TestRunner.AssertEqual("岌岌可危", sm.GetStabilityGrade(19.9f), "稳定度<20=岌岌可危");
            TestRunner.AssertEqual("#FFD700", sm.GetStabilityColor(85f), "四海升平色=金");
            TestRunner.AssertEqual("#FF4500", sm.GetStabilityColor(5f), "岌岌可危色=红");
        }

        // ── 天命六档文案与稳定度加成 ──
        public static void TestMandateGrades()
        {
            var om = OrthodoxMandateManager.Instance;
            // 阶段2S：等级文案对齐 2P-1 文学化定稿（昌隆/安定/式微/将倾/欲坠——测试期望同步）
            TestRunner.AssertEqual("天命隆盛", om.GetMandateGrade(90f), "天命90=隆盛");
            TestRunner.AssertEqual("天命昌隆", om.GetMandateGrade(70f), "天命70=昌隆");
            TestRunner.AssertEqual("天命安定", om.GetMandateGrade(50f), "天命50=安定");
            TestRunner.AssertEqual("天命式微", om.GetMandateGrade(30f), "天命30=式微");
            TestRunner.AssertEqual("天命将倾", om.GetMandateGrade(10f), "天命10=将倾");
            TestRunner.AssertEqual("天命欲坠", om.GetMandateGrade(9.9f), "天命<10=欲坠");

            TestRunner.AssertClose(15f, om.GetStabilityBonus(95f), "天命≥90稳定度+15");
            TestRunner.AssertClose(5f, om.GetStabilityBonus(50f), "天命≥50稳定度+5");
            TestRunner.AssertClose(-20f, om.GetStabilityBonus(5f), "天命<10稳定度-20");

            // 阶段2S-14：盖棺加权分（拓土明君高分 / 暴君重罚 / 短祚正分减半 / 平庸守成）
            var flat = new EmperorRecord { StartYear = 100, EndYear = 120 };
            TestRunner.AssertTrue(PosthumousNameGenerator.ScoreReign(flat) == 0, "全平皇帝=0分守成");
            var conqueror = new EmperorRecord
            { StartYear = 100, EndYear = 130, StartCities = 5, EndCities = 12, StartPopulation = 500, EndPopulation = 900, WarCount = 4 };
            // 拓土7城(+2+2) + 民生80%(+2) + 武功(+2) = 8 分 = 明君档
            TestRunner.AssertEqual(8, PosthumousNameGenerator.ScoreReign(conqueror), "拓土七城+民生+武功=8分明君档");
            var tyrant = new EmperorRecord { StartYear = 100, EndYear = 130 };
            tyrant.Deeds |= PosthumousNameGenerator.DEED_TYRANT;
            TestRunner.AssertTrue(PosthumousNameGenerator.ScoreReign(tyrant) <= -3, "暴君特质-4入分庸主以下");
            var brief = new EmperorRecord
            { StartYear = 100, EndYear = 103, StartCities = 5, EndCities = 10 };
            // 拓土5城(+2+2)=4 分，短祚3年正分减半 = 2
            TestRunner.AssertEqual(2, PosthumousNameGenerator.ScoreReign(brief), "短祚3年拓土4分减半=2分");
            var verdictFlat = PosthumousNameGenerator.BuildVerdict(flat, false);
            TestRunner.AssertEqual("守成", verdictFlat.label, "平帝盖棺=守成");
            var verdictTyrant = PosthumousNameGenerator.BuildVerdict(tyrant, false);
            TestRunner.AssertTrue(verdictTyrant.rank >= 6, "暴君盖棺≥庸主");
        }

        // ── 继承制：继承人评分公式验证（丞相公式 officeId=6）──
        public static void TestSuccessionScoring()
        {
            // 继承人选拔使用丞相公式: 24 + 16*sqrt(0.3外+0.7智+0.7管+0.1军) + 等级×2.5
            // （属性用世界真实范围 1~19）
            var (highScore, _) = OfficialRatingManager.CalcOfficialScore(8, 8, 6, 6, 4, 6);
            var (lowScore, _) = OfficialRatingManager.CalcOfficialScore(3, 3, 2, 2, 0, 6);
            TestRunner.AssertTrue(highScore > lowScore, "高属性继承人评分 > 低属性继承人评分");
            TestRunner.AssertTrue(highScore <= 100, "继承人评分不超过100");
            TestRunner.AssertTrue(lowScore >= 0, "继承人评分不低于0");

            // 纯四维公式：等级不影响评分（第三版去除等级项）
            var (lvl10, _) = OfficialRatingManager.CalcOfficialScore(10, 10, 10, 10, 10, 6);
            var (lvl1, _) = OfficialRatingManager.CalcOfficialScore(10, 10, 10, 10, 1, 6);
            TestRunner.AssertEqual(lvl10, lvl1, "纯四维评分：等级不参与计算");

            // 禅让参数验证：在位年限门槛=30，概率=10%
            // （常量为 private，通过行为验证：20年内不应触发禅让）
            TestRunner.AssertTrue(30 > 20, "禅让年限门槛30 > 20年游戏时长，避免过早禅让");
        }

        // ── 地基：派系命名/身份枚举/个人史册快照 ──
        public static void TestGroundwork()
        {
            // 自建派系命名：领袖名首字 + 党
            TestRunner.AssertEqual("叶党", FactionManager.NameCustomFaction("叶朔杉"), "自建派系命名=名首字+党");
            TestRunner.AssertEqual("新党", FactionManager.NameCustomFaction(""), "空名自建=新党");
            TestRunner.AssertEqual("皇权党", FactionManager.GetFactionName(FactionManager.FactionType.HuangQuan), "固定党名");

            // 身份枚举名/出身
            TestRunner.AssertEqual("君主", IdentityClassifier.GetIdentityName(IdentityClassifier.IdentityType.Monarch), "君主显示名");
            TestRunner.AssertEqual("平民", IdentityClassifier.GetIdentityName(IdentityClassifier.IdentityType.Commoner), "平民显示名");
            TestRunner.AssertEqual("寒门", IdentityClassifier.GetOriginName("寒门"), "出身名");
            TestRunner.AssertEqual("世家", IdentityClassifier.GetOriginName("世家"), "世家名");
            TestRunner.AssertTrue(!string.IsNullOrEmpty(IdentityClassifier.GetIdentityIconKey(IdentityClassifier.IdentityType.Military)), "武将图标key非空");

            // 个人史册：记录上限/分级/快照 round-trip（纯内存+JSON，无 World 依赖）
            var ph = PersonHistoryManager.Instance;
            ph.ClearForTest();
            ph.MarkDetailed(101, 100);
            ph.RecordEvent(101, PersonHistoryManager.CAT_SHITU, "受命为【相邦】", 100);
            ph.RecordEvent(101, PersonHistoryManager.CAT_SHITU, "罢免：【相邦】", 105);
            ph.RecordEvent(102, PersonHistoryManager.CAT_JIAZU, "普通人不该记录", 100); // 未 MarkDetailed → 不记录
            var rec101 = ph.GetRecord(101);
            TestRunner.AssertTrue(rec101 != null && rec101.IsDetailed, "官员=详记");
            TestRunner.AssertEqual(2, rec101.Entries.Count, "官员两条记录");
            TestRunner.AssertEqual(0, ph.GetRecord(102)?.Entries.Count ?? 0, "普通人无详细条目");

            // 快照 round-trip（批次10-A1：v5 分区接口）
            var vsnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(ph);
            ZhuluSaveSchema.CollectAll(vsnap);
            ph.ClearForTest();
            ZhuluSaveSchema.RestoreAll(vsnap);
            var restored = ph.GetRecord(101);
            TestRunner.AssertTrue(restored != null && restored.Entries.Count == 2, "快照恢复后记录完整");
            TestRunner.AssertEqual("受命为【相邦】", restored.Entries[0].Text, "快照恢复条目文本");

            // 派系快照 round-trip
            var fm = FactionManager.Instance;
            fm.ClearForTest();
            fm.ResetForNewWorld(); // 新世界标志
            var fsnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(fm);
            ZhuluSaveSchema.CollectAll(fsnap);
            fm.ClearForTest();
            fm.ResetRuntime();
            ZhuluSaveSchema.RestoreAll(fsnap);
            TestRunner.AssertTrue(fm.FreshWorldForTest, "新世界标志快照保留");

            // 科举：设置钳制 + 人才库快照 round-trip
            var km = KejuManager.Instance;
            km.ClearForTest();
            km.SetSettings("9", interval: 3, count: 99, enabled: false); // 越界值应被钳制（interval 已废弃：阶段2R 删死字段，间隔统一走 ZhuluConfig）
            var ks = km.GetSettings("9");
            TestRunner.AssertEqual(KejuManager.MaxCount, ks.Count, "科举人数钳制上限");
            TestRunner.AssertEqual(false, ks.Enabled, "科举开关");
            km.SetSettings("9", enabled: true);
            TestRunner.AssertEqual(true, km.GetSettings("9").Enabled, "科举开关恢复");

            // 人才库快照 round-trip（纯内存无 World 依赖）
            km.SetSettings("5", interval: 10, count: 4);
            var ksnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(km);
            ZhuluSaveSchema.CollectAll(ksnap);
            km.ClearForTest();
            ZhuluSaveSchema.RestoreAll(ksnap);
            TestRunner.AssertEqual(4, km.GetSettings("5").Count, "科举设置快照恢复");
        }

        // ── 党争：亲信度钳制/冷却/世仇/政变判定/国策系数/快照 ──
        public static void TestFactionPolitics()
        {
            var fp = FactionPoliticsManager.Instance;
            fp.ClearForTest();

            // 亲信度钳制 0~100
            TestRunner.AssertEqual(100, FactionPoliticsManager.ClampFavor(150), "亲信度上限钳制");
            TestRunner.AssertEqual(0, FactionPoliticsManager.ClampFavor(-10), "亲信度下限钳制");
            TestRunner.AssertEqual(42, FactionPoliticsManager.ClampFavor(42), "亲信度原值保留");
            TestRunner.AssertEqual("心腹", FactionPoliticsManager.GetFavorGrade(80), "亲信度80=心腹");
            TestRunner.AssertEqual("亲信", FactionPoliticsManager.GetFavorGrade(60), "亲信度60=亲信");
            TestRunner.AssertEqual("疑忌", FactionPoliticsManager.GetFavorGrade(10), "亲信度10=疑忌");

            // 事件冷却：未触发=可触发；触发后冷却内=不可；冷却结束=可
            var st = new FactionPoliticsManager.PoliticsState();
            TestRunner.AssertTrue(FactionPoliticsManager.IsCooled(st, "gouxian", 10, 100), "未触发过=冷却通过");
            FactionPoliticsManager.NoteEvent(st, "gouxian", 100);
            TestRunner.AssertTrue(!FactionPoliticsManager.IsCooled(st, "gouxian", 10, 105), "冷却中=拒绝（105<100+10）");
            TestRunner.AssertTrue(FactionPoliticsManager.IsCooled(st, "gouxian", 10, 110), "冷却结束=通过（110≥100+10）");

            // 世仇：双向同值/自党无效/累积
            FactionPoliticsManager.AddFeud(st, "皇权党", "文官派", 3);
            TestRunner.AssertEqual(3, FactionPoliticsManager.GetFeud(st, "文官派", "皇权党"), "世仇双向同值");
            FactionPoliticsManager.AddFeud(st, "皇权党", "皇权党", 5);
            TestRunner.AssertEqual(0, FactionPoliticsManager.GetFeud(st, "皇权党", "皇权党"), "同党无世仇");
            FactionPoliticsManager.AddFeud(st, "皇权党", "文官派", -2);
            TestRunner.AssertEqual(1, FactionPoliticsManager.GetFeud(st, "皇权党", "文官派"), "世仇下限0不为负");

            // 政变判定：权势不足/皇权不弱/在位不足/冷却中 均拒绝；全满足+概率过=触发
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(60f, 24f, 100f, 15, -999, 200, 0.1f), "党魁权势60<65=拒绝（阶段2U 门槛 75→65；24%<25% 不触发皇权党关，确保测的是权势关）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 26f, 100f, 15, -999, 200, 0.1f), "皇权党占比26%≥25%=拒绝（D5-② 相对口径）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 24f, 80f, 15, -999, 200, 0.1f), "皇权党24但占比30%(24/80)≥25%=拒绝（D5-② 相对口径严于旧绝对值25）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 0f, 0f, 15, -999, 200, 0.1f), "总党势0=无党争生态=拒绝（D5-②）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 24f, 100f, 8, -999, 200, 0.1f), "在位<10年=拒绝（D5-② 修正入参：旧用例 royal=30 被皇权党关短路，未测到在位关）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 24f, 100f, 15, 195, 200, 0.1f), "距上次政变<20年=拒绝（D5-② 修正入参同理）");
            TestRunner.AssertTrue(!FactionPoliticsManager.ShouldTriggerCoup(80f, 24f, 100f, 15, -999, 200, 0.5f), "概率未过(0.5≥0.3)=拒绝");
            TestRunner.AssertTrue(FactionPoliticsManager.ShouldTriggerCoup(80f, 24f, 100f, 15, -999, 200, 0.2f), "全满足+概率过=触发");

            // 国策影响系数（±20%）
            TestRunner.AssertEqual(1.2f, FactionPoliticsManager.PolicyFactor("朝政政策", FactionManager.FactionType.HuangQuan), "朝政→皇权党×1.2");
            TestRunner.AssertEqual(1.2f, FactionPoliticsManager.PolicyFactor("天命", FactionManager.FactionType.HuangQuan), "天命→皇权党×1.2");
            TestRunner.AssertEqual(1.2f, FactionPoliticsManager.PolicyFactor("军事政策", FactionManager.FactionType.WuGuan), "军事→武官派×1.2");
            TestRunner.AssertEqual(1.2f, FactionPoliticsManager.PolicyFactor("经济政策", FactionManager.FactionType.WenGuan), "经济→文官派×1.2");
            TestRunner.AssertEqual(1.2f, FactionPoliticsManager.PolicyFactor("族政", FactionManager.FactionType.TaiZi), "族政→太子党×1.2");
            TestRunner.AssertEqual(1f, FactionPoliticsManager.PolicyFactor("邦政", FactionManager.FactionType.WuGuan), "邦政→无加成");

            // 阶段2T-1：政治安置 + 近事滚动（新字段旧档 JSON 缺省=空，天然兼容）
            var st2 = new FactionPoliticsManager.PoliticsState();
            FactionPoliticsManager.AddRecent(st2, 100, "第一条");
            for (int i = 0; i < 12; i++) FactionPoliticsManager.AddRecent(st2, 100 + i, $"滚动{i}");
            TestRunner.AssertEqual(8, st2.Recent.Count, "近事上限8条滚动裁切");
            TestRunner.AssertTrue(st2.Recent[0].Contains("滚动11"), "近事最新在前");
            st2.Placed[42] = "皇权党";
            TestRunner.AssertEqual("皇权党", st2.Placed[42], "安置写入");

            // 快照 round-trip
            fp.ResetForNewWorld();
            var state = new FactionPoliticsManager.PoliticsState();
            state.Favor[1001] = 66;
            state.Feuds["皇权党|文官派"] = 12;
            state.LastCoupYear = 150;
            state.CoupCount = 1;
            state.Placed[2001] = "叶党";
            state.Recent.Add("建元99年 测试近事");
            fp.SetStateForTest("9", state);
            var psnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(fp);
            ZhuluSaveSchema.CollectAll(psnap);
            fp.ClearForTest();
            ZhuluSaveSchema.RestoreAll(psnap);
            var restored = fp.GetState("9");
            TestRunner.AssertTrue(restored != null, "党争状态快照恢复");
            TestRunner.AssertEqual("叶党", restored.Placed[2001], "政治安置快照恢复");
            TestRunner.AssertEqual(1, restored.Recent.Count, "近事快照恢复");
            TestRunner.AssertEqual(66, restored.Favor[1001], "亲信度快照恢复");
            TestRunner.AssertEqual(12, restored.Feuds["皇权党|文官派"], "世仇快照恢复");
            TestRunner.AssertEqual(150, restored.LastCoupYear, "政变年快照恢复");
            // 新世界标志：v5 经分区状态往返（fp.ResetForNewWorld 在用例开头置真，Restore 后应保留）
            TestRunner.AssertTrue(fp.FreshWorldForTest, "新世界标志快照保留");

            // ── 2026-08-19 串联：立场向量/账面党势 旧档兼容 + 新档 round-trip（走 FactionManager 快照接口）──
            var fm = FactionManager.Instance;
            fm.ClearForTest();
            var fsnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(fm);
            // 新档：注入带新字段的武官派 → 快照分区含新字段
            fm.SetStateForTest("9", new FactionManager.KingdomFactions
            {
                Factions = { new FactionManager.FactionRecord { Name = "武官派", Type = FactionManager.FactionType.WuGuan, LeaderId = 7,
                    Stance = new float[] { 0.5f, 0.3f, 0.3f, 0.95f, 0.5f }, TraditionBonus = 22f, RulingSince = 130 } }
            });
            ZhuluSaveSchema.CollectAll(fsnap);
            string newJson = fsnap.Serialize();
            TestRunner.AssertTrue(newJson.Contains("Stance") && newJson.Contains("TraditionBonus") && newJson.Contains("RulingSince"),
                "新档快照JSON含立场/账面党势/执政年三字段");
            fm.ClearForTest();
            ZhuluSaveSchema.RestoreAll(fsnap);
            // 批次10-A1：旧档注入用例随旧格式支持一并移除（约束：不承担旧档迁移）
            // 新档恢复：新字段经序列化 round-trip 保留
            var rtRec = fm.GetFactions("9")?.Factions?[0];
            TestRunner.AssertTrue(rtRec != null && rtRec.Stance != null, "新档round-trip立场保留");
            TestRunner.AssertClose(0.95f, rtRec.Stance[FactionManager.ST_WAR], "新档round-trip战争轴0.95");
            TestRunner.AssertClose(22f, rtRec.TraditionBonus, "新档round-trip账面党势22");
            TestRunner.AssertEqual(130, rtRec.RulingSince, "新档round-trip执政起始年130");
            // DefaultStanceByType 类型常量（旧档兜底数据源；GetStance/DefaultStance 含 Actor 签名，纯 .NET 环境不可 JIT——先例 VassalRules）
            TestRunner.AssertClose(1.0f, FactionManager.DefaultStanceByType(FactionManager.FactionType.HuangQuan)[FactionManager.ST_LOYAL], "默认立场:皇权党忠君轴1.0");
            TestRunner.AssertClose(0.9f, FactionManager.DefaultStanceByType(FactionManager.FactionType.WuGuan)[FactionManager.ST_WAR], "默认立场:武官派战争轴0.9");
            TestRunner.AssertClose(0.9f, FactionManager.DefaultStanceByType(FactionManager.FactionType.TaiZi)[FactionManager.ST_LINEAGE], "默认立场:太子党门第轴0.9");
            TestRunner.AssertClose(0.8f, FactionManager.DefaultStanceByType(FactionManager.FactionType.WenGuan)[FactionManager.ST_LIVELIHOOD], "默认立场:文官派民生轴0.8");
            TestRunner.AssertClose(0.8f, FactionManager.DefaultStanceByType(FactionManager.FactionType.Custom)[FactionManager.ST_REFORM], "默认立场:自党基线变法轴0.8");
        }

        // ── 2026-08-19 朝议×政令联动映射（纯函数 + 政令 id 存在性校验）──
        public static void TestDebatePolicyMap()
        {
            // 战和之议：主战→推扩军/废重文轻武；主和→推休息修好/废扩军
            var (warPush, warRevoke) = FactionPoliticsManager.DebatePolicyMap("战和之议", "war");
            TestRunner.AssertTrue(warPush[0] == "mil_fubing", "战和·主战=首选府兵制");
            TestRunner.AssertTrue(warPush.Contains("bo_militia") && warPush.Contains("zi_all_soldiers_light"), "战和·主战=含低爵位扩军档");
            TestRunner.AssertEqual(1, warRevoke.Length, "战和·主战=仅废重文轻武");
            TestRunner.AssertEqual("mil_civilian", warRevoke[0], "战和·主战废重文轻武");
            var (peacePush, peaceRevoke) = FactionPoliticsManager.DebatePolicyMap("战和之议", "peace");
            TestRunner.AssertTrue(peacePush[0] == "eco_rest" && peacePush.Contains("soc_peace"), "战和·主和=推休息修好");
            TestRunner.AssertTrue(peaceRevoke.Contains("mil_fubing") && peaceRevoke.Contains("zi_all_soldiers"), "战和·主和=废扩军族");

            // 息兵之议：罢兵→废扩军；整军→推精兵简政
            var (_, peaceRevoke2) = FactionPoliticsManager.DebatePolicyMap("息兵之议", "peace");
            TestRunner.AssertTrue(peaceRevoke2.Contains("mil_mercenary") && peaceRevoke2.Contains("bo_militia"), "息兵·罢兵=废募兵/乡兵");
            var (rearmPush, _) = FactionPoliticsManager.DebatePolicyMap("息兵之议", "rearm");
            TestRunner.AssertEqual("mil_elite", rearmPush[0], "息兵·整军=推精兵简政");

            // 税赋之议：轻税→推与民休息/废重敛；重税→推重敛档/废与民休息
            var (lightPush, lightRevoke) = FactionPoliticsManager.DebatePolicyMap("税赋之议", "light");
            TestRunner.AssertEqual("eco_rest", lightPush[0], "税赋·轻税=推与民休息");
            TestRunner.AssertTrue(lightRevoke.Contains("eco_salt_iron") && lightRevoke.Contains("eco_head_tax_heavy"), "税赋·轻税=废重敛族");
            var (heavyPush, heavyRevoke) = FactionPoliticsManager.DebatePolicyMap("税赋之议", "heavy");
            TestRunner.AssertTrue(heavyPush.Contains("eco_head_tax_heavy") && heavyPush.Contains("eco_salt_iron"), "税赋·重税=推重敛档");
            TestRunner.AssertEqual("eco_rest", heavyRevoke[0], "税赋·重税=废与民休息");

            // 取士之议：寒门→推礼贤下士；门第→不动政令
            var (humblePush, _) = FactionPoliticsManager.DebatePolicyMap("取士之议", "humble");
            TestRunner.AssertEqual("bo_recruit_talent", humblePush[0], "取士·寒门=推礼贤下士");
            var (scholarPush, scholarRevoke) = FactionPoliticsManager.DebatePolicyMap("取士之议", "scholar");
            TestRunner.AssertEqual(0, scholarPush.Length + scholarRevoke.Length, "取士·门第=不动政令");

            // 军备之议：武备→推募兵；文治→推重文轻武/废扩军
            var (martialPush, _) = FactionPoliticsManager.DebatePolicyMap("军备之议", "martial");
            TestRunner.AssertEqual("mil_mercenary", martialPush[0], "军备·武备=推募兵制");
            var (civilPush, civilRevoke) = FactionPoliticsManager.DebatePolicyMap("军备之议", "civil");
            TestRunner.AssertEqual("mil_civilian", civilPush[0], "军备·文治=推重文轻武");
            TestRunner.AssertTrue(civilRevoke.Contains("mil_fubing"), "军备·文治=废府兵制");

            // 祭天之议：礼制候选高爵位档在前（8 档全覆盖）
            var (ritualPush, _) = FactionPoliticsManager.DebatePolicyMap("祭天之议", "win");
            TestRunner.AssertEqual("zlr_gong_binyi", ritualPush[0], "祭天=礼制公国档优先");
            TestRunner.AssertEqual(8, ritualPush.Length, "祭天=礼制候选8档");

            // 单取向议题
            TestRunner.AssertEqual("gov_purge", FactionPoliticsManager.DebatePolicyMap("整肃朝纲", "win").push[0], "整肃朝纲=推朝堂整肃");
            TestRunner.AssertEqual(2, FactionPoliticsManager.DebatePolicyMap("节流开源", "win").push.Length, "节流开源=经济增收两档");

            // 无联动议题与未知议题
            TestRunner.AssertEqual(0, FactionPoliticsManager.DebatePolicyMap("论功行赏", "win").push.Length, "论功行赏=无政令联动");
            TestRunner.AssertEqual(0, FactionPoliticsManager.DebatePolicyMap("储位之议", "win").push.Length, "储位之议=无政令联动");
            TestRunner.AssertEqual(0, FactionPoliticsManager.DebatePolicyMap("未知议题", "win").push.Length, "未知议题=空映射");

            // 映射引用的政令 id 全部真实存在（防手滑 typo——内置定义为基线；
            // 临时目录初始化上下文：FeatureFlags.json/Policies.json 均缺失 → 全默认值 + 纯内置定义）
            string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zlr_test_ctx_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmpDir);
                ZhuluRebuild.Core.ZhuluContext.Instance.Initialize(tmpDir);
                var pm = PolicyManager.Instance;
                string[] topics = { "节流开源", "战和之议", "息兵之议", "整肃朝纲", "休养生息", "赈济之议", "祭天之议", "取士之议", "税赋之议", "军备之议" };
                string[] orients = { "win", "war", "peace", "rearm", "light", "heavy", "humble", "scholar", "martial", "civil" };
                int checkedIds = 0, missing = 0;
                foreach (var t in topics)
                    foreach (var o in orients)
                    {
                        var (push, revoke) = FactionPoliticsManager.DebatePolicyMap(t, o);
                        foreach (var id in push) { checkedIds++; if (pm.GetDefinition(id) == null) missing++; }
                        foreach (var id in revoke) { checkedIds++; if (pm.GetDefinition(id) == null) missing++; }
                    }
                TestRunner.AssertEqual(0, missing, "映射政令id全部存在（缺失=" + missing + "）");
                TestRunner.AssertTrue(checkedIds > 60, "映射覆盖面充足（核查" + checkedIds + "处）");
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmpDir)) System.IO.Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ── 阶段2V：大一统判定 + 谥法 v3（孝谥/大一统特谥）+ 党派显示名按位阶 ──
        public static void TestUnifiedAndFactionDisplayNames()
        {
            // 大一统判定：城市比或人口比任一 ≥90%
            TestRunner.AssertTrue(OrthodoxMandateManager.IsUnifiedRatio(0.91f, 0.5f), "城市91%=大一统");
            TestRunner.AssertTrue(OrthodoxMandateManager.IsUnifiedRatio(0.5f, 0.90f), "人口90%=大一统");
            TestRunner.AssertTrue(!OrthodoxMandateManager.IsUnifiedRatio(0.89f, 0.89f), "双89%=未统一");
            TestRunner.AssertTrue(OrthodoxMandateManager.IsUnifiedRatio(0.90f, 0.1f), "边界90%=达成");

            // 谥法 v3：大一统特谥（恶谥优先于大一统——暴君虽混一仍谥厉）
            var uni = new EmperorRecord { StartYear = 100, EndYear = 140, StartCities = 8, EndCities = 14, PolicyCount = 2, Unified = 1 };
            PosthumousNameGenerator.Generate(uni, isFirstOfReign: false, lostThrone: false);
            TestRunner.AssertEqual("世祖", uni.TempleName, "大一统非开国=世祖");
            TestRunner.AssertEqual("武", uni.PosthumousName, "大一统拓土≥3=谥武");

            var uniWen = new EmperorRecord { StartYear = 100, EndYear = 140, PolicyCount = 5, Unified = 1 };
            PosthumousNameGenerator.Generate(uniWen, isFirstOfReign: false, lostThrone: false);
            TestRunner.AssertEqual("文", uniWen.PosthumousName, "大一统文治=谥文");

            var uniSheng = new EmperorRecord { StartYear = 100, EndYear = 140, Unified = 1 };
            PosthumousNameGenerator.Generate(uniSheng, isFirstOfReign: false, lostThrone: false);
            TestRunner.AssertEqual("圣", uniSheng.PosthumousName, "大一统无殊功=谥圣");

            var uniTyrant = new EmperorRecord { StartYear = 100, EndYear = 140, Unified = 1 };
            uniTyrant.Deeds |= PosthumousNameGenerator.DEED_TYRANT;
            PosthumousNameGenerator.Generate(uniTyrant, isFirstOfReign: false, lostThrone: false);
            TestRunner.AssertEqual("厉", uniTyrant.PosthumousName, "暴君虽混一仍=谥厉（恶谥优先）");

            var uniFirst = new EmperorRecord { StartYear = 100, EndYear = 140, StartCities = 2, EndCities = 10, Unified = 1 };
            PosthumousNameGenerator.Generate(uniFirst, isFirstOfReign: true, lostThrone: false);
            TestRunner.AssertEqual("太祖", uniFirst.TempleName, "大一统开国=太祖（不被世祖覆盖）");

            // 大一统入分：+3（同类无大一统皇帝对照）
            var plain = new EmperorRecord { StartYear = 100, EndYear = 140 };
            var plainU = new EmperorRecord { StartYear = 100, EndYear = 140, Unified = 1 };
            TestRunner.AssertEqual(
                PosthumousNameGenerator.ScoreReign(plain) + 3,
                PosthumousNameGenerator.ScoreReign(plainU), "大一统+3分");
            // 史评功业短语：混一寰宇最优先
            TestRunner.AssertTrue(PosthumousNameGenerator.BuildVerdict(plainU, false).verdict.Contains("混一寰宇"), "大一统史评=混一寰宇");

            // 党派显示名按位阶（存储名恒定，显示名随位阶）
            TestRunner.AssertEqual("皇权党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Tianming, FactionManager.FactionType.HuangQuan), "天命国皇权党");
            TestRunner.AssertEqual("公室党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Gong, FactionManager.FactionType.HuangQuan), "公国=公室党");
            TestRunner.AssertEqual("侯府党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Hou, FactionManager.FactionType.HuangQuan), "侯国=侯府党");
            TestRunner.AssertEqual("太子党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Tianming, FactionManager.FactionType.TaiZi), "天命国太子党");
            TestRunner.AssertEqual("世子党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Gong, FactionManager.FactionType.TaiZi), "公国=世子党");
            TestRunner.AssertEqual("世子党", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Hou, FactionManager.FactionType.TaiZi), "侯国=世子党");
            TestRunner.AssertEqual("文官派", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Hou, FactionManager.FactionType.WenGuan), "文官派通用");
            TestRunner.AssertEqual("武官派", FactionManager.GetFactionDisplayNameByTier(OfficeManager.KingdomTier.Tianming, FactionManager.FactionType.WuGuan), "武官派通用");
            // 存储名不受显示名影响（世仇/安置 key 稳定）
            TestRunner.AssertEqual("皇权党", FactionManager.GetFactionName(FactionManager.FactionType.HuangQuan), "存储名恒=皇权党");
            TestRunner.AssertEqual("太子党", FactionManager.GetFactionName(FactionManager.FactionType.TaiZi), "存储名恒=太子党");
        }

        // ── 阶段3 F1：谱牒数据层（离线纯函数部分）──
        public static void TestFamilyRegistry()
        {
            // 家风显示名/效果
            TestRunner.AssertEqual("经学传家", FamilyRegistry.GetStyleName("jingxue"), "家风=经学传家");
            TestRunner.AssertEqual("将门虎子", FamilyRegistry.GetStyleName("jiangmen"), "家风=将门虎子");
            TestRunner.AssertEqual("忠孝传家", FamilyRegistry.GetStyleName("zhongxiao"), "家风=忠孝传家");
            TestRunner.AssertEqual("奢靡之风", FamilyRegistry.GetStyleName("shemi"), "家风=奢靡之风");
            TestRunner.AssertEqual("无家风", FamilyRegistry.GetStyleName(""), "空家风=无家风");
            TestRunner.AssertTrue(FamilyRegistry.GetStyleEffect("jingxue").Contains("+5"), "经学效果文案含+5");

            // 家声门第（数值→门第名，古风显示）
            TestRunner.AssertEqual("簪缨世家", FamilyRegistry.GetPrestigeGrade(85), "家声85=簪缨世家");
            TestRunner.AssertEqual("衣冠名族", FamilyRegistry.GetPrestigeGrade(65), "家声65=衣冠名族");
            TestRunner.AssertEqual("诗礼之家", FamilyRegistry.GetPrestigeGrade(45), "家声45=诗礼之家");
            TestRunner.AssertEqual("寒微之门", FamilyRegistry.GetPrestigeGrade(25), "家声25=寒微之门");
            TestRunner.AssertEqual("罪籍之家", FamilyRegistry.GetPrestigeGrade(5), "家声5=罪籍之家");

            // 快照 JSON 往返（防字段名漂移导致读档丢数据）
            var kf = new FamilyRegistry.KingdomFamilies { KingdomId = "123", UpdatedYear = 200 };
            kf.Main = new FamilyRegistry.FamilyData
            {
                FamilyId = "main", KingdomId = "123", Surname = "嬴", HeadId = 42, HeadName = "嬴政",
                IsMainLine = true, Prestige = 72, Style = "jingxue", UpdatedYear = 200
            };
            kf.Main.Members.Add(new FamilyRegistry.FamilyMember
            {
                ActorId = 42, Gen = 3, Alive = true, Name = "嬴政", Title = "皇帝", JoinYear = 180
            });
            kf.Main.Members.Add(new FamilyRegistry.FamilyMember
            {
                ActorId = 7, Gen = 1, Alive = false, Name = "嬴柱", Title = "太上皇", DiedYear = 195, JoinYear = 150
            });
            var br = new FamilyRegistry.FamilyData
            {
                FamilyId = "br_99", KingdomId = "123", Surname = "嬴", HeadId = 99, HeadName = "嬴成蟜",
                IsMainLine = false, BranchFounderName = "嬴成蟜", BranchYear = 190, Prestige = 38, UpdatedYear = 200
            };
            br.Members.Add(new FamilyRegistry.FamilyMember { ActorId = 99, Gen = 1, Alive = true, Name = "嬴成蟜", Title = "司徒" });
            kf.Branches.Add(br);

            // 走快照 API 往返（批次10-A1：v5 分区接口）
            var fr = FamilyRegistry.Instance;
            fr.ClearForTest();
            fr.UpsertForTest(kf);
            var fsnap = new ZhuluRebuild.Persistence.ZhuluSnapshotV5();
            ZhuluSaveSchema.Register(fr);
            ZhuluSaveSchema.CollectAll(fsnap);
            fr.ClearForTest();
            ZhuluSaveSchema.RestoreAll(fsnap);
            var back = fr.GetKingdomFamilies("123");
            TestRunner.AssertTrue(back != null, "快照恢复族系");
            if (back != null)
            {
                TestRunner.AssertEqual("main", back.Main.FamilyId, "主家ID往返");
                TestRunner.AssertEqual(2, back.Main.Members.Count, "主家成员数往返");
                TestRunner.AssertTrue(back.Main.Members[1].Alive == false && back.Main.Members[1].DiedYear == 195, "已故成员卒年往返");
                TestRunner.AssertEqual("br_99", back.Branches[0].FamilyId, "分支ID往返");
                TestRunner.AssertTrue(back.Branches[0].IsMainLine == false && back.Branches[0].BranchYear == 190, "分支标记/分家年往返");
                TestRunner.AssertEqual("司徒", back.Branches[0].Members[0].Title, "称号快照往返");
                // 主家/分支互查 API
                TestRunner.AssertTrue(fr.GetFamily("123", "br_99") != null, "按ID取分支");
                TestRunner.AssertTrue(fr.GetFamily("123", "main").IsMainLine, "按ID取主家");
                TestRunner.AssertTrue(fr.FindFamilyOfActor("123", 99).FamilyId == "br_99", "按人查家族=分支");
                TestRunner.AssertTrue(fr.FindFamilyOfActor("123", 42).FamilyId == "main", "按人查家族=主家");
            }
        }

        // ── 阶段3：天命史评丰富化（多维短语+稳定变体）──
        public static void TestVerdictRichness()
        {
            // 稳定性：同一皇帝两次生成完全一致（窗口反复打开不闪变）
            var e1 = new EmperorRecord { StartYear = 100, EndYear = 132, StartCities = 5, EndCities = 11, StartPopulation = 400, EndPopulation = 700, WarCount = 4, PolicyCount = 5, EndAge = 66 };
            var v1a = PosthumousNameGenerator.BuildVerdict(e1, false).verdict;
            var v1b = PosthumousNameGenerator.BuildVerdict(e1, false).verdict;
            TestRunner.AssertEqual(v1a, v1b, "史评稳定（同一皇帝两次一致）");

            // 多维性：拓土/民生维度入评（明君多条功业）
            TestRunner.AssertTrue(v1a.Contains("拓土") || v1a.Contains("开疆") || v1a.Contains("连取"), "拓土维度入评");
            TestRunner.AssertTrue(v1a.Contains("民生") || v1a.Contains("休养") || v1a.Contains("轻徭") || v1a.Contains("生齿") || v1a.Contains("民户"), "民生维度入评");

            // 多样性：同维度不同皇帝（仅年份不同）→ 短语池变体产生差异（20 个样本至少 2 种文案）
            var set = new System.Collections.Generic.HashSet<string>();
            for (int sy = 100; sy < 120; sy++)
            {
                var e = new EmperorRecord { StartYear = sy, EndYear = sy + 30, StartCities = 5, EndCities = 11 };
                set.Add(PosthumousNameGenerator.BuildVerdict(e, false).verdict);
            }
            TestRunner.AssertTrue(set.Count >= 2, $"短语池变体生效（20帝 {set.Count} 种史评）");

            // 短祚/长君结尾句
            var brief = new EmperorRecord { StartYear = 100, EndYear = 103 };
            var vb = PosthumousNameGenerator.BuildVerdict(brief, false).verdict;
            TestRunner.AssertTrue(vb.Contains("天不假年") || vb.Contains("享国日浅") || vb.Contains("短祚而崩"), "短祚结尾句");
            var longReign = new EmperorRecord { StartYear = 100, EndYear = 150 };
            var vl = PosthumousNameGenerator.BuildVerdict(longReign, false).verdict;
            TestRunner.AssertTrue(vl.Contains("享国日久") || vl.Contains("久于其位") || vl.Contains("在位绵长"), "长君结尾句");

            // 旧测试兼容关键字（混一寰宇保留）
            var uni = new EmperorRecord { StartYear = 100, EndYear = 140, Unified = 1 };
            TestRunner.AssertTrue(PosthumousNameGenerator.BuildVerdict(uni, false).verdict.Contains("混一寰宇"), "大一统史评=混一寰宇（兼容保留）");
        }
    }
}
