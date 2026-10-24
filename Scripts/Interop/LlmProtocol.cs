using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZhuluRebuild.Interop
{
    /// <summary>支持的三种接口协议（用户 2026-10-24 指定）。</summary>
    public enum LlmProtocolKind
    {
        ChatCompletions = 0,   // OpenAI / DeepSeek / 通义 / Kimi / 本地 vLLM
        AnthropicMessages = 1, // Claude
        Responses = 2          // OpenAI Responses API
    }

    /// <summary>失败分类（决定是否重试）。</summary>
    public enum LlmFailureKind
    {
        None = 0,
        RateLimited = 1,     // 429
        Timeout = 2,         // 超时 / 网络中断
        ServerError = 3,     // 5xx
        AuthFailed = 4,      // 401 / 403 —— 不可重试
        BadRequest = 5,      // 400 —— 配置问题，不可重试
        ParseFailed = 6      // 返回体解析失败 —— 不可重试
    }

    /// <summary>
    /// 协议适配：把「一句话请求」翻译成各家格式，再把各家返回体里的话取出来。
    /// 纯函数，不碰网络——因此可以完全单测。
    /// </summary>
    public interface ILlmProtocol
    {
        LlmProtocolKind Kind { get; }
        string BuildPath(string baseUrl);
        Dictionary<string, string> BuildHeaders(string apiKey);
        string BuildBody(string model, string system, string user, int maxTokens);
        /// <summary>取正文；取不到返回 null。</summary>
        string ExtractText(string responseJson);
        /// <summary>取错误描述；无错误返回 null。</summary>
        string ExtractError(string responseJson);
        /// <summary>
        /// 取结束原因（finish_reason / stop_reason / incomplete 细节）；无则返回 null。
        /// LlmFix-2：用于把「输出额度耗尽」与「结构不符」区分开——推理模型常把
        /// 额度烧在 reasoning 上导致正文为空，提示必须能自解释。
        /// </summary>
        string ExtractFinishReason(string responseJson);
    }

    public static class LlmProtocolFactory
    {
        public static ILlmProtocol Create(LlmProtocolKind kind)
        {
            switch (kind)
            {
                case LlmProtocolKind.AnthropicMessages: return new AnthropicMessagesProtocol();
                case LlmProtocolKind.Responses: return new ResponsesProtocol();
                default: return new ChatCompletionsProtocol();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 1) Chat Completions
    // ═══════════════════════════════════════════════════════════
    internal sealed class ChatCompletionsProtocol : ILlmProtocol
    {
        public LlmProtocolKind Kind { get { return LlmProtocolKind.ChatCompletions; } }

        public string BuildPath(string baseUrl)
        {
            string b = (baseUrl ?? "").TrimEnd('/');
            if (b.EndsWith("/chat/completions")) return b;
            // LlmFix-3：OpenAI/vLLM/LM Studio 等端点挂在 /v1 下；DeepSeek 官方 /v1 别名同样有效。
            // 带路径的网关（既无 /v1 也非完整端点）只能按惯例补 /v1——覆盖主流三种填法
            if (b.EndsWith("/v1") || b.IndexOf("/v1/", StringComparison.Ordinal) >= 0)
                return b + "/chat/completions";
            return b + "/v1/chat/completions";
        }

        public Dictionary<string, string> BuildHeaders(string apiKey)
        {
            var h = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            // LlmFix-4：空密钥不发 Authorization（本地 vLLM/Ollama 无鉴权端点）
            if (!string.IsNullOrEmpty(apiKey)) h["Authorization"] = "Bearer " + apiKey;
            return h;
        }

        public string BuildBody(string model, string system, string user, int maxTokens)
        {
            // LlmFix-2（用户决策）：不传 max_tokens——deepseek-flash 映射到推理模型时，
            // 540 额度被 reasoning 烧光导致 content 恒空；不传则用服务端默认额度。
            // 参数保留只为接口兼容（测试连接仍传小值，本实现忽略）。
            var body = new JObject
            {
                ["model"] = model,
                ["temperature"] = 1.0,   // LlmV2-11：结构多样性再放大（0.7→0.9→1.0）
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system ?? "" },
                    new JObject { ["role"] = "user", ["content"] = user ?? "" }
                }
            };
            return body.ToString(Formatting.None);
        }

        public string ExtractText(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            if (root == null) return null;
            var content = root.SelectToken("choices[0].message.content");
            if (content != null && content.Type == JTokenType.String) return (string)content;
            // 部分服务把 content 返回成数组 [{type:"text",text:"..."}]
            if (content is JArray)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in (JArray)content)
                {
                    var t = part["text"];
                    if (t != null) sb.Append((string)t);
                }
                if (sb.Length > 0) return sb.ToString();
            }
            return null;
        }

        public string ExtractError(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            return root == null ? null : (string)root.SelectToken("error.message");
        }

        public string ExtractFinishReason(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            return root == null ? null : (string)root.SelectToken("choices[0].finish_reason");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 2) Anthropic Messages
    // ═══════════════════════════════════════════════════════════
    internal sealed class AnthropicMessagesProtocol : ILlmProtocol
    {
        public LlmProtocolKind Kind { get { return LlmProtocolKind.AnthropicMessages; } }

        public string BuildPath(string baseUrl)
        {
            string b = (baseUrl ?? "").TrimEnd('/');
            if (b.EndsWith("/messages")) return b;
            if (b.EndsWith("/v1")) return b + "/messages";
            return b + "/v1/messages";
        }

        public Dictionary<string, string> BuildHeaders(string apiKey)
        {
            return new Dictionary<string, string>
            {
                { "x-api-key", apiKey ?? "" },
                { "anthropic-version", "2023-06-01" },
                { "Content-Type", "application/json" }
            };
        }

        public string BuildBody(string model, string system, string user, int maxTokens)
        {
            // Anthropic 把 system 放在顶层，不放进 messages。
            // max_tokens 是该协议必填项，不能像 chat 那样省略——固定给足（LlmFix-2）
            var body = new JObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["system"] = system ?? "",
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = user ?? "" }
                }
            };
            return body.ToString(Formatting.None);
        }

        public string ExtractText(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            if (root == null) return null;
            var arr = root["content"] as JArray;
            if (arr == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var part in arr)
            {
                if ((string)part["type"] != "text") continue;
                var t = part["text"];
                if (t != null) sb.Append((string)t);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public string ExtractError(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            return root == null ? null : (string)root.SelectToken("error.message");
        }

        public string ExtractFinishReason(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            return root == null ? null : (string)root.SelectToken("stop_reason");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 3) Responses
    // ═══════════════════════════════════════════════════════════
    internal sealed class ResponsesProtocol : ILlmProtocol
    {
        public LlmProtocolKind Kind { get { return LlmProtocolKind.Responses; } }

        public string BuildPath(string baseUrl)
        {
            string b = (baseUrl ?? "").TrimEnd('/');
            if (b.EndsWith("/responses")) return b;
            // LlmFix-3：同 chat 协议——OpenAI Responses 端点在 /v1 下
            if (b.EndsWith("/v1") || b.IndexOf("/v1/", StringComparison.Ordinal) >= 0)
                return b + "/responses";
            return b + "/v1/responses";
        }

        public Dictionary<string, string> BuildHeaders(string apiKey)
        {
            var h = new Dictionary<string, string> { { "Content-Type", "application/json" } };
            // LlmFix-4：空密钥不发 Authorization（本地 vLLM/Ollama 无鉴权端点）
            if (!string.IsNullOrEmpty(apiKey)) h["Authorization"] = "Bearer " + apiKey;
            return h;
        }

        public string BuildBody(string model, string system, string user, int maxTokens)
        {
            // LlmFix-2（用户决策）：不传 max_output_tokens，理由同 chat 协议
            var body = new JObject
            {
                ["model"] = model,
                ["input"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system ?? "" },
                    new JObject { ["role"] = "user", ["content"] = user ?? "" }
                }
            };
            return body.ToString(Formatting.None);
        }

        public string ExtractText(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            if (root == null) return null;

            var direct = root["output_text"];
            if (direct != null && direct.Type == JTokenType.String) return (string)direct;

            var arr = root["output"] as JArray;
            if (arr == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var item in arr)
            {
                var content = item["content"] as JArray;
                if (content == null) continue;
                foreach (var part in content)
                {
                    var t = part["text"];
                    if (t != null) sb.Append((string)t);
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public string ExtractError(string responseJson)
        {
            var root = LlmJson.TryParse(responseJson);
            return root == null ? null : (string)root.SelectToken("error.message");
        }

        public string ExtractFinishReason(string responseJson)
        {
            // Responses API：status=incomplete 时细节在 incomplete_details.reason
            var root = LlmJson.TryParse(responseJson);
            if (root == null) return null;
            string reason = (string)root.SelectToken("incomplete_details.reason");
            if (!string.IsNullOrEmpty(reason)) return reason;
            string status = (string)root["status"];
            return status == "completed" ? null : status;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // JSON 容错工具
    // ═══════════════════════════════════════════════════════════
    public static class LlmJson
    {
        /// <summary>宽松解析：失败返回 null，绝不抛。</summary>
        public static JObject TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var tok = JToken.Parse(text);
                return tok as JObject;
            }
            catch { return null; }
        }

        /// <summary>
        /// 从模型输出里抠出 JSON 对象。
        /// 模型常返回 ```json {...} ``` 或前后带解释文字——先剥代码块，再取首个配对花括号区间。
        /// </summary>
        public static string ExtractJsonObject(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();

            // 剥 ```json ... ``` / ``` ... ```
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int start = s.IndexOf('\n', fence);
                if (start >= 0)
                {
                    int end = s.IndexOf("```", start + 1, StringComparison.Ordinal);
                    if (end > start) s = s.Substring(start + 1, end - start - 1).Trim();
                }
            }

            int first = s.IndexOf('{');
            if (first < 0) return null;

            int depth = 0;
            bool inString = false, escape = false;
            for (int i = first; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(first, i - first + 1);
                }
            }
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 重试策略（纯函数）
    // ═══════════════════════════════════════════════════════════
    public static class LlmRetryPolicy
    {
        public const int MaxAttempts = 3;

        /// <summary>错误类型 → 是否重试。401/403 绝不重试（密钥无效/封号，重试加速封禁）。</summary>
        public static bool ShouldRetry(LlmFailureKind kind)
        {
            switch (kind)
            {
                case LlmFailureKind.RateLimited:
                case LlmFailureKind.Timeout:
                case LlmFailureKind.ServerError:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>第 n 次失败后的退避毫秒：1s → 3s → 8s，带 ±20% 抖动。</summary>
        public static int DelayMs(int failedAttempt, double jitter01)
        {
            int baseMs = failedAttempt <= 1 ? 1000 : (failedAttempt == 2 ? 3000 : 8000);
            double factor = 0.8 + 0.4 * Math.Max(0.0, Math.Min(1.0, jitter01));   // 0.8 ~ 1.2
            return (int)(baseMs * factor);
        }

        /// <summary>HTTP 状态码 → 失败类型。</summary>
        public static LlmFailureKind Classify(int statusCode)
        {
            if (statusCode >= 200 && statusCode < 300) return LlmFailureKind.None;
            if (statusCode == 429) return LlmFailureKind.RateLimited;
            if (statusCode == 401 || statusCode == 403) return LlmFailureKind.AuthFailed;
            if (statusCode >= 500) return LlmFailureKind.ServerError;
            return LlmFailureKind.BadRequest;
        }
    }
}
