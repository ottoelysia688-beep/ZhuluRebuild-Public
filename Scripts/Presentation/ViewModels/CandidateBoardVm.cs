using System;
using System.Collections.Generic;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Presentation.ViewModels
{
    public class CandidateItemVm
    {
        public int Rank { get; set; }
        public string CandidateId { get; set; }
        public string Name { get; set; }
        public string SuitabilityGrade { get; set; }
        public int TotalScore { get; set; }
        public string MainReason { get; set; }
    }

    public class CandidateBoardVm
    {
        public string TargetOfficeId { get; set; }
        public string TargetOfficeTitle { get; set; }
        public string KingdomId { get; set; }
        public string KingdomName { get; set; }
        public List<CandidateItemVm> Candidates { get; set; } = new List<CandidateItemVm>();

        public static CandidateBoardVm CreateFromRealCitizens(string kingdomId, string kingdomName, string officeId, string officeTitle)
        {
            var board = new CandidateBoardVm
            {
                TargetOfficeId = officeId,
                TargetOfficeTitle = officeTitle,
                KingdomId = kingdomId,
                KingdomName = kingdomName
            };

            int officeIdx = OfficeManager.GetOfficeIndex(officeId);

            // 收集当前王国所有已在任官员 ID 与国君 ID，兼任限制：绝不重复提名在任官或国君！
            HashSet<string> busyPersonIds = new HashSet<string>();
            string[] offices = new[] { "prime_minister", "personnel", "revenue", "rites", "military", "justice", "works" };
            foreach (var off in offices)
            {
                var app = OfficeManager.Instance.GetAppointment(kingdomId, off);
                if (app != null && !string.IsNullOrEmpty(app.PersonId))
                {
                    busyPersonIds.Add(app.PersonId);
                }
            }

            var kFact = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomSnapshot(kingdomId);
            if (kFact != null && !string.IsNullOrEmpty(kFact.RulerId))
            {
                busyPersonIds.Add(kFact.RulerId);
            }

            var citizens = WorldBoxReadOnlyAdapter.Instance.CaptureKingdomCitizens(kingdomId);
            var scoredList = new List<(ActorFactSnapshot citizen, int score, string grade)>();

            if (citizens != null && citizens.Count > 0)
            {
                foreach (var c in citizens)
                {
                    if (c == null || string.IsNullOrEmpty(c.ActorId)) continue;

                    // 已在任官职或国君直接过滤
                    if (busyPersonIds.Contains(c.ActorId)) continue;

                    var (score, grade) = OfficialRatingManager.CalcOfficialScore(
                        c.Intelligence, c.Stewardship, c.Military, c.Diplomacy, c.Level, officeIdx, c.Age);

                    scoredList.Add((c, score, grade));
                }
            }

            scoredList.Sort((a, b) => b.score.CompareTo(a.score));

            int rank = 1;
            foreach (var item in scoredList)
            {
                var c = item.citizen;
                string cleanName = TitleSuffixPatches.StripKingSuffix(c.Name ?? "贤才");

                board.Candidates.Add(new CandidateItemVm
                {
                    Rank = rank++,
                    CandidateId = c.ActorId,
                    Name = cleanName,
                    SuitabilityGrade = item.grade,
                    TotalScore = item.score,
                    MainReason = $"匹配得分 {item.score}分 · 智{c.Intelligence} 管{c.Stewardship} 军{c.Military} 外{c.Diplomacy} (Lv{c.Level})"
                });

                if (rank > 6) break; // 最多展示前 6 名贤才
            }

            return board;
        }


    }
}
