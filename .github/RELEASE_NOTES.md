**LilToNonToon Switcher 1.1.9** —— 跟另一个 lilToon→NonToon 转换插件**逐属性对拍**（16 对材质）后找出并修掉的 4 个问题。

## 一、阴影 / 边缘阴影的渐变索引串位（最明显）

源材质只用了「边缘阴影」没用「阴影」时（`_UseShadow = 0`、`_UseRimShade = 1`），
烘焙出来只有一条渐变、它的切片索引是 `0` —— 而我们按"位置"把第一个切片当成 **Shade** 的索引：

```
Shade 模块   → 采到了边缘阴影的渐变 ✗
RimShade 模块 → 被关掉（-1）✗
```

现在烘焙时按模块分别记索引，实测：

```
M_HairOutline:  源 _UseShadow=0 _UseRimShade=1  →  ShadeIdx=-1  RimShadeIdx=0  ✓
```

## 二、凭空多出的高光

源材质 `_UseReflection = 0`（lilToon 根本不画高光）时，我们直接跳过映射，
把 NonToon 的默认 `_Roughness = 0.5` 留在那里 —— 等于**多加了一个高光**。
现在这种情况写 `_Roughness = 1`（无高光）。受影响的是绝大多数材质
（`M_Alpha_1/2`、`M_Bandage`、`M_Body_*`、`M_Hair*`、`M_Eye`、`M_Shoes` …）。

## 三、法线强度照搬了无效值

lilToon 的 `_BumpScale` 在**没挂法线贴图**时完全不参与渲染，但作者往往调过（实测有 `8.17`）。
照搬到 NonToon 会把默认白贴图当法线放大 —— 现在没挂贴图时写 `_NormalScale = 0`。

## 四、alpha 通道的混合系数被误解

1.1.7 加预乘 alpha 换算时，把 `_SrcBlendAlpha / _DstBlendAlpha` 也一起换成了 `SrcAlpha` ✗。
但 lilToon 预乘的是**颜色**通道，alpha 通道并没有预乘（写成 SrcAlpha 会让目标 alpha 变成 `a²`），
现在只换算 `_SrcBlend`。

## 顺带确认：这些差异是**对方**的 bug

对拍时逐项回源材质核对过，以下不是我们的问题：

| 项目 | 源材质 | 我们 | 另一个插件 |
| --- | --- | --- | --- |
| `_OutlineWidth`（6 个材质） | 0.112 | 0.112 ✓ | **0**（描边整个丢了） |
| `_OutlineColor`（5 个材质） | (0.783,0.544,0.513) | 同源 ✓ | (0.6,0.45,0.55)（NonToon 默认色） |
| Stencil（8 个材质） | `_StencilRef=146` | 照搬 ✓ | **全部清零**（lilToon 确实用 stencil 做遮罩） |
| `M_HairShadow` 混合 | `Zero / SrcColor` | 0/3 ✓ | 5/10（正片叠底变普通 alpha） |
| `_OutlineVertexR2Width = 2` | 顶点色当描边方向 | `_OutlineFromVertexColor=1` ✓ | 0 |
| `_MatCapColor` | 源值 | 源值 ✓ | 默认 (1,1,1,1) |

## 升级后

ALCOM 更新到 1.1.9 → **重新转换**全部材质（这一版的修复都是转换期写入的）。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
