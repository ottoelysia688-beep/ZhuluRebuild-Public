using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ZhuluRebuild.Interop
{
    /// <summary>模型返回的解析结果（逐字段校验，不合格的字段置 null 由调用方兜底）。</summary>
    public sealed class LlmEmperorParse
    {
        public string Shi;          // 校验通过的谥字；null = 不合格
        public string Temple;       // 校验通过的庙号；null = 不合格/空
        public string Comment;      // 校验通过的实录评语；null = 不合格
        public string WildComment;  // 野史另说（LlmV2-1）；null = 不合格/未写
        public int Shift;           // -1 / 0 / +1
        public string ShiftReason;
        public string FailReason;   // 整体失败原因（评语不可用时）
        /// <summary>是否有任一字段可用（空串不算——庙号空串表示"不祔庙"，不是有效结果）。</summary>
        public bool AnyUsable
        {
            get
            {
                return !string.IsNullOrEmpty(Shi)
                    || !string.IsNullOrEmpty(Temple)
                    || !string.IsNullOrEmpty(Comment);
            }
        }
    }

    /// <summary>
    /// 六道校验（2026-10-24 历代天命接入 第4步）。
    ///
    /// 模型能自由写评语，但**不能造谥字**——所以逐字段校验、逐字段兜底：
    ///   · 谥字越界 → 该字段丢弃，用本地哈希结果
    ///   · 庙号越界 → 同上
    ///   · 评语不合规 → 丢弃评语，用模板
    /// 一个字段不合格不影响其他字段，避免"一处不合格全盘作废"。
    /// </summary>
    public static class LlmResponseValidator
    {
        /// <summary>现代词汇黑名单（出现即判评语不合格）。</summary>
        private static readonly string[] ModernWords =
        {
            "经济", "政策", "数据", "系统", "管理", "效率", "优化", "指标", "评估",
            "资源", "机制", "功能", "模块", "流程", "方案", "项目", "团队"
        };

        public static LlmEmperorParse Parse(string raw, IList<string> shiCandidates,
                                            IList<string> templeCandidates,
                                            int minChars = 8, int maxChars = 200)
        {
            var r = new LlmEmperorParse();

            string json = LlmJson.ExtractJsonObject(raw);
            if (json == null) { r.FailReason = "返回体不含 JSON"; return r; }

            JObject obj;
            try { obj = JObject.Parse(json); }
            catch { r.FailReason = "JSON 解析失败"; return r; }

            // ── 谥字：必须在候选集内 ──
            // LlmFix-4 M5：候选集为空（上游构造异常）时不再放行任意字——
            // 那等于让模型生造谥字（"伟帝"一眼假），一律回退本地哈希结果
            string shi = ((string)obj["shi"] ?? "").Trim();
            if (shi.Length > 0)
            {
                if (shiCandidates != null && shiCandidates.Count > 0 && shiCandidates.Contains(shi))
                {
                    r.Shi = shi;
                }
                else if (r.FailReason == null)
                {
                    r.FailReason = (shiCandidates == null || shiCandidates.Count == 0)
                        ? "候选集为空，谥字回退本地"
                        : "谥字「" + shi + "」不在候选集内";
                }
            }

            // ── 庙号：候选集内，或空串（不祔庙）──
            string temple = ((string)obj["temple"] ?? "").Trim();
            if (temple.Length == 0)
                r.Temple = "";
            else if (templeCandidates != null && templeCandidates.Contains(temple))
                r.Temple = temple;

            // ── 跨类位移 ──
            int shift = 0;
            var sh = obj["shift"];
            if (sh != null) { int.TryParse(sh.ToString(), out shift); }
            if (shift < -1) shift = -1;
            if (shift > 1) shift = 1;
            r.Shift = shift;
            r.ShiftReason = ((string)obj["shift_reason"] ?? "").Trim();

            // ── 评语 ──
            string comment = ((string)obj["comment"] ?? "").Trim().Trim('"');
            string why = CheckComment(comment, minChars, maxChars, r.Shi ?? shi, temple);
            if (why == null) r.Comment = comment;
            else if (r.FailReason == null) r.FailReason = why;

            // ── 野史（LlmV2-1）：民间口吻另说；单独校验，不合格只丢野史保实录 ──
            string wild = ((string)obj["wild"] ?? "").Trim().Trim('"');
            string wildWhy = CheckComment(wild, minChars, maxChars, r.Shi ?? shi, temple);
            if (wildWhy == null) r.WildComment = wild;

            return r;
        }

        /// <summary>评语校验：返回 null 表示合格，否则返回原因。</summary>
        public static string CheckComment(string comment, int minChars, int maxChars,
                                          string shi, string temple)
        {
            if (string.IsNullOrEmpty(comment)) return "评语为空";
            if (comment.Length < minChars) return "评语过短（" + comment.Length + " 字）";
            if (comment.Length > maxChars) return "评语过长（" + comment.Length + " 字）";

            foreach (var w in ModernWords)
                if (comment.IndexOf(w, StringComparison.Ordinal) >= 0)
                    return "评语含现代词汇「" + w + "」";

            // 不复述谥号庙号（避免"景帝曰……"这种同义反复）
            if (!string.IsNullOrEmpty(shi) && shi.Length == 1 && comment.IndexOf(shi + "帝", StringComparison.Ordinal) >= 0)
                return "评语复述谥号";
            if (!string.IsNullOrEmpty(temple) && comment.IndexOf(temple, StringComparison.Ordinal) >= 0)
                return "评语复述庙号";

            return null;
        }
    }
}
