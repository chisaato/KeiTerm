using System.Net.Sockets;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public class TransferRulesTests
{
    private static readonly DateTimeOffset SourceTime = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TransferConflictPolicy.Overwrite, ConflictResolution.Overwrite)]
    [InlineData(TransferConflictPolicy.Skip, ConflictResolution.Skip)]
    [InlineData(TransferConflictPolicy.AutoRename, ConflictResolution.Rename)]
    [InlineData(TransferConflictPolicy.Ask, ConflictResolution.NeedsPrompt)]
    public void Resolve_DirectPolicies_IgnoreStamps(TransferConflictPolicy policy, ConflictResolution expected)
    {
        TransferFileFacts source = new(1, SourceTime);
        TransferFileFacts newerTarget = new(99, SourceTime.AddHours(3));
        Assert.Equal(expected, ConflictRules.Resolve(policy, source, newerTarget));
    }

    [Fact]
    public void Resolve_UndefinedPolicy_NeedsPrompt()
    {
        TransferFileFacts source = new(1, SourceTime);
        Assert.Equal(
            ConflictResolution.NeedsPrompt,
            ConflictRules.Resolve((TransferConflictPolicy)99, source, source));
    }

    [Fact]
    public void Resolve_OverwriteIfOlder_UsesTwoSecondToleranceOnBothSides()
    {
        TransferFileFacts source = new(10, SourceTime);
        DateTimeOffset cutoff = SourceTime - ConflictRules.OlderTolerance;

        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(10, cutoff)));
        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(1, cutoff.AddTicks(1))));
        Assert.Equal(ConflictResolution.Overwrite, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(10, cutoff.AddTicks(-1))));
        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(999, SourceTime)));
        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(10, SourceTime.AddSeconds(2))));
        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(10, SourceTime.AddHours(1))));
    }

    [Fact]
    public void Resolve_OverwriteIfOlder_ComparesAbsoluteTimeNotClockFace()
    {
        TransferFileFacts source = new(4, SourceTime);
        DateTimeOffset sameInstant = new(2026, 10, 17, 20, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset olderByOffset = SourceTime.AddSeconds(-3).ToOffset(TimeSpan.FromHours(8));

        Assert.Equal(ConflictResolution.Skip, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(4, sameInstant)));
        Assert.Equal(ConflictResolution.Overwrite, ConflictRules.Resolve(
            TransferConflictPolicy.OverwriteIfOlder, source, new TransferFileFacts(4, olderByOffset)));
    }

    [Fact]
    public void NextAvailable_AppendsCounterBeforeLastExtension()
    {
        Assert.Equal(("report", ".pdf"), AutoRename.SplitStemAndExtension("report.pdf"));
        Assert.Equal(("a.tar", ".gz"), AutoRename.SplitStemAndExtension("a.tar.gz"));
        Assert.Equal(("README", ""), AutoRename.SplitStemAndExtension("README"));
        Assert.Equal((".bashrc", ""), AutoRename.SplitStemAndExtension(".bashrc"));
        Assert.Equal((".config", ".json"), AutoRename.SplitStemAndExtension(".config.json"));

        Assert.Equal("report (1).pdf", AutoRename.NextAvailable("report.pdf", _ => false));
        Assert.Equal("README (1)", AutoRename.NextAvailable("README", _ => false));
        Assert.Equal(".bashrc (1)", AutoRename.NextAvailable(".bashrc", _ => false));
        Assert.Equal("a.tar (1).gz", AutoRename.NextAvailable("a.tar.gz", _ => false));
    }

    [Fact]
    public void NextAvailable_SkipsCollisionsWithoutCheckingOriginal()
    {
        HashSet<string> taken = ["report (1).pdf", "report (2).pdf"];
        List<string> asked = [];
        string next = AutoRename.NextAvailable("report.pdf", name =>
        {
            asked.Add(name);
            return taken.Contains(name);
        });

        Assert.Equal("report (3).pdf", next);
        Assert.Equal(["report (1).pdf", "report (2).pdf", "report (3).pdf"], asked);
    }

    [Fact]
    public void NextAvailable_DotfileCollidesIndependently()
    {
        HashSet<string> taken = [".bashrc (1)"];
        Assert.Equal(".bashrc (2)", AutoRename.NextAvailable(".bashrc", taken.Contains));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dir/file.txt")]
    [InlineData("dir\\file.txt")]
    [InlineData(".")]
    [InlineData("..")]
    public void NextAvailable_RejectsPathOrEmpty(string fileName)
    {
        Assert.Throws<ArgumentException>(() => AutoRename.NextAvailable(fileName, _ => false));
    }

    [Theory]
    [InlineData(null, 100, false, true, true, 0, false)]
    [InlineData(40L, 100, false, true, true, 40, false)]
    [InlineData(40L, 100, false, false, true, 0, true)]
    [InlineData(40L, 100, true, true, true, 0, true)]
    [InlineData(40L, 100, false, true, false, 0, true)]
    [InlineData(0L, 100, false, true, true, 0, true)]
    [InlineData(100L, 100, false, true, true, 0, true)]
    [InlineData(101L, 100, false, true, true, 0, true)]
    [InlineData(null, 0, true, true, true, 0, false)]
    public void Decide_ResumeOnlyWhenPartialIsStrictlyInsideUnchangedSource(
        long? partial,
        long sourceSize,
        bool sourceModified,
        bool resumeEnabled,
        bool protocolSupportsResume,
        long expectedOffset,
        bool expectedDelete)
    {
        ResumeDecision decision = ResumeRules.Decide(
            partial, sourceSize, sourceModified, resumeEnabled, protocolSupportsResume);
        Assert.Equal(expectedOffset, decision.StartOffset);
        Assert.Equal(expectedDelete, decision.DeleteExistingPartial);
    }

    [Fact]
    public void Decide_RejectsNegativeLengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResumeRules.Decide(-1, 10, false, true, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResumeRules.Decide(1, -1, false, true, true));
    }

    [Fact]
    public void PartialNames_AppendAndRecognizeOrdinalSuffix()
    {
        Assert.Equal("report.pdf.keipart", PartialFileNames.ToPartial("report.pdf"));
        Assert.Equal(".bashrc.keipart", PartialFileNames.ToPartial(".bashrc"));
        Assert.Equal("/tmp/a.txt.keipart", PartialFileNames.ToPartial("/tmp/a.txt"));

        Assert.True(PartialFileNames.IsPartial("report.pdf.keipart"));
        Assert.True(PartialFileNames.TryGetOriginal("report.pdf.keipart", out string original));
        Assert.Equal("report.pdf", original);
        Assert.True(PartialFileNames.TryGetOriginal("/tmp/a.txt.keipart", out string pathOriginal));
        Assert.Equal("/tmp/a.txt", pathOriginal);

        Assert.False(PartialFileNames.IsPartial("report.pdf"));
        Assert.False(PartialFileNames.IsPartial("report.pdf.KEIPART"));
        Assert.False(PartialFileNames.IsPartial(".keipart"));
        Assert.False(PartialFileNames.IsPartial(null));
        Assert.False(PartialFileNames.TryGetOriginal("file.keipart.extra", out _));
    }

    [Fact]
    public void Classify_MapsDotNetExceptions()
    {
        ITransferFailureClassifier classifier = DotNetTransferFailureClassifier.Instance;

        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SocketException((int)SocketError.ConnectionReset)));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new TimeoutException("timed out")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(
            new IOException("reset", new SocketException((int)SocketError.TimedOut))));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(
            new IOException("outer", new IOException("inner", new TimeoutException()))));

        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new FileNotFoundException()));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new DirectoryNotFoundException()));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new PathTooLongException()));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(28)));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(unchecked((int)0x80070070))));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(unchecked((int)0x80070027))));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(unchecked((int)0x80070020))));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(unchecked((int)0x80070021))));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(32)));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(Io(33)));

        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(new IOException("generic")));
        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(new InvalidOperationException("no")));
        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(
            new AggregateException(new IOException("io"), new OperationCanceledException())));

        Assert.Throws<ArgumentNullException>(() => classifier.Classify(null!));
        Assert.Throws<ArgumentException>(() => classifier.Classify(new OperationCanceledException()));
        Assert.Throws<ArgumentException>(() => classifier.Classify(new TaskCanceledException()));
        Assert.Throws<ArgumentException>(() => classifier.Classify(
            new IOException("wrapped cancel", new OperationCanceledException())));
        Assert.Throws<ArgumentException>(() => classifier.Classify(
            new AggregateException(new TaskCanceledException())));
    }

    [Fact]
    public void Delay_FixedStaysAtInitial_ExponentialCapsAndDoesNotOverflow()
    {
        FileTransferSettings exponential = new()
        {
            BackoffMode = RetryBackoffMode.Exponential,
            InitialRetryIntervalSeconds = 2,
            MaxRetryIntervalSeconds = 60,
        };
        Assert.Equal(TimeSpan.FromSeconds(2), RetryBackoff.Delay(1, exponential));
        Assert.Equal(TimeSpan.FromSeconds(32), RetryBackoff.Delay(5, exponential));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryBackoff.Delay(6, exponential));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryBackoff.Delay(10_000, exponential));

        FileTransferSettings cappedFirst = new()
        {
            BackoffMode = RetryBackoffMode.Exponential,
            InitialRetryIntervalSeconds = 100,
            MaxRetryIntervalSeconds = 60,
        };
        Assert.Equal(TimeSpan.FromSeconds(60), RetryBackoff.Delay(1, cappedFirst));

        FileTransferSettings saturated = new()
        {
            BackoffMode = RetryBackoffMode.Exponential,
            InitialRetryIntervalSeconds = int.MaxValue,
            MaxRetryIntervalSeconds = int.MaxValue,
        };
        Assert.Equal(TimeSpan.FromSeconds(int.MaxValue), RetryBackoff.Delay(40, saturated));

        FileTransferSettings fixedInterval = new()
        {
            BackoffMode = RetryBackoffMode.Fixed,
            InitialRetryIntervalSeconds = 2,
            MaxRetryIntervalSeconds = 60,
        };
        Assert.Equal(TimeSpan.FromSeconds(2), RetryBackoff.Delay(1, fixedInterval));
        Assert.Equal(TimeSpan.FromSeconds(2), RetryBackoff.Delay(9, fixedInterval));

        FileTransferSettings fixedAboveMax = new()
        {
            BackoffMode = RetryBackoffMode.Fixed,
            InitialRetryIntervalSeconds = 100,
            MaxRetryIntervalSeconds = 60,
        };
        Assert.Equal(TimeSpan.FromSeconds(100), RetryBackoff.Delay(3, fixedAboveMax));

        FileTransferSettings negative = new() { InitialRetryIntervalSeconds = -4 };
        Assert.Equal(-4, negative.InitialRetryIntervalSeconds);
        Assert.Equal(TimeSpan.Zero, RetryBackoff.Delay(1, negative));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoff.Delay(0, exponential));
    }

    [Theory]
    [InlineData(TransferFailureKind.Transient, 1, 3, true, true)]
    [InlineData(TransferFailureKind.Transient, 3, 3, true, true)]
    [InlineData(TransferFailureKind.Transient, 4, 3, true, false)]
    [InlineData(TransferFailureKind.Transient, 1, 3, false, false)]
    [InlineData(TransferFailureKind.Transient, 1, 0, true, false)]
    [InlineData(TransferFailureKind.Permanent, 1, 3, true, false)]
    [InlineData(TransferFailureKind.Unknown, 1, 3, true, false)]
    [InlineData(TransferFailureKind.Transient, 0, 3, true, false)]
    public void ShouldRetry_OnlyTransientWithinRetryCount(
        TransferFailureKind kind, int attemptsSoFar, int retryCount, bool enabled, bool expected)
    {
        FileTransferSettings settings = new()
        {
            AutoRetryEnabled = enabled,
            RetryCount = retryCount,
        };
        Assert.Equal(expected, RetryBackoff.ShouldRetry(kind, attemptsSoFar, settings));
    }

    [Fact]
    public void Plan_OrdersParentsFirst_AndSkipsDirectorySymlinks()
    {
        DateTimeOffset modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        SourceTreeEntry[] entries =
        [
            new("z-empty", SourceEntryKind.Directory, 0, modified),
            new("b/file.txt", SourceEntryKind.File, 10, modified),
            new("a", SourceEntryKind.Directory, 0, modified),
            new("a/empty", SourceEntryKind.Directory, 0, modified),
            new("a/linkdir", SourceEntryKind.SymlinkToDirectory),
            new("a/linkfile", SourceEntryKind.SymlinkToFile, 4, modified),
            new("readme", SourceEntryKind.File, 1, modified),
        ];

        TransferPlanOp[] expected =
        [
            new TransferPlanOp.CreateDirectory("a", modified),
            new TransferPlanOp.CreateDirectory("a/empty", modified),
            new TransferPlanOp.Skip("a/linkdir", DirectoryTransferPlanner.DirectorySymlinkSkipReason),
            new TransferPlanOp.TransferFile("a/linkfile", 4, modified, true),
            new TransferPlanOp.CreateDirectory("b", null),
            new TransferPlanOp.TransferFile("b/file.txt", 10, modified, false),
            new TransferPlanOp.TransferFile("readme", 1, modified, false),
            new TransferPlanOp.CreateDirectory("z-empty", modified),
        ];

        Assert.Equal(expected, DirectoryTransferPlanner.Plan(entries));
        Assert.Equal(expected, DirectoryTransferPlanner.Plan(entries.Reverse()));
    }

    [Fact]
    public void Plan_EmptyDirectoryIsCreated_DirectorySymlinkIsNot()
    {
        DateTimeOffset modified = new(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(
        [
            new SourceTreeEntry("empty", SourceEntryKind.Directory, 0, modified),
            new SourceTreeEntry("link", SourceEntryKind.SymlinkToDirectory),
            new SourceTreeEntry("link/child.txt", SourceEntryKind.File, 8, modified),
            new SourceTreeEntry("link/nested", SourceEntryKind.Directory, 0, modified),
        ]);

        Assert.Equal(
        [
            new TransferPlanOp.CreateDirectory("empty", modified),
            new TransferPlanOp.Skip("link", DirectoryTransferPlanner.DirectorySymlinkSkipReason),
            new TransferPlanOp.Skip("link/child.txt", DirectoryTransferPlanner.UnderDirectorySymlinkSkipReason),
            new TransferPlanOp.Skip("link/nested", DirectoryTransferPlanner.UnderDirectorySymlinkSkipReason),
        ],
        plan);
        Assert.DoesNotContain(plan, op => op is TransferPlanOp.CreateDirectory { RelativePath: "link" or "link/nested" });
    }

    [Fact]
    public void Plan_SynthesizesMissingParents_AndNormalizesSeparators()
    {
        DateTimeOffset modified = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(
        [
            new SourceTreeEntry(@"a\b\c.txt", SourceEntryKind.File, 3, modified),
            new SourceTreeEntry("./a/./b/d.txt", SourceEntryKind.File, 1, modified),
        ]);

        Assert.Equal(
        [
            new TransferPlanOp.CreateDirectory("a", null),
            new TransferPlanOp.CreateDirectory("a/b", null),
            new TransferPlanOp.TransferFile("a/b/c.txt", 3, modified, false),
            new TransferPlanOp.TransferFile("a/b/d.txt", 1, modified, false),
        ],
        plan);
    }

    [Fact]
    public void Plan_EmptyInput_IsEmpty()
    {
        Assert.Empty(DirectoryTransferPlanner.Plan([]));
    }

    [Fact]
    public void Plan_RejectsDuplicatesAbsoluteAndParentCycles()
    {
        Assert.Throws<ArgumentException>(() => DirectoryTransferPlanner.Plan(
        [
            new SourceTreeEntry("a/b", SourceEntryKind.File),
            new SourceTreeEntry(@"a\b", SourceEntryKind.File),
        ]));
        Assert.Throws<ArgumentException>(() => DirectoryTransferPlanner.Plan(
            [new SourceTreeEntry("/abs", SourceEntryKind.File)]));
        Assert.Throws<ArgumentException>(() => DirectoryTransferPlanner.Plan(
            [new SourceTreeEntry("../x", SourceEntryKind.Directory)]));
        Assert.Throws<ArgumentException>(() => DirectoryTransferPlanner.Plan(
        [
            new SourceTreeEntry("a", SourceEntryKind.File, 1),
            new SourceTreeEntry("a/b.txt", SourceEntryKind.File, 1),
        ]));
        Assert.Throws<ArgumentNullException>(() => DirectoryTransferPlanner.Plan(null!));
    }

    private static IOException Io(int hresult)
    {
        IOException exception = new("io");
        exception.HResult = hresult;
        return exception;
    }
}
