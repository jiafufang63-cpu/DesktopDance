#!/usr/bin/env python3
"""英文说明（README.md，GitHub 首页）里不许混进中文。每次推送由 GitHub 自动跑，也可以手动运行：
    python3 tools/check-readme-en.py
检查两件事：
1. 正文里不能有中文字。例外只有两处：最上面切换语言的那一行，和原作的名字「桌面蹦迪」。
2. docs/ 里凡是有英文版的图（文件名带 -en），英文说明必须用英文版，不能用中文版。
"""
import os, re, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
path = os.path.join(root, "README.md")
text = open(path, encoding="utf-8").read()
ALLOWED = ["[中文](README.zh-CN.md)", "桌面蹦迪"]
cjk = re.compile(r"[　-〿㐀-鿿＀-￯]")
problems = []

for n, line in enumerate(text.split("\n"), 1):
    stripped = line
    for a in ALLOWED:
        stripped = stripped.replace(a, "")
    if cjk.search(stripped):
        problems.append(f"第 {n} 行有中文：{line.strip()[:80]}")

docs = set(os.listdir(os.path.join(root, "docs")))
for ref in re.findall(r"docs/([A-Za-z0-9_.-]+)", text):
    stem, ext = os.path.splitext(ref)
    if not stem.endswith("-en") and f"{stem}-en{ext}" in docs:
        problems.append(f"用了中文版的图 docs/{ref}，应该用 docs/{stem}-en{ext}")
    if ref not in docs:
        problems.append(f"引用的图不存在：docs/{ref}")

if problems:
    print("README.md 没通过检查：")
    for p in problems:
        print("  -", p)
    sys.exit(1)
print("README.md 检查通过：没有混进中文，图片都是英文版。")
