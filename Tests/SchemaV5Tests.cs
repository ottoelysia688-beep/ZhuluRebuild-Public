using System.Collections.Generic;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 批次10-A1：快照 Schema v5 引擎测试。
    /// 核心验证点：Sections 值经 JSON 序列化→JToken→MakeGenericType 转回强类型字典的全链路保真
    /// （这是与 v4 最大的机制差异，也是最容易翻车的地方）。
    /// </summary>
    public static class SchemaV5Tests
    {
        // ── 测试用状态与分区 ──
        public sealed class DemoState
        {
            public long Num;
            public float Flt = 1f;
            public string Text;
            public bool Flag;
            public List<int> Nums;
            public Dictionary<string, long> Map;
        }

        public sealed class DemoSection : ISnapshotSection<DemoState>
        {
            public Dictionary<string, DemoState> Store = new Dictionary<string, DemoState>();
            public int ApplyCalls;
            public string SectionName { get { return "demo"; } }
            public Dictionary<string, DemoState> CaptureStates()
            {
                var d = new Dictionary<string, DemoState>();
                foreach (var kv in Store) d[kv.Key] = kv.Value;
                return d;
            }
            public void ApplyStates(Dictionary<string, DemoState> data)
            {
                ApplyCalls++;
                if (data == null) return;
                foreach (var kv in data) Store[kv.Key] = kv.Value;
            }
        }

        public sealed class StringSection : ISnapshotSection<string>
        {
            public Dictionary<string, string> Store = new Dictionary<string, string>();
            public string SectionName { get { return "strs"; } }
            public Dictionary<string, string> CaptureStates() { return new Dictionary<string, string>(Store); }
            public void ApplyStates(Dictionary<string, string> data)
            {
                if (data == null) return;
                foreach (var kv in data) Store[kv.Key] = kv.Value;
            }
        }

        public static void Run()
        {
            TestPocoRoundTrip();
            TestStringPayload();
            TestMissingSectionSkipped();
            TestOldVersionRejected();
            TestDuplicateRegisterIdempotent();
        }

        private static void TestPocoRoundTrip()
        {
            ZhuluSaveSchema.ClearForTest();
            var src = new DemoSection();
            src.Store["k1"] = new DemoState { Num = -42, Flt = 3.14f, Text = "含|竖线:与\"引号\"", Flag = true,
                Nums = new List<int> { 1, 2, 3 }, Map = new Dictionary<string, long> { ["a"] = 7L } };
            src.Store["空键位"] = new DemoState();   // 默认值也要保真
            ZhuluSaveSchema.Register(src);

            var snap = new ZhuluSnapshotV5 { WorldId = "W", SavedAtYear = 33 };
            ZhuluSaveSchema.CollectAll(snap);
            string json = snap.Serialize();

            var back = ZhuluSnapshotV5.Deserialize(json);
            TestRunner.AssertTrue(back != null, "V5序列化往返非空");
            TestRunner.AssertEqual(5, back.Version, "V5版本号");
            TestRunner.AssertEqual("W", back.WorldId, "WorldId保真");

            var dst = new DemoSection();   // 独立目标实例——模拟读档侧内存
            ZhuluSaveSchema.Register(dst);
            var snap2 = ZhuluSnapshotV5.Deserialize(json);
            ZhuluSaveSchema.RestoreAll(snap2);

            TestRunner.AssertEqual(1, dst.ApplyCalls, "Apply恰好一次");
            TestRunner.AssertTrue(dst.Store.ContainsKey("k1") && dst.Store.ContainsKey("空键位"), "两键齐恢复");
            if (dst.Store.ContainsKey("k1"))
            {
                var s = dst.Store["k1"];
                TestRunner.AssertEqual(-42L, s.Num, "Num保真");
                TestRunner.AssertClose(3.14f, s.Flt, "Flt保真");
                TestRunner.AssertEqual("含|竖线:与\"引号\"", s.Text, "Text特殊字符保真");
                TestRunner.AssertEqual(true, s.Flag, "Flag保真");
                TestRunner.AssertEqual(3, s.Nums != null ? s.Nums.Count : -1, "List保真");
                TestRunner.AssertEqual(7L, s.Map != null && s.Map.ContainsKey("a") ? s.Map["a"] : -1L, "Map保真");
            }
            var e = dst.Store["空键位"];
            TestRunner.AssertEqual(0L, e.Num, "默认Num");
            TestRunner.AssertClose(1f, e.Flt, "默认Flt");
        }

        private static void TestStringPayload()
        {
            ZhuluSaveSchema.ClearForTest();
            var src = new StringSection();
            src.Store["g"] = "朝代|王国|起|止|因|皇帝";
            ZhuluSaveSchema.Register(src);

            var snap = new ZhuluSnapshotV5();
            ZhuluSaveSchema.CollectAll(snap);
            var dst = new StringSection();
            ZhuluSaveSchema.Register(dst);
            ZhuluSaveSchema.RestoreAll(ZhuluSnapshotV5.Deserialize(snap.Serialize()));

            TestRunner.AssertEqual("朝代|王国|起|止|因|皇帝", dst.Store.ContainsKey("g") ? dst.Store["g"] : "", "string载荷保真(历史卷轴通道)");
        }

        private static void TestMissingSectionSkipped()
        {
            ZhuluSaveSchema.ClearForTest();
            // 快照里没有 strs 分区——注册了的接收方 Apply 不应被调用、内存不被清空
            var snap = new ZhuluSnapshotV5();
            snap.Sections["other"] = new Dictionary<string, object>();   // 无关段

            var dst = new StringSection();
            dst.Store["keep"] = "原值";
            ZhuluSaveSchema.Register(dst);
            ZhuluSaveSchema.RestoreAll(snap);

            TestRunner.AssertEqual("原值", dst.Store.ContainsKey("keep") ? dst.Store["keep"] : "", "缺段不覆盖");
        }

        private static void TestOldVersionRejected()
        {
            string v4json = "{\"Version\":4,\"WorldId\":\"old\"}";
            TestRunner.AssertTrue(ZhuluSnapshotV5.Deserialize(v4json) == null, "v4旧快照明确拒绝");
        }

        private static void TestDuplicateRegisterIdempotent()
        {
            ZhuluSaveSchema.ClearForTest();
            var sec = new DemoSection();
            ZhuluSaveSchema.Register(sec);
            ZhuluSaveSchema.Register(sec);   // 同名再注册→覆盖不叠加
            var snap = new ZhuluSnapshotV5();
            ZhuluSaveSchema.CollectAll(snap);
            ZhuluSaveSchema.RestoreAll(snap);
            TestRunner.AssertEqual(1, sec.ApplyCalls, "重名注册只生效一份");
        }
    }
}
