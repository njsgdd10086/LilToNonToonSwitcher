**LilToNonToon Switcher 1.1.13** —— 修掉「衣服 / 配件被洗成黑白」。

## 原因

lilToon 的**色彩校正**（_MainTexHSVG，顺序是 色相|饱和|明度|Gamma）是被 shader keyword
**EFFECT_HUE_VARIATION** 门控的：关键字没开时，shader **根本不读**这串值，材质里存的可能只是残留。

实测那套白金服装：关键字是**关**的，而材质里存着 (0, 0, 1.4, 0.7) —— 饱和度 **0**。
我们之前只看值跟默认不一样就无条件烘进贴图，于是把整件洗成灰度 ✗ —— 而 lilToon 渲染出来是**金色**的。

现在只在关键字真的开着时才烘，否则保留原贴图颜色，并在日志说明：

`
· _MainTexHSVG (-0.37, 0, 1.4, 0.7)（源材质没开 EFFECT_HUE_VARIATION 关键字，lilToon 不会应用它）
    ->  不烘焙色彩校正，保留原贴图颜色
`

实测修复后 UV1_White_Gold：烘焙贴图饱和度 **33.4**（源 32.1）、RGB 与源**完全一致** ✓。

## 另一个修复：旧烘焙图残留

当这次转换**不需要**烘焙时，_BaseTexture 现在会**指回源贴图**。之前它会继续挂着上一次转换留下的
旧烘焙图 —— 有一件衣服因此即使已经不需要烘也还是灰的。

（需要烘透明遮罩的 UV1~UV4 照常烘焙 ✓；不需要的 UV5_White、UV5_Default、BulletMat 直接指回源贴图 ✓。）

## 升级后

ALCOM 更新到 1.1.13 → **重新转换**那套服装的材质。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

`
https://njsgdd10086.github.io/vpm-listing/index.json
`
