namespace ZhuluRebuild.Adapters
{
    /// <summary>
    /// 【2026-10-06 移出存档】君主单位名后缀追加功能（SuffixMap / RebuildSuffixes /
    /// ActorGetNamePatch / 命名服务 FormatActorDisplayName 追加路径）已按用户拍板整体移出，
    /// 完整代码与恢复指引见 docs/移出代码存档-称号后缀.md。
    /// 本类现仅保留 StripKingSuffix：旧档单位名中" - 称号"与" (xx)"残留的剥离工具，
    /// 被命名服务/榜单/适配器多处依赖，删除会破坏旧档兼容。
    /// </summary>
    public static class TitleSuffixPatches
    {
        public static string StripKingSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int idx = name.IndexOf(" - ");
            if (idx >= 0)
            {
                return name.Substring(0, idx);
            }
            int idxParen = name.IndexOf(" (");
            if (idxParen >= 0)
            {
                return name.Substring(0, idxParen);
            }
            return name;
        }
    }
}
