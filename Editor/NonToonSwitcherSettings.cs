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

        private void SaveSettings()
        {
            Save(true);
        }
    }
}
