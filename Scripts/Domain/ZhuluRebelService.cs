using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 叛军体制（阶段2V）：叛军在成为正式国家之前不具备位阶——
    /// 不参与爵位/科举/党争/百官/城主/礼治/宣称/附庸/天命；
    /// 叛乱平定转正（ZhuluYearDriverPatch.CheckRebelKingdomRename 清标记）后自动恢复全套体制。
    /// 判定依据：zlr_rebel_named 标记（叛乱命名补丁写入，转正时清除——标记生命周期即叛军生命周期）。
    /// </summary>
    public static class ZhuluRebelService
    {
        private const string KEY_REBEL = "zlr_rebel_named";

        /// <summary>是否叛军国（在叛乱中——转正后自动 false）。</summary>
        public static bool IsRebel(Kingdom k)
        {
            if (k == null || !k.isAlive()) return false;
            try { return KingdomDataExt.GetStr(k, KEY_REBEL) == "1"; }
            catch { return false; }
        }

        /// <summary>叛军首领称号（无爵位——草莽聚义）。</summary>
        public const string REBEL_LEADER_TITLE = "渠帅";

        /// <summary>叛军体制位阶名（UI 显示用——替代爵位名）。</summary>
        public const string REBEL_TIER_NAME = "叛军";

        /// <summary>叛军体制说明（总览/国势窗口显示）。</summary>
        public const string REBEL_DESC = "揭竿而起，尚未建章立制：无爵位、无百官、无科举。平定转正后方入天下体制。";
    }
}
