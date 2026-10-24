using ZhuluRebuild.Interop;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-24 配置读写契约守护（第8步前置）。
    /// 只测纯函数部分——文件 IO 依赖 ModPath，交给实机验证。
    /// </summary>
    public static class LlmConfigStoreTests
    {
        public static void Run()
        {
            // ── 协议名解析（容错写法）──
            TestRunner.AssertEqual(LlmProtocolKind.ChatCompletions,
                LlmConfigStore.ParseProtocol("chat"), "chat → ChatCompletions");
            TestRunner.AssertEqual(LlmProtocolKind.ChatCompletions,
                LlmConfigStore.ParseProtocol(""), "空串 → 默认 Chat");
            TestRunner.AssertEqual(LlmProtocolKind.ChatCompletions,
                LlmConfigStore.ParseProtocol(null), "null → 默认 Chat");
            TestRunner.AssertEqual(LlmProtocolKind.AnthropicMessages,
                LlmConfigStore.ParseProtocol("Anthropic"), "大小写不敏感");
            TestRunner.AssertEqual(LlmProtocolKind.AnthropicMessages,
                LlmConfigStore.ParseProtocol("messages"), "messages 别名");
            TestRunner.AssertEqual(LlmProtocolKind.Responses,
                LlmConfigStore.ParseProtocol("responses"), "responses 解析");

            // ── 协议名回写往返 ──
            foreach (var k in new[] { LlmProtocolKind.ChatCompletions,
                                      LlmProtocolKind.AnthropicMessages,
                                      LlmProtocolKind.Responses })
            {
                string text = LlmConfigStore.ProtocolText(k);
                TestRunner.AssertEqual(k, LlmConfigStore.ParseProtocol(text), "协议往返一致: " + text);
            }

            // ── 密钥脱敏 ──
            TestRunner.AssertEqual("（未填写）", LlmConfigStore.MaskKey(""), "空密钥提示");
            TestRunner.AssertEqual("（未填写）", LlmConfigStore.MaskKey(null), "null 密钥提示");
            TestRunner.AssertEqual("••••", LlmConfigStore.MaskKey("abc"), "短密钥全掩");
            TestRunner.AssertEqual("sk-a••••••••", LlmConfigStore.MaskKey("sk-abcdefg"), "长密钥只露前 4 位");
            TestRunner.AssertTrue(LlmConfigStore.MaskKey("sk-abcdefg").IndexOf("bcdefg") < 0,
                "脱敏后不泄露后段");

            // ── 显示名 ──
            TestRunner.AssertEqual("Chat Completions",
                LlmConfigStore.ProtocolDisplay(LlmProtocolKind.ChatCompletions), "显示名 chat");
            TestRunner.AssertEqual("Anthropic Messages",
                LlmConfigStore.ProtocolDisplay(LlmProtocolKind.AnthropicMessages), "显示名 anthropic");

            // ── 逐字段容错（LlmFix-4 L8：坏字段只回退该字段，不拖垮整份配置）──
            var cfg1 = LlmConfigStore.ParseJson(
                "{\"enabled\":true,\"timeout_seconds\":\"abc\",\"endpoint\":\"https://a.com\"}");
            TestRunner.AssertTrue(cfg1.Enabled, "坏字段不拖垮整份配置（enabled 仍生效）");
            TestRunner.AssertEqual(30, cfg1.TimeoutSeconds, "无法解析的字段回退默认值");
            TestRunner.AssertEqual("https://a.com", cfg1.Endpoint, "好字段正常读取");
            var cfg4 = LlmConfigStore.ParseJson("{\"enabled\":true,\"timeout_seconds\":\"45\"}");
            TestRunner.AssertEqual(45, cfg4.TimeoutSeconds, "数字字符串按数值接受");
            var cfg2 = LlmConfigStore.ParseJson("不是JSON");
            TestRunner.AssertTrue(!cfg2.Enabled, "整体损坏回默认（禁用）");
            var cfg3 = LlmConfigStore.ParseJson("{\"enabled\":true,\"timeout_seconds\":999}");
            TestRunner.AssertEqual(120, cfg3.TimeoutSeconds, "超上限钳到 120");
            TestRunner.AssertEqual(LlmProtocolKind.ChatCompletions, cfg3.Protocol, "协议缺省 chat");
        }
    }
}
