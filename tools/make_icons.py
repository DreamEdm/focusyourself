"""生成扩展图标、exe 图标和 README logo（番茄造型，与桌面端托盘图标、界面里的小番茄一致）。

番茄的轮廓在 32x32 的坐标系里用 SVG 路径描述，和 src/FocusYourself.App/Views/TomatoShape.cs 里的
三个常量逐字相同（有单元测试检查），改一边就要改另一边。

用法：python tools/make_icons.py
"""
import re
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent

# ---- 与 TomatoShape.cs 保持一致 ----
BODY = "M 16,9.6 C 19.2,7.6 24.6,7.6 27.6,11 C 30.6,14.4 30.4,21.4 27.4,25.4 C 24.8,28.8 20.6,30.4 16,30.4 C 11.4,30.4 7.2,28.8 4.6,25.4 C 1.6,21.4 1.4,14.4 4.4,11 C 7.4,7.6 12.8,7.6 16,9.6 Z"
CALYX = "M 16,7.4 C 13.4,5.6 9.8,5.8 6.6,8.4 C 9.4,9 11.6,9.4 13,9.8 C 11.8,11 10.8,12.6 10.6,14.2 C 12.8,13.2 14.6,11.8 16,10.6 C 17.4,11.8 19.2,13.2 21.4,14.2 C 21.2,12.6 20.2,11 19,9.8 C 20.4,9.4 22.6,9 25.4,8.4 C 22.2,5.8 18.6,5.6 16,7.4 Z"
STEM = "M 15.2,8.4 C 15,6.2 15.6,4.2 17,2.8 C 17.5,2.3 18.4,2.5 18.6,3.2 C 18.7,3.6 18.5,4 18.2,4.3 C 17.2,5.4 16.8,6.8 16.9,8.4 Z"

SS = 8  # 超采样倍数


def parse_path(d: str, samples: int = 24) -> list[tuple[float, float]]:
    """只支持本文件用到的绝对命令 M / C / L / Z，三次贝塞尔按固定步数采样成折线。"""
    tokens = re.findall(r"[MCLZ]|-?\d*\.?\d+", d)
    pts: list[tuple[float, float]] = []
    i, cmd = 0, ""
    while i < len(tokens):
        if tokens[i] in "MCLZ":
            cmd = tokens[i]
            i += 1
            if cmd == "Z":
                continue
        nums = lambda n: [float(t) for t in tokens[i:i + n]]
        if cmd in ("M", "L"):
            x, y = nums(2)
            pts.append((x, y))
            i += 2
        elif cmd == "C":
            x1, y1, x2, y2, x, y = nums(6)
            x0, y0 = pts[-1]
            for k in range(1, samples + 1):
                t = k / samples
                a, b, c, e = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
                pts.append((a * x0 + b * x1 + c * x2 + e * x, a * y0 + b * y1 + c * y2 + e * y))
            i += 6
    return pts


def mask_of(d: str, s: int) -> Image.Image:
    u = s / 32
    m = Image.new("L", (s, s), 0)
    ImageDraw.Draw(m).polygon([(x * u, y * u) for x, y in parse_path(d)], fill=255)
    return m


def radial(s: int, cx: float, cy: float, r: float, stops: list[tuple[float, tuple[int, int, int]]]) -> Image.Image:
    """以 (cx, cy) 为圆心、半径 r（都是 0~1 的相对坐标）的径向渐变。"""
    yy, xx = np.mgrid[0:s, 0:s] / s
    t = np.clip(np.hypot(xx - cx, yy - cy) / r, 0, 1)
    out = np.zeros((s, s, 3))
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        k = np.clip((t - t0) / (t1 - t0), 0, 1)[..., None]
        seg = (t >= t0) & (t <= t1)
        out[seg] = (np.array(c0) * (1 - k) + np.array(c1) * k)[seg]
    out[t > stops[-1][0]] = stops[-1][1]
    return Image.fromarray(out.astype(np.uint8), "RGB").convert("RGBA")


def linear(s: int, top: tuple[int, int, int], bottom: tuple[int, int, int], y0: float, y1: float) -> Image.Image:
    yy = np.mgrid[0:s, 0:s][0] / s
    t = np.clip((yy - y0) / (y1 - y0), 0, 1)[..., None]
    out = np.array(top) * (1 - t) + np.array(bottom) * t
    return Image.fromarray(out.astype(np.uint8), "RGB").convert("RGBA")


def layer(img: Image.Image, fill: Image.Image, mask: Image.Image) -> None:
    """把 fill 按 mask 作为不透明度叠到 img 上（不能用 Image.composite，它会把颜色也一起混黑）。"""
    top = fill.copy()
    top.putalpha(ImageChops.multiply(fill.getchannel("A"), mask))
    img.alpha_composite(top)


def tomato(size: int, shadow: bool = True) -> Image.Image:
    s = size * SS
    u = s / 32
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    body = mask_of(BODY, s)
    calyx = ImageChops.lighter(mask_of(CALYX, s), mask_of(STEM, s))

    # 落在果实下方的柔和阴影（小尺寸看不清，只在大图上画）
    if shadow and size >= 48:
        sh = Image.new("L", (s, s), 0)
        ImageDraw.Draw(sh).ellipse([6 * u, 28.2 * u, 26 * u, 31.4 * u], fill=90)
        sh = sh.filter(ImageFilter.GaussianBlur(1.1 * u))
        layer(img, Image.new("RGBA", (s, s), (120, 20, 30, 255)), sh)

    # 果实：左上偏亮的径向渐变
    layer(img, radial(s, 0.36, 0.38, 0.72, [(0, (0xFF, 0x9A, 0x7A)), (0.45, (0xF2, 0x55, 0x52)), (1, (0xC4, 0x2E, 0x40))]), body)

    # 右下的弧形暗部，让果实更立体
    inner = Image.new("L", (s, s), 0)
    ImageDraw.Draw(inner).ellipse([-1.5 * u, 6 * u, 27.5 * u, 28.5 * u], fill=255)
    rim = ImageChops.multiply(ImageChops.subtract(body, inner), Image.new("L", (s, s), 70)).filter(ImageFilter.GaussianBlur(1.2 * u))
    layer(img, Image.new("RGBA", (s, s), (110, 10, 30, 255)), ImageChops.multiply(rim, body))

    # 叶子下方的一圈淡阴影
    cs = calyx.filter(ImageFilter.GaussianBlur(0.9 * u))
    cs = ImageChops.multiply(ImageChops.offset(cs, 0, int(0.8 * u)), Image.new("L", (s, s), 110))
    layer(img, Image.new("RGBA", (s, s), (120, 15, 30, 255)), ImageChops.multiply(cs, body))

    # 高光：左上一块柔光 + 一道小亮斑
    hl = Image.new("L", (s, s), 0)
    ImageDraw.Draw(hl).ellipse([6.2 * u, 12.2 * u, 13.6 * u, 17.6 * u], fill=95)
    hl = hl.filter(ImageFilter.GaussianBlur(1.0 * u))
    layer(img, Image.new("RGBA", (s, s), (255, 255, 255, 255)), ImageChops.multiply(hl, body))
    if size >= 32:
        spark = Image.new("L", (s, s), 0)
        ImageDraw.Draw(spark).ellipse([8.2 * u, 13.2 * u, 10.6 * u, 15 * u], fill=200)
        layer(img, Image.new("RGBA", (s, s), (255, 255, 255, 255)), spark.filter(ImageFilter.GaussianBlur(0.25 * u)))

    # 叶子 + 果柄：上亮下暗的绿色
    layer(img, linear(s, (0x6B, 0xD8, 0x9C), (0x2A, 0x9A, 0x63), 2.5 / 32, 14 / 32), calyx)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    icons = ROOT / "extension" / "icons"
    icons.mkdir(parents=True, exist_ok=True)
    for size in (16, 32, 48, 128):
        tomato(size).save(icons / f"icon{size}.png")
    frames = [tomato(n) for n in (16, 24, 32, 48, 64, 128, 256)]
    frames[-1].save(ROOT / "src" / "FocusYourself.App" / "app.ico", sizes=[f.size for f in frames], append_images=frames[:-1])
    # README 顶部的 logo
    images = ROOT / "docs" / "images"
    images.mkdir(parents=True, exist_ok=True)
    tomato(256).save(images / "logo.png")
    print("icons written")


if __name__ == "__main__":
    main()
