using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 地图王国铭牌爵位显示。只改 NameplateText 的最终显示文本，不改 Kingdom.data.name，
    /// 因而不会污染存档国名、战争名、史册或其他系统读取到的国家名称。
    /// 改文本后重调原版 setText 重排，让布局按含 badge 的完整文本重算宽度/图标间距。
    /// 天子国号与叛军国号已直接表达身份，二者不再叠加地图爵位。
    /// </summary>
    [HarmonyPatch(typeof(NameplateText), "showTextKingdom")]
    public static class NameplateTierPatch
    {
        private static readonly FieldInfo TextNameField = AccessTools.Field(typeof(NameplateText), "_text_name");
        // 原版私有 setText(string, Vector3, int pAdditionalWidth=10)——重调它让铭牌按最终文本重排（含 badge 宽度）
        private static readonly MethodInfo SetTextMethod = AccessTools.Method(
            typeof(NameplateText), "setText",
            new[] { typeof(string), typeof(Vector3), typeof(int) });

        private static readonly string[] BadgeNames = { "天子", "公", "侯", "伯", "子", "邑", "王庭", "大汗庭", "汗国", "部盟", "大部", "部族" };
        // 阶段3H-6性能：爵位徽章年度缓存——tier 只在年度结算变，避免每次铭牌刷新都做三重服务查询
        private static readonly System.Collections.Generic.Dictionary<string, string> _tierNameCache
            = new System.Collections.Generic.Dictionary<string, string>();
        private static int _tierNameCacheYear = -1;
        private static readonly string[] BadgeColors = { "#FFD45C", "#B88AF2", "#7FCB74", "#6DA7E8", "#58C7C7", "#A7ADB7" };

        // 批次16→20：爵位贴图盖章——复用原版 NameplateText._icon_special 槽位（该版本无 base_icon，
        // 只有 species/special/favorite 三个图标槽）。旧"独立Image子对象"方案因调用点位于天命国
        // 早退分支之后而从未执行，且自定义挂载不参与原版布局——两处一并根修。
        // 批次20：从"仅天命持有国显示玉玺"泛化为"全等级显示爵位贴图"（双体制共用T25~T30：
        // 王庭=玉玺、大汗庭=九鼎…部族=土城），天子/王庭特判被统一逻辑自然吸收。
        private static FieldInfo _iconSpecialField;
        private static FieldInfo _showIconSpecialField;

        /// <summary>tierId → 贴图资源键（GameResources/ui/zhulu/traits/ 下文件名去扩展名；批次7已全量注入）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> TierSpriteKeys
            = new System.Collections.Generic.Dictionary<string, string>
        {
            // 东方五等+天子
            { Domain.ZhuluTierService.TIER_TIANZI, "ui/zhulu/traits/T25_天子_玉玺" },
            { Domain.ZhuluTierService.TIER_GONG,   "ui/zhulu/traits/T26_公_九鼎" },
            { Domain.ZhuluTierService.TIER_HOU,    "ui/zhulu/traits/T27_侯_印绶" },
            { Domain.ZhuluTierService.TIER_BO,     "ui/zhulu/traits/T28_伯_铜爵" },
            { Domain.ZhuluTierService.TIER_ZI,     "ui/zhulu/traits/T29_子_玄圭" },
            { Domain.ZhuluTierService.TIER_NAN,    "ui/zhulu/traits/T30_邑_土城" },
            // 游牧六级（与东方共用贴图文件——特质注册本就同键）
            { Domain.NomadicTierService.TIER_WANGTING,  "ui/zhulu/traits/T25_天子_玉玺" },
            { Domain.NomadicTierService.TIER_DAHANTING, "ui/zhulu/traits/T26_公_九鼎" },
            { Domain.NomadicTierService.TIER_HANGUO,    "ui/zhulu/traits/T27_侯_印绶" },
            { Domain.NomadicTierService.TIER_BUMENG,    "ui/zhulu/traits/T28_伯_铜爵" },
            { Domain.NomadicTierService.TIER_DABU,      "ui/zhulu/traits/T29_子_玄圭" },
            { Domain.NomadicTierService.TIER_BUZU,      "ui/zhulu/traits/T30_邑_土城" },
        };

        /// <summary>tierId → 已加载 Sprite 缓存（含加载失败占位，避免每次刷新重试 IO）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _tierSpriteCache
            = new System.Collections.Generic.Dictionary<string, Sprite>();

        // 批次19：盖章追踪——铭牌 GameObject 是对象池复用的！
        // 只有曾被我们盖过章的铭牌才会在失去 tier 后做一次清理（sprite置空+隐藏）；
        // 从未碰过的铭牌完全不干预——原版对该槽位的任何用途完整保留。
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NameplateText, object> _stampedByUs
            = new System.Runtime.CompilerServices.ConditionalWeakTable<NameplateText, object>();

        /// <summary>有爵位的国在铭牌图标槽显示对应爵位贴图；无 tier 或开关关闭时回收曾盖章的复用铭牌。必须在各早退分支前调用。</summary>
        private static void UpdateTierIcon(NameplateText np, string tierId)
        {
            try
            {
                bool enabled = ZhuluConfig.Instance.MapTierIconsEnabled;
                bool wantStamp = enabled && !string.IsNullOrEmpty(tierId) && TierSpriteKeys.ContainsKey(tierId);

                if (!wantStamp)
                {
                    // 开关关闭 / 无爵位：只回收自己的章，从未碰过的铭牌不碰
                    if (!_stampedByUs.TryGetValue(np, out var _)) return;
                    _stampedByUs.Remove(np);
                    if (_iconSpecialField.GetValue(np) is Image staleImg) staleImg.sprite = null;
                    _showIconSpecialField.SetValue(np, false);
                    return;
                }

                if (_iconSpecialField == null)
                    _iconSpecialField = AccessTools.Field(typeof(NameplateText), "_icon_special");
                if (_showIconSpecialField == null)
                    _showIconSpecialField = AccessTools.Field(typeof(NameplateText), "_show_icon_special");
                if (_iconSpecialField == null || _showIconSpecialField == null) return;

                if (!_tierSpriteCache.TryGetValue(tierId, out Sprite sp))
                {
                    string key = TierSpriteKeys[tierId];
                    sp = SpriteTextureLoader.getSprite(key);
                    _tierSpriteCache[tierId] = sp; // 失败也缓存，防每帧重试IO；换档后按新tierId重新尝试
                    if (sp == null)
                        ZhuluContext.Instance.LogRateLimited("TierIconMissing",
                            $"[TierIcon] 贴图加载失败 key={key} (限频)", 60f);
                }
                var img = _iconSpecialField.GetValue(np) as Image;
                if (img == null || sp == null)
                {
                    if (img == null)
                        ZhuluContext.Instance.LogRateLimited("TierIconSlotMissing",
                            "[TierIcon] 图标槽Image为null (限频)", 60f);
                    return;
                }
                img.sprite = sp;
                img.color = Color.white;
                img.preserveAspect = true;
                img.raycastTarget = false;
                _showIconSpecialField.SetValue(np, true);
                _stampedByUs.Remove(np);
                _stampedByUs.Add(np, null);
            }
            catch (Exception ex)
            {
                // 批次18-B3：图标链路静默失败=铭牌永远无贴图且无从排查——限频留痕
                try { ZhuluContext.Instance.LogRateLimited("TierIcon", "[TierIcon] 爵位贴图异常(限频): " + ex.Message, 60f); } catch { }
            }
        }

        [HarmonyPostfix]
        private static void ShowTextKingdom_Postfix(NameplateText __instance, Kingdom __0, Vector2 __1)
        {
            try
            {
                if (__instance == null || __0 == null) return;

                // 批次20：tier 解析提到最前——爵位贴图与文字徽章共用同一按年缓存；
                // 图标必须先于"天子/叛军不加文字"早退分支执行，否则目标国永远走不到图标逻辑。
                int cacheYear = ZhuluTime.CurrentYear;
                if (cacheYear != _tierNameCacheYear) { _tierNameCache.Clear(); _tierNameCacheYear = cacheYear; }
                string npKey = __0.id.ToString();
                string tierName;
                string tierId;
                if (_tierNameCache.TryGetValue(npKey, out string pair))
                {
                    int sep = pair.IndexOf('|');
                    tierId = pair.Substring(0, sep);
                    tierName = pair.Substring(sep + 1);
                }
                else
                {
                    bool nomadic = KingdomCivilizationService.Instance.IsNomadic(__0);
                    tierId = nomadic ? NomadicTierService.GetKingdomTierId(__0) : ZhuluTierService.GetKingdomTierId(__0);
                    tierName = nomadic ? NomadicTierService.GetTierName(tierId) : ZhuluTierService.GetTierName(tierId);
                    _tierNameCache[npKey] = tierId + "|" + tierName;
                }

                // 批次20修正：ZhuluTierService.GetKingdomTierId 设计上"天子不占五等"
                // （源注释：天命国由调用方特判）——图标同理：天命/天眷持有国恒显
                // 玉玺/王庭图腾(T25)，覆盖其底层国力爵位；否则天命国会错戴公侯印。
                string iconTierId = tierId;
                try
                {
                    if (PolicyManager.IsMandateHolderForKingdom(__0)
                        || Domain.NomadicMandateManager.Instance.IsMandateHolder(__0.id.ToString()))
                        iconTierId = KingdomCivilizationService.Instance.IsNomadic(__0)
                            ? Domain.NomadicTierService.TIER_WANGTING
                            : Domain.ZhuluTierService.TIER_TIANZI;
                }
                catch { }
                UpdateTierIcon(__instance, iconTierId);

                if (!ZhuluConfig.Instance.MapTierLabelsEnabled) return;
                Text label = TextNameField?.GetValue(__instance) as Text;
                if (label == null || string.IsNullOrEmpty(label.text)) return;

                // 用户定稿：天子国号与“××义军”已自带身份信息，不显示重复爵位。
                // 先清掉本补丁可能在同一个 NameplateText 对象上留下的旧 badge，
                // 防止普通国转为天子/叛军后短暂残留“（公）”等文本。
                if (PolicyManager.IsMandateHolderForKingdom(__0)
                    || ZhuluRebelService.IsRebel(__0))
                {
                    string cleaned = RemoveOwnBadge(label.text);
                    if (cleaned != label.text)
                        ApplyTextAndRelayout(__instance, label, cleaned, __1);
                    return;
                }

                string badge = BuildBadge(tierId, tierName,
                    ZhuluConfig.Instance.MapTierColorsEnabled && label.supportRichText);

                // 批次15：附庸/宗主地图标记——宗主加金⟨宗主⟩，附庸加灰⟨附庸·宗主名⟩
                string vassalBadge = "";
                try
                {
                    var vassalIds = VassalManager.Instance.GetVassalIds(__0);
                    if (vassalIds.Count > 0)
                    {
                        vassalBadge = ZhuluConfig.Instance.MapTierColorsEnabled && label.supportRichText
                            ? $" <color=#FFD45C>⟨宗主·{vassalIds.Count}附⟩</color>"
                            : $" ⟨宗主·{vassalIds.Count}附⟩";
                    }
                    else
                    {
                        string suzId = Persistence.KingdomDataExt.GetStr(__0, "suzerain");
                        if (!string.IsNullOrEmpty(suzId))
                        {
                            Kingdom suz = Persistence.KingdomDataExt.FindKingdomById(suzId);
                            // 批次17：宗主已亡→不渲染附庸徽标（年度解约前的窗口期防"附庸·死国名/?"残留）
                            if (suz != null && suz.isAlive())
                            {
                                vassalBadge = ZhuluConfig.Instance.MapTierColorsEnabled && label.supportRichText
                                    ? $" <color=#8A8F9C>⟨附庸·{suz.name}⟩</color>"
                                    : $" ⟨附庸·{suz.name}⟩";
                            }
                        }
                    }
                }
                catch { }
                badge += vassalBadge;

                string storedName = __0.name ?? __0.data?.name ?? "";
                string text = RemoveOwnBadge(label.text);
                string finalText;
                if (!string.IsNullOrEmpty(storedName))
                {
                    int at = text.IndexOf(storedName, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        finalText = text.Substring(0, at) + storedName + badge + text.Substring(at + storedName.Length);
                        ApplyTextAndRelayout(__instance, label, finalText, __1);
                        return;
                    }
                }

                // 兼容无法直接找到国名的铭牌格式：只改第一行，绝不覆盖人口等后续行。
                int newline = text.IndexOf('\n');
                finalText = newline >= 0
                    ? text.Substring(0, newline) + badge + text.Substring(newline)
                    : text + badge;
                ApplyTextAndRelayout(__instance, label, finalText, __1);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[NameplateTier] 地图爵位显示异常: {ex.Message}");
            }
        }

        /// <summary>改文本后重调原版 setText——按含 badge 的最终文本重算铭牌宽度/图标间距（修左端顶图标）。</summary>
        private static void ApplyTextAndRelayout(NameplateText instance, Text label, string finalText, Vector2 pPos)
        {
            try
            {
                if (SetTextMethod != null)
                {
                    SetTextMethod.Invoke(instance, new object[] { finalText, (Vector3)pPos, 10 });
                    return;
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[NameplateTier] setText 重排失败，回退直接写入: {ex.Message}");
            }
            label.text = finalText; // 兜底（API 不兼容时）
        }

        private static string BuildBadge(string tierId, string tierName, bool richText)
        {
            string plain = $"（{tierName}）";
            if (!richText) return plain;
            return $"<color={GetTierColor(tierId)}>{plain}</color>";
        }

        private static string GetTierColor(string tierId)
        {
            switch (tierId)
            {
                case ZhuluTierService.TIER_TIANZI: return "#FFD45C";
                case NomadicTierService.TIER_WANGTING: return "#FFD45C";
                case ZhuluTierService.TIER_GONG: return "#B88AF2";
                case NomadicTierService.TIER_DAHANTING: return "#B88AF2";
                case ZhuluTierService.TIER_HOU: return "#7FCB74";
                case NomadicTierService.TIER_HANGUO: return "#7FCB74";
                case ZhuluTierService.TIER_BO: return "#6DA7E8";
                case NomadicTierService.TIER_BUMENG: return "#6DA7E8";
                case ZhuluTierService.TIER_ZI: return "#58C7C7";
                case NomadicTierService.TIER_DABU: return "#58C7C7";
                default: return "#A7ADB7";
            }
        }

        /// <summary>幂等清理逐鹿自己上一次写入的徽标；不触碰其他括号内容。</summary>
        internal static string RemoveOwnBadge(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            foreach (string name in BadgeNames)
            {
                string plain = $"（{name}）";
                foreach (string color in BadgeColors)
                    text = text.Replace($"<color={color}>{plain}</color>", "");
                text = text.Replace(plain, "");
            }
            return text;
        }
    }
}
