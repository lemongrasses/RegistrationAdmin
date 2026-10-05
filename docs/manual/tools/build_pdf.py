# -*- coding: utf-8 -*-
"""把《使用手冊》與《部署手冊》的 Markdown 轉成 HTML，再用 Chrome／Edge 無頭模式輸出同名 PDF。

用法：python docs/manual/tools/build_pdf.py
只依賴 Python 標準函式庫；只處理手冊用到的 Markdown 語法
（標題、段落、清單、表格、程式碼區塊、引言、圖片、連結、粗體、行內程式碼）。
"""
import html
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MANUAL_DIR = os.path.dirname(HERE)
MANUALS = ["使用手冊", "部署手冊"]
BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

CSS = """
@page { size: A4; margin: 16mm 14mm 18mm 14mm; }
body { font-family: "Microsoft JhengHei UI", "Microsoft JhengHei", "Noto Sans TC", sans-serif;
       font-size: 10.5pt; line-height: 1.65; color: #1f2933; }
h1 { font-size: 20pt; border-bottom: 3px solid #2563eb; padding-bottom: 6px; margin-top: 0; page-break-before: always; }
h1:first-of-type { page-break-before: avoid; font-size: 22pt; }
h2 { font-size: 15pt; color: #1d4ed8; margin-top: 22px; border-bottom: 1px solid #dbe3ef; padding-bottom: 3px; }
h3 { font-size: 12.5pt; margin-top: 18px; }
h2, h3 { page-break-after: avoid; }
table { border-collapse: collapse; width: 100%; margin: 8px 0 14px; font-size: 9.5pt; page-break-inside: auto; }
tr { page-break-inside: avoid; }
th, td { border: 1px solid #cfd8e3; padding: 4px 7px; vertical-align: top; text-align: left; }
th { background: #eef3fb; }
th:first-child { white-space: nowrap; }
img { max-width: 100%; border: 1px solid #cfd8e3; border-radius: 6px; }
p.figure { text-align: center; margin: 10px 0 4px; page-break-inside: avoid; }
td img { border: none; max-height: 34px; }
pre { background: #f4f6fa; border: 1px solid #dbe3ef; border-radius: 6px; padding: 8px 10px; font-size: 9pt;
      white-space: pre-wrap; page-break-inside: avoid; font-family: Consolas, "Microsoft JhengHei UI", monospace; }
code { font-family: Consolas, "Microsoft JhengHei UI", monospace; background: #f1f4f9; padding: 0 3px; border-radius: 3px; font-size: 9.5pt; }
pre code { background: none; padding: 0; }
blockquote { margin: 10px 0; padding: 6px 12px; background: #fff8e6; border-left: 4px solid #f0b429; }
hr { border: none; border-top: 1px solid #dbe3ef; margin: 18px 0; }
a { color: #1d4ed8; text-decoration: none; }
"""


def inline(text):
    parts = re.split(r"(`[^`]+`)", text)
    out = []
    for part in parts:
        if part.startswith("`") and part.endswith("`") and len(part) > 1:
            out.append("<code>" + html.escape(part[1:-1]) + "</code>")
            continue
        s = html.escape(part, quote=False)
        s = re.sub(r"!\[([^\]]*)\]\(([^)]+)\)", r'<img src="\2" alt="\1">', s)
        s = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r'<a href="\2">\1</a>', s)
        s = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", s)
        out.append(s)
    return "".join(out)


def convert(md):
    lines = md.splitlines()
    out = []
    i = 0
    para = []

    def flush():
        if para:
            text = " ".join(para)
            cls = ' class="figure"' if re.fullmatch(r"!\[[^\]]*\]\([^)]+\)", text.strip()) else ""
            out.append(f"<p{cls}>{inline(text)}</p>")
            para.clear()

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if stripped.startswith("```"):
            flush()
            i += 1
            code = []
            while i < len(lines) and not lines[i].strip().startswith("```"):
                code.append(lines[i])
                i += 1
            out.append("<pre><code>" + html.escape("\n".join(code)) + "</code></pre>")
            i += 1
            continue
        if not stripped:
            flush()
            i += 1
            continue
        m = re.match(r"(#{1,4})\s+(.*)", line)
        if m:
            flush()
            n = len(m.group(1))
            out.append(f"<h{n}>{inline(m.group(2))}</h{n}>")
            i += 1
            continue
        if stripped == "---":
            flush()
            out.append("<hr>")
            i += 1
            continue
        if stripped.startswith("|"):
            flush()
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(lines[i].strip())
                i += 1
            cells = lambda r: [c.strip() for c in r.strip("|").split("|")]
            out.append("<table><thead><tr>" + "".join(f"<th>{inline(c)}</th>" for c in cells(rows[0])) + "</tr></thead><tbody>")
            for r in rows[2:]:
                out.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in cells(r)) + "</tr>")
            out.append("</tbody></table>")
            continue
        if stripped.startswith(">"):
            flush()
            quote = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                quote.append(lines[i].strip()[1:].strip())
                i += 1
            out.append("<blockquote>" + inline(" ".join(quote)) + "</blockquote>")
            continue
        if re.match(r"(\d+\.|-)\s", stripped) and not line.startswith(" "):
            flush()
            ordered = bool(re.match(r"\d+\.", stripped))
            tag = "ol" if ordered else "ul"
            out.append(f"<{tag}>")
            while i < len(lines) and (re.match(r"(\d+\.|-)\s", lines[i]) or lines[i].startswith("   ")):
                item = re.sub(r"^(\d+\.|-)\s+", "", lines[i])
                i += 1
                sub = []
                while i < len(lines) and lines[i].startswith("   ") and lines[i].strip():
                    sub.append(lines[i].strip())
                    i += 1
                html_item = inline(item)
                if sub:
                    subs = [inline(re.sub(r"^-\s+", "", s)) for s in sub]
                    html_item += "<ul>" + "".join("<li>" + s + "</li>" for s in subs) + "</ul>"
                out.append(f"<li>{html_item}</li>")
            out.append(f"</{tag}>")
            continue
        para.append(stripped)
        i += 1
    flush()
    return "\n".join(out)


def build(name, browser):
    src = os.path.join(MANUAL_DIR, name + ".md")
    html_out = os.path.join(MANUAL_DIR, name + ".html")
    pdf_out = os.path.join(MANUAL_DIR, name + ".pdf")
    with open(src, encoding="utf-8") as f:
        md = f.read()
    title = re.search(r"^# (.+)$", md, re.M).group(1)
    doc = ('<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8">'
           "<title>" + html.escape(title) + "</title><style>" + CSS + "</style></head><body>"
           + convert(md) + "</body></html>")
    with open(html_out, "w", encoding="utf-8") as f:
        f.write(doc)
    url = "file:///" + html_out.replace("\\", "/")
    subprocess.run([browser, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                    "--print-to-pdf=" + pdf_out, url], check=True, timeout=120)
    os.remove(html_out)
    print("wrote", pdf_out)


def main():
    browser = next((b for b in BROWSERS if os.path.exists(b)), None)
    if browser is None:
        sys.exit("找不到 Chrome 或 Edge")
    for name in MANUALS:
        build(name, browser)


if __name__ == "__main__":
    main()
