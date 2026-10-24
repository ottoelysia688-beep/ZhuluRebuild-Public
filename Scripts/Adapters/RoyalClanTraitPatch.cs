using HarmonyLib;

namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 王室氏族特质即时切换（2026-08-19 防王族断代）：
    /// Kingdom.trySetRoyalClan（新君继位/王族更替，setKing 内调用）→ Postfix 立即切换新旧王族特质
    /// （新王族→王室家族99；旧王族→普通氏族18）。年度兜底同步见 ZhuluYearDriverPatch。
    /// 挂载方式：Main.Init 的 [HarmonyPatch] 自动扫描。
    /// </summary>
    [HarmonyPatch(typeof(Kingdom), "trySetRoyalClan")]
    public static class RoyalClanTraitPatch
    {
        private static long _oldClanId = -1L;

        static void Prefix(Kingdom __instance)
        {
            try { _oldClanId = __instance.data.royal_clan_id; }
            catch { _oldClanId = -1L; }
        }

        static void Postfix(Kingdom __instance)
        {
            try
            {
                var mgr = Domain.RoyalClanTraitManager.Instance;
                if (!mgr.Registered) return;
                long newId = __instance.data.royal_clan_id;
                // 新王族 → 王室家族（99）
                if (newId > 0 && newId != _oldClanId)
                {
                    var nc = World.world?.clans?.get(newId);
                    if (nc != null && nc.isAlive() && mgr.ApplyClanTrait(nc, true))
                        Core.ZhuluContext.Instance.LogInfo($"[RoyalClanTrait] 王族更替: 【{nc.data?.name}】受封王室家族特质（上限99）");
                }
                // 旧王族 → 普通氏族（18）
                if (_oldClanId > 0 && _oldClanId != newId)
                {
                    var oc = World.world?.clans?.get(_oldClanId);
                    if (oc != null && oc.isAlive() && mgr.ApplyClanTrait(oc, false))
                        Core.ZhuluContext.Instance.LogInfo($"[RoyalClanTrait] 旧族除封: 【{oc.data?.name}】转为普通氏族（上限18）");
                }
            }
            catch { }
        }
    }
}
