namespace Kei.Term.App.Logging;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// 命令/弹窗入口的统一容错：捕获异常并记录，按语义返回 fallback，避免 AsyncRelayCommand 静默吞掉异常
public static class Safe
{
    // 无返回值动作
    public static async Task RunAsync(ILogger logger, string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} 失败", operation);
        }
    }

    // 有返回值动作：失败时返回 fallback（如对话框取消语义的 null）
    public static async Task<T?> RunAsync<T>(ILogger logger, string operation, Func<Task<T>> action, T? fallback = default)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} 失败", operation);
            return fallback;
        }
    }
}
