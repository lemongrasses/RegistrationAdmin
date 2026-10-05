"""產生程式圖示 app.ico（16～256 多尺寸）與預覽圖。

用法：python assets/icon/make_icon.py
需要 Pillow。輸出：
  src/RegistrationAdmin.App/Assets/app.ico
  assets/icon/app-256.png、assets/icon/preview.png
"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ICO_PATH = ROOT / "src" / "RegistrationAdmin.App" / "Assets" / "app.ico"
OUT_DIR = Path(__file__).resolve().parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# 與 Themes/Theme.xaml 的主色一致
BLUE_TOP = (59, 130, 246)
BLUE_BOTTOM = (29, 78, 216)
NAVY = (23, 37, 84)
WHITE = (255, 255, 255)
LINE = (203, 213, 225)
CHECK = (22, 163, 74)


def render(size: int) -> Image.Image:
    """以 8 倍解析度繪製後縮小，小尺寸用較少、較粗的元素以保持清晰。"""
    ss = 8
    n = size * ss
    small = size <= 24
    medium = size <= 48

    def p(v: float) -> float:
        return v * n

    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # 背景：圓角方塊＋垂直漸層
    inset = 0.03 if not small else 0.0
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        t = y / (n - 1)
        c = tuple(round(a + (b - a) * t) for a, b in zip(BLUE_TOP, BLUE_BOTTOM))
        gd.line([(0, y), (n, y)], fill=c + (255,))
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [p(inset), p(inset), p(1 - inset) - 1, p(1 - inset) - 1], radius=p(0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # 夾板
    bx0, by0, bx1, by1 = (0.22, 0.2, 0.78, 0.88) if small else (0.25, 0.2, 0.75, 0.86)
    if not medium:
        # 柔和陰影
        shadow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rounded_rectangle(
            [p(bx0), p(by0 + 0.02), p(bx1), p(by1 + 0.02)], radius=p(0.06), fill=(15, 23, 42, 70))
        img.alpha_composite(shadow)
    d.rounded_rectangle([p(bx0), p(by0), p(bx1), p(by1)], radius=p(0.06 if not small else 0.05), fill=WHITE)

    # 夾子
    cx0, cx1 = (0.36, 0.64) if small else (0.38, 0.62)
    d.rounded_rectangle([p(cx0), p(0.12), p(cx1), p(0.27)], radius=p(0.035), fill=NAVY)
    if not medium:
        d.ellipse([p(0.475), p(0.145), p(0.525), p(0.195)], fill=BLUE_TOP)

    # 勾選清單
    rows = [0.47, 0.70] if small else ([0.42, 0.57, 0.72] if not medium else [0.43, 0.60, 0.76])
    check_w = 0.075 if small else 0.05
    line_w = 0.07 if small else (0.045 if medium else 0.035)
    for i, ry in enumerate(rows):
        x = bx0 + (0.08 if small else 0.07)
        s = 0.07 if small else 0.055
        last_unchecked = i == len(rows) - 1 and not small
        if last_unchecked:
            # 最後一項尚未勾選：空心方框
            d.rounded_rectangle([p(x), p(ry - s), p(x + 2 * s * 0.85), p(ry + s * 0.7)],
                                radius=p(0.015), outline=LINE, width=max(1, round(p(0.022))))
        else:
            d.line([(p(x), p(ry - s * 0.1)), (p(x + s * 0.6), p(ry + s * 0.55)),
                    (p(x + s * 1.75), p(ry - s * 0.9))],
                   fill=CHECK, width=round(p(check_w)), joint="curve")
        lx0 = x + (0.2 if small else 0.15)
        lx1 = bx1 - (0.08 if small else 0.07) - (0.06 if i == 1 and not small else 0)
        d.rounded_rectangle([p(lx0), p(ry - line_w / 2), p(lx1), p(ry + line_w / 2)],
                            radius=p(line_w / 2), fill=LINE)

    return img.resize((size, size), Image.LANCZOS)


def bmp_entry(im: Image.Image) -> bytes:
    """32 位元 BGRA DIB（小尺寸用，相容性最好）。"""
    w, h = im.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    px = im.tobytes("raw", "BGRA")
    rows = [px[y * w * 4:(y + 1) * w * 4] for y in range(h)]
    xor = b"".join(reversed(rows))
    mask_row = ((w + 31) // 32) * 4
    andmask = b"\x00" * (mask_row * h)
    return header + xor + andmask


def write_ico(images: list[Image.Image], path: Path) -> None:
    blobs = []
    for im in images:
        if im.width >= 256:
            buf = io.BytesIO()
            im.save(buf, "PNG")
            blobs.append(buf.getvalue())
        else:
            blobs.append(bmp_entry(im))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for im, blob in zip(images, blobs):
        dim = 0 if im.width >= 256 else im.width
        out.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    for blob in blobs:
        out.write(blob)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(out.getvalue())


def main() -> None:
    images = [render(s) for s in SIZES]
    write_ico(images, ICO_PATH)
    images[-1].save(OUT_DIR / "app-256.png")

    # 預覽：各尺寸原尺寸排列，另附放大後的 16/32 像素版本
    shown = [256, 128, 64, 48, 32, 24, 16]
    by_size = dict(zip(SIZES, images))
    pad = 24
    zoom = [(16, 8), (32, 4)]
    width = pad + sum(s + pad for s in shown) + sum(s * z + pad for s, z in zoom)
    height = 256 + 2 * pad
    for bg, name in [((255, 255, 255, 255), "preview.png"), ((32, 32, 32, 255), "preview-dark.png")]:
        sheet = Image.new("RGBA", (width, height), bg)
        x = pad
        for s in shown:
            sheet.alpha_composite(by_size[s], (x, pad + (256 - s) // 2))
            x += s + pad
        for s, z in zoom:
            big = by_size[s].resize((s * z, s * z), Image.NEAREST)
            sheet.alpha_composite(big, (x, pad + (256 - s * z) // 2))
            x += s * z + pad
        sheet.save(OUT_DIR / name)
    print(f"已輸出 {ICO_PATH}")


if __name__ == "__main__":
    main()
