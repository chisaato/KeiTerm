# 本机快速解锁

入口在「设置 → SSH → 保险库」。保险库必须先设置主密码，启用时再次验证主密码。
登记是即时的本机授权操作，设置窗口的「取消」不会撤回它；撤销须点击「停用」。
需要保险库材料时，主密码窗口可选 Touch ID 或 Windows Hello，密码输入始终保留。
应用启动和非交互的自动重连不会主动触发系统验证。

| 平台 | 系统验证 | 密钥保管 |
| --- | --- | --- |
| macOS | LocalAuthentication 的生物验证策略，复用已录入 Touch ID | 当前用户本机 Keychain，不启用 iCloud 同步 |
| Windows | UserConsentVerifier 桌面窗口互操作，复用 Windows Hello 的人脸、指纹或 PIN | CurrentUser DPAPI 加密后存入 Windows 凭据管理器 |
| Linux | 暂不支持快速解锁 | 继续输入主密码 |

这是本机便利解锁：系统验证与密钥保管分开，应用只在系统验证成功后读取密钥，
没有声称密钥由 Secure Enclave / TPM 以生物信息绑定。Windows 的 PIN 属于正常回退方式，
当前实现不强制只能用人脸或指纹。设备、录入、策略或桌面互操作 API 不可用时回退主密码。
Windows 后端先检查系统版本，再实际探测桌面互操作接口和 Hello 能力，未通过探测即禁用入口。

Core 只声明平台存储与保险库接口，P/Invoke 全部在 Infrastructure。
本机存储只接收 32 字节 MEK，不保存主密码、SSH 私钥明文或 SSH 密码。
密钥标识由保险库 KDF 盐的 SHA-256 生成；主密码轮换生成新盐，旧项即使删除失败也不能
解锁当前保险库。还原包含旧盐的完整旧备份可能重新匹配尚未删除的旧登记，不能视为备份间撤销。
还原、复制保险库不会携带系统存储项；同一机器上相同盐的库副本会匹配同一登记。
解锁仍以保险库现有验证密文检验 MEK，跨库密钥、损坏密钥和迟到结果都必须被拒绝。
托管副本、自有原生缓冲区与验证明文用完清零，系统返回的数据对象按系统 API 释放。

多个连接共享一次保险库解锁交互。取消系统验证后返回主密码窗口供显式重试。
锁定和停用递增保险库提交版本，阻止尚未提交的系统验证结果重新装入 MEK；
停用保留当前已解锁的会话。取消启用会补偿删除可能已写入的设备项。
更换主密码删除旧项，当前库的新盐同时使旧密钥失效。

macOS 构建会编译 arm64/x64 通用原生库并传递到应用、测试及发布目录，需要 Xcode Command Line Tools。
传统 Keychain 在锁定、ACL 或签名身份变化时可能另行请求系统授权。
发布签名及真实 Touch ID 的验证步骤见 [macOS 打包说明](../../packaging/macos/QUICK_UNLOCK.md)。
Windows 发布无需附加原生库；需在实际 Hello 设备验证成功、取消、锁屏、硬件断开和立即重试。

自动化测试使用真实 SQLite 和加密保险库验证登记、主密码证明、重启后解密、密钥损坏、
密码轮换、取消补偿、锁定/停用竞态、密码回退、并发连接及界面按钮。
macOS 原生 ABI 测试不触发生物验证、不读写用户钥匙串；Windows 的 DPAPI/凭据存储测试
只在 Windows 执行，使用随机测试标识并清理，系统认证由测试边界替换。
真实传感器与最终签名安装包仍需人工验证。

参考：[Apple LAContext](https://developer.apple.com/documentation/localauthentication/lacontext)、
[Apple macOS Keychain](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains)、
[Microsoft 桌面 UserConsentVerifier](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nf-userconsentverifierinterop-iuserconsentverifierinterop-requestverificationforwindowasync)。
