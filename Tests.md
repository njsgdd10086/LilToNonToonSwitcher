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
