using System;
using System.Collections.Generic;
using ZhuluRebuild.Core;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Presentation.ViewModels
{
    public class PolicyOptionVm
    {
        public string PolicyId { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string CategoryTitle { get; set; }
        public string OfficeTitle { get; set; }
        public string SummaryDesc { get; set; }
        public string BuffDesc { get; set; }
        public string TradeoffDesc { get; set; }
        public bool IsActive { get; set; }
        public string StatusLabel { get; set; }
        // 阶段2M：等级门槛/天命专属/持续开销（国策页丰富显示）
        public string MinTier { get; set; } = "";
        public bool RequiresMandate { get; set; } = false;
        public float UpkeepPct { get; set; } = 0f;
        public bool IsEligible { get; set; } = true;
    }

    public class CategoryCardVm
    {
        public string CategoryId { get; set; }
        public string CategoryTitle { get; set; }
        public string HostOfficerInfo { get; set; }
        public string HostHolderId { get; set; } = "";      // 主管官席任职人 Actor ID（动态头像用）
        public string HostHolderName { get; set; } = "";    // 主管官席任职人名
        public string ActivePolicyTitle { get; set; }
        public string ActivePolicyBuff { get; set; }
        public bool HasMinister { get; set; } = true;
        public List<PolicyOptionVm> Options { get; set; } = new List<PolicyOptionVm>();
    }

    public class KingdomPoliciesVm
    {
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public int ActivePolicyCount { get; set; }
        public int MaxPolicyCapacity { get; set; } = 4;
        public string SelectedCategory { get; set; } = "";
        public string SelectedCategoryView { get; set; } = "";
        public List<CategoryCardVm> CategoryCards { get; set; } = new List<CategoryCardVm>();

        public static KingdomPoliciesVm CreateFromAdapterOrMock(string kingdomId, string kingdomName, string selectedCategory = "")
        {
            try
            {
                var vm = new KingdomPoliciesVm
                {
                    KingdomId = string.IsNullOrEmpty(kingdomId) ? "k_1" : kingdomId,
                    KingdomName = string.IsNullOrEmpty(kingdomName) ? "当前王国" : kingdomName,
                    SelectedCategory = selectedCategory ?? "",
                    SelectedCategoryView = selectedCategory ?? ""
                };

                var allDefs = PolicyManager.Instance.GetAllDefinitions();
                var catMap = new Dictionary<string, CategoryCardVm>();

                // 按国家等级过滤可颁布的政策
                Kingdom policyK = KingdomDataExt.FindKingdomById(vm.KingdomId);
                bool policyIsMandate = PolicyManager.IsMandateHolderForKingdom(policyK);
                int policyRawCities = (policyK != null && policyK.cities != null) ? policyK.cities.Count : 1;
                int policyEffCities = KingdomExt.GetEffectiveCities(vm.KingdomId, policyRawCities);
                // 2026-08-18 特质权威 rank + 统一 MeetsTierGate（与 CanIssue/AI 池同口径——
                // 原城数口径致"国名公国、政策列表按伯国"；bo 精确匹配致 AI 颁的礼制政策玩家看不见）
                int currentRank = PolicyManager.GetKingdomRank(policyK, policyIsMandate);

                foreach (var def in allDefs)
                {
                    var displayDef = PolicyManager.Instance.GetDisplayDefinition(def, policyK);
                    // 天命专属政策过滤：非天命国不显示，天命国直接放行不走minTier
                    if (def.RequiresMandate)
                    {
                        if (!policyIsMandate) continue;
                    }
                    // 混合过滤（统一口径：null/zi=邑子专属，bo/hou/gong 递进）
                    else if (!PolicyManager.MeetsTierGate(def.MinTier, currentRank))
                    {
                        continue;
                    }
                    // 阶段3H-18：体制专属过滤（与 AI 池/颁布口同口径）
                    if (!PolicyManager.MeetsCivGate(def, policyK))
                    {
                        continue;
                    }
                    if (!catMap.TryGetValue(def.Category, out var catVm))
                    {
                        // 主管官席名称按国家等级动态显示
                        string dynamicOfficeTitle = OfficeManager.GetOfficeTitleForKingdom(def.OfficeId, policyK);
                        // 主管任职人（用于动态小头像）
                        string hostHolderId = "";
                        string hostHolderName = "";
                        try
                        {
                            var appoint = OfficeManager.Instance.GetAppointment(vm.KingdomId, def.OfficeId);
                            if (appoint != null)
                            {
                                hostHolderId = appoint.PersonId ?? "";
                                hostHolderName = appoint.PersonName ?? "";
                            }
                        }
                        catch { }
                        catVm = new CategoryCardVm
                        {
                            CategoryId = def.Category,
                            CategoryTitle = displayDef.CategoryTitle,
                            HostOfficerInfo = $"主管官席: {dynamicOfficeTitle}",
                            HostHolderId = hostHolderId,
                            HostHolderName = hostHolderName,
                            ActivePolicyTitle = "尚无实施政令",
                            ActivePolicyBuff = "暂无政法加成",
                            HasMinister = true,
                            Options = new List<PolicyOptionVm>()
                        };
                        catMap[def.Category] = catVm;
                    }

                    bool isActive = PolicyManager.Instance.IsPolicyActive(vm.KingdomId, def.PolicyId);
                    if (isActive)
                    {
                        catVm.ActivePolicyTitle = displayDef.Title;
                        catVm.ActivePolicyBuff = displayDef.BuffDesc;
                        vm.ActivePolicyCount++;
                    }

                    // 阶段2M：等级门槛/天命/持续开销（国策页丰富）
                    bool isTmNow = PolicyManager.IsMandateHolderForKingdom(policyK);
                    bool tierOk = def.RequiresMandate || PolicyManager.MeetsTierGate(def.MinTier, currentRank);
                    bool mandateOk = !def.RequiresMandate || isTmNow;
                    catVm.Options.Add(new PolicyOptionVm
                    {
                        PolicyId = def.PolicyId,
                        Title = displayDef.Title,
                        Category = def.Category,
                        CategoryTitle = displayDef.CategoryTitle,
                        OfficeTitle = displayDef.OfficeTitle,
                        SummaryDesc = displayDef.SummaryDesc,
                        BuffDesc = displayDef.BuffDesc,
                        TradeoffDesc = displayDef.TradeoffDesc,
                        IsActive = isActive,
                        StatusLabel = isActive ? "在效中" : "可颁行",
                        MinTier = def.MinTier ?? "",
                        RequiresMandate = def.RequiresMandate,
                        UpkeepPct = def.UpkeepPct,
                        IsEligible = tierOk && mandateOk
                    });
                }

                foreach (var kvp in catMap)
                {
                    vm.CategoryCards.Add(kvp.Value);
                }

                vm.MaxPolicyCapacity = catMap.Count;

                return vm;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[KingdomPoliciesVm] 构建 {kingdomId} 政策 ViewModel 异常，退回 Mock 数据", ex);
                return CreateMockSample(kingdomId, kingdomName, selectedCategory);
            }
        }

        /// <summary>阶段2M：当前国家等级是否 ≥ 政策要求等级（zi/bo/hou/gong）。</summary>
        private static bool TierAtLeast(string required, string kingdomId)
        {
            try
            {
                Kingdom k = KingdomDataExt.FindKingdomById(kingdomId);
                if (k == null) return false;
                int pp = ZhuluTierService.GetPowerPoints(k);
                string curTier = ZhuluTierService.GetTierId(pp);
                int rankOf(string t)
                {
                    switch (t)
                    {
                        case "zi": return 1;
                        case "bo": return 2;
                        case "hou": return 3;
                        default: return 4;
                    }
                }
                return rankOf(curTier) >= rankOf(required);
            }
            catch { return true; }
        }

        public static KingdomPoliciesVm CreateMockSample(string kingdomId = "k_1", string kingdomName = "齐国", string selectedCategory = "")
        {
            var vm = new KingdomPoliciesVm
            {
                KingdomId = string.IsNullOrEmpty(kingdomId) ? "k_1" : kingdomId,
                KingdomName = string.IsNullOrEmpty(kingdomName) ? "齐国" : kingdomName,
                ActivePolicyCount = 1,
                MaxPolicyCapacity = 4,
                SelectedCategory = selectedCategory ?? "",
                SelectedCategoryView = selectedCategory ?? ""
            };

            var catVm = new CategoryCardVm
            {
                CategoryId = "governance",
                CategoryTitle = "朝政政策",
                HostOfficerInfo = "主管官席: 丞相",
                ActivePolicyTitle = "中央集权",
                ActivePolicyBuff = "城市忠诚度 +20, 叛乱阴谋触发率 -40%",
                HasMinister = true
            };

            catVm.Options.Add(new PolicyOptionVm
            {
                PolicyId = "gov_central",
                Title = "中央集权",
                Category = "governance",
                CategoryTitle = "朝政政策",
                OfficeTitle = "丞相",
                SummaryDesc = "强干弱枝，巩固朝廷权威，削弱地方割据势力",
                BuffDesc = "城市忠诚度 +20, 叛乱阴谋触发率 -40%",
                TradeoffDesc = "边郡领土扩张速度 -15%",
                IsActive = true,
                StatusLabel = "在效中"
            });

            vm.CategoryCards.Add(catVm);

            return vm;
        }
    }
}
