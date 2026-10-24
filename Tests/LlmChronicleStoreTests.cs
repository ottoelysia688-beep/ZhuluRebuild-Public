using System.Collections.Generic;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 断代史契约守护（LlmV2-4）。
    /// 核心：期段键稳定、AI 结果落快照不重算、失败本地模板兜底且同样落库。
    /// </summary>
    public static class LlmChronicleStoreTests
    {
        private static LlmPromptBuilder.DynastyDigestFact F(string name, int emperors, string end = "active")
        {
            return new LlmPromptBuilder.DynastyDigestFact
            { Name = name, StartYear = 161, EndYear = 510, Emperors = emperors, Wars = 7, Policies = 52, EndReason = end };
        }

        public static void Run()
        {
            var store = new LlmChronicleStore();

            // ── 1. 期段键 ──
            TestRunner.AssertEqual("101|200", LlmChronicleStore.BuildKey(101, 200), "键=起止年段");

            // ── 2. 触发节流（纯函数）──
            var due = OrthodoxMandateManager.ChronicleSpanDue(200, 100);
            TestRunner.AssertTrue(due != null && due.Item1 == 101 && due.Item2 == 200, "届满返回段 (101,200)");
            TestRunner.AssertTrue(OrthodoxMandateManager.ChronicleSpanDue(150, 100) == null, "未届满返回 null");
            TestRunner.AssertTrue(OrthodoxMandateManager.ChronicleSpanDue(100, 0) == null, "关闭(0)返回 null");
            TestRunner.AssertTrue(OrthodoxMandateManager.ChronicleSpanDue(0, 100) == null, "非法年份返回 null");

            // ── 3. 读写与排序 ──
            store.Put(LlmChronicleStore.BuildKey(101, 200), new LlmChronicleEntry
            { SpanStart = 101, SpanEnd = 200, Digest = "其一。", Source = "llm" });
            store.Put(LlmChronicleStore.BuildKey(1, 100), new LlmChronicleEntry
            { SpanStart = 1, SpanEnd = 100, Digest = "其二。", Source = "fallback" });
            TestRunner.AssertEqual(2, store.Count, "两条史论");
            TestRunner.AssertTrue(store.Has("1|100"), "键存在性");
            var all = store.All();
            TestRunner.AssertEqual(100, all[0].SpanEnd, "All 按段末升序（首条=1~100 段）");

            // ── 4. 快照往返（含 null 清空语义）──
            var captured = store.CaptureStates();
            var restored = new LlmChronicleStore();
            restored.ApplyStates(captured);
            TestRunner.AssertEqual(2, restored.Count, "往返条数一致");
            TestRunner.AssertEqual("其一。", restored.Get("101|200").Digest, "往返内容一致");
            restored.ApplyStates(null);
            TestRunner.AssertEqual(0, restored.Count, "null 清空（旧档无分区=无史论）");

            // ── 5. digest 解析 ──
            TestRunner.AssertEqual("商起于章氏，传十六主，三百五十年。",
                LlmChronicleStore.TryParseDigest("{\"digest\":\"商起于章氏，传十六主，三百五十年。\"}", 12, 200),
                "合法 digest 通过");
            TestRunner.AssertEqual("断代之论。",
                LlmChronicleStore.TryParseDigest("```json\n{\"digest\":\"断代之论。\"}\n```", 4, 200),
                "代码块包裹可解析");
            TestRunner.AssertTrue(LlmChronicleStore.TryParseDigest("{\"digest\":\"通过优化管理机制提升效率。\"}", 12, 200) == null,
                "现代词被拒");
            TestRunner.AssertTrue(LlmChronicleStore.TryParseDigest("{\"digest\":\"太短\"}", 12, 200) == null,
                "过短被拒");
            TestRunner.AssertTrue(LlmChronicleStore.TryParseDigest("不是JSON", 12, 200) == null,
                "非 JSON 返回 null");

            // ── 6. 事实载荷与模板兜底 ──
            var facts = new List<LlmPromptBuilder.DynastyDigestFact> { F("商", 16, "篡位失国"), F("费", 2) };
            string payload = LlmPromptBuilder.BuildChroniclePayload(101, 200, facts, 2);
            TestRunner.AssertTrue(payload.Contains("101~200年"), "载荷含年期");
            TestRunner.AssertTrue(payload.Contains("\"国号\":\"商\"") && payload.Contains("\"帝数\":16"), "载荷含朝代事实");
            TestRunner.AssertTrue(payload.Contains("篡位失国"), "载荷含结局");
            string fallback = OrthodoxMandateManager.BuildFallbackDigest(101, 200, facts);
            TestRunner.AssertTrue(fallback.Contains("商") && fallback.Contains("费"), "模板含朝代名");
            TestRunner.AssertTrue(fallback.Contains("十八帝"), "模板帝数求和（16+2）");
            TestRunner.AssertTrue(LlmPromptBuilder.ChronicleSystemPromptFor(200).Contains("60 至 200 字"),
                "断代史提示词字数联动");
        }
    }
}
