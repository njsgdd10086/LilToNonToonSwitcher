**LilToNonToon Switcher 1.1.1** —— 修掉 1.1.0 带进来的新问题：**透明材质转完「该实心的地方透了、前后遮挡也乱了」**。

## 问题原因

1.1.0 判断出材质是透明模式后，会按 NonToon 自己的渲染模式下拉框去设置：

```
Blend SrcAlpha / OneMinusSrcAlpha、_ZWrite 0、队列 2460
```

但 **lilToon 的混合方式、ZWrite、Cull、AlphaToMask、渲染队列全都是材质驱动的**：

```hlsl
Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]
ZWrite [_ZWrite]
Cull [_Cull]
AlphaToMask [_AlphaToMask]
```

很多作者（包括 MANUKA 的脸、头发）会故意把它调成
「透明混合但仍然写深度、还待在几何队列里」——例如
`Blend One OneMinusSrcAlpha` + `_ZWrite 1` + 队列 2000 / 2450。
强制改成 NonToon 默认值以后，这些部件就透出了背后的东西，遮挡顺序也跟着乱。

## 现在的做法

- `_RenderingMode` 只负责 NonToon 怎么处理 alpha：不透明强制 1 / 镂空做剪切 / 透明保留；
- 下面这些**全部沿用原材质**：

```
_Cull、_SrcBlend、_DstBlend、_SrcBlendAlpha、_DstBlendAlpha、_ZWrite、_AlphaToMask
渲染队列：原材质自己设过就照搬，否则用原 shader 声明的队列（透明 2460 / 镂空 2450 / 不透明 2000）
```

- 只有原材质没有这些属性时，才退回 NonToon 那套模式默认值；
- 转换日志里会多一行 `渲染状态（沿用原材质）`，写明沿用了哪些值、队列是多少，方便核对。

## 升级后请这样做

1. ALCOM 更新到 1.1.1（索引里已经是这个版本）；
2. **重新转换**头部那几件出问题的材质（不会动原 lilToon 材质，直接再转一次覆盖同名输出即可）；
3. 在 Unity 里看一眼：该实心的地方是否实心了、前后遮挡是否正常。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
