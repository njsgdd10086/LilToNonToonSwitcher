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
2. 转换结果里该材质的 `renderQueue` 应与源材质**一致**（例如仍是 `3000`，不会被自动改掉）。
3. 按提示把该材质的 Render Queue 手动改到 `≤2500`（Shader Core 下拉框选 AlphaTest=2450，或直接填 2500），
   再到 `lilAvatarUtils → 光照 → 在阴影中` 确认它与脸、头发一样被压暗。
