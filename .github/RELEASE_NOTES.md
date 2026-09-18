**LilToNonToon Switcher 1.1.13** —— 修掉「白金色服装的金饰变成灰色」（MatCap / 金属质感整片失效）。

## 三个叠加的原因

**1. Shader Core 的模块开关要写「关键字」**

NonToon 每个模块的开关带 `SCConstValue`，真正让模块生效的是材质上的关键字
`<属性名大写>_<值>`（MatCap 就是 `_JP_LILXYZW_NONTOON_MATCAPS_ENABLE_1`）。
我们以前只写了 `_Enable` 这个整数、没加关键字 ✗ ——
表现就是「开关明明是勾着的，却要手动在 Inspector 里取消再勾一次才亮」✗。现在两者都写 ✓。

**2. MatCap 混合模式理解反了**

lilToon 的 `lilBlendColor`：

```
0 = Normal（直接用 matcap 颜色替换）   1 = Add   2 = Screen   3 = Multiply
```

我们以前把 **0（Normal）**丢进了 Multiply ✗ —— 白布 × 金色 matcap 会算成发灰 ✗。
现在 0/1/2 走 **MatCap (Add)**（叠加最接近「替换」），只有 3 才用 Multiply，
并**清空另一个槽**（材质是复用的，上次留在里面的贴图会双重生效）。

**3. 遮罩通道与模块读取的通道不一致**

MatCap 的遮罩以前写死挂到 `MatCapMultiply` 模块 ✗，而贴图其实放在 `MatCapAdd` ✗ →
数据烘进 R 通道、模块却读 A ✗（被乘成 ~0）。现在按实际槽位分配通道，
写回模块并读回校验；同时去掉了第二层 MatCap 的无用遮罩（它没转、却会占通道并改模块通道）。

另外还修了：转换后**强制重新导入材质**（Shader Core 的模块状态需要刷新）、
不需要烘焙时 `_BaseTexture` **指回源贴图**（不再残留旧烘焙图）、MatCap 颜色**总是写入**。

## 已知限制：金属反射

lilToon 的 `_UseReflection`（`_Metallic` / `_Smoothness` / 环境反射）NonToon 没有对应能力，
只能近似成一个高光 —— 所以「靠反射变金」的部分（帽子的**羽毛 / 玫瑰花**）转换后会比原版**偏灰** ✓。
需要完全一致的话，这部分建议保留 lilToon 材质、或手动把 NonToon 的 Specular 调暖 + 降低 Roughness 近似。

## 升级后

ALCOM 更新到 1.1.13 → **重新转换**受影响的材质（用了 MatCap / 金属感的那些）。

## 安装 / 升级

VCC / ALCOM 仓库地址（总仓库，本插件与 NonToon Light Limit 都在这份索引里）：

```
https://njsgdd10086.github.io/vpm-listing/index.json
```
