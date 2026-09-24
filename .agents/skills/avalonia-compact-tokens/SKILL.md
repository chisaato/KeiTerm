---
name: avalonia-compact-tokens
description: Use when designing, styling, or creating UI components, views, and XAML layouts in Avalonia desktop tools, especially when styling buttons, text boxes, combo boxes, or enforcing dark-mode design tokens and compact desktop density
---

# Avalonia Compact Tokens 规范

## 核心理念
桌面专业工具（如 JetBrains IDE、SecureCRT、Redis Assistant）不同于移动端或 Web 系统。它的核心特征是**高信息密度、实体边界、深沉层级、克制动效与严密几何**。

坚决杜绝移动端下划线输入框、膨胀的 36px+ 巨型控件、老年机大号文字和刺眼的大圆角。

---

## 1. 工业桌面硬指标

| 维度 | 标准取值 | 禁忌 | 说明 |
| :--- | :--- | :--- | :--- |
| **标准控件高** | `28px ~ 32px` | `> 36px` | 按钮、输入框、下拉框统一基准高度 |
| **工具栏图标按钮** | `28×28px ~ 30×30px` | `< 24px` 或 `> 36px` | 点击热区适中，带 1px 垂直分隔线分组 |
| **圆角 (Radius)** | `3px ~ 4px` | `> 6px` | 克制微圆角，禁止 8px/12px 移动端大圆角 |
| **输入框形态** | **实体封闭矩形框** | 悬空单下划线 | 必须带 1px 微弱描边与实体深色背景 |
| **文本字阶** | 标题 13~14px，正文/标签 13px，次要 11.5~12px | 正文 `< 11px` 或 `> 15px` | 保证字符饱满清晰，避免过小或过大 |
| **组件垂直间距** | `6px ~ 10px` | `> 16px` | 字段紧凑咬合，同屏容纳更多配置项 |

---

## 2. 颜色与表面层级（Tokens 契约）

所有视图 XAML **严禁硬编码 `#Hex` 颜色**，必须引用 `Kei.*` 资源令牌：

### 表面层级 (Surfaces)
- `Kei.Bg.Window` (`#1E1E22`): 顶级窗口底色
- `Kei.Bg.Panel` (`#25252A`): 侧边栏、主内容分割面板
- `Kei.Bg.PanelAlt` (`#2A2A30`): 菜单栏、面板标题头、工具栏
- `Kei.Bg.Card` (`#222227`): 弹窗内部的内容分组卡片底色
- `Kei.Bg.Input` (`#18181B`): 输入框、下拉框、数字微调框凹陷实体底色
- `Kei.Bg.Hover` (`#323238`): 悬停高亮底色
- `Kei.Bg.Pressed` (`#3A3A42`): 按下激活底色

### 描边与焦点 (Borders)
- `Kei.Border` (`#3A3A42`): 实体控件与卡片 1px 标准描边
- `Kei.Border.Subtle` (`#2E2E35`): 内部卡片与分隔的微弱反光线
- `Kei.Border.Focus` (`#3574F0`): 输入框/下拉框获得键盘焦点时的亮蓝高光（无模糊光晕扩散）

### 文字与强调 (Typography & Accents)
- `Kei.Text.Primary` (`#E8E8EC`): 主文字、输入框正文、主要标签
- `Kei.Text.Secondary` (`#B8B8C2`): 辅助字段名、列表次要文字
- `Kei.Text.Muted` (`#7E7E8A`): 说明提示、占位符、已禁用文字
- `Kei.Accent` (`#3574F0`): 主行动按钮、选中高亮
- `Kei.Session.Foreground` (`#4EC9B0`): 终端/会话高亮色

---

## 3. 字体体系设计

桌面工具必须明确将**界面字体 (UI Font)** 与 **终端/编辑器字体 (Terminal/Editor Font)** 解耦：
1. **界面字体 (UI Font)**：优先选用笔画柔和的思源黑体（`Source Han Sans SC` / `Noto Sans CJK SC`），回退包含 `Microsoft YaHei UI`、`Segoe UI`。
2. **终端等宽字体 (Terminal Font)**：优先选用高品质的 `JetBrainsMono Nerd Font Mono`，专注英文字符与 Nerd 终端图标。
3. **回退字体 (Fallback Font)**：终端必须指定中文回退字体（如 `Source Han Sans HW SC` / `Noto Sans Mono CJK SC`），防止中文宽字符破损。

---

## 4. 常见违规借口与事实反驳

| 常见借口 (Rationalization) | 实际规范 (Reality) |
| :--- | :--- |
| “这个按钮只用一次，写个 `#1E1E1E` 快速调调” | 零裸值是硬性契约，任何临时写死的颜色后续都会在契约测试中报错中断构建。 |
| “大留白看起来更加开阔、有现代设计感” | 这是移动端和展示网页的思路。生产力工具用户需要一眼获取全局信息，过大留白带来无谓的视线跳跃和滚动。 |
| “输入框用下划线更轻盈简洁” | 桌面终端是密集操作工具，下划线在多输入项时缺乏容器边界，容易导致输入区域判断模糊。 |
| “圆角做 8px 看起来圆润柔和” | 超过 4px 的圆角在桌面多窗口、网格排版下极其幼稚，破坏了工业工具的精密感。 |

---

## 5. 红线清单 - 立即修正

- ❌ 在任何 View XAML 中直接出现 `Background="#..."`、`Foreground="#..."` 裸十六进制。
- ❌ 让普通输入框呈现无边框下划线风格。
- ❌ 在工具栏或普通表单中使用超过 36px 高度的组件。
- ❌ 正文字号小于 12px 或大于 14px。
- ✅ 复杂弹窗使用 `Border`（`Kei.Bg.Card` + `Kei.Border.Subtle` + `CornerRadius="4"`）组织逻辑卡片。
