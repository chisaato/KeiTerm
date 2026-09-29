using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

// 夹具均为 ssh-keygen 生成的一次性测试密钥
public class PrivateKeyFormatTests
{
    // ssh-keygen -t ed25519 -N secret（OpenSSH 新格式，ssh-keygen 默认）
    private const string EncryptedOpenSsh = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABCazjLsc6
        f1is/3n3PlcPx7AAAAGAAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIEBMkE2sJ3039rGD
        cfhpJfuFk6fMrz8IrdBl5YA1ZJTyAAAAkFARr+0h+ooxZJ+Pd9yqa4a+betiz6Yai+1H1n
        vmvz/GWpISFut9d88ZDhoCy61Zx4RqAfAy9pb7EFFyv4NkMTZD5CgORAjVQMB6spOAkl/p
        68kX90z0fA84S+MnWIorq7FHJdwFuYk3ZOCt3k/PBYU3TmCCVZSCQgkRwTPnSiMUq3urZb
        pO0RRgjgWRcaKOMg==
        -----END OPENSSH PRIVATE KEY-----
        """;

    // ssh-keygen -t ed25519 -N ""
    private const string PlainOpenSsh = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        QyNTUxOQAAACCB2EQeqe+IfPntKgzxoozYKj7Fy9wGBEsgDPWf2/OmmAAAAJC3fA0ot3wN
        KAAAAAtzc2gtZWQyNTUxOQAAACCB2EQeqe+IfPntKgzxoozYKj7Fy9wGBEsgDPWf2/OmmA
        AAAEAneDr9usBnNCDBTw8G3/d7NUKSGX4AVw34/o7dycusgYHYRB6p74h8+e0qDPGijNgq
        PsXL3AYESyAM9Z/b86aYAAAAB3Jvb3RAdm0BAgMEBQY=
        -----END OPENSSH PRIVATE KEY-----
        """;

    [Fact]
    public void OpenSshNewFormat_DetectedByCipherName()
    {
        // 回归：旧实现在文本中查找 "bcrypt"，而该字段位于 Base64 内部，加密密钥被误判为无口令
        Assert.DoesNotContain("bcrypt", EncryptedOpenSsh);
        Assert.True(PrivateKeyFormat.IsEncrypted(EncryptedOpenSsh));
        Assert.False(PrivateKeyFormat.IsEncrypted(PlainOpenSsh));
    }

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,19AC\n\nAAAA\n-----END RSA PRIVATE KEY-----", true)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nMIIFHD\n-----END ENCRYPTED PRIVATE KEY-----", true)]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\n", true)]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\n", false)]
    [InlineData("not a key", false)]
    public void OtherFormats(string content, bool expected)
        => Assert.Equal(expected, PrivateKeyFormat.IsEncrypted(content));
}
