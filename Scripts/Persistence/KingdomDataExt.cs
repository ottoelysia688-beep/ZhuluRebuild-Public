using System.Collections.Generic;

namespace ZhuluRebuild.Persistence
{
    /// <summary>
    /// 王国级逐鹿数据持久化助手。
    /// 直接读写 Kingdom.data.custom_data_*（游戏原生容器，随存档自动序列化）。
    /// 注意：CustomDataContainer.dict 是 internal，必须走公开的索引器与 TryGetValue。
    /// </summary>
    public static class KingdomDataExt
    {
        private const string Prefix = "zlr_";

        public static string GetStr(Kingdom k, string key, string fallback = "")
        {
            if (k?.data?.custom_data_string == null) return fallback;
            if (k.data.custom_data_string.TryGetValue(Prefix + key, out var v) && v != null) return v;
            return fallback;
        }

        public static void SetStr(Kingdom k, string key, string value)
        {
            if (k?.data == null) return;
            if (k.data.custom_data_string == null)
                k.data.custom_data_string = new CustomDataContainer<string>();
            k.data.custom_data_string[Prefix + key] = value ?? "";
        }

        public static long GetLong(Kingdom k, string key, long fallback = 0)
        {
            if (k?.data?.custom_data_long == null) return fallback;
            if (k.data.custom_data_long.TryGetValue(Prefix + key, out var v)) return v;
            return fallback;
        }

        public static void SetLong(Kingdom k, string key, long value)
        {
            if (k?.data == null) return;
            if (k.data.custom_data_long == null)
                k.data.custom_data_long = new CustomDataContainer<long>();
            k.data.custom_data_long[Prefix + key] = value;
        }

        public static float GetFloat(Kingdom k, string key, float fallback = 0f)
        {
            if (k?.data?.custom_data_float == null) return fallback;
            if (k.data.custom_data_float.TryGetValue(Prefix + key, out var v)) return v;
            return fallback;
        }

        public static void SetFloat(Kingdom k, string key, float value)
        {
            if (k?.data == null) return;
            if (k.data.custom_data_float == null)
                k.data.custom_data_float = new CustomDataContainer<float>();
            k.data.custom_data_float[Prefix + key] = value;
        }

        /// <summary>王国编号→对象 查找缓存（避免每次 FindKingdomById 都遍历全部王国）。</summary>
        private static Dictionary<string, Kingdom> _kingdomCache = null;
        private static int _cacheBuiltYear = -1;

        /// <summary>读档/新世界时调用：强制失效缓存（缓存按世界年刷新，读档瞬间年份未恢复会导致缓存过期不重建——
        /// 曾致读档后 FindKingdomById 查不到现存王国，误判"郑朝对应王国已灭亡"）。</summary>
        public static void ResetCache()
        {
            _kingdomCache = null;
            _cacheBuiltYear = -1;
        }

        /// <summary>按王国 ID 精确查找存活的 Kingdom 对象（找不到返回 null）。带缓存，年度刷新。</summary>
        public static Kingdom FindKingdomById(string kingdomId)
        {
            if (string.IsNullOrEmpty(kingdomId)) return null;
            if (World.world?.kingdoms?.list == null) return null;

            // 缓存不存在或跨年了 → 重建
            int currentYear = Core.ZhuluTime.CurrentYear;
            if (_kingdomCache == null || currentYear != _cacheBuiltYear)
            {
                RebuildKingdomCache();
                _cacheBuiltYear = currentYear;
            }

            if (_kingdomCache.TryGetValue(kingdomId, out Kingdom k))
            {
                // 确认仍存活（可能年中被灭）
                if (k != null && k.isAlive()) return k;
            }

            // 缓存未命中（可能是年中新增的王国）→ 回退遍历
            foreach (Kingdom item in World.world.kingdoms.list)
            {
                if (item == null || !item.isAlive()) continue;
                if (item.id.ToString() == kingdomId)
                {
                    _kingdomCache[kingdomId] = item; // 顺手加入缓存
                    return item;
                }
            }
            return null;
        }

        /// <summary>重建王国查找缓存（遍历一次全部王国）。</summary>
        private static void RebuildKingdomCache()
        {
            _kingdomCache = new Dictionary<string, Kingdom>();
            if (World.world?.kingdoms?.list == null) return;
            foreach (Kingdom k in World.world.kingdoms.list)
            {
                if (k == null || !k.isAlive()) continue;
                _kingdomCache[k.id.ToString()] = k;
            }
        }

        /// <summary>世界重载时清空缓存（由 Main.cs 年份回退检测调用）。</summary>
        public static void InvalidateKingdomCache()
        {
            _kingdomCache = null;
            _cacheBuiltYear = -1;
        }
    }
}
