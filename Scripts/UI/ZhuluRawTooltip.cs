using System.Reflection;
using UnityEngine;

namespace ZhuluRebuild.UI
{
    /// <summary>
    /// 逐鹿原生文本 tooltip（2026-08-20）：注册自定义 tooltip type "zhulu_raw"——
    /// 回调把 tip_name/tip_description 原样写入 Tooltip，不经 .Localize()/getText。
    /// 用途：皇帝名/族员名/年号等运行时动态文本。若用原版 "normal" type，
    /// 每个动态词都会触发 getText → 未注册键 → Player.log 刷 "missing text: 某某某" 噪音。
    /// 注册时机：Main.OnModLoad 末尾（AssetManager.tooltips 已初始化）。
    /// 注：Tooltip.setDescription 在 0.51.2 为非公开方法 → 缓存反射调用（静态缓存一次）。
    /// </summary>
    public static class ZhuluRawTooltip
    {
        public const string TYPE = "zhulu_raw";

        // setDescription 非公开 → 静态缓存 MethodInfo（项目约定：反射必须缓存）
        private static readonly MethodInfo SetDescriptionMethod = typeof(Tooltip).GetMethod(
            "setDescription", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>注册 tooltip 类型（幂等：重复注册静默忽略）。</summary>
        public static void Init()
        {
            try
            {
                AssetManager.tooltips.add(new TooltipAsset
                {
                    id = TYPE,
                    callback = ShowRaw
                });
            }
            catch
            {
                // 已注册（热重载二次调用）→ 忽略
            }
        }

        private static void ShowRaw(Tooltip pTooltip, string pType, TooltipData pData)
        {
            if (!string.IsNullOrEmpty(pData?.tip_name))
                pTooltip.name.text = pData.tip_name; // 原样，不本地化
            if (!string.IsNullOrEmpty(pData?.tip_description) && SetDescriptionMethod != null)
            {
                try { SetDescriptionMethod.Invoke(pTooltip, new object[] { pData.tip_description }); }
                catch { }
            }
        }

        /// <summary>
        /// 便捷挂载：给目标控件挂 TipButton 并绑定原生文本 hover 提示。
        /// （动态人名等场景一站式调用）
        /// </summary>
        public static void Attach(GameObject owner, string title, string description)
        {
            try
            {
                if (owner == null) return;
                var tip = owner.GetComponent<TipButton>();
                if (tip == null) tip = owner.AddComponent<TipButton>();
                tip.type = TYPE;
                tip.hoverAction = () =>
                {
                    Tooltip.show(owner, TYPE, new TooltipData
                    {
                        tip_name = title,
                        tip_description = description
                    });
                };
            }
            catch { }
        }
    }
}
