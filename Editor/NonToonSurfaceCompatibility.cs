using UnityEngine;
using UnityEditor;

namespace NonToonSwitcher
{
    /// <summary>Transfers lilToon surface semantics that the stock slots cannot represent.</summary>
    public static class NonToonSurfaceCompatibility
    {
        public const string Prefix = "_com_nontoon_switcher_surface_";
        public const string ModuleId = "com.nontoon.switcher.surface";

        public static bool EnsureInstalled(ConversionLog log)
        {
            var shader = ShaderUtility.FindNonToonShader();
            if (IsCompiled(shader)) return true;
            if (!ModuleInstaller.EnsureModule(ModuleId, "lilToon 材质兼容模块", log)) return false;
            shader = ShaderUtility.FindNonToonShader();
            if (IsCompiled(shader)) return true;
            // The registry can already contain an older version of this module.
            // Its token check alone does not prove newly added properties were generated.
            if (shader == null) return false;
            var path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path)) return false;
            try { ModuleInstaller.RegenerateShader(path); }
            catch (System.Exception error)
            {
                if (log != null) log.Error("重新生成材质兼容模块失败：" + error.Message);
                return false;
            }
            return IsCompiled(ShaderUtility.FindNonToonShader());
        }

        private static bool IsCompiled(Shader shader)
        {
            return shader != null && shader.FindPropertyIndex(Prefix + "MainUV") >= 0;
        }

        public static bool HasOutlineCompatibility(Material target)
        {
            return target != null && target.HasProperty(Prefix + "OutlineCompatible") &&
                target.GetFloat(Prefix + "OutlineCompatible") > 0.5f;
        }

        public static void ApplyMatCaps(Material source, Material target, ConversionLog log)
        {
            if (source == null || target == null || !target.HasProperty(Prefix + "LilCompatible")) return;
            target.SetFloat(Prefix + "Enable", 1f);
            target.SetFloat(Prefix + "LilCompatible", 1f);
            CopyMainUV(source, target);
            for (int layer = 0; layer < 2; layer++)
            {
                string from = layer == 0 ? "_MatCap" : "_MatCap2nd";
                string to = Prefix + "Lil" + layer;
                string toggle = layer == 0 ? "_UseMatCap" : "_UseMatCap2nd";
                bool enabled = source.HasProperty(toggle) && source.GetFloat(toggle) > 0.5f;
                target.SetFloat(to + "Enabled", enabled ? 1f : 0f);
                CopyTexture(source, target, from + "Tex", to + "Texture");
                CopyTexture(source, target, from + "BlendMask", to + "Mask");
                CopyTexture(source, target, from + "BumpMap", to + "BumpMap");
                target.SetColor(to + "Color", source.HasProperty(from + "Color")
                    ? source.GetColor(from + "Color") : Color.white);
                foreach (string suffix in new[] { "Blend", "BlendMode", "MainStrength", "EnableLighting",
                    "ShadowMask", "BackfaceMask", "ApplyTransparency", "NormalStrength",
                    "CustomNormal", "BumpScale", "Lod", "Perspective", "ZRotCancel", "VRParallaxStrength" })
                {
                    if (source.HasProperty(from + suffix)) target.SetFloat(to + suffix, source.GetFloat(from + suffix));
                }
                if (source.HasProperty(from + "BlendUV1")) target.SetVector(to + "BlendUV1", source.GetVector(from + "BlendUV1"));
            }
            // Stock MatCaps cannot express the two independent source layers.
            // Disable them to avoid applying a second reflection over the compatible result.
            string legacy = ShaderUtility.NtMatCapsPrefix + "Enable";
            ShaderUtility.SetIntPersistent(target, legacy, 0);
            string keyword = legacy.ToUpperInvariant();
            target.DisableKeyword(keyword + "_1");
            target.EnableKeyword(keyword + "_0");
            log.Mapped("MatCap 两层独立开关、混合模式、Alpha、强度、遮罩与光照", "lilToon 兼容采样（保留 HDR）");
        }

        public static void ApplyOutline(Material source, Material target, ConversionLog log)
        {
            if (source == null || target == null || !target.HasProperty(Prefix + "OutlineCompatible")) return;
            target.SetFloat(Prefix + "OutlineCompatible", 1f);
            CopyMainUV(source, target);
            if (source.HasProperty("_OutlineWidthMask"))
                target.SetTexture(Prefix + "OutlineWidthMask", source.GetTexture("_OutlineWidthMask"));
            target.SetFloat(Prefix + "OutlineVertexWidth", source.HasProperty("_OutlineVertexR2Width")
                ? source.GetFloat("_OutlineVertexR2Width") : 0f);
            target.SetFloat(Prefix + "OutlineFixWidth", source.HasProperty("_OutlineFixWidth")
                ? source.GetFloat("_OutlineFixWidth") : 0f);
            // Width masks now work directly; remove the old whole-shell depth workaround.
            target.SetFloat("_OutlineZOffset", source.HasProperty("_OutlineZBias") ? source.GetFloat("_OutlineZBias") : 0f);
            int vertexMode = source.HasProperty("_OutlineVertexR2Width")
                ? Mathf.RoundToInt(source.GetFloat("_OutlineVertexR2Width")) : 0;
            ShaderUtility.SetIntPersistent(target, "_OutlineFromVertexColor", vertexMode == 2 ? 1 : 0);
            target.SetInteger("_OutlineFromVertexColor", vertexMode == 2 ? 1 : 0);
            log.Mapped("描边宽度遮罩与顶点宽度模式", "直接采样源遮罩，单位化外扩方向");
        }

        private static void CopyMainUV(Material source, Material target)
        {
            if (!source.HasProperty("_MainTex")) return;
            var scale = source.GetTextureScale("_MainTex");
            var offset = source.GetTextureOffset("_MainTex");
            target.SetVector(Prefix + "MainUV", new Vector4(scale.x, scale.y, offset.x, offset.y));
        }

        private static void CopyTexture(Material source, Material target, string from, string to)
        {
            if (!source.HasProperty(from) || !target.HasProperty(to)) return;
            target.SetTexture(to, source.GetTexture(from));
            target.SetTextureScale(to, source.GetTextureScale(from));
            target.SetTextureOffset(to, source.GetTextureOffset(from));
        }
    }
}
