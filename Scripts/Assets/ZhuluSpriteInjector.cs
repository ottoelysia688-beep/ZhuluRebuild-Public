using System;
using System.Collections.Generic;
using System.IO;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Assets
{
    /// <summary>
    /// 贴图注入统一入口：进程内按 key 去重。
    ///
    /// 背景（2026-10-23 审计）：<see cref="ZhuluRebuild.Core.ZhuluBootstrap.InjectAllUiTextures"/>
    /// 启动时已把 GameResources/ui/zhulu 下 158 张 PNG 全量注入 SpriteTextureLoader；
    /// 之后 TraitManager(步骤5) / IdentityClassifier(5a) / RoyalClanTraitManager(5e) 又逐张
    /// addSprite 同一批 key，单局产生 ~44 次「An item with the same key has already been added」
    /// 异常 + 44 次冗余磁盘读。另有 3 张窗口装饰贴图在骨架构建阶段已被 NML ResourcesPatch 命中。
    ///
    /// 语义：先到者有效。重复注入的字节完全相同，跳过不影响显示。
    /// 回滚：删除本类并恢复各调用点为直接 addSprite 即可（见 docs/修复方案-20261023.md F8）。
    /// </summary>
    public static class ZhuluSpriteInjector
    {
        private static readonly HashSet<string> Injected = new HashSet<string>();

        /// <summary>
        /// 注入一张贴图。返回 true=本次真正注入；false=已注入过 / 已被其他 mod 占用 / 文件异常。
        /// </summary>
        public static bool TryInject(string key, string filePath)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(filePath)) return false;
            if (!Injected.Add(key)) return false;
            try
            {
                SpriteTextureLoader.addSprite(key, File.ReadAllBytes(filePath));
                return true;
            }
            catch (ArgumentException)
            {
                // 键已被 NML ResourcesPatch 或前序注入占用——先到者有效，同字节幂等
                return false;
            }
            catch (Exception ex)
            {
                Injected.Remove(key);   // 真失败（文件损坏/权限），允许下次重试
                ZhuluContext.Instance.LogWarning($"[Sprite] 注入失败 {key}: {ex.Message}");
                return false;
            }
        }

        /// <summary>仅供自检/排障：本次进程已注入的 key 数量。</summary>
        public static int InjectedCount => Injected.Count;
    }
}
