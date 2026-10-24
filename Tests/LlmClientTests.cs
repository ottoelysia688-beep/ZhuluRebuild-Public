using System;
using System.Collections.Generic;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 大模型客户端契约守护（历代天命接入 第4步）。
    ///
    /// 全部用假传输，不联网。重点守住三件在线上最容易出事的行为：
    ///   ① 429 会退避重试，但**只重试 3 次**
    ///   ② 401/403 **绝不重试**且立即熔断（重试只会加速封号）
    ///   ③ 模型返回带 ```json 包裹或多余文字时能抠出 JSON
    /// </summary>
    public static class LlmClientTests
    {
        private sealed class FakeTransport : ILlmTransport
        {
            private readonly Queue<LlmHttpResponse> _queue = new Queue<LlmHttpResponse>();
            public int Calls;
            public string LastBody;
            public string LastUrl;
            public Dictionary<string, string> LastHeaders;

            public void Enqueue(int status, string body, bool timeout = false)
            {
                _queue.Enqueue(new LlmHttpResponse { StatusCode = status, Body = body, TimedOut = timeout });
            }

            public LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
            {
                Calls++;
                LastUrl = url;
                LastBody = body;
                LastHeaders = headers;
                if (_queue.Count == 0) return new LlmHttpResponse { StatusCode = 200, Body = "{}" };
                return _queue.Dequeue();
            }
        }

        private static LlmClient MakeClient(FakeTransport t, LlmProtocolKind kind, List<int> sleeps = null)
        {
            return new LlmClient(
                t,
                LlmProtocolFactory.Create(kind),
                sleepMs: ms => { if (sleeps != null) sleeps.Add(ms); },
                jitter: () => 0.5);
        }

        private const string ChatOk =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"shi\\\":\\\"景\\\"}\"}}]}";
        private const string AnthropicOk =
            "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"shi\\\":\\\"文\\\"}\"}]}";
        private const string ResponsesOk =
            "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"shi\\\":\\\"武\\\"}\"}]}]}";

        public static void Run()
        {
            // ── 1. 三协议：路径 ──
            var chat = LlmProtocolFactory.Create(LlmProtocolKind.ChatCompletions);
            var anth = LlmProtocolFactory.Create(LlmProtocolKind.AnthropicMessages);
            var resp = LlmProtocolFactory.Create(LlmProtocolKind.Responses);

            TestRunner.AssertEqual("https://a.com/v1/chat/completions",
                chat.BuildPath("https://a.com/v1"), "Chat 路径拼接");
            TestRunner.AssertEqual("https://a.com/v1/messages",
                anth.BuildPath("https://a.com"), "Anthropic 路径补 /v1/messages");
            TestRunner.AssertEqual("https://a.com/v1/responses",
                resp.BuildPath("https://a.com"), "Responses 路径（自动补 /v1）");

            // LlmFix-3：/v1 智能补全——根地址/尾斜杠/完整端点三种填法
            TestRunner.AssertEqual("https://api.openai.com/v1/chat/completions",
                chat.BuildPath("https://api.openai.com"), "根地址自动补 /v1/chat/completions");
            TestRunner.AssertEqual("https://api.deepseek.com/v1/chat/completions",
                chat.BuildPath("https://api.deepseek.com/"), "尾斜杠容忍");
            TestRunner.AssertEqual("https://a.com/v1/chat/completions",
                chat.BuildPath("https://a.com/v1/chat/completions"), "完整端点原样保留");

            // ── 2. 三协议：鉴权头 ──
            TestRunner.AssertTrue(chat.BuildHeaders("sk-1").ContainsKey("Authorization"), "Chat 用 Bearer");
            TestRunner.AssertTrue(anth.BuildHeaders("sk-1").ContainsKey("x-api-key"), "Anthropic 用 x-api-key");
            TestRunner.AssertTrue(anth.BuildHeaders("sk-1").ContainsKey("anthropic-version"), "Anthropic 带版本头");
            TestRunner.AssertTrue(resp.BuildHeaders("sk-1").ContainsKey("Authorization"), "Responses 用 Bearer");

            // ── 3. 三协议：请求体差异 ──
            string chatBody = chat.BuildBody("m", "系统", "用户", 100);
            TestRunner.AssertTrue(chatBody.Contains("\"messages\""), "Chat 体含 messages");
            TestRunner.AssertTrue(!chatBody.Contains("max_tokens"), "Chat 不再传 max_tokens（LlmFix-2 推理模型额度根因）");

            string anthBody = anth.BuildBody("m", "系统", "用户", 100);
            TestRunner.AssertTrue(anthBody.Contains("\"system\":\"系统\""), "Anthropic 的 system 在顶层");
            TestRunner.AssertTrue(anthBody.Contains("\"max_tokens\":4096"), "Anthropic 必填 max_tokens 固定 4096");

            string respBody = resp.BuildBody("m", "系统", "用户", 100);
            TestRunner.AssertTrue(respBody.Contains("\"input\""), "Responses 体含 input");
            TestRunner.AssertTrue(!respBody.Contains("max_output_tokens"), "Responses 不再传 max_output_tokens");

            // ── 4. 三协议：取文本 ──
            TestRunner.AssertTrue(chat.ExtractText(ChatOk).Contains("景"), "Chat 取文本");
            TestRunner.AssertTrue(anth.ExtractText(AnthropicOk).Contains("文"), "Anthropic 取文本");
            TestRunner.AssertTrue(resp.ExtractText(ResponsesOk).Contains("武"), "Responses 取文本（output[].content[]）");
            TestRunner.AssertTrue(resp.ExtractText("{\"output_text\":\"直接文本\"}") == "直接文本",
                "Responses 支持 output_text 简写");
            TestRunner.AssertTrue(chat.ExtractText("垃圾") == null, "非法返回体取文本返回 null");

            // ── 5. JSON 容错 ──
            TestRunner.AssertEqual("{\"a\":1}",
                LlmJson.ExtractJsonObject("```json\n{\"a\":1}\n```"), "剥离 ```json 代码块");
            TestRunner.AssertEqual("{\"a\":1}",
                LlmJson.ExtractJsonObject("好的，结果如下：{\"a\":1} 以上。"), "前后带解释文字仍能抠出");
            TestRunner.AssertEqual("{\"a\":{\"b\":2}}",
                LlmJson.ExtractJsonObject("{\"a\":{\"b\":2}}"), "嵌套花括号配对正确");
            TestRunner.AssertTrue(LlmJson.ExtractJsonObject("没有花括号") == null, "无 JSON 返回 null");
            TestRunner.AssertEqual("{\"t\":\"含}号\"}",
                LlmJson.ExtractJsonObject("{\"t\":\"含}号\"}"), "字符串内的 } 不误判");

            // ── 6. 重试策略 ──
            TestRunner.AssertTrue(LlmRetryPolicy.ShouldRetry(LlmFailureKind.RateLimited), "429 可重试");
            TestRunner.AssertTrue(LlmRetryPolicy.ShouldRetry(LlmFailureKind.Timeout), "超时可重试");
            TestRunner.AssertTrue(LlmRetryPolicy.ShouldRetry(LlmFailureKind.ServerError), "5xx 可重试");
            TestRunner.AssertTrue(!LlmRetryPolicy.ShouldRetry(LlmFailureKind.AuthFailed), "401 不重试");
            TestRunner.AssertTrue(!LlmRetryPolicy.ShouldRetry(LlmFailureKind.BadRequest), "400 不重试");
            TestRunner.AssertTrue(LlmRetryPolicy.DelayMs(1, 0.5) < LlmRetryPolicy.DelayMs(2, 0.5),
                "退避递增");
            TestRunner.AssertEqual(LlmFailureKind.RateLimited, LlmRetryPolicy.Classify(429), "429 分类");
            TestRunner.AssertEqual(LlmFailureKind.AuthFailed, LlmRetryPolicy.Classify(403), "403 分类");

            // ── 7. 客户端：429 两次后成功（共 3 次尝试）──
            var t1 = new FakeTransport();
            t1.Enqueue(429, "{\"error\":{\"message\":\"rate limit\"}}");
            t1.Enqueue(429, "{\"error\":{\"message\":\"rate limit\"}}");
            t1.Enqueue(200, ChatOk);
            var sleeps = new List<int>();
            var r1 = MakeClient(t1, LlmProtocolKind.ChatCompletions, sleeps)
                .Call("https://a.com/v1", "sk", "m", "sys", "user", 100, 12);
            TestRunner.AssertTrue(r1.Success, "429 重试后成功");
            TestRunner.AssertEqual(3, r1.Attempts, "共尝试 3 次");
            TestRunner.AssertEqual(2, sleeps.Count, "退避 2 次");

            // ── 8. 客户端：401 不重试且熔断 ──
            var t2 = new FakeTransport();
            t2.Enqueue(401, "{\"error\":{\"message\":\"invalid api key\"}}");
            var c2 = MakeClient(t2, LlmProtocolKind.ChatCompletions);
            var r2 = c2.Call("https://a.com/v1", "bad", "m", "s", "u", 100, 12);
            TestRunner.AssertTrue(!r2.Success, "401 失败");
            TestRunner.AssertEqual(1, r2.Attempts, "401 只尝试 1 次（不重试）");
            TestRunner.AssertTrue(c2.Breaker.IsOpen, "401 触发熔断");

            // 熔断后再调用：不发请求
            int before = t2.Calls;
            var r3 = c2.Call("https://a.com/v1", "bad", "m", "s", "u", 100, 12);
            TestRunner.AssertTrue(!r3.Success, "熔断后仍失败");
            TestRunner.AssertEqual(before, t2.Calls, "熔断后不再发请求");

            // ── 9. 客户端：返回体无可取文本 → ParseFailed，不重试 ──
            var t3 = new FakeTransport();
            t3.Enqueue(200, "{\"choices\":[]}");
            var r4 = MakeClient(t3, LlmProtocolKind.ChatCompletions)
                .Call("https://a.com/v1", "sk", "m", "s", "u", 100, 12);
            TestRunner.AssertEqual(LlmFailureKind.ParseFailed, r4.Failure, "无可取文本 → ParseFailed");
            TestRunner.AssertEqual(1, r4.Attempts, "解析失败不重试");

            // ── 9b. 空正文 + finish_reason=length → 专属自解释错误（LlmFix-2）──
            var t3b = new FakeTransport();
            t3b.Enqueue(200,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":\"length\"}]}");
            var r4b = MakeClient(t3b, LlmProtocolKind.ChatCompletions)
                .Call("https://a.com/v1", "sk", "m", "s", "u", 100, 12);
            TestRunner.AssertEqual(LlmFailureKind.ParseFailed, r4b.Failure, "额度耗尽也归 ParseFailed");
            TestRunner.AssertTrue(r4b.Error != null && r4b.Error.Contains("额度"),
                "错误自解释（含「额度」而非只有「无可取文本」）");

            // ── 10. 未配置时不发请求 ──
            var t4 = new FakeTransport();
            var r5 = MakeClient(t4, LlmProtocolKind.ChatCompletions).Call("", "", "m", "s", "u", 100, 12);
            TestRunner.AssertTrue(!r5.Success, "未配置直接失败");
            TestRunner.AssertEqual(0, t4.Calls, "未配置不发请求");

            // ── 10b. 空密钥允许（本地端点）——请求头不含 Authorization（LlmFix-4）──
            var t4b = new FakeTransport();
            t4b.Enqueue(200, ChatOk);
            var r5b = MakeClient(t4b, LlmProtocolKind.ChatCompletions)
                .Call("https://localhost:8000/v1", "", "m", "s", "u", 100, 12);
            TestRunner.AssertTrue(r5b.Success, "空密钥本地端点可调用");
            TestRunner.AssertTrue(t4b.LastHeaders == null || !t4b.LastHeaders.ContainsKey("Authorization"),
                "空密钥不发 Authorization 头");

            // ── 11. 超时也走重试 ──
            var t5 = new FakeTransport();
            t5.Enqueue(0, null, timeout: true);
            t5.Enqueue(200, ChatOk);
            var r6 = MakeClient(t5, LlmProtocolKind.ChatCompletions)
                .Call("https://a.com/v1", "sk", "m", "s", "u", 100, 12);
            TestRunner.AssertTrue(r6.Success, "超时后重试成功");
            TestRunner.AssertEqual(2, r6.Attempts, "超时重试 1 次后成功");

            // ── 12. 六道校验 ──
            var shiPool = new List<string> { "宣", "光", "中", "兴" };
            var templePool = new List<string> { "世宗", "高宗" };

            // 全合法
            var ok = LlmResponseValidator.Parse(
                "{\"shi\":\"宣\",\"temple\":\"世宗\",\"comment\":\"收权于朝，复三城之失，天命复振。\",\"shift\":0}",
                shiPool, templePool);
            TestRunner.AssertEqual("宣", ok.Shi, "合法谥字通过");
            TestRunner.AssertEqual("世宗", ok.Temple, "合法庙号通过");
            TestRunner.AssertTrue(ok.Comment != null, "合法评语通过");

            // 谥字越界 → 该字段丢弃，评语仍可用
            var badShi = LlmResponseValidator.Parse(
                "{\"shi\":\"伟\",\"comment\":\"收权于朝，复三城之失，天命复振。\"}",
                shiPool, templePool);
            TestRunner.AssertTrue(badShi.Shi == null, "越界谥字被丢弃");
            TestRunner.AssertTrue(badShi.Comment != null, "谥字越界不影响评语");

            // 评语含现代词 → 丢弃评语
            var modern = LlmResponseValidator.Parse(
                "{\"shi\":\"宣\",\"comment\":\"通过优化管理机制提升效率，国势大振。\"}",
                shiPool, templePool);
            TestRunner.AssertTrue(modern.Comment == null, "含现代词的评语被丢弃");
            TestRunner.AssertEqual("宣", modern.Shi, "评语不合格不影响谥字");

            // 评语过短
            TestRunner.AssertTrue(LlmResponseValidator.CheckComment("太短", 12, 120, null, null) != null,
                "过短评语被拒");

            // 复述庙号
            TestRunner.AssertTrue(
                LlmResponseValidator.CheckComment("世宗之世，国势极盛，四夷来朝，可谓中兴之主。", 12, 120, "宣", "世宗") != null,
                "复述庙号的评语被拒");

            // 非 JSON 返回
            var garbage = LlmResponseValidator.Parse("模型今天不想干活", shiPool, templePool);
            TestRunner.AssertTrue(!garbage.AnyUsable, "非 JSON 返回全部不可用");
            TestRunner.AssertTrue(garbage.FailReason != null, "非 JSON 给出失败原因");

            // 带 ```json 包裹仍能解析
            var fenced = LlmResponseValidator.Parse(
                "```json\n{\"shi\":\"光\",\"comment\":\"承积弱之余，收权于朝，天命复振。\"}\n```",
                shiPool, templePool);
            TestRunner.AssertEqual("光", fenced.Shi, "代码块包裹仍可解析");

            // 候选集为空 → 谥字不放行（LlmFix-4 M5：原实现空集等于"任意字合法"）
            var emptyPool = LlmResponseValidator.Parse(
                "{\"shi\":\"宣\",\"comment\":\"收权于朝，复三城之失，天命复振。\"}",
                new List<string>(), templePool);
            TestRunner.AssertTrue(emptyPool.Shi == null, "空候选集不放行谥字");
            TestRunner.AssertTrue(emptyPool.Comment != null, "谥字回退不影响评语");
            TestRunner.AssertTrue(emptyPool.FailReason != null && emptyPool.FailReason.Contains("候选集为空"),
                "空集给出明确失败原因");

            // ── 12b. 提示词（LlmFix-4/6）：战争次数接真实数据（-1 省略）；字数上限随配置 ──
            var f = new ZhuluRebuild.Domain.EmperorFacts
            { CityDelta = 3, PolicyCount = 4, ReignYears = 20, WarCount = -1 };
            string payload = LlmPromptBuilder.BuildUserPayload(
                f, "韩", "姜昭", "建元", 1, "", "", null, null, null, null);
            TestRunner.AssertTrue(!payload.Contains("战争次数"), "未采集(-1)时省略战争字段（不臆造）");
            TestRunner.AssertTrue(payload.Contains("\"施政次数\":4"), "施政次数字段保留");
            var f2 = new ZhuluRebuild.Domain.EmperorFacts { WarCount = 7, PolicyCount = 2 };
            string payload2 = LlmPromptBuilder.BuildUserPayload(
                f2, "韩", "姜昭", "建元", 1, "", "", null, null, null, null);
            TestRunner.AssertTrue(payload2.Contains("\"战争次数\":7"), "真实战争数进入考卷");
            TestRunner.AssertTrue(LlmPromptBuilder.SystemPromptFor(120).Contains("8 至 120 字"),
                "系统提示词字数上限联动 MaxChars");

            // ── 12c. 长评提示词去八股 + 野史双评语（LlmV2-1）──
            string sysp = LlmPromptBuilder.SystemPromptFor(200);
            TestRunner.AssertTrue(sysp.Contains("野史") && sysp.Contains("\"wild\""), "提示词含野史双评语指令");
            TestRunner.AssertTrue(!sysp.Contains("简劲克制") && !sysp.Contains("一至两句"),
                "旧八股文风指令已删");
            TestRunner.AssertTrue(sysp.Contains("勿仿其事"), "示例标注勿仿");

            var both = LlmResponseValidator.Parse(
                "{\"shi\":\"宣\",\"comment\":\"收权于朝，复三城之失，天命复振。\",\"wild\":\"里巷相传，其人少时家贫，及贵犹忆糟糠。\"}",
                shiPool, templePool);
            TestRunner.AssertTrue(both.Comment != null, "实录通过");
            TestRunner.AssertTrue(both.WildComment != null && both.WildComment.Contains("里巷"), "野史通过");
            var badWild = LlmResponseValidator.Parse(
                "{\"shi\":\"宣\",\"comment\":\"收权于朝，复三城之失，天命复振。\",\"wild\":\"通过优化管理机制提升效率。\"}",
                shiPool, templePool);
            TestRunner.AssertTrue(badWild.Comment != null, "野史不合格不影响实录");
            TestRunner.AssertTrue(badWild.WildComment == null, "含现代词的野史被丢弃");

            // 长度下限 12→8（B 案）
            TestRunner.AssertTrue(LlmResponseValidator.CheckComment("八字评语恰好八百", 8, 200, null, null) == null,
                "8 字下限通过");
            TestRunner.AssertTrue(LlmResponseValidator.CheckComment("七字评语恰好七", 8, 200, null, null) != null,
                "低于下限被拒");

            // ── 12d. 笔法池与反八股（LlmV2-11）──
            TestRunner.AssertTrue(LlmPromptBuilder.SystemPromptFor(200).Contains("严禁每篇都"),
                "反八股明令进提示词");
            TestRunner.AssertTrue(LlmPromptBuilder.SystemPromptFor(200).Contains("白话夹古"),
                "三体示例含白话夹古（现代体许可）");
            string kA = "商|16|白风|425|510";
            TestRunner.AssertEqual(LlmPromptBuilder.PickStyleOf(kA), LlmPromptBuilder.PickStyleOf(kA),
                "同帝笔法恒定（读档不变）");
            var seen = new HashSet<string>();
            for (int i = 0; i < 40; i++) seen.Add(LlmPromptBuilder.PickStyleOf("朝|" + i + "|帝|1|" + i));
            TestRunner.AssertTrue(seen.Count >= 4, "40 帝至少覆盖 4 种笔法（实测 " + seen.Count + "）");
            var f3 = new ZhuluRebuild.Domain.EmperorFacts { PolicyCount = 3 };
            string withStyle = LlmPromptBuilder.BuildUserPayload(f3, "韩", "甲", "元", 1, "", "", null, null, null, null, "白话夹古——示例");
            TestRunner.AssertTrue(withStyle.Contains("\"文体\":\"白话夹古"), "文体字段进入载荷");
            string noStyle = LlmPromptBuilder.BuildUserPayload(f3, "韩", "甲", "元", 1, "", "", null, null, null, null, null);
            TestRunner.AssertTrue(!noStyle.Contains("文体"), "不传文体则载荷无该字段");

            // ── 12e. 笔法门控（LlmV2-12：颂体仅美谥、贬体仅恶/悯）──
            bool sawPraise = false, sawBlameForEvil = false, praiseLeak = false, blameLeak = false;
            for (int i = 0; i < 60; i++)
            {
                string kf = LlmPromptBuilder.PickStyleOf("朝|" + i + "|帝|1|" + i, ZhuluRebuild.Domain.ShiCategory.Fine);
                string ke = LlmPromptBuilder.PickStyleOf("朝|" + i + "|帝|1|" + i, ZhuluRebuild.Domain.ShiCategory.Evil);
                if (kf.Contains("颂体")) sawPraise = true;
                if (ke.Contains("贬体")) sawBlameForEvil = true;
                if (ke.Contains("颂体")) praiseLeak = true;
                if (kf.Contains("贬体")) blameLeak = true;
            }
            TestRunner.AssertTrue(sawPraise, "美谥类能取到颂体（纯褒）");
            TestRunner.AssertTrue(sawBlameForEvil, "恶谥类能取到贬体（只贬）");
            TestRunner.AssertTrue(!praiseLeak && !blameLeak, "门控不串（颂/贬不越类）");
            TestRunner.AssertTrue(!LlmPromptBuilder.PickStyleOf("朝|7|帝|1|7", ZhuluRebuild.Domain.ShiCategory.Plain).Contains("颂体")
                && !LlmPromptBuilder.PickStyleOf("朝|7|帝|1|7", ZhuluRebuild.Domain.ShiCategory.Plain).Contains("贬体"),
                "平谥类不取颂/贬极端体");
            TestRunner.AssertTrue(LlmPromptBuilder.SystemPromptFor(200).Contains("可纯褒"), "反八股清单含纯褒");
        }
    }
}
