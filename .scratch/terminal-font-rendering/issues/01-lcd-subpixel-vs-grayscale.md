# 终端字形比 Konsole / SecureCRT 略淡（LCD 子像素 vs 灰度抗锯齿）

Status: wontfix（暂缓，等上游条件成熟再研究）

## 现象

同一字体（JetBrainsMono Nerd Font Medium 11pt）、同一配色下，KeiTerm 与 Konsole 叠加后字形完全重合（字宽、行高、笔画位置一致），
但 KeiTerm 的笔画略淡：只有 SemiBold 的视觉分量才接近 Konsole 的 Medium。

## 测量（2026-10-19，同一张截图里的 `clear`，左 Kei / 右 Konsole）

| | KeiTerm | Konsole |
|---|---|---|
| 墨量（覆盖率之和） | 167.8 | 173.9（Kei 少约 3.5%） |
| 满覆盖像素 | 55 | 68（Kei 少约 19%） |
| 有墨像素 | 243 | 251 |
| 平均覆盖率 | 0.691 | 0.693 |

结论：总墨量几乎相同，差别在笔画中心——Konsole 更多像素是满色，KeiTerm 更多是半透明过渡。

## 原因（已确认部分）

- 本机 fontconfig：`hintstyle=3`（hintfull）、`rgba=1`（RGB 子像素）、`lcdfilter=1`；KDE `XftSubPixel=rgb`。Konsole 走 Qt 的 **LCD 子像素渲染**，横向精度是灰度的 3 倍，竖笔边缘更实。
- RoyalTerminal 0.6.2 先把终端画到离屏 `SKSurface`（`TerminalRenderSurface` / `TerminalShaderPostProcessor.CreateRenderSurface`，无 `SKSurfaceProperties`、未声明像素排列），再贴回窗口；Skia 在这种表面上不做 LCD，所以 `FontEdging.SubpixelAntialias` 实际退化为灰度。应用侧改不了，需上游暴露表面属性或改渲染路径。
- 应用侧已做到最大：`TerminalFontRendering` 设置灰度抗锯齿 + `Hinting=Full` + 关闭亚像素定位/线性度量。
- 无头渲染对照（SkiaSharp 3.119.4，字号 15–22px，None/Slight/Full）：同字号内 Full 的墨量与满覆盖像素都不低于其他档，**Full 已是 hinting 里最实的设置**，换档不会更接近 Konsole。

## 未确认部分

- Skia 灰度混合的对比度/gamma 与 Qt LCD 混合的差异究竟占多少，没有拆开量化。
- 对照实验没能复现截图的尺寸（截图里 `clear` 为 60×12 像素，任何单一字号都对不上，疑似屏幕缩放或截图拉伸），因此无法与 Konsole 基准做直接排名。要重做请在 100% 缩放下各截一张同尺寸图。

## 决定

- 不做「自动加粗一档」之类的字形补偿：不能保证对其他字体同样成立，会造成不一致。字重按字体文件真实值导入（Konsole 的 Medium 就是 Medium），想更粗由用户自己选 SemiBold。
- 暂缓。可重新研究的时机：
  1. RoyalTerminal 上游提供离屏表面的 `SKSurfaceProperties`/像素几何，或直接绘制到窗口表面的选项；
  2. 上游提供文字对比度/gamma 调节；
  3. 100% 缩放下获得同尺寸基准截图，重新量化。

## 复现对照实验

用 SkiaSharp 无头渲染 `clear`：前景 `66,160,78`，背景 `35,38,39`，覆盖率取绿色通道 `(G-38)/122`，统计墨量、满覆盖（>0.9）与有墨（>0.15）像素数。
