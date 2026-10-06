# 本机编译运行 — 缺失项清单

> 生成时间：2026-10-04 ｜ 主机：macOS 27.0.1 / Apple Silicon (arm64)
> 仓库状态：`1a075df feat: 继续做 roadmap 的东西,但还没完成`，工作区干净

## 结论速览

| 阶段 | 结果 |
| --- | --- |
| 编译 `dotnet build Kei.Term.slnx` | ✅ 成功 — 0 错误 / **2 警告** |
| 运行 `dotnet run --project src/Kei.Term.App` | ✅ 成功 — 窗口已渲染（见下方证据） |
| 实测操作（打开设置/身份/会话编辑） | ⚠️ 抓到 **2 条 Critical：首次启动设置页查询早于建表，异常被静默吞掉**（见 F 节） |
| 实测操作（关闭终端标签） | ⚠️ **3/3 复现**：跨线程操作控件，每次关标签泄漏一条 SSH 会话与文件管理器（见 G 节） |
| 测试 `dotnet test` | ⚠️ 400 总计 — **390 通过 / 4 失败 / 6 跳过** |

运行证据（`~/Library/Application Support/KeiTerm/logs/`）：

```
[INF] [App] 应用启动 版本=1.0.0.0 数据目录=/Users/0x33a/Library/Application Support/KeiTerm
[INF] [App] 启动完成: 主窗口已在 await 之前同步赋值
[INF] [App] 数据库 Schema 版本=5
[INF] [InternalVaultManager] Vault 初始化完成 模式=明文
[INF] [Avalonia] [Layout]Layout pass finished in '00:00:00.0017307'
```

`lsappinfo` 确认 `Kei.Term.App` 为 Foreground 可见窗口，进程持续响应布局。

---

## A. 阻塞编译的缺失项（已修复）

### A1. .NET 10 SDK —— 本机完全没有

- 症状：`which dotnet` 无结果，`/usr/local/share/dotnet`、`/opt/homebrew/share/dotnet`、`~/.dotnet` 均不存在，Spotlight 也搜不到。
- 处理：已通过官方脚本安装 **SDK 10.0.401** 到 `~/.dotnet`。
- **遗留动作**：`~/.dotnet` 不在 `PATH` 中，当前每个命令都要显式 `export PATH="$HOME/.dotnet:$PATH"`。建议固化：

```bash
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc
echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> ~/.zshrc
source ~/.zshrc
```

- 注意：`dotnet --list-runtimes` 只有 `Microsoft.NETCore.App 10.0.12` 与 `Microsoft.AspNetCore.App 10.0.12`，**没有 WindowsDesktop 运行时**。这是 macOS 上的正常现象，不影响本项目。

### A2. `.slim/clonedeps/repos/royalapplications__RoyalTerminal` —— 未 clone

`.slim/clonedeps.json` 声明了该依赖（`ref a1957b0...`，用于查终端行高量/渲染滚动同步），但 `.slim/clonedeps/` 整个目录不存在。

- 影响：**不阻塞编译**（NuGet 包 `RoyalApps.RoyalTerminal.Avalonia 0.6.0-preview.2` 已正常还原），只影响需要读上游源码时。
- 处理：按需 clone 到该路径。

---

## B. 阻塞"完整验证"的缺失项

### B1. macOS 无 `/run`，Linux 专用测试直接崩溃

`tests/Kei.Term.Tests/PortForwardTests.cs:312` 中 `LocalSshd` 构造函数硬编码 `Directory.CreateDirectory("/run/sshd")`。macOS 根卷受 SIP 保护且无 `/run`：

```
System.IO.IOException : Read-only file system : '/run'
  at Kei.Term.Tests.PortForwardTests.LocalSshd..ctor() ... PortForwardTests.cs:line 312
```

该测试（`:207`）是裸 `[Fact]`，**没有像 `SshIntegrationTests` 那样用 `KEITERM_SSHD_TESTS` 守卫**，所以在任何非 Linux 机器上必失败。

### B2. sshd 集成测试被跳过（6 个）

`SshIntegrationTests` 与上述 `PortForwardTests` 都需要真实 sshd：

- 本机 `/usr/sbin/sshd`、`ssh-keygen`、`ssh-add`、`ssh-agent` **都存在**；
- 但即使设 `KEITERM_SSHD_TESTS=1`，仍会撞上 B1 的 `/run/sshd`，可用路径需先改；
- 当前 `SSH_AUTH_SOCK=/var/run/com.apple.launchd.../Listeners`，`ssh-add -l` 报 `The agent has no identities.` —— 若后续要测 agent 认证，需先导入一把钥匙。

---

## C. 仓库缺失的工程配置

以下文件在仓库中**均不存在**（`ls -a` 仅 `.agents .autocorrectrc .git .gitignore .ignore .slim .superpowers`）：

| 缺失文件 | 影响 |
| --- | --- |
| `global.json` | SDK 版本未固定。README/AGENTS.md 要求 .NET 10，但无机器可校验，本机装的是 10.0.401 |
| `NuGet.config` | 无源固定。实测 `api.nuget.org` 被 302 重定向到 `nuget.azure.cn`（区域镜像），CI 与他机行为不可复现 |
| `.editorconfig` | AGENTS.md 写了 `var` 使用规范与注释规范，但**没有任何工具强制**，纯靠人工 review |
| `Directory.Build.props` | 5 个 csproj 各自重复 `TargetFramework/Nullable/ImplicitUsings`；`TreatWarningsAsErrors`、`LangVersion`、分析器级别无处统一配置 |
| `.config/dotnet-tools.json` | 无工具清单（`just` 是外部依赖，未纳入版本管理） |
| `.github/workflows/` | **无任何 CI**。这正是 3 个稳定失败的测试能长期留在主干而不被发现的原因 |

---

## D. 构建警告（2 个）

| 位置 | 代码 | 说明 |
| --- | --- | --- |
| `src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.cs:238` | `CS8602` | `session.ForwardStartErrors` 解引用可能为空。`session` 为 `ISshSession?`，在 `if (failure == null)` 分支内编译器无法确认其非空（同分支下方 `StartFileSystem(..., session!, ...)` 已用 `!` 抑制，此处漏了） |
| `src/Kei.Term.App/Views/SessionPickerWindow.axaml:15` | `AVLN5001` | `TextBox.Watermark` 已过时 → 应改 `PlaceholderText` |

---

## E. 测试失败明细

### E1. 稳定失败 ×3（每次必现）

**① ② 认证兜底行为与测试预期不一致**

```
ConnectionOrchestratorTests.NoMaterials_AndFallbackCancelled_OpensNoTab          [line 121]
ConnectionOrchestratorTests.Reconnect_AuthCancelled_LeavesExistingTabUntouched  [line 148]
```

根因在 `src/Kei.Term.App/Services/Connection/AuthMaterialCollector.cs:70-77`：

```csharp
if (materials.Count == 0)
{
    string? effectiveUser = identity?.Username ?? config.Username;
    if (!string.IsNullOrWhiteSpace(effectiveUser))
    {
        // 未配置认证材料，先尝试直连免密登录
        materials.Add(new MaterializedAuthMethod(AuthMaterialKind.Password,
            new SecretPayload { Password = string.Empty }));
    }
    else
    {
        // 仅当连用户名都没有时才弹认证窗
    }
}
```

该"免密直连"分支是最近一次 WIP 提交引入的。测试用的 `Config()` 带 `Username = "ops"`，于是**不再进入兜底弹窗**：弹窗计数为 0（`Assert.Single` 失败），且照常建标签 / 复位重连目标（`Assert.Empty` 失败）。

**结论**：不是产品回归，而是**测试停留在旧行为**。需要决策——若"有用户名就先试空密码"是有意设计，则这两个测试要改成用无用户名的配置来触发兜底路径；若不是，则回退 `AuthMaterialCollector` 的分支。

**③ Linux 专用测试未加守卫** —— 即 B1，`PortForwardTests.cs:312`。

### E2. 不稳定失败 ×1（每次不同，属并发/线程亲和问题）

同一槽位在 6 次运行中随机落在不同测试上：

- `TerminalHostTests.ClearScreen_RemovesScrollback_SoNothingLeftToScroll`
- `TerminalLineHeightTests.TerminalControl_ScrollDataHistoryAndMidBottomOffset_MaintainsPositions`
- `TabBarAndConnectionStateTests.TerminalTabViewModel_StateTransitions_MaintainCompatibility`
- `TerminalTabTitleHeadlessTests.BellInBackgroundTab_MarksActivity_UntilSelected`
- `Contracts.TreeThemeContractTests.TreeLines_LastChildHasNoBottomOverflow`

统一报错：

```
System.InvalidOperationException : The calling thread cannot access this object
because a different thread owns it.
  at Avalonia.Threading.Dispatcher.VerifyAccess()
  at Avalonia.AvaloniaObject.GetValue[T](StyledProperty`1 property)
  ...
  at Kei.Term.App.ViewModels.TerminalTabViewModel.AlignGridToBottom()
       in TerminalTabViewModel.Control.cs:line 271
```

`AlignGridToBottom()`（`TerminalTabViewModel.Control.cs:263`）在 `:271` 读取 `_terminal.Padding.Top`（`TemplatedControl` 的 `StyledProperty`）与 `_terminal.Bounds`，但调用点 `:223` / `:252` / `:259` 不都保证在 UI 线程上（对比 `:134` 是有 `Dispatcher.UIThread.Post` 包裹的）。

`Avalonia.Headless.HeadlessUnitTestSession` 跨并行测试集合共享 Application，谁先踩到这条跨线程路径，谁就随机翻车。

**修复方向**（二选一或并用）：
1. 把 `AlignGridToBottom()` 内部改为 `Dispatcher.UIThread.Post(...)` / `CheckAccess()` 守卫，或只缓存非控件状态（行高、尺寸）后再套用；
2. 测试侧用 `[Collection]` 串行化所有触碰 Avalonia 控件的 headless 测试。

---

## F. 【实测发现】首次启动设置页查询早于建表：确定性必现，异常被静默吞掉

这一条是**实际跑起来操作时**在日志里抓到的，不是静态分析。

首次启动（全新数据库）后打开设置窗口，日志出现两条 `Critical`：

```
[CRT] [App] TaskScheduler 未观察任务异常
  SQLite Error 1: 'no such table: external_editors'
  → SqliteExternalEditorRepository.GetAllEditorsAsync  (SqliteExternalEditorRepository.cs:36)
  → FileTransferSettingsPage.ReloadAsync                (FileTransferSettingsPage.cs:103)

[CRT] [App] TaskScheduler 未观察任务异常
  SQLite Error 1: 'no such table: identities'
  → SqliteIdentityRepository.GetAllAsync                (SqliteIdentityRepository.cs:33)
  → SshSettingsPage.LoadIdentitiesAsync                 (SshSettingsPage.cs:98)
```

### 根因：启动顺序倒置

`App.axaml.cs` 里 ViewModel 的构造**早于**数据库迁移：

```
:113   var settingsVm = new SettingsViewModel(...)   ← 构造函数内部就发起了查询
:138   var schemaVersion = await SchemaMigrator.MigrateAsync(db);   ← 建表在这里，太晚了
```

顺着构造函数看下去：

```
SettingsViewModel.cs:64   构造函数
SettingsViewModel.cs:153  → ReloadCore()
SettingsViewModel.cs:169      _ = _fileTransfer.ReloadAsync();      ← fire-and-forget
SettingsViewModel.cs:235      _ = _ssh.LoadIdentitiesAsync();       ← fire-and-forget
```

另外 `Views/SettingsWindow.axaml.cs:119` 还有一处 `_ = proxyPage.ReloadAsync();`。

这三处都是**裸 fire-and-forget，没有 try/catch**。全新库上查询先于建表打到 SQLite，于是抛 `no such table`；异常无人观察，被 `TaskScheduler.UnobservedTaskException`（`App.axaml.cs:176`）在 **Task 被 GC 时**才记下来。

### 为什么是必现而不是竞争

这一点曾被我误判为"时序竞争"，实测后纠正：**整条查询链是同步跑完的**，因此构造函数里必然先于迁移命中空库。

隔离实验（`Microsoft.Data.Sqlite 10.0.12` + `Dapper 2.1.89`，与仓储同形的调用链，在建表前发起查询后立刻检查返回的 Task）：

```
调用后立刻检查 -> IsCompleted = True
                  IsFaulted   = True
                  是否同步跑完 = 是（整条链内联执行）
  异常: SqliteException: SQLite Error 1: 'no such table: external_editors'.
```

原因是 **SQLite 没有真正的异步 I/O**：`SqliteConnection.OpenAsync` / `SqliteCommand.ExecuteReaderAsync` 都是同步实现后包一层 `Task.FromResult`。Dapper 的 `QueryAsync` 在已完成的 Task 上继续时不会跳到线程池。所以 `await` 只是语法糖，代码实际上是在 **UI 线程的构造函数里内联执行完毕**。

结论：只要库是全新的，`ReloadCore()` 里那两个查询 **100% 失败**，与调度顺序无关。

### 真正的设计张力

`App.axaml.cs:63-65` 有一条硬约束：

```
// 关键：全部同步构造，主窗口必须在任何 await 之前赋值。
// lifetime 在 OnFrameworkInitializationCompleted 同步段结束后即进入 Start() 显示阶段，
// 此时 MainWindow 若为 null，之后再赋值不会触发 Show → 进程存活但窗口永不出现。
```

于是形成死结：**窗口必须在首个 await 前装配完毕** ⇒ 所有 ViewModel 必须在 `:113` 同步构造 ⇒ 仓储一经构造即可被查询，而 Schema 要到 `:138` 才就绪。构造函数是同步的，无法在里面 `await` 迁移。

### 为什么难以察觉

- 报错时机严重滞后：失败发生在 `23:17:48`，日志却打在 `23:20:12`（终结器线程 GC 时才重抛），看起来像"打开设置窗口时出的错"，与真实原因（启动顺序）完全脱节。
- **第二次全新库启动没有打出这条日志**——但**失败照样发生了**，只是进程在 GC 回收那个 faulted Task 之前就退出了（存活仅 2.4 秒）。日志缺失 ≠ 问题不存在，这正是该 bug 最阴的地方。
- 后果是静默的：首次启动时设置的"身份"下拉与"外部编辑器"列表就是空的，界面上没有任何提示。
- 本机 `~/Library/Application Support/KeiTerm` 首次启动时确实不存在（`23:17:47` 才创建），所以踩中了。

### 修复方向

按推荐度排序：

1. **把迁移挂到 `OpenAsync` 这个唯一入口上**（最彻底）。AGENTS.md 已规定"统一经 `SqliteConnectionFactory.OpenAsync` 开连接"，而 `SchemaMigrator.MigrateAsync` 本身已具备所需语义——`:14` 注释声明"可安全重入"，`:36` 用 `BEGIN IMMEDIATE` 保证"并发启动的两个迁移者只有一个能拿到写锁"，且 `:28` 版本已达标即早退。做法：工厂内缓存一个 `Lazy<Task>` 的迁移任务，公开 `OpenAsync` 先 `await` 它再真正开连接；同时拆一个私有的 raw-open 供 `MigrateAsync` 自己使用，避免 `MigrateAsync → OpenAsync → MigrateAsync` 递归。这样任何调用方都不可能查到未迁移的库。
2. 把 `ReloadCore()` 的加载推迟到迁移之后（例如由 `mainVm.InitializeAsync()` 之后再触发），并给三处 fire-and-forget 套 `try/catch` + 日志——至少不再静默。
3. 简单但阻塞启动：在构造 ViewModel 之前同步跑完迁移（`MigrateAsync(db).GetAwaiter().GetResult()`）。此时尚无 UI 上下文，死锁风险低，但会拉长冷启动。

---

## G. 【实测发现】关闭终端标签时跨线程操作控件，导致会话与文件管理器泄漏

这条是 2026-10-06 真实使用中触发的（关闭一个正在输入过的终端标签 `chisato`）：

```
[WRN] [MainViewModel] 后台释放终端标签异常 标题=chisato
  System.InvalidOperationException: The calling thread cannot access this object
  because a different thread owns it.
    at Avalonia.Threading.Dispatcher.VerifyAccess()
    at RoyalTerminal.Avalonia.Controls.TerminalControl.SetCompositionText(String, Nullable`1)
    at RoyalTerminal.Avalonia.Services.TerminalTextInputMethodClient.Reset()
    at RoyalTerminal.Avalonia.Controls.TerminalControl.ResetKeyboardInputState()
    at RoyalTerminal.Avalonia.Controls.TerminalControl.DetachEndpoint()
    at Kei.Term.App.ViewModels.TerminalTabViewModel.DetachSessionAsync()   TerminalTabViewModel.cs:234
    at Kei.Term.App.ViewModels.TerminalTabViewModel.DisposeAsync()        TerminalTabViewModel.cs:408
    at Kei.Term.App.ViewModels.MainViewModel.CloseTabAsync                MainViewModel.cs:1305
```

### 根因：`Task.Run` 的意图与控件线程亲和冲突

`MainViewModel.cs:1300` 有意把释放放到后台，避免网络读取阻塞 UI：

```csharp
// 后台异步清理底层会话与网络资源，避免任何网络读取阻塞导致 UI 卡顿
_ = Task.Run(async () =>
{
    try { await tab.DisposeAsync(); }
    catch (Exception ex) { _logger.LogWarning(ex, "后台释放终端标签异常 标题={Title}", tab.Title); }
});
```

但 `Task.Run` 会切到**线程池线程**，而 `DisposeAsync` → `DetachSessionAsync` 里第 234 行 `_terminal?.DetachEndpoint()` 是要碰 Avalonia 控件的。控件只要已被创建并挂到 TopLevel（连接过的标签必然如此），`TerminalTextInputMethodClient` 就存在，`Reset()` → `SetCompositionText()` 会走 `VerifyAccess()` 并抛异常。

### 触发率：几乎每次关标签都中

累计观测 **3 次关标签，3 次全部命中**（2026-10-06 三次会话，堆栈逐字一致）：

| 时间 | 标签 | 结果 |
| --- | --- | --- |
| 11:36:43 | `chisato` | 命中 |
| 11:48:19 | `root@chisato-svc:~` | 命中 |
| 11:48:47 | `root@chisato-svc:~` | 命中 |

早先我以为"只有在那个终端里输入过才会命中"，实测否定了这个推测——触发条件只是**控件已创建并挂载**，对连过的标签来说等于必然。所以这不是边缘情况，而是**关一个标签泄漏一条会话**。

### 真正的问题：异常把清理流程截断了

异常被 `catch` 兜住，所以进程不崩——但它是从 `DetachSessionAsync` 第 234 行抛出的，**后面的清理全部被跳过**：

```csharp
_endpoint?.Dispose();          // 233  已执行
_terminal?.DetachEndpoint();   // 234  ← 在这里抛出
_endpoint = null;              // 235  跳过
var session = _session;        // 237  跳过
_session = null;               // 238  跳过
session.Disconnected -= ...;   // 241  跳过 → 事件处理器泄漏
session.OutputReceived -= ...; // 242  跳过 → 事件处理器泄漏
await session.DisposeAsync();  // 243  跳过 → SSH 会话泄漏
```

而 `DisposeAsync` 里第 409 行也一并跳过：

```csharp
await DetachSessionAsync();                        // 408  ← 抛出
await CloseFileManagerAsync(releaseOnly: true);    // 409  跳过 → 文件管理器泄漏
_logger.LogInformation("终端标签已释放 ...");       // 410  跳过
```

**后果：每次关标签都泄漏一条 SSH 会话 + SFTP/文件管理器资源，并留下未解绑的事件处理器。** 关 20 个标签就漏 20 条会话，而日志里只有一条 WRN，UI 上看不出任何异常。

### 与 E2 的关系

E2（`AlignGridToBottom` 读 `Padding`）和本条是**同一类**线程亲和问题，但本条是真实使用路径上稳定可触发的，且后果是资源泄漏而非仅测试抖动。修 E2 时应当一并处理：

- `DisposeAsync` / `DetachSessionAsync` 中触碰控件的部分必须 `Dispatcher.UIThread.Post` 回 UI 线程，或把控件解绑与网络释放拆成两段（控件部分回 UI 线程，会话释放留在后台）；
- `DetachSessionAsync` 里的清理应当用 `try/finally` 保护，避免前面一步失败导致后面全部跳过。

---

## H. 建议处理顺序

1. **修首次启动顺序 bug**（F）—— 唯一一个确定性影响真实用户首次体验的问题，且修复成本最低。
2. **修关标签时的会话泄漏**（G）—— 3/3 复现率，每次关标签泄漏一条 SSH 会话。
3. **固化 PATH**（A1 遗留动作）—— 否则每个新 shell 都要手动 export。
4. **补上 `global.json` / `NuGet.config`**，把 SDK 版本与包源固定下来（C）。
5. **给 `PortForwardTests` 加平台守卫**（B1/E1③）—— 一处小改，恢复 1 个测试。
6. **决策"免密直连"是否保留**（E1①②）—— 产品行为问题，需要你拍板。
7. **修 `AlignGridToBottom` 线程亲和**（E2）—— 与 G 同源，消除随机失败，恢复测试可信度。
8. **加最小 CI**（C）—— 让上述失败无处遁形。
9. 顺手清 2 个构建警告（D）。
