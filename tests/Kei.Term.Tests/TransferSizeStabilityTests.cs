using System.Globalization;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

namespace Kei.Term.Tests;

public class TransferSizeStabilityTests
{
    [Theory]
    [InlineData(FileSizeDisplayMode.Iec, 1048576L)]
    [InlineData(FileSizeDisplayMode.Si, 1000000L)]
    public void Progress_DoesNotDropTrailingZeros(FileSizeDisplayMode mode, long unit)
    {
        FileTransferTaskItemViewModel item = new("archive", "/local", "/remote", FileTransferDirection.Upload)
        {
            State = FileTransferState.Transferring,
            SizeDisplayMode = mode
        };
        int? width = null;
        foreach (decimal amount in new[] { 966.01m, 966.10m, 966.11m, 967.00m })
        {
            long bytes = (long)(amount * unit);
            item.NoteProgress(bytes, 1700 * unit);
            string expected = amount.ToString("0.00", CultureInfo.CurrentCulture);
            Assert.StartsWith(expected, item.FormattedSizeText);
            width ??= item.FormattedSizeText.Length;
            Assert.Equal(width.Value, item.FormattedSizeText.Length);
        }
        item.State = FileTransferState.Completed;
        Assert.Equal(FileSizeFormatter.Format(1700 * unit, mode), item.FormattedSizeText);
    }

    [Fact]
    public void UnknownTotal_AlsoKeepsProgressPrecision()
    {
        FileTransferTaskItemViewModel item = new("archive", "/local", "/remote", FileTransferDirection.Upload)
        {
            State = FileTransferState.Transferring
        };
        item.NoteProgress(1048576, 0);
        Assert.Equal(1m.ToString("0.00", CultureInfo.CurrentCulture) + " MiB", item.FormattedSizeText);
        Assert.Equal("1536 B", FileSizeFormatter.FormatProgress(1536, FileSizeDisplayMode.Bytes));
        Assert.Equal("1,50 KiB", FileSizeFormatter.FormatProgress(1536, FileSizeDisplayMode.Iec, CultureInfo.GetCultureInfo("de-DE")));
    }
}
