# 终端字形比 Konsole / SecureCRT 略淡（LCD / 灰度差异待确认）

Status: wontfix（暂缓，等上游条件成熟再研究）

## 现象

同一字体（JetBrainsMono Nerd Font Medium 11pt）、同一配色下，当时叠图观察认为 KeiTerm 与 Konsole 的字形位置接近；该观察不足以证明字宽、行高、笔画位置完全一致，
但 KeiTerm 的笔画略淡：只有 SemiBold 的视觉分量才接近 Konsole 的 Medium。

## 历史测量（2026-10-19，拟截取 `clear` 的 ROI，左 Kei / 右 Konsole）

以下保留当时观察值，但 ROI 可能混入 prompt 符号，不作为纯 `clear` 的精确基准，也不能据此证明字形完全相同。

| | KeiTerm | Konsole |
|---|---|---|
| 墨量（覆盖率之和） | 167.8 | 173.9（Kei 少约 3.5%） |
| 满覆盖像素 | 55 | 68（Kei 少约 19%） |
| 有墨像素 | 243 | 251 |
| 平均覆盖率 | 0.691 | 0.693 |

该 ROI 中 Konsole 的满覆盖像素更多；由于取样边界和内容未严格控制，不能把差异直接归因于字形笔画中心或渲染机制。

## 渲染线索（不是已确认根因）

- 本机 fontconfig：`hintstyle=3`（hintfull）、`rgba=1`（RGB 子像素）、`lcdfilter=1`；KDE `XftSubPixel=rgb`。这些只能证明系统配置偏好，不能证明 Konsole 的实际窗口渲染走了 LCD。
- 当时检查 RoyalTerminal 0.6.2 的线索是先画到离屏 `SKSurface`（`TerminalRenderSurface` / `TerminalShaderPostProcessor.CreateRenderSurface`，未显式设置 `SKSurfaceProperties`/像素排列），再贴回窗口。这是研究实际抗锯齿路径的线索，不足以单独证明两端差异只能由 LCD / 灰度造成，或应用侧绝无调整余地。
- 当前应用侧 `TerminalFontRendering` 使用灰度抗锯齿 + `Hinting=Full` + 关闭亚像素定位/线性度量；这描述现状，不代表已经找到最优解。
- 裸 Skia 无头渲染对照（SkiaSharp 3.119.4，字号 15–22px，None/Slight/Full）中，同字号内 Full 的墨量与满覆盖像素不低于其他档。实验未复现真实终端的尺寸、字体度量和完整渲染链，不能外推为 Full 绝无调整余地。

## 未确认部分

- Konsole 实际抗锯齿路径，以及两端混合对比度/gamma 等因素各占多少，没有严格确认或拆开量化。
- 当时截图 bbox 记录为 60×12 像素，但实际还包含 prompt 符号，不能当作纯 `clear` 的字形尺寸。裸 Skia 实验未对齐真实终端尺寸；不能据此认定屏幕缩放或截图拉伸是根因。后续应控制文本、ROI、字号、缩放及渲染链，重建同条件基准。

## 决定

- 不做「自动加粗一档」之类的字形补偿：不能保证对其他字体同样成立，会造成不一致。字重按字体文件真实值导入（Konsole 的 Medium 就是 Medium），想更粗由用户自己选 SemiBold。
- 暂缓。可重新研究的时机：
  1. RoyalTerminal 上游提供离屏表面的 `SKSurfaceProperties`/像素几何，或直接绘制到窗口表面的选项；
  2. 上游提供文字对比度/gamma 调节；
   3. 获得受控文本、纯字形 ROI 与同条件尺寸基准截图（可先用 100% 缩放），重新量化。

## 复现对照实验

用 SkiaSharp 无头渲染 `clear`：前景 `66,160,78`，背景 `35,38,39`，覆盖率取绿色通道 `(G-38)/122`，统计墨量、满覆盖（>0.9）与有墨（>0.15）像素数。

## Comments

### 2026-10-20 勘误与证据边界

以上已收回「Konsole 确定使用 LCD」「叠图证明字形完全一致」「Full 已无调整余地」的过度断言。历史数值仅作线索，原因仍未严格确认。用户明确不允许自动字重或字形补偿，未来择机研究；本 ticket 保持 `wontfix`（暂缓），不因[本轮功能交付办结](../spec.md)而改为已解决。
