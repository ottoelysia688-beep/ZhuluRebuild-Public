using System;
using System.Collections.Generic;
using System.IO;

namespace ZhuluRebuild.Persistence
{
    /// <summary>
    /// UI 偏好持久化（批次9d，2026-08-23）：
    /// 记忆玩家在窗口里的折叠/展开选择（谱牒段头/史册朝代段/历代天命朝代段）——
    /// 此前三处都是内存字典，重启游戏全弹回默认态。
    /// 存储位置遵守持久化铁律：mod 自身目录 .runtime/ui_prefs.json（世界无关的全局偏好，不进存档快照）。
    /// 写穿策略：每次 SetFold 即整文件重写（文件 <2KB，点击频率极低，无性能顾虑）；
    /// 读取懒加载一次；任何 IO 异常静默降级为内存态（偏好丢失不影响功能）。
    /// </summary>
    public static class ZhuluPreferences
    {
        private const string FileName = "ui_prefs.json";
        private static Dictionary<string, bool> _folds;
        private static bool _loaded;
        private static bool _ioBroken;   // 首次写失败后置位——本会话不再尝试（防日志刷屏）

        private static string FilePath()
        {
            string mod = Core.ZhuluContext.Instance != null ? Core.ZhuluContext.Instance.ModPath : null;
            if (string.IsNullOrEmpty(mod)) return null;
            return Path.Combine(mod, ".runtime", FileName);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _folds = new Dictionary<string, bool>();
            try
            {
                string p = FilePath();
                if (p != null && File.Exists(p))
                {
                    var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(p));
                    if (loaded != null) _folds = loaded;
                }
            }
            catch { }
        }

        /// <summary>读折叠态（key 约定："窗口:段key"，如 "lineage:royal" / "chronicle:era@1200" / "mandate:dynasty:X"）。</summary>
        public static bool GetFold(string key, bool defaultExpanded)
        {
            EnsureLoaded();
            return _folds.TryGetValue(key, out bool v) ? v : defaultExpanded;
        }

        /// <summary>写折叠态并立即落盘。</summary>
        public static void SetFold(string key, bool expanded)
        {
            EnsureLoaded();
            _folds[key] = expanded;
            Save();
        }

        private static void Save()
        {
            if (_ioBroken) return;
            try
            {
                string p = FilePath();
                if (p == null) return;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, Newtonsoft.Json.JsonConvert.SerializeObject(_folds, Newtonsoft.Json.Formatting.Indented));
            }
            catch
            {
                _ioBroken = true;   // 只读环境等场景——降级为内存态，不再重试
            }
        }
    }
}
