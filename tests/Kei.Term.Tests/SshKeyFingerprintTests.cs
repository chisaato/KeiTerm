using System;
using Kei.Term.Ssh.Services;
using Xunit;

namespace Kei.Term.Tests;

// SshKeyFingerprint 测试：测试向量为 ssh-keygen 生成的 OpenSSH 私钥，
// 期望值与 `ssh-keygen -l -E sha256` 输出的冒号 hex 一致（精确断言）。
public class SshKeyFingerprintTests
{
    // ssh-keygen -t ed25519 -N "" 生成
    private const string Ed25519Key = """
-----BEGIN OPENSSH PRIVATE KEY-----
b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
QyNTUxOQAAACDCV/rL06pwkWBUw0wf4mtVRycXYe4Om5R2s0WLrRQbPwAAAJivEjPlrxIz
5QAAAAtzc2gtZWQyNTUxOQAAACDCV/rL06pwkWBUw0wf4mtVRycXYe4Om5R2s0WLrRQbPw
AAAEAwXbzOHoTM9YbaU34sA5JPnVEA2rm+BM9y7KdeVEWDBsJX+svTqnCRYFTDTB/ia1VH
Jxdh7g6blHazRYutFBs/AAAAEmd6emNoaEBnenotZGVza3RvcAECAw==
-----END OPENSSH PRIVATE KEY-----
""";

    // ssh-keygen -t ed25519 -N "test-pass" 生成
    private const string EncryptedEd25519Key = """
-----BEGIN OPENSSH PRIVATE KEY-----
b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABDRCSo2j2
pYnCRpR09xIFqTAAAAGAAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIEfyZIqGzrId2rkO
/Hlq9/8hjcX0cOAOqf9RBMHF30CIAAAAoKHqpIkaoGnI/4hlttqci++X91kCEEHLYuPydy
iFTLvDXcmDUKLxhfu7aR3o+mWhUhO4EeM9dcMaQJVBG1XxL+RAWkJa6qexhW3+LPCI+S1Q
o2DhlJTb0SbQ5JihYr8GT6uF6JwIw45BiKzUGhCRDl28D4KJXwRBo+qFoe7AAWjn4diaI2
4HRRUMujCt8F1++klRsxUsG6r6jFmpjCEWbsI=
-----END OPENSSH PRIVATE KEY-----
""";

    // ssh-keygen -t rsa -b 2048 -N "" 生成
    private const string RsaKey = """
-----BEGIN OPENSSH PRIVATE KEY-----
b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAABFwAAAAdzc2gtcn
NhAAAAAwEAAQAAAQEAyHjUx7UsDnTctUEGqLg5flFLsogSgFu5iNIBpgcRYZTRjzcEQodQ
ceQ4xgfrWVw4PRpEqKYUADbeMxcSHDI6P5Y69aZq8ZinXKFOP1sUFzqNeiqLhT0X8zO1qW
yefuHzyBT/r71W8aGhNjXmJw3te3hXYmR1N7x6gpzvyXdY48toQiJhAKBi2PVmtErLVPjw
MgeRKUGZrhNGwRkwROaxLzNC5E6oQpEa+7GC2feF+RPQAxKUJ6qBbtl9A9cv+rFNArzOSV
K86EekhSxCok4YdLnvfgWv/nu9Ynf1p++VGdwDp8JJB3HXqekCUHc32WIwYHjV+kyvhxA0
Lc6Q4W1fDQAAA8ju42KW7uNilgAAAAdzc2gtcnNhAAABAQDIeNTHtSwOdNy1QQaouDl+UU
uyiBKAW7mI0gGmBxFhlNGPNwRCh1Bx5DjGB+tZXDg9GkSophQANt4zFxIcMjo/ljr1pmrx
mKdcoU4/WxQXOo16KouFPRfzM7WpbJ5+4fPIFP+vvVbxoaE2NeYnDe17eFdiZHU3vHqCnO
/Jd1jjy2hCImEAoGLY9Wa0SstU+PAyB5EpQZmuE0bBGTBE5rEvM0LkTqhCkRr7sYLZ94X5
E9ADEpQnqoFu2X0D1y/6sU0CvM5JUrzoR6SFLEKiThh0ue9+Ba/+e71id/Wn75UZ3AOnwk
kHcdep6QJQdzfZYjBgeNX6TK+HEDQtzpDhbV8NAAAAAwEAAQAAAQAH/Dwpik9rcf86nxD3
VoM/w1g7D82A3GZ7CzZymRR5qRZh2ISsa7xqFPen97fJsvEwiePTPe5NBeJ68X/QrLWCQ+
csUskuv3BnMauuvV+C/1uUUq6FC6ZxEw245nE3x6NQxHlc1DNq44/K24HD63uar/P3IN5E
bpcIrdylT0I0rJr52JUEzgnpL//uSirgqDw0rHXrhvOgurdhiqGzIqb7pBsJ+A7hsHvFXo
kDIZsSUY1j5RXdKXe+0+3N0h30YQahAC44v7xEH9ZOC9raqGw+HH/MkhYtv88eyosnPak1
2ErV4chs21JJIsR5b0hfvqtsDr+3mup2yiaQZRuxPU9BAAAAgCkAiRwyOMm7vbFPrcL0dS
CXeEpW5D4Dr3OkBQrMVFRJOVNggxPPeFpG6Ic74DwtogLzEUv4b8xQROE+V3MqglfE7HIY
bkk2hby9yiSD2ieVhWF1w0JxHdNiOtSJH8DXOvBTPbSTmdznhku2EmFsbebUJzCZgn1V6F
Ze18HoxuVJAAAAgQDiq9FxnqONLvJpaOKWkgTh2IH86F6/oLcsdSoYHv8B/vQRYjpaWWFo
hYUJHiE6QZPbasx+Q4l3GVMfp0qwDODtqGrPMs3/e+ftR6zWYdMFk/n1NMm3fVP1em13rX
NZHggtQpoTmfsPM2m/ZCiV7QdXnhZKr1hc0yQQnMOeUZwJYQAAAIEA4mkzSJWdDXDyQfYI
CVXxRk4Usk/N/2W7aVx07hnO9dDelMrJDcB0DXsYlHC1FKkSceDxVN+7jTj4gADQ+LJv7J
0Sk0WYGzybGfRfznaV9BT+v5ZGoqoMxtFfLVc5dNxyPp8mtSTrnTheDjFrCo5z72PhzmJH
6shCf/0kodHdWS0AAAASZ3p6Y2hoQGd6ei1kZXNrdG9wAQ==
-----END OPENSSH PRIVATE KEY-----
""";

    // ssh-keygen -t ecdsa -b 256 -N "" 生成
    private const string Ecdsa256Key = """
-----BEGIN OPENSSH PRIVATE KEY-----
b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAaAAAABNlY2RzYS
1zaGEyLW5pc3RwMjU2AAAACG5pc3RwMjU2AAAAQQTg/iUowVt23p/ls3ek40POjD6zwsd1
WRILt8CSlp7YUEUnb+rffZDeqm9/1u7Z0PkYk39KYl3uU9lQS0NcEfJaAAAAsKOvB5+jrw
efAAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBOD+JSjBW3ben+Wz
d6TjQ86MPrPCx3VZEgu3wJKWnthQRSdv6t99kN6qb3/W7tnQ+RiTf0piXe5T2VBLQ1wR8l
oAAAAhAIpHEmf/TDTg0MWypwAKWYJ9tXpYhApttVCdApQHI55lAAAAEmd6emNoaEBnenot
ZGVza3RvcAECAwQF
-----END OPENSSH PRIVATE KEY-----
""";

    // 与 ssh-keygen -l -E sha256 输出一致
    private const string Ed25519Fingerprint =
        "71:93:5d:98:70:8b:f9:bd:2d:09:b0:5a:6a:35:41:0e:c2:f5:e5:00:0d:15:47:fd:f0:92:da:66:96:a2:7a:e6";

    private const string RsaFingerprint =
        "55:4a:9f:a1:84:5c:ff:7e:ea:ba:10:36:07:19:62:89:4b:21:66:6d:0a:8e:21:58:3c:a6:bf:ce:2b:d4:5f:c4";

    private const string Ecdsa256Fingerprint =
        "6f:7a:18:a8:c1:74:0d:08:90:1c:0d:7b:b7:4a:0c:54:c9:a5:f8:bf:83:97:28:1e:4f:bb:10:8c:da:c5:99:2e";

    private const string EncryptedEd25519Fingerprint =
        "d5:30:fd:4d:f5:60:a9:8c:e4:eb:7a:69:b5:ae:86:49:91:a7:91:12:bb:c7:67:8b:20:c3:87:38:3b:46:16:de";

    [Fact]
    public void Compute_Ed25519_MatchesSshKeygenFingerprint()
        => Assert.Equal(Ed25519Fingerprint, SshKeyFingerprint.Compute(Ed25519Key));

    [Fact]
    public void Compute_Rsa_MatchesSshKeygenFingerprint()
        => Assert.Equal(RsaFingerprint, SshKeyFingerprint.Compute(RsaKey));

    [Fact]
    public void Compute_Ecdsa256_MatchesSshKeygenFingerprint()
        => Assert.Equal(Ecdsa256Fingerprint, SshKeyFingerprint.Compute(Ecdsa256Key));

    [Fact]
    public void Compute_EncryptedKey_WithCorrectPassphrase_MatchesFingerprint()
        => Assert.Equal(EncryptedEd25519Fingerprint, SshKeyFingerprint.Compute(EncryptedEd25519Key, "test-pass"));

    [Fact]
    public void Compute_EncryptedKey_WithoutPassphrase_ReturnsNull()
        => Assert.Null(SshKeyFingerprint.Compute(EncryptedEd25519Key));

    [Fact]
    public void Compute_EncryptedKey_WithWrongPassphrase_ReturnsNull()
        => Assert.Null(SshKeyFingerprint.Compute(EncryptedEd25519Key, "wrong-pass"));

    [Fact]
    public void Compute_GarbageContent_ReturnsNull()
    {
        Assert.Null(SshKeyFingerprint.Compute("这不是一个私钥"));
        Assert.Null(SshKeyFingerprint.Compute(""));
        Assert.Null(SshKeyFingerprint.Compute(null!));
    }
}
