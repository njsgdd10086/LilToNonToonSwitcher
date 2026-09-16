// LilToNonToon Switcher
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// lilToon keeps every mask in its own texture and channel. NonToon packs them into the RGBA channels of one
    /// shared mask (<c>_SharedMask</c>), and each feature selects its channel through a "Mask Channel" property.
    /// This class reads the channel selection from the freshly converted material and packs the matching lilToon
    /// masks into a single texture.
    /// </summary>
    public static class NonToonMaskBuilder
    {
        private sealed class MaskSource
        {
            public string LilToonProperty;
            public string ModuleKeyword;        // used to locate the "<Feature> Mask Channel" property
            public string FeatureName;
            public bool RequireToggle;
            public string ToggleProperty;
        }

        public static void Bake(Material lilToonMaterial, Material nonToonMaterial, string folder, ConversionLog log)
        {
            var shader = nonToonMaterial.shader;
            if (!ShaderUtility.HasProperty(nonToonMaterial, "_SharedMask"))
            {
                foreach (var feature in UsedLilToonMasks(lilToonMaterial))
                    log.Unsupported(feature + "（当前 NonToon 版本没有共享遮罩）");
                return;
            }

            var sources = new List<MaskSource>
            {
                new MaskSource { LilToonProperty = "_AlphaMask", ModuleKeyword = "Cutoff", FeatureName = "alpha mask" },
                new MaskSource { LilToonProperty = "_BacklightColorTex", ModuleKeyword = "Backlight", FeatureName = "backlight mask",
                                 RequireToggle = true, ToggleProperty = "_UseBacklight" },
                new MaskSource { LilToonProperty = "_RimColorTex", ModuleKeyword = "RimLight", FeatureName = "rim light mask" },
                new MaskSource { LilToonProperty = "_ReflectionColorTex", ModuleKeyword = "Specular", FeatureName = "specular mask" },
                new MaskSource { LilToonProperty = "_MatCapBlendMask", ModuleKeyword = "MatCapMultiply", FeatureName = "matcap mask" },
                new MaskSource { LilToonProperty = "_MatCap2ndBlendMask", ModuleKeyword = "MatCapAdd", FeatureName = "2nd matcap mask" },
                new MaskSource { LilToonProperty = "_HairSpecularMask", ModuleKeyword = "HairSpecular", FeatureName = "hair specular mask" },
            };

            Color[] pixels = null;
            var width = 0;
            var height = 0;
            var used = new List<string>();
            var channelOwner = new string[4];

            foreach (var source in sources)
            {
                if (!ShaderUtility.HasProperty(lilToonMaterial, source.LilToonProperty)) continue;
                if (source.RequireToggle && ShaderUtility.HasProperty(lilToonMaterial, source.ToggleProperty) &&
                    lilToonMaterial.GetFloat(source.ToggleProperty) == 0f) continue;

                // lilToon 的透明遮罩是改 alpha 的，NonToon 的共享遮罩改不了 alpha ——
                // 它的效果已经由贴图烘焙器写进基础贴图的 Alpha 了，这里不用再占一个通道。
                if (source.LilToonProperty == "_AlphaMask" &&
                    ShaderUtility.HasProperty(lilToonMaterial, "_AlphaMaskMode") &&
                    Mathf.RoundToInt(lilToonMaterial.GetFloat("_AlphaMaskMode")) != 0)
                {
                    log.Mapped("lilToon 透明遮罩（_AlphaMaskMode " +
                               Mathf.RoundToInt(lilToonMaterial.GetFloat("_AlphaMaskMode")) + "）",
                        "已烘焙进 _BaseTexture 的 Alpha，未写入共享遮罩（NonToon 的遮罩不改 alpha）");
                    continue;
                }

                var texture = lilToonMaterial.GetTexture(source.LilToonProperty) as Texture2D;
                if (texture == null) continue;

                var channelProperty = FindChannelProperty(shader, source.ModuleKeyword);
                var channel = channelProperty == null
                    ? 0
                    : Mathf.Clamp(ShaderUtility.GetIntValue(nonToonMaterial, channelProperty), 0, 3);
                if (channelOwner[channel] != null)
                {
                    log.Warn(source.FeatureName + " 和 " + channelOwner[channel] + " 都指向 NonToon 共享遮罩的 " +
                             "RGBA"[channel] + " 通道，只烘焙了 " + channelOwner[channel] +
                             "。想两个都保留，请在转换后的材质上改掉其中一个的 Mask Channel。");
                    continue;
                }

                if (!ReadPixels(texture, out var texturePixels, out var textureWidth, out var textureHeight, log, source.FeatureName))
                    continue;

                if (pixels == null)
                {
                    width = textureWidth;
                    height = textureHeight;
                    pixels = new Color[width * height];
                    for (var i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                }

                channelOwner[channel] = source.FeatureName;
                used.Add(source.FeatureName);
                var sourceIsAlpha = source.LilToonProperty == "_BacklightColorTex";
                var sourceIsGreen = source.LilToonProperty == "_RimColorTex";

                for (var y = 0; y < height; y++)
                {
                    var sy = Mathf.Clamp(y * textureHeight / height, 0, textureHeight - 1);
                    for (var x = 0; x < width; x++)
                    {
                        var sx = Mathf.Clamp(x * textureWidth / width, 0, textureWidth - 1);
                        var sample = texturePixels[sy * textureWidth + sx];
                        var value = sourceIsAlpha ? sample.a : sourceIsGreen ? sample.g : sample.r;

                        var target = pixels[y * width + x];
                        switch (channel)
                        {
                            case 0: target.r = value; break;
                            case 1: target.g = value; break;
                            case 2: target.b = value; break;
                            default: target.a = value; break;
                        }
                        pixels[y * width + x] = target;
                    }
                }
            }

            if (pixels == null) return;

            var directory = folder.Replace('\\', '/').TrimEnd('/');
            EnsureFolder(directory);
            var maskPath = directory + "/" + ShaderUtility.SanitizeFileName(nonToonMaterial.name) + "_NTMask.png";

            var existing = nonToonMaterial.GetTexture("_SharedMask") as Texture2D;
            if (existing != null && AssetDatabase.GetAssetPath(existing) != maskPath)
                log.Warn("NonToon 共享遮罩原本已有内容（" + existing.name + "），现在被生成的遮罩替换了。");

            WriteTexture(pixels, width, height, maskPath);
            ImportAsMask(maskPath);

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            if (asset == null)
            {
                log.Warn("生成的共享遮罩加载失败（路径：" + maskPath + "，文件存在：" +
                         File.Exists(maskPath) + "); the mask channel values were left at their defaults.");
                return;
            }

            nonToonMaterial.SetTexture("_SharedMask", asset);
            log.Mapped("lilToon 遮罩（" + string.Join("、", used) + "）", "_SharedMask");
        }

        private static void WriteTexture(Color[] pixels, int width, int height, string path)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(true, false);
                var png = texture.EncodeToPNG();
                if (png != null) File.WriteAllBytes(path, png);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void ImportAsMask(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;               // masks are data, not colour
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
        }

        private static string FindChannelProperty(Shader shader, string moduleKeyword)
        {
            if (shader == null) return null;
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (name.IndexOf("MaskChannel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf(moduleKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (shader.GetPropertyDescription(i).IndexOf("Mask Channel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                return name;
            }
            // Fall back to the main module naming used by NonToon itself.
            var direct = ShaderUtility.NtModulePrefix + moduleKeyword.ToLowerInvariant() + "_" +
                         moduleKeyword + "MaskChannel";
            return ShaderUtility.HasProperty(shader, direct) ? direct : null;
        }

        private static bool ReadPixels(Texture2D texture, out Color[] pixels, out int width, out int height,
            ConversionLog log, string featureName)
        {
            pixels = null;
            width = 0;
            height = 0;

            if (texture.isReadable)
            {
                try
                {
                    width = texture.width;
                    height = texture.height;
                    pixels = texture.GetPixels();
                    if (pixels != null && pixels.Length > 0) return true;
                }
                catch (Exception exception)
                {
                    log.Warn("无法读取 " + featureName + "（" + exception.Message + "），该遮罩通道仍是默认值。");
                    return false;
                }
                return false;
            }

            // Read/Write is disabled on the source texture: blit it through a temporary render texture.
            try
            {
                width = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.width), 4, 4096);
                height = Mathf.Clamp(Mathf.NextPowerOfTwo(texture.height), 4, 4096);
                var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readable.Apply();
                    pixels = readable.GetPixels();
                }
                finally
                {
                    if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(temporary);
                }
                if (pixels != null && pixels.Length > 0) return true;
            }
            catch (Exception exception)
            {
                log.Warn("无法读取 " + featureName + "（" + exception.Message + "），该遮罩通道仍是默认值。" +
                         "请在贴图上勾选 Read/Write 后再转换这个遮罩。");
            }
            return false;
        }

        private static IEnumerable<string> UsedLilToonMasks(Material material)
        {
            var properties = new[]
            {
                "_AlphaMask", "_BacklightColorTex", "_RimColorTex", "_ReflectionColorTex",
                "_MatCapBlendMask", "_MatCap2ndBlendMask", "_HairSpecularMask"
            };
            foreach (var property in properties)
            {
                if (!ShaderUtility.HasProperty(material, property)) continue;
                if (material.GetTexture(property) != null) yield return property;
            }
        }

        internal static void EnsureFolder(string folder)
        {
            NonToonTextureBaker.EnsureFolder(folder);
        }
    }
}
