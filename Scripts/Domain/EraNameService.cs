using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 年号服务。加载 Configs/EraNames.json 年号池，为每位新皇帝分配不重复的年号。
    /// </summary>
    public class EraNameService : ISnapshotSection<EraNameService.EraState>
    {
        public static EraNameService Instance { get; } = new EraNameService();

        private readonly List<string> _pool = new List<string>();
        private readonly HashSet<string> _used = new HashSet<string>();
        private bool _loaded = false;

        private EraNameService() { }

        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = Path.Combine(ZhuluContext.Instance.ModPath, "Configs", "EraNames.json");
                if (!File.Exists(path))
                {
                    ZhuluContext.Instance.LogWarning("[EraNameService] EraNames.json 不存在，使用默认年号");
                    _pool.AddRange(new[] { "建元", "太初", "永昌", "元和", "天授", "开元", "贞观", "洪武", "永乐", "康熙" });
                    return;
                }
                string json = File.ReadAllText(path);
                // 简单解析：提取所有引号内的年号字符串
                // ⚠️ 只收长度>=2 且非分类名的条目——文件里"美谥/恶谥/美庙号/恶庙号"分类及其单字
                // （文/武/愍/殇/哀…）不是年号，混入会导致皇帝年号出现"愍"等谥号字
                var categoryNames = new HashSet<string> { "年号池", "年号1", "年号2", "年号3", "年号场1", "年号场2", "美谥", "恶谥", "美庙号", "恶庙号" };
                var lines = json.Split('\n');
                foreach (var line in lines)
                {
                    string trimmed = line.Trim().Trim(',', ' ', '\r', '\t');
                    if (trimmed.StartsWith("\"") && trimmed.EndsWith("\"") && trimmed.Length > 2)
                    {
                        string era = trimmed.Substring(1, trimmed.Length - 2);
                        if (!string.IsNullOrEmpty(era) && era.Length >= 2 && !categoryNames.Contains(era))
                            _pool.Add(era);
                    }
                }
                ZhuluContext.Instance.LogInfo($"[EraNameService] 已加载 {_pool.Count} 个年号");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[EraNameService] 加载年号池失败", ex);
                _pool.AddRange(new[] { "建元", "太初", "永昌", "元和", "天授", "开元", "贞观", "洪武", "永乐", "康熙" });
            }
        }

        /// <summary>随机取一个未使用过的年号。池耗尽时允许重复。</summary>
        public string GetRandomEraName()
        {
            EnsureLoaded();
            var available = _pool.FindAll(e => !_used.Contains(e));
            if (available.Count == 0)
            {
                // 池耗尽，重置已用列表（允许循环）
                _used.Clear();
                available = _pool;
            }
            if (available.Count == 0) return "建元";
            string chosen = available[UnityEngine.Random.Range(0, available.Count)];
            _used.Add(chosen);
            return chosen;
        }

        /// <summary>标记年号为已使用（读档恢复时调用）。</summary>
        public void MarkUsed(string eraName)
        {
            if (!string.IsNullOrEmpty(eraName)) _used.Add(eraName);
        }

        /// <summary>新世界专用：清空已用年号标记（防止新地图沿用旧世界年号去重状态）。</summary>
        public void ResetForNewWorld()
        {
            _used.Clear();
        }

    
        // ═══════════ v2 快照存档接口 ═══════════
        // ---- batch10-A1: schema v5 section ----
        public sealed class EraState { public List<string> Used = new List<string>(); }
        public string SectionName => "era_names";
        public Dictionary<string, EraState> CaptureStates()
        {
            return new Dictionary<string, EraState>
            {
                ["__global__"] = new EraState { Used = new List<string>(_used) }
            };
        }
        public void ApplyStates(Dictionary<string, EraState> data)
        {
            if (data == null || !data.TryGetValue("__global__", out var st) || st == null) return;
            _used.Clear();
            if (st.Used == null) return;
            foreach (var e in st.Used) if (!string.IsNullOrEmpty(e)) _used.Add(e);
        }

        public void MigrateFromCustomData()
        {
            // 旧档无年号池记录：从当前天命国/各国 era_name 收集已用年号
            try
            {
                if (World.world?.kingdoms?.list == null) return;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    string era = Persistence.KingdomDataExt.GetStr(k, "era_name", "");
                    if (!string.IsNullOrEmpty(era)) _used.Add(era);
                }
            }
            catch { }
        }
        public void ResetRuntime()
        {
            ResetForNewWorld(); // 新世界/串档拒绝：清空年号去重池
        }

}
}
