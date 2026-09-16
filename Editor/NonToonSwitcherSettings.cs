// LilToNonToon Switcher
using UnityEditor;
using UnityEngine;

namespace NonToonSwitcher
{
    /// <summary>
    /// Project-wide settings, saved to ProjectSettings/NonToonSwitcherSettings.asset.
    /// The FilePath attribute is what makes ScriptableSingleton actually write to disk; without it Unity
    /// only keeps the values in memory for the current session.
    /// </summary>
    [FilePath("ProjectSettings/NonToonSwitcherSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class NonToonSwitcherSettings : ScriptableSingleton<NonToonSwitcherSettings>
    {
        public const string DefaultOutputFolder = "Assets/NonToonConverted";

        [SerializeField] private string outputFolder = DefaultOutputFolder;
        [SerializeField] private SwitcherMode switcherMode = SwitcherMode.MaterialSetter;
        [SerializeField] private bool createMenuToggle = true;
        [SerializeField] private bool setNonToonOnByDefault = true;
        [SerializeField] private bool reuseExistingSwitcher = true;
        [SerializeField] private string menuParameterName = "NonToon";
        [SerializeField] private string menuLabel = "NonToon";
        [SerializeField] private bool bakeBaseTexture = true;
        [SerializeField] private bool bakeSharedMask = true;
        [SerializeField] private bool bakeGradients = true;
        [SerializeField] private bool logToConsole = true;
        [SerializeField] private float outlineZOffsetFactor = DefaultOutlineZOffsetFactor;
        [SerializeField] private float outlineWidthFactor = 1f;
        [SerializeField] private ReplaceMode replaceMode = ReplaceMode.Switch;

        /// <summary>lilToon 描边宽度贴图的补偿：描边整体后移 = 描边宽度 × 0.01 × 这个倍数。</summary>
        public const float DefaultOutlineZOffsetFactor = 1f;

        public string OutputFolder
        {
            get { return string.IsNullOrEmpty(outputFolder) ? DefaultOutputFolder : outputFolder; }
            set { outputFolder = ShaderUtility.ToAssetPath(value); SaveSettings(); }
        }

        /// <summary>MA Material Setter (one entry per renderer slot) or MA Material Swap (one entry per material).</summary>
        public SwitcherMode SwitcherMode
        {
            get { return switcherMode; }
            set { switcherMode = value; SaveSettings(); }
        }

        public bool CreateMenuToggle
        {
            get { return createMenuToggle; }
            set { createMenuToggle = value; SaveSettings(); }
        }

        /// <summary>Reverse the switch: show NonToon by default and toggle back to lilToon.</summary>
        public bool SetNonToonOnByDefault
        {
            get { return setNonToonOnByDefault; }
            set { setNonToonOnByDefault = value; SaveSettings(); }
        }

        /// <summary>
        /// 同一个 avatar 下已存在 _NonToonSwitch 时复用它（把新材质追加进去）。
        /// 关闭后每次转换都会新建一个开关。
        /// </summary>
        public bool ReuseExistingSwitcher
        {
            get { return reuseExistingSwitcher; }
            set { reuseExistingSwitcher = value; SaveSettings(); }
        }

        public string MenuParameterName
        {
            get { return string.IsNullOrEmpty(menuParameterName) ? "NonToon" : menuParameterName; }
            set { menuParameterName = value; SaveSettings(); }
        }

        public string MenuLabel
        {
            get { return string.IsNullOrEmpty(menuLabel) ? "NonToon" : menuLabel; }
            set { menuLabel = value; SaveSettings(); }
        }

        public bool BakeSharedMask
        {
            get { return bakeSharedMask; }
            set { bakeSharedMask = value; SaveSettings(); }
        }

        /// <summary>Bake _MainTex x _Color into a new PNG so the colour survives the conversion.</summary>
        public bool BakeBaseTexture
        {
            get { return bakeBaseTexture; }
            set { bakeBaseTexture = value; SaveSettings(); }
        }

        /// <summary>Bake lilToon shadow / rim shade colours into a Shader Core gradient array.</summary>
        public bool BakeGradients
        {
            get { return bakeGradients; }
            set { bakeGradients = value; SaveSettings(); }
        }

        public bool LogToConsole
        {
            get { return logToConsole; }
            set { logToConsole = value; SaveSettings(); }
        }

        /// <summary>
        /// 转换后的材质怎么落到模型上：
        /// <see cref="ReplaceMode.Switch"/> 保留 lilToon 材质并建切换开关（默认）；
        /// <see cref="ReplaceMode.ReplaceInPlace"/> 原地直接替换成 NonToon；
        /// <see cref="ReplaceMode.DuplicateThenReplace"/> 复制一份 &lt;名字&gt;_nontoon，换在副本上，原对象取消勾选。
        /// </summary>
        public ReplaceMode ReplaceMode
        {
            get { return replaceMode; }
            set { replaceMode = value; SaveSettings(); }
        }

        /// <summary>
        /// lilToon 的描边宽度贴图（`_OutlineWidthMask`）NonToon 没有对应功能，
        /// 转换时会把描边整体往后推 `描边宽度 × 0.01 × 这个倍数`，避免描边壳在嘴/眼附近画到脸上。
        /// 1 = 与描边自身宽度同量级（默认）；0 = 不做处理；觉得描边太淡可以调小。
        /// </summary>
        public float OutlineZOffsetFactor
        {
            get { return Mathf.Clamp(outlineZOffsetFactor, 0f, 10f); }
            set { outlineZOffsetFactor = Mathf.Clamp(value, 0f, 10f); SaveSettings(); }
        }

        /// <summary>
        /// 描边宽度的整体手动倍数（默认 1 = 不改）。
        /// 自动的部分是"按使用该材质的对象世界缩放折算"（因为 lilToon 的描边偏移在物体空间、
        /// NonToon 在世界空间），这个倍数是在那之上再乘一次，用来整体调粗细。
        /// </summary>
        public float OutlineWidthFactor
        {
            get { return Mathf.Clamp(outlineWidthFactor, 0f, 5f); }
            set { outlineWidthFactor = Mathf.Clamp(value, 0f, 5f); SaveSettings(); }
        }

        private void SaveSettings()
        {
            Save(true);
        }
    }
}
