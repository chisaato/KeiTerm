#!/usr/bin/env python3
"""生成 KeiTerm 的 macOS 应用图标源图（1024x1024 PNG）。

配色取自 DesignSystem/KeiTokens.axaml，保证与应用本体一致：
  Kei.Bg.PanelAlt  #2A2A30 / Kei.Bg.Window #1E1E22  背景渐变
  Kei.Accent       #3574F0                          提示符
  Kei.Session.Foreground #4EC9B0                    光标块

用法：
    python3 packaging/macos/make-icon.py packaging/macos/AppIcon.png
"""

import sys
from PIL import Image, ImageDraw

SIZE = 1024
SS = 4  # 超采样倍率，先大后缩以保证边缘平滑

# KeiTokens.axaml 中的令牌值
BG_TOP = (42, 42, 48)
BG_BOTTOM = (30, 30, 34)
ACCENT = (53, 116, 240)
SESSION = (78, 201, 176)
BORDER = (58, 58, 66)


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


def main(out_path: str) -> None:
    canvas = SIZE * SS
    # macOS 图标惯例：主体四周留白，圆角半径约为边长的 22.5%
    inset = round(canvas * 0.085)
    body = canvas - inset * 2
    radius = round(body * 0.225)

    icon = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    base = vertical_gradient(body, BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = rounded_mask(body, radius)

    layer = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    layer.paste(base, (inset, inset), mask)
    icon = Image.alpha_composite(icon, layer)

    draw = ImageDraw.Draw(icon)

    # 内描边：微弱反光，呼应 Kei.Border
    draw.rounded_rectangle(
        (inset, inset, inset + body - 1, inset + body - 1),
        radius=radius,
        outline=BORDER + (255,),
        width=max(1, round(canvas * 0.004)),
    )

    # 终端提示符 ">" —— 两道折线，比字体渲染更可控
    stroke = round(canvas * 0.032)
    cx = inset + body * 0.34
    cy = inset + body * 0.47
    arm = body * 0.115
    draw.line([(cx - arm, cy - arm), (cx, cy)], fill=ACCENT + (255,), width=stroke, joint="curve")
    draw.line([(cx, cy), (cx - arm, cy + arm)], fill=ACCENT + (255,), width=stroke, joint="curve")
    # 折线端点补圆，避免斜接缺口
    for px, py in ((cx - arm, cy - arm), (cx, cy), (cx - arm, cy + arm)):
        r = stroke / 2
        draw.ellipse((px - r, py - r, px + r, py + r), fill=ACCENT + (255,))

    # 光标块 "_" —— 会话高亮色
    bar_y = cy + arm
    bar_h = round(canvas * 0.032)
    draw.rounded_rectangle(
        (cx + body * 0.055, bar_y - bar_h / 2, cx + body * 0.055 + body * 0.19, bar_y + bar_h / 2),
        radius=bar_h / 2,
        fill=SESSION + (255,),
    )

    icon.resize((SIZE, SIZE), Image.LANCZOS).save(out_path, "PNG")
    print(f"已生成 {out_path} ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "AppIcon.png")
