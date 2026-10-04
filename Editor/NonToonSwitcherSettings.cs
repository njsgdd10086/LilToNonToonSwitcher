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

        // ---- 色带（Shade 渐变）标定参数 ----
        // 这两个值决定"明暗过渡从哪里开始、暗端有多暗"，是**转换器对 lilToon→NonToon 观感差异的标定**：
//   · 1 / 1   = 完全照搬 lilToon 的窗口与原色（最忠实，但 NonToon 的乘算 Shade 会让中间调偏亮，
//               实测色带会变成 0.447→1 的近水平线，衣服看起来比 lil 平）；
//   · 2.6/0.7 = 出厂默认。对着 lilToon 的渲染做四分位实测标定得到（裙区中位 0.693 → 0.601，
//               lil 为 0.534），让层次更接近 lil。
// 两者都能在设置窗口里随时改，改完重新转换即可。
        [SerializeField] private float gradientWindowScale = 1f;
        [SerializeField] private float gradientDarkEnd = 1f;

        // ---- 色调补偿（高级）----
        // NonToon 的着色链路与 lilToon 并不等价（NonToon 的 Shade 是乘算、光也积得偏少），
        // 所以"忠实照搬"会整体偏暗偏平。这三项把补偿做在**烘焙出来的贴图**上，
        // 而不是写成 shader 里的魔数（自建模块的属性到不了 shader，写进去就没法调了）。
        // 全部设为 1 即"不做任何补偿"，是最忠实的档位。
        [SerializeField] private float baseExposure = 1f;
        [SerializeField] private float toneCurveGamma = 1f;
        [SerializeField] private float toneCurveGain = 1f;

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

        /// <summary>
        /// 色带过渡窗口的展宽倍数。1 = 照搬 lilToon 的 (border ± blur/2)；
        /// 大于 1 会让暗部更早进入（中间调变暗），用于补偿不同着色链路的观感差异。
        /// </summary>
        public float GradientWindowScale
        {
            get { return gradientWindowScale <= 0f ? 1f : gradientWindowScale; }
            set { gradientWindowScale = Mathf.Clamp(value, 0.1f, 8f); SaveSettings(); }
        }

        /// <summary>
        /// 色带暗端的额外收缩系数。1 = 不加收；小于 1 会把暗端压得更深。
        /// </summary>
        public float GradientDarkEnd
        {
            get { return gradientDarkEnd <= 0f ? 1f : gradientDarkEnd; }
            set { gradientDarkEnd = Mathf.Clamp(value, 0.2f, 1f); SaveSettings(); }
        }

        /// <summary>烘焙贴图时的整体曝光倍数。1 = 不补偿（最忠实）。</summary>
        public float BaseExposure
        {
            get { return baseExposure <= 0f ? 1f : baseExposure; }
            set { baseExposure = Mathf.Clamp(value, 0.2f, 4f); SaveSettings(); }
        }

        /// <summary>色调曲线 gamma：>1 压暗中低段（1 = 不补偿）。</summary>
        public float ToneCurveGamma
        {
            get { return toneCurveGamma <= 0f ? 1f : toneCurveGamma; }
            set { toneCurveGamma = Mathf.Clamp(value, 0.3f, 3f); SaveSettings(); }
        }

        /// <summary>色调曲线增益：整体乘数（1 = 不补偿）。</summary>
        public float ToneCurveGain
        {
            get { return toneCurveGain <= 0f ? 1f : toneCurveGain; }
            set { toneCurveGain = Mathf.Clamp(value, 0.2f, 4f); SaveSettings(); }
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
