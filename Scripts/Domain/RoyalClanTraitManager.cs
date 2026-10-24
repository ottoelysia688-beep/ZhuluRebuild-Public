using System;
using System.Collections.Generic;
using NeoModLoader.General;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Domain
{
    /// <summary>
    /// 王室氏族特质系统（2026-08-19 防王族断代）：
    /// 在原版氏族编辑器「和谐」栏注册两个限员特质——
    /// · 王室家族（zlr_royal_family）：成员上限 99——王族枝繁叶茂，不易断代灭亡
    /// · 普通氏族（zlr_common_family）：成员上限 18——寻常族姓人丁有度
    /// 规则：新君继位（trySetRoyalClan）即时切换 + 年度全局兜底同步；
    /// 与原版三个限员特质（最佳五人5/神圣十二人12/纵横天下1000——要么太少要么太多）互斥，
    /// 切换时自动移除原版限员特质，保证王族=99、普通=18 精确生效。
    /// 贴图复用原版：王室家族=纵横天下图、普通氏族=神圣十二人图。
    /// </summary>
    public class RoyalClanTraitManager
    {
        public static RoyalClanTraitManager Instance { get; } = new RoyalClanTraitManager();

        public const string TRAIT_ROYAL = "zlr_royal_family";   // 王室家族（上限99）
        public const string TRAIT_COMMON = "zlr_common_family"; // 普通氏族（上限18）
        public const int ROYAL_LIMIT = 99;
        public const int COMMON_LIMIT = 18;

        // 原版三个限员特质 id（互斥——切换时移除，防上限叠加：BaseStats.mergeStats 求和）
        private static readonly string[] VanillaLimitTraits = { "best_five", "divine_dozen", "we_are_legion" };

        private bool _registered;
        /// <summary>特质是否已注册（补丁/年度同步的前置条件）。</summary>
        public bool Registered => _registered;

        /// <summary>注册两个氏族特质到 AssetManager.clan_traits（Main.Init 5c 步）。</summary>
        public void Init()
        {
            if (_registered) return;
            try
            {
                // ── 贴图注入（2026-08-20 专属贴图 T31/T32：先注入缓存再注册特质——path_icon 即缓存键）──
                InjectClanTraitIcons();

                // ── 王室家族（上限99；专属贴图：宗庙谱）──
                // 反编译实锤：BaseTrait.base_stats_meta 字段无初始化器——new 时为 null，
                // 必须先 add()（BaseTraitLibrary.add 内部补 new BaseStats()）再写 meta（原版 ClanTraitLibrary 同模式）。
                // 2026-08-20 修复：原"add 前写 meta"直接 NRE 被 catch 吞掉——特质从未注册成功（用户实测和谐栏只有原版三个的根因之一）。
                var royal = new ClanTrait
                {
                    id = TRAIT_ROYAL,
                    group_id = "harmony",
                    path_icon = "ui/zhulu/traits/T31_王室家族_宗庙谱", // 2026-08-20 专属贴图（原复用纵横天下）
                    spawn_random_trait_allowed = false,   // 系统特质：不随机生成（只随王族身份切换）
                    needs_to_be_explored = false,          // 运行时注册绕过探索解锁——编辑器直接可用（人物特质同款修复）
                    rarity = Rarity.R0_Normal              // 常规边框（非成就奇观）
                };
                AssetManager.clan_traits.add(royal);
                royal.base_stats_meta["limit_clan_members"] = ROYAL_LIMIT;

                // ── 普通氏族（上限18；专属贴图：简牍）──
                var common = new ClanTrait
                {
                    id = TRAIT_COMMON,
                    group_id = "harmony",
                    path_icon = "ui/zhulu/traits/T32_普通氏族_简牍", // 2026-08-20 专属贴图（原复用神圣十二人）
                    spawn_random_trait_allowed = false,
                    needs_to_be_explored = false,
                    rarity = Rarity.R0_Normal
                };
                AssetManager.clan_traits.add(common);
                common.base_stats_meta["limit_clan_members"] = COMMON_LIMIT;

                // ── 互斥链（双向）：王室↔普通；两特质↔原版三限员特质 ──
                BuildOpposites(royal, common);

                // ── 语言条目（getTranslatedName/getTranslatedDescription 走 clan_trait_{id} 键）──
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_ROYAL, "王室家族");
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_ROYAL + "_info", "王族苗裔，成员上限九十九人");
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_ROYAL + "_info_2", "天潢贵胄，宗枝繁茂");
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_COMMON, "普通氏族");
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_COMMON + "_info", "寻常族姓，成员上限十八人");
                LM.AddToCurrentLocale("clan_trait_" + TRAIT_COMMON + "_info_2", "人丁有度，代代相传");

                _registered = true;
                ZhuluContext.Instance.LogInfo($"[RoyalClanTrait] 氏族特质注册完成: 王室家族(上限{ROYAL_LIMIT})/普通氏族(上限{COMMON_LIMIT})——和谐栏");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError("[RoyalClanTrait] 特质注册异常", ex);
            }
        }

        /// <summary>
        /// 2026-08-20 注入氏族特质专属贴图（T31 宗庙谱 / T32 简牍）到游戏贴图缓存。
        /// 氏族特质走 ClanTraitButton → getSprite(path_icon)——自定义路径不在游戏资源包内，
        /// 必须注入 SpriteTextureLoader 缓存（键=path_icon）才能显示；与 Actor 特质注入同机制。
        /// </summary>
        private void InjectClanTraitIcons()
        {
            try
            {
                var icons = new Dictionary<string, string>
                {
                    { TRAIT_ROYAL, "traits/T31_王室家族_宗庙谱" },
                    { TRAIT_COMMON, "traits/T32_普通氏族_简牍" }
                };
                foreach (var kv in icons)
                {
                    try
                    {
                        string rel = kv.Value.Replace('/', System.IO.Path.DirectorySeparatorChar);
                        string file = System.IO.Path.Combine(ZhuluContext.Instance.ModPath, "GameResources", "ui", "zhulu", rel + ".png");
                        if (!System.IO.File.Exists(file))
                        {
                            ZhuluContext.Instance.LogWarning($"[RoyalClanTrait] 氏族贴图缺失: {file}");
                            continue;
                        }
                        // 2026-10-23 [F8]：全量注入已注册过同名 key → 去重跳过
                        if (ZhuluRebuild.Assets.ZhuluSpriteInjector.TryInject("ui/zhulu/" + kv.Value, file))
                            ZhuluContext.Instance.LogInfo($"[RoyalClanTrait] 注入氏族贴图: {kv.Key} -> ui/zhulu/{kv.Value}");
                    }
                    catch (Exception ex)
                    {
                        ZhuluContext.Instance.LogWarning($"[RoyalClanTrait] 氏族贴图注入异常: {kv.Key}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[RoyalClanTrait] 氏族贴图注入整体异常: {ex.Message}");
            }
        }

        /// <summary>互斥链双向注册（opposite_list + opposite_traits 双写——绕过 post_init 时机）。</summary>
        private void BuildOpposites(ClanTrait royal, ClanTrait common)
        {
            try
            {
                var vanilla = new List<ClanTrait>();
                foreach (var vid in VanillaLimitTraits)
                {
                    var v = AssetManager.clan_traits.get(vid);
                    if (v != null) vanilla.Add(v);
                }

                SetOpposites(royal, vanilla, common);
                SetOpposites(common, vanilla, royal);

                // 原版特质反向注册我方两特质（保证互斥对称——checkOppositeErrors 口径）
                foreach (var v in vanilla)
                {
                    try
                    {
                        v.opposite_list = v.opposite_list ?? new List<string>();
                        if (!v.opposite_list.Contains(royal.id)) v.opposite_list.Add(royal.id);
                        if (!v.opposite_list.Contains(common.id)) v.opposite_list.Add(common.id);
                        v.opposite_traits = v.opposite_traits ?? new HashSet<ClanTrait>();
                        v.opposite_traits.Add(royal);
                        v.opposite_traits.Add(common);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[RoyalClanTrait] 互斥链设置异常: {ex.Message}");
            }
        }

        private static void SetOpposites(ClanTrait trait, List<ClanTrait> vanilla, ClanTrait zlrOther)
        {
            trait.opposite_list = new List<string>();
            trait.opposite_traits = new HashSet<ClanTrait>();
            foreach (var v in vanilla)
            {
                trait.opposite_list.Add(v.id);
                trait.opposite_traits.Add(v);
            }
            trait.opposite_list.Add(zlrOther.id);
            trait.opposite_traits.Add(zlrOther);
        }

        /// <summary>
        /// 年度全局兜底同步（YearDriver 全局阶段）：王族→王室家族特质，其余→普通氏族特质。
        /// 覆盖：新创建氏族、旧存档补挂、王族更替漏网、玩家手动改特质后的自动纠正。
        /// </summary>
        public void SyncAllClans()
        {
            if (!_registered) return;
            try
            {
                // ① 王室氏族集合：各国现任君主的氏族（kingdoms 一轮 O(K)）
                var royalIds = new HashSet<long>();
                if (World.world?.kingdoms?.list != null)
                {
                    foreach (Kingdom k in World.world.kingdoms.list)
                    {
                        try
                        {
                            if (k == null || !k.isAlive() || !k.hasKing()) continue;
                            var kc = k.getKingClan();
                            if (kc != null && kc.isAlive()) royalIds.Add(kc.id);
                        }
                        catch { }
                    }
                }

                // ② 全氏族一轮同步 O(C)
                int switched = 0;
                var clans = World.world?.clans?.list;
                if (clans == null) return;
                foreach (Clan clan in clans)
                {
                    try
                    {
                        if (clan == null || !clan.isAlive()) continue;
                        if (ApplyClanTrait(clan, royalIds.Contains(clan.id))) switched++;
                    }
                    catch { }
                }
                if (switched > 0)
                    ZhuluContext.Instance.LogInfo($"[RoyalClanTrait] 年度同步: {switched} 个氏族特质切换（在朝王族 {royalIds.Count} 支）");
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[RoyalClanTrait] SyncAllClans 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 按身份切换单个氏族的特质（即时生效——recalcBaseStats 重算 limit_clan_members）。
        /// 返回是否发生变更（供调用方统计/播报）。
        /// </summary>
        public bool ApplyClanTrait(Clan clan, bool royal)
        {
            try
            {
                if (clan == null || !clan.isAlive()) return false;
                string want = royal ? TRAIT_ROYAL : TRAIT_COMMON;
                string other = royal ? TRAIT_COMMON : TRAIT_ROYAL;
                bool changed = false;

                // 移除另一个逐鹿特质 + 原版三个限员特质（防 mergeStats 求和叠加）
                try { if (clan.hasTrait(other)) { clan.removeTrait(other); changed = true; } } catch { }
                foreach (var vt in VanillaLimitTraits)
                {
                    try { if (clan.hasTrait(vt)) { clan.removeTrait(vt); changed = true; } } catch { }
                }

                // 添加目标特质（已持有则跳过）
                try
                {
                    if (!clan.hasTrait(want) && clan.addTrait(want)) changed = true;
                }
                catch { }

                return changed;
            }
            catch { return false; }
        }
    }
}
