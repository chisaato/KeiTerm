using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kei.Term.Core.Vault;

namespace Kei.Term.Infrastructure.Vault.Linux;

// Linux 没有指纹或 TPM 绑定：只把 32 字节 Vault Key 的 base64 文本交给当前用户的默认登录钥匙环。
// 系统验证部分不存在，IsAvailable 只反映 Secret Service 库是否加载成功；不可用即回退主密码。
public sealed class LinuxSecretServiceQuickUnlockStore : IDeviceQuickUnlockStore
{
    private const int KeyLength = 32;

    private readonly ISecretServiceBackend _backend;

    public LinuxSecretServiceQuickUnlockStore()
        : this(NativeSecretServiceBackend.Load()) { }

    internal LinuxSecretServiceQuickUnlockStore(ISecretServiceBackend backend)
        => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public string DisplayName => "登录钥匙环";
    public bool IsSupported => _backend.IsSupported;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Linux 无生物/TPM 验证步骤：库在即视为可用，其余情况由调用方回退主密码。
        return Task.FromResult(IsSupported);
    }

    public async Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (!IsSupported) return false;
        ct.ThrowIfCancellationRequested();
        bool found = await Task.Run(() => _backend.HasKey(keyId), CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return found;
    }

    public async Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (key.Length != KeyLength)
            throw new ArgumentException("保险库密钥必须为 32 字节。", nameof(key));
        EnsureSupported();
        ct.ThrowIfCancellationRequested();

        // 只借用副本转成 base64 文本；本机存储不保留原始字节。
        byte[] copy = key.ToArray();
        try
        {
            string secret = Convert.ToBase64String(copy);
            await Task.Run(() => _backend.StoreKey(keyId, secret), CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                // 钥匙环写入没有取消 API；操作结束后撤销这次已取消的启用。
                await Task.Run(() => _backend.DeleteKey(keyId), CancellationToken.None).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public async Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsSupported) return new DeviceUnlockResult(DeviceUnlockOutcome.Unavailable);
        ct.ThrowIfCancellationRequested();

        string? secret;
        try
        {
            secret = await Task.Run(() => _backend.LookupKey(keyId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return new DeviceUnlockResult(DeviceUnlockOutcome.Failed);
        }
        ct.ThrowIfCancellationRequested();
        if (secret == null) return new DeviceUnlockResult(DeviceUnlockOutcome.NotEnrolled);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret);
        }
        catch (FormatException)
        {
            return new DeviceUnlockResult(DeviceUnlockOutcome.Failed);
        }

        // 读回来必须恰好 32 字节，否则视为损坏并清掉缓冲区。
        if (key.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            return new DeviceUnlockResult(DeviceUnlockOutcome.Failed);
        }
        if (ct.IsCancellationRequested)
        {
            CryptographicOperations.ZeroMemory(key);
            ct.ThrowIfCancellationRequested();
        }
        return new DeviceUnlockResult(DeviceUnlockOutcome.Success, key);
    }

    public async Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (!IsSupported) return;
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => _backend.DeleteKey(keyId), CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("本机登录钥匙环不可用，请使用主密码解锁。");
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 512 || keyId.Contains('\0'))
            throw new ArgumentException("本机快速解锁标识无效。", nameof(keyId));
    }

    private static bool IsNativeFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException;
}

// libsecret 的调用只出现在这一层；测试用内存实现替换，避免触碰用户真实钥匙环。
internal interface ISecretServiceBackend
{
    bool IsSupported { get; }
    bool HasKey(string keyId);
    void StoreKey(string keyId, string secret);
    string? LookupKey(string keyId);
    void DeleteKey(string keyId);
}

// 库加载失败时的占位实现：对外表现为不支持，不回退到命令行工具。
internal sealed class UnsupportedSecretServiceBackend : ISecretServiceBackend
{
    public static readonly UnsupportedSecretServiceBackend Instance = new();

    private UnsupportedSecretServiceBackend() { }

    public bool IsSupported => false;
    public bool HasKey(string keyId) => false;
    public string? LookupKey(string keyId) => null;
    public void StoreKey(string keyId, string secret) => throw new PlatformNotSupportedException("登录钥匙环不可用。");
    public void DeleteKey(string keyId) { }
}

// 通过 libsecret-1.so.0 访问 Secret Service。默认集合即登录钥匙环，条目属性为
// application=KeiTerm 与 key-id=<传入标识>，schema 为 org.keiterm.VaultKey。
internal sealed class NativeSecretServiceBackend : ISecretServiceBackend
{
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";
    private const string GObjectLibrary = "libgobject-2.0.so.0";
    private const string SchemaName = "org.keiterm.VaultKey";
    private const string ApplicationAttribute = "application";
    private const string ApplicationValue = "KeiTerm";
    private const string KeyIdAttribute = "key-id";
    private const string KeyLabel = "KeiTerm 保管库密钥";

    // 64 位 SecretSchema：name(8) + flags(4)+pad(4) + attributes[32]×16 + reserved(4)+pad(4) + 7×指针。
    private const int SchemaSize = 592;
    private const int SchemaNameOffset = 0;
    private const int SchemaAttributesOffset = 16;
    private const int SchemaAttributeStride = 16;
    private const int SchemaAttributeTypeOffset = 8;
    private const int AttributeTypeString = 0;

    private static readonly Lazy<IntPtr> Schema = new(BuildSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ServiceGetSync _serviceGetSync;
    private readonly ServiceStoreSync _serviceStoreSync;
    private readonly ServiceLookupSync _serviceLookupSync;
    private readonly ServiceSearchSync _serviceSearchSync;
    private readonly ServiceClearSync _serviceClearSync;
    private readonly ListFreeFull _listFreeFull;
    private readonly ErrorFree _errorFree;
    private readonly ValueNew _valueNew;
    private readonly ValueGet _valueGet;
    private readonly ValueUnref _valueUnref;

    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;
    private readonly IntPtr _gFree;
    private readonly HashTableNewFull _hashTableNewFull;
    private readonly HashTableInsert _hashTableInsert;
    private readonly HashTableDestroy _hashTableDestroy;
    private readonly StrDup _strDup;
    private readonly ObjectUnref _objectUnref;
    private readonly IntPtr _objectUnrefFunction;

    private NativeSecretServiceBackend(IntPtr secret, IntPtr glib, IntPtr gobject)
    {
        _serviceGetSync = Bind<ServiceGetSync>(secret, "secret_service_get_sync");
        _serviceStoreSync = Bind<ServiceStoreSync>(secret, "secret_service_store_sync");
        _serviceLookupSync = Bind<ServiceLookupSync>(secret, "secret_service_lookup_sync");
        _serviceSearchSync = Bind<ServiceSearchSync>(secret, "secret_service_search_sync");
        _serviceClearSync = Bind<ServiceClearSync>(secret, "secret_service_clear_sync");
        _listFreeFull = Bind<ListFreeFull>(glib, "g_list_free_full");
        _errorFree = Bind<ErrorFree>(glib, "g_error_free");
        _valueNew = Bind<ValueNew>(secret, "secret_value_new");
        _valueGet = Bind<ValueGet>(secret, "secret_value_get");
        _valueUnref = Bind<ValueUnref>(secret, "secret_value_unref");

        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        _gFree = NativeLibrary.GetExport(glib, "g_free");
        _hashTableNewFull = Bind<HashTableNewFull>(glib, "g_hash_table_new_full");
        _hashTableInsert = Bind<HashTableInsert>(glib, "g_hash_table_insert");
        _hashTableDestroy = Bind<HashTableDestroy>(glib, "g_hash_table_destroy");
        _strDup = Bind<StrDup>(glib, "g_strdup");
        _objectUnref = Bind<ObjectUnref>(gobject, "g_object_unref");
        _objectUnrefFunction = NativeLibrary.GetExport(gobject, "g_object_unref");
    }

    public bool IsSupported => true;

    // 加载失败（库缺失、符号缺失或非 64 位布局）一律返回不支持，由调用方继续用主密码。
    internal static ISecretServiceBackend Load(
        string secretLibrary = SecretLibrary, string glibLibrary = GlibLibrary,
        string gobjectLibrary = GObjectLibrary)
    {
        if (IntPtr.Size != 8) return UnsupportedSecretServiceBackend.Instance;
        if (!NativeLibrary.TryLoad(secretLibrary, out IntPtr secret) ||
            !NativeLibrary.TryLoad(glibLibrary, out IntPtr glib) ||
            !NativeLibrary.TryLoad(gobjectLibrary, out IntPtr gobject))
            return UnsupportedSecretServiceBackend.Instance;

        try
        {
            return new NativeSecretServiceBackend(secret, glib, gobject);
        }
        catch (EntryPointNotFoundException)
        {
            return UnsupportedSecretServiceBackend.Instance;
        }
    }

    public bool HasKey(string keyId)
    {
        IntPtr value = LookupSecretValue(keyId);
        if (value == IntPtr.Zero) return false;
        _valueUnref(value);
        return true;
    }

    public string? LookupKey(string keyId)
    {
        IntPtr value = LookupSecretValue(keyId);
        if (value == IntPtr.Zero) return null;
        try
        {
            IntPtr bytes = _valueGet(value, out nuint length);
            if (bytes == IntPtr.Zero || length == 0) return null;
            byte[] buffer = new byte[checked((int)length)];
            try
            {
                Marshal.Copy(bytes, buffer, 0, buffer.Length);
                return Encoding.UTF8.GetString(buffer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        finally
        {
            _valueUnref(value);
        }
    }

    public void StoreKey(string keyId, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        IntPtr service = IntPtr.Zero;
        IntPtr table = IntPtr.Zero;
        IntPtr value = IntPtr.Zero;
        IntPtr label = IntPtr.Zero;
        IntPtr secretPointer = IntPtr.Zero;
        try
        {
            // secret_value_new 会复制内容；这里临时持有 UTF-8 副本，用完清零后释放。
            secretPointer = Marshal.StringToCoTaskMemUTF8(secret);
            value = _valueNew(secretPointer, bytes.Length, IntPtr.Zero);
            if (value == IntPtr.Zero) throw new InvalidOperationException("无法创建钥匙环条目值。");

            service = GetService();
            table = BuildAttributes(keyId);
            label = Marshal.StringToCoTaskMemUTF8(KeyLabel);
            // collection 传 NULL 即默认登录钥匙环。
            bool stored = _serviceStoreSync(service, Schema.Value, table, IntPtr.Zero, label,
                value, IntPtr.Zero, IntPtr.Zero);
            if (!stored) throw new InvalidOperationException("无法写入登录钥匙环。");
        }
        finally
        {
            if (secretPointer != IntPtr.Zero)
            {
                Marshal.Copy(new byte[bytes.Length], 0, secretPointer, bytes.Length);
                Marshal.FreeCoTaskMem(secretPointer);
            }
            CryptographicOperations.ZeroMemory(bytes);
            if (label != IntPtr.Zero) Marshal.FreeCoTaskMem(label);
            if (value != IntPtr.Zero) _valueUnref(value);
            if (table != IntPtr.Zero) _hashTableDestroy(table);
            if (service != IntPtr.Zero) _objectUnref(service);
        }
    }

    public void DeleteKey(string keyId)
    {
        IntPtr service = GetService();
        IntPtr table = IntPtr.Zero;
        try
        {
            table = BuildAttributes(keyId);
            // clear 的 false 只表示没删掉未锁定项。没有匹配项时也是 false，不能据此轮换 Vault Key。
            IntPtr searchError = IntPtr.Zero;
            IntPtr matches = _serviceSearchSync(service, Schema.Value, table, 0, IntPtr.Zero, ref searchError);
            bool searchFailed = searchError != IntPtr.Zero;
            if (searchFailed) _errorFree(searchError);
            bool foundItem = matches != IntPtr.Zero;
            if (foundItem) _listFreeFull(matches, _objectUnrefFunction);

            bool cleared = false;
            bool clearFailed = false;
            if (!searchFailed && foundItem)
            {
                IntPtr clearError = IntPtr.Zero;
                cleared = _serviceClearSync(service, Schema.Value, table, IntPtr.Zero, ref clearError);
                clearFailed = clearError != IntPtr.Zero;
                if (clearFailed) _errorFree(clearError);
            }

            if (!DeleteCompleted(searchFailed, foundItem, cleared, clearFailed))
                throw new InvalidOperationException("无法从登录钥匙环删除。");
        }
        finally
        {
            if (table != IntPtr.Zero) _hashTableDestroy(table);
            _objectUnref(service);
        }
    }

    // 零项且搜索无错误：删除成功。搜到项之后 clear 返回 false 或带上 GError：仍然失败。
    internal static bool DeleteCompleted(bool searchFailed, bool foundItem, bool cleared, bool clearFailed)
    {
        if (searchFailed) return false;
        if (!foundItem) return true;
        return cleared && !clearFailed;
    }

    private IntPtr LookupSecretValue(string keyId)
    {
        IntPtr service = GetService();
        IntPtr table = IntPtr.Zero;
        try
        {
            table = BuildAttributes(keyId);
            return _serviceLookupSync(service, Schema.Value, table, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (table != IntPtr.Zero) _hashTableDestroy(table);
            _objectUnref(service);
        }
    }

    private IntPtr GetService()
    {
        // SECRET_SERVICE_NONE = 0。
        IntPtr service = _serviceGetSync(0, IntPtr.Zero, IntPtr.Zero);
        if (service == IntPtr.Zero) throw new InvalidOperationException("无法连接登录钥匙环。");
        return service;
    }

    private IntPtr BuildAttributes(string keyId)
    {
        // g_free 作为 key/value 的析构回调，释放 g_strdup 出来的字符串。
        IntPtr table = _hashTableNewFull(_strHash, _strEqual, _gFree, _gFree);
        if (table == IntPtr.Zero) throw new InvalidOperationException("无法创建钥匙环属性。");
        try
        {
            InsertAttribute(table, ApplicationAttribute, ApplicationValue);
            InsertAttribute(table, KeyIdAttribute, keyId);
            return table;
        }
        catch
        {
            _hashTableDestroy(table);
            throw;
        }
    }

    private void InsertAttribute(IntPtr table, string name, string value)
    {
        IntPtr nameUtf8 = Marshal.StringToCoTaskMemUTF8(name);
        IntPtr valueUtf8 = Marshal.StringToCoTaskMemUTF8(value);
        IntPtr nameCopy;
        IntPtr valueCopy;
        try
        {
            nameCopy = _strDup(nameUtf8);
            valueCopy = _strDup(valueUtf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nameUtf8);
            Marshal.FreeCoTaskMem(valueUtf8);
        }
        _hashTableInsert(table, nameCopy, valueCopy);
    }

    // 只在进程生命周期内构建一次，永不释放；属性槽其余 name 为 NULL，作为结束标志。
    private static IntPtr BuildSchema()
    {
        IntPtr memory = Marshal.AllocHGlobal(SchemaSize);
        for (int offset = 0; offset < SchemaSize; offset += IntPtr.Size)
            Marshal.WriteIntPtr(memory, offset, IntPtr.Zero);

        Marshal.WriteIntPtr(memory, SchemaNameOffset, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteIntPtr(memory, SchemaAttributesOffset,
            Marshal.StringToCoTaskMemUTF8(ApplicationAttribute));
        Marshal.WriteInt32(memory, SchemaAttributesOffset + SchemaAttributeTypeOffset, AttributeTypeString);
        Marshal.WriteIntPtr(memory, SchemaAttributesOffset + SchemaAttributeStride,
            Marshal.StringToCoTaskMemUTF8(KeyIdAttribute));
        Marshal.WriteInt32(memory, SchemaAttributesOffset + SchemaAttributeStride + SchemaAttributeTypeOffset,
            AttributeTypeString);
        return memory;
    }

    private static T Bind<T>(IntPtr library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ServiceGetSync(uint flags, IntPtr cancellable, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool ServiceStoreSync(IntPtr service, IntPtr schema, IntPtr attributes, IntPtr collection,
        IntPtr label, IntPtr value, IntPtr cancellable, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ServiceLookupSync(IntPtr service, IntPtr schema, IntPtr attributes,
        IntPtr cancellable, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ServiceSearchSync(IntPtr service, IntPtr schema, IntPtr attributes,
        uint flags, IntPtr cancellable, ref IntPtr error);

    private delegate bool ServiceClearSync(IntPtr service, IntPtr schema, IntPtr attributes,
        IntPtr cancellable, ref IntPtr error);

    private delegate void ListFreeFull(IntPtr list, IntPtr itemFree);

    private delegate void ErrorFree(IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ValueNew(IntPtr secret, nint length, IntPtr contentType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ValueGet(IntPtr value, out nuint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ValueUnref(IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr HashTableNewFull(IntPtr hashFunc, IntPtr equalFunc, IntPtr keyDestroy,
        IntPtr valueDestroy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HashTableInsert(IntPtr table, IntPtr key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HashTableDestroy(IntPtr table);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr StrDup(IntPtr str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ObjectUnref(IntPtr obj);
}
