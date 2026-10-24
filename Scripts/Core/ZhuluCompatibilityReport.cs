using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 只读 Harmony/API 兼容性报告（2026-08-20 手册阶段 2）。
    /// 启动时检查关键 WorldBox API 是否存在、逐鹿必需补丁是否已挂载。
    /// 只观察、不改功能、不阻止启动——防"某 agent 改了没生效"。
    /// </summary>
    public static class ZhuluCompatibilityReport
    {
        private const string HarmonyId = "zhulu.rebuild.kingdom_tabs";

        private sealed class CheckResult
        {
            public string Name;
            public bool Found;
            public bool Patched;
            public bool PatchExpected;
            public string Detail;
        }

        private static readonly List<CheckResult> Results = new List<CheckResult>();

        public static void Run()
        {
            try
            {
                Results.Clear();

                // 批次10-A1：快照分区注册核对（防"注册了但没接/漏接系统"静默）
                try
                {
                    Persistence.ZhuluSaveCore.WarmSchema();
                    int n = Persistence.ZhuluSaveSchema.RegisteredCount;
                    var names = Persistence.ZhuluSaveSchema.RegisteredNames();
                    ZhuluContext.Instance.LogInfo($"[Compat] 快照分区注册: {n}/23 [{string.Join(" ", names)}]");
                    if (n != 23)
                        ZhuluContext.Instance.LogError($"[Compat] 快照分区数异常(期望23 实际{n})——存在漏接系统风险！");
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogWarning("[Compat] 快照分区核对失败: " + ex.Message);
                }

                CheckMethod("年度驱动", typeof(Kingdom), "updateAge", Type.EmptyTypes);
                CheckMethod("保存", typeof(SaveManager), "saveWorldToDirectory",
                    new[] { typeof(string), typeof(bool), typeof(bool) });
                CheckMethodWithFallback("读档", typeof(SaveManager), "loadWorld",
                    new[] { typeof(string), typeof(bool) });
                CheckMethod("新地图", typeof(MapBox), "generateNewMap", Type.EmptyTypes);
                CheckMethod("地图国家铭牌", typeof(NameplateText), "showTextKingdom",
                    new[] { typeof(Kingdom), typeof(Vector2) });
                // setText 是重排 API（3 参），逐鹿不 Patch 它——只确认存在
                CheckMethod("铭牌重排", typeof(NameplateText), "setText",
                    new[] { typeof(string), typeof(Vector3), typeof(int) }, patchExpected: false);
                CheckField("铭牌文字字段", typeof(NameplateText), "_text_name");
                CheckMethod("战争创建", typeof(WarManager), "newWar",
                    new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset) });
                CheckMethod("战争入口", typeof(DiplomacyManager), "startWar",
                    new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset), typeof(bool) });
                CheckMethod("战争结束", typeof(WarManager), "endWar",
                    new[] { typeof(War), typeof(WarWinner) });
                CheckMethod("战争更新", typeof(War), "update", Type.EmptyTypes);
                CheckMethod("人物名", typeof(Actor), "getName", Type.EmptyTypes);
                // 2026-10-23 [F1]：改名汇聚点是属性 name 的 setter（NanoObject 上 virtual、Actor 上 override）。
                // 原代码按 setName(String) 一参查找，实际是 setName(String, Boolean) 两参 → 取到 null。
                // 开关关闭时不要求挂载，避免自检误报 ERROR。
                CheckMethod("人物编辑改名", typeof(Actor), "set_name", new[] { typeof(string) },
                    patchExpected: ZhuluConfig.Instance.NamingManualRenameCapture);
                // 2026-10-23 [F2]：实际签名是 setParent1(Actor, bool) / setParent2(Actor, bool)。
                // 旧写法传 Type.EmptyTypes 必然 miss → 先打一条 HarmonyX 警告，再靠名字兜底判 PATCHED，
                // 制造「误报 + 日志噪声」。这里按真实签名精确匹配。
                CheckMethodWithFallback("子女归族·父母位1", typeof(Actor), "setParent1", new[] { typeof(Actor), typeof(bool) });
                CheckMethodWithFallback("子女归族·父母位2", typeof(Actor), "setParent2", new[] { typeof(Actor), typeof(bool) });
                CheckDynamicMethod("原版家庭创建命名", "Family", "newFamily");
                CheckMethod("换君", typeof(Kingdom), "setKing",
                    new[] { typeof(Actor), typeof(bool) });

                int found = 0, patched = 0, patchRequired = 0;
                var broken = new List<CheckResult>();
                foreach (var r2 in Results)
                {
                    if (r2.Found) found++;
                    if (r2.PatchExpected) patchRequired++;
                    if (r2.PatchExpected && r2.Patched) patched++;
                    // P0-7：MISSING（游戏更新致方法不存在）与 UNPATCHED（补丁没挂上）都算功能性缺失
                    if (!r2.Found || (r2.PatchExpected && !r2.Patched)) broken.Add(r2);
                    string state = !r2.Found ? "MISSING"
                        : r2.PatchExpected && r2.Patched ? "PATCHED"
                        : r2.PatchExpected ? "UNPATCHED"
                        : "FOUND";
                    // 2026-10-23 [F2]：正常结论（PATCHED/FOUND）降为 Info，WARN 只留给真问题。
                    // 旧行为把所有结果一律打 WARN，24 条正常结论淹没真正的缺失项。
                    if (state == "PATCHED" || state == "FOUND")
                        ZhuluContext.Instance.LogInfo($"[Compatibility] {state} {r2.Name}: {r2.Detail}");
                    else
                        ZhuluContext.Instance.LogWarning($"[Compatibility] {state} {r2.Name}: {r2.Detail}");
                }

                // ── P0-7（2026-08-23）：汇总显性化 ──
                // 旧行为只 LogWarning 一句统计，容易被淹没；现在有缺失时升 Error 级并逐项点名，
                // 杜绝"战争/存档等功能无声缺失而用户不知情"。零缺失才走常规 Warning 汇总。
                if (broken.Count > 0)
                {
                    var detail = new System.Text.StringBuilder();
                    detail.Append($"API {found}/{Results.Count} 存在；必需补丁 {patched}/{patchRequired} 已挂载");
                    foreach (var b in broken)
                        detail.Append("\n  ✗ 【").Append(b.Name).Append("】")
                              .Append(!b.Found ? "目标 API 不存在（疑似游戏版本更新）：" : "补丁未挂载：")
                              .Append(b.Detail);
                    ZhuluContext.Instance.LogError(
                        "═══ [Compatibility] 兼容性告急：以下逐鹿关键功能已失效，请检查游戏版本（锚定 build 549）或向作者反馈 ═══\n" + detail);
                }
                else
                {
                    ZhuluContext.Instance.LogWarning(
                        $"[Compatibility] API {found}/{Results.Count} 存在；必需补丁 {patched}/{patchRequired} 已挂载 —— 全部正常");
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[Compatibility] 兼容性报告异常: {ex.Message}");
            }
        }

        /// <summary>检查方法存在 + 逐鹿 Harmony 补丁是否挂载（默认要求挂载）。</summary>
        private static void CheckMethod(string name, Type type, string method, Type[] args, bool patchExpected = true)
        {
            try
            {
                var mi = AccessTools.Method(type, method, args);
                if (mi == null) { Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = "方法不存在" }); return; }
                bool patched = patchExpected && HasOurPatch(mi);
                Results.Add(new CheckResult { Name = name, Found = true, Patched = patched, PatchExpected = patchExpected, Detail = mi.DeclaringType?.Name + "." + method });
            }
            catch (Exception ex) { Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = "检查失败: " + ex.Message }); }
        }

        /// <summary>读档可能有多重载——任一存在即 Found；补丁检查取首个命中方法。</summary>
        private static void CheckMethodWithFallback(string name, Type type, string method, Type[] args, bool patchExpected = true)
        {
            try
            {
                var mi = AccessTools.Method(type, method, args);
                if (mi == null)
                {
                    bool any = false;
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name == method) { any = true; mi = m; break; }
                    }
                    if (!any) { Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = "方法不存在" }); return; }
                }
                bool patched = patchExpected && HasOurPatch(mi);
                Results.Add(new CheckResult { Name = name, Found = true, Patched = patched, PatchExpected = patchExpected, Detail = mi.DeclaringType?.Name + "." + method });
            }
            catch (Exception ex) { Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = "检查失败: " + ex.Message }); }
        }

        private static void CheckField(string name, Type type, string field)
        {
            try
            {
                var fi = AccessTools.Field(type, field);
                Results.Add(new CheckResult { Name = name, Found = fi != null, PatchExpected = false, Detail = fi != null ? "字段存在" : "字段不存在" });
            }
            catch (Exception ex) { Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = false, Detail = "检查失败: " + ex.Message }); }
        }

        /// <summary>跨 WorldBox 小版本动态检查无编译期引用的类型/方法。</summary>
        private static void CheckDynamicMethod(string name, string typeName, string methodName, bool patchExpected = true)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = $"类型 {typeName} 不存在" });
                    return;
                }
                MethodInfo method = null;
                foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (candidate.Name == methodName) { method = candidate; break; }
                }
                if (method == null)
                {
                    Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = $"方法 {typeName}.{methodName} 不存在" });
                    return;
                }
                Results.Add(new CheckResult
                {
                    Name = name,
                    Found = true,
                    Patched = patchExpected && HasOurPatch(method),
                    PatchExpected = patchExpected,
                    Detail = typeName + "." + methodName
                });
            }
            catch (Exception ex)
            {
                Results.Add(new CheckResult { Name = name, Found = false, PatchExpected = patchExpected, Detail = "检查失败: " + ex.Message });
            }
        }

        /// <summary>检查逐鹿（HarmonyId）是否真的 Patch 了该方法——防其他 Mod 误报。</summary>
        private static bool HasOurPatch(System.Reflection.MethodBase method)
        {
            if (method == null) return false;
            try
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) return false;
                if (info.Owners != null && info.Owners.Contains(HarmonyId)) return true;
                return PatchlistContain(info.Prefixes, HarmonyId)
                    || PatchlistContain(info.Postfixes, HarmonyId)
                    || PatchlistContain(info.Transpilers, HarmonyId)
                    || PatchlistContain(info.Finalizers, HarmonyId);
            }
            catch { return false; }
        }

        private static bool PatchlistContain(IReadOnlyList<Patch> list, string owner)
        {
            if (list == null) return false;
            foreach (var p in list) { if (p != null && p.owner == owner) return true; }
            return false;
        }
    }
}
