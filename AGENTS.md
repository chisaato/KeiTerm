<!--
AGENTS.md - 针对 AI / Agent 协作的工程规范与运行指令
-->

## 常用命令

```bash
# 构建工程
dotnet build Kei.Term.slnx

# 执行单元测试
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 含真实 sshd 的集成测试（Linux，需要 /usr/sbin/sshd）
KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 运行应用程序
dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj
```

## 开发规范与架构约束

- **技术栈**：.NET 10 (C# 13) + Avalonia 12.x + CommunityToolkit.Mvvm。
- **命名空间与分层**：
  - 代码项目与命名空间统一使用 `Kei.Term.*` 前缀。
  - `Kei.Term.Core` 严禁引入任何 UI（Avalonia）或平台特定（P/Invoke）依赖。
  - `Kei.Term.Infrastructure` 与 `Kei.Term.Ssh` 仅面向 `Kei.Term.Core` 中的抽象接口进行实现注入。
- **SSH 与密钥处理约束**：
  - 硬件 SK 密钥（`ed25519-sk` / `ecdsa-sk`）**只走 `SshNet.Agent`** 桥接，不直接绑定底层 Yubico/CTAP2 库。
  - 普通私钥明文仅在内存流转，禁止记录到明文日志或写入未加密持久化文件。
  - 所有 SSH 连接（终端 / SFTP / SCP / 跳板）必须经 `SshDialer` 建立，以保证主机密钥校验、保活与跳板语义一致；远端 shell 命令中的路径一律 `ShellQuote`。
- **持久化约束**：
  - SQLite 表必须启用 `PRAGMA foreign_keys = ON;` 保证级联约束有效——统一经 `SqliteConnectionFactory.OpenAsync` 开连接，禁止仓储自行 `new SqliteConnection`。
  - Schema 变更只能在 `Storage/Schema/SchemaMigrations.cs` 追加版本号 +1 的迁移；已发布的迁移不得修改，仓储内不得 `CREATE TABLE`。
  - 仓储使用 Dapper + 私有行对象（`XxxRow`）映射，领域模型不带持久化特性；时间列用 `SqliteValue.FormatUtc/ParseUtc`。
  - 目录树采用 `parent_id` 扁平持久化 + 启动时单次加载至内存组树模型，避免递归 SQL。
- **ViewModel 规模**：
  - 新增弹窗一律走统一的交互服务（见 `docs/design/10-refactor-plan.md`），不再往 ViewModel 上加 `Func<>` 弹窗属性。
  - ViewModel 超过约 600 行时，先按职责拆分再加新功能。
- **测试**：测试必须能因生产行为出错而失败；不写只测 Mock、按源码文本/调用顺序断言、回显常量或默认值的测试（XAML 契约测试除外）。
- **代码与注释**：
  - 多用行内注释，少用行后注释。
  - 所有新增的核心业务逻辑与算法变更，需在 `tests/Kei.Term.Tests` 中补充对应的单元测试。

## Agent 的选用

对于界面修复类的 Agent 使用 @designer.

代码逻辑类的,可以使用主会话进行,或 @fixer 视任务复杂度决定.

## 代码规范

### var 的使用

什么时候不使用 var

- 类型可以静态且轻易推导
- 处于简短的函数调用中,尤其是获取数据时
- 多条复制声明堆叠

其中 `多条声明堆叠` 指的是

```csharp
var a = 1;
var b = new A();
var c = new B();
var d = XXX
```

考虑到人的完形崩坏风险,需要避开大量近似的 var 重复.

什么时候使用 var

- 在代码中短暂地使用,例如很快就结束的小方法体
- 长链式方法后无法准确推断类型时
- Lambda式链式调用的返回值接收器. (即无法轻松推导)
- 包含有泛型或类型参数等,可能导致类型表示过场且包裹时
