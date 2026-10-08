using System.Runtime.InteropServices;

namespace Kei.Term.Infrastructure.Vault.Windows;

// SDK ABI 来源：UserConsentVerifierInterop.h、windows.security.credentials.ui.h、AsyncInfo.h。
// https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders
// 直接读取 COM vtable，使 Infrastructure 仍然可以以 net10.0 在三端编译。
internal sealed class NativeWindowsHelloVerifier : IWindowsHelloVerifier
{
    private const string RuntimeClass = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid StaticsId = new("AF4F3F91-564C-4DDC-B8B5-973447627C65");
    private static readonly Guid InteropId = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    private static readonly Guid VerificationOperationId = new("FD596FFD-2318-558F-9DBE-D21DF43764A5");
    private static readonly Guid AsyncInfoId = new("00000036-0000-0000-C000-000000000046");

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.Run(() =>
    {
        using WinRtApartment apartment = new();
        // 同时探测桌面互操作接口，不能只凭 UWP 的可用性承诺桌面窗口可以验证。
        using ComPointer interop = GetFactory(InteropId);
        using ComPointer statics = GetFactory(StaticsId);
        ThrowIfFailed(statics.Method<StartAvailability>(6)(statics.Pointer, out nint operationPointer));
        using ComPointer operation = new(operationPointer);
        return GetAsyncResult(operation, ct) == 0;
    }, ct);

    public Task<WindowsHelloResult> VerifyAsync(nint owner, string reason, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!IsWindow(owner)) return WindowsHelloResult.DeviceNotPresent;
        using WinRtApartment apartment = new();
        using ComPointer interop = GetFactory(InteropId);
        using HString message = new(reason);
        Guid operationId = VerificationOperationId;
        ThrowIfFailed(interop.Method<StartVerification>(6)(interop.Pointer, owner,
            message.Pointer, ref operationId, out nint operationPointer));
        using ComPointer operation = new(operationPointer);
        return (WindowsHelloResult)GetAsyncResult(operation, ct);
    }, ct);

    private static ComPointer GetFactory(Guid id)
    {
        using HString runtimeClass = new(RuntimeClass);
        ThrowIfFailed(RoGetActivationFactory(runtimeClass.Pointer, ref id, out nint factory));
        return new(factory);
    }

    private static int GetAsyncResult(ComPointer operation, CancellationToken ct)
    {
        using ComPointer info = operation.Query(AsyncInfoId);
        ReadInt getStatus = info.Method<ReadInt>(7);
        bool terminal = false;
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    // 使用真正的 IAsyncInfo.Cancel，取消后永不取结果或读取密钥。
                    _ = info.Method<NoArgument>(9)(info.Pointer);
                    terminal = getStatus(info.Pointer, out int cancelledStatus) >= 0 && cancelledStatus != 0;
                    ct.ThrowIfCancellationRequested();
                }
                ThrowIfFailed(getStatus(info.Pointer, out int status));
                switch (status)
                {
                    case 0:
                        // 在已初始化的同一 MTA 线程上等待，避免跨 apartment 使用裸接口。
                        ct.WaitHandle.WaitOne(40);
                        continue;
                    case 1:
                        terminal = true;
                        ct.ThrowIfCancellationRequested();
                        ThrowIfFailed(operation.Method<ReadInt>(8)(operation.Pointer, out int result));
                        return result;
                    case 2:
                        terminal = true;
                        throw new OperationCanceledException("Windows Hello 验证已取消。", ct);
                    case 3:
                        terminal = true;
                        ThrowIfFailed(info.Method<ReadInt>(8)(info.Pointer, out int error));
                        ThrowIfFailed(error);
                        throw new COMException("Windows Hello 返回了失败状态。");
                    default:
                        throw new COMException("Windows Hello 返回了未知的异步状态。");
                }
            }
        }
        finally
        {
            // WinRT 禁止对仍在运行的操作调用 Close；取消请求后的清理由系统完成。
            if (terminal) _ = info.Method<NoArgument>(10)(info.Pointer);
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private sealed class WinRtApartment : IDisposable
    {
        public WinRtApartment() => ThrowIfFailed(RoInitialize(1));
        public void Dispose() => RoUninitialize();
    }

    private sealed class HString : IDisposable
    {
        public nint Pointer { get; }

        public HString(string value)
        {
            ThrowIfFailed(WindowsCreateString(value, (uint)value.Length, out nint pointer));
            Pointer = pointer;
        }

        public void Dispose() => _ = WindowsDeleteString(Pointer);
    }

    private sealed class ComPointer(nint pointer) : IDisposable
    {
        public nint Pointer { get; } = pointer != 0 ? pointer : throw new COMException("系统未返回 COM 接口。");

        public T Method<T>(int slot) where T : Delegate
        {
            nint vtable = Marshal.ReadIntPtr(Pointer);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * nint.Size));
        }

        public ComPointer Query(Guid id)
        {
            ThrowIfFailed(Method<QueryInterface>(0)(Pointer, ref id, out nint result));
            return new(result);
        }

        public void Dispose() => _ = Method<Release>(2)(Pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(nint self, ref Guid id, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartAvailability(nint self, out nint operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartVerification(nint self, nint owner, nint message, ref Guid operationId, out nint operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadInt(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NoArgument(nint self);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initializationType);
    [DllImport("combase.dll")]
    private static extern void RoUninitialize();
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint runtimeClass, ref Guid id, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string value, uint length, out nint hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint owner);
}
