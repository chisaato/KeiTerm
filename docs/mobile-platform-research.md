# KeiTerm 移动端（Android / iOS）发布可行性调研报告

- **调研日期**：2026-10-06
- **调研性质**：纯方案与源码/官方资料技术可行性调研，**未在真机或模拟器构建、安装或运行**；只作为方案底稿。
- **项目背景**：KeiTerm 当前为基于 .NET 10 + Avalonia 12.1.2 的桌面 SSH 客户端。本报告评估将其发布到 Android 与 iOS 平台的可行性、技术边界与最小验证路线。

---

## 一、 Avalonia 12 官方移动平台支持矩阵与 SDK 要求

根据 Avalonia 官方支持平台规范（[Avalonia Supported Platforms](https://docs.avaloniaui.net/docs/supported-platforms)）：
- **Android 支持分级**：
  - **Tier 1**（每个版本均在测试矩阵，优先修复 Bug）：**Android 16 (API 36)**，支持 `ARM64`, `x64`。
  - **Tier 2**（尽力支持，非测试矩阵主力）：**Android 12 至 15 (API 31 至 35)**，支持 `ARM64`, `ARM32`, `x64`。
  - **Tier 3**（商业支持级别）：**Android 11 及更早版本 (API 30 及以下)**。
- **iOS / iPadOS 支持分级**：
  - **Tier 1**：**iOS / iPadOS 26**，支持 `ARM64`。
  - **Tier 2**：**iOS 18.x**，支持 `ARM64`。
  - **Tier 3**：**iOS 17.x 及更早版本**，支持 `ARM64`。
- **SDK 与构建链要求**：
  - **.NET SDK**：要求 .NET 10.0，移动端跟随微软 MAUI 支持生命周期。
  - **工具链匹配**：JDK 版本与 Xcode 版本需与所选 .NET 10 移动工作负载（`android` / `ios` workload）的官方推荐版本对齐（不应笼统假设任意版本均兼容）。
   - **iOS 构建约束**：完整构建与签名需要 macOS + 匹配的 Xcode，可使用本地或远程 Mac 构建机。个人 Apple 账号可用于受限真机调试，但仍需签名；TestFlight / App Store 分发通常需要付费开发者计划。
   - 上述 Tier 是框架维护与测试等级，不等同于应用的最低系统版本或商店要求的 target SDK；这些需在正式移植时另行确定。

---

## 二、 框架路线选型：Avalonia 优先验证，MAUI 保持备选

| 评估维度 | Avalonia 12 Mobile 路线（推荐先做最小验证） | .NET MAUI 路线（保持备选） |
| :--- | :--- | :--- |
| **工程基础** | 本项目已有成熟 Avalonia 桌面 XAML、控件与主题字典，可大量直接复用 | 属于另一套 XAML 方言与控件体系，需重写界面与控件渲染 |
| **终端控件** | 优先验证项目内 `RoyalApps.RoyalTerminal.Avalonia` 的移动渲染与输入，不预设可直接沿用 | 需另行集成或封装终端组件，现有 Avalonia 控件不能直接使用 |
| **布局与字体** | 基于 Skia 渲染，排版逻辑共享，但**移动端系统字体与桌面字体库存在差异，不能假定字体度量完全一致** | 映射为平台原生控件，各系统间控件风格与尺寸天然不同 |
| **决策建议** | **推荐先做 Avalonia 移动端最小原型验证**；若在移动端遇到不可绕过的框架级缺陷，再考虑 MAUI 备选方案 |

---

## 三、 移动端生命周期、视口与 UI 交互线框

- **单视图生命周期**：移动端无 `MainWindow`，由 `ISingleViewApplicationLifetime.MainView` 承载根视图。
- **安全区与视口**：`TopLevel.InsetsManager` 负责 Safe Area，`IInputPane` 监听软键盘开合与 `OccludedRect`。官方文档明确**Avalonia 不会自动滚动避让软键盘**，需应用层监听并调整终端高度。
- **移动端 UI 适配小线框（交互建议，非现有实现；按键行实际需分组或横向滚动，不能压缩触摸热区）**：

```
手机端（紧凑全屏终端 + 扩展栏）：
+-------------------------------------------------------------+
| [◀ 返回]  🟢 prod-db-01 (10.0.1.25)   [会话 (1/2) ⏷] [SFTP] | <- 顶栏状态与文字入口
+-------------------------------------------------------------+
| (keicli) ubuntu@prod-db-01:~$ ps aux | grep nginx           |
| root      1024  0.0  0.2  14230  4120 ?  Ss   10:00   0:00   |
| (keicli) ubuntu@prod-db-01:~$ _                             |
+-------------------------------------------------------------+
| [Esc] [Tab] [Ctrl▲] [Alt] [/] [-] [|] [~] [▲] [▼] [◀] [▶] [⌨] | <- 扩展修饰行
+-------------------------------------------------------------+
|  [ 系统软键盘区域（IME 组字在候选条内，确认后统一提交 UTF-8） ] |
+-------------------------------------------------------------+

平板端（宽屏工作台：导航 + 终端 + SFTP 双栏）：
+-----------------------------------------------------------------------------------------+
| [KeiTerm]  [+] 新建连接   | 🟢 prod-db-01     ⚪ test-web-02      [⚙ 设置]  [🔒 锁定凭据]   |
+---------------------------+-----------------------------------+-------------------------+
| 会话导航                  | 终端区域 (prod-db-01)             | SFTP 管理 (/var/log)    |
| 🟢 生产数据库 (prod-db-01)| $ tail -f /var/log/syslog         | [ ⬆ 上一级 ]  [ 🔄 刷新] |
| ⚪ 测试节点 (test-web-02) | Oct 06 14:30:01 worker CRON[120]: | 📄 syslog      142 MB   |
|                           |                                   | 📁 nginx/               |
+---------------------------+-----------------------------------+-------------------------+
```

---

### 交互细节建议

- 手机首页用最近连接、收藏与搜索；终端保持单会话占满内容区，点击明确入口切换会话，不依赖隐藏手势。
- `Ctrl` / `Alt` 支持一次生效和锁定状态，状态明确可见。特殊按键通过终端输入适配器编码；普通文字以 UTF-8 提交，IME 组字过程不提前发送，第三方输入法行为需真机验证。
- 长按进入文本选择；显式区分本地历史滚动与远端 TUI 鼠标控制。多行粘贴原样预览并确认，不擅自逐行执行或合并命令；远端启用时使用 bracketed paste，它不是安全保证。
- 手机文件管理先收起软键盘，再打开目录列表页或可展开抽屉，提供上传、下载、重命名、删除、复制路径与传输进度。删除与覆盖需确认；系统文件选择和分享代替任意本地路径访问。
- 平板按可用窗口宽度及输入方式展开可收起侧栏、终端与 SFTP 双栏，可选最多两个终端分屏；系统分屏导致窗口变窄时回到手机布局。外接键盘输入和触控板需独立验证。
- 连接状态使用文字而非仅颜色；键盘开合、旋转和窗口调整后同步远端 PTY 尺寸。系统后台可能暂停 SFTP，界面不能把未确认的任务显示为完成。

## 四、 编译模式（Mono AOT / 解释器 vs NativeAOT）、SQLite 与依赖审查

- **iOS 运行模式辨析**：
  - iOS 默认使用 Mono 运行时。由于禁止内存写执行，Mono 采用 **Mono AOT** 将 IL 转为静态代码。
  - 通过 `<UseInterpreter>true</UseInterpreter>` 可开启 **Mono 解释器**，执行未静态化的动态代码。
  - **NativeAOT** 是另一套不带 Mono 运行时的直接机器码方案，目前不兼容 Mono 解释器。
- **依赖库审查**：
  1. **Dapper 2.1.89**：因依赖 `Reflection.Emit`，在纯 AOT 下无法运行。若开启 Mono 解释器理论上允许动态调用，但有性能损耗；同时即便使用 `Dapper.AOT` 也非绝对零风险，必须针对私有 Row 模型及 Release 裁剪进行严格回归测试。
   2. **Microsoft.Data.Sqlite 10.0.12**：**默认依赖 `SQLitePCLRaw.bundle_e_sqlite3`，打包自带的 `e_sqlite3` 原生库**，而非直接使用移动系统自带的 sqlite 库。不同架构和平台下的库文件命名与打包机制略有差异；通常由库自动初始化，发布包仍需验证原生提供者加载。
  3. **RoyalApps.RoyalTerminal.Avalonia 0.6.0-preview.2**：官方目前缺少 Android/iOS 预编译 Native Ghostty 包，并不等于未来永远禁用；在当前移动端需配置纯托管 `BasicVtProcessor`（`VtProcessorPreference.Managed`），其实机下的宽字符渲染、vim/tmux 滚动及光标查询响应需重点实机核验。
  4. **SSH.NET 2026.0.0**：历史 Issue 曾出现动态反射在 AOT 裁剪下缺失问题；需在 Release 裁剪构建中实测是否必须配置 RootAssembly 保留。

---

## 五、 KeiTerm 现有源码的跨平台审查矩阵

根据对当前代码库的核查，以下模块在移动端移植时存在明确适配风险：

| 模块 / 源码位置 | 现有实现形式 | 移动端风险分析与纠偏 | 适配建议 |
| :--- | :--- | :--- | :--- |
| **App 启动与弹窗**<br>`src/Kei.Term.App/App.axaml.cs` | 强绑定 `IClassicDesktopStyleApplicationLifetime` 与多窗口弹窗 | 移动端为 `ISingleViewApplicationLifetime`，无桌面多窗口能力，直接运行将无法呈现界面 | 抽象单视图根容器 `MainView`，弹窗全部收拢为内嵌模态层或 BottomSheet |
| **外部进程与句柄探测**<br>`src/Kei.Term.Core/Services/LocalFileTracker.cs:374` | 探测 Linux/OSX 时调用 `Process.Start("fuser", ...)` | 不能从 Linux 内核推断 Android 具备 `fuser`；iOS 严禁调用外部进程。代码内虽有 `catch` 降级，但移动端不应盲目启动子进程 | 增加移动操作系统短路，在 Android/iOS 上直接降级为普通 `FileStream` 尝试 |
| **文件选取与物理路径**<br>`AuthPromptWindow` / `IdentityEditWindow` / `KnownHostsManagerWindow` | 依赖 `IStorageFile.TryGetLocalPath()` | 在 Android SAF（`content://` URI）或 iOS 安全作用域下，`TryGetLocalPath()` 可能返回 `null`，导致私钥导入失效 | 改为直接使用 `OpenReadAsync()` 读入内存流；**严禁将私钥明文写入临时文件** |
| **内部保险库持久化**<br>`src/Kei.Term.Infrastructure/Vault/InternalVaultManager.cs` | SQLite 存储 AES-GCM 密文 + PBKDF2（HMAC-SHA512） | 算法具备跨平台基础，但设备解锁、备份与密钥失效语义需安全审查 | 评估 Android Keystore / iOS Keychain 封装及生物识别解锁，不直接迁移桌面密钥管理假设 |

---

## 六、 SSH.NET 移动兼容性、Agent 与后台保活客观限制

1. **SSH.NET 移动兼容性**：
   - 能够编译不等于实机网络行为完全一致。在移动蜂窝网络与 Wi-Fi 漫游切换、NAT 超时及弱网状态下，TCP 链路容易无感知挂起，需要应用层积极的心跳探测。
2. **SSH Agent 与硬件 SK 密钥移植限制（红线声明）**：
   - 移动操作系统沙盒内默认不存在桌面级常驻 OpenSSH Agent，也不暴露默认的 Unix Domain Socket 路径。
   - `SshNet.Agent` 是连接现有 Agent 进程的客户端协议库，自身不是 Agent 守护进程。
   - 系统 WebAuthn / Passkeys 无法直接等同于 SSH FIDO2（`ed25519-sk`）扩展。按照 KeiTerm “硬件 SK 密钥仅走 SshNet.Agent” 的架构约束，**移动端初期不应宣称支持硬件 SK 密钥与 SSH Agent**。
3. **后台保活客观事实：挂起与断线区别，重连不是恢复**：
    - **挂起与网络断开**：iOS 切后台后可能挂起进程，不能持续执行应用收发与保活；连接是否已断需回前台后检测，挂起本身不等于立即关闭 Socket。Android 前台服务需符合类型与权限要求，Android 15+ 的 `dataSync` 等特定类型另有超时配额，不能套用到所有服务。
   - **保活不作虚假承诺**：不能承诺全天候后台保持 SSH 链路。重连（Reconnect）会建立全新 PTY 会话，无法等同于原会话无缝恢复（Resume）。断线后应向用户清晰展示断开与重连状态，并建议结合远端 `tmux`/`screen` 保证工作区不丢失。

---

## 七、 结论与分步验证建议

1. **当前可行性结论**：
   - 基于现有代码库，移动端发布具备可行性，且**优先推荐 Avalonia 路线**以最大化复用 XAML 和现有资产。
   - 但现有 Core 和 App 代码**无法零修改直接运行**，必须先完成启动生命周期、外部文件读取和进程调用的解耦。
2. **推荐的三步最小可行性验证（PoC）**：
   - **第一步（UI 与终端）**：建立 `net10.0-android` 最小工程，装配 `ISingleViewApplicationLifetime` + `RoyalTerminal`（Managed VT 模式），实机验证软键盘弹出、Insets 避让与基础字符渲染。
    - **第二步（存储与 AOT）**：接入包含私有 Row 对象的 SQLite 仓储，尤其在 iOS 真机 Release 发布配置下跑通迁移、读写与保险库解锁；Android 验证不能替代 iOS AOT 验证。
   - **第三步（SSH 与后台）**：集成 SSH.NET 建立基础 Shell 与 SFTP 传输，实机观测切后台、锁屏及网络漫游下的连接挂起与重连表现。
   - *完成上述三步验证后，再启动完整产品化移植工作。*

---

## 八、 官方资料引用

1. **Avalonia UI 官方平台支持分级**：`https://docs.avaloniaui.net/docs/supported-platforms`
2. **Avalonia 移动开发指南**：
   - Android: `https://docs.avaloniaui.net/docs/platform-specific-guides/android/`
   - iOS: `https://docs.avaloniaui.net/docs/platform-specific-guides/ios/`
   - 存储服务（StorageProvider）: `https://docs.avaloniaui.net/docs/services/storage/storage-provider`
   - 安全区（InsetsManager）: `https://docs.avaloniaui.net/docs/services/insets-manager`
   - 软键盘视口（InputPane）: `https://docs.avaloniaui.net/docs/services/input-pane`
3. **.NET MAUI 平台支持策略与 Mono 运行时编译**：
   - 支持生命周期: `https://dotnet.microsoft.com/en-us/platform/support/policy/maui`
   - 运行时与编译模式（Mono AOT / 解释器）: `https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0`
4. **Android 官方前台服务规范**：
   - 前台服务类型与约束: `https://developer.android.com/develop/background-work/services/fgs/service-types`
   - 前台服务超时行为: `https://developer.android.com/develop/background-work/services/fgs/timeout`
5. **RoyalApps.RoyalTerminal 官方架构**：`https://github.com/royalapplications/RoyalTerminal`
6. **SQLite 原生库选用**：`https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions`
7. **Apple 应用后台生命周期**：`https://developer.apple.com/documentation/uikit/extending-your-app-s-background-execution-time`
