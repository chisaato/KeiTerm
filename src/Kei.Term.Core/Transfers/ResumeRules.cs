namespace Kei.Term.Core.Transfers;

// StartOffset > 0 表示接着已有部分文件传，此时不删除。
// StartOffset 为 0 且 DeleteExistingPartial 为 true：删掉已有部分文件后从头传。
public readonly record struct ResumeDecision(long StartOffset, bool DeleteExistingPartial);

public static class ResumeRules
{
    public static ResumeDecision Decide(
        long? partialLength,
        long sourceSize,
        bool sourceModified,
        bool resumeEnabled,
        bool protocolSupportsResume)
    {
        if (partialLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partialLength), partialLength, "部分文件长度不能为负。");
        }

        if (sourceSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSize), sourceSize, "源文件大小不能为负。");
        }

        // 续传只看长度：0 < 已有长度 < 源大小，且源修改时间未变、开关打开、协议支持。
        bool canResume = resumeEnabled
            && protocolSupportsResume
            && !sourceModified
            && partialLength is > 0 and long length
            && length < sourceSize;
        if (canResume)
        {
            return new ResumeDecision(partialLength!.Value, DeleteExistingPartial: false);
        }

        return new ResumeDecision(0, DeleteExistingPartial: partialLength is not null);
    }
}
