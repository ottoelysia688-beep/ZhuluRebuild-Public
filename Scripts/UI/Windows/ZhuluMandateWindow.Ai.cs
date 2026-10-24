using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 历代天命窗口 · AI 评价展示（2026-10-24 第7步）。
    ///
    /// 只做"叠加"：在现有三行制（帝号 / 功过书 / 史评）上各加一点，
    /// 不改变原有排版与数据来源。AI 未配置时全部退化为原样。
    ///
    /// 展示规则（用户 2026-10-24 确认）：
    ///   · 状态点放行首 ●已生成 / ◐生成中 / ○未生成 / ✕失败
    ///   · 「极盛 N 年」放行尾（来自轨迹峰值，无轨迹则不显示）
    ///   · AI 评语**替换**模板评语（不并列，避免同一段说两遍）
    ///   · 一键重写放朝代级状态条
    /// </summary>
    public partial class ZhuluMandateWindow
    {
        private static bool _aiSubscribed;

        /// <summary>帝王在 AI 存储里的键（含即位/卒年，同名不同帝可区分）。</summary>
        private static string AiKey(DynastyRecord d, EmperorRecord e, int idx)
        {
            return LlmCommentStore.BuildKey(d != null ? d.Dynasty : "", idx,
                                            e != null ? e.Name : "",
                                            e != null ? e.StartYear : 0,
                                            e != null ? e.EndYear : 0);
        }

        /// <summary>
        /// 行首状态点（LlmFix-3 四态：●已生成/◐生成中/×失败/○未生成）。
        /// 未启用 AI 时返回空串（不干扰原有排版）。
        /// </summary>
        private static string AiStatusDot(DynastyRecord d, EmperorRecord e, int idx)
        {
            if (!LlmEmperorService.Instance.Config.Enabled) return "";
            if (e == null || e.EndYear < 0) return "";       // 在位中不显示状态

            switch (LlmEmperorService.Instance.AiStatusOf(AiKey(d, e, idx)))
            {
                case LlmAiStatus.Done:
                    return $"<color={ZhuluUIStyle.Hex.Jade}>●</color> ";
                case LlmAiStatus.Queued:
                    return $"<color={ZhuluUIStyle.TextColor.Secondary}>◐</color> ";
                case LlmAiStatus.Failed:
                    return $"<color={ZhuluUIStyle.Hex.Vermilion}>×</color> ";
                default:
                    return $"<color={ZhuluUIStyle.TextColor.Tertiary}>○</color> ";
            }
        }

        /// <summary>行尾「极盛 N 年」。无轨迹（旧档）不显示，绝不臆断。</summary>
        private static string PeakTag(EmperorRecord e)
        {
            if (e == null || e.PeakYear <= 0) return "";
            return $" <color={ZhuluUIStyle.TextColor.Tertiary}>极盛 <color={ZhuluUIStyle.Hex.GoldDim}>{e.PeakYear}</color> 年</color>";
        }

        /// <summary>取 AI 评语；无则返回 null（调用方回退模板评语）。</summary>
        private static string TryGetAiComment(DynastyRecord d, EmperorRecord e, int idx)
        {
            if (e == null) return null;
            var c = LlmCommentStore.Instance.Get(AiKey(d, e, idx));
            if (c == null || string.IsNullOrEmpty(c.Comment)) return null;
            return c.Comment;
        }

        /// <summary>
        /// 朝代级 AI 状态条。返回占用的高度（未启用 AI 时返回 0，列表上移回原位）。
        /// </summary>
        private float BuildAiStatusBar(Transform parent, DynastyRecord d)
        {
            var svc = LlmEmperorService.Instance;
            if (!svc.Config.Enabled) return 0f;

            EnsureAiSubscription();

            float h = 18f;
            bool open = !svc.IsAvailable;

            Text bar = CreateText(parent,
                open
                    ? $"<color={ZhuluUIStyle.Hex.Vermilion}>●</color> <color={ZhuluUIStyle.TextColor.Secondary}>AI 不可用（{svc.UnavailableReason}）</color>"
                    : BuildBarText(svc),
                9);
            bar.alignment = TextAnchor.MiddleLeft;
            var br = bar.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1);
            br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(8, -28f);
            br.sizeDelta = new Vector2(_innerWidth - 120f, h);
            bar.raycastTarget = false;

            // 一键全部重写（朝代级）
            if (!open)
            {
                CreateSmallButton(parent, "一键重写", _innerWidth - 82f, -27f, 74f, 16f,
                    lbl => ReevaluateDynasty(d, lbl));
            }

            return h + 6f;
        }

        /// <summary>状态条文本：可用/已用，若有失败原因则一并显示（便于定位"入队了但没结果"）。</summary>
        private static string BuildBarText(LlmEmperorService svc)
        {
            string s = $"<color={ZhuluUIStyle.Hex.Jade}>●</color> <color={ZhuluUIStyle.TextColor.Secondary}>AI 可用</color>" +
                       $"　<color={ZhuluUIStyle.TextColor.Tertiary}>已用 {svc.RequestsUsed}/{svc.Config.MaxRequestsPerGame}</color>";
            if (!string.IsNullOrEmpty(svc.LastError))
            {
                // LlmFix-3：110 字原始片段会撑爆 18px 状态条——屏上截 40 字，全文进日志
                string err = svc.LastError;
                if (err.Length > 40)
                {
                    err = err.Substring(0, 40) + "…（详情见日志）";
                    try { ZhuluContext.Instance.LogRateLimited("LlmBarError", "[Llm] 上次失败详情: " + svc.LastError, 60f); }
                    catch { }
                }
                s += $"　<color={ZhuluUIStyle.Hex.Vermilion}>上次失败：{err}</color>";
            }
            return s;
        }

        /// <summary>行内「AI 重评」按钮（只重评这一位）。V2-8：左移让位给最右的「本纪 ▸」组成按钮组。</summary>
        private void CreateAiReevalButton(Transform parent, DynastyRecord d, EmperorRecord e, int idx, float y)
        {
            if (!LlmEmperorService.Instance.Config.Enabled) return;
            CreateSmallButton(parent, "AI 重评", _innerWidth - 110f, y + 2f, 48f, 14f,
                lbl => ReevaluateEmperor(d, e, idx, lbl));
        }

        /// <summary>小按钮；回调收到自己的 Text，便于点击后把结果写在按钮上（避免"点了没反应"）。</summary>
        private void CreateSmallButton(Transform parent, string label, float x, float y, float w, float h, Action<Text> onClick)
        {
            GameObject obj = new GameObject("AiBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            var rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);

            var img = obj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = new Color(0.22f, 0.20f, 0.16f, 0.95f);
            img.raycastTarget = true;

            Text t = CreateText(obj.transform, label, 8);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.98f, 0.77f, 0.44f);
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
            t.raycastTarget = false;

            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() =>
            {
                try { onClick(t); }
                catch (Exception) { try { t.text = "异常"; } catch { } }
            });
        }

        // ═══════════════ 重评 ═══════════════

        private void ReevaluateEmperor(DynastyRecord d, EmperorRecord e, int idx, Text label)
        {
            if (e == null || e.EndYear < 0) return;

            var svc = LlmEmperorService.Instance;
            bool ok = EnqueueOne(d, e, idx);

            // 把结果直接写在按钮上——"点了没反应"最难排查，必须给可见反馈
            if (label != null)
            {
                if (ok) label.text = "已提交";
                else label.text = ShortReason(svc.LastRejectReason);
            }
            try
            {
                ZhuluContext.Instance.LogRateLimited("AiReeval",
                    $"[Llm] 重评 {e.Name}：{(ok ? "已入队" : "被拒：" + svc.LastRejectReason)}", 2f);
            }
            catch { }

            RefreshHistoryPanelSafe();
        }

        private static string ShortReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "未提交";
            if (reason.Contains("熔断")) return "熔断中";   // LlmFix-1 M2：熔断拒绝要在按钮上可见
            if (reason.Contains("未启用")) return "未启用";
            if (reason.Contains("上限")) return "队列满";
            if (reason.Contains("已在队列")) return "排队中";
            if (reason.Contains("未初始化")) return "未加载";
            if (reason.Contains("已有评语")) return "已有";
            return "未提交";
        }

        private void ReevaluateDynasty(DynastyRecord d, Text label = null)
        {
            if (d == null || d.Emperors == null) return;
            int okCount = 0;
            for (int i = 0; i < d.Emperors.Count; i++)
            {
                var e = d.Emperors[i];
                if (e == null || e.EndYear < 0) continue;   // 在位中不评
                if (EnqueueOne(d, e, i + 1)) okCount++;
            }
            if (label != null) label.text = okCount > 0 ? $"已提交 {okCount}" : "未提交";
            RefreshHistoryPanelSafe();
        }

        /// <summary>构造并入队一条请求；返回是否成功入队。</summary>
        private bool EnqueueOne(DynastyRecord d, EmperorRecord e, int idx)
        {
            var svc = LlmEmperorService.Instance;
            if (!svc.Config.Enabled) return false;

            bool isFirst = idx == 1;
            bool lost = (d.EndYear != -1 && d.EndReason != "active" && idx == d.Emperors.Count);

            var facts = BuildFactsFromRecord(e, isFirst, lost);
            var usedShi = new HashSet<string>();
            var usedTempleSet = new HashSet<string>();
            for (int i = 0; i < idx - 1 && i < d.Emperors.Count; i++)
            {
                var p = d.Emperors[i];
                if (p == null) continue;
                if (!string.IsNullOrEmpty(p.PosthumousName)) usedShi.Add(p.PosthumousName);
                if (!string.IsNullOrEmpty(p.TempleName)) usedTempleSet.Add(p.TempleName);
            }

            // LlmFix-4 L6：补传已用庙号集合——原来传 null，庙号候选不去重
            var set = ShiCandidateProvider.Build(facts, usedShi, usedTempleSet);
            string key = AiKey(d, e, idx);
            string payload = LlmPromptBuilder.BuildUserPayload(
                facts, d.Dynasty, e.Name, e.Era, idx,
                idx > 1 ? d.Emperors[idx - 2].PosthumousName : "",
                idx > 1 ? d.Emperors[idx - 2].TempleName : "",
                set.Shi, set.Temple, new List<string>(usedShi), e.Track,
                LlmPromptBuilder.PickStyleOf(key, set.Base));   // LlmV2-12：笔法随帝轮换+谥类门控

            return svc.Enqueue(new LlmEmperorRequest
            {
                Key = key,
                SystemPrompt = LlmPromptBuilder.SystemPromptFor(svc.Config.MaxChars),
                UserPayload = payload,
                ShiCandidates = set.Shi,
                TempleCandidates = set.Temple,
                EndYear = e.EndYear,
                Manual = true
            }, force: true);
        }

        private static EmperorFacts BuildFactsFromRecord(EmperorRecord e, bool isFirst, bool lost)
        {
            int years = e.EndYear - e.StartYear;
            float popPct = 0f;
            if (e.StartPopulation > 0)
                popPct = (e.EndPopulation - e.StartPopulation) * 100f / e.StartPopulation;

            var f = new EmperorFacts
            {
                CityDelta = e.EndCities - e.StartCities,
                TreasDelta = e.EndTreasury - e.StartTreasury,
                MandDelta = e.EndMandate - e.StartMandate,
                StabDelta = e.EndStability - e.StartStability,
                ReignYears = years,
                PolicyCount = e.PolicyCount,
                WarCount = e.WarCount,
                VassalDelta = e.EndVassals - e.StartVassals,
                EndAge = e.EndAge,
                EndMandate = e.EndMandate,
                EndStability = e.EndStability,
                PopPct = popPct,
                IsFirstOfReign = isFirst,
                Unified = e.Unified == 1,
                Tyrant = (e.Deeds & PosthumousNameGenerator.DEED_TYRANT) != 0,
                Foolish = (e.Deeds & PosthumousNameGenerator.DEED_FOOLISH) != 0,
                Benevolent = (e.Deeds & PosthumousNameGenerator.DEED_BENEVOLENT) != 0,
                Abdicated = (e.Deeds & PosthumousNameGenerator.DEED_ABDICATION) != 0,
                Usurped = (e.Deeds & PosthumousNameGenerator.DEED_USURPED) != 0,
                LostThrone = lost,
                MandateCollected = e.StartMandate > 0f || e.EndMandate > 0f
            };

            if (e.Track != null && e.Track.Count >= 2)
                f.Shape = EmperorTrackSampler.Shape(e.Track);
            else
                f.Shape = EmperorTrackSampler.ShapeFromEndpoints(
                    EmperorSample.PowerScoreOf(e.StartCities, e.StartPopulation),
                    EmperorSample.PowerScoreOf(e.EndCities, e.EndPopulation));

            return f;
        }

        // ═══════════════ 刷新 ═══════════════

        private void EnsureAiSubscription()
        {
            if (_aiSubscribed) return;
            _aiSubscribed = true;
            try { LlmEmperorService.Instance.OnCommentReady += _ => RefreshHistoryPanelSafe(); }
            catch { }
        }

        private void RefreshHistoryPanelSafe()
        {
            try
            {
                if (World.world == null) return;
                if (!gameObject.activeInHierarchy) return;
                RefreshHistoryPanel();
            }
            catch { }
        }
    }
}
