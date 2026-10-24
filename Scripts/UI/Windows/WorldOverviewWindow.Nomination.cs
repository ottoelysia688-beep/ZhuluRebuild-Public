using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Presentation.ViewModels;
using ZhuluRebuild.Presentation.Routing;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Gameplay;
using ZhuluRebuild.Persistence;


// ── 由 WorldOverviewWindow.cs 按 region 拆出的 partial 部分：5. 王国贤才提名长榜视图 ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.UI.Windows
{
    public partial class WorldOverviewWindow : MonoBehaviour
    {
        #region 5. 王国贤才提名长榜视图 (Nomination View)
        private void RenderNominationUI(string kingdomId, string officeId)
        {
            _headerBackOverride = null;   // 清残留返回覆盖
            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
            string kName = kFact != null ? kFact.KingdomName : "当前王国";

            // 按国家等级动态获取官职名（GetOfficeTitle(officeId) 无等级上下文=默认天命国制，子国/伯国显示错误）
            Kingdom nomK = ZhuluRebuild.Persistence.KingdomDataExt.FindKingdomById(kingdomId);
            string officeTitle = OfficeManager.GetOfficeTitleForKingdom(officeId, nomK);

            var boardVm = CandidateBoardVm.CreateFromRealCitizens(kingdomId, kName, officeId, officeTitle);

            UpdateHeaderTitle($"{kName} · {officeTitle} 提名榜");
            SetHeaderTabsVisible(false);
            CreateHeaderBanner($"{kName} · {officeTitle} 提名榜", "请为该官席遴选并任命最适任的本国贤才");

            if (boardVm.Candidates != null && boardVm.Candidates.Count > 0)
            {
                foreach (var c in boardVm.Candidates)
                {
                    CreateCandidateRowUI(c, boardVm, kingdomId, kName, officeId, officeTitle);
                }
            }
            else
            {
                CreateEmptyNominationHint();
            }

            CreateNominationBottomBar(kingdomId);
        }

        private void CreateCandidateRowUI(CandidateItemVm candidate, CandidateBoardVm boardVm, string kingdomId, string kingdomName, string officeId, string officeTitle)
        {
            GameObject rowObj = new GameObject($"Candidate_{candidate.CandidateId}", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            rowObj.transform.SetParent(_contentTransform, false);

            RectTransform rect = rowObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(InnerWidth, 44f);

            Image img = rowObj.GetComponent<Image>();
            img.color = new Color(0.16f, 0.18f, 0.24f, 0.95f);

            HorizontalLayoutGroup layout = rowObj.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 4, 4);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;

            GameObject infoObj = new GameObject("Info", typeof(RectTransform), typeof(VerticalLayoutGroup));
            infoObj.transform.SetParent(rowObj.transform, false);
            infoObj.GetComponent<RectTransform>().sizeDelta = new Vector2(125f, 36f);

            VerticalLayoutGroup infoLayout = infoObj.GetComponent<VerticalLayoutGroup>();
            infoLayout.spacing = 1f;

            Text t1 = CreateText(infoObj.transform, $"<b>#{candidate.Rank}</b> <color=#FFFFFF>{candidate.Name}</color> ({candidate.SuitabilityGrade})", 9);
            t1.GetComponent<RectTransform>().sizeDelta = new Vector2(125f, 16f);

            Text t2 = CreateText(infoObj.transform, $"<color=#FFCC00><b>{candidate.TotalScore}分</b></color> <color=#AAAAAA>({candidate.MainReason})</color>", 8);
            t2.GetComponent<RectTransform>().sizeDelta = new Vector2(125f, 16f);

            string cid = candidate.CandidateId;
            string cname = candidate.Name;
            Action appointAction = () => {
                int currentYear = ZhuluTime.CurrentYear;
                OfficeManager.Instance.AppointOfficer(kingdomId, kingdomName, officeId, cid, cname, currentYear, isPlayer: true);

                // 任命完成后无缝返回朝廷官制视图！
                UiRouter.Instance.NavigateTo(RouteDestination.KingdomOffices, kingdomId: kingdomId);
                RebuildUI();
            };

            GameObject appointBtn = CreateButton(rowObj.transform, "任命", new Color(0.2f, 0.6f, 0.3f), appointAction);
            appointBtn.GetComponent<RectTransform>().sizeDelta = new Vector2(50f, 24f);
        }

        private void CreateEmptyNominationHint()
        {
            GameObject hintObj = new GameObject("EmptyHint", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            hintObj.transform.SetParent(_contentTransform, false);
            hintObj.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 40f);

            Image img = hintObj.GetComponent<Image>();
            img.color = new Color(0.16f, 0.18f, 0.24f, 0.9f);

            VerticalLayoutGroup group = hintObj.GetComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(6, 6, 6, 6);
            group.childAlignment = TextAnchor.MiddleCenter;

            Text txt = CreateText(hintObj.transform, "<color=#FF8888>暂无满 16 岁适任国民，需等待人口增长</color>", 9);
            txt.alignment = TextAnchor.MiddleCenter;
        }

        private void CreateNominationBottomBar(string kingdomId)
        {
            GameObject bottomObj = new GameObject("BottomBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            bottomObj.transform.SetParent(_contentTransform, false);
            bottomObj.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, 26f);

            HorizontalLayoutGroup layout = bottomObj.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;

            Action backAction = () => {
                UiRouter.Instance.NavigateTo(RouteDestination.KingdomOffices, kingdomId: kingdomId);
                RebuildUI();
            };

            GameObject backBtn = CreateButton(bottomObj.transform, "返回官制面板", new Color(0.4f, 0.3f, 0.2f), backAction);
            backBtn.GetComponent<RectTransform>().sizeDelta = new Vector2(140f, 24f);
        }
        #endregion

    }
}
