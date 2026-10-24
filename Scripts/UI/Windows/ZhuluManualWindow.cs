using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.UI;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 逐鹿玩法速览窗（神力栏第 6 按钮，纯静态内容无数据依赖）。
    /// 三章：爵位进阶 / 天命玩法 / 系统功能（金标题+分隔线，小而美）。
    /// mod 根目录《逐鹿玩法速览.txt》同源内容（# 章节标记）。
    /// P1-C2（2026-08-23）：骨架迁移至 ZhuluScrollWindowBase 首个样板窗——
    /// 本文件从 308 行减到约百行，仅保留内容构建与本地文字风格补丁。
    /// 批次7美化B2：章节改用 ZhuluWidgets.Card 程序生成圆角卡底+窗尾云纹分隔收笔
    /// （首个美化样板窗——后续窗口照此迁移）。
    /// </summary>
    public class ZhuluManualWindow : ZhuluScrollWindowBase
    {
        public static ZhuluManualWindow Instance { get; private set; }

        protected override string WindowTitle => "<b>玩法速览</b>";
        protected override float WindowWidth => 320f;
        protected override float WindowHeight => 400f;

        private readonly List<GameObject> _cards = new List<GameObject>();

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluManualWindow] 初始化完成");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy) EnsureBuilt();
        }

        /// <summary>四章速览（圆角卡片化；同源 逐鹿玩法速览.txt）。</summary>
        protected override void BuildContent()
        {
            AddSection("☰ 爵位进阶", new[]
            {
                "国力提升可晋爵（取城增口）",
                "五等爵：邑 → 子 → 伯 → 侯 → 公",
                "爵位影响官制/党争/国名位阶",
            });

            AddSection("☰ 天命玩法", new[]
            {
                "受命条件：国力≥40 · 礼治≥8 · 独立国 · 国祚≥15年",
                "礼治来源：祭祖 / 祭天 / 宗庙",
                "天命值影响稳定、忠诚、朝贡",
            });

            AddSection("☰ 游牧体制", new[]
            {
                "兽人国家固定游牧；其他智慧种族可设置未来新国家体制",
                "部族 → 大部 → 部盟 → 汗国 → 大汗庭 → 王庭",
                "长生天眷与东方天命独立；王庭之主称天可汗",
            });

            AddSection("☰ 系统功能", new[]
            {
                "科举取士 / 部族选贤 · 官制任命 · 国策颁布",
                "党争派系 · 邦交朝贡 · 地方官",
                "谥号庙号 · 年号纪元 · 谱牒世系",
                "天命史册 · 个人史册 · 家族大事",
            });

            // 云纹收笔（B1 全量注入后贴图可用；缺失仍自动回退细线）
            ZhuluWidgets.Divider(ContentRoot, InnerWidth * 0.72f, 0, true);

            var tip = CreateText(ContentRoot, $"<color={UI.ZhuluUIStyle.Hex.Muted}>逐鹿中原 · 会盟草原</color>", 9);
            tip.alignment = TextAnchor.MiddleCenter;
            LayoutLeft(tip.GetComponent<RectTransform>(), 0, 0, InnerWidth, 16);
            _cards.Add(tip.gameObject);
        }

        private void AddSection(string title, string[] lines)
        {
            float h = 30f + lines.Length * 16f;
            // 批次7美化：程序生成圆角九宫格卡底（墨底+暗金描边）替代 SolidBg 双层直角底；
            // 金标题由 Card 内建（预留状态边条位），行文本沿用本地风格补丁。
            var body = ZhuluWidgets.Card(ContentRoot, InnerWidth, h, title);
            _cards.Add(body.gameObject);

            float y = 2f;
            foreach (var line in lines)
            {
                var lt = CreateText(body, $"<color=#cfc7b8>· {line}</color>", 9);
                lt.alignment = TextAnchor.MiddleLeft;
                LayoutLeft(lt.GetComponent<RectTransform>(), 2, y, InnerWidth - 28f, 14);
                y += 16f;
            }
        }
    }
}
