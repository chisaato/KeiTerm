# 02 同步包格式（age 口令模式）

Status: ready-for-agent
Blocked by: None (can start immediately)

见 `../spec.md`「同步包格式」，以及 ADR-0004。

**要做什么：** 一个可独立验证的组件，能把字节流用同步口令加密成 age v1 文件（单一 scrypt recipient），也能把 age 口令模式的文件解开；与标准 `age` 工具互通。

- [ ] 先评估两种实现并在本文件的 Comments 里写结论：用项目已依赖的 NSec 自研 scrypt recipient 与 STREAM 分块，或引入 AgeSharp（预览版，依赖 BouncyCastle）。评估项包括：许可证、依赖体积、跨平台（含未来移动端）、维护风险。
- [ ] 加密使用 age 默认工作因子；解密接受合理的工作因子范围，超过上限的拒绝，以防恶意文件耗尽资源。
- [ ] 口令错误、文件截断、头部被篡改、非 age 文件时分别给出可区分的错误，且不产生部分明文输出。
- [ ] C2SP/CCTV 中与 scrypt recipient 和 STREAM 相关的测试向量全部通过。
- [ ] 本机装有 `age` 时：我们加密、`age -d` 解密成功；`age -p` 加密、我们解密成功。未安装时这两项测试跳过。
- [ ] 口令与派生出的密钥材料用完后清零。
