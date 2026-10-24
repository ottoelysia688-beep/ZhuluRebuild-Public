using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;
using ZhuluRebuild.Presentation.Routing;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 天命窗口 v2（分层③数据）：数值/评语文本构造 + 主/历史面板刷新逻辑。
    /// 与核心文件同属 partial 类（ZhuluMandateWindow）。
    /// </summary>
    public partial class ZhuluMandateWindow : MonoBehaviour
    {
        // ═══════════════════════ 数据与文本（分层③）═══════════════════════

        /// <summary>阶段2O：增量段（绿▲/红▼/灰·）。</summary>
            private static string DeltaStr(double v, string suffix = "")
            {
                if (v >= 0.5) return $"<color={UI.ZhuluUIStyle.Hex.Positive}>+{v:F0}{suffix}</color>";
                if (v <= -0.5) return $"<color={UI.ZhuluUIStyle.Hex.Negative}>{v:F0}{suffix}</color>";
                // 2026-10-24：原来零值返回「·」，与项间分隔符「 · 」拼成「城· · 民」的双点乱码；
                // 改为显式 0，读起来是「城0 · 民-45%」。
                return $"<color={UI.ZhuluUIStyle.TextColor.Secondary}>0{suffix}</color>";
            }

        /// <summary>
        /// 2026-08-18：换行文本保守高度（preferredHeight 个别字体度量低估致评语/功过书重叠）。
        /// 去富文本标签后按 CJK 字宽（≈fontSize×1.05）估行数，与 preferredHeight 取大。
        /// </summary>
        private static float MeasureWrappedHeight(Text t, float width, float lineH)
        {
            float h;
            try { h = Mathf.Max(13f, t.preferredHeight); } catch { h = 13f; }
            try
            {
                // 去富文本标签（<color=#xxx>…</color> 等）
                string plain = System.Text.RegularExpressions.Regex.Replace(t.text ?? "", "<[^>]+>", "");
                if (!string.IsNullOrEmpty(plain) && width > 1f)
                {
                    float fontSize = t.fontSize > 0 ? t.fontSize : 8f;
                    float charW = fontSize * 1.05f;
                    int estLines = Mathf.Max(1, Mathf.CeilToInt(plain.Length * charW / width));
                    h = Mathf.Max(h, estLines * Mathf.Max(lineH, fontSize + 4f));
                }
            }
            catch { }
            return h;
        }

        /// <summary>阶段2O：行2 功过书（城/民/库/天命/稳/附庸增量 + 战·政辅助——旧档未采集自动省略）。</summary>
        private static string BuildDeedsLine(EmperorRecord e)
        {
            var sb = new StringBuilder("<color=#6a6458>");
            sb.Append("城").Append(DeltaStr(e.EndCities - e.StartCities));
            if (e.StartPopulation > 0)
            {
                int pct = (int)Mathf.Round((e.EndPopulation - e.StartPopulation) * 100f / Mathf.Max(1, e.StartPopulation));
                sb.Append(" · 民").Append(DeltaStr(pct, "%"));
            }
            sb.Append(" · 库").Append(DeltaStr(e.EndTreasury - e.StartTreasury));
            sb.Append(" · 天命").Append(DeltaStr(e.EndMandate - e.StartMandate));
            sb.Append(" · 稳").Append(DeltaStr(e.EndStability - e.StartStability));
            sb.Append(" · 附庸").Append(DeltaStr(e.EndVassals - e.StartVassals));
            sb.Append("</color>");
            if (e.WarCount >= 0 || e.PolicyCount >= 0)
            {
                sb.Append("　<color=#6a7a8f>");
                if (e.WarCount >= 0) sb.Append($"战{e.WarCount}");
                if (e.PolicyCount >= 0) sb.Append($" · 政{e.PolicyCount}");
                sb.Append("</color>");
            }
            return sb.ToString();
        }

        /// <summary>阶段2O：行3 盖棺史评（史评句 + 九等徽章 + 德行徽章 + 寿数）。
        /// 2026-10-24 第7步：新增 overrideVerdict——有 AI 评语时**只替换引号内的史评句**，
        /// 九等徽章/德行/谥法释义照常保留（原先整行替换会把这些信息一起丢掉）。</summary>
        private static string BuildVerdictLine(EmperorRecord e, bool lostThrone, string overrideVerdict = null)
        {
            var (rank, label, verdict) = PosthumousNameGenerator.BuildVerdict(e, lostThrone);
            bool hasOverride = !string.IsNullOrEmpty(overrideVerdict);
            if (rank < 0 && !hasOverride) return "";
            // LlmV2-2（用户确认）：AI 长评在列表**全文换行**（行高自适应，不截首句）
            string shown = hasOverride ? overrideVerdict : verdict;
            string rankColor = rank <= 1 ? "#FFD700" : rank <= 3 ? "#70d468" : rank <= 5 ? "#9a9a9a" : "#f08080";
            var sb = new StringBuilder();
            sb.Append($"「<color=#b8a87a>{shown}</color>」");
            if (rank >= 0) sb.Append($" <color={rankColor}>【{label}】</color>");
            if (e.Unified == 1) sb.Append(" <color=#FFD700>【大一统】</color>"); // 阶段2V
            if (e.SlewHolder == 1) sb.Append(" <color=#FFD700>【破敌天命】</color>"); // 阶段3H-10
            int d = e.Deeds;
            if ((d & PosthumousNameGenerator.DEED_BENEVOLENT) != 0) sb.Append(" <color=#70d468>仁</color>");
            if ((d & PosthumousNameGenerator.DEED_TYRANT) != 0) sb.Append(" <color=#f08080>暴</color>");
            if ((d & PosthumousNameGenerator.DEED_FOOLISH) != 0) sb.Append(" <color=#9a9a9a>昏</color>");
            if ((d & PosthumousNameGenerator.DEED_GENIUS) != 0) sb.Append(" <color=#FFD700>奇才</color>");
            if ((d & PosthumousNameGenerator.DEED_RESTORATION) != 0) sb.Append(" <color=#FFD700>复国</color>");
            if ((d & PosthumousNameGenerator.DEED_ABDICATION) != 0) sb.Append(" <color=#88b8e8>禅让</color>");
            if ((d & PosthumousNameGenerator.DEED_USURPED) != 0) sb.Append(" <color=#f08080>被篡</color>");
            if (e.EndAge > 0) sb.Append($" <color=#5a5f6e>寿{PosthumousNameGenerator.ToChinese(e.EndAge)}</color>");
            // 阶段3：谥法释义（看懂为何得此谥——古义注脚）
            string shifa = PosthumousNameGenerator.GetShifaMeaning(e.PosthumousName);
            if (!string.IsNullOrEmpty(shifa))
                sb.Append($" <color=#6a6458>（谥法：{shifa}）</color>");
            return sb.ToString();
        }

        /// <summary>阶段2M-29：数字转中文（期号）。</summary>
        private static string CnNum(int n)
        {
            string[] cn = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
            if (n >= 1 && n <= 9) return cn[n];
            return n.ToString();
        }

        /// <summary>朝代终结原因中文映射</summary>
        private static string DynastyEndReasonText(string reason)
        {
            switch (reason)
            {
                case "active": return "今上";
                case "vacant": return "天命空悬";
                case "亡国失命": return "亡国失命";
                case "衰微失命": return "衰微失命";
                default: return string.IsNullOrEmpty(reason) || reason == "-1" ? "天命易主" : reason;
            }
        }

        /// <summary>去掉名字中的称号后缀（如"郁瑛 - 皇帝"→"郁瑛"）。</summary>
        private static string StripTitle(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int idx = name.IndexOf(" - ");
            if (idx > 0) return name.Substring(0, idx);
            return name;
        }


        // 阶段3H-16性能：问鼎者行按年缓存
        private int _challengerCacheYear = -1;
        private string _challengerCacheHolder = "";
        private string _challengerCacheText = "";

        private void RefreshMainPanel()
        {
            if (_mainPanel == null || !_mainPanel.activeSelf) return;
            if (_showNomadic)
            {
                RefreshNomadicMainPanel();
                return;
            }

            string holderId = OrthodoxMandateManager.Instance.GetMandateHolderId();

            if (holderId == null)
            {
                _mainValueText.text = "—";
                _mainValueText.color = new Color(0.55f, 0.5f, 0.4f);
                _mainGradeText.text = "天命空悬";
                _mainGradeText.color = new Color(0.7f, 0.3f, 0.3f);
                if (_mandateIcon != null) _mandateIcon.gameObject.SetActive(false);
                SetBar(0f);
                _mainInfoText.text = "\n<color=#FF4500>【天命空悬】</color>\n天下无主，列国争雄，神器虚悬以待有德。\n\n<color=#888888>受命之阶：国力点 ≥ 40 · 立国 ≥ 15 年 · 净礼治 ≥ 8 · 无宗主</color>"; // 阶段2O：修正 2M-32 语义颠倒文案（实际=国力点≥40，用户拍板去城数路径）
                _mainEffectText.text = BuildContendersLine();
                if (_challengerText != null) _challengerText.text = "";   // 空悬无争位者（窥鼎者已在效果行）
                return;
            }

            Kingdom k = KingdomDataExt.FindKingdomById(holderId);
            if (k == null) return;

            float val = OrthodoxMandateManager.Instance.GetMandateValue(holderId);
            string grade = OrthodoxMandateManager.Instance.GetMandateGrade(val);
            string colorHex = OrthodoxMandateManager.Instance.GetMandateColor(val);
            string eraName = OrthodoxMandateManager.Instance.GetEraName(holderId);
            string dynasty = OrthodoxMandateManager.Instance.GetDynastyName(holderId);
            string kingName = (k.king != null && k.king.isAlive()) ? StripTitle(k.king.getName()) : "未知";
            int cityCount = k.cities != null ? k.cities.Count : 0;
            int army = 0;
            try { army = k.countTotalWarriors(); } catch { }
            float stabBonus = OrthodoxMandateManager.Instance.GetStabilityBonus(val);

            _mainValueText.text = $"{(int)val}";
            _mainValueText.color = ParseHexColor(colorHex, new Color(1f, 0.84f, 0f));
            _mainGradeText.text = grade;   // grade 已含"天命"前缀（如"天命平稳"），勿重复拼接
            _mainGradeText.color = ParseHexColor(colorHex, new Color(1f, 0.84f, 0f));
            SetBar(val);

            // 天命玉佩图标（按天命值映射：隆盛→受命/昌盛→强盛/平稳→稳定/衰微→衰弱/将尽→动摇/欲绝→已失）
            if (_mandateIcon != null)
            {
                string iconKey = val >= 90f ? "blessed" : val >= 70f ? "strong" : val >= 50f ? "stable"
                    : val >= 30f ? "weak" : val >= 10f ? "shaken" : "lost";
                SetSlicedSprite(_mandateIcon, $"ui/zhulu/governance/zlr_mandate_{iconKey}_24");
                _mandateIcon.gameObject.SetActive(true);
            }

            _mainInfoText.text =
                BuildTrendLine(holderId, val) +
                $"<color={UI.ZhuluUIStyle.Hex.Imperial}>【{dynasty}朝 · 今上】</color>\n" +
                $"皇帝 <color=#6ca3fd>{kingName}</color> <color=#ffd8d9>（在位{PosthumousNameGenerator.ToChinese(ReignYears(holderId, k))}年）</color>\n" +
                $"年号 <color=#e0554a>{eraName}</color> · 疆域 <color=#e0554a>{PosthumousNameGenerator.ToChinese(cityCount)}城</color> · 兵锋 <color=#e0554a>{PosthumousNameGenerator.ToChinese(army)}</color>\n" +
                $"<color={UI.ZhuluUIStyle.Hex.Warn}>礼治 {ZhuluLiZhiService.GetLiZhiScore(holderId)}</color>（受命之阶 · 净礼治≥8）· 宗庙礼制 +2 / 祭祖 +3 / 祭天 +8\n" +
                BuildMandateDetailLine(holderId, k) +
                BuildUnityLine(holderId, k);
            // 效果明细（稳定度 + 忠诚，按区间）
            float loyaltyBonus = 0f;
            if (val >= 90f) loyaltyBonus = 5f;
            else if (val >= 70f) loyaltyBonus = 3f;
            else if (val < 10f) loyaltyBonus = -10f;
            else if (val < 30f) loyaltyBonus = -5f;
            bool good = stabBonus >= 0;
            string stabStr = good
                ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>✦ 稳定度 +{stabBonus:F0}</color>"
                : $"<color={UI.ZhuluUIStyle.Hex.Negative}>✦ 稳定度 {stabBonus:F0}</color>";
            string loyalStr = loyaltyBonus >= 0
                ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>忠诚 +{loyaltyBonus:F0}</color>"
                : $"<color={UI.ZhuluUIStyle.Hex.Negative}>忠诚 {loyaltyBonus:F0}</color>";
            string tribStr2 = val >= 70f ? "　<color=#f0c060>朝贡 +10%</color>" : (val >= 50f ? "　<color=#f0c060>朝贡 +5%</color>" : "");
            _mainEffectText.text = $"{stabStr}　{loyalStr}{tribStr2}　<color={UI.ZhuluUIStyle.Hex.Muted}>（{grade}区间加成）</color>";

            // 2026-08-20 问鼎者行：可对当前朝代发动夺天命之战的强国（资格实时可视化）
            if (_challengerText != null)
            {
                // 阶段3H-16性能：问鼎者行按年缓存（资格与军力对比一年一变，全王国兵力统计开销大）
                int curYear = ZhuluTime.CurrentYear;
                if (_challengerCacheYear != curYear || _challengerCacheHolder != holderId)
                {
                    _challengerCacheYear = curYear;
                    _challengerCacheHolder = holderId;
                    _challengerCacheText = BuildMandateChallengersLine(holderId, k);
                }
                _challengerText.text = _challengerCacheText;
            }

            // 易主预警条（天命 < 可配置阈值（默认30）且非空悬）
            if (_warnBar != null)
            {
                bool warn = val < ZhuluConfig.Instance.MandateWarnThreshold;
                _warnBar.SetActive(warn);
                if (warn) _warnText.text = "⚠ 天命衰微，恐有易主之兆！";
            }
        }

        private void RefreshNomadicMainPanel()
        {
            string holderId = NomadicMandateManager.Instance.GetMandateHolderId();
            if (string.IsNullOrEmpty(holderId))
            {
                _mainValueText.text = "—";
                _mainGradeText.text = "长生天眷空悬";
                _mainGradeText.color = new Color(0.7f, 0.3f, 0.3f);
                if (_mandateIcon != null) _mandateIcon.gameObject.SetActive(false);
                SetBar(0f);
                _mainInfoText.text = "\n<color=#c8b98a>【长生天眷空悬】</color>\n草原诸部尚无共主。\n\n<color=#888888>受眷之阶：国力点 ≥ 40 · 立国 ≥ 15 年 · 汗威 ≥ 8 · 无宗主</color>";
                _mainEffectText.text = "游牧国家达到条件后，可建立王庭，首领称天可汗。";
                _challengerText.text = ""; _warnBar.SetActive(false);
                return;
            }

            Kingdom k = KingdomDataExt.FindKingdomById(holderId);
            if (k == null) return;
            float value = NomadicMandateManager.Instance.GetMandateValue(holderId);
            string grade = NomadicMandateManager.Instance.GetMandateGrade(value);
            string color = NomadicMandateManager.Instance.GetMandateColor(value);
            float stability = NomadicMandateManager.Instance.GetStabilityBonus(value);
            string king = k.king != null && k.king.isAlive() ? StripTitle(k.king.getName()) : "未知";
            int cities = k.cities?.Count ?? 0;
            int army = 0; try { army = k.countTotalWarriors(); } catch { }

            _mainValueText.text = ((int)value).ToString();
            _mainValueText.color = ParseHexColor(color, new Color(0.84f, 0.68f, 0.40f));
            _mainGradeText.text = grade; _mainGradeText.color = _mainValueText.color;
            SetBar(value);
            if (_mandateIcon != null) _mandateIcon.gameObject.SetActive(true);
            _mainInfoText.text =
                BuildNomadTrendLine(holderId, value) +
                $"<color=#c8b98a>【{k.name} · 王庭】</color>\n" +
                $"天可汗 <color=#6ca3fd>{king}</color>\n" +
                $"疆域 <color=#e0554a>{cities}城</color> · 兵锋 <color=#e0554a>{army}</color>\n" +
                $"<color={UI.ZhuluUIStyle.Hex.Warn}>汗威 {ZhuluLiZhiService.GetLiZhiScore(holderId)}</color>（受眷之阶 · 净汗威≥8）\n" +
                $"<color=#8a8072>{NomadicMandateManager.Instance.GetDetail(holderId)}</color>\n" +
                BuildNomadCourtLine(holderId, k);
            float loyalty = value >= 90f ? 5f : value >= 70f ? 3f : value < 10f ? -10f : value < 30f ? -5f : 0f;
            string stabilityText = stability >= 0f ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>✦ 稳定度 +{stability:F0}</color>" : $"<color={UI.ZhuluUIStyle.Hex.Negative}>✦ 稳定度 {stability:F0}</color>";
            string loyaltyText = loyalty >= 0f ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>忠诚 +{loyalty:F0}</color>" : $"<color={UI.ZhuluUIStyle.Hex.Negative}>忠诚 {loyalty:F0}</color>";
            _mainEffectText.text = $"{stabilityText}　{loyaltyText}　<color={UI.ZhuluUIStyle.Hex.Muted}>（与东方天命同数值档）</color>";
            _challengerText.text = ""; // 阶段3F：用户要求删策略说明文案
            bool warn = value < ZhuluConfig.Instance.MandateWarnThreshold;
            _warnBar.SetActive(warn); if (warn) _warnText.text = "⚠ 天眷衰微，王庭恐将失眷！";
        }

        /// <summary>阶段3H-13：游牧天眷趋势行（东方 BuildTrendLine 同构）。</summary>
        private string BuildNomadTrendLine(string holderId, float currentVal)
        {
            var trend = NomadicMandateManager.Instance.GetTrend(holderId);
            var sb = new StringBuilder("<color=#888888>趋势</color> ");
            if (trend.Count >= 2 && trend[0] >= 0f && trend[1] >= 0f)
            {
                float d = trend[0] - trend[1];
                sb.Append(d >= 0f
                    ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>▲ +{d:F1}</color>"
                    : $"<color={UI.ZhuluUIStyle.Hex.Negative}>▼ {d:F1}</color>");
            }
            else sb.Append("<color=#888888>—</color>");
            if (trend.Count >= 3 && trend[1] >= 0f && trend[2] >= 0f)
            {
                float d = trend[1] - trend[2];
                sb.Append(d >= 0f
                    ? $"　<color={UI.ZhuluUIStyle.Hex.Positive}>▲ +{d:F1}</color>"
                    : $"　<color={UI.ZhuluUIStyle.Hex.Negative}>▼ {d:F1}</color>");
            }
            sb.Append("<color=#888888>（十年一评）</color>\n");
            return sb.ToString();
        }

        /// <summary>阶段3H-2：王庭世系行——历N汗 · 当今汗号年号 · 在位年 · 天眷趋势（草原式，按钮位不动）。</summary>
        private string BuildNomadCourtLine(string holderId, Kingdom k)
        {
            try
            {
                var courts = NomadicMandateManager.Instance.GetCourts();
                var open = courts.FindLast(c => c.EndYear == -1 && c.KingdomId == holderId);
                int khanCount = open != null ? open.Khans.Count : 0;
                string era = open != null && open.Khans.Count > 0 ? (open.Khans[open.Khans.Count - 1].Era ?? "") : "";
                string eraTxt = string.IsNullOrEmpty(era) ? "" : $" · 汗号「{era}」";
                int reign = 0;
                try { long rs = KingdomDataExt.GetLong(k, "reign_start", ZhuluTime.CurrentYear); reign = Mathf.Max(0, ZhuluTime.CurrentYear - (int)rs); } catch { }
                var tr = NomadicMandateManager.Instance.GetTrend(holderId);
                string trendTxt = "<color=#8a8578>—初立</color>";
                if (tr != null && tr.Count >= 2 && tr[0] >= 0f && tr[1] >= 0f)
                    trendTxt = tr[0] > tr[1] ? "<color=#70d468>▲上扬</color>" : tr[0] < tr[1] ? "<color=#f73939>▼下沉</color>" : "<color=#8a8578>━持平</color>";
                return $"<color=#c8b98a>世系</color> <color=#ffd8d9>历{khanCount}汗</color>{eraTxt} · 在位{reign}年 · 天眷{trendTxt}";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 阶段2V：大一统行——在位皇帝已达成显示金字；未达成显示实时王化比例（本国+附庸/天下）。
        /// </summary>
        private string BuildUnityLine(string holderId, Kingdom k)
        {
            try
            {
                var (cityRatio, popRatio) = OrthodoxMandateManager.CalcUnityRatio(k);
                var hist = OrthodoxMandateManager.Instance.GetHistory();
                var curEmp = hist.FindLast(d => d.EndYear == -1 && d.KingdomId == holderId)
                    ?.Emperors.FindLast(x => x.EndYear == -1);
                if (curEmp != null && curEmp.Unified == 1)
                    return $"\n<color={UI.ZhuluUIStyle.Hex.Imperial}>☀ 大一统 · 混一寰宇（城{cityRatio:P0} · 民{popRatio:P0}）</color>";
                return $"\n<color=#8a8578>王化 城{cityRatio:P0} · 民{popRatio:P0}（大一统之阶 ≥90%）</color>";
            }
            catch { return ""; }
        }

        /// <summary>趋势行：▲/▼ 最近三次结算</summary>
        private string BuildTrendLine(string holderId, float currentVal)
        {
            var trend = OrthodoxMandateManager.Instance.GetTrend(holderId);
            var sb = new StringBuilder("<color=#888888>趋势</color> ");
            // 最近 vs 上期
            if (trend.Count >= 2 && trend[0] >= 0f && trend[1] >= 0f)
            {
                float d = trend[0] - trend[1];
                sb.Append(d >= 0f
                    ? $"<color={UI.ZhuluUIStyle.Hex.Positive}>▲ +{d:F1}</color>"
                    : $"<color={UI.ZhuluUIStyle.Hex.Negative}>▼ {d:F1}</color>");
            }
            else sb.Append("<color=#888888>—</color>");
            // 上期 vs 更早
            if (trend.Count >= 3 && trend[1] >= 0f && trend[2] >= 0f)
            {
                float d = trend[1] - trend[2];
                sb.Append(d >= 0f
                    ? $"　<color={UI.ZhuluUIStyle.Hex.Positive}>▲ +{d:F1}</color>"
                    : $"　<color={UI.ZhuluUIStyle.Hex.Negative}>▼ {d:F1}</color>");
            }
            sb.Append("<color=#888888>（十年一评）</color>\n");
            return sb.ToString();
        }

        /// <summary>
        /// 2026-08-19 串联：天人感应明细行——读 mandate_detail（天命结算各修正项加减），
        /// 正向绿/负向红，玩家首次可见"天命为何涨跌"。
        /// </summary>
        private string BuildMandateDetailLine(string holderId, Kingdom k)
        {
            try
            {
                if (k == null) return "";
                string raw = KingdomDataExt.GetStr(k, "mandate_detail");
                if (string.IsNullOrEmpty(raw)) return "";
                // 格式："城池+12　国祚+10　稳定+10　交战-5"——逐项上色
                var sb = new StringBuilder("<color=#8a8578>☯ 天人感应：</color>");
                foreach (var token in raw.Split('　'))
                {
                    if (string.IsNullOrEmpty(token)) continue;
                    bool neg = token.Contains("-");
                    sb.Append(neg
                        ? $"<color=#e0554a>{token}</color>　"
                        : $"<color=#70d468>{token}</color>　");
                }
                return sb.ToString().TrimEnd('　') + "\n";
            }
            catch { return ""; }
        }

        /// <summary>皇帝在位年数</summary>
        private int ReignYears(string holderId, Kingdom k)
        {
            try
            {
                long start = KingdomDataExt.GetLong(k, "reign_start", -1);
                if (start < 0) return 0;
                return Mathf.Max(1, ZhuluTime.CurrentYear - (int)start);
            }
            catch { return 0; }
        }

        // 2026-08-23 清理：BuildRecentEvents 已随 _mainEventText 移除（近期天命块阶段3G 删除后成为死代码）

        /// <summary>阶段2O：空悬期「窥鼎者」列表（独立国按国力点取前 3——受命基业=国力点≥40，标注差距原因）。
        /// 修复 2M-32 反向筛选 bug：原按"城<6"过滤，显示的恰是无资格小国、隐藏了真正候选。</summary>
        private string BuildContendersLine()
        {
            try
            {
                var cands = new List<(string name, int pp, int lz, bool ready, string note)>();
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom kk in World.world.kingdoms.list)
                    {
                        if (kk == null || !kk.isAlive()) continue;
                        if (VassalManager.Instance.IsVassal(kk)) continue;
                        int pp = ZhuluTierService.GetPowerPoints(kk);
                        if (pp < 25) continue; // 窥鼎门槛：过弱者不上榜
                        int lz = ZhuluLiZhiService.GetNetLiZhi(kk.id.ToString());
                        bool baseOk = pp >= 40;
                        bool lizhiOk = lz >= 8;
                        string note = baseOk && lizhiOk ? "" : (baseOk ? "礼制未逮" : "基业不足");
                        cands.Add((kk.name ?? "?", pp, lz, baseOk && lizhiOk, note));
                    }
                }
                if (cands.Count == 0) return "<color=#8a8578>列国孱弱，尚无窥鼎者</color>";
                // 按国力点降序取前 3
                cands.Sort((a, b) => b.pp.CompareTo(a.pp));
                var parts = new List<string>();
                for (int i = 0; i < cands.Count && i < 3; i++)
                {
                    var c = cands[i];
                    string mark = c.ready
                        ? "<color=#70d468>可受命</color>"
                        : $"<color=#8a8578>（{c.note}）</color>";
                    parts.Add($"{c.name} <color={ZhuluUIStyle.Hex.Gold}>{c.pp}界</color>·礼{c.lz} {mark}");
                }
                return "⚔ 窥鼎者：" + string.Join("　", parts);
            }
            catch { return ""; }
        }


        /// <summary>
        /// 2026-08-20 问鼎者行：列出可对当前天命朝代发动夺天命之战的强国（前2名）。
        /// 完全合格=绿✓；差一档=灰色括注缺项（人口/兵力不足·礼治未逮·爵位不足·交战中·和约期·冷却）。
        /// 诊断价值：玩家可直观看出"为何问鼎未触发"（门槛维持双80%，达标后 AI 年度 8%~30% 概率触发——数据优势越大概率越高）。
        /// </summary>
        private string BuildMandateChallengersLine(string holderId, Kingdom holder)
        {
            try
            {
                int year = ZhuluTime.CurrentYear;
                int hPop = holder.units != null ? holder.units.Count : 0;
                int hArmy = 0;
                try { hArmy = holder.countTotalWarriors(); } catch { }

                var ranked = new List<(Kingdom k, int pop, bool ok, string note)>();
                bool holderNomad = false;
                try { holderNomad = Domain.KingdomCivilizationService.Instance.IsNomadic(holder); } catch { }
                foreach (Kingdom kk in World.world.kingdoms.list)
                {
                    if (kk == null || !kk.isAlive() || kk == holder) continue;
                    // 阶段3G：问鼎者限同体系（东方天命只列东方国；长生天眷只列游牧国）
                    try { if (Domain.KingdomCivilizationService.Instance.IsNomadic(kk) != holderNomad) continue; } catch { }
                    int pop = kk.units != null ? kk.units.Count : 0;
                    // 预筛：人口≥天命国40%或兵力≥40%才上榜（纯弱国无诊断价值）
                    int army = 0;
                    try { army = kk.countTotalWarriors(); } catch { }
                    bool nearPop = hPop <= 0 || pop * 100 >= hPop * 40;
                    bool nearArmy = hArmy <= 0 || army * 100 >= hArmy * 40;
                    if (!nearPop && !nearArmy) continue;
                    ranked.Add((kk, pop, false, ""));
                }
                if (ranked.Count == 0) return "<color=#6a6458>⚔ 问鼎者：列国势弱，无人敢窥九鼎</color>";

                ranked.Sort((a, b) => b.pop.CompareTo(a.pop));
                var parts = new List<string>();
                int shown = 0;
                foreach (var (kk, _, _, _) in ranked)
                {
                    if (shown >= 2) break;
                    bool ok = ZhuluWarService.CanMandateWar(kk, holder, year);
                    string note;
                    if (ok)
                    {
                        // 2026-08-19 概率制：合格者显示年度问鼎概率（数据超越越大概率越高，8%~30%）
                        int pct = Mathf.RoundToInt(ZhuluWarService.GetMandateWarChance(kk, holder) * 100f);
                        note = $"<color={UI.ZhuluUIStyle.Hex.Positive}>✓可问鼎{pct}%</color>";
                    }
                    else
                    {
                        // 差距诊断（按常见程度排序，取第一命中）
                        if (VassalManager.Instance.GetSuzerain(kk) == holder) note = "附庸";
                        else if (PolicyManager.GetKingdomRank(kk, false) < ZhuluWarService.MANDATE_WAR_MIN_RANK) note = "爵卑";
                        else if (ZhuluLiZhiService.GetLiZhiScore(kk.id.ToString()) < ZhuluWarService.MANDATE_WAR_MIN_LIZHI) note = "礼薄";
                        else
                        {
                            int cPop = kk.units != null ? kk.units.Count : 0;
                            int cArmy = 0;
                            try { cArmy = kk.countTotalWarriors(); } catch { }
                            if (hPop > 0 && cPop * 100 < hPop * ZhuluWarService.MANDATE_WAR_MIN_RATIO) note = "民寡";
                            else if (hArmy > 0 && cArmy * 100 < hArmy * ZhuluWarService.MANDATE_WAR_MIN_RATIO) note = "兵弱";
                            else if (ZhuluDiplomacyService.IsWarForbidden(kk.id.ToString(), holderId, year)) note = "和约";
                            else if (ZhuluWarService.IsInMandateWarCooldown(kk, year)) note = "冷却";
                            else note = "交战";
                        }
                        note = $"<color=#8a8578>({note})</color>";
                    }
                    parts.Add($"{kk.name}{note}");
                    shown++;
                }
                return $"<color={UI.ZhuluUIStyle.Hex.Warn}>⚔ 问鼎者</color>\n{string.Join("　", parts)}"; // 阶段3F：删条件提示
            }
            catch { return ""; }
        }


        private void RefreshHistoryPanel()
        {
            // 不检查 activeSelf——面板未激活时调用也要刷新（否则 ShowHistory 时序问题会导致永远空白）
            if (_historyPanel == null) return;

            // 清空旧卡片（SetActive(false) 立即隐藏，Destroy 延迟释放）
            for (int i = _historyList.childCount - 1; i >= 0; i--)
            {
                var child = _historyList.GetChild(i);
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            if (_showNomadic)
            {
                BuildNomadicHistory();
                RecalculateHistoryHeight();
                return;
            }

            var history = OrthodoxMandateManager.Instance.GetHistory();

            // LlmV2-2：断代史卡（默认折叠，最新在上）——列表态与详情态都挂顶部
            BuildChronicleCards();

            // LlmV2-2：帝王详情态——整页换成单帝本纪（实录/野史 tab、柱状轨迹、明细表）
            if (_detailEmperor != null)
            {
                try { BuildEmperorDetailCard(); } catch { }
                RecalculateHistoryHeight();
                return;
            }
            // 传承统计条
            if (_historyStatText != null)
            {
                int dynasties = 0, voidYears = 0, totalYears = 0;
                int curYear = ZhuluTime.CurrentYear;
                foreach (var d in history)
                {
                    if (d.Dynasty == "空悬")
                    {
                        voidYears += d.EndYear == -1 ? Mathf.Max(0, curYear - d.StartYear) : Mathf.Max(0, d.EndYear - d.StartYear);
                    }
                    else
                    {
                        dynasties++;
                        totalYears += d.EndYear == -1 ? Mathf.Max(0, curYear - d.StartYear) : Mathf.Max(0, d.EndYear - d.StartYear);
                    }
                }
                var uniqK = new System.Collections.Generic.HashSet<string>();
                foreach (var dd in history) { if (!string.IsNullOrEmpty(dd.KingdomId)) uniqK.Add(dd.KingdomId); }
                string uniqStr = uniqK.Count > 0 ? $" · {uniqK.Count} 国曾受命" : "";
                _historyStatText.text = dynasties == 0
                    ? "<color=#8a8578>天命尚无传承</color>"
                    : $"<color={ZhuluUIStyle.Hex.Gold}>历 {dynasties} 朝</color>{uniqStr} · 传承 <color={UI.ZhuluUIStyle.Hex.Positive}>{totalYears} 年</color> · 空悬 <color=#ac8b7f>{voidYears} 年</color>";
            }
            _historyEmptyText.gameObject.SetActive(false);

            // 倒序（最新的在上面）
            for (int i = history.Count - 1; i >= 0; i--)
            {
                try { BuildDynastyCard(history[i]); } catch { }
            }

            // ── 手动计算总高度（双保险：不依赖 ContentSizeFitter 链——动态 UI 上 fitter 有时不触发重建）──
            RecalculateHistoryHeight();
        }

        private void BuildNomadicHistory()
        {
            // 阶段3H：结构化王庭记录优先（与东方朝代卡同构）；旧档无记录回落日志行
            var courts = NomadicMandateManager.Instance.GetCourts();
            if (courts.Count > 0)
            {
                BuildNomadicCourtCards(courts);
                return;
            }
            List<string> history = NomadicMandateManager.Instance.GetHistory();
            if (_historyStatText != null)
                _historyStatText.text = history.Count == 0 ? "<color=#8a8578>长生天眷尚无传承</color>"
                    : $"<color=#c8b98a>长生天眷变迁 {history.Count} 次</color>";
            _historyEmptyText.gameObject.SetActive(history.Count == 0);
            if (history.Count == 0) _historyEmptyText.text = "尚无王庭获得长生天眷";
            for (int i = history.Count - 1; i >= 0; i--)
            {
                string[] p = (history[i] ?? "").Split('|');
                // 阶段3F-5：按记录类型结构化渲染（立庭/失眷/汗位传承——汗位行·全名+年号高亮）
                string pType = p.Length >= 2 ? p[1] : "";
                string typeLabel; string typeColor; float rowH; Color bg;
                if (pType == "获得") { typeLabel = "蒙天眷·立庭"; typeColor = "#c8b98a"; rowH = 38f; bg = new Color(0.22f, 0.16f, 0.07f, 0.85f); }
                else if (pType == "失去") { typeLabel = "失眷"; typeColor = "#f08080"; rowH = 38f; bg = new Color(0.20f, 0.09f, 0.09f, 0.85f); }
                else if (pType == "汗位") { typeLabel = "汗位传承"; typeColor = "#FFD700"; rowH = 42f; bg = new Color(0.16f, 0.14f, 0.08f, 0.85f); }
                else { typeLabel = pType; typeColor = "#c8b98a"; rowH = 34f; bg = new Color(0.17f, 0.12f, 0.07f, 0.75f); }
                string line = p.Length >= 5
                    ? $"<color={typeColor}>{p[0]}年 · {typeLabel}</color>　<color=#f0d8a8>{p[3]}</color>　<color=#8a8578>{p[4]}</color>"
                    : history[i];
                GameObject row = new GameObject("NomadicHistory", typeof(RectTransform), typeof(Image));
                row.transform.SetParent(_historyList, false);
                row.GetComponent<RectTransform>().sizeDelta = new Vector2(_innerWidth - 20f, rowH);
                row.GetComponent<Image>().color = bg;
                Text t = CreateText(row.transform, line, 9); t.alignment = TextAnchor.MiddleLeft;
                RectTransform tr = t.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(8f, 2f); tr.offsetMax = new Vector2(-8f, -2f);
            }
        }

        // ═══════════ 阶段3H：王庭卡片渲染（东方朝代卡同构——草原式）═══════════
        private static string NomadCourtStateKey(NomadicMandateManager.CourtRecord c)
            => "nc_" + c.KingdomId + "_" + c.StartYear;

        private bool IsNomadCourtExpanded(NomadicMandateManager.CourtRecord c, bool isActive)
            => ZhuluRebuild.Persistence.ZhuluPreferences.GetFold("mandate:" + NomadCourtStateKey(c), isActive);   // 批次9d 持久化

        private void BuildNomadicCourtCards(List<NomadicMandateManager.CourtRecord> courts)
        {
            int khanTotal = 0;
            foreach (var c0 in courts) khanTotal += c0.Khans.Count;
            if (_historyStatText != null)
                _historyStatText.text = $"<color=#c8b98a>长生天眷传承 {courts.Count} 王庭 · {khanTotal} 汗</color>";
            _historyEmptyText.gameObject.SetActive(false);

            for (int ci = courts.Count - 1; ci >= 0; ci--)
            {
                var c = courts[ci];
                bool active = c.EndYear == -1;
                bool expanded = IsNomadCourtExpanded(c, active);

                // Row 容器（阶段3H-6：与东方朝代卡完全同构——双层底/-36左延/折叠/换行防溢出）
                GameObject row = new GameObject("NomadCourt", typeof(RectTransform));
                row.transform.SetParent(_historyList, false);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.sizeDelta = new Vector2(_innerWidth, 20f);

                GameObject solidObj = new GameObject("SolidBg", typeof(RectTransform), typeof(Image));
                solidObj.transform.SetParent(row.transform, false);
                var solidRect = solidObj.GetComponent<RectTransform>();
                solidRect.anchorMin = Vector2.zero;
                solidRect.anchorMax = Vector2.one;
                solidRect.offsetMin = new Vector2(-36f, 0f);
                solidRect.offsetMax = Vector2.zero;
                var solidImg = solidObj.GetComponent<Image>();
                solidImg.color = active ? new Color(0.20f, 0.17f, 0.09f, 0.95f) : new Color(0.12f, 0.12f, 0.15f, 0.92f);
                solidImg.raycastTarget = false;

                GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
                bgObj.transform.SetParent(row.transform, false);
                var bgRect = bgObj.GetComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = new Vector2(-36f, 0f);
                bgRect.offsetMax = Vector2.zero;
                var bgImg = bgObj.GetComponent<Image>();
                bgImg.color = active ? new Color(0.20f, 0.17f, 0.09f, 0.95f) : new Color(0.14f, 0.14f, 0.17f, 0.9f);
                bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
                bgImg.type = Image.Type.Sliced;
                bgImg.raycastTarget = false;

                // 头部行：▼/▶【X王庭】 历N汗 起止（终因）——草原式（前朝默认收起，点击展开）
                string period = active ? $"{c.StartYear}年~今" : $"{c.StartYear}年~{c.EndYear}年";
                string reasonColor = active ? "#c8b98a" : "#f73939";
                string reason = active ? "（当今王庭）" : $"（{NomadicEndReasonText(c.EndReason)}）";
                string courtColor = active ? "#c8b98a" : "#FF9A8A";
                string courtName = string.IsNullOrEmpty(c.Court) ? "未知" : c.Court;
                string arrow = expanded ? "▼" : "▶";
                string headText = $"<b>{arrow}<color={courtColor}>【{courtName}王庭】</color></b> 历{c.Khans.Count}汗　" +
                                  $"<color=#ffd8d9>{period}</color> <color={reasonColor}>{reason}</color>" +
                                  (expanded ? "" : " <color=#8a8578>·点击展开诸汗</color>");
                Text head = CreateText(row.transform, headText, 10);
                head.alignment = TextAnchor.MiddleLeft;
                head.horizontalOverflow = HorizontalWrapMode.Wrap;
                head.verticalOverflow = VerticalWrapMode.Overflow;
                var headRect = head.GetComponent<RectTransform>();
                headRect.anchorMin = new Vector2(0, 1);
                headRect.anchorMax = new Vector2(0, 1);
                headRect.pivot = new Vector2(0, 1);
                headRect.anchoredPosition = new Vector2(8, -8);
                headRect.sizeDelta = new Vector2(_innerWidth - 16, 16f);
                head.raycastTarget = false;

                string stateKey = NomadCourtStateKey(c);
                if (!expanded)
                {
                    // 收起态：头部即卡（透明点击层）
                    GameObject clickLayer = new GameObject("ClickLayer", typeof(RectTransform), typeof(Image));
                    clickLayer.transform.SetParent(row.transform, false);
                    var clRect = clickLayer.GetComponent<RectTransform>();
                    clRect.anchorMin = Vector2.zero;
                    clRect.anchorMax = Vector2.one;
                    clRect.offsetMin = Vector2.zero;
                    clRect.offsetMax = Vector2.zero;
                    var clImg = clickLayer.GetComponent<Image>();
                    clImg.color = new Color(1f, 1f, 1f, 0.01f);
                    clImg.raycastTarget = true;
                    var headBtn = row.AddComponent<Button>();
                    headBtn.targetGraphic = clImg;
                    headBtn.onClick.AddListener(() =>
                    {
                        try { _dynastyState[stateKey] = true; ZhuluRebuild.Persistence.ZhuluPreferences.SetFold("mandate:" + stateKey, true); RefreshHistoryPanel(); } catch { }
                    });
                    float headH = Mathf.Max(16f, head.preferredHeight);
                    rowRect.sizeDelta = new Vector2(_innerWidth, 12f + headH + 8f);
                    continue;
                }
                // 展开态头部区域可点击收起（仅头部 22px，不挡诸汗行）
                GameObject clickLayer2 = new GameObject("ClickLayer2", typeof(RectTransform), typeof(Image));
                clickLayer2.transform.SetParent(row.transform, false);
                var cl2Rect = clickLayer2.GetComponent<RectTransform>();
                cl2Rect.anchorMin = new Vector2(0f, 1f);
                cl2Rect.anchorMax = new Vector2(1f, 1f);
                cl2Rect.pivot = new Vector2(0.5f, 1f);
                cl2Rect.anchoredPosition = Vector2.zero;
                cl2Rect.sizeDelta = new Vector2(0f, 22f);
                var cl2Img = clickLayer2.GetComponent<Image>();
                cl2Img.color = new Color(1f, 1f, 1f, 0.01f);
                cl2Img.raycastTarget = true;
                var headBtn2 = row.AddComponent<Button>();
                headBtn2.targetGraphic = cl2Img;
                headBtn2.onClick.AddListener(() =>
                {
                        try { _dynastyState[stateKey] = false; ZhuluRebuild.Persistence.ZhuluPreferences.SetFold("mandate:" + stateKey, false); RefreshHistoryPanel(); } catch { }
                });

                // 诸汗行（两行制：行1 主行 / 行2 即汗位快照·功过书——超宽换行+保守行高，绝不溢出底卡）
                float y = -26f;
                int idx = 1;
                foreach (var k in c.Khans)
                {
                    string reign = k.EndYear == -1 ? $"{k.StartYear}~今" : $"{k.StartYear}~{k.EndYear}年";
                    // 阶段3H-9：盖棺徽章——汗号+等第（东方谥庙徽章同位）；旧档无史评回落 ◆评价短徽章
                    string slewTag = k.SlewHolder == 1 ? " <color=#FFD700>⚔覆灭王庭</color>" : "";
                    string verdictStr;
                    if (k.EndYear == -1) verdictStr = "";
                    else if (!string.IsNullOrEmpty(k.KhanTitle) && !string.IsNullOrEmpty(k.RankLabel))
                        verdictStr = $" <color={UI.ZhuluUIStyle.Hex.Imperial}>◆{k.KhanTitle}{k.RankLabel}</color>";
                    else if (!string.IsNullOrEmpty(k.RankLabel))
                        verdictStr = $" <color=#6ca3fd>◆{k.RankLabel}</color>";
                    else verdictStr = string.IsNullOrEmpty(k.Verdict) ? "" : $" <color={UI.ZhuluUIStyle.Hex.Imperial}>◆{k.Verdict}</color>";
                    Text r1 = CreateText(row.transform,
                        $"第{idx}汗 <b>{k.Name}</b> <color=#e0554a>{k.Era}</color> <color=#ffd8d9>{reign}</color>{slewTag}{(k.EndYear == -1 ? " <color={UI.ZhuluUIStyle.Hex.Positive}>（当今可汗）</color>" : verdictStr)}", 9);
                    r1.alignment = TextAnchor.MiddleLeft;
                    r1.horizontalOverflow = HorizontalWrapMode.Wrap;
                    r1.verticalOverflow = VerticalWrapMode.Overflow;
                    var r1Rect = r1.GetComponent<RectTransform>();
                    r1Rect.anchorMin = new Vector2(0, 1);
                    r1Rect.anchorMax = new Vector2(0, 1);
                    r1Rect.pivot = new Vector2(0, 1);
                    r1Rect.anchoredPosition = new Vector2(8, y);
                    r1Rect.sizeDelta = new Vector2(_innerWidth - 16, 18f);
                    r1.raycastTarget = false;
                    float used = Mathf.Max(14f, r1.preferredHeight) + 2f;

                    Text snapText = CreateText(row.transform, BuildKhanSnapshotLine(k), 8);
                    snapText.alignment = TextAnchor.MiddleLeft;
                    snapText.horizontalOverflow = HorizontalWrapMode.Wrap;
                    snapText.verticalOverflow = VerticalWrapMode.Overflow;
                    var sRectR = snapText.GetComponent<RectTransform>();
                    sRectR.anchorMin = new Vector2(0, 1);
                    sRectR.anchorMax = new Vector2(0, 1);
                    sRectR.pivot = new Vector2(0, 1);
                    sRectR.anchoredPosition = new Vector2(8, y - used);
                    sRectR.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                    snapText.raycastTarget = false;
                    used += MeasureWrappedHeight(snapText, _innerWidth - 16, 13f) + 3f;

                    // 阶段3H-9：行3 盖棺史评（「评语」+ 等第徽章——东方盖棺史评同位）
                    if (k.EndYear > 0 && !string.IsNullOrEmpty(k.VerdictText))
                    {
                        string slewBadge = k.SlewHolder == 1 ? " <color=#FFD700>【覆灭王庭】</color>" : "";
                        string rankBadge = slewBadge + (string.IsNullOrEmpty(k.RankLabel) ? "" : $" <color={UI.ZhuluUIStyle.Hex.Imperial}>【{k.RankLabel}】</color>");
                        Text vText = CreateText(row.transform, $"「<color=#b8a87a>{k.VerdictText}</color>」{rankBadge}", 8);
                        vText.alignment = TextAnchor.MiddleLeft;
                        vText.horizontalOverflow = HorizontalWrapMode.Wrap;
                        vText.verticalOverflow = VerticalWrapMode.Overflow;
                        var vRect = vText.GetComponent<RectTransform>();
                        vRect.anchorMin = new Vector2(0, 1);
                        vRect.anchorMax = new Vector2(0, 1);
                        vRect.pivot = new Vector2(0, 1);
                        vRect.anchoredPosition = new Vector2(8, y - used);
                        vRect.sizeDelta = new Vector2(_innerWidth - 16, 14f);
                        vText.raycastTarget = false;
                        used += MeasureWrappedHeight(vText, _innerWidth - 16, 13f);
                    }

                    y -= used + 7f;
                    idx++;
                }
                rowRect.sizeDelta = new Vector2(_innerWidth, -y + 8f);
            }
        }

        /// <summary>阶段3H-5：可汗快照/功过行——在位=即汗位快照（帐落·控弦·部众·府库·附庸·天眷·稳定）；
        /// 盖棺=功过增量（帐落/控弦/部众%/府库/天眷/稳/附庸）+ 征伐施政数。未采集(-1)自动省略，史册未载整行提示。</summary>
        private static string BuildKhanSnapshotLine(NomadicMandateManager.KhanRecord k)
        {
            var sb = new System.Text.StringBuilder("<color=#6a6458>");
            if (k.StartCities < 0 && k.EndCities < 0)
            {
                sb.Append("帐落控弦之数，早岁史册未载</color>");
                return sb.ToString();
            }
            if (k.EndYear == -1)
            {
                sb.Append($"即汗位（{k.StartYear}）：{k.StartCities}帐落 · 控弦 {k.StartArmy}");
                if (k.StartPop > 0) sb.Append($" · 部众 {k.StartPop}");
                if (k.StartTreasury >= 0) sb.Append($" · 府库 {k.StartTreasury}");
                if (k.StartVassals >= 0) sb.Append($" · 附庸 {k.StartVassals}");
                if (k.StartMandate >= 0f) sb.Append($" · 天眷 {k.StartMandate:F0}");
                if (k.StartStability >= 0f) sb.Append($" · 稳定 {k.StartStability:F0}");
                sb.Append("</color>");
                return sb.ToString();
            }
            if (k.StartCities < 0 || k.EndCities < 0)
            {
                sb.Append("帐落控弦之数，早岁史册未载</color>");
                return sb.ToString();
            }
            sb.Append("帐落").Append(DeltaStr(k.EndCities - k.StartCities));
            sb.Append(" · 控弦").Append(DeltaStr(k.EndArmy - k.StartArmy));
            if (k.StartPop > 0)
            {
                int pct = (int)Mathf.Round((k.EndPop - k.StartPop) * 100f / Mathf.Max(1, k.StartPop));
                sb.Append(" · 部众").Append(DeltaStr(pct, "%"));
            }
            if (k.StartTreasury >= 0 && k.EndTreasury >= 0) sb.Append(" · 府库").Append(DeltaStr((int)(k.EndTreasury - k.StartTreasury)));
            if (k.StartMandate >= 0f && k.EndMandate >= 0f) sb.Append(" · 天眷").Append(DeltaStr((int)(k.EndMandate - k.StartMandate)));
            if (k.StartStability >= 0f && k.EndStability >= 0f) sb.Append(" · 稳").Append(DeltaStr((int)(k.EndStability - k.StartStability)));
            if (k.StartVassals >= 0 && k.EndVassals >= 0) sb.Append(" · 附庸").Append(DeltaStr(k.EndVassals - k.StartVassals));
            sb.Append("</color>");
            if (k.WarCount >= 0 || k.PolicyCount >= 0)
            {
                sb.Append("　<color=#6a7a8f>");
                if (k.WarCount >= 0) sb.Append($"征{k.WarCount}");
                if (k.PolicyCount >= 0) sb.Append($" · 政{k.PolicyCount}");
                sb.Append("</color>");
            }
            return sb.ToString();
        }

        private void SetNomadRow(Text t, float w, float y)
        {
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var r = t.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(8, y);
            r.sizeDelta = new Vector2(w - 16, 16);
        }

        private static string NomadicEndReasonText(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "天眷中断";
            if (reason.Contains("改朝")) return "改朝换代";
            if (reason.Contains("转移")) return "天眷移帐";
            return reason;
        }

        /// <summary>
        /// 手动撑起高度链：DynastyList → HistoryPanel → Content。
        /// 动态创建的嵌套 ContentSizeFitter 在 SetActive 切换后可能不触发布局重建，
        /// 导致 ScrollRect 内容高度不增长、永远滚不动（用户反馈"历代天命无法滚动"）。
        /// </summary>
        private void RecalculateHistoryHeight()
        {
            try
            {
                float totalH = 4f; // 顶部 padding
                int n = 0;
                for (int i = 0; i < _historyList.childCount; i++)
                {
                    var c = _historyList.GetChild(i);
                    if (c == null || !c.gameObject.activeSelf) continue;
                    var cr = c.GetComponent<RectTransform>();
                    if (cr != null) totalH += cr.rect.height;
                    n++;
                }
                if (n > 0) totalH += (n - 1) * 6f; // VLG spacing
                totalH += 4f; // 底部 padding

                // 1) DynastyList 高度 = 卡片总高
                var listRT = _historyList.GetComponent<RectTransform>();
                listRT.sizeDelta = new Vector2(_innerWidth, totalH);

                // 2) HistoryPanel 高度 = 返回按钮(24) + 统计条(16) + spacing(6*2) + 列表
                var panelRT = _historyPanel.GetComponent<RectTransform>();
                panelRT.sizeDelta = new Vector2(_innerWidth, 24f + 16f + 12f + totalH + 8f);

                // 3) Content 高度 = HistoryPanel 高度 + padding
                if (_contentTransform != null)
                {
                    var contentRT = _contentTransform as RectTransform;
                    if (contentRT != null)
                    {
                        contentRT.sizeDelta = new Vector2(WindowWidth - 24f, panelRT.sizeDelta.y + 8f);
                    }
                }

                // 4) 强制布局重建（fitter 链双保险）
                LayoutRebuilder.ForceRebuildLayoutImmediate(listRT);
                LayoutRebuilder.ForceRebuildLayoutImmediate(panelRT);
                if (_contentTransform != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_contentTransform as RectTransform);
                }

                // 5) 滚动回顶部（最新朝代在最上，倒序渲染）
                if (_contentScroll != null)
                {
                    _contentScroll.verticalNormalizedPosition = 1f;
                }
            }
            catch { }
        }

        // ═══════════════════════ 工具 ═══════════════════════

    }
}
