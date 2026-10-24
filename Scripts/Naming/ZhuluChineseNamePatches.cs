using System;
using HarmonyLib;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Naming
{
    /// <summary>
    /// 显式挂载逐鹿命名补丁。所有生成类后置均使用 Priority.First（Postfix 中最高优先级最后执行），
    /// 人物名前置同样使用 Priority.First 先记录"调用前是否为空"，从而识别并压过其他命名 Mod。
    /// </summary>
    public static class ZhuluChineseNamePatches
    {
        public static void Mount(Harmony harmony)
        {
            int mounted = 0;

            mounted += PatchPair(harmony,
                AccessTools.Method(typeof(Actor), "getName"),
                nameof(ActorNamePrefix), nameof(ActorNamePostfix),
                Priority.First, Priority.First);

            // 2026-10-23 [F1]：改名入口链（IL 反汇编实证）：
            //   UnitWindow.onNameChange → NanoObject.setName(string, bool) → NanoObject.set_name(string)
            // 其中 set_name 在 NanoObject 为 virtual、在 Actor 为 override，是所有改名路径的唯一汇聚点。
            // 旧写法 AccessTools.Method(typeof(Actor), "setName", new[] { typeof(string) }) 参数个数写错
            // （实际是 setName(String, Boolean) 两参）→ 恒返回 null → 补丁从未挂载 →
            // 玩家改名被 ActorNamePostfix 覆盖回权威名（v1.13.1 声称已修的「一改就恢复」实际未修）。
            // 开关：naming_manual_rename_capture=false 可退回旧行为（不挂载本补丁）。
            if (ZhuluConfig.Instance.NamingManualRenameCapture)
            {
                var actorNameSetter = AccessTools.PropertySetter(typeof(Actor), "name")
                                      ?? AccessTools.Method(typeof(Actor), "set_name", new[] { typeof(string) });
                mounted += PatchPostfix(harmony, actorNameSetter, nameof(ActorSetNamePostfix));
            }
            else
            {
                ZhuluContext.Instance.LogInfo("[ChineseName] naming_manual_rename_capture=false，跳过玩家改名捕获补丁");
            }

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(City), "generateName", new[] { typeof(Actor) }),
                nameof(CityNamePostfix));

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(Kingdom), "newCivKingdom", new[] { typeof(Actor) }),
                nameof(KingdomNamePostfix));

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(Clan), "newClan", new[] { typeof(Actor), typeof(bool) }),
                nameof(ClanNamePostfix));

            // 原版 Family 与 Clan 是两套对象。0.51.x 不同小版本的 newFamily 参数略有差异，
            // 因此按方法名枚举所有重载，并由 __args 选取创建者。
            Type familyType = AccessTools.TypeByName("Family");
            if (familyType != null)
            {
                foreach (var method in AccessTools.GetDeclaredMethods(familyType))
                {
                    if (method.Name == "newFamily")
                        mounted += PatchPostfix(harmony, method, nameof(FamilyNamePostfix));
                }
            }

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(WarManager), "newWar", new[] { typeof(Kingdom), typeof(Kingdom), typeof(WarTypeAsset) }),
                nameof(WarNamePostfix));

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(WorldLog), "logAllianceCreated", new[] { typeof(Alliance) }),
                nameof(AllianceCreatedPostfix));

            // 文明命名：Culture 类没有 generateName 方法（名字由 pActor.generateName(MetaType.Culture,...) 生成），
            // 统一走 createCulture(Actor, bool) 双参后置（反编译验证 2026-08-01）
            mounted += PatchPostfix(harmony,
                FindTarget(typeof(Culture), "createCulture", new[] { typeof(Actor), typeof(bool) }),
                nameof(CultureNamePostfix));

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(Language), "generateName", new[] { typeof(Actor) }),
                nameof(LanguageNamePostfix));

            mounted += PatchPostfix(harmony,
                FindTarget(typeof(Religion), "generateName", new[] { typeof(Actor) }),
                nameof(ReligionNamePostfix));

            // 亚种命名：拦截 Subspecies.generateName 的后置（private，带 ActorAsset+WorldTile 双参）
            var subspeciesGenName = AccessTools.Method(typeof(Subspecies), "generateName",
                new[] { typeof(ActorAsset), typeof(WorldTile) });
            if (subspeciesGenName != null)
                mounted += PatchPostfix(harmony, subspeciesGenName, nameof(SubspeciesNamePostfix));

            // 书籍命名：拦截 Book.newBook 的后置
            var bookNewBook = AccessTools.Method(typeof(Book), "newBook");
            if (bookNewBook != null)
                mounted += PatchPostfix(harmony, bookNewBook, nameof(BookNamePostfix));

            // 装备命名：拦截 ItemManager.generateItem 的后置（按类型分池）
            var generateItemMethod = AccessTools.Method(typeof(ItemManager), "generateItem");
            if (generateItemMethod != null)
                mounted += PatchPostfix(harmony, generateItemMethod, nameof(ItemNamePostfix));

            // 装备重铸/蜕变命名：拦截 Item.reforge 的后置（transmute 内部也调用 reforge）
            var reforgeMethod = AccessTools.Method(typeof(Item), "reforge", new[] { typeof(int) });
            if (reforgeMethod != null)
                mounted += PatchPostfix(harmony, reforgeMethod, nameof(ItemReforgePostfix));

            mounted += PatchPair(harmony,
                AccessTools.Method(typeof(Kingdom), "getMotto"),
                nameof(KingdomMottoPrefix), nameof(KingdomMottoPostfix),
                Priority.First, Priority.First);

            mounted += PatchPair(harmony,
                AccessTools.Method(typeof(Clan), "getMotto"),
                nameof(ClanMottoPrefix), nameof(ClanMottoPostfix),
                Priority.First, Priority.First);

            mounted += PatchPair(harmony,
                AccessTools.Method(typeof(Alliance), "getMotto"),
                nameof(AllianceMottoPrefix), nameof(AllianceMottoPostfix),
                Priority.First, Priority.First);

            ZhuluContext.Instance.LogInfo($"[ChineseName] 权威命名补丁挂载完成：{mounted} 个目标方法");
        }

        private static System.Reflection.MethodBase FindTarget(Type type, string methodName, Type[] argumentTypes)
        {
            return AccessTools.Method(type, methodName, argumentTypes) ?? AccessTools.Method(type, methodName);
        }

        private static int PatchPostfix(Harmony harmony, System.Reflection.MethodBase target, string postfixName)
        {
            if (target == null)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 未找到命名目标，跳过：{postfixName}");
                return 0;
            }
            try
            {
                var postfix = new HarmonyMethod(AccessTools.Method(typeof(ZhuluChineseNamePatches), postfixName))
                {
                    priority = Priority.First
                };
                harmony.Patch(target, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[ChineseName] 命名补丁挂载失败：{postfixName}", ex);
                return 0;
            }
        }

        private static int PatchPair(Harmony harmony, System.Reflection.MethodBase target,
            string prefixName, string postfixName, int prefixPriority, int postfixPriority)
        {
            if (target == null)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 未找到命名目标，跳过：{postfixName}");
                return 0;
            }
            try
            {
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(ZhuluChineseNamePatches), prefixName))
                {
                    priority = prefixPriority
                };
                var postfix = new HarmonyMethod(AccessTools.Method(typeof(ZhuluChineseNamePatches), postfixName))
                {
                    priority = postfixPriority
                };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogError($"[ChineseName] 命名补丁挂载失败：{postfixName}", ex);
                return 0;
            }
        }

        // ───────────────────────────── 人物名 ─────────────────────────────

        private static void ActorNamePrefix(Actor __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = ZhuluChineseNameService.Instance.IsEligibleActor(__instance)
                    && ActorAccess.IsNameEmpty(__instance);
            }
            catch { }
        }

        private static void ActorNamePostfix(Actor __instance, bool __state, ref string __result)
        {
            try
            {
                // 热路径优化：只处理已被逐鹿接管的角色（字典查找 O(1)）
                string authoritative = ZhuluChineseNameService.Instance.GetTrackedName(__instance);
                if (authoritative == null)
                {
                    // 只有名字为空的新角色才走完整生成流程
                    if (__state)
                    {
                        authoritative = ZhuluChineseNameService.Instance.EnsureActorName(__instance, true);
                    }
                    if (authoritative == null) return;
                }
                // 2026-10-06：FormatActorDisplayName（君主名后缀追加）已移出——直赋权威名
                // （代码存档见 docs/移出代码存档-称号后缀.md）
                __result = authoritative;
            }
            catch { }
        }

        /// <summary>接收原版编辑器/其他显式改名调用，立即更新逐鹿持久化权威名。</summary>
        private static void ActorSetNamePostfix(Actor __instance, string __0)
        {
            try
            {
                // WindowMetaGeneric.onNameChange 会先写 custom_name=true；自动生成名不具备该标记。
                if (!ActorAccess.IsCustomName(__instance)) return;
                ZhuluChineseNameService.Instance.AcceptManualActorRename(__instance, __0);
            }
            catch (Exception ex)
            {
                ZhuluContext.Instance.LogWarning($"[ChineseName] 接收玩家人物改名失败: {ex.Message}");
            }
        }

        // ───────────────────────────── 城市/国家/氏族/战争/联盟/文化/语言/宗教 ─────────────────────────────

        private static void CityNamePostfix(City __instance, Actor __0)
        {
            try { ZhuluChineseNameService.Instance.SetCityName(__instance, __0); }
            catch { }
        }

        private static void KingdomNamePostfix(Kingdom __instance, Actor __0)
        {
            try { Domain.KingdomCivilizationService.Instance.LockNewKingdom(__instance, __0); } catch { }
            try { ZhuluChineseNameService.Instance.SetKingdomName(__instance); }
            catch { }
        }

        private static void ClanNamePostfix(Clan __instance, Actor __0)
        {
            try { ZhuluChineseNameService.Instance.SetClanName(__instance, __0); }
            catch { }
        }

        private static void FamilyNamePostfix(object __instance, object[] __args)
        {
            try
            {
                Actor founder = null;
                if (__args != null)
                    foreach (object arg in __args)
                        if (arg is Actor actor) { founder = actor; break; }
                if (founder != null)
                    ZhuluChineseNameService.Instance.SetFamilyObjectName(__instance, founder);
            }
            catch { }
        }

        private static void WarNamePostfix(War __result, Kingdom __0, Kingdom __1, WarTypeAsset __2)
        {
            try { ZhuluChineseNameService.Instance.SetWarName(__result, __0, __1, __2); }
            catch { }
        }

        private static void AllianceCreatedPostfix(Alliance __0)
        {
            try { ZhuluChineseNameService.Instance.SetAllianceName(__0); }
            catch { }
        }

        private static void CultureNamePostfix(Culture __instance, Actor __0)
        {
            try { ZhuluChineseNameService.Instance.SetCultureName(__instance, __0); }
            catch { }
        }

        private static void LanguageNamePostfix(Language __instance, Actor __0)
        {
            try { ZhuluChineseNameService.Instance.SetLanguageName(__instance, __0); }
            catch { }
        }

        private static void ReligionNamePostfix(Religion __instance, Actor __0)
        {
            try { ZhuluChineseNameService.Instance.SetReligionName(__instance, __0); }
            catch { }
        }

        private static void SubspeciesNamePostfix(Subspecies __instance)
        {
            try { ZhuluChineseNameService.Instance.SetSubspeciesName(__instance); }
            catch { }
        }

        private static void BookNamePostfix(Book __instance)
        {
            try
            {
                if (__instance == null || __instance.data == null) return;
                string name = ZhuluChineseNameService.Instance.GenerateBookName();
                if (!string.IsNullOrEmpty(name)) __instance.data.name = name;
            }
            catch { }
        }

        /// <summary>装备生成后置：给装备设置中文名（按装备类型分池 + 真实材质）。
        /// 注意：游戏在加传奇 mod 时会先 set 一个英文传奇名（generateModsFor→checkModName→setName），
        /// 所以不能因"已有名字"就跳过——只有已经是中文名（含汉字）的才保留。
        /// 2026-08-18 重构：① 材质优先 ItemData.material（实例真实材质，重铸后正确——
        /// 原 asset.material 是资产默认值，"银剑叫金锤"即此错位）② 武器按 id/subtype 细分种类
        /// ③ 品阶用 quality（R0~R3）替代 modifiers>=3 猜测。</summary>
        private static void ItemNamePostfix(Item __result)
        {
            try
            {
                if (__result == null || __result.data == null) return;
                // 已是中文名（存档/其他mod已命名）则不覆盖
                if (!string.IsNullOrEmpty(__result.data.name) && ContainsChinese(__result.data.name)) return;
                string name = BuildEquipmentName(__result);
                if (!string.IsNullOrEmpty(name)) __result.data.name = name;
            }
            catch { }
        }

        /// <summary>装备重铸后置：重铸（reforge）会调用 NameGenerator.getName("reforged_item") 重置为英文名，
        /// 重新按类型+真实材质赋中文名。transmute 内部也调用 reforge，一并覆盖。</summary>
        private static void ItemReforgePostfix(Item __instance)
        {
            try
            {
                if (__instance == null || __instance.data == null) return;
                string name = BuildEquipmentName(__instance);
                if (!string.IsNullOrEmpty(name)) __instance.data.name = name;
            }
            catch { }
        }

        /// <summary>装备中文名合成（生成/重铸共用）：类型 + 品阶 + 实例材质 + 武器细分。</summary>
        private static string BuildEquipmentName(Item item)
        {
            EquipmentType equipType = EquipmentType.Weapon;
            string materialId = null;
            string assetKey = null;
            int rarity = 0;
            try
            {
                if (item.asset != null)
                {
                    equipType = item.asset.equipment_type;
                    assetKey = (item.asset.id ?? "") + " " + (item.asset.equipment_subtype ?? "");
                    if (string.IsNullOrEmpty(materialId)) materialId = item.asset.material;
                }
            }
            catch { }
            // 实例材质优先（重铸换材后 asset.material 失真）
            try
            {
                string instMat = item.data.material;
                if (!string.IsNullOrEmpty(instMat)) materialId = instMat;
            }
            catch { }
            // 品阶：Item._quality 非公开（反射缓存，R0~R3）；读不到时 modifiers>=3 兜底为传奇
            try
            {
                if (_itemQualityField == null)
                {
                    _itemQualityField = typeof(Item).GetField("_quality",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                }
                object q = _itemQualityField != null ? _itemQualityField.GetValue(item) : null;
                if (q != null) rarity = (int)q;
            }
            catch { }
            if (rarity <= 0 && item.data.modifiers != null && item.data.modifiers.Count >= 3) rarity = 3;
            return ZhuluChineseNameService.Instance.GenerateEquipmentName(equipType, rarity, materialId, assetKey);
        }

        private static System.Reflection.FieldInfo _itemQualityField;

        /// <summary>判断字符串是否包含中文字符。</summary>
        private static bool ContainsChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            return false;
        }

        // ───────────────────────────── 格言 ─────────────────────────────

        private static void KingdomMottoPrefix(Kingdom __instance, out bool __state)
        {
            __state = __instance?.data == null || string.IsNullOrWhiteSpace(__instance.data.motto);
        }

        private static void KingdomMottoPostfix(Kingdom __instance, bool __state, ref string __result)
        {
            try
            {
                string motto = ZhuluChineseNameService.Instance.EnsureKingdomMotto(__instance, __state);
                if (__state && !string.IsNullOrWhiteSpace(motto)) __result = motto;
            }
            catch { }
        }

        private static void ClanMottoPrefix(Clan __instance, out bool __state)
        {
            __state = __instance?.data == null || string.IsNullOrWhiteSpace(__instance.data.motto);
        }

        private static void ClanMottoPostfix(Clan __instance, bool __state, ref string __result)
        {
            try
            {
                string motto = ZhuluChineseNameService.Instance.EnsureClanMotto(__instance, __state);
                if (__state && !string.IsNullOrWhiteSpace(motto)) __result = motto;
            }
            catch { }
        }

        private static void AllianceMottoPrefix(Alliance __instance, out bool __state)
        {
            __state = __instance?.data == null || string.IsNullOrWhiteSpace(__instance.data.motto);
        }

        private static void AllianceMottoPostfix(Alliance __instance, bool __state, ref string __result)
        {
            try
            {
                string motto = ZhuluChineseNameService.Instance.EnsureAllianceMotto(__instance, __state);
                if (__state && !string.IsNullOrWhiteSpace(motto)) __result = motto;
            }
            catch { }
        }
    }
}
