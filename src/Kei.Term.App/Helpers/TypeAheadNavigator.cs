namespace Kei.Term.App.Helpers;

using System;
using System.Collections.Generic;

/// <summary>
/// 键盘盲打输入前缀匹配导航器（Type-Ahead Search / Quick Navigation by Prefix）
/// 支持连续输入拼词前缀定位、单字符连续按键循环跳转、800ms 键入超时重置与退格修改。
/// </summary>
public class TypeAheadNavigator
{
    private string _buffer = string.Empty;
    private DateTimeOffset _lastInputTime = DateTimeOffset.MinValue;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    public TypeAheadNavigator(TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        _timeout = timeout ?? TimeSpan.FromMilliseconds(800);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string CurrentBuffer => _buffer;

    public void Reset()
    {
        _buffer = string.Empty;
    }

    /// <summary>
    /// 处理一次文本或按键字符输入，返回匹配的项目索引。若无匹配则返回 -1。
    /// </summary>
    public int ProcessInput<T>(
        string input,
        IReadOnlyList<T> items,
        int currentIndex,
        Func<T, string?> nameSelector)
    {
        if (string.IsNullOrEmpty(input) || items == null || items.Count == 0)
        {
            return -1;
        }

        var now = _timeProvider.GetUtcNow();
        bool isTimeout = (now - _lastInputTime) >= _timeout;
        _lastInputTime = now;

        // 单字符重复按键循环模式：当当前前缀为单字符且新输入的单字符相同时，跳到下一个匹配项
        bool isSingleCharRepeat = !isTimeout
            && _buffer.Length == 1
            && input.Length == 1
            && string.Equals(_buffer, input, StringComparison.OrdinalIgnoreCase);

        if (isSingleCharRepeat)
        {
            return FindNextMatch(items, currentIndex, _buffer, nameSelector);
        }

        if (isTimeout)
        {
            _buffer = input;
        }
        else
        {
            _buffer += input;
        }

        return FindFirstMatch(items, currentIndex, _buffer, nameSelector);
    }

    /// <summary>
    /// 处理退格键（Backspace），删除末尾一个字符并重新匹配。
    /// </summary>
    public int HandleBackspace<T>(
        IReadOnlyList<T> items,
        int currentIndex,
        Func<T, string?> nameSelector)
    {
        if (_buffer.Length == 0 || items == null || items.Count == 0)
        {
            return -1;
        }

        _buffer = _buffer[..^1];
        if (_buffer.Length == 0)
        {
            return -1;
        }

        _lastInputTime = _timeProvider.GetUtcNow();
        return FindFirstMatch(items, currentIndex, _buffer, nameSelector);
    }

    private static int FindFirstMatch<T>(
        IReadOnlyList<T> items,
        int currentIndex,
        string prefix,
        Func<T, string?> nameSelector)
    {
        // 若当前选中的项已经满足新前缀，优先保留当前项
        if (currentIndex >= 0 && currentIndex < items.Count)
        {
            string? name = nameSelector(items[currentIndex]);
            if (IsEligibleMatch(name, prefix))
            {
                return currentIndex;
            }
        }

        // 从头开始查找第一个满足前缀的项
        for (int i = 0; i < items.Count; i++)
        {
            string? name = nameSelector(items[i]);
            if (IsEligibleMatch(name, prefix))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextMatch<T>(
        IReadOnlyList<T> items,
        int currentIndex,
        string prefix,
        Func<T, string?> nameSelector)
    {
        int start = currentIndex + 1;
        for (int i = 0; i < items.Count; i++)
        {
            int index = (start + i) % items.Count;
            string? name = nameSelector(items[index]);
            if (IsEligibleMatch(name, prefix))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsEligibleMatch(string? name, string prefix)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // ".." 属于返回上级的特殊项，除非用户专门输入 "." 开头，否则不参与普通字母盲打匹配
        if (name == ".." && !prefix.StartsWith('.'))
        {
            return false;
        }

        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
