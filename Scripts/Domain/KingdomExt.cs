using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 王国逐鹿扩展数据。所有状态（权力值/辉煌标记/基准名/国祚/宗主）一律持久化到
    /// Kingdom.data.custom_data_*，随游戏存档自动保存；不再使用任何内存字典。
    /// </summary>
    public static class KingdomExt
    {
        private const string KEY_POWER = "power";
        private const string KEY_BASENAME = "basename";
        private const string KEY_EVERKING = "everking";
        private const string KEY_EVEREMP = "everemp";
        private const string KEY_BIRTHYEAR = "birthyear";
        private const string KEY_OVERLORD = "overlord";
        private const string KEY_MANUAL_NAME = "manual_name";

        public static string StripSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            string result = name;
            string[] suffixes = { "-宗主国", "-附庸", "大汗庭", "王庭", "汗国", "部盟", "大部", "部族", "公国", "侯国", "伯国", "子国", "邑国", "帝国", "天朝", "朝", "国", "郡", "邑" };
            bool changed;
            do
            {
                changed = false;
                foreach (var suf in suffixes.OrderByDescending(s => s.Length))
                {
                    if (!result.EndsWith(suf) || result.Length <= suf.Length) continue;
                    result = result.Substring(0, result.Length - suf.Length);
                    changed = true;
                    break;
                }
            }
            while (changed);
            return result;
        }

        public static string GetBaseName(string kingdomId, string currentRawName)
        {
            if (string.IsNullOrEmpty(kingdomId)) return currentRawName ?? "";
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null)
            {
                string saved = KingdomDataExt.GetStr(k, KEY_BASENAME);
                // 2026-08-03 对齐防御：命名系统权威名（zlr_cn_base_name）与基础名不一致时，
                // 说明 zlr_basename 已被污染（如"平原"被重命名为"交趾"后 basename 残留"交趾"），
                // 以权威名为准并即时修正，阻断污染继续扩散（失天命后按错误基础名显示）。
                string authoritative = KingdomDataExt.GetStr(k, "cn_base_name");
                if (!string.IsNullOrEmpty(authoritative))
                {
                    if (saved != authoritative)
                    {
                        KingdomDataExt.SetStr(k, KEY_BASENAME, authoritative);
                        return authoritative;
                    }
                    return saved;
                }
                if (!string.IsNullOrEmpty(saved)) return saved;
                string stripped = StripSuffix(currentRawName);
                KingdomDataExt.SetStr(k, KEY_BASENAME, stripped);
                return stripped;
            }
            return StripSuffix(currentRawName);
        }

        public static void SetBaseName(string kingdomId, string baseName)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null && !string.IsNullOrEmpty(baseName))
            {
                KingdomDataExt.SetStr(k, KEY_BASENAME, baseName);
            }
        }

        public static bool WasEverKing(string kingdomId, int rawCityCount)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return rawCityCount >= 6;
            if (KingdomDataExt.GetLong(k, KEY_EVERKING) == 1) return true;
            if (rawCityCount >= 6)
            {
                KingdomDataExt.SetLong(k, KEY_EVERKING, 1);
                return true;
            }
            return false;
        }

        public static bool WasEverEmperor(string kingdomId, int rawCityCount)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return rawCityCount >= 12;
            if (KingdomDataExt.GetLong(k, KEY_EVEREMP) == 1) return true;
            if (rawCityCount >= 12)
            {
                KingdomDataExt.SetLong(k, KEY_EVEREMP, 1);
                KingdomDataExt.SetLong(k, KEY_EVERKING, 1);
                return true;
            }
            return false;
        }

        public static int GetEffectiveCities(string kingdomId, int rawCityCount)
        {
            if (WasEverEmperor(kingdomId, rawCityCount)) return 12;
            if (WasEverKing(kingdomId, rawCityCount)) return 6;
            return rawCityCount;
        }

        /// <summary>
        /// 写入 Kingdom.data.name 的纯国名。爵位和附庸状态属于展示信息，不再写入存档名；
        /// 天命国仍使用已经持久化的朝代名。此入口同时把旧档的“燕伯国”迁移为“燕国”。
        /// </summary>
        public static string GetStoredName(string kingdomId, string currentRawName)
        {
            if (string.IsNullOrEmpty(kingdomId)) return currentRawName ?? "";

            Kingdom namedKingdom = KingdomDataExt.FindKingdomById(kingdomId);
            string manualName = namedKingdom != null ? KingdomDataExt.GetStr(namedKingdom, KEY_MANUAL_NAME) : "";
            if (!string.IsNullOrWhiteSpace(manualName)) return manualName;

            if (OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId))
            {
                string dynasty = OrthodoxMandateManager.Instance.GetDynastyName(kingdomId);
                if (!string.IsNullOrEmpty(dynasty))
                {
                    while (dynasty.EndsWith("国") || dynasty.EndsWith("朝"))
                        dynasty = dynasty.Substring(0, dynasty.Length - 1);
                    return dynasty + "朝";
                }
            }

            if (namedKingdom != null && NomadicMandateManager.Instance.IsMandateHolder(kingdomId))
            {
                string nomadicBase = GetBaseName(kingdomId, currentRawName);
                if (string.IsNullOrWhiteSpace(nomadicBase)) nomadicBase = StripSuffix(currentRawName);
                return (string.IsNullOrWhiteSpace(nomadicBase) ? "无名" : nomadicBase) + "王庭";
            }

            string baseName = GetBaseName(kingdomId, currentRawName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = StripSuffix(currentRawName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "无名";
            while (baseName.EndsWith("国") || baseName.EndsWith("朝"))
                baseName = baseName.Substring(0, baseName.Length - 1);
            return baseName + "国";
        }

        /// <summary>记录玩家的精确国名；实际 data.name 将保持原样，不强加“国/朝”后缀。</summary>
        public static void SetManualName(string kingdomId, string manualName)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null || string.IsNullOrWhiteSpace(manualName)) return;
            KingdomDataExt.SetStr(k, KEY_MANUAL_NAME, manualName);
            string baseName = StripSuffix(manualName);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                KingdomDataExt.SetStr(k, KEY_BASENAME, baseName);
                KingdomDataExt.SetStr(k, "cn_base_name", baseName);
            }
        }

        public static string GetManualName(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            return k == null ? "" : KingdomDataExt.GetStr(k, KEY_MANUAL_NAME);
        }

        /// <summary>
        /// 逐鹿内部 UI 使用的格式化名称（含爵位/附庸状态）。不得再写回 Kingdom.data.name；
        /// 地图爵位由 NameplateTierPatch 单独负责。
        /// </summary>
        public static string GetFormattedName(string kingdomId, string currentRawName, int rawCityCount)
        {
            Kingdom manuallyNamed = KingdomDataExt.FindKingdomById(kingdomId);
            string manualName = manuallyNamed != null ? KingdomDataExt.GetStr(manuallyNamed, KEY_MANUAL_NAME) : "";
            if (!string.IsNullOrWhiteSpace(manualName)) return manualName;

            // 正统天命国优先用王朝名（受命时固定，永不变）："平"+"朝"="平朝"。
            // 2026-08-03 修复：基础名 zlr_basename 可能被命名系统污染（平原→交趾），
            // 用基础名会导致地图名漂移（交趾朝）；王朝名与史册/换帝记录/天命窗口完全一致。
            if (OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId))
            {
                string dynasty = OrthodoxMandateManager.Instance.GetDynastyName(kingdomId);
                if (!string.IsNullOrEmpty(dynasty))
                {
                    string d = dynasty;
                    if (d.EndsWith("国")) d = d.Substring(0, d.Length - 1);
                    string expected = d + "朝";
                    // 幂等保护：已是"王朝名+朝"格式则保持（防读档瞬间天命标记未恢复时误改）
                    if (!string.IsNullOrEmpty(currentRawName) && currentRawName == expected) return currentRawName;
                    return expected;
                }
            }

            string baseName = GetBaseName(kingdomId, currentRawName);

            // 幂等保护：当前名已是"X朝"格式（天命国显示名）——即使天命标记暂时未恢复也保持不变。
            // 防读档初期名字同步把"朔朝"误改成"朔方侯国"（用户反馈：点进存档天命国会改名字）
            if (!string.IsNullOrEmpty(currentRawName) && currentRawName == baseName + "朝")
            {
                return currentRawName;
            }

            // 正统天命国用"朝"后缀（王朝名为空时的兜底）
            if (OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId))
            {
                string name = baseName;
                if (name.EndsWith("国")) name = name.Substring(0, name.Length - 1);
                return name + "朝";
            }

            // 2026-10-06 用户拍板：国名取消爵位后缀（"朔方侯国"→"朔方"，游牧同理）——
            // 原按爵位拼后缀实现（阶段2B 包1 特质权威版：君主爵位特质优先→国力点回退）
            // 移入 docs/移出代码存档-称号后缀.md。
            // 天命国"朝"与游牧天命"王庭"为政体格式本体（见上方分支），不属爵位后缀，保留。
            string normalName = baseName;
            if (normalName.EndsWith("国"))
            {
                normalName = normalName.Substring(0, normalName.Length - 1);
            }

            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null && !string.IsNullOrEmpty(KingdomDataExt.GetStr(k, KEY_OVERLORD)))
            {
                normalName += "-附庸";
            }

            return normalName;
        }

        public static int GetPowerValue(string kingdomId)
        {
            // C6（2026-10-01）：哨兵由 58 改为中性档中位 50——
            // 58 是历史巧合值，会让"从未参与演算"的国家伪装成真实数据（实测第341年存档有 2 国恰为 58）；
            // 自愈写回 50（中央主导档中位）= 惰性初始化，约 19 个调用点零改动。
            // 旧档存量键不受影响，仅缺失键收敛到 50（档位偏移 ≤1 档，实测最大值 56→50 同档）。
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return 50;
            long v = KingdomDataExt.GetLong(k, KEY_POWER, -1);
            if (v < 0)
            {
                KingdomDataExt.SetLong(k, KEY_POWER, 50);
                return 50;
            }
            return (int)v;
        }

        public static void SetPowerValue(string kingdomId, int val)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null)
            {
                KingdomDataExt.SetLong(k, KEY_POWER, Mathf.Clamp(val, 0, 100));
            }
        }

        public static int GetDynastyBirthYear(string kingdomId, int currentYear)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return currentYear;
            long yr = KingdomDataExt.GetLong(k, KEY_BIRTHYEAR, 0);
            if (yr <= 0)
            {
                KingdomDataExt.SetLong(k, KEY_BIRTHYEAR, currentYear);
                return currentYear;
            }
            return (int)yr;
        }

        public static void ResetDynastyBirthYear(string kingdomId, int currentYear)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null)
            {
                KingdomDataExt.SetLong(k, KEY_BIRTHYEAR, currentYear);
            }
        }

        public static int GetDynastyAge(string kingdomId, int currentYear)
        {
            int birth = GetDynastyBirthYear(kingdomId, currentYear);
            return Mathf.Max(0, currentYear - birth);
        }

        public static void SetOverlord(string kingdomId, string overlordId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k != null)
            {
                KingdomDataExt.SetStr(k, KEY_OVERLORD, overlordId ?? "");
            }
        }

        public static bool IsVassal(string kingdomId)
        {
            Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
            if (k == null) return false;
            return !string.IsNullOrEmpty(KingdomDataExt.GetStr(k, KEY_OVERLORD));
        }
    
        // ═══════════ v2 快照存档接口 ═══════════

        // ---- batch10-A1: schema v5 section (adapter for static host) ----
        public sealed class KingdomExtState
        {
            public string BaseName;
            public long Power;
            public long BirthYear;
            public long EverKing;
            public long EverEmp;
            public string Overlord;
            public string ManualName;
        }
        public sealed class KingdomExtSection : ISnapshotSection<KingdomExtState>
        {
            public string SectionName => "kingdom_ext";
            public Dictionary<string, KingdomExtState> CaptureStates()
            {
                var d = new Dictionary<string, KingdomExtState>();
                if (World.world?.kingdoms?.list == null) return d;
                foreach (Kingdom k in World.world.kingdoms.list)
                {
                    if (k == null || !k.isAlive()) continue;
                    d[k.id.ToString()] = new KingdomExtState
                    {
                        BaseName = KingdomDataExt.GetStr(k, KEY_BASENAME, ""),
                        Power = KingdomDataExt.GetLong(k, KEY_POWER, 0),
                        BirthYear = KingdomDataExt.GetLong(k, KEY_BIRTHYEAR, 0),
                        EverKing = KingdomDataExt.GetLong(k, KEY_EVERKING, 0),
                        EverEmp = KingdomDataExt.GetLong(k, KEY_EVEREMP, 0),
                        Overlord = KingdomDataExt.GetStr(k, KEY_OVERLORD, ""),
                        ManualName = KingdomDataExt.GetStr(k, KEY_MANUAL_NAME, ""),
                    };
                }
                return d;
            }
            public void ApplyStates(Dictionary<string, KingdomExtState> data)
            {
                if (data == null) return;
                foreach (var kv in data)
                {
                    Kingdom k = KingdomDataExt.FindKingdomById(kv.Key);
                    if (k == null || !k.isAlive() || kv.Value == null) continue;
                    var st = kv.Value;
                    KingdomDataExt.SetStr(k, KEY_BASENAME, st.BaseName ?? "");
                    KingdomDataExt.SetLong(k, KEY_POWER, st.Power);
                    KingdomDataExt.SetLong(k, KEY_BIRTHYEAR, st.BirthYear);
                    KingdomDataExt.SetLong(k, KEY_EVERKING, st.EverKing);
                    KingdomDataExt.SetLong(k, KEY_EVEREMP, st.EverEmp);
                    KingdomDataExt.SetStr(k, KEY_OVERLORD, st.Overlord ?? "");
                    KingdomDataExt.SetStr(k, KEY_MANUAL_NAME, st.ManualName ?? "");
                }
            }
        }
        public static void MigrateFromCustomData() { }
        public static void ResetRuntime() { }

}
}
