#!/usr/bin/env python3
"""由 GitHub Release 资产生成 VPM 仓库索引（vpm.json / index.json）。

用法：
    python scripts/make_vpm_repo.py --releases releases-a.json releases-b.json \
        --repository owner/repo --output-dir pages

`releases-*.json` 是 GitHub API `/repos/{owner}/{repo}/releases` 的返回内容，
可以传多个仓库的结果，脚本会把它们合并成同一份「作者总仓库」索引。
生成的索引里，每个包版本的 url 直接指向 Release 资产（browser_download_url），
这样 VCC / ALCOM 下载时走的是 GitHub Release，不依赖 GitHub Pages 的可用性。
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re

REPO_NAME = "ATRI_NAIXU VPM Packages"
AUTHOR_NAME = "ATRI_NAIXU"

# 每个包在 VCC / ALCOM 里显示的信息
PACKAGE_INFO = {
    "com.nontoon.switcher": {
        "displayName": "LilToNonToon Switcher",
        "description": "右键把 lilToon 材质一键转换为 NonToon（输出 <名称>_nontoon.mat），"
                       "并自动生成 Modular Avatar 的 MA Material Setter + 菜单开关。",
    },
    "com.atrinaxu.nontoon.lightlimit": {
        "displayName": "NonToon Light Limit",
        "description": "给 NonToon 加上亮度上下限与亮度倍数，并支持全局统一控制，"
                       "做 Light Limit Changer 式的亮度调节；另附一键生成全局亮度动画 + 表情菜单滑块的小工具。",
    },
}
DEFAULT_UNITY = "2022.3"

# 资产名规则：<包名>-<版本>.zip，包名里含连字符，所以从右侧取版本
ASSET_PATTERN = re.compile(r"^(?P<name>.+)-(?P<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)\.zip$")
# 从 release 的 API url 里取出它属于哪个仓库
REPO_PATTERN = re.compile(r"repos/(?P<slug>[^/]+/[^/]+)/releases")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--releases", required=True, nargs="+",
                        help="GitHub releases API 返回的 JSON 文件（可以传多个仓库的结果）")
    parser.add_argument("--repository", required=True, help="索引所在仓库 owner/repo")
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()

    login, repo_name = args.repository.split("/", 1)

    packages: dict[str, dict] = {}
    for path in args.releases:
        # utf-8-sig 兼容带 BOM 的 JSON（例如用 PowerShell 写出来的文件）
        releases = json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))
        for release in releases:
            if release.get("draft"):
                continue

            match_repo = REPO_PATTERN.search(release.get("url", ""))
            slug = match_repo.group("slug") if match_repo else args.repository

            for asset in release.get("assets", []):
                match = ASSET_PATTERN.match(asset["name"])
                if not match:
                    continue
                pkg_name = match.group("name")
                version = match.group("version")
                info = PACKAGE_INFO.get(pkg_name, {})
                packages.setdefault(pkg_name, {"versions": {}})["versions"][version] = {
                    "name": pkg_name,
                    "displayName": info.get("displayName", pkg_name),
                    "version": version,
                    "unity": info.get("unity", DEFAULT_UNITY),
                    "description": info.get("description", ""),
                    "url": asset["browser_download_url"],
                    "repo": f"https://github.com/{slug}",
                    "author": {"name": AUTHOR_NAME, "url": f"https://github.com/{login}"},
                }

    vpm = {
        "name": REPO_NAME,
        "id": f"com.{login}.vpm-repo",
        "url": f"https://{login}.github.io/{repo_name}/index.json",
        "author": {"name": AUTHOR_NAME, "url": f"https://github.com/{login}"},
        "packages": packages,
    }

    text = json.dumps(vpm, ensure_ascii=False, indent=2) + "\n"
    output = pathlib.Path(args.output_dir)
    output.mkdir(parents=True, exist_ok=True)
    # VCC / ALCOM 读 index.json；vpm.json 作为兼容别名一并保留
    (output / "index.json").write_text(text, encoding="utf-8")
    (output / "vpm.json").write_text(text, encoding="utf-8")

    total = sum(len(pkg["versions"]) for pkg in packages.values())
    print(f"已生成索引：包 {len(packages)} 个，版本 {total} 个")
    for pkg_name, pkg in packages.items():
        print(f"  {pkg_name}: {', '.join(sorted(pkg['versions']))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
