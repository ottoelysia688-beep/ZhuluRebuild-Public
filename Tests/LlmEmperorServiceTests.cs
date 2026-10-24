using System;
using System.Collections.Generic;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 评价服务契约守护（历代天命接入 第6步）。
    /// 重点：幂等（不重复烧额度）、上限、部分字段合格时的合并兜底。
    /// </summary>
    public static class LlmEmperorServiceTests
    {
        private static LlmEmperorRequest Req(string key, bool manual = false)
        {
            return new LlmEmperorRequest
            {
                Key = key,
                SystemPrompt = "sys",
                UserPayload = "user",
                ShiCandidates = new List<string> { "宣", "光", "中", "兴" },
                TempleCandidates = new List<string> { "世宗", "高宗" },
                EndYear = 120,
                Manual = manual
            };
        }

        private static LlmConfig Cfg(bool enabled = true, int max = 100)
        {
            return new LlmConfig
            {
                Enabled = enabled,
                Endpoint = "https://a.com/v1",
                Model = "m",
                ApiKey = "sk",
                MaxRequestsPerGame = max,
                MaxChars = 120
            };
        }

        public static void Run()
        {
            var store = LlmCommentStore.Instance;
            var svc = new LlmEmperorService();
            svc.AutoStartWorker = false;   // 单测不联网，改用 ProcessInline

            // ── 1. 未启用 → 不入队 ──
            svc.Configure(Cfg(enabled: false));
            TestRunner.AssertTrue(!svc.Enqueue(Req("t1|1|甲|1|10")), "未启用不入队");

            // ── 2. 启用后可入队 ──
            svc.Configure(Cfg());
            string k1 = "t2|1|乙|1|10";
            TestRunner.AssertTrue(svc.Enqueue(Req(k1)), "启用后可入队");
            TestRunner.AssertEqual(1, svc.QueuedCount, "队列长度 1");

            // ── 3. 重复入队被拒 ──
            TestRunner.AssertTrue(!svc.Enqueue(Req(k1)), "同一帝王重复入队被拒");
            TestRunner.AssertEqual(1, svc.QueuedCount, "队列仍为 1");

            // ── 4. ProcessInline 应用结果 ──
            int applied = svc.ProcessInline(r => new LlmCallResult
            {
                Success = true,
                Text = "{\"shi\":\"宣\",\"temple\":\"世宗\",\"comment\":\"收权于朝，复三城之失，天命复振。\"}"
            });
            TestRunner.AssertEqual(1, applied, "应用 1 条");
            TestRunner.AssertTrue(store.Has(k1), "结果已写库");
            TestRunner.AssertEqual("宣", store.Get(k1).Shi, "谥字写入");
            TestRunner.AssertEqual("llm", store.Get(k1).Source, "来源为 llm");

            // ── 5. 已缓存 → 不再入队（省额度）──
            TestRunner.AssertTrue(!svc.Enqueue(Req(k1)), "已生成的不再请求");

            // ── 6. 手动重写 force=true 可入队 ──
            TestRunner.AssertTrue(svc.Enqueue(Req(k1, manual: true), force: true), "手动重写可强制入队");
            svc.ProcessInline(r => new LlmCallResult
            {
                Success = true,
                Text = "{\"shi\":\"光\",\"comment\":\"承积弱之余，收权于朝，天命复振，可谓中兴。\"}"
            });
            TestRunner.AssertEqual("光", store.Get(k1).Shi, "重写后谥字更新");
            TestRunner.AssertEqual("manual", store.Get(k1).Source, "来源标记 manual");

            // ── 7. 校验失败不写库 ──
            string k2 = "t2|2|丙|1|10";
            svc.Enqueue(Req(k2));
            svc.ProcessInline(r => new LlmCallResult
            {
                Success = true,
                Text = "{\"shi\":\"伟\",\"comment\":\"通过优化管理机制提升效率，国势大振。\"}"
            });
            TestRunner.AssertTrue(!store.Has(k2), "全部字段不合格时不写库");

            // ── 8. 部分合格：谥字越界但评语合格 → 评语保留 ──
            string k3 = "t2|3|丁|1|10";
            svc.Enqueue(Req(k3));
            svc.ProcessInline(r => new LlmCallResult
            {
                Success = true,
                Text = "{\"shi\":\"伟\",\"comment\":\"虽有拓地之功，然民力已竭，可为叹惜。\"}"
            });
            TestRunner.AssertTrue(store.Has(k3), "部分合格仍写库");
            TestRunner.AssertEqual("", store.Get(k3).Shi, "越界谥字被丢弃");
            TestRunner.AssertTrue(store.Get(k3).Comment.Length > 0, "合格评语保留");

            // ── 9. 请求失败不写库 ──
            string k4 = "t2|4|戊|1|10";
            svc.Enqueue(Req(k4));
            svc.ProcessInline(r => new LlmCallResult { Success = false, Failure = LlmFailureKind.Timeout });
            TestRunner.AssertTrue(!store.Has(k4), "请求失败不写库");

            // ── 10. 每局上限 ──
            var svc2 = new LlmEmperorService();
            svc2.AutoStartWorker = false;
            svc2.Configure(Cfg(max: 0));
            TestRunner.AssertTrue(!svc2.Enqueue(Req("t3|1|己|1|10")), "达上限后不入队");

            // ── 11. 重置清空队列与计数 ──
            svc.Enqueue(Req("t4|1|庚|1|10"));
            svc.ResetForNewWorld();
            TestRunner.AssertEqual(0, svc.QueuedCount, "重置后队列清空");
            TestRunner.AssertEqual(0, svc.RequestsUsed, "重置后计数归零");

            // ── 12. 熔断（LlmFix-1 M2）：真 worker + 假传输(401)——
            //     只烧 1 次额度、剩余队列倾泻不卡键、再入队被拒并给可见原因 ──
            var svc3 = new LlmEmperorService();
            svc3.AutoStartWorker = true;   // 真线程，但传输是假的，不联网
            svc3.Configure(Cfg(), new AuthFailTransport());
            svc3.Enqueue(Req("t5|1|a|1|10"));
            svc3.Enqueue(Req("t5|2|b|1|10"));
            svc3.Enqueue(Req("t5|3|c|1|10"));
            WaitIdle(svc3);
            svc3.Pump();   // 清 _completed 与在途标记
            TestRunner.AssertTrue(!svc3.IsAvailable, "401 后熔断生效");
            TestRunner.AssertEqual(1, svc3.RequestsUsed, "只计 1 次真请求（熔断中断不烧额度）");
            TestRunner.AssertTrue(!svc3.Enqueue(Req("t5|4|d|1|10")), "熔断中入队被拒");
            TestRunner.AssertTrue(svc3.LastRejectReason != null && svc3.LastRejectReason.Contains("熔断"),
                "拒绝原因可见（含「熔断」）");
            // 键不卡死：换新 client（熔断重置）后，被倾泻的帝王能重新入队
            svc3.Configure(Cfg(), new OkTransport());
            TestRunner.AssertTrue(svc3.Enqueue(Req("t5|2|b|1|10")), "熔断倾泻后的键不卡死（可重新入队）");
            WaitIdle(svc3);
            svc3.Pump();
            TestRunner.AssertTrue(store.Has("t5|2|b|1|10"), "恢复后评语写库");
            svc3.ResetForNewWorld();
            store.Remove("t5|2|b|1|10");

            // ── 13. 批量通知（LlmFix-1 L7）：一次 Pump 处理多条结果只通知一次 ──
            var svc4 = new LlmEmperorService();
            svc4.AutoStartWorker = true;
            int notified = 0;
            svc4.OnCommentReady += _ => notified++;
            svc4.Configure(Cfg(), new AuthFailTransport());
            svc4.Enqueue(Req("t6|1|a|1|10"));
            svc4.Enqueue(Req("t6|2|b|1|10"));
            WaitIdle(svc4);
            notified = 0;
            svc4.Pump();   // 两条失败结果一次处理完
            TestRunner.AssertEqual(1, notified, "一次 Pump 只通知一次");
            svc4.ResetForNewWorld();

            // ── 14. 状态四态（LlmFix-3）：Queued→Done 与 Failed ──
            var blocker = new BlockTransport();
            var svc5 = new LlmEmperorService();
            svc5.AutoStartWorker = true;
            svc5.Configure(Cfg(), blocker);
            string fk = "t7|1|a|1|10";
            svc5.Enqueue(Req(fk));
            TestRunner.AssertEqual(LlmAiStatus.Queued, svc5.AiStatusOf(fk), "请求在途为 ◐ 生成中");
            blocker.Release.Set();
            WaitIdle(svc5);
            svc5.Pump();
            TestRunner.AssertEqual(LlmAiStatus.Done, svc5.AiStatusOf(fk), "成功后为 ● 已生成");
            svc5.ResetForNewWorld();
            store.Remove(fk);
            TestRunner.AssertEqual(LlmAiStatus.None, svc5.AiStatusOf(fk), "重置后回到 ○ 未生成");

            var svc6 = new LlmEmperorService();
            svc6.AutoStartWorker = true;
            svc6.Configure(Cfg(), new AuthFailTransport());
            svc6.Enqueue(Req("t7|2|b|1|10"));
            WaitIdle(svc6);
            svc6.Pump();
            TestRunner.AssertEqual(LlmAiStatus.Failed, svc6.AiStatusOf("t7|2|b|1|10"), "失败后为 ×");
            svc6.ResetForNewWorld();

            // ── 15. 谥字/庙号回写纯函数（LlmFix-5 H2：AI 选择写回帝王记录）──
            var dyn = new ZhuluRebuild.Domain.DynastyRecord { Dynasty = "韩" };
            var emp1 = new ZhuluRebuild.Domain.EmperorRecord
            { Name = "甲", StartYear = 1, EndYear = 10, PosthumousName = "武", TempleName = "太宗" };
            var emp2 = new ZhuluRebuild.Domain.EmperorRecord
            { Name = "乙", StartYear = 11, EndYear = 20, PosthumousName = "文" };
            dyn.Emperors = new System.Collections.Generic.List<ZhuluRebuild.Domain.EmperorRecord> { emp1, emp2 };
            var hist = new System.Collections.Generic.List<ZhuluRebuild.Domain.DynastyRecord> { dyn };

            string key1 = LlmCommentStore.BuildKey("韩", 1, "甲", 1, 10);
            var choice = new LlmEmperorComment { Shi = "桓", Temple = "太祖" };
            TestRunner.AssertTrue(
                ZhuluRebuild.Domain.OrthodoxMandateManager.ApplyAiChoiceToList(hist, key1, choice),
                "回写返回已改动");
            TestRunner.AssertEqual("桓", emp1.PosthumousName, "谥字被 AI 选择替换");
            TestRunner.AssertEqual("太祖", emp1.TempleName, "庙号被 AI 选择替换");

            // 空 Shi/空 Temple = 模型未选（候选池空），保留本地
            var choice2 = new LlmEmperorComment { Shi = "", Temple = "" };
            TestRunner.AssertTrue(
                !ZhuluRebuild.Domain.OrthodoxMandateManager.ApplyAiChoiceToList(hist, key1, choice2),
                "空选择无改动");
            TestRunner.AssertEqual("桓", emp1.PosthumousName, "空谥字保留本地");

            // 键不匹配（帝名不同）→ 不动记录
            string badKey = LlmCommentStore.BuildKey("韩", 1, "丙", 1, 10);
            TestRunner.AssertTrue(
                !ZhuluRebuild.Domain.OrthodoxMandateManager.ApplyAiChoiceToList(hist, badKey, choice),
                "键不匹配不动记录");

            // 只补庙号：谥字保留本地
            string key2 = LlmCommentStore.BuildKey("韩", 2, "乙", 11, 20);
            ZhuluRebuild.Domain.OrthodoxMandateManager.ApplyAiChoiceToList(hist, key2,
                new LlmEmperorComment { Shi = "", Temple = "世宗" });
            TestRunner.AssertEqual("文", emp2.PosthumousName, "空谥字段保留本地谥");
            TestRunner.AssertEqual("世宗", emp2.TempleName, "庙号可单独回写");

            // ── 16. 两个事件的节拍（LlmFix-5）：回写逐条，UI 刷新批末一次 ──
            var blocker2 = new BlockTransport();
            var svc7 = new LlmEmperorService();
            svc7.AutoStartWorker = true;
            int appliedEvents = 0, readyEvents = 0;
            svc7.OnCommentApplied += _ => appliedEvents++;
            svc7.OnCommentReady += _ => readyEvents++;
            svc7.Configure(Cfg(), blocker2);
            svc7.Enqueue(Req("t8|1|a|1|10"));
            svc7.Enqueue(Req("t8|2|b|1|10"));
            blocker2.Release.Set();
            WaitIdle(svc7);
            svc7.Pump();
            TestRunner.AssertEqual(2, appliedEvents, "回写事件逐条触发（2 条）");
            TestRunner.AssertEqual(1, readyEvents, "UI 刷新事件批末一次");
            svc7.ResetForNewWorld();
            store.Remove("t8|1|a|1|10");
            store.Remove("t8|2|b|1|10");

            // ── 17. 多类型任务分发（LlmV2-3：断代史等非评语请求与评语共用队列）──
            var blocker3 = new BlockTransport();
            var svc8 = new LlmEmperorService();
            svc8.AutoStartWorker = true;
            string taskResult = null;
            bool taskFailed = false;
            svc8.Configure(Cfg(), blocker3);
            svc8.Enqueue(Req("t9|1|a|1|10"));
            svc8.Enqueue(new LlmTask
            {
                Kind = LlmRequestKind.Chronicle,
                Key = "chronicle|100|200",
                SystemPrompt = "sys",
                UserPayload = "user",
                OnResult = r =>
                {
                    if (r != null && r.Success && !string.IsNullOrEmpty(r.Text)) taskResult = r.Text;
                    else taskFailed = true;
                }
            });
            // 注：不断言 QueuedCount 精确值——worker 线程可能在断言前已取走首项（时序无关的结果断言在下方）
            TestRunner.AssertTrue(!svc8.Enqueue(new LlmTask { Key = "chronicle|100|200", SystemPrompt = "s", UserPayload = "u", OnResult = _ => { } }),
                "同键任务在途去重");
            blocker3.Release.Set();
            WaitIdle(svc8);
            svc8.Pump();
            TestRunner.AssertTrue(store.Has("t9|1|a|1|10"), "评语项照常写库（零回归）");
            TestRunner.AssertTrue(taskResult != null && taskResult.Contains("shi"), "任务项经回调拿到正文");
            TestRunner.AssertTrue(!taskFailed, "任务未失败");
            TestRunner.AssertEqual(2, svc8.RequestsUsed, "两类请求都计额度");
            svc8.ResetForNewWorld();
            store.Remove("t9|1|a|1|10");

            // 任务失败路径：回调收到 Success=false 自行兜底
            var svc9 = new LlmEmperorService();
            svc9.AutoStartWorker = true;
            bool sawFailure = false;
            svc9.Configure(Cfg(), new AuthFailTransport());
            svc9.Enqueue(new LlmTask
            {
                Kind = LlmRequestKind.Chronicle,
                Key = "chronicle|300|400",
                SystemPrompt = "s", UserPayload = "u",
                OnResult = r => { if (r == null || !r.Success) sawFailure = true; }
            });
            WaitIdle(svc9);
            svc9.Pump();
            TestRunner.AssertTrue(sawFailure, "失败任务回调收到失败结果");
            svc9.ResetForNewWorld();

            // 清理测试数据，避免污染单例
            store.Remove(k1); store.Remove(k2); store.Remove(k3); store.Remove(k4);
        }

        /// <summary>等待 worker 把队列处理完（不调用 Pump——留给调用方控制通知时机）。</summary>
        private static void WaitIdle(LlmEmperorService svc, int timeoutMs = 8000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (svc.QueuedCount == 0 && !svc.IsBusy)
                {
                    System.Threading.Thread.Sleep(30);   // 等最终状态完全落定
                    return;
                }
                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>永远 401 —— 用于触发熔断（不联网）。</summary>
        private sealed class AuthFailTransport : ILlmTransport
        {
            public LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
            {
                return new LlmHttpResponse { StatusCode = 401, Body = "{\"error\":{\"message\":\"bad key\"}}" };
            }
        }

        /// <summary>永远返回合法评价 JSON —— 用于熔断恢复后的成功路径（不联网）。</summary>
        private sealed class OkTransport : ILlmTransport
        {
            public LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
            {
                return new LlmHttpResponse
                {
                    StatusCode = 200,
                    Body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"shi\\\":\\\"宣\\\",\\\"comment\\\":\\\"收权于朝，复三城之失，天命复振。\\\"}\"}}]}"
                };
            }
        }

        /// <summary>阻塞到放行为止再返回成功 —— 用于稳定观察"生成中"状态（不联网）。</summary>
        private sealed class BlockTransport : ILlmTransport
        {
            public readonly System.Threading.ManualResetEvent Release =
                new System.Threading.ManualResetEvent(false);

            public LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
            {
                Release.WaitOne(8000);
                return new LlmHttpResponse
                {
                    StatusCode = 200,
                    Body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"shi\\\":\\\"宣\\\",\\\"comment\\\":\\\"收权于朝，复三城之失，天命复振。\\\"}\"}}]}"
                };
            }
        }
    }
}
