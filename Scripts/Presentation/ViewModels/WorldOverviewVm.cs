using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Presentation.ViewModels
{
    public class RankItemVm
    {
        public int Rank { get; set; }
        public string TargetId { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string ScoreValue { get; set; }
        public string ScoreLabel { get; set; }
        public bool IsKingdom { get; set; }
    }

    public class SelectedDetailVm
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string StageOrRole { get; set; }
        public string FactSummary { get; set; }
        public List<string> Highlights { get; set; } = new List<string>();
    }

    public class WorldOverviewVm
    {
        public string ActiveTab { get; set; } = "state"; // state, monarch, talent（旧 mandate/power/war/officer 体系遗留清理）
        public int CurrentYear { get; set; } = 1;
        public int ActiveWars { get; set; } = 0;
        public int TotalKingdoms { get; set; } = 0;
        public List<RankItemVm> RankItems { get; set; } = new List<RankItemVm>();
        public SelectedDetailVm SelectedDetail { get; set; }

        public static WorldOverviewVm CreateFromAdapterOrMock(string activeTab = "state")
        {
            var vm = new WorldOverviewVm { ActiveTab = activeTab };

            try
            {
                // 2026-10-06 移出：TitleSuffixPatches.RebuildSuffixes()（君主名后缀重建）——见 docs/移出代码存档-称号后缀.md
                var worldSnap = WorldBoxReadOnlyAdapter.Instance.CaptureWorldSnapshot();
                if (worldSnap != null && worldSnap.Kingdoms != null && worldSnap.Kingdoms.Count > 0)
                {
                    vm.CurrentYear = worldSnap.CurrentYear;
                    vm.ActiveWars = worldSnap.ActiveWarsCount;
                    vm.TotalKingdoms = worldSnap.KingdomCount;

                    List<KingdomFactSnapshot> sortedList = new List<KingdomFactSnapshot>(worldSnap.Kingdoms);

                    if (activeTab == "mandate")
                    {
                        sortedList.Sort((a, b) =>
                        {
                            float valA = MandateManager.Instance.GetMandateValue(a.KingdomId);
                            float valB = MandateManager.Instance.GetMandateValue(b.KingdomId);
                            return valB.CompareTo(valA);
                        });
                    }
                    else
                    {
                        sortedList.Sort((a, b) =>
                        {
                            int valA = KingdomExt.GetPowerValue(a.KingdomId);
                            int valB = KingdomExt.GetPowerValue(b.KingdomId);
                            return valB.CompareTo(valA);
                        });
                    }

                    int rank = 1;
                    foreach (var k in sortedList)
                    {
                        var mData = MandateManager.Instance.GetMandateData(k.KingdomId);
                        int powerVal = KingdomExt.GetPowerValue(k.KingdomId);
                        int effCities = KingdomExt.GetEffectiveCities(k.KingdomId, k.CityCount);
                        Kingdom kObj = KingdomDataExt.FindKingdomById(k.KingdomId);
                        string tierName = RegimeManager.GetTierName(kObj); // 特质权威（叛军/天命感知）
                        string kingTitle = RegimeManager.GetKingTitle(kObj);
                        string formattedName = KingdomExt.GetFormattedName(k.KingdomId, k.KingdomName, k.CityCount);

                        string scoreVal = (activeTab == "mandate") ? $"{mData.Value:F0}" : $"{powerVal}";
                        string scoreLbl = (activeTab == "mandate") ? mData.StatusText : RegimeManager.GetBandName(powerVal);

                        vm.RankItems.Add(new RankItemVm
                        {
                            Rank = rank++,
                            TargetId = k.KingdomId,
                            Title = formattedName,
                            Subtitle = $"君主：{kingTitle} · {k.RulerName ?? "未知"} · {k.CityCount}城",
                            ScoreValue = scoreVal,
                            ScoreLabel = scoreLbl,
                            IsKingdom = true
                        });
                    }

                    if (vm.RankItems.Count > 0)
                    {
                        var firstK = sortedList[0];
                        var mData = MandateManager.Instance.GetMandateData(firstK.KingdomId);
                        int powerVal = KingdomExt.GetPowerValue(firstK.KingdomId);
                        int effCities = KingdomExt.GetEffectiveCities(firstK.KingdomId, firstK.CityCount);
                        Kingdom fkObj = KingdomDataExt.FindKingdomById(firstK.KingdomId);
                        bool nomadic = fkObj != null && KingdomCivilizationService.Instance.IsNomadic(fkObj);
                        string tierName = RegimeManager.GetTierName(fkObj); // 特质权威（叛军/天命感知）
                        string kingTitle = RegimeManager.GetKingTitle(fkObj);
                        string formattedName = KingdomExt.GetFormattedName(firstK.KingdomId, firstK.KingdomName, firstK.CityCount);
                        string bandName = RegimeManager.GetBandName(powerVal);

                        vm.SelectedDetail = new SelectedDetailVm
                        {
                            Id = firstK.KingdomId,
                            Name = formattedName,
                            Category = $"【{tierName}】位阶",
                            StageOrRole = $"{(nomadic ? "天眷" : "天命")} {mData.Value:F0} 分 · {mData.StatusText}",
                            FactSummary = $"人口 {firstK.TotalPopulation} · 军队 {firstK.TotalMilitary} · 领土 {firstK.CityCount} 城 · 权力 {bandName}",
                            Highlights = new List<string>
                            {
                                $"君主：【{kingTitle}】{firstK.RulerName}",
                                // 2026-10-24：原第三条重复了 StageOrRole 的「天命 N 分 · 评价」，已删。
                                // 同一卡片内不复述同一数据（见 docs/文本美化调查-20261024.md §2.5）。
                                $"中央权力 {powerVal} 分 · {bandName}"
                            }
                        };
                    }

                    return vm;
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[WorldOverviewVm] 捕捉天下快照构建 ViewModel 失败", ex);
            }

            return CreateMockSample(activeTab);
        }

        public static WorldOverviewVm CreateMockSample(string activeTab = "mandate")
        {
            var vm = new WorldOverviewVm
            {
                ActiveTab = activeTab,
                CurrentYear = 1,
                ActiveWars = 0,
                TotalKingdoms = 1
            };

            vm.RankItems.Add(new RankItemVm
            {
                Rank = 1,
                TargetId = "k_1",
                Title = "齐伯国",
                Subtitle = "君主：伯君 · 管仲 · 5城",
                ScoreValue = "88",
                ScoreLabel = "天命有归",
                IsKingdom = true
            });

            vm.SelectedDetail = new SelectedDetailVm
            {
                Id = "k_1",
                Name = "齐伯国",
                Category = "【伯国】位阶",
                StageOrRole = "天命：88分 · 天命有归",
                FactSummary = "人口 1200 · 军队 350 · 领土 5 城 · 权力 中央主导",
                Highlights = new List<string>
                {
                    "君主：【伯君】管仲",
                    "中央权力 58 分 · 中央主导"
                }
            };

            return vm;
        }
    }
}
