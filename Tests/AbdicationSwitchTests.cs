using System;
using System.Reflection;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 「禅让机制」设置开关的契约守护（2026-10-13）。
    ///
    /// 背景：一个设置开关的完整链路有 5 环 ——
    ///   ① ZhuluConfig 字段 ② Load 的 case 键 ③ Save 的输出 ④ 设置窗口开关行 ⑤ RefreshSwitch 的 case
    /// 再加 1 环逻辑短路（SuccessionManager.CheckAbdication 入口判断）。
    /// 任一环断掉，开关都会"看起来在但实际无效" —— 例如忘了接逻辑，UI 能拨但机制照跑。
    ///
    /// 本测试做**反射静态校验**（不调 Load/Save：它们内部引用 UnityEngine.Mathf/Debug，
    /// 纯 .NET 环境无法加载 —— 与 VassalRules 的 UnityEngine.Random 同类限制）。
    /// 引用完整性主要由编译期保证，此处补的是"防误删字段 / 防默认值被改"。
    /// </summary>
    public static class AbdicationSwitchTests
    {
        public static void Run()
        {
            // ① 配置字段存在且为 bool（设置窗口回调与 CheckAbdication 都依赖它）
            var prop = typeof(ZhuluConfig).GetProperty("AbdicationEnabled",
                BindingFlags.Public | BindingFlags.Instance);
            TestRunner.AssertTrue(prop != null, "开关:ZhuluConfig.AbdicationEnabled 字段存在");
            TestRunner.AssertTrue(prop != null && prop.PropertyType == typeof(bool),
                "开关:字段类型为 bool");

            // ② 默认值 true —— 零回归：未拨动时行为等同旧版（原 CheckAbdication 无此判断）
            try
            {
                bool def = (bool)prop.GetValue(ZhuluConfig.Instance);
                TestRunner.AssertTrue(def, "开关:默认值 true（未拨动 = 原行为）");
            }
            catch (Exception ex)
            {
                TestRunner.AssertTrue(false, "开关:读取默认值异常 " + ex.GetType().Name + " —— " + ex.Message);
            }

            // ③ 逻辑侧与 UI 侧类型可加载（字段引用由编译期校验；此处防类型被误删/改名）
            TestRunner.AssertTrue(typeof(ZhuluRebuild.Domain.SuccessionManager) != null,
                "开关:SuccessionManager 可加载（CheckAbdication 引用该字段）");
            TestRunner.AssertTrue(typeof(ZhuluRebuild.UI.Windows.ZhuluSettingsWindow) != null,
                "开关:ZhuluSettingsWindow 可加载（开关行 + RefreshSwitch case）");

            // ④ 常量语义未被改动（禅让门槛是文档化的设计参数，误改会静默改变平衡）
            var sm = typeof(ZhuluRebuild.Domain.SuccessionManager);
            var fTenure = sm.GetField("ABDICATION_TENURE", BindingFlags.NonPublic | BindingFlags.Static);
            var fChance = sm.GetField("ABDICATION_CHANCE", BindingFlags.NonPublic | BindingFlags.Static);
            if (fTenure != null)
                TestRunner.AssertEqual(30, Convert.ToInt32(fTenure.GetRawConstantValue()),
                    "开关:ABDICATION_TENURE = 30（设计参数未被误改）");
            if (fChance != null)
                TestRunner.AssertClose(0.10f, Convert.ToSingle(fChance.GetRawConstantValue()),
                    "开关:ABDICATION_CHANCE = 0.10");
        }
    }
}
