#!/usr/bin/env python3
"""生成 KeiTerm 的应用图标源图（1024x1024 PNG）及分级优化的 Windows .ico。

默认一次运行同时输出 3 套视觉变体（带底座标准版、全镂空纯透明版、外部透明+窗口深色内衬版）：
  1. AppIcon.png / KeiTerm.ico
     —— 标准圆角深灰底座版（含右下角荧光粉内衬斜边、窗口深色内衬）
  2. AppIcon-transparent.png / KeiTerm-transparent.ico
     —— 全镂空纯透明版（去掉圆角底座与窗口内衬，仅保留三层粉色光环框与 `>_`，交界处做透明擦除遮挡）
  3. AppIcon-transparent-window-fill.png / KeiTerm-transparent-window-fill.ico
     —— 外部透明 + 窗口深色内衬版（去掉外围圆角底座，异形光环直接悬浮于透明背景，但保留终端框内部深色衬底以兼顾浅色桌面可读性）

设计取自《蔚蓝档案》天童ケイ（Tendou Kei）的立绘与设定：
  光环   —— 数个相互叠压的粉色矩形框（与爱丽丝光环左右镜像），直接化作层叠的终端窗口；
            通过三阶深浅粉分色（#FFAAD0 / #DA669E / #FF88BC）与交界遮挡体现真实的前后叠压关系
  白发   —— 冷调亮白（#F8F8FC），用作终端提示符 ">"
  领带   —— 千年学园制服青蓝色（#4EB4E8），用作终端光标块 "_"
  内搭与终端底色 —— 深灰纵向渐变底色，与 DesignSystem/KeiTokens.axaml 的 Kei.Bg.* 保持一致
  内衬   —— 外套内侧荧光粉（#EC2870），在标准版底座右下角圆角安全区内切出一道斜向衬边

用法：
    uv run --with pillow packaging/macos/make-icon.py packaging/macos/AppIcon.png [src/Kei.Term.App/Assets/KeiTerm.ico]
"""

import io
import pathlib
import struct
import sys
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
SS = 4  # 超采样倍率，先大后缩以保证边缘平滑

# 终端背景与边框：对齐 KeiTokens.axaml 中的 Kei.Bg.PanelAlt / Kei.Bg.Window / Kei.Border
BG_TOP = (42, 42, 48)
BG_BOTTOM = (22, 22, 26)
WINDOW_FILL = (18, 18, 22, 232)  # 主框与前景框内部深色填充，拉开前后窗口叠压层次
TRANSPARENT = (0, 0, 0, 0)
BORDER = (64, 64, 74)

# 立绘取色（区分主框、后框、前框的粉色明度层次）
HALO_MAIN = (255, 170, 208)   # 主窗口亮粉 (#FFAAD0)
HALO_BACK = (218, 102, 158)   # 左后方副框深粉 (#DA669E)，自然退后
HALO_FRONT = (255, 136, 188)  # 右下前景副框霓虹粉 (#FF88BC)
HALO_GLOW = (255, 84, 156)    # 光环外发光 (#FF549C)
LINING = (236, 40, 112)       # 外套荧光粉内衬 (#EC2870)
HAIR = (248, 248, 252)        # 白发提示符 ">" (#F8F8FC)
TIE = (78, 180, 232)          # 青蓝色领带光标 "_" (#4EB4E8)

# 光环框体归一化坐标：(左, 上, 右, 下)，拉开主框纵向净空以容纳 ">_"
MAIN_FRAME = (0.31, 0.165, 0.865, 0.675)
BACK_FRAMES = [
    (0.11, 0.275, 0.465, 0.585),  # 左后方框，右沿被主框自然遮挡
]
FRONT_FRAMES = [
    (0.545, 0.585, 0.805, 0.845),  # 右下前景小框，遮挡主框底边，消除十字网格感
]
HALO_BLOCKS = [
    (0.135, 0.155, 0.265, 0.218),  # 左上角漂浮的小扁框（对应ケイ镜像光环左上角小方框）
    (0.475, 0.165, 0.605, 0.222),  # 主框顶边重叠的扁平页签块（克制高度，避免挤压下方提示符）
]
HALO_TILT = -4.0  # 度，顺时针微倾 4°


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


def glow(layer: Image.Image, color: tuple, radius: float, strength: float) -> Image.Image:
    if radius <= 0.5 or strength <= 0:
        return Image.new("RGBA", layer.size, TRANSPARENT)
    alpha = layer.getchannel("A").filter(ImageFilter.GaussianBlur(radius))
    alpha = alpha.point(lambda a: min(255, round(a * strength)))
    out = Image.new("RGBA", layer.size, color + (0,))
    out.putalpha(alpha)
    return out


def draw_halo(
    body: int,
    window_fill: tuple[int, int, int, int] = WINDOW_FILL,
    compact: bool = False,
    tilt: float = HALO_TILT,
) -> Image.Image:
    layer = Image.new("RGBA", (body, body), TRANSPARENT)
    draw = ImageDraw.Draw(layer)
    stroke = round(body * (0.062 if compact else 0.036))

    def box(rect):
        return tuple(round(v * body) for v in rect)

    if compact:
        # 16/24/32 px 小尺寸简化版：聚焦主终端框 + 左上浮块 + 顶边页签，最大化框体可读性
        main_rect = (0.18, 0.18, 0.88, 0.82)
        draw.rectangle(box((0.08, 0.10, 0.26, 0.20)), fill=HALO_BACK + (255,))
        draw.rectangle(box(main_rect), fill=window_fill, outline=HALO_MAIN + (255,), width=stroke)
        draw.rectangle(box((0.38, 0.18, 0.56, 0.26)), fill=HALO_MAIN + (255,))
    else:
        # 1. 绘制左后方副框
        for rect in BACK_FRAMES:
            draw.rectangle(box(rect), outline=HALO_BACK + (255,), width=stroke)

        # 2. 绘制主终端框：fill 传入 WINDOW_FILL 或 TRANSPARENT 均可擦除后方框体伸入的线条
        draw.rectangle(box(MAIN_FRAME), fill=window_fill, outline=HALO_MAIN + (255,), width=stroke)

        # 3. 绘制右下前景框：擦除/遮挡主框底边交叉线，明确"前景压主框"的叠压关系
        for rect in FRONT_FRAMES:
            draw.rectangle(box(rect), fill=window_fill, outline=HALO_FRONT + (255,), width=stroke)

        # 4. 绘制左上漂浮块与主框顶边页签
        for rect in HALO_BLOCKS:
            draw.rectangle(box(rect), fill=HALO_MAIN + (255,))

    if abs(tilt) > 0.01:
        return layer.rotate(tilt, resample=Image.BICUBIC, center=(body * 0.5, body * 0.5))
    return layer


def draw_prompt(body: int, compact: bool = False, tilt: float = HALO_TILT) -> Image.Image:
    layer = Image.new("RGBA", (body, body), TRANSPARENT)
    draw = ImageDraw.Draw(layer)

    if compact:
        # 小尺寸下放大加粗 ">_" 并居中于简化主框 (0.18..0.88, 0.18..0.82)
        stroke = round(body * 0.082)
        cx, cy = body * 0.46, body * 0.52
        arm = body * 0.125
        bar_w = body * 0.19
        bar_gap = body * 0.055
    else:
        # 标准尺寸：居中于主框净空区域 (x=0.31..0.865, y=0.222..0.585)
        stroke = round(body * 0.052)
        cx, cy = body * 0.525, body * 0.415
        arm = body * 0.092
        bar_w = body * 0.155
        bar_gap = body * 0.052

    # 提示符 ">" —— 两道折线，端点补圆避免斜接缺口
    for seg in (((cx - arm, cy - arm), (cx, cy)), ((cx, cy), (cx - arm, cy + arm))):
        draw.line(seg, fill=HAIR + (255,), width=stroke)
    r = stroke / 2
    for px, py in ((cx - arm, cy - arm), (cx, cy), (cx - arm, cy + arm)):
        draw.ellipse((px - r, py - r, px + r, py + r), fill=HAIR + (255,))

    # 光标块 "_" —— 领带青蓝，与 ">" 底端基线对齐
    bar_h = stroke
    x0 = cx + bar_gap
    draw.rounded_rectangle(
        (x0, cy + arm - bar_h / 2, x0 + bar_w, cy + arm + bar_h / 2),
        radius=bar_h / 2,
        fill=TIE + (255,),
    )

    if abs(tilt) > 0.01:
        return layer.rotate(tilt, resample=Image.BICUBIC, center=(body * 0.5, body * 0.5))
    return layer


def render_canvas(
    target_size: int = SIZE,
    inset_ratio: float = 0.085,
    compact: bool = False,
    tilt: float = HALO_TILT,
    with_plate: bool = True,
    window_fill: tuple[int, int, int, int] = WINDOW_FILL,
) -> Image.Image:
    work_size = max(512, target_size * SS)
    inset = round(work_size * inset_ratio)
    body = work_size - inset * 2

    if with_plate:
        radius = round(body * 0.225)
        mask = rounded_mask(body, radius)
        plate = vertical_gradient(body, BG_TOP, BG_BOTTOM).convert("RGBA")

        # 内衬荧光粉：位于圆角安全区内部 (0.69..0.765 < 0.775)，避免被 22.5% 圆角遮罩裁切掉
        lining = Image.new("RGBA", (body, body), TRANSPARENT)
        l0, l1 = (0.66, 0.76) if compact else (0.69, 0.765)
        ImageDraw.Draw(lining).polygon(
            [(body * 1.0, body * l0), (body * 1.0, body * l1), (body * l1, body * 1.0), (body * l0, body * 1.0)],
            fill=LINING + (255,),
        )
        plate = Image.alpha_composite(plate, lining)
    else:
        plate = Image.new("RGBA", (body, body), TRANSPARENT)

    halo = draw_halo(body, window_fill=window_fill, compact=compact, tilt=tilt)
    prompt = draw_prompt(body, compact=compact, tilt=tilt)

    glow_scale = 0.55 if compact else 1.0
    plate = Image.alpha_composite(plate, glow(halo, HALO_GLOW, body * 0.032 * glow_scale, 1.05 * glow_scale))
    plate = Image.alpha_composite(plate, glow(halo, HALO_GLOW, body * 0.011 * glow_scale, 0.85 * glow_scale))
    plate = Image.alpha_composite(plate, halo)
    plate = Image.alpha_composite(plate, glow(prompt, TIE, body * 0.018 * glow_scale, 0.45 * glow_scale))
    plate = Image.alpha_composite(plate, prompt)

    icon = Image.new("RGBA", (work_size, work_size), TRANSPARENT)
    if with_plate:
        icon.paste(plate, (inset, inset), mask)
        border_w = max(1, round(work_size * (0.008 if compact else 0.004)))
        ImageDraw.Draw(icon).rounded_rectangle(
            (inset, inset, inset + body - 1, inset + body - 1),
            radius=radius,
            outline=BORDER + (255,),
            width=border_w,
        )
    else:
        icon.paste(plate, (inset, inset), plate)

    return icon.resize((target_size, target_size), Image.LANCZOS)


def render(mode: str = "standard") -> Image.Image:
    """按模式生成 1024x1024 图标：
    - 'standard': 带深灰圆角底座 + 荧光粉斜边 + 窗口深色内衬（macOS 8.5% 留白）
    - 'transparent': 全镂空纯透明版（无圆角底座、窗口内部完全透明，4% 留白容纳辉光）
    - 'transparent-window-fill': 外部透明 + 仅窗口内部深色衬底版（4% 留白容纳辉光）
    """
    if mode == "transparent":
        return render_canvas(target_size=SIZE, inset_ratio=0.04, compact=False, tilt=HALO_TILT, with_plate=False, window_fill=TRANSPARENT)
    if mode == "transparent-window-fill":
        return render_canvas(target_size=SIZE, inset_ratio=0.04, compact=False, tilt=HALO_TILT, with_plate=False, window_fill=WINDOW_FILL)
    return render_canvas(target_size=SIZE, inset_ratio=0.085, compact=False, tilt=HALO_TILT, with_plate=True, window_fill=WINDOW_FILL)


def save_multi_size_ico(
    ico_path: str,
    mode: str = "standard",
    sizes: tuple[int, ...] = (16, 24, 32, 48, 64, 128, 256),
) -> None:
    """按尺寸分级渲染并打包为标准 Windows .ico，解决小尺寸下边距浪费与旋转发虚问题。"""
    with_plate = mode == "standard"
    win_fill = TRANSPARENT if mode == "transparent" else WINDOW_FILL

    png_blobs: list[bytes] = []
    for s in sizes:
        if s <= 24:
            # 16px / 24px（窗口标题栏、任务栏列表）：零留白满幅、0° 水平对齐、简化加粗版
            img = render_canvas(target_size=s, inset_ratio=0.0, compact=True, tilt=0.0, with_plate=with_plate, window_fill=win_fill)
        elif s <= 32:
            # 32px：极窄留白、轻微 -2° 倾角、简化加粗版
            img = render_canvas(target_size=s, inset_ratio=0.02, compact=True, tilt=-2.0, with_plate=with_plate, window_fill=win_fill)
        else:
            # 48px–256px：紧凑留白 (2%) + 完整立绘光环细节
            img = render_canvas(target_size=s, inset_ratio=0.02, compact=False, tilt=HALO_TILT, with_plate=with_plate, window_fill=win_fill)

        buf = io.BytesIO()
        img.save(buf, format="PNG")
        png_blobs.append(buf.getvalue())

    with open(ico_path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(sizes)))
        offset = 6 + 16 * len(sizes)
        for s, blob in zip(sizes, png_blobs):
            dim = 0 if s >= 256 else s
            f.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset))
            offset += len(blob)
        for blob in png_blobs:
            f.write(blob)


def derive_variant_path(base_path: str, suffix: str) -> str:
    p = pathlib.Path(base_path)
    return str(p.with_name(f"{p.stem}-{suffix}{p.suffix}"))


def main(png_path: str, ico_path: str | None) -> None:
    modes = (
        ("standard", None),
        ("transparent", "transparent"),
        ("transparent-window-fill", "transparent-window-fill"),
    )

    for mode, suffix in modes:
        out_png = png_path if suffix is None else derive_variant_path(png_path, suffix)
        icon = render(mode=mode)
        icon.save(out_png, "PNG")
        print(f"已生成 {out_png} ({SIZE}x{SIZE}, mode={mode})")

        if ico_path:
            out_ico = ico_path if suffix is None else derive_variant_path(ico_path, suffix)
            save_multi_size_ico(out_ico, mode=mode)
            print(f"已生成分级优化的 {out_ico} (16–256 px, mode={mode})")


if __name__ == "__main__":
    main(
        sys.argv[1] if len(sys.argv) > 1 else "AppIcon.png",
        sys.argv[2] if len(sys.argv) > 2 else None,
    )
