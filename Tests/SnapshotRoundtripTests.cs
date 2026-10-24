using System.Collections.Generic;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 批次10-A1：快照注册表守卫（替代 v4 容器字段往返测试——v5 的保真由 SchemaV5Tests 引擎级+各系统语义断言承担）。
    /// 守卫点：23 个分区必须全部注册、名称逐一匹配——防"漏接系统导致其数据静默不再持久化"。
    /// </summary>
    public static class SnapshotRoundtripTests
    {
        private static readonly string[] Expected =
        {
            "treasury", "stability", "succession", "vassal", "corruption",
            "civilization", "kingdom_ext", "reparations", "claims",
            "keju", "families", "royal_families", "factions", "faction_politics",
            "person_histories", "identities", "policies", "offices",
            "orthodox_mandate", "nomadic_mandate", "chronicle", "era_names", "warfare"
        };

        public static void Run()
        {
            // 生产注册表由 SaveCore.EnsureSchemaRegistered 填充（WarmSchema 公开入口）
            ZhuluSaveCore.WarmSchema();

            TestRunner.AssertEqual(25, ZhuluSaveSchema.RegisteredCount, "分区注册总数=25（2026-10-24 增 llm_comments/llm_chronicles）");
            var names = new List<string>(ZhuluSaveSchema.RegisteredNames());
            foreach (var expect in Expected)
                TestRunner.AssertTrue(names.Contains(expect), "分区已注册: " + expect);

            // v5 序列化烟测：空快照可序列化/读回，版本号=5
            var snap = new ZhuluSnapshotV5 { WorldId = "smoke", SavedAtYear = 1 };
            var back = ZhuluSnapshotV5.Deserialize(snap.Serialize());
            TestRunner.AssertTrue(back != null && back.Version == 5 && back.WorldId == "smoke", "V5空快照烟测");

            // 旧版拒绝
            TestRunner.AssertTrue(ZhuluSnapshotV5.Deserialize("{\"Version\":4}") == null, "v4旧快照拒绝");
        }
    }
}
