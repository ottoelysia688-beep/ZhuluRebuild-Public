using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 AI 评价存储契约守护（历代天命接入 第5步）。
    /// 核心：模型输出非确定，必须落快照；读档后同一帝王拿到的是同一条评语。
    /// </summary>
    public static class LlmCommentStoreTests
    {
        private static LlmEmperorComment C(string shi, string comment, string source = "llm")
        {
            return new LlmEmperorComment
            {
                Shi = shi,
                Temple = "世宗",
                Comment = comment,
                WildComment = "里巷相传，其人少时家贫，及贵犹忆糟糠。",   // LlmV2-1：野史字段随快照往返
                Shift = 0,
                Source = source,
                GeneratedAtYear = 120
            };
        }

        public static void Run()
        {
            var store = new LlmCommentStore();
            string k1 = LlmCommentStore.BuildKey("大周", 3, "李承乾", 100, 129);
            string k2 = LlmCommentStore.BuildKey("大周", 4, "李承乾", 130, 150);

            // ── 1. 键包含即位/卒年，同名不同帝可区分 ──
            TestRunner.AssertTrue(k1 != k2, "同名不同在位期 → 键不同");

            // ── 2. 基本读写 ──
            TestRunner.AssertTrue(!store.Has(k1), "初始不存在");
            store.Put(k1, C("景", "十九年而国势极盛。"));
            TestRunner.AssertTrue(store.Has(k1), "写入后存在");
            TestRunner.AssertEqual("景", store.Get(k1).Shi, "读回谥字");
            TestRunner.AssertEqual(1, store.Count, "计数为 1");

            // ── 3. 覆盖（手动重写场景）──
            store.Put(k1, C("宣", "重写后的评语。", "manual"));
            TestRunner.AssertEqual("宣", store.Get(k1).Shi, "覆盖生效");
            TestRunner.AssertEqual("manual", store.Get(k1).Source, "来源被更新");
            TestRunner.AssertEqual(1, store.Count, "覆盖不增加计数");

            // ── 4. 空键与空值不写入 ──
            store.Put(null, C("文", "x"));
            store.Put("", C("文", "x"));
            store.Put("k", null);
            TestRunner.AssertEqual(1, store.Count, "空键/空值被忽略");

            // ── 5. 快照往返 ──
            store.Put(k2, C("文", "承平之世，百姓安之。"));
            var captured = store.CaptureStates();
            TestRunner.AssertTrue(captured.ContainsKey("__global__"), "快照用 __global__ 作外层键");

            var restored = new LlmCommentStore();
            restored.ApplyStates(captured);
            TestRunner.AssertEqual(2, restored.Count, "往返后条目数一致");
            TestRunner.AssertEqual("宣", restored.Get(k1).Shi, "往返后内容一致（含覆盖结果）");
            TestRunner.AssertEqual("文", restored.Get(k2).Shi, "往返后第二条件一致");
            TestRunner.AssertTrue(restored.Get(k1).WildComment != null && restored.Get(k1).WildComment.Contains("里巷"),
                "野史字段随快照往返（LlmV2-1）");

            // ── 6. 缺分区 = 清空（LlmFix-1 H4：读档后状态 = 该档状态，旧档无分区即无评语）──
            var untouched = new LlmCommentStore();
            untouched.Put("x", C("武", "原有数据。"));
            untouched.ApplyStates(null);
            TestRunner.AssertEqual(0, untouched.Count, "null 状态清空（旧档无分区=无评语）");

            // ── 6b. 盖棺键与窗口键同基准（LlmFix-1 H1：窗口 idx 从 1 数，自动路径曾用 0 基）──
            var emp = new Domain.EmperorRecord { Name = "李承乾", StartYear = 100, EndYear = 129 };
            TestRunner.AssertEqual(
                LlmCommentStore.BuildKey("大周", 1, "李承乾", 100, 129),
                Domain.PosthumousNameGenerator.BuildAutoAiKey("大周", 0, emp),
                "盖棺键与窗口键同基准（1 起）");

            // ── 7. 清空（世界切换）──
            store.Clear();
            TestRunner.AssertEqual(0, store.Count, "Clear 后为空");

            // ── 8. 单例存在且分区名正确 ──
            TestRunner.AssertTrue(LlmCommentStore.Instance != null, "单例可用");
            TestRunner.AssertEqual("llm_comments", LlmCommentStore.Instance.SectionName, "分区名正确");
        }
    }
}
