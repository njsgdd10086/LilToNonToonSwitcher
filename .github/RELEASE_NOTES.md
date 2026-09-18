**LilToNonToon Switcher 1.1.11** —— 修掉「凭空多出的描边」（很可能就是切到 NonToon 后视角被东西挡住的原因）。

## 问题：没有描边的材质被加上了一圈描边

lilToon「有没有描边」是**靠 shader 变体**区分的：

| shader | 有没有描边 |
| --- | --- |
| Hidden/lilToonOutline、Hidden/lilToonTransparentOutline | 有 |
| Hidden/lilToon、_lil/lilToonMulti、Hidden/lilToonMultiRefraction … | **没有** |

没有描边的变体里，_OutlineWidth 只是作者调过的**残留值**（这些材质里是 0.08）。
我们之前无条件照搬它，于是给花瓣、纸片、装饰这类材质凭空加了一圈描边（再乘上对象缩放）。

**为什么会在 VR 里挡住视角**：这圈描边壳是**不透明**的（混合 One / Zero），
而它所在的透明/特效层往往就在头脸附近 —— 切到 NonToon 之后它突然出现，看起来就像有东西糊住视野。

现在按 shader 名判断，不含 Outline 的变体写 _OutlineWidth = 0：

`
· 描边（源 shader「Hidden/lilToonMultiRefraction」没有描边变体，_OutlineWidth=0.08 是残留值）  ->  _OutlineWidth = 0（不加描边）
`

有描边的变体不受影响（M_Hair 0.072、Chocolat_Costume 0.07 都照旧）。

## 另外：折射材质

Hidden/lilToonMultiRefraction（折射）NonToon 没有对应实现，会按普通**不透明**层渲染 ——
注意这类材质的混合本来就是 One / Zero（不透明），它原本是靠**折射扭曲**看起来透的。
转换日志里会明确提示；这类材质建议保持 lilToon、或转换后手动调成半透明。

## 升级后

ALCOM 更新到 1.1.11 → **重新转换**材质（描边宽度是转换期写入的）。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

`
https://njsgdd10086.github.io/vpm-listing/index.json
`
