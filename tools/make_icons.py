"""生成扩展图标和 exe 图标（番茄造型，与桌面端托盘图标一致）。

用法：python tools/make_icons.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent


def tomato(size: int) -> Image.Image:
    # 先画 4 倍大再缩小，得到平滑边缘
    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = s / 32
    # 果实：从亮到暗的竖向渐变
    body = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    top, bottom = (0xFF, 0x8A, 0x6B), (0xE8, 0x48, 0x50)
    for y in range(s):
        t = y / s
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        bd.line([(0, y), (s, y)], fill=c)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse([3 * u, 6 * u, 29 * u, 30 * u], fill=255)
    img.paste(body, (0, 0), mask)
    # 高光
    d.ellipse([8 * u, 11 * u, 15 * u, 16 * u], fill=(255, 255, 255, 70))
    # 叶子
    leaf = [(16, 2), (13, 7), (8, 7), (12.5, 9.5), (16, 8.5), (19.5, 9.5), (24, 7), (19, 7)]
    d.polygon([(x * u, y * u) for x, y in leaf], fill=(0x4C, 0xC3, 0x8A, 255))
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    icons = ROOT / "extension" / "icons"
    icons.mkdir(parents=True, exist_ok=True)
    for size in (16, 32, 48, 128):
        tomato(size).save(icons / f"icon{size}.png")
    big = tomato(256)
    big.save(ROOT / "src" / "FocusYourself.App" / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    # README 顶部的 logo
    images = ROOT / "docs" / "images"
    images.mkdir(parents=True, exist_ok=True)
    tomato(256).save(images / "logo.png")
    print("icons written")


if __name__ == "__main__":
    main()
