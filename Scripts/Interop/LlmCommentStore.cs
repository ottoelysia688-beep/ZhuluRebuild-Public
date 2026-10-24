using System;
using System.Collections.Generic;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Interop
{
    /// <summary>一位帝王的 AI 评价结果（含来源，便于窗口区分「史官评」徽章）。</summary>
    public sealed class LlmEmperorComment
    {
        public string Shi { get; set; } = "";
        public string Temple { get; set; } = "";
        public string Comment { get; set; } = "";
        /// <summary>野史另说（LlmV2-1）：民间传闻口吻，与实录对照；空 = 未写/不合格被丢。</summary>
        public string WildComment { get; set; } = "";
        public int Shift { get; set; }
        public string ShiftReason { get; set; } = "";
        /// <summary>llm = 模型生成；fallback = 本地模板；manual = 玩家手动重写。</summary>
        public string Source { get; set; } = "";
        public int GeneratedAtYear { get; set; }
    }

    /// <summary>
    /// AI 评价结果存储（2026-10-24 历代天命接入 第5步）。
    ///
    /// 为什么必须持久化：模型输出是非确定性的。若读档后重新请求，同一帝王会得到不同评语，
    /// 历史记录当场翻变——这比"没有评语"更糟。故结果一律落快照，已生成永不重算。
    ///
    /// 分区键约定沿用框架：全局单例用 "__global__" 作外层键，内部再按帝王键存。
    /// </summary>
    public sealed class LlmCommentStore : ISnapshotSection<LlmCommentStore.LlmCommentState>
    {
        public sealed class LlmCommentState
        {
            public Dictionary<string, LlmEmperorComment> Comments { get; set; }
                = new Dictionary<string, LlmEmperorComment>();
        }

        public static LlmCommentStore Instance { get; } = new LlmCommentStore();

        private const string GlobalKey = "__global__";
        private readonly Dictionary<string, LlmEmperorComment> _map =
            new Dictionary<string, LlmEmperorComment>(StringComparer.Ordinal);

        public string SectionName { get { return "llm_comments"; } }

        public int Count { get { return _map.Count; } }

        /// <summary>
        /// 帝王唯一键。含即位/卒年——同一王朝内重名的帝王也能区分。
        /// </summary>
        public static string BuildKey(string dynasty, int reignIndex, string name, int startYear, int endYear)
        {
            return (dynasty ?? "") + "|" + reignIndex + "|" + (name ?? "") + "|" + startYear + "|" + endYear;
        }

        public bool Has(string key)
        {
            return !string.IsNullOrEmpty(key) && _map.ContainsKey(key);
        }

        public LlmEmperorComment Get(string key)
        {
            LlmEmperorComment v;
            return (!string.IsNullOrEmpty(key) && _map.TryGetValue(key, out v)) ? v : null;
        }

        /// <summary>
        /// 写入（幂等由调用方控制：正常路径已存在则跳过，手动重写强制覆盖）。
        /// </summary>
        public void Put(string key, LlmEmperorComment comment)
        {
            if (string.IsNullOrEmpty(key) || comment == null) return;
            _map[key] = comment;
        }

        public void Remove(string key)
        {
            if (!string.IsNullOrEmpty(key)) _map.Remove(key);
        }

        /// <summary>世界切换/读档前清空。</summary>
        public void Clear()
        {
            _map.Clear();
        }

        // ── 快照 ──
        public Dictionary<string, LlmCommentState> CaptureStates()
        {
            var state = new LlmCommentState();
            foreach (var kv in _map) state.Comments[kv.Key] = kv.Value;
            var d = new Dictionary<string, LlmCommentState>();
            d[GlobalKey] = state;
            return d;
        }

        public void ApplyStates(Dictionary<string, LlmCommentState> data)
        {
            // 2026-10-24 LlmFix-1 H4：null（快照无本分区，如旧档）同样清空——
            // 语义是"读档后状态 = 该档状态"，否则上一个世界的评语残留并会串进新档
            _map.Clear();
            if (data == null) return;
            LlmCommentState state;
            if (data.TryGetValue(GlobalKey, out state) && state != null && state.Comments != null)
            {
                foreach (var kv in state.Comments)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                        _map[kv.Key] = kv.Value;
            }
        }
    }
}
