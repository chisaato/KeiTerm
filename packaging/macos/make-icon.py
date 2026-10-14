#!/usr/bin/env python3
"""生成 KeiTerm 的应用图标源图（1024x1024 PNG），并可选输出 Windows .ico。

设计取自《蔚蓝档案》天童ケイ（Tendou Kei）的立绘：
  光环   —— 数个相互叠压的粉色矩形框，形似层叠的窗口，这里直接化作终端窗口，
            主框顶边下垂的"页签"缺口保留原样
  头发   —— 白色，用作提示符 ">"
  领带   —— 青蓝色，用作光标块 "_"
  外套   —— 深灰底色，与 DesignSystem/KeiTokens.axaml 的 Kei.Bg.* 保持一致
  内衬   —— 荧光粉，作为底部一抹斜向衬边
光环在立绘里是轻微倾斜的，这里整体旋转同样的小角度，保留那点"歪着戴"的神态。

用法：
    uv run --with pillow packaging/macos/make-icon.py packaging/macos/AppIcon.png [src/Kei.Term.App/Assets/KeiTerm.ico]
"""

import sys
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
SS = 4  # 超采样倍率，先大后缩以保证边缘平滑

# 外套深灰：KeiTokens.axaml 中的 Kei.Bg.PanelAlt / Kei.Bg.Window
BG_TOP = (42, 42, 48)
BG_BOTTOM = (24, 24, 28)
BORDER = (58, 58, 66)
# 立绘取色
HALO = (255, 164, 204)
HALO_GLOW = (255, 92, 160)
LINING = (236, 40, 112)
HAIR = (246, 246, 250)
TIE = (78, 176, 228)

# 光环框体，按立绘光环的实测比例换算到图标主体内的 0..1 归一化坐标：(左, 上, 右, 下)
MAIN_FRAME = (0.384, 0.17, 0.88, 0.642)
BACK_FRAMES = [
    (0.12, 0.274, 0.496, 0.594),  # 左侧框，右沿伸进主框，被主框遮住
]
FRONT_FRAMES = [
    (0.512, 0.55, 0.752, 0.834),  # 右下小框，压在主框底边之上并探出下方
]
HALO_BLOCKS = [
    (0.152, 0.189, 0.285, 0.253),  # 左上角漂浮的小扁框（实心，小尺寸下更清楚）
    (0.52, 0.17, 0.637, 0.272),  # 主框顶边下垂的页签缺口
]
HALO_TILT = -4  # 度，逆时针为正


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def vertical_gradient(size: int, top: tuple, bottom: tuple) -> Image.Image:
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        grad.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return grad.resize((size, size), Image.NEAREST)


def draw_halo(body: int) -> Image.Image:
    layer = Image.new("RGBA", (body, body), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    stroke = round(body * 0.034)

    def box(rect):
        return tuple(round(v * body) for v in rect)

    for rect in BACK_FRAMES:
        draw.rectangle(box(rect), outline=HALO + (255,), width=stroke)
    # 主框挖空内部：遮住身后框体伸进来的线条，让提示符所在的"窗口"读起来是前景
    draw.rectangle(box(MAIN_FRAME), fill=(0, 0, 0, 0), outline=HALO + (255,), width=stroke)
    for rect in FRONT_FRAMES:
        draw.rectangle(box(rect), outline=HALO + (255,), width=stroke)
    for rect in HALO_BLOCKS:
        draw.rectangle(box(rect), fill=HALO + (255,))
    return layer.rotate(HALO_TILT, resample=Image.BICUBIC, center=(body * 0.5, body * 0.5))


def draw_prompt(body: int) -> Image.Image:
    layer = Image.new("RGBA", (body, body), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    # 提示符 ">" —— 两道折线，端点补圆避免斜接缺口
    stroke = round(body * 0.05)
    cx, cy = body * 0.575, body * 0.395
    arm = body * 0.085
    for seg in (((cx - arm, cy - arm), (cx, cy)), ((cx, cy), (cx - arm, cy + arm))):
        draw.line(seg, fill=HAIR + (255,), width=stroke)
    r = stroke / 2
    for px, py in ((cx - arm, cy - arm), (cx, cy), (cx - arm, cy + arm)):
        draw.ellipse((px - r, py - r, px + r, py + r), fill=HAIR + (255,))

    # 光标块 "_" —— 领带青
    bar_h = stroke
    x0 = cx + body * 0.05
    draw.rounded_rectangle(
        (x0, cy + arm - bar_h / 2, x0 + body * 0.15, cy + arm + bar_h / 2),
        radius=bar_h / 2,
        fill=TIE + (255,),
    )
    return layer.rotate(HALO_TILT, resample=Image.BICUBIC, center=(body * 0.5, body * 0.5))


def glow(layer: Image.Image, color: tuple, radius: float, strength: float) -> Image.Image:
    alpha = layer.getchannel("A").filter(ImageFilter.GaussianBlur(radius))
    alpha = alpha.point(lambda a: min(255, round(a * strength)))
    out = Image.new("RGBA", layer.size, color + (0,))
    out.putalpha(alpha)
    return out


def render() -> Image.Image:
    canvas = SIZE * SS
    # macOS 图标惯例：主体四周留白，圆角半径约为边长的 22.5%
    inset = round(canvas * 0.085)
    body = canvas - inset * 2
    radius = round(body * 0.225)
    mask = rounded_mask(body, radius)

    plate = vertical_gradient(body, BG_TOP, BG_BOTTOM).convert("RGBA")

    # 内衬荧光粉：右下角一道斜向衬边，像外套敞开处露出的里衬
    lining = Image.new("RGBA", (body, body), (0, 0, 0, 0))
    ImageDraw.Draw(lining).polygon(
        [(body * 1.0, body * 0.80), (body * 1.0, body * 0.86), (body * 0.86, body * 1.0), (body * 0.80, body * 1.0)],
        fill=LINING + (255,),
    )
    plate = Image.alpha_composite(plate, lining)

    halo = draw_halo(body)
    prompt = draw_prompt(body)
    plate = Image.alpha_composite(plate, glow(halo, HALO_GLOW, body * 0.035, 1.1))
    plate = Image.alpha_composite(plate, glow(halo, HALO_GLOW, body * 0.012, 0.9))
    plate = Image.alpha_composite(plate, halo)
    plate = Image.alpha_composite(plate, glow(prompt, TIE, body * 0.02, 0.5))
    plate = Image.alpha_composite(plate, prompt)

    icon = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    icon.paste(plate, (inset, inset), mask)

    # 内描边：微弱反光，呼应 Kei.Border
    ImageDraw.Draw(icon).rounded_rectangle(
        (inset, inset, inset + body - 1, inset + body - 1),
        radius=radius,
        outline=BORDER + (255,),
        width=max(1, round(canvas * 0.004)),
    )
    return icon.resize((SIZE, SIZE), Image.LANCZOS)


def main(png_path: str, ico_path: str | None) -> None:
    icon = render()
    icon.save(png_path, "PNG")
    print(f"已生成 {png_path} ({SIZE}x{SIZE})")
    if ico_path:
        sizes = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]
        icon.save(ico_path, "ICO", sizes=sizes)
        print(f"已生成 {ico_path}")


if __name__ == "__main__":
    main(
        sys.argv[1] if len(sys.argv) > 1 else "AppIcon.png",
        sys.argv[2] if len(sys.argv) > 2 else None,
    )
