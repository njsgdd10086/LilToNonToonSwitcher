# 更新日志

## [1.1.15] - 2026-09-17

这一版做了**架构调整**：把 Shader Core 模块从插件里拆出去，做成独立的模块包。功能不变，但模块可以单独安装使用了。

### 变更

- **织物/法线细节模块搬到新包 `com.nontoon.modules`**（仓库 NonToon Modules）✓ ——
  本插件现在通过 VPM 依赖**订阅**它（`vpmDependencies`）✓，插件里不再包含任何 Shader Core 模块文件 ✓；
- 转换时会调用模块包的 API **自动勾选**织物模块（幂等 ✓，没登记就登记 + 重新生成 NonToon shader ✓）；
- 调用的方式是**反射** ✓ —— 模块包万一没装，本插件仍能正常编译 ✓，只在转换日志里给一条明确提示 ✓；
- 菜单新增 `Tools/LilToNonToon Switcher/打开 NonToon 模块管理` ✓（跳到模块包的勾选界面 ✓）。

### 说明

- 升级后请让 VCC/ALCOM 一并安装 `com.nontoon.modules`（依赖会自动带上 ✓）；
- **如果之前手动把本插件的 `Shaders` 目录拷进过工程**，请删掉那份旧副本 ✗ ——
  同一个模块 id 出现两次会被 Shader Core 重复编入 shader 导致编译错误；
  模块包的勾选界面发现重复时也会在 Console 里提示该删哪一份 ✓。

### 模块本身没变

织物模块的算法与默认值同 1.1.14（强度 0.25 ✓、方向 0.35 / -0.5 ✓、软压缩 ✓），只是换了家。

## [1.1.14] - 2026-09-17

这一版是继续追「白金色服装的金饰 / 质感」时挖出来的一批问题。

### 修复

- **第二层 MatCap 从来没转过**：lilToon 的 `_MatCap2ndTex`（第二层 MatCap）完全没被映射 ✗ ——
  帽子的**羽毛 / 玫瑰**这类走第二层的装饰，转换后就一直是灰的 ✗。
  现在第一层按自己的混合模式占一个槽、**第二层用剩下那个槽** ✓（`_MatCap2ndTex` → `MatCapMultiply`、
  `_MatCap2ndColor × _MatCap2ndBlend` → 对应颜色、`_MatCap2ndBlendMask` 按其槽位分配通道 ✓，
  并用 `_UseMatCap2nd` 判断是否需要 ✓）。

- **Shader Core 的模块开关要写「关键字」**：模块开关带 `SCConstValue`，真正让它生效的是材质上的关键字
  `<属性名大写>_<值>`（例如 `_JP_LILXYZW_NONTOON_MATCAPS_ENABLE_1`）。以前只写 `_Enable` 整数 ✗，
  表现就是「开关明明勾着、却要手动在 Inspector 里取消再勾一次才生效」✗。现在整数和关键字都写 ✓。

- **没有打开 MatCap 模块**：源材质用 MatCap 时，现在会自动把 `matcaps_Enable` 设为 1 ✓。

- **`_BumpMap` 现在同时接到 Details 模块的 `_Detail0NormalMap`**：NonToon 的主 `_NormalMap` 只影响
  `sd.N`（toon 硬色阶下细节几乎看不出来 ✗），而 Details 的 `_Detail0NormalMap` 喂 `sd.N_detail`、
  Shade 模块**真的**用它参与明暗 ✓ —— 这是法线细节唯一可能的出口 ✓。
  同时把四层 `Detail*Boost` 钉成 1 ✓（Details 打开后 `albedo *= detailTex * boost`，
  `_DetailMask` 默认全白，boost 不是 1 会整体改亮度 ✗）。

- **遮罩通道按「实际使用的槽位」分配**（MatCap 以前写死 `MatCapMultiply` ✗，而贴图放在 `MatCapAdd` ✗
  → 数据烘进 R、模块读 A ✗，被乘成 ~0）+ 通道写回后**读回校验** ✓。

- **转换后强制重新导入材质**：Shader Core 的模块状态要在材质重新导入后刷新 ✓，
  否则会出现「转换完了但模块没生效」✓。

- **不需要烘焙时把 `_BaseTexture` 指回源贴图**（不再残留上一次转换的旧烘焙图 ✓）、
  **RenderTexture 兜底路径保持原贴图长宽比** ✓、**MatCap 颜色总是写入** ✓。

### 已知限制（写进 README）

- **金属反射**：lilToon 的 `_UseReflection`（`_Metallic` / `_Smoothness` / 环境反射）NonToon 没有对应能力 ✗，
  只能近似成一个高光 —— 「靠反射变金」的部分会比原版偏灰 ✓。
- **织物质感 / 法线细节**：NonToon 的着色是 toon 硬色阶 + 硬高光 ✗，法线扰动没有足够的输出通道 ✓ ——
  主 `_NormalMap`、Details 的 `_Detail0NormalMap`、调高 `_NormalScale`、降低 `_Roughness` 都试过 ✓，
  效果都不理想（降低 roughness 还会变成塑料光泽 ✗）。需要完全一致只能自建 shader / 写模块 ✓。

## [1.1.13] - 2026-09-17

这一版主要修 **MatCap（金属质感）整片失效** —— 顺着「白金色服装的金饰变成灰的」一路查到三个叠加的原因。

### 修复

- **Shader Core 的模块开关要写关键字，不只是写 `_Enable`**：NonToon 每个模块的开关带
  `SCConstValue`，真正让模块生效的是材质上的关键字 **`<属性名大写>_<值>`**（例如
  `_JP_LILXYZW_NONTOON_MATCAPS_ENABLE_1`）。我们以前只写整数、不加关键字 ✗，
  表现就是「开关明明是勾着的、却要手动在 Inspector 里取消再勾一次才生效」✗。现在两者都写 ✓。

- **MatCap 混合模式理解反了**：lilToon 的 `lilBlendColor` 是
  `0 = Normal`（用 matcap 颜色替换）/ `1 = Add` / `2 = Screen` / `3 = Multiply` ——
  我们以前把 0 丢进 Multiply ✗，白布 × 金色 matcap 会算成发灰 ✗。现在 0/1/2 走 **`MatCapAdd`**
  （叠加最接近「替换」）、只有 3 才用 Multiply，并**清空另一个槽**（复用材质时旧贴图会双重生效）。

- **遮罩通道按「实际使用的槽」分配**：MatCap 的遮罩以前写死挂到 `MatCapMultiply` 模块 ✗，
  而贴图其实放在 `MatCapAdd` ✗ → 数据烘进 R 通道、模块却读 A ✗（被乘成 ~0）。
  现在按实际槽位分配通道，并写回模块 + 读回校验 ✓。

- **去掉第二层 MatCap 的无用遮罩**：`_MatCap2ndBlendMask` 会占掉一个通道、还把模块的
  Mask Channel 改到自己那个通道上 ✗，导致第一层的金色遮罩被别的通道乘掉 ✓
  （第二层的贴图 `_MatCap2ndTex` 我们本来就没转）。

- **转换后强制重新导入材质**：Shader Core 的模块状态要在材质重新导入后才会刷新 ✓，
  否则会出现「转换完了但模块没生效」✓。

- **不需要烘焙时把 `_BaseTexture` 指回源贴图**：以前会残留上一次转换留下的旧烘焙图 ✓
  （有一件衣服因此一直是灰的 ✓）。

- MatCap 颜色现在**总是写入**（以前源色是白就跳过 ✗，材质里可能留着上一次的旧颜色 ✗）。

### 已知限制

- **金属反射**：lilToon 的 `_UseReflection`（`_Metallic` / `_Smoothness` / 环境反射）在 NonToon 里没有对应能力 ✗，
  只能近似成一个高光 —— 所以「靠反射变金」的部分（例如帽子的**羽毛 / 玫瑰花**）转换后会比原版**偏灰** ✓。
  需要完全一致的话，这部分建议保留 lilToon 材质，或手动把 NonToon 的 Specular 调暖 + 降低 Roughness 近似。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.13] - 2026-09-17

### 修复

- **衣服 / 配件被洗成黑白**（实测一整套白金服装）：lilToon 的「色彩校正」（_MainTexHSVG）是被
  shader keyword **EFFECT_HUE_VARIATION** 门控的 —— 关键字没开时 shader 根本不读这串值，
  材质里存的可能只是残留（这套衣服存着 (0, 0, 1.4, 0.7)，**饱和度 0**）。
  我们之前只看值是不是默认就无条件烘焙，于是把整件洗成了灰度 —— 而 lilToon 渲染出来是金色的。

  现在只在关键字真的开着时才烘色彩校正，否则保留原贴图颜色，日志写明：

  `
  · _MainTexHSVG (-0.37, 0, 1.4, 0.7)（源材质没开 EFFECT_HUE_VARIATION 关键字，lilToon 不会应用它）  ->  不烘焙色彩校正，保留原贴图颜色
  `

  实测：UV1_White_Gold 烘焙后饱和度 33.4（源 32.1）、RGB 与源完全一致 ✓。

- **旧烘焙图残留**：当这次转换**不需要**烘焙时（比如上面那种情况），_BaseTexture 现在会**指回源贴图** ——
  之前会继续挂着上一次转换留下的旧烘焙图（有一件衣服因此一直是灰的，即使已经不需要烘）。
  需要烘透明遮罩的材质（UV1~UV4）仍然走烘焙 ✓，不需要的（UV5_White、UV5_Default、BulletMat）
  直接指回源贴图 ✓。

## [1.1.12] - 2026-09-17

### 新增

- **版本号与更新检查**：

  - 菜单 Tools > LilToNonToon Switcher > 关于与更新检查…：显示当前版本、索引里的最新版本，并提供
    「检查更新 / 打开发布页」两个按钮；
  - 菜单 Tools > LilToNonToon Switcher > 检查更新：直接检查并在有新版时弹窗；
  - 设置窗口顶部显示「版本 vX.Y.Z」+「检查更新 / 发布页」按钮；
  - 编辑器启动后**每天自动检查一次**（EditorPrefs 记时间戳，失败静默，发现新版只在 Console 提示一行）。

  检查走的是本仓库的 VPM 索引（https://njsgdd10086.github.io/vpm-listing/index.json），
  不需要额外配置。注：Unity 的菜单名是**静态**的（[MenuItem] 编译期就固定），所以版本号放在
  菜单打开的对话框和设置窗口里，没法直接写进菜单文字。

## [1.1.11] - 2026-09-17

### 修复

- **凭空多出的描边**：lilToon「有没有描边」是**靠 shader 变体**区分的 ——
  `Hidden/lilToonOutline` / `Hidden/lilToonTransparentOutline` 有描边，
  而 `Hidden/lilToon`、`_lil/lilToonMulti`、`Hidden/lilToonMultiRefraction` 这些**没有**。
  这些变体里 `_OutlineWidth` 只是作者调过的**残留值**（常见 0.08），我们之前无条件照搬，
  于是给它们凭空加了一圈描边（还要再乘对象缩放）。

  现在按 shader 名判断：变体名里不含 `Outline` 时把描边宽度写成 0，日志写明原因：

  ```
  · 描边（源 shader「Hidden/lilToonMultiRefraction」没有描边变体，_OutlineWidth=0.08 是残留值）  ->  _OutlineWidth = 0（不加描边）
  ```

  **这一条很可能就是「切到 NonToon 后视角被东西挡住」的原因**：透明 / 特效层（花瓣、纸片、装饰）
  多出的那圈描边壳是**不透明**的（混合 `1/0`），贴在头脸附近时看起来就像有东西糊住视野。

- 折射材质（`Hidden/lilToonMultiRefraction`）现在会明确提示：NonToon 没有折射，
  材质会按普通**不透明**层渲染（源材质的混合本来就是 `One / Zero`，所以它原本也是不透明层，
  只是靠折射扭曲看起来"透"）。这类材质建议保持 lilToon 或手动调。

## [1.1.10] - 2026-09-17

### 修复

- **多个遮罩挤在同一个通道上**：NonToon 每个模块的「Mask Channel」默认都是 `A`，而一个 lilToon 材质
  可能有多个遮罩（边缘光、逆光、发丝高光…）。我们之前从不改这些通道，于是第二个遮罩会和第一个撞车
  —— 只能保留一个、另一个丢掉（日志里那句"都指向 NonToon 共享遮罩的 A 通道"就是这个）。
  现在烘焙时会给每个遮罩**分配一个空闲通道**并把对应模块指向它（最多 4 个），日志会写明写到哪个通道。

  这条是防御性的：只有同时存在两个以上遮罩的材质才会触发。

## [1.1.9] - 2026-09-17

这一版是**跟另一个 lilToon→NonToon 转换插件逐属性对拍**（16 对材质、同一套 NonToon 属性）找出来的问题。

### 修复

- **阴影 / 边缘阴影的渐变索引串位**：只烘出 RimShade、没烘 Shade 时（源材质 `_UseShadow = 0`
  但 `_UseRimShade = 1`），RimShade 的切片索引是 `0`，而我们按"位置"把 `bakedIndices[0]` 当成
  Shade 的索引 → **Shade 模块采到了边缘阴影的渐变，RimShade 反而被关掉**。
  现在烘焙时按模块分别记下自己的索引（`ShadeIdx = -1`、`RimShadeIdx = 0`）。

- **凭空多出的高光**：源材质 `_UseReflection = 0`（lilToon 不画高光）时我们直接跳过映射，
  结果留下 NonToon 的默认 `_Roughness = 0.5` → 多出一个高光。现在这种情况写 `_Roughness = 1`（无高光）。
  受影响的是绝大多数材质（`M_Alpha_1/2`、`M_Bandage`、`M_Body_*`、`M_Hair*`、`M_Eye`、`M_Shoes` …）。

- **法线强度照搬了无效值**：lilToon 的 `_BumpScale` 在**没挂法线贴图**时完全不参与渲染，但作者往往
  调过（实测有 `8.17` 这种值）。照搬到 NonToon 会把默认白贴图也当成法线放大。现在没挂贴图时写 `_NormalScale = 0`。

- **alpha 通道的混合系数被误解**：1.1.7 加预乘 alpha 换算时，把 `_SrcBlendAlpha / _DstBlendAlpha`
  也一起换成了 `SrcAlpha`。但 lilToon 预乘的是**颜色**通道，alpha 通道并没有预乘（写成 SrcAlpha 会让
  目标 alpha 变成 `a²`），现在只换算 `_SrcBlend`。

### 顺带确认（这些我们是对的，另一个插件反而有问题）

对拍时逐项回源材质核对，以下差异是**对方**的 bug，不是我们的：

| 项目 | 源材质 | 我们 | 另一个插件 |
| --- | --- | --- | --- |
| `_OutlineWidth`（6 个材质） | 0.112 | 0.112 ✓ | **0**（描边丢了） |
| `_OutlineColor`（5 个材质） | (0.783,0.544,0.513) | 同源 ✓ | (0.6,0.45,0.55)（NonToon 默认色） |
| Stencil（8 个材质） | `_StencilRef=146` 等 | 照搬 ✓ | **全部清零**（lilToon 的 shader 确实用 stencil 做遮罩） |
| `M_HairShadow` 混合 | `Zero / SrcColor`（正片叠底） | 0/3 ✓ | 5/10 |
| `_OutlineVertexR2Width = 2` | 用顶点色当描边方向 | `_OutlineFromVertexColor=1` ✓ | 0 |
| `_MatCapColor` | 源值 | 源值 ✓ | 默认 (1,1,1,1) |

## [1.1.8] - 2026-09-17

### 修复

- **不该有的边缘光**：lilToon 的 `_UseRim` 是这层效果的开关，作者关掉时（= 0）颜色值仍然留在材质里。
  我们之前无条件照搬 `_RimColor`，于是**在作者没启用边缘光的材质上凭空多出一圈边缘光**
  （头发上最明显）。现在 `_UseRim = 0` 时写黑色并把范围推到 `(1, 1)`（等于关掉），日志会写明：

  ```
  · _UseRim = 0（作者没启用边缘光）  ->  _jp_lilxyzw_nontoon_rimlight_RimLightColor = 黑色（关掉）
  ```

- **边缘光的形状（菲涅尔幂）没换算**：lilToon 的边缘光是把 `f = 1 − dot(N,V)` 先取
  `pow(f, _RimFresnelPower)`，再用 `_RimBorder` / `_RimBlur` 在**幂次空间**卡阈值；
  NonToon 是在原始空间做 smoothstep。我们之前直接把 `border ± blur/2` 填进范围，等于用了错误的曲线
  （边缘光过宽、贴不到轮廓）。现在按数学关系换算回原始空间：

  ```
  阈值位置 = border^(1/power)
  宽度     = blur / (power · center^(power-1))      ← f^p 的导数
  ```

  实测两套材质的换算结果与另一个转换插件（同样是 NonToon + 独立换算）几乎重合：

  | 材质 | `_RimBorder` / `_RimBlur` / power | 现在 | 另一个插件 |
  | --- | --- | --- | --- |
  | `M_Hair` | 0.51 / 0.36 / 2.4 | (0.644, 0.866) | (0.60, 0.90) |
  | `M_Body_Skin` | 0.739 / 0.661 / 3 | (0.769, 1.000) | (0.75, 0.98) |

- 边缘光颜色现在会乘上 `_RimMainStrength`（lilToon 里它作用在边缘光强度上，等价于缩放颜色）。

### 说明

- 源材质要是没写 `_UseRim`（老材质），仍然按"启用"处理，行为不变。

## [1.1.7] - 2026-09-17

### 修复

- **半透明腮红 / 薄纱转完变成又厚又实的一块，边界一刀切**（例如开"害羞脸红"表情时脸上出现硬边色块）。
  原因是两个 shader 对透明度的处理方式不同：

  | | lilToon | NonToon |
  | --- | --- | --- |
  | 片元 | **预乘 alpha**：`rgb *= alpha` | 不预乘 |
  | 混合 | `Blend One OneMinusSrcAlpha`（`_SrcBlend = 1`） | 照搬同一组系数 |

  照搬 `One / OneMinusSrcAlpha` 时，NonToon 会把颜色**按原样叠上去**（等于半透明层变成实心块）。
  数学上 lilToon 出的是 `rgb·a + dst·(1−a)`，把源系数换成 `SrcAlpha(5)` 之后 NonToon 出的**完全一样** ——
  所以转换时现在会自动做这个等价换算（写进日志）：

  ```
  · 渲染状态（沿用原材质）  ->  Cull=2、SrcBlend=5、DstBlend=10、…、_SrcBlend=5（lilToon 预乘 alpha → NonToon 用 SrcAlpha 等价换算）
  ```

  只在「源系数 = `One` **且** 目标系数 ≠ `Zero`」时换算；不透明的 `One / Zero` 保持原样
  （否则会把 alpha 也乘进去，不透明材质会变暗）。

  影响面很广：lilToon 的透明材质基本都是这套系数（示例工程里 17 个，包括脸上的特效层、
  头发、以及各种 ring / planet 配件），**重新转换后**都会变对。

## [1.1.6] - 2026-09-17

### 修复

- **1.1.5 的阴影渐变修复其实没生效**：写 `.scgradients` 资产的代码在最后一步把 Gradient
  **又压回了 4 个等距点** ——

  ```csharp
  key0 = gradient.Evaluate(0f);        // 不管 Gradient 里有多少关键点
  key1 = gradient.Evaluate(1f / 3f);   // 都只在这四个位置取值写出去
  key2 = gradient.Evaluate(2f / 3f);
  key3 = gradient.Evaluate(1f);
  ```

  而阴影过渡窗口（例如宽 0.189）在 0~1 里只占很小一段，4 个等距点几乎全落在窗口外 →
  写出来的渐变是错的（实测你工程里那份干脆是**全白**，等于 NonToon 完全没有阴影压暗）。

  现在：**把 Gradient 的真实关键点原样写出去**（最多 8 个，Unity 的上限）。

- **关键点改用「过渡窗口边界」而不是密集采样**：lilToon 的阴影在窗口内是**分段线性**的，
  所以取 `0 / 1` 加上每层阴影的 `border ± blur/2`（最多 8 个）就与 lilToon **完全等价**，
  比密集采样更准也更省。

- **补上 `_ShadowStrength`**：lilToon 里它是
  `lns.x = lerp(1.0, lns.x, _ShadowStrength)` —— 把受光系数往 1 拉，也就是"阴影只有几成"。
  之前完全没用，阴影会偏重（例如某张脸的强度是 **0.2**，阴影本应很淡）。
  现在按同样的比例收着。

- 源材质用「阴影色贴图 / LUT」模式（`_ShadowColorType != 0`）时会给出警告：
  NonToon 只能按单一阴影色近似。

## [1.1.5] - 2026-09-17

### 修复

- **脸上出现硬边、还带阶梯的明暗分界**（脸颊一块灰蓝、分界线像台阶）。原因是烘 LilToon 阴影色时
  渐变的关键点位置和软硬都不对：

  | | 旧做法 | lilToon 的真实行为 | 现在 |
  | --- | --- | --- | --- |
  | 分界位置 | 关键点放在 `1 − border`（**镜像了**） | 在 `x = saturate(dotNL × 0.5 + 0.5)` 空间里，过渡窗口是 `[border − blur/2, border + blur/2]` | 按窗口逐点采样 |
  | 软硬 | **完全没用 `_ShadowBlur`** | 由 blur 决定过渡宽度 | 用 blur 算窗口宽度 |
  | 第三层阴影 | `_Shadow3rdColor` 的 alpha = 0（作者没启用）也被当成黑色关键点 | `lerp(indirect, third, a3 × (1 − s3))`，a3 = 0 时不生效 | alpha ≈ 0 的层直接跳过 |
  | 阴影强度 | alpha 被忽略 | 每层阴影色的 **alpha 就是强度** | 按强度做 lerp |

  现在按 lilToon 的公式（`lil_common_frag.hlsl` 的 `lilTooningScale` + 阴影色叠加顺序）
  在同一个 `x` 空间里**采样 24 个关键点**烘成 Shade 渐变：过渡窗口 `[border ± blur/2]`、
  alpha 当强度、a3 = 0 的层跳过、受光端回到白色（受光处的 albedo × 光由 NonToon 自己算）。

  转换日志会写明实际用的层数与窗口，例如：
  ```
  · 阴影色 2 层（border 0.117 / blur 0.189，按 lilToon 的过渡窗口采样）  ->  _SharedGradients（Shade 渐变）
  ```

  注意：**这个修复要重新转换材质才会生效**（渐变是烘焙出来的资产）。

## [1.1.4] - 2026-09-17

### 修复

- **半透明的轻纱 / 薄片材质转完变成实心**：lilToon 的「透明遮罩」（`_AlphaMaskMode`）是**直接改 alpha** 的
  （mode 1 替换，2 相乘，3 相加，4 相减，遮罩值先过 `_AlphaMaskScale` / `_AlphaMaskValue`），
  而 NonToon 没有这个功能（`_SharedMask` 只喂给各模块做范围遮罩，改不了 alpha）。
  现在整条链会按 lilToon 的公式烘进 `_BaseTexture` 的 Alpha（顺序也一致：主色 → 透明遮罩 → …），
  遮罩按它自己的 tiling/offset 采样；只要用了透明遮罩就强制烘焙，并把导入设置钉成 `alphaSource = FromInput`。

  顺手修掉两个会静默跳过烘焙的情况：

  - **遮罩贴图没挂**时也算"用了遮罩" —— lilToon 采样没设置过的贴图属性用的就是 shader 里的默认白贴图（= 1），
    所以 `_AlphaMaskValue` 本身就是「整体透明度偏移」（例如 `smooth white planet` 的 −0.33 = alpha × 0.67）。
    以前要求"必须挂了贴图"，正好把这类材质全部跳过，于是它们转换后是实心的。
  - **源材质没挂主贴图**（`_MainTex` = `fileID 0`，lilToon 用默认白贴图）时，以前烘焙器第一句就静默返回。
    现在改为「以白底贴图参与烘焙」，把算好的 Alpha 带出来。

  遮罩贴图读不出来（没勾 Read/Write）时会警告并按默认白遮罩（= 1）计算，而不是整段丢掉。

- **描边粗细和 lilToon 不一致**：两个 shader 的描边偏移不在同一个空间 ——

  ```
  lilToon ：positionOS += outlineN * (_OutlineWidth * 0.01 * 宽度贴图)   然后过物体矩阵 → 会被对象缩放缩放
  NonToon ：vertex.position（已是世界空间）+= outlineN * _OutlineWidth * 0.01 → 不受对象缩放影响
  ```

  所以对象一旦被缩放，NonToon 的描边就会等比例偏粗/偏细。现在转换时按「使用该材质的渲染器」的
  **世界缩放**折算 `_OutlineWidth`（缩放 ≈ 1 时等于不改），同一材质被不同缩放共用时会警告并取平均。

### 新增

- **描边宽度倍数**（手动系数，默认 1，重新转换后生效）：
  菜单 `Tools > LilToNonToon Switcher > 描边宽度倍数 >`（`0.25 / 0.5 / 0.75 / 1 / 1.5`），
  设置窗口「高级设置」里也有 0–2 的滑条。
  最终公式：`描边宽度 = 原值 × 对象世界缩放 × 这个倍数`。
- 转换日志会写明描边补偿的实际数值，例如：
  ```
  · 描边宽度  ->  0.07 → 0.021（对象缩放 0.3 × 手动倍数 1）
  ```

### 已知限制（写进 README）

- lilToon 的 `_OutlineFixWidth` 会在**相机距离小于 1 米**时把描边按 `× saturate(距离)` 收窄，
  NonToon 的描边没有随距离变化的机制，所以贴脸看时 NonToon 仍会略粗一点；用「描边宽度倍数」可以按材质补偿。

## [1.1.3] - 2026-09-16

### 修复

- **描边后移会被误判触发**：1.1.2 判断"源材质有没有用描边宽度贴图"时只看了
  `GetTexture("_OutlineWidthMask") != null`，但 Unity 对**没设置过**的贴图属性会返回
  shader 里声明的默认贴图（lilToon 写的是 `"white"`），那**不是 null**。
  于是那些压根没写这个属性、根本没用宽度遮罩的材质（实测 liltonon 工程里 24 个 lilToon 材质中有 2 个）
  也会被白白后移描边。现在会排除所有内置默认贴图（`whiteTexture` / `blackTexture` /
  `grayTexture` / `normalTexture` 等），只有真的挂了 PNG 才算数。

### 新增

- **描边后移的日志写细了**：凡是"源材质挂了宽度贴图"的材质都会说明自己的结果，包括没后移的原因：

  ```
  · 描边宽度贴图（NonToon 没有这个功能） -> 描边整体后移 0.0007（_OutlineZOffset，倍数 1 × 描边宽度）…
  · 描边宽度贴图（NonToon 没有这个功能） -> 描边宽度为 0，本来就没有描边，未做后移
  · 描边宽度贴图（NonToon 没有这个功能） -> 后移倍数 = 0，未做处理（描边可能盖住嘴唇 / 眼睛）
  · 描边宽度贴图（NonToon 没有这个功能） -> 原有 _OutlineZOffset 0.001 已经不小于后移量，保持不动
  ```

  没挂宽度贴图的材质不会打印这一行（否则每个材质都多一行），所以
  **日志里没有这一行 = 没用宽度遮罩 = 不需要处理**。

## [1.1.2] - 2026-09-16

### 修复

- **嘴唇 / 眼睛附近被描边"糊住"、看起来像脸被撕破**：
  lilToon 的描边宽度贴图（`_OutlineWidthMask`）NonToon 没有对应功能 —— 作者常用它把五官附近的描边宽度压成 0，
  而 NonToon 的描边是**均匀**的反向外扩壳，于是嘴腔内壁的壳照样外扩、压在嘴唇上，
  并且它采样的是该处 UV 的基础贴图颜色（正是嘴部那块深红），看起来就是"脸被撕破"。

  现在转换时会把描边**整体沿视线方向后移**（写进 `_OutlineZOffset`）：

  ```
  后移量 = _OutlineWidth × 0.01 × 倍数（默认倍数 1）
  ```

  `× 0.01` 是 NonToon 自己的换算（`pos += N * _OutlineWidth * 0.01`），所以后移量正好等于描边自身的外扩距离：
  描边壳上任何顶点都不会比原位更靠近相机 → 有表面挡着的地方必然被深度测试剔除；
  剪影处法线垂直于视线、后移不改变屏幕位置，描边依旧保留。

  只在「源材质的 `_OutlineWidthMask` 上真的挂了贴图」时生效，其它材质完全不动；
  原本 `_OutlineZOffset` 就更大的材质不会被改小。

### 新增

- **描边后移倍数可调**（默认 1）：菜单 `Tools > LilToNonToon Switcher > 描边后移倍数 >`
  可选 `0（不处理）/ 0.5 / 1 / 1.5 / 2`，设置窗口「高级设置」里也有 0–3 的滑条。
  调小＝描边更明显但凹处风险回升，调大＝更保险但描边可能被邻近几何吃掉。
- **转换方式三选一**（菜单 `Tools > LilToNonToon Switcher > 转换方式 >`，窗口里有同样的下拉）：
  1. **保留原材质 + 建切换开关**（默认，和以前一样）；
  2. **直接替换成 NonToon（原地替换）**：不建开关，回退只能靠 `Ctrl+Z` / 版本管理；
  3. **复制一份 `_nontoon` 后替换**：把选中对象复制成 `<名字>_nontoon`，材质换在副本上，
     **原对象保留 lilToon 材质但自动取消勾选**（想回退就把它勾回来）。

  第 3 种的细节：副本里会自动删掉之前生成的 `_NonToonSwitch`（否则副本里既有 NonToon 材质、
  又有会切回 lilToon 的开关，互相打架）；重复转换会复用已有的同名副本；副本创建与取消勾选都走 Undo；
  转换报告里会写明副本名字，并提示"场景里有两个 Avatar 描述符，上传时选 `_nontoon` 那一份"。
  副本是 `Instantiate` 出来的普通对象，不会挂回 prefab。

## [1.1.1] - 2026-09-15

### 修复

- **透明材质转完「该实心的地方透了、前后遮挡也乱了」**（1.1.0 引入）。
  1.1.0 判断出透明模式后，会把 `_ZWrite` 强制设成 0、Blend 设成 `SrcAlpha / OneMinusSrcAlpha`、
  渲染队列设成 2460 —— 但 lilToon 的混合方式 / `_ZWrite` / `_Cull` / `_AlphaToMask` / 渲染队列
  **全都是材质驱动的**（pass 里写的是 `Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]`、
  `ZWrite [_ZWrite]`，渲染队列也能被材质覆盖），作者经常故意调成
  「透明混合但仍然写深度、还待在几何队列里」（例如 MANUKA 的脸、头发就是
  `Blend One OneMinusSrcAlpha` + `_ZWrite 1` + 队列 2000 / 2450）。
  强制改成 NonToon 的默认值以后，这些部件就会透出背后的东西，遮挡顺序也跟着乱。

  现在 `_RenderingMode` 只负责 NonToon 怎么处理 alpha（不透明强制 1 / 镂空做剪切 / 透明保留），
  以下这些**全部沿用原材质**：

  ```
  _Cull、_SrcBlend、_DstBlend、_SrcBlendAlpha、_DstBlendAlpha、_ZWrite、_AlphaToMask
  渲染队列：原材质自己设过就照搬，否则用原 shader 声明的队列（透明 2460 / 镂空 2450 / 不透明 2000）
  ```

  只有原材质没有这些属性时，才退回 NonToon 自己那套模式默认值（和它的渲染模式下拉框一致）。
  转换日志会多一行 `渲染状态（沿用原材质）`，写明实际沿用了哪些值、队列是多少。

## [1.1.0] - 2026-09-15

### 修复

- **用 HDR 主色 / 色调校正改色的衣服，转换后颜色变回去了**：
  之前只把 `_MainTex × _Color` 烘焙进 `_BaseTexture`，而且 `_Color` 是白色（HDR 白也算白色）时直接沿用原贴图，
  于是 lilToon 的「色调校正」（HSV / Gamma + `_MainColorAdjustMask`）和「渐变映射」
  （`_MainGradationTex` / `_MainGradationStrength`）全都没了。
  现在按 lilToon 的处理顺序完整烘焙：

  ```
  贴图 → 色调校正 HSV/Gamma → 渐变映射 → 色调校正遮罩（lerp）→ × _Color（含 Alpha / HDR）
  ```

  数值全为默认时仍然直接沿用原贴图（不会白白生成一张 PNG）。
- **原来是「镂空 / 透明」的材质，转完变成不透明**：lilToon 的 Inspector 一旦选了渲染模式，
  材质会被换成对应的隐藏变体（`Hidden/lilToonCutout`、`Hidden/lilToonTransparentOutline`、
  `Hidden/lilToonTwoPassTransparent` …），这些材质的 `_TransparentMode` 往往还是 0，
  旧代码只看属性、不看 shader 名，于是判成 Opaque。
  现在先按 **shader 名字**判断，再退回属性；透明模式会一并把 `_ZWrite` 关掉（和 lilToon 的透明变体一致），
  Blend / RenderQueue 的数值与 NonToon 自己的渲染模式下拉框完全一致。
- **没有勾选 Read/Write 的贴图，读取时颜色空间不一致**：走 RenderTexture 兜底的那条路以前返回线性化后的数值，
  和 `GetPixels()` 的原始数值不一样，颜色乘算 / 色调校正会算得过暗。现在两条路径返回同一份数据。

### 新增

- 转换日志会写明渲染模式是**按 shader 名**判断出来的
  （例如 `rendering mode Cutout（按 shader 名判断：Hidden/lilToonCutout）`）；
  主色烘焙也会列出到底烘焙了哪几项（色调校正 / 渐变映射 / 遮罩 / 主色）。

## [1.0.3] - 2026-09-13

### 新增

- 菜单里新增「是否复用已有 `_NonToonSwitch`」的开关（`Tools > LilToNonToon Switcher >` 下两项互斥打勾）：
  - **转换时复用已有的 _NonToonSwitch**（默认）
  - **转换时总是新建 _NonToonSwitch**
  设置窗口里也有对应复选框，设置保存在 ProjectSettings 中

## [1.0.2] - 2026-09-13

### 修复

- **同一个 avatar 下重复转换不再堆积开关**：已存在 `_NonToonSwitch` 时改为把新的材质条目追加进去，
  菜单项也不会重复创建（之前每转换一次就新建一个开关，转几次就会出现几个）
- 没有 avatar 时，切换对象改为挂在公共祖先的父级／场景根，不再塞进被选中的对象内部
- 输出文件夹改为**递归创建**：`Assets/A/B/C` 这种多层路径即使中间目录都不存在也能建出来
  （之前会直接报「找不到上级目录」而中止转换）

## [1.0.1] - 2026-09-13

### 修复

- 发布流水线：把包校验与 VPM 索引生成抽成 `scripts/` 下的脚本，本地与 CI 共用同一份逻辑
- 发布流水线：gh-pages 任务改为在仓库根目录运行，修复找不到脚本导致的失败
- 发布流水线：Release 上传后校验附件确实存在，避免出现「只有标签、没有包」的情况
- `.meta` 文件统一使用 LF 换行

## [1.0.0] - 2026-09-13

首个版本。

### 新增

- 右键选中对象 → 批量把 lilToon 材质转换为 NonToon
- 转换结果保存为 `<原材质名>_nontoon.mat`，连带烘焙的贴图与渐变使用同一前缀
- 自动生成 Modular Avatar 的 **MA Material Setter**（对象 + 材质槽 + 材质）+ 菜单开关，实现一键切换 shader
- 也可选择 MA Material Swap 模式
- 识别 lilToon 全部 65 个 shader（含 `Hidden/lilToonOutline` 等隐藏变体）
- 属性转换：
  - `_MainTex` × `_Color` 烘焙为 PNG 写入 `_BaseTexture`
  - `_BumpMap` / `_BumpScale` → `_NormalMap` / `_NormalScale`
  - `_TransparentMode` → `_RenderingMode`（Opaque / Cutout / Transparent）+ Blend / RenderQueue
  - `_OutlineColor` / `_OutlineWidth` / `_OutlineZBias`
  - lilToon 的各个遮罩写入 `_SharedMask` 的对应通道
  - 由阴影色、边缘阴影色生成 Shader Core 的 Gradient（`.scgradients`）
  - `_Smoothness` → `_Roughness`、`_ReflectionColor` → `_SpecularColor`
  - Stencil / Cull / ZWrite / Blend 等渲染状态
- 转换日志（Console 与窗口）。未支持的功能以警告记录，不会静默丢弃
- 设置窗口（输出文件夹、开关默认状态、菜单名、各项烘焙开关）
- `Tools > LilToNonToon Switcher > 导出为 unitypackage`
- VPM 分发：推送 `v*` 标签自动打包、创建 Release 并更新 VPM 索引

### 修复

- 解决 Shader Core 的 shader 上 `Material.SetInt` 不落盘的问题（整型属性改走 SerializedObject 读写）
- 解决烘焙资源 import 导致材质内存值回滚的问题（保存后重新取回实例）
- 修复 RimShade 渐变索引越界导致边缘发黑的问题
- 修复 Material Setter 模式下原地替换材质、导致开关失效的问题
- 修复 `_ReflectionColor` 被 reflectance 压暗、高光几乎不可见的问题
