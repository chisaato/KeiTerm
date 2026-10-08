# Touch ID 本机快速解锁

macOS 实现复用系统已录入的 Touch ID，使用 `LocalAuthentication` 的生物验证策略。
主密码仍然负责首次启用、保险库恢复和 Touch ID 不可用时的解锁。
应用不保存主密码，当前用户的传统 Keychain 只保存该保险库的 32 字节 MEK。
项目使用独立 service/account，不设置 iCloud 同步；本机材料不进入应用配置或备份。

这是本机便利解锁：应用先完成 Touch ID 验证，再从钥匙串取出 MEK；钥匙串项本身未绑定
`biometryCurrentSet` 或 Secure Enclave。拥有当前系统用户权限的其他程序不能被这层应用流程
当作独立的安全边界。钥匙串由系统加密及访问控制保护，应用的保险库继续使用原有加密。

## 构建与打包

在 Infrastructure 项目中导入 `Vault/MacOS/MacOSQuickUnlock.targets`。
Mac 上构建需安装 Xcode Command Line Tools；targets 使用 SDK 编译包含 arm64/x86_64 的
`libkei_quick_unlock.dylib`，自动复制到输出与 publish 目录，单文件发布时保持该库为旁置文件。
现有 `just bundle-macos` 会先签该 dylib，再签 `.app`；无需新增 Keychain entitlement。
macOS 最低版本沿用现有的 11.0。发布 osx 目标需在 Mac 上构建。

正式分发应沿用稳定的 Developer ID 签名身份。开发过程中切换可执行文件、ad-hoc 签名或升级
导致签名身份变化时，传统 Keychain 可能重新要求系统钥匙串授权。系统钥匙串锁定时也可能要求
输入钥匙串密码；Touch ID 成功不保证能解锁传统 Keychain。上述情况下可继续使用主密码。

## 手工验证

在有已录入 Touch ID 的 Mac 上，使用最终签名的 `.app`：

1. 输入主密码启用本机快速解锁，锁定后使用 Touch ID，确认保险库内容正常解密。
2. 取消系统验证，关闭应用的解锁窗口，确认不解锁且下一次可以重试。
3. 验证过程中手动锁定/关闭窗口，确认迟到回调不能解锁保险库。
4. 系统锁屏、睡眠、拔下带 Touch ID 的键盘，再确认主密码入口可用。
5. 更换主密码、停用快速解锁后，确认旧项不能解锁当前保险库。还原旧备份时确认需重新启用；
   若旧盐对应的钥匙串项仍存在，还原旧库可能重新匹配该项，不能假定备份之间也完成了撤销。
6. 测试没有传感器或未录入指纹的设备、重新签名后的安装包及系统钥匙串锁定状态。

自动化测试只验证无交互的原生 ABI 和取消流程，不写入用户钥匙串，也不触发 Touch ID 弹窗。

参考：

- [Apple LAContext](https://developer.apple.com/documentation/localauthentication/lacontext)
- [Apple Mac keychain implementations](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains)
- [Apple Keychain services](https://developer.apple.com/documentation/security/keychain-services)
