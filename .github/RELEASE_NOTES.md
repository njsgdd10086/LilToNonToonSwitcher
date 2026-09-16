**LilToNonToon Switcher 1.1.7** —— 修掉「半透明层转完变成又厚又实的一块」（开"害羞脸红"表情时脸上的硬边色块就是这个）。

## 原因：两个 shader 对透明度的处理方式不同

| | lilToon | NonToon |
| --- | --- | --- |
| 片元 | **预乘 alpha**：`rgb *= alpha` | 不预乘 |
| 混合 | `Blend One OneMinusSrcAlpha`（`_SrcBlend = 1`） | 照搬同一组系数 |

照搬 `One / OneMinusSrcAlpha` 时，NonToon 会把颜色**按原样叠上去** ——
半透明的腮红层就变成了一块实心色块，边界一刀切（你截图里那条硬边就是这么来的）。

数学上 lilToon 输出的是 `rgb·a + dst·(1−a)`；把源系数换成 `SrcAlpha(5)` 之后，
NonToon 输出的**完全一样**。所以转换时现在会自动做这个等价换算，并写进日志：

```
· 渲染状态（沿用原材质）  ->  Cull=2、SrcBlend=5、DstBlend=10、…（lilToon 预乘 alpha → NonToon 用 SrcAlpha 等价换算）
```

只在「源系数 = `One` **且** 目标系数 ≠ `Zero`」时换算；不透明的 `One / Zero` 保持原样
（否则会把 alpha 也乘进去，不透明材质会变暗）。

## 影响面

lilToon 的透明材质基本都是这套系数 —— 示例工程里就有 **17 个**：
脸上的特效层（腮红）、`Chocolat_Hair`、所有 `smooth white / black ring / planet`、
`Costume`、`Face_transparent` 等等。**重新转换后**全部生效。

## 升级后

ALCOM 更新到 1.1.7 → **重新转换**透明类材质（脸、头发、配件等）。
不透明材质（`One / Zero`）不受影响，转不转都一样。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
