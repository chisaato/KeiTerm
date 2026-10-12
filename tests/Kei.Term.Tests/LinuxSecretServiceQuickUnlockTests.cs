using System.Security.Cryptography;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault.Linux;

namespace Kei.Term.Tests;

// 钥匙环语义走内存后端。另有一条测试直接调用 libsecret 的 secret_value_new/get/unref。
// 两者都不连接 Secret Service，不读写用户真实钥匙环。库缺失路径用不存在的库名验证。
public sealed class LinuxSecretServiceQuickUnlockTests
{
    [Fact]
    public async Task RoundTrip_StoresBase64Text_AndReturnsSameKey()
    {
        InMemorySecretServiceBackend backend = new();
        LinuxSecretServiceQuickUnlockStore store = new(backend);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await store.StoreKeyAsync("keiterm-v1-abc", key);

            // 落盘的是 base64 文本，不是原始 32 字节。
            Assert.Equal(Convert.ToBase64String(key), backend.PeekRaw("keiterm-v1-abc"));
            Assert.True(await store.HasKeyAsync("keiterm-v1-abc"));
            Assert.True(store.IsSupported);
            Assert.Equal("登录钥匙环", store.DisplayName);

            DeviceUnlockResult result = await store.UnlockKeyAsync("keiterm-v1-abc", "解锁 KeiTerm 保险库");
            try
            {
                Assert.Equal(DeviceUnlockOutcome.Success, result.Outcome);
                Assert.Equal(key, result.Key);
            }
            finally
            {
                if (result.Key != null) CryptographicOperations.ZeroMemory(result.Key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task Unlock_WithDifferentKeyId_ReturnsNotEnrolled()
    {
        InMemorySecretServiceBackend backend = new();
        LinuxSecretServiceQuickUnlockStore store = new(backend);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await store.StoreKeyAsync("key-id-a", key);

            Assert.False(await store.HasKeyAsync("key-id-b"));
            DeviceUnlockResult result = await store.UnlockKeyAsync("key-id-b", "解锁 KeiTerm 保险库");
            Assert.Equal(DeviceUnlockOutcome.NotEnrolled, result.Outcome);
            Assert.Null(result.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task Unlock_AfterDelete_ReturnsNotEnrolled()
    {
        InMemorySecretServiceBackend backend = new();
        LinuxSecretServiceQuickUnlockStore store = new(backend);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await store.StoreKeyAsync("key-id", key);
            await store.DeleteKeyAsync("key-id");

            Assert.False(await store.HasKeyAsync("key-id"));
            DeviceUnlockResult result = await store.UnlockKeyAsync("key-id", "解锁 KeiTerm 保险库");
            Assert.Equal(DeviceUnlockOutcome.NotEnrolled, result.Outcome);
            Assert.Null(result.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task Unlock_RejectsWrongLengthContent()
    {
        InMemorySecretServiceBackend backend = new();
        // 31 字节的 base64：能解码，但不是 32 字节。
        backend.SetRaw("key-id", Convert.ToBase64String(new byte[31]));
        LinuxSecretServiceQuickUnlockStore store = new(backend);

        DeviceUnlockResult result = await store.UnlockKeyAsync("key-id", "解锁 KeiTerm 保险库");
        Assert.Equal(DeviceUnlockOutcome.Failed, result.Outcome);
        Assert.Null(result.Key);
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("bm90LWEta2V5")] // "not-a-key"：合法 base64 但只有 9 字节
    [InlineData("")]
    public async Task Unlock_RejectsCorruptedContent(string stored)
    {
        InMemorySecretServiceBackend backend = new();
        backend.SetRaw("key-id", stored);
        LinuxSecretServiceQuickUnlockStore store = new(backend);

        DeviceUnlockResult result = await store.UnlockKeyAsync("key-id", "解锁 KeiTerm 保险库");
        Assert.Equal(DeviceUnlockOutcome.Failed, result.Outcome);
        Assert.Null(result.Key);
    }

    [Fact]
    public async Task StoreKey_RejectsNon32ByteKeyAndInvalidId()
    {
        LinuxSecretServiceQuickUnlockStore store = new(new InMemorySecretServiceBackend());

        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreKeyAsync("key-id", new byte[31]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreKeyAsync(string.Empty, new byte[32]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreKeyAsync("bad\0id", new byte[32]));
    }

    [Fact]
    public void DeleteCompleted_TreatsMissingItemAsSuccess_AndRemainingItemAsFailure()
    {
        // clear 在没有匹配项时返回 false 且不设置错误。这必须算删除成功。
        Assert.True(NativeSecretServiceBackend.DeleteCompleted(
            searchFailed: false, foundItem: false, cleared: false, clearFailed: false));
        // 锁定项会被搜索到，但 clear 不删它，同样返回 false。这必须仍然失败。
        Assert.False(NativeSecretServiceBackend.DeleteCompleted(
            searchFailed: false, foundItem: true, cleared: false, clearFailed: false));
        Assert.False(NativeSecretServiceBackend.DeleteCompleted(
            searchFailed: true, foundItem: false, cleared: false, clearFailed: false));
        Assert.False(NativeSecretServiceBackend.DeleteCompleted(
            searchFailed: false, foundItem: true, cleared: true, clearFailed: true));
        Assert.True(NativeSecretServiceBackend.DeleteCompleted(
            searchFailed: false, foundItem: true, cleared: true, clearFailed: false));
    }

    // 生产改动：secret_value_new 的 content_type 为空、MIME 不是 text/plain，或按 C 字符串截断长度时失败。
    // 只调用 secret_value_new / get / get_content_type / unref，不连接 Secret Service，不写用户钥匙环。
    [RequiresLibsecretFact]
    public void SecretValue_RoundTrip_ReadsBackBytes_AfterNativeCreateAndRelease()
    {
        NativeSecretServiceBackend native =
            Assert.IsType<NativeSecretServiceBackend>(NativeSecretServiceBackend.Load());
        // 内嵌 NUL 与非 ASCII，用来抓住把 length 当成 strlen 的截断。
        byte[] secret = [0x61, 0x00, 0x62, 0xC3, 0xA9];

        bool created = native.TryRoundTripSecretValue(secret, out byte[]? roundTrip, out string? contentType);

        Assert.True(created);
        Assert.Equal(secret, roundTrip);
        Assert.Equal("text/plain", contentType);
    }

    [Fact]
    public async Task Load_WhenLibraryMissing_IsNotSupported()
    {
        // 用不存在的库名走真实加载逻辑；不会命中本机 libsecret，更不会读写真实钥匙环。
        ISecretServiceBackend backend =
            NativeSecretServiceBackend.Load("libkeiterm-missing-secret-xyz.so.0", "libglib-2.0.so.0");
        LinuxSecretServiceQuickUnlockStore store = new(backend);

        Assert.False(store.IsSupported);
        Assert.False(await store.IsAvailableAsync());
        Assert.False(await store.HasKeyAsync("key-id"));
        Assert.Equal(DeviceUnlockOutcome.Unavailable,
            (await store.UnlockKeyAsync("key-id", "解锁 KeiTerm 保险库")).Outcome);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            store.StoreKeyAsync("key-id", new byte[32]));
    }

    private sealed class RequiresLibsecretFactAttribute : FactAttribute
    {
        public RequiresLibsecretFactAttribute()
        {
            if (NativeSecretServiceBackend.Load() is not NativeSecretServiceBackend)
                Skip = "libsecret-1.so.0 不可用，跳过原生 secret_value 创建/读取/释放。";
        }
    }

    private sealed class InMemorySecretServiceBackend : ISecretServiceBackend
    {
        private readonly Dictionary<string, string> _values = [];

        public bool IsSupported => true;
        public bool HasKey(string keyId) => _values.ContainsKey(keyId);
        public void StoreKey(string keyId, string secret) => _values[keyId] = secret;
        public string? LookupKey(string keyId) =>
            _values.TryGetValue(keyId, out string? value) ? value : null;
        public void DeleteKey(string keyId) => _values.Remove(keyId);

        public void SetRaw(string keyId, string secret) => _values[keyId] = secret;
        public string? PeekRaw(string keyId) => _values.TryGetValue(keyId, out string? value) ? value : null;
    }
}
