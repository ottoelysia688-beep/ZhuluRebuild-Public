using System;

namespace ZhuluRebuild.Presentation.Routing
{
    public enum RouteDestination
    {
        None,
        WorldOverview,
        WorldWars,
        WorldChronicle,
        Settings,
        KingdomState,
        KingdomOffices,
        KingdomPolicies,
        KingdomDynasty,
        KingdomDiplomacy,   // 邦交纵横：宣称/附庸/宗主
        KingdomKeju,        // 科举取士（独立窗口）
        KingdomFaction,     // 党争风云（独立窗口）
        KingdomLineage,     // 宗族谱牒（独立窗口，阶段3 F4）
        PersonDetail,
        Nomination,
        LlmConfig          // 大模型接入（独立窗口，2026-10-24）
    }

    public class RouteState
    {
        public RouteDestination Destination { get; set; }
        public string ActiveKingdomId { get; set; }
        public string ActivePersonId { get; set; }
        public string ActiveOfficeId { get; set; }
        public string SubTabId { get; set; } = "current";
        public long RefreshToken { get; set; }
    }

    public class UiRouter
    {
        public static UiRouter Instance { get; } = new UiRouter();

        public RouteState CurrentRoute { get; private set; } = new RouteState();

        public event Action<RouteState> OnRouteChanged;

        private UiRouter() { }

        public void NavigateTo(RouteDestination destination, string kingdomId = null, string personId = null, string subTab = "current", string officeId = null)
        {
            CurrentRoute.Destination = destination;
            CurrentRoute.ActiveKingdomId = kingdomId;
            CurrentRoute.ActivePersonId = personId;
            CurrentRoute.ActiveOfficeId = officeId;
            CurrentRoute.SubTabId = subTab;
            CurrentRoute.RefreshToken = DateTime.UtcNow.Ticks;

            Core.ZhuluContext.Instance.LogInfo($"[UiRouter] 路由导航切换 -> {destination}, KingdomId: {kingdomId ?? "none"}, OfficeId: {officeId ?? "none"}");
            OnRouteChanged?.Invoke(CurrentRoute);
        }

        public void Clear()
        {
            CurrentRoute = new RouteState();
            OnRouteChanged?.Invoke(CurrentRoute);
        }
    }
}
