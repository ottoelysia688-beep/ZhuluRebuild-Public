using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZhuluRebuild.Interop
{
    /// <summary>
    /// 大模型配置读写（2026-10-24 历代天命接入 第8步）。
    ///
    /// 存 `.runtime/llm.json`——该目录已被 .gitignore 覆盖，密钥不会进版本库。
    /// 读取失败/文件缺失一律返回默认配置（enabled=false），绝不影响游戏启动。
    /// </summary>
    public static class LlmConfigStore
    {
        public const string FileName = "llm.json";

        private static string _path;

        public static string Path_
        {
            get
            {
                if (_path != null) return _path;
                string root = null;
                try { root = ZhuluRebuild.Core.ZhuluContext.Instance.ModPath; } catch { }
                if (string.IsNullOrEmpty(root)) root = ".";
                _path = System.IO.Path.Combine(root, ".runtime", FileName);
                return _path;
            }
        }

        /// <summary>读取配置；文件缺失/整体损坏返回默认（禁用）配置。</summary>
        public static LlmConfig Load()
        {
            try
            {
                string p = Path_;
                if (!File.Exists(p)) return new LlmConfig();
                return ParseJson(File.ReadAllText(p, System.Text.Encoding.UTF8));
            }
            catch
            {
                return new LlmConfig();
            }
        }

        /// <summary>
        /// 纯解析（可单测）。LlmFix-4 L8：逐字段容错——手改出类型错误
        /// （如 "timeout_seconds":"12"）只回退该字段默认值，
        /// 不再让整份配置静默变成"未启用"（用户只会看到 AI 没开，无从排查）。
        /// </summary>
        public static LlmConfig ParseJson(string text)
        {
            var cfg = new LlmConfig();
            try
            {
                var o = JObject.Parse(text ?? "");
                cfg.Enabled = ReadBool(o, "enabled", cfg.Enabled);
                cfg.AutoEval = ReadBool(o, "auto_eval", cfg.AutoEval);
                cfg.Endpoint = ReadString(o, "endpoint", cfg.Endpoint);
                cfg.Model = ReadString(o, "model", cfg.Model);
                cfg.ApiKey = ReadString(o, "api_key", cfg.ApiKey);
                cfg.TimeoutSeconds = ReadInt(o, "timeout_seconds", cfg.TimeoutSeconds);
                cfg.MaxChars = ReadInt(o, "max_chars", cfg.MaxChars);
                cfg.MaxRequestsPerGame = ReadInt(o, "max_requests_per_game", cfg.MaxRequestsPerGame);
                cfg.Protocol = ParseProtocol(ReadString(o, "protocol", "chat"));
            }
            catch
            {
                // 整体不是 JSON → 全默认（禁用），不影响游戏启动
                return new LlmConfig();
            }

            // 边界保护：异常值不应把游戏拖垮
            if (cfg.TimeoutSeconds < 3) cfg.TimeoutSeconds = 3;
            if (cfg.TimeoutSeconds > 120) cfg.TimeoutSeconds = 120;
            if (cfg.MaxChars < 12) cfg.MaxChars = 12;
            if (cfg.MaxChars > 300) cfg.MaxChars = 300;
            if (cfg.MaxRequestsPerGame < 0) cfg.MaxRequestsPerGame = 0;
            return cfg;
        }

        private static bool ReadBool(JObject o, string key, bool def)
        {
            try { var v = o[key]; return v == null ? def : (bool)v; }
            catch { return def; }
        }

        private static int ReadInt(JObject o, string key, int def)
        {
            try
            {
                var v = o[key];
                if (v == null) return def;
                if (v.Type == JTokenType.Integer) return (int)v;
                int parsed;
                return int.TryParse(v.ToString(), out parsed) ? parsed : def;
            }
            catch { return def; }
        }

        private static string ReadString(JObject o, string key, string def)
        {
            try { var v = o[key]; return v == null ? def : (string)v; }
            catch { return def; }
        }

        public static bool Save(LlmConfig cfg)
        {
            try
            {
                if (cfg == null) return false;
                string p = Path_;
                string dir = System.IO.Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var o = new JObject
                {
                    ["enabled"] = cfg.Enabled,
                    ["auto_eval"] = cfg.AutoEval,
                    ["protocol"] = ProtocolText(cfg.Protocol),
                    ["endpoint"] = cfg.Endpoint ?? "",
                    ["model"] = cfg.Model ?? "",
                    ["api_key"] = cfg.ApiKey ?? "",
                    ["timeout_seconds"] = cfg.TimeoutSeconds,
                    ["max_chars"] = cfg.MaxChars,
                    ["max_requests_per_game"] = cfg.MaxRequestsPerGame
                };
                File.WriteAllText(p, o.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        public static LlmProtocolKind ParseProtocol(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "anthropic":
                case "messages":
                    return LlmProtocolKind.AnthropicMessages;
                case "responses":
                    return LlmProtocolKind.Responses;
                default:
                    return LlmProtocolKind.ChatCompletions;
            }
        }

        public static string ProtocolText(LlmProtocolKind k)
        {
            switch (k)
            {
                case LlmProtocolKind.AnthropicMessages: return "anthropic";
                case LlmProtocolKind.Responses: return "responses";
                default: return "chat";
            }
        }

        public static string ProtocolDisplay(LlmProtocolKind k)
        {
            switch (k)
            {
                case LlmProtocolKind.AnthropicMessages: return "Anthropic Messages";
                case LlmProtocolKind.Responses: return "Responses";
                default: return "Chat Completions";
            }
        }

        /// <summary>密钥脱敏（界面与日志都只显示前 4 位）。</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "（未填写）";
            if (key.Length <= 4) return "••••";
            return key.Substring(0, 4) + "••••••••";
        }

        /// <summary>把配置应用到服务单例（启动与保存后调用）。</summary>
        public static void Apply(LlmConfig cfg)
        {
            try { LlmEmperorService.Instance.Configure(cfg); } catch { }
        }

        /// <summary>重新读盘并应用。</summary>
        public static LlmConfig ReloadAndApply()
        {
            var cfg = Load();
            Apply(cfg);
            return cfg;
        }
    }
}
