using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Diagnostics;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.Tests;

public sealed class AgeSyncPackageTests
{
    private sealed class AgeCliFactAttribute : FactAttribute
    {
        public AgeCliFactAttribute()
        {
            if (FindExecutable("age") is null) Skip = "PATH 中未安装 age CLI。";
            else if (OperatingSystem.IsWindows() || FindExecutable("python3") is null)
                Skip = "CLI 口令交互测试需要 Unix PTY 与 python3。";
        }
    }

    private static string? FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);

    [AgeCliFact]
    public async Task AgeCliBidirectionalInterop()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-age-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sut = new AgeSyncPackage();
            byte[] plain = RandomNumberGenerator.GetBytes(150000);
            string input = Path.Combine(directory, "input");
            string package = Path.Combine(directory, "package.age");
            string output = Path.Combine(directory, "output");
            await File.WriteAllBytesAsync(input, plain);
            await File.WriteAllBytesAsync(package, sut.Encrypt(plain, "password"));
            async Task Run(params string[] arguments)
            {
                var info = new ProcessStartInfo(FindExecutable("python3")!) { RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TestData", "Age", "cli-pty.py"));
                info.ArgumentList.Add(FindExecutable("age")!);
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                using var process = Process.Start(info)!;
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { process.Kill(true); throw; }
                Assert.True(process.ExitCode == 0, await stderr + await stdout);
            }
            await Run("-d", "-o", output, package);
            Assert.Equal(plain, await File.ReadAllBytesAsync(output));
            await Run("-p", "-o", package, input);
            Assert.Equal(plain, sut.Decrypt(await File.ReadAllBytesAsync(package), "password"));
        }
        finally { Directory.Delete(directory, true); }
    }

    public static IEnumerable<object[]> Vectors() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "Age"))
        .Where(p => Path.GetFileName(p).StartsWith("scrypt", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("stream_", StringComparison.Ordinal))
        .Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void CctvVector(string name)
    {
        byte[] vector = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Age", name));
        int split = vector.AsSpan().IndexOf("\n\n"u8);
        var metadata = Encoding.UTF8.GetString(vector, 0, split).Split('\n')
            .Select(l => l.Split(": ", 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        byte[] data = vector[(split + 2)..];
        if (metadata.ContainsKey("compressed"))
        {
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            data = output.ToArray();
        }
        byte[] Decrypt()
        {
            if (name.StartsWith("scrypt", StringComparison.Ordinal))
                return new AgeSyncPackage().Decrypt(data, metadata.GetValueOrDefault("passphrase", "password"));
            int marker = data.AsSpan().IndexOf("\n--- "u8);
            int start = marker + 1 + data.AsSpan(marker + 1).IndexOf((byte)'\n') + 1;
            return AgeSyncPackage.DecryptPayload(data.AsSpan(start), Convert.FromHexString(metadata["file key"]));
        }
        if (metadata["expect"] == "success")
            Assert.Equal(metadata["payload"], Convert.ToHexStringLower(SHA256.HashData(Decrypt())));
        else
            Assert.Throws<SyncPackageException>(() => Decrypt());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(131072)]
    public void RoundTrip(int size)
    {
        var sut = new AgeSyncPackage();
        byte[] plain = RandomNumberGenerator.GetBytes(size);
        byte[] encrypted = sut.Encrypt(plain, "同步口令🔑".AsSpan());
        Assert.StartsWith("age-encryption.org/v1\n-> scrypt ", Encoding.ASCII.GetString(encrypted));
        Assert.Equal(plain, sut.Decrypt(encrypted, "同步口令🔑".AsSpan()));
    }

    [Fact]
    public void FailuresAreClassifiedAndDoNotReturnPlaintext()
    {
        var sut = new AgeSyncPackage();
        byte[] encrypted = sut.Encrypt(new byte[70000], "password");
        Assert.Equal(SyncPackageError.WrongPassphrase, Assert.Throws<SyncPackageException>(() => sut.Decrypt(encrypted, "wrong")).Error);
        Assert.Equal(SyncPackageError.NotAge, Assert.Throws<SyncPackageException>(() => sut.Decrypt("not age"u8, "password")).Error);
        Assert.Equal(SyncPackageError.CorruptOrTruncated, Assert.Throws<SyncPackageException>(() => sut.Decrypt(encrypted.AsSpan(0, encrypted.Length - 1), "password")).Error);
        int mac = Encoding.ASCII.GetString(encrypted).IndexOf("--- ", StringComparison.Ordinal) + 4;
        encrypted[mac] = encrypted[mac] == (byte)'A' ? (byte)'B' : (byte)'A';
        Assert.Equal(SyncPackageError.HeaderAuthenticationFailed, Assert.Throws<SyncPackageException>(() => sut.Decrypt(encrypted, "password")).Error);
    }

    [Fact]
    public void RejectsExcessiveWorkBeforeDerivation()
    {
        byte[] input = Encoding.ASCII.GetBytes("age-encryption.org/v1\n-> scrypt AAAAAAAAAAAAAAAAAAAAAA 99999999999999999999\n");
        Assert.Equal(SyncPackageError.WorkFactorTooHigh,
            Assert.Throws<SyncPackageException>(() => new AgeSyncPackage().Decrypt(input, "password")).Error);
    }

    [Fact]
    public void FreshRandomnessAndExactUtf8Passphrase()
    {
        var sut = new AgeSyncPackage();
        byte[] first = sut.Encrypt("secret"u8, "é");
        Assert.NotEqual(first, sut.Encrypt("secret"u8, "é"));
        Assert.Equal(SyncPackageError.WrongPassphrase,
            Assert.Throws<SyncPackageException>(() => sut.Decrypt(first, "e\u0301")).Error);
    }
}
