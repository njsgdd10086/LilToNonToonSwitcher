#!/usr/bin/env python3
"""校验 Unity 包结构与版本号。

用法：
    python scripts/validate_package.py              # 只校验结构
    python scripts/validate_package.py --tag v1.0.0 # 同时校验标签与版本一致
    python scripts/validate_package.py --github-output  # 额外输出 version / zipname 到 GITHUB_OUTPUT

被 .github/workflows/validate.yml 与 release.yml 共用，本地也可以直接跑来验证。
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import sys

REQUIRED_FIELDS = ("name", "version", "displayName", "unity", "license")
REQUIRED_PATHS = ("README.md", "LICENSE", "CHANGELOG.md", "package.json", "Editor")


def fail(message: str) -> None:
    print(f"[失败] {message}", file=sys.stderr)
    sys.exit(1)


def count_braces(text: str) -> tuple:
    """数花括号，但跳过字符串 / 字符字面量 / 注释里的（正则里的 \\{ 之类不然会误报）。"""
    opens = closes = 0
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        # 行注释
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            i = text.find("\n", i)
            if i < 0:
                break
            continue
        # 块注释
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        # 字符串（含逐字字符串 @"..."，里面的 "" 是一个转义引号）
        if ch == "@" and i + 1 < n and text[i + 1] == '"':
            i += 2
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue
        if ch == '"':
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        # 字符字面量
        if ch == "'":
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        if ch == "{":
            opens += 1
        elif ch == "}":
            closes += 1
        i += 1
    return opens, closes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".", help="包根目录")
    parser.add_argument("--tag", default="", help="要校验的标签，例如 v1.0.0")
    parser.add_argument("--github-output", action="store_true", help="把结果写入 GITHUB_OUTPUT")
    args = parser.parse_args()

    root = pathlib.Path(args.root).resolve()
    manifest = root / "package.json"
    if not manifest.exists():
        fail(f"找不到 {manifest}")

    try:
        # utf-8-sig 兼容带 BOM 的 JSON（例如用 PowerShell 写出来的文件）
        data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as exc:
        fail(f"package.json 不是合法 JSON: {exc}")

    for field in REQUIRED_FIELDS:
        if not data.get(field):
            fail(f"package.json 缺少字段 {field}")

    for path in REQUIRED_PATHS:
        if not (root / path).exists():
            fail(f"缺少 {path}")

    version = data["version"]
    name = data["name"]

    # 每个 .cs 都要有对应的 .meta，否则编辑器导入时 GUID 会变
    missing_meta = [
        path.name
        for path in sorted((root / "Editor").glob("*.cs"))
        if not path.with_name(path.name + ".meta").exists()
    ]
    if missing_meta:
        fail("以下脚本缺少 .meta：" + ", ".join(missing_meta))

    sources = sorted((root / "Editor").glob("*.cs"))
    if not sources:
        fail("Editor/ 下没有 .cs 文件")

    for path in sources:
        text = path.read_text(encoding="utf-8")
        opens, closes = count_braces(text)
        if opens != closes:
            fail(f"{path.name} 花括号不匹配（{{={opens} }}={closes}）")

    tag = args.tag
    if tag.startswith("v") and tag[1:] != version:
        fail(f"标签 {tag} 与 package.json 的版本 {version} 不一致")

    zip_name = f"{name}-{version}.zip"
    print(f"[通过] {name} {version}（Unity {data['unity']}，{data['license']}），"
          f"源码 {len(sources)} 个，标签 {tag or '未校验'}，压缩包名 {zip_name}")

    if args.github_output:
        output_path = os.environ.get("GITHUB_OUTPUT")
        if output_path:
            with open(output_path, "a", encoding="utf-8") as handle:
                handle.write(f"version={version}\n")
                handle.write(f"zipname={zip_name}\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
