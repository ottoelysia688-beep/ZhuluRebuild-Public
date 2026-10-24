using ZhuluRebuild.Domain;
using ZhuluRebuild.Naming;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// P1-7c/7d 姓氏尾缀净化与书香门第状态机测试（2026-08-23）。
    /// StripClanSuffix：纯函数边界全覆盖；
    /// NotifyExamPass：经 UpsertForTest 注入内存族系的状态化验证（三门触发/游牧等价/重复静默）。
    /// </summary>
    public static class NamingHealTests
    {
        // ═══════════ StripClanSuffix 边界 ═══════════

        public static void TestStripSuffix()
        {
            // 东方：剥"氏"
            TestRunner.AssertEqual("訾", ZhuluChineseNameService.StripClanSuffix("訾氏"), "訾氏→訾");
            TestRunner.AssertEqual("王", ZhuluChineseNameService.StripClanSuffix("王氏"), "王氏→王");
            // 游牧：剥"部"
            TestRunner.AssertEqual("耶律", ZhuluChineseNameService.StripClanSuffix("耶律部"), "耶律部→耶律");
            // 嵌套双后缀（实锤场景形态）
            TestRunner.AssertEqual("訾", ZhuluChineseNameService.StripClanSuffix("訾氏氏"), "訾氏氏→訾");
            TestRunner.AssertEqual("柯", ZhuluChineseNameService.StripClanSuffix("柯部氏"), "柯部氏→柯");
            // 无后缀原样
            TestRunner.AssertEqual("王", ZhuluChineseNameService.StripClanSuffix("王"), "王→王原样");
            // 单字符保护（s.Length>1 守卫——绝不剥成空串）
            TestRunner.AssertEqual("氏", ZhuluChineseNameService.StripClanSuffix("氏"), "单'氏'保留");
            TestRunner.AssertEqual("部", ZhuluChineseNameService.StripClanSuffix("部"), "单'部'保留");
            // 空串安全
            TestRunner.AssertEqual("", ZhuluChineseNameService.StripClanSuffix(""), "空串→空串");
        }

        // ═══════════ 书香门第状态机（UpsertForTest 内存注入） ═══════════

        public static void TestNotifyExamPassJinshi()
        {
            var reg = FamilyRegistry.Instance;
            reg.ClearForTest();

            var kf = new FamilyRegistry.KingdomFamilies { KingdomId = "T1" };
            kf.Main.Members.Add(new FamilyRegistry.FamilyMember { ActorId = 100, Alive = true, Name = "甲" });
            reg.UpsertForTest(kf);

            // 秀才（rank 1）不计入门数
            reg.NotifyExamPass("T1", 100, TraitManager.TRAIT_XIUCAI, quietLogs: true);
            AssertState(reg, "T1", 0, false, "秀才及第不计功名门数");

            // 进士第1门
            reg.NotifyExamPass("T1", 100, TraitManager.TRAIT_JINSHI, quietLogs: true);
            AssertState(reg, "T1", 1, false, "一门后未触发");

            // 第2门
            reg.NotifyExamPass("T1", 100, TraitManager.TRAIT_JINSHI, quietLogs: true);
            AssertState(reg, "T1", 2, false, "两门后未触发");

            // 第3门 → 触发书香门第
            reg.NotifyExamPass("T1", 100, TraitManager.TRAIT_JINSHI, quietLogs: true);
            AssertState(reg, "T1", 3, true, "三门触发书香门第");

            // 已达成后再及第：完全静默——计数冻结在3、不加成（P1-7c 语义）
            reg.NotifyExamPass("T1", 100, TraitManager.TRAIT_ZHUANGYUAN, quietLogs: true);
            var kfCheck = reg.GetKingdomFamilies("T1");
            TestRunner.AssertEqual(3, kfCheck.Main.JinshiPassCount, "达成后计数冻结");
            TestRunner.AssertEqual(true, kfCheck.Main.Shuxiang, "达成标记保持");
            TestRunner.AssertEqual(48, kfCheck.Main.Prestige, "家声只加一次(+8)");

            // 游牧系魁首/汗庭学士同等级等价计门
            reg.ClearForTest();
            var kf2 = new FamilyRegistry.KingdomFamilies { KingdomId = "T2" };
            kf2.Main.Members.Add(new FamilyRegistry.FamilyMember { ActorId = 200, Alive = true, Name = "乙" });
            reg.UpsertForTest(kf2);

            reg.NotifyExamPass("T2", 200, TraitManager.NOMAD_COURT_SCHOLAR, quietLogs: true);   // 汗庭学士级 rank3
            reg.NotifyExamPass("T2", 200, TraitManager.NOMAD_COURT_SCHOLAR, quietLogs: true);
            reg.NotifyExamPass("T2", 200, TraitManager.NOMAD_CENTURION, quietLogs: true);       // 百夫长 rank2 不计（千夫长是武进士级rank3，见 KejuManager.cs:646）
            reg.NotifyExamPass("T2", 200, TraitManager.NOMAD_FIRST_SCHOLAR, quietLogs: true);   // 第3门
            AssertState(reg, "T2", 3, true, "游牧魁首系等价计门");

            reg.ClearForTest();
        }

        private static void AssertState(FamilyRegistry reg, string kId, int passCount, bool shuxiang, string name)
        {
            var kf = reg.GetKingdomFamilies(kId);
            TestRunner.AssertTrue(kf != null, name + ": 族系存在");
            if (kf == null) return;
            TestRunner.AssertEqual(passCount, kf.Main.JinshiPassCount, name + ": JinshiPassCount");
            TestRunner.AssertEqual(shuxiang, kf.Main.Shuxiang, name + ": Shuxiang");
        }
    }
}
