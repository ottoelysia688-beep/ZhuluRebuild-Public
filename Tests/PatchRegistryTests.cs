using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 补丁治理层契约守护。
    ///
    /// 背景：F1（改名补丁因参数个数写错而静默失效）潜伏整整一年，根因不是技术难度，
    /// 而是"没有一处能回答：一共装了多少补丁、哪个失败了"。
    /// 引入 ZhuluPatchRegistry 后，用这三条用例把"可审计"钉死：
    ///   1. 每个 [HarmonyPatch] 类都必须在用途表登记（新增补丁漏登记 → 立刻失败）
    ///   2. 补丁 Id 不得重复（否则屏蔽清单/日志会指向错误对象）
    ///   3. 登记项总数 = 属性式补丁数 + 手工挂载数（防止手工项被悄悄删掉）
    /// </summary>
    public static class PatchRegistryTests
    {
        public static void Run()
        {
            bool origDebug = ZhuluContext.Instance.DebugLogging;
            ZhuluContext.Instance.DebugLogging = false;   // 测试进程无 Unity，Debug.Log 不可用
            try
            {
                RunCore();
            }
            finally
            {
                ZhuluContext.Instance.DebugLogging = origDebug;
            }
        }

        private static void RunCore()
        {
            var entries = ZhuluPatchRegistry.BuildEntries();

            // 1. 用途登记完整性
            var undocumented = entries.Where(e => !ZhuluPatchRegistry.IsDocumented(e.Id))
                                      .Select(e => e.Id).ToList();
            TestRunner.AssertTrue(undocumented.Count == 0,
                "所有补丁均已登记用途" + (undocumented.Count == 0 ? "" : "（未登记：" + string.Join(", ", undocumented) + "）"));

            // 2. Id 唯一
            var dup = entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            TestRunner.AssertTrue(dup.Count == 0,
                "补丁 Id 唯一" + (dup.Count == 0 ? "" : "（重复：" + string.Join(", ", dup) + "）"));

            // 3. 结构完整性：属性式 + 手工项都要在，且手工项恰好是已知的 5 个
            // 2026-10-24：save_probe/succession_probe 两探针已随使命完成移除（F9 实测结案、P0-2 已落地）
            var manual = entries.Where(e => e.IsManual).Select(e => e.Id).OrderBy(x => x).ToList();
            var expectedManual = new[] { "chinese_name", "no_heir", "save_sync", "warfare", "year_driver" };
            TestRunner.AssertTrue(manual.SequenceEqual(expectedManual),
                $"手工挂载项齐备（实际：{string.Join(",", manual)}）");

            // 4. 属性式补丁数量：与程序集中实际带 [HarmonyPatch] 的类**逐一比对**（防漏扫/多扫）
            var auto = entries.Where(e => !e.IsManual).ToList();
            var actualPatchTypes = typeof(ZhuluPatchRegistry).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0
                         || t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                             .Any(m => m.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
                .Select(ZhuluPatchRegistry.IdOf)
                .OrderBy(x => x)
                .ToList();
            var registeredAuto = auto.Select(e => e.Id).OrderBy(x => x).ToList();
            TestRunner.AssertTrue(registeredAuto.SequenceEqual(actualPatchTypes),
                $"属性式补丁与程序集一致（登记 {registeredAuto.Count} / 实际 {actualPatchTypes.Count}）");

            // 5. 每条都必须有非空用途描述
            var emptyPurpose = entries.Where(e => string.IsNullOrEmpty(e.Purpose) || e.Purpose.Contains("未登记"))
                                      .Select(e => e.Id).ToList();
            TestRunner.AssertTrue(emptyPurpose.Count == 0,
                "用途描述非空" + (emptyPurpose.Count == 0 ? "" : "（缺失：" + string.Join(", ", emptyPurpose) + "）"));

            // 6. 安装计划：无屏蔽文件时全部待装
            string tempDir = Path.Combine(Path.GetTempPath(), "zhulu_patchplan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var plan0 = ZhuluPatchRegistry.BuildPlan(tempDir);
                TestRunner.AssertEqual(entries.Count, plan0.Pending.Count, "无屏蔽时全部待装");
                TestRunner.AssertTrue(plan0.Masked.Count == 0, "无屏蔽时屏蔽集为空");
                TestRunner.AssertTrue(plan0.UnknownMaskIds.Count == 0, "无屏蔽时无未知 Id");

                // 7. 屏蔽生效：注释/空行/空白必须被正确忽略
                File.WriteAllText(Path.Combine(tempDir, ZhuluPatchRegistry.MaskFileName),
                    "# 诊断用\n\n  tax_modifier  \nTaxModifierPatch\nsave_sync\n", new UTF8Encoding(false));
                var plan1 = ZhuluPatchRegistry.BuildPlan(tempDir);
                TestRunner.AssertTrue(plan1.Masked.Contains("TaxModifierPatch"), "屏蔽命中已登记 Id（大小写/空白无关）");
                TestRunner.AssertTrue(plan1.Masked.Contains("save_sync"), "屏蔽命中手工项");
                TestRunner.AssertEqual(entries.Count - 2, plan1.Pending.Count, "屏蔽后待装数减 2");
                TestRunner.AssertTrue(plan1.UnknownMaskIds.Contains("tax_modifier"), "未登记 Id 归入未知清单");

                // 8. 全部屏蔽 → 待装为空（极端用例：一键停用所有补丁）
                var allIds = string.Join("\n", entries.Select(e => e.Id));
                File.WriteAllText(Path.Combine(tempDir, ZhuluPatchRegistry.MaskFileName), allIds, new UTF8Encoding(false));
                var plan2 = ZhuluPatchRegistry.BuildPlan(tempDir);
                TestRunner.AssertEqual(0, plan2.Pending.Count, "全部屏蔽时待装为空");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
