using System;
using System.Reflection;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 外部联动契约守护测试（生态红线）。
    /// 背景：社区 mod「天下名人传记」ZhuluLink 以反射方式消费本 mod 内部成员——
    /// 字段/属性改名不会产生任何编译错误，只会让下游 mod 静默失效（catch-all 回落）。
    /// 本测试按 ZhuluLink 的实际反射方式逐项校验契约点：红 = 有下游 mod 断链。
    /// 维护规则：任何触碰下列成员的重构，必须同步更新本测试与 Scripts/Interop/ZhuluInterop.cs。
    /// </summary>
    public static class InteropContractTests
    {
        public static void Run()
        {
            TestRunner.AssertTrue(ZhuluInterop.ApiVersion >= 1, "门面 ApiVersion ≥ 1");

            // ── 契约点 1：PersonHistoryManager（个人史册——传记五分类镜像） ──
            var phm = typeof(PersonHistoryManager);
            TestRunner.AssertTrue(phm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null,
                "契约:PersonHistoryManager.Instance 可反射");
            TestRunner.AssertTrue(phm.GetField("_records", BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "契约:PersonHistoryManager._records 私有字段存在（ZhuluLink 直挖路径）");
            var pRec = phm.GetNestedType("PersonRecord");
            TestRunner.AssertTrue(pRec != null, "契约:PersonHistoryManager+PersonRecord 嵌套类型存在");
            TestRunner.AssertTrue(pRec?.GetProperty("Entries", BindingFlags.Public | BindingFlags.Instance) != null,
                "契约:PersonRecord.Entries 自动属性（ZhuluLink 注释明言必须 GetProperty）");
            var hEntry = phm.GetNestedType("HistoryEntry");
            TestRunner.AssertTrue(hEntry != null, "契约:PersonHistoryManager+HistoryEntry 嵌套类型存在");
            TestRunner.AssertTrue(hEntry?.GetProperty("Year", BindingFlags.Public | BindingFlags.Instance) != null
                && hEntry.GetProperty("Category", BindingFlags.Public | BindingFlags.Instance) != null
                && hEntry.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance) != null,
                "契约:HistoryEntry.Year/Category/Text 自动属性");

            // ── 契约点 2：OrthodoxMandateManager（帝王庙号/谥号——传记盖棺显示） ──
            var omm = typeof(OrthodoxMandateManager);
            TestRunner.AssertTrue(omm.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null,
                "契约:OrthodoxMandateManager.Instance 可反射");
            TestRunner.AssertTrue(omm.GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "契约:OrthodoxMandateManager._history 私有字段存在（ZhuluLink 直挖路径）");
            var dynRec = typeof(DynastyRecord);
            TestRunner.AssertTrue(dynRec.GetProperty("Emperors", BindingFlags.Public | BindingFlags.Instance) != null,
                "契约:DynastyRecord.Emperors 属性");
            var empRec = typeof(EmperorRecord);
            TestRunner.AssertTrue(empRec.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance) != null
                && empRec.GetProperty("PosthumousName", BindingFlags.Public | BindingFlags.Instance) != null
                && empRec.GetProperty("TempleName", BindingFlags.Public | BindingFlags.Instance) != null,
                "契约:EmperorRecord.Name/PosthumousName/TempleName 属性");

            // ── 契约点 3：ZhuluTierService（王国爵位——传记封爵升降检测） ──
            TestRunner.AssertTrue(typeof(ZhuluTierService).GetMethod("GetKingdomTierId",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(global::Kingdom) }, null) != null,
                "契约:ZhuluTierService.GetKingdomTierId(Kingdom) 签名");
            TestRunner.AssertTrue(typeof(ZhuluTierService).GetMethod("GetTierName",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null) != null,
                "契约:ZhuluTierService.GetTierName(string) 签名");

            // ── 门面行为（空世界安全：绝不抛异常，空数据返回空结果） ──
            var emperors = ZhuluInterop.GetEmperorRecords();
            TestRunner.AssertTrue(emperors != null, "门面:GetEmperorRecords 永不返回 null");
            var entries = ZhuluInterop.GetPersonHistory(-12345L, "仕途", "功名");
            TestRunner.AssertTrue(entries != null && entries.Count == 0, "门面:GetPersonHistory 未知 id 返回空列表");
            var all = ZhuluInterop.GetPersonHistory(-12345L);
            TestRunner.AssertTrue(all != null, "门面:GetPersonHistory 无分类过滤可用");
            TestRunner.AssertTrue(ZhuluInterop.GetEmperorPosthumous("___不存在___") == null,
                "门面:GetEmperorPosthumous 未匹配返回 null");
            TestRunner.AssertTrue(ZhuluInterop.GetEmperorPosthumous(null) == null,
                "门面:GetEmperorPosthumous null 安全");
            TestRunner.AssertTrue(ZhuluInterop.GetKingdomTierId(null) == null,
                "门面:GetKingdomTierId null 安全");
            TestRunner.AssertTrue(ZhuluInterop.GetKingdomTierName(null) == "",
                "门面:GetKingdomTierName null 安全");
        }
    }
}
