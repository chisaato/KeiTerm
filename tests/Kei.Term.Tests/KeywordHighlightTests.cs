using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

// 关键字高亮：一行进、区间出。重叠时更长的优先，非整词不得命中。
public class KeywordHighlightTests
{
    [Fact]
    public void WholeWord_ErrorAndFail_AreCaseInsensitive()
    {
        IReadOnlyList<KeywordSpan> spans = KeywordHighlighter.Find("ERROR then Fail.");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new KeywordSpan(0, 5), spans[0]);
        Assert.Equal(new KeywordSpan(11, 4), spans[1]);
    }

    [Fact]
    public void Erroring_IsNotError()
    {
        Assert.Empty(KeywordHighlighter.Find("erroring"));
        Assert.Empty(KeywordHighlighter.Find("failure"));
    }

    [Fact]
    public void Ipv4_MatchesFourOctetsOnly()
    {
        IReadOnlyList<KeywordSpan> spans = KeywordHighlighter.Find("host 10.1.2.3 ok");

        Assert.Equal(new[] { new KeywordSpan(5, 8) }, spans);
        Assert.Empty(KeywordHighlighter.Find("999.1.2.3"));
        Assert.Empty(KeywordHighlighter.Find("1.2.3"));
    }

    [Fact]
    public void Url_RunsUntilWhitespace()
    {
        IReadOnlyList<KeywordSpan> spans = KeywordHighlighter.Find("see https://example.com/a b http://x");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new KeywordSpan(4, 21), spans[0]);
        Assert.Equal(new KeywordSpan(28, 8), spans[1]);
    }

    [Fact]
    public void Overlap_KeepsLongerSpan()
    {
        // URL 盖住其中的 error，更长的 URL 留下，里面的整词不再单独占区间
        IReadOnlyList<KeywordSpan> spans = KeywordHighlighter.Find("http://error.example/fail");

        Assert.Equal(new[] { new KeywordSpan(0, 25) }, spans);
    }

    [Fact]
    public void NonOverlapping_KeepsAll()
    {
        IReadOnlyList<KeywordSpan> spans = KeywordHighlighter.Find("error 10.0.0.1 https://a.test fail");

        Assert.Equal(4, spans.Count);
        Assert.Equal(new KeywordSpan(0, 5), spans[0]);
        Assert.Equal(new KeywordSpan(6, 8), spans[1]);
        Assert.Equal(new KeywordSpan(15, 14), spans[2]);
        Assert.Equal(new KeywordSpan(30, 4), spans[3]);
    }
}
