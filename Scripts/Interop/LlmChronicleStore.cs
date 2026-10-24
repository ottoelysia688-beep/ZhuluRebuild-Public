using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Interop
{
    /// <summary>一段年期的断代史论（LlmV2-4）。</summary>
    public sealed class LlmChronicleEntry
    {
        public int SpanStart { get; set; }
        public int SpanEnd { get; set; }
        public string Digest { get; set; } = "";
        /// <summary>llm = 史官评；fallback = 本地模板。</summary>
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// 断代史存储（快照分区 llm_chronicles，键=起止年段 "start|end"）。
    /// 与帝王评语同理：AI 输出非确定，必须落快照、已生成不重算；
    /// 失败也落本地模板——既给玩家兜底文本，又防同一期段反复请求烧额度。
    /// </summary>
    public sealed class LlmChronicleStore : ISnapshotSection<LlmChronicleStore.ChronicleState>
    {
        public sealed class ChronicleState
        {
            public Dictionary<string, LlmChronicleEntry> Digests { get; set; }
                = new Dictionary<string, LlmChronicleEntry>();
        }

        public static LlmChronicleStore Instance { get; } = new LlmChronicleStore();

        private const string GlobalKey = "__global__";
        private readonly Dictionary<string, LlmChronicleEntry> _map =
            new Dictionary<string, LlmChronicleEntry>(StringComparer.Ordinal);

        public string SectionName { get { return "llm_chronicles"; } }
        public int Count { get { return _map.Count; } }

        public static string BuildKey(int spanStart, int spanEnd)
        {
            return spanStart + "|" + spanEnd;
        }

        public bool Has(string key)
        {
            return !string.IsNullOrEmpty(key) && _map.ContainsKey(key);
        }

        public LlmChronicleEntry Get(string key)
        {
            LlmChronicleEntry v;
            return (!string.IsNullOrEmpty(key) && _map.TryGetValue(key, out v)) ? v : null;
        }

        public void Put(string key, LlmChronicleEntry entry)
        {
            if (string.IsNullOrEmpty(key) || entry == null) return;
            _map[key] = entry;
        }

        /// <summary>按段末年升序返回全部条目（窗口渲染用，最新在上由调用方反转）。</summary>
        public List<LlmChronicleEntry> All()
        {
            var list = new List<LlmChronicleEntry>(_map.Values);
            list.Sort((a, b) => (a != null ? a.SpanEnd : 0).CompareTo(b != null ? b.SpanEnd : 0));
            return list;
        }

        public void Clear()
        {
            _map.Clear();
        }

        public Dictionary<string, ChronicleState> CaptureStates()
        {
            var state = new ChronicleState();
            foreach (var kv in _map) state.Digests[kv.Key] = kv.Value;
            var d = new Dictionary<string, ChronicleState>();
            d[GlobalKey] = state;
            return d;
        }

        public void ApplyStates(Dictionary<string, ChronicleState> data)
        {
            // LlmFix-1 同语义：null（旧档无分区）也清空——读档后状态 = 该档状态
            _map.Clear();
            if (data == null) return;
            ChronicleState state;
            if (data.TryGetValue(GlobalKey, out state) && state != null && state.Digests != null)
            {
                foreach (var kv in state.Digests)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                        _map[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// 解析模型返回 {"digest":"…"}（LlmV2-4）：剥代码块/防现代词/限长（复用评语校验器）。
        /// 不合格返回 null，调用方走本地模板兜底。
        /// </summary>
        public static string TryParseDigest(string raw, int minChars, int maxChars)
        {
            string json = LlmJson.ExtractJsonObject(raw);
            if (json == null) return null;
            try
            {
                var o = JObject.Parse(json);
                string d = ((string)o["digest"] ?? "").Trim().Trim('"');
                return LlmResponseValidator.CheckComment(d, minChars, maxChars, null, null) == null ? d : null;
            }
            catch { return null; }
        }
    }
}
