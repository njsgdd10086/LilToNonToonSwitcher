## MatCap / 描边兼容修复

转换时自动通过 NonToon Modules 注册本插件附带的 SurfaceCompatibility 模块。该模块只对转换器写入启用标志的材质生效，不修改 NonToon 依赖包源码。

MatCap 两层分别保留开关、Normal/Add/Screen/Multiply 混合、RGBA、强度、独立 RGB 遮罩、模糊和光照参数。描边阶段按世界空间单位方向外扩，保留源宽度遮罩和顶点色模式；基础表面的法线不变。

更新插件后，对原 lilToon 对象重新执行转换即可更新已有输出材质。可以回到上一版插件并重新转换；移除本插件前，应先将材质切回原来的 lilToon。

### 验证

在安装 lilToon 2.4.1、NonToon 0.3.0、Shader Core 0.3.0 和 NonToon Modules 0.3.0 的图形版 Unity Editor 中，运行菜单：

~~~text
Tools / LilToNonToon Switcher / 验证 MatCap 与描边
~~~

测试使用程序化球体和内存材质，不需要模型、贴图资产、Unity MCP 或额外测试包。失败时抛出异常，每项结果也会写入 Console。图形环境需要正常支持 NonToon；这些检查不能代替 VRChat 实机或其他渲染管线验证。

包结构检查：

~~~text
python scripts/validate_package.py --root .
~~~

## 转换前体检（手工回归）

点下转换后、真正动手之前会先跑一次只读体检，检查两类「改材质 / 改 shader 都救不回来」的问题：

1. 渲染器 `Mesh Renderer → 照明 → 接受阴影` 没勾（这些部位在阴影里永远不会变暗）；
2. 材质渲染队列落在 Unity 的透明通道（**大于 2500**）。BIRP 的透明通道拿不到 `SHADOWS_SCREEN`，
   这些材质在阴影里会保持满亮度 —— 典型来源是源 lilToon 材质手工改过 `Custom Render Queue`，
   例如 `3000`（lilToon 的 `TwoPassTransparent` 默认只有 `2460`）。

命中时：Console 输出一段醒目富文本警告、弹窗确认（批处理下不弹窗），并写进转换结果。
转换器**不会**替你改渲染队列 —— 忠实照搬源材质的值，需要按提示手动改。

验证方法：

1. 选一个含上述情况的模型执行转换：转换开始前就应该弹出警告窗，Console 里有同内容的彩色色块。
   弹窗有**两个按钮**：「继续转换」/「停止转换」；点右上角的叉也按「停止」处理。
   选「停止」后：不应产生任何新材质/开关，**也不应再弹任何确认框**（一次点击就结束，只留一行 Console 日志）。
2. 转换结果里该材质的 `renderQueue` 应与源材质**一致**（例如仍是 `3000`，不会被自动改掉）。
3. 按提示把该材质的 Render Queue 手动改到 `≤2500`（Shader Core 下拉框选 AlphaTest=2450，或直接填 2500），
   再到 `lilAvatarUtils → 光照 → 在阴影中` 确认它与脸、头发一样被压暗。

## 平铺法线（真实肤质材质）回归

背景：NonToon 的主 `_NormalMap` 不乘 `_NormalMap_ST`，源材质用平铺法线（如 `_BumpMap_ST = 80,80`）时
会被按 1× 采样，细密皮肤纹理变成"鳄鱼皮"。转换器现在的处理：`_NormalMap` 照接、`_NormalScale` 置 0，
细节交给 Details 模块，并在日志里说明。

1. 选一个带平铺法线的材质（材质面板里法线贴图的 Tiling 不是 1,1）执行转换。
2. 检查结果材质的 `_NormalScale` 应为 **0**；`_NormalMap` 仍有贴图（**不是 null**，否则会过曝）；
   `_jp_lilxyzw_nontoon_details_Detail0Texture_ST` 应等于源材质的 `_BumpMap_ST`。
3. 渲染对比：皮肤应恢复平滑（与 lilToon 接近），不再出现鳞片/鳄鱼皮纹理。
4. 普通材质（`_BumpMap_ST = 1,1`）回归：`_NormalScale` 仍等于源的 `_BumpScale`，法线正常生效。

## 多层主色烘焙 / 恢复（手工回归）

NonToon 只有一层主色，lilToon 的 2、3 层主色转换后会丢失。`LilToonBakeUtility` 用 lilToon 自己的烘焙
（`Hidden/ltsother_baker` + `RunBake`）把它们合并进 `_MainTex`，并支持一键还原。

准备：选一个含 2 或 3 层主色的模型（材质面板「主色 2nd / 3rd」有勾选）。

1. 选中对象 → `Tools/LilToNonToon Switcher/一键烘焙 lilToon 多层主色（合并 2/3 层）` → 确认。
2. 检查烘焙结果：
   - 每个材质旁边多出 `<材质名>_baked.png`；
   - 材质上 `_Color` 变白、`_MainTexHSVG` = (0,1,1,1)、`_UseMain2ndTex` / `_UseMain3rdTex` = 0；
   - `_MainTex` 指向 `_baked.png`，画面里 2、3 层的花纹还在（已经进贴图了）。
3. 备份文件应出现在输出文件夹下：`_LilToonBakeBackup.json`（条目数 = 烘焙成功的材质数）。
4. 点 `一键恢复烘焙前的 lilToon 材质` →
   `_MainTex` 换回原贴图、`_Color` / HSVG / 渐变 / 2、3 层全部还原，备份文件条目清空。
   Console 里每个材质会有一行复核结果；若出现 `⚠ 复核不一致：…` 说明有属性没写回去，需要记录。
5. 若烘焙后又**转换**过：恢复只回滚 lilToon 材质，Console 会点名"还在用烘焙贴图的 NonToon 材质"，
   对这些材质**再执行一次转换**才会回到恢复前的效果（恢复不会自动改它们）。
6. 边界情况：没有主贴图的材质、不是项目资产的材质（场景内临时材质）、全部为默认值的材质，
   都应"跳过"并在 Console 说明原因，不会中断批量流程。
7. 失败保留：把某个材质资产移走后点恢复，该条目应留在备份里（下次还能重试）。
