using System;
using System.IO;
using System.Text;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 回滚保障 L2 层的契约守护：FeatureFlags 开关必须真的能被解析与写回。
    ///
    /// 为什么必须测：
    ///   本次修复的「随时回调」主要靠两个运行时开关（naming_manual_rename_capture /
    ///   modifier_cache_enabled）。若 ZhuluConfig.Load() 里漏了 case、或 Save() 里漏了输出行，
    ///   开关会**静默失效** —— 改 JSON 毫无反应，而回滚方案看起来"有"，实则没有。
    ///   ZhuluConfig.Save() 是整体重写且字段写死在代码里，这个坑尤其容易踩。
    ///
    /// 覆盖：
    ///   1. Load()：false 能被正确解析（默认值是 true，只有真读到 false 才算通过）
    ///   2. Save()：两个键必须出现在重写后的文件里
    /// </summary>
    public static class FeatureFlagRoundtripTests
    {
        public static void Run()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "zhulu_flagtest_" + Guid.NewGuid().ToString("N"));
            string cfgDir = Path.Combine(tempRoot, "Configs");
            Directory.CreateDirectory(cfgDir);
            string file = Path.Combine(cfgDir, "FeatureFlags.json");
            File.WriteAllText(file,
                "{\n  \"naming_manual_rename_capture\": false,\n  \"modifier_cache_enabled\": false\n}\n",
                new UTF8Encoding(false));

            bool origDebug = ZhuluContext.Instance.DebugLogging;
            ZhuluContext.Instance.DebugLogging = false;   // 测试进程无 Unity，Debug.Log 不可用
            try
            {
                var cfg = ZhuluConfig.Instance;
                cfg.Initialize(tempRoot);

                // 默认值均为 true，只有真正读到了 false 才算解析成功
                TestRunner.AssertTrue(!cfg.NamingManualRenameCapture, "Load 解析 naming_manual_rename_capture=false");
                TestRunner.AssertTrue(!cfg.ModifierCacheEnabled, "Load 解析 modifier_cache_enabled=false");

                // Save 是整体重写：漏写任何一个键，该开关就会在下次保存时被抹掉
                cfg.NamingManualRenameCapture = true;
                cfg.ModifierCacheEnabled = true;
                cfg.Save();
                string saved = File.ReadAllText(file);
                TestRunner.AssertTrue(saved.Contains("\"naming_manual_rename_capture\": true"),
                    "Save 写回 naming_manual_rename_capture");
                TestRunner.AssertTrue(saved.Contains("\"modifier_cache_enabled\": true"),
                    "Save 写回 modifier_cache_enabled");

                // 重新 Load 一遍，确认写回的值可被再次解析（往返闭合）
                cfg.NamingManualRenameCapture = false;
                cfg.ModifierCacheEnabled = false;
                cfg.Load();
                TestRunner.AssertTrue(cfg.NamingManualRenameCapture, "往返：重新 Load 得到 true（naming）");
                TestRunner.AssertTrue(cfg.ModifierCacheEnabled, "往返：重新 Load 得到 true（modifier）");

                // LlmFix-6：采样间隔键的往返（Save 整体重写，漏行即静默失效——同上坑）
                TestRunner.AssertTrue(saved.Contains("\"emperor_sample_interval\""),
                    "Save 写回 emperor_sample_interval");
                cfg.EmperorSampleInterval = 20;
                cfg.Save();
                cfg.EmperorSampleInterval = 0;
                cfg.Load();
                TestRunner.AssertEqual(20, cfg.EmperorSampleInterval, "采样间隔往返一致（20）");
                cfg.EmperorSampleInterval = 99;
                cfg.Load();
                TestRunner.AssertEqual(20, cfg.EmperorSampleInterval, "超范围值不覆盖已存值（钳 0~50 在写入时）");

                // LlmV2-5：步进规则——开启最低 2、2 再减=关闭、关闭加=2、封顶 50
                TestRunner.AssertEqual(0, ZhuluConfig.StepSampleInterval(2, -1), "2 再减 = 关闭");
                TestRunner.AssertEqual(0, ZhuluConfig.StepSampleInterval(0, -1), "关闭态减保持关闭");
                TestRunner.AssertEqual(2, ZhuluConfig.StepSampleInterval(0, 1), "关闭加一跳到 2");
                TestRunner.AssertEqual(4, ZhuluConfig.StepSampleInterval(3, 1), "常规加一");
                TestRunner.AssertEqual(2, ZhuluConfig.StepSampleInterval(3, -1), "常规减一");
                TestRunner.AssertEqual(50, ZhuluConfig.StepSampleInterval(50, 1), "封顶 50 不再增");
                TestRunner.AssertEqual(0, ZhuluConfig.StepSampleInterval(1, -1), "手改异常值 1 归关闭");
                TestRunner.AssertEqual(2, ZhuluConfig.StepSampleInterval(1, 1), "手改异常值 1 加到 2");
            }
            finally
            {
                // 还原默认值并尽量指回真实配置路径，避免污染后续测试
                try
                {
                    var cfg = ZhuluConfig.Instance;
                    cfg.NamingManualRenameCapture = true;
                    cfg.ModifierCacheEnabled = true;
                    string realRoot = Path.GetFullPath(Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
                    if (File.Exists(Path.Combine(realRoot, "Configs", "FeatureFlags.json")))
                        cfg.Initialize(realRoot);
                }
                catch { }
                ZhuluContext.Instance.DebugLogging = origDebug;
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }
}
