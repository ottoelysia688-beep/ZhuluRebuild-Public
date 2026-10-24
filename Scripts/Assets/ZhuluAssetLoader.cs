using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ZhuluRebuild.Core;

namespace ZhuluRebuild.Assets
{
    public class ZhuluAssetLoader
    {
        public static ZhuluAssetLoader Instance { get; } = new ZhuluAssetLoader();

        private readonly Dictionary<string, Sprite> _loadedSprites = new Dictionary<string, Sprite>();

        private ZhuluAssetLoader() { }

        public Sprite GetSprite(string spriteKey)
        {
            if (string.IsNullOrEmpty(spriteKey)) return null;

            if (_loadedSprites.TryGetValue(spriteKey, out var existing))
            {
                return existing;
            }

            string cleanKey = spriteKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!cleanKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                cleanKey += ".png";
            }

            string assetPath = Path.Combine(ZhuluContext.Instance.ModPath, "GameResources", "ui", "zhulu", cleanKey);
            if (!File.Exists(assetPath))
            {
                // 全局子目录递归检索
                string fileName = Path.GetFileName(cleanKey);
                string baseDir = Path.Combine(ZhuluContext.Instance.ModPath, "GameResources", "ui", "zhulu");
                if (Directory.Exists(baseDir))
                {
                    string[] foundFiles = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories);
                    if (foundFiles.Length > 0)
                    {
                        assetPath = foundFiles[0];
                    }
                }
            }

            if (File.Exists(assetPath))
            {
                try
                {
                    byte[] fileData = File.ReadAllBytes(assetPath);
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Point;
                    if (UnityEngine.ImageConversion.LoadImage(tex, fileData))
                    {
                        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        _loadedSprites[spriteKey] = sprite;
                        ZhuluContext.Instance.LogInfo($"[ZhuluAssetLoader] 成功加载古风贴图: {spriteKey} -> {assetPath}");
                        return sprite;
                    }
                }
                catch (Exception ex)
                {
                    ZhuluContext.Instance.LogError($"[ZhuluAssetLoader] 加载贴图资产 {spriteKey} 失败", ex);
                }
            }

            ZhuluContext.Instance.LogWarning($"[ZhuluAssetLoader] 未找到贴图 {spriteKey}，使用原生贴图兜底");
            return null;
        }
    }
}
