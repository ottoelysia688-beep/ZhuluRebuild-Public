using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Presentation.ViewModels
{
    public class OfficeCardVm
    {
        public string OfficeId { get; set; } // prime_minister, revenue, rites, military, justice, works
        public string Title { get; set; }    // 丞相, 户部, 礼部, 兵部, 刑部, 工部
        public string HolderName { get; set; }
        public string HolderId { get; set; }
        public string Status { get; set; } // filled, vacant
        public string Tenure { get; set; }
        public string Suitability { get; set; }
        public string BuffDescription { get; set; }
        public string IntegrityDisplay { get; set; } // 清廉度显示
        public string PowerDisplay { get; set; }     // 权势显示
    }

    public class KingdomOfficesVm
    {
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public int FilledSeats { get; set; }
        public int VacantSeats { get; set; }
        public string AdminRiskLevel { get; set; } = "尚稳";

        public List<OfficeCardVm> OfficeCards { get; set; } = new List<OfficeCardVm>();
        public CandidateBoardVm CandidateBoard { get; set; }

        public static KingdomOfficesVm CreateFromAdapterOrMock(string kingdomId, string kingdomName)
        {
            try
            {
                var vm = new KingdomOfficesVm
                {
                    KingdomId = string.IsNullOrEmpty(kingdomId) ? "k_1" : kingdomId,
                    KingdomName = string.IsNullOrEmpty(kingdomName) ? "当前王国" : kingdomName
                };

                // 注意：ViewModel 只做只读呈现。自动选拔由主循环年度 Tick 负责，
                // 严禁在 VM 构建路径触发全图写操作（历史上每开一次官制页就全图选拔一遍）。

                // 按国家等级动态获取应设官席和对应名称
                Kingdom k = KingdomDataExt.FindKingdomById(vm.KingdomId);
                int rawCities = (k != null && k.cities != null) ? k.cities.Count : 1;
                int effCities = KingdomExt.GetEffectiveCities(vm.KingdomId, rawCities);
                bool isMandate = PolicyManager.IsMandateHolderForKingdom(k);
                string[] officeIds = OfficeManager.GetExpectedSeatsWithMandate(k, isMandate);
                // 阶段2M：追加地方官（县丞/郡守/刺史——按爵位配置）
                try
                {
                    int pp = ZhuluTierService.GetPowerPoints(k);
                    string tierId = k != null && KingdomCivilizationService.Instance.IsNomadic(k)
                        ? NomadicTierService.GetTierId(pp) : ZhuluTierService.GetTierId(pp);
                    string[] localSeats = OfficeManager.GetLocalSeats(tierId);
                    var combined = new List<string>(officeIds);
                    foreach (var ls in localSeats) combined.Add(ls);
                    officeIds = combined.ToArray();
                }
                catch { }

                int filled = 0;
                int vacant = 0;
                var seenOffice = new HashSet<string>(); // 阶段2M：地方官去重（GetLocalSeats 返回相同 OfficeId——只显示一个）

                for (int i = 0; i < officeIds.Length; i++)
                {
                    string oid = officeIds[i];
                    // 地方官相同 id 只显示一次（刺史×4 → 一张卡）
                    if (oid.StartsWith("local_") && !seenOffice.Add(oid)) continue;
                    string title = OfficeManager.GetOfficeTitleForKingdom(oid, k);

                    var record = OfficeManager.Instance.GetAppointment(vm.KingdomId, oid);
                    if (record != null)
                    {
                        filled++;
                        int curYear = ZhuluTime.CurrentYear;
                        int protectLeft = record.ProtectionUntilYear - curYear + 1;
                        string tenure = protectLeft > 0 ? $"在任 (保护期剩: {protectLeft}年)" : "在任";

                        float corr = CorruptionManager.Instance.GetCorruption(vm.KingdomId, oid);
                        float pow = CorruptionManager.Instance.GetPower(vm.KingdomId, oid);
                        string integrity = $"清廉: {100 - corr:F0}% [{CorruptionManager.Instance.GetCorruptionGrade(corr)}]";
                        string powerStr = $"权势: {pow:F0} [{CorruptionManager.Instance.GetPowerGrade(pow)}]";
                        // 阶段2M：政绩显示（地方官描述改为政绩——用户反馈"直接显示政绩"）
                        string perf = $"政绩 {record.PerformanceScore}";
                        if (oid.StartsWith("local_"))
                            record.BuffDescription = $"辖城税收+1%、忠诚+1；{perf}"; // 阶段2M：地方官直接显示政绩

                        vm.OfficeCards.Add(new OfficeCardVm
                        {
                            OfficeId = oid,
                            Title = title,
                            HolderName = record.PersonName,
                            HolderId = record.PersonId,
                            Status = "filled",
                            Tenure = tenure,
                            Suitability = "在任履职",
                            BuffDescription = isMandate && (oid == "prime_minister" || oid == "rites")
                                ? (record.BuffDescription ?? "") + (k != null && KingdomCivilizationService.Instance.IsNomadic(k) ? "｜汗威主持" : "｜🛕礼治主持")
                                : record.BuffDescription, // 阶段2M：天命国礼治主持标记
                            IntegrityDisplay = integrity,
                            PowerDisplay = powerStr
                        });
                    }
                    else
                    {
                        vacant++;
                        vm.OfficeCards.Add(new OfficeCardVm
                        {
                            OfficeId = oid,
                            Title = title,
                            HolderName = "空缺",
                            HolderId = "",
                            Status = "vacant",
                            Tenure = "待选拔",
                            Suitability = "点击提名",
                            BuffDescription = "【官席空缺】无政务加成"
                        });
                    }
                }

                vm.FilledSeats = filled;
                vm.VacantSeats = vacant;
                vm.AdminRiskLevel = vacant >= 3 ? "⚠️ 紊乱" : (vacant > 0 ? "尚稳" : "清平");

                return vm;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[KingdomOfficesVm] 构建 {kingdomId} 官制 ViewModel 异常，退回 Mock 数据", ex);
                return CreateMockSample(kingdomId, kingdomName);
            }
        }

        public static KingdomOfficesVm CreateMockSample(string kingdomId = "k_1", string kingdomName = "齐国")
        {
            var vm = new KingdomOfficesVm
            {
                KingdomId = string.IsNullOrEmpty(kingdomId) ? "k_1" : kingdomId,
                KingdomName = string.IsNullOrEmpty(kingdomName) ? "齐国" : kingdomName,
                FilledSeats = 0,
                VacantSeats = 7,
                AdminRiskLevel = "⚠️ 紊乱"
            };

            string[] officeIds = new[] { "prime_minister", "personnel", "revenue", "rites", "military", "justice", "works" };
            string[] officeTitles = new[] { "丞相", "吏部", "户部", "礼部", "兵部", "刑部", "工部" };

            for (int i = 0; i < officeIds.Length; i++)
            {
                vm.OfficeCards.Add(new OfficeCardVm
                {
                    OfficeId = officeIds[i],
                    Title = officeTitles[i],
                    HolderName = "空缺",
                    HolderId = "",
                    Status = "vacant",
                    Tenure = "待选拔",
                    Suitability = "点击提名",
                    BuffDescription = "【官席空缺】无政务加成"
                });
            }

            return vm;
        }
    }
}
