using System;
using ZhuluRebuild.Core;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Domain
{
    public class MandateData
    {
        public string KingdomId { get; set; }
        public float Value { get; set; } = 50f;
        public string StatusText { get; set; } = "稳定";
        public string StatusColor { get; set; } = "#90EE90";
        public int LowYears { get; set; } = 0;
        public bool Collapsed { get; set; } = false;
    }

    /// <summary>
    /// 天命管理器（已改造为正统天命的 UI 适配层）。
    /// 实际计算由 OrthodoxMandateManager 负责，本类保留接口供 ViewModel 读取。
    /// </summary>
    public class MandateManager
    {
        public static MandateManager Instance { get; } = new MandateManager();

        private MandateManager() { }

        public MandateData GetMandateData(string kingdomId)
        {
            var data = new MandateData { KingdomId = kingdomId, Value = 50f };
            if (string.IsNullOrEmpty(kingdomId)) return data;

            Kingdom kingdom = KingdomDataExt.FindKingdomById(kingdomId);
            if (kingdom != null && KingdomCivilizationService.Instance.IsNomadic(kingdom))
            {
                bool nomadicHolder = NomadicMandateManager.Instance.IsMandateHolder(kingdomId);
                if (nomadicHolder)
                {
                    data.Value = NomadicMandateManager.Instance.GetMandateValue(kingdomId);
                    data.StatusText = NomadicMandateManager.Instance.GetMandateGrade(data.Value);
                    data.StatusColor = NomadicMandateManager.Instance.GetMandateColor(data.Value);
                    data.Collapsed = data.Value < 10f;
                }
                else
                {
                    data.Value = 0f;
                    data.StatusText = "未获长生天眷";
                    data.StatusColor = "#888888";
                }
                return data;
            }

            bool isHolder = OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId);
            if (isHolder)
            {
                data.Value = OrthodoxMandateManager.Instance.GetMandateValue(kingdomId);
                data.StatusText = OrthodoxMandateManager.Instance.GetMandateGrade(data.Value);
                data.StatusColor = OrthodoxMandateManager.Instance.GetMandateColor(data.Value);
                data.Collapsed = data.Value < 10f;
            }
            else
            {
                data.Value = 0f;
                data.StatusText = "非正统";
                data.StatusColor = "#888888";
            }
            return data;
        }

        public float GetMandateValue(string kingdomId)
        {
            Kingdom kingdom = KingdomDataExt.FindKingdomById(kingdomId);
            if (kingdom != null && KingdomCivilizationService.Instance.IsNomadic(kingdom))
            {
                if (NomadicMandateManager.Instance.IsMandateHolder(kingdomId))
                    return NomadicMandateManager.Instance.GetMandateValue(kingdomId);
                return 50f;
            }
            if (OrthodoxMandateManager.Instance.IsMandateHolder(kingdomId))
                return OrthodoxMandateManager.Instance.GetMandateValue(kingdomId);
            return 50f; // 非天命国返回中性值（供稳定度计算用）
        }

        /// <summary>已废弃：天命计算由 OrthodoxMandateManager.TickAll 接管。保留接口不报错。</summary>
        public void TickAll(int currentYear) { }

        public string GetMandateStatusText(float val)
        {
            return OrthodoxMandateManager.Instance.GetMandateGrade(val);
        }

        public string GetMandateStatusColor(float val)
        {
            return OrthodoxMandateManager.Instance.GetMandateColor(val);
        }
    }
}
