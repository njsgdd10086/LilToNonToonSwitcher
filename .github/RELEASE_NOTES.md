**LilToNonToon Switcher 1.1.16** —— 放宽依赖范围，兼容 NonToon 0.2.0 / Shader Core 0.2.0

## [1.1.16] - 2026-10-02

### 变更

- **放宽依赖范围**：`jp.lilxyzw.shadercore` / `jp.lilxyzw.nontoon` 由 `^0.1.5` / `^0.1.3` 改成
  `>=0.1.5` / `>=0.1.3`，`com.nontoon.modules` 改成 `>=0.1.0` ✓ ——
  否则 NonToon 0.2.0 一发布，本插件就会因为版本冲突装不上 ✗。

### 兼容性说明

逐文件对比了 NonToon 0.1.3 → 0.2.0 与 Shader Core 0.1.12 → 0.2.0：转换器与织物模块依赖的内部细节都没变 ✓
（phase 钩子、`ProjectSettings` 白名单 API、`SCConstValue` 关键字机制、`SCShaderImporter` 全部一致 ✓），
所以本版**代码无变化**，只是依赖范围放开。NonToon 0.2.0 的新功能是接入 VRC Light Volumes ✓（对我们无影响 ✓）。
