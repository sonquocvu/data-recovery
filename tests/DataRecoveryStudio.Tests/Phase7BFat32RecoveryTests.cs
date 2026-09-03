using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase7BFat32RecoveryTests
{
    [Fact]
    public async Task ProductionEndToEnd_RecoversExactBytesAndZeroLengthThroughOpaqueIds()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().ActiveConflictCandidate().Build());
        var before = await FingerprintAsync(fixture.ImagePath);
        var session = await fixture.ScanAsync();
        var zero = Find(session, "?ERO.TXT");
        var free = Find(session, "?REE.TXT");
        var one = Find(session, "?NNER.TXT");
        var chain = Find(session, "?HAIN.DAT");
        var conflict = Find(session, "?ONFLICT.BIN");
        var ambiguousPlan = Find(session, "?EUSED.BIN");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId,
            [zero.CandidateId, free.CandidateId, one.CandidateId, chain.CandidateId, conflict.CandidateId, ambiguousPlan.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true)), CancellationToken.None);

        Assert.Equal(6, plan.Items.Count);
        Assert.DoesNotContain(plan.Items, item => item.OutputName.Contains('/') || item.OutputName.Contains('\\'));
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(Fat32RecoveryBatchOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(6, result.Files.Count);
        Assert.Equal(0, AssertFile(result, zero).BytesWritten);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), AssertFile(result, zero).Sha256);
        Assert.Equal(Enumerable.Repeat((byte)0x10, 512).Concat(Enumerable.Repeat((byte)0x11, 188)), await File.ReadAllBytesAsync(AssertFile(result, free).OutputPath!));
        Assert.Equal(Enumerable.Repeat((byte)0, 1), await File.ReadAllBytesAsync(AssertFile(result, one).OutputPath!));
        Assert.Equal(Enumerable.Repeat((byte)0x40, 512).Concat(Enumerable.Repeat((byte)0x41, 188)), await File.ReadAllBytesAsync(AssertFile(result, chain).OutputPath!));
        Assert.Equal(700, new FileInfo(AssertFile(result, free).OutputPath!).Length);
        Assert.Equal(700, new FileInfo(AssertFile(result, chain).OutputPath!).Length);
        Assert.Equal(Fat32RecoveryOutcome.ActiveClusterConflict, AssertFile(result, conflict).Outcome);
        Assert.Equal(Fat32RecoveryOutcome.SkippedByPolicy, AssertFile(result, ambiguousPlan).Outcome);
        Assert.All(result.Files.Where(item => item.OutputPath is not null), item => Assert.Equal(RecoveryVerificationState.Verified, item.Verification));
        Assert.All(result.Files.Where(item => item.OutputPath is not null), item => Assert.StartsWith(Path.GetFullPath(fixture.Destination) + Path.DirectorySeparatorChar, Path.GetFullPath(item.OutputPath!), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, Directory.GetFiles(fixture.Destination).Length);
        Assert.Empty(Directory.GetFiles(fixture.Destination, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(before, await FingerprintAsync(fixture.ImagePath));
    }

    [Fact]
    public async Task DefaultPolicy_AllowsZeroAndFreeButBlocksPreservedAndDamaged()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId,
            [Find(session, "?ERO.TXT").CandidateId, Find(session, "?REE.TXT").CandidateId,
             Find(session, "?HAIN.DAT").CandidateId, Find(session, "?IXED.BIN").CandidateId], fixture.Destination), CancellationToken.None);

        Assert.True(plan.Items.Single(item => item.CandidateId == Find(session, "?ERO.TXT").CandidateId).IsEligible);
        Assert.True(plan.Items.Single(item => item.CandidateId == Find(session, "?REE.TXT").CandidateId).IsEligible);
        Assert.False(plan.Items.Single(item => item.CandidateId == Find(session, "?HAIN.DAT").CandidateId).IsEligible);
        Assert.False(plan.Items.Single(item => item.CandidateId == Find(session, "?IXED.BIN").CandidateId).IsEligible);
    }

    [Fact]
    public async Task PreservedNonContiguousChain_RequiresOptInAndWritesChainOrder()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var chain = Find(session, "?HAIN.DAT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [chain.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true)), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        var file = Assert.Single(result.Files);

        Assert.Equal(Fat32RecoveryOutcome.RecoveredPreservedChainWithWarning, file.Outcome);
        Assert.Equal(Enumerable.Repeat((byte)0x40, 512).Concat(Enumerable.Repeat((byte)0x41, 188)), await File.ReadAllBytesAsync(file.OutputPath!));
    }

    [Fact]
    public async Task ActiveFileOwnershipConflict_IsBlockedEvenWithOptIn()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().DeletedFileStartsAt(60, 12).Build());
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true, AllowDeterministicDamagedContent: true)), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(Fat32RecoveryOutcome.ActiveClusterConflict, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(60u)]
    public async Task ActiveDirectoryAndFileClusters_CannotBeRecoveredUnderDeletedNames(uint activeCluster)
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().DeletedFileStartsAt(activeCluster, 12).Build());
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true, AllowDeterministicDamagedContent: true)), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal(Fat32RecoveryOutcome.ActiveClusterConflict, Assert.Single(result.Files).Outcome);
    }

    [Fact]
    public async Task IncompleteActiveOwnershipAnalysis_FailsClosed()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().SetFatEntry(60, 0).Build());
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal(Fat32RecoveryOutcome.DamagedMetadata, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public async Task ExplicitDamagedPolicy_UsesOnlyDeterministicContiguousSpanAndWarns()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?IXED.BIN");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowDeterministicDamagedContent: true)), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        var file = Assert.Single(result.Files);
        Assert.Equal(Fat32RecoveryOutcome.RecoveredDamagedWithWarning, file.Outcome);
        Assert.Contains("overwritten or unrelated", file.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(700, file.BytesWritten);
    }

    [Theory]
    [InlineData(0x0FFFFFF7u)]
    [InlineData(0x0FFFFFF2u)]
    public async Task BadAndReservedClusters_RemainBlockedEvenWithDamagedOptIn(uint value)
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().SetFatEntry(10, value).Build());
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true, AllowDeterministicDamagedContent: true)), CancellationToken.None);
        Assert.False(Assert.Single(plan.Items).IsEligible);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal(Fat32RecoveryOutcome.SkippedByPolicy, Assert.Single(result.Files).Outcome);
    }

    [Fact]
    public async Task MirroredFatDisagreement_RemainsBlocked()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().MirroredDisagreement(10, 0x0FFFFFFF).Build());
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true, AllowDeterministicDamagedContent: true)), CancellationToken.None);
        Assert.False(Assert.Single(plan.Items).IsEligible);
    }

    [Fact]
    public async Task PreservedChainWithExtraCluster_IsRejectedConservatively()
    {
        var bytes = new Fat32TestFixtureBuilder().SetFatEntry(41, 42).SetFatEntry(42, 0x0FFFFFFF).Payload(42, 0x42).Build();
        await using var fixture = await RecoveryFixture.CreateAsync(bytes);
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?HAIN.DAT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination,
            new(AllowPreservedFatChain: true)), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal(Fat32RecoveryOutcome.DamagedMetadata, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public async Task SourceMutationAfterPlanning_FailsBeforeCreatingOutput()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination), CancellationToken.None);
        await using (var stream = new FileStream(fixture.ImagePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = Fat32TestFixtureBuilder.ImageLength - 1;
            stream.WriteByte(0x7A);
        }

        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
        Assert.Equal(Fat32RecoveryBatchOutcome.SourceChanged, result.Outcome);
        Assert.Empty(result.Files);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public async Task DuplicateIdsRecoverOnceAndCollisionNeverOverwrites()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "_REE.TXT"), "user-owned");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId,
            [candidate.CandidateId, candidate.CandidateId], fixture.Destination), CancellationToken.None);
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Single(plan.Items);
        Assert.Single(result.Files);
        Assert.Equal("user-owned", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "_REE.TXT")));
        Assert.Equal("_REE (1).TXT", Path.GetFileName(result.Files[0].OutputPath));
    }

    [Fact]
    public async Task NameConfidence_UsesProbableNameButFallsBackForAmbiguousName()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var probable = session.Result.Candidates.Single(item => item.NameState == Fat32NameState.ProbableDeletedLongName);
        var ambiguous = session.Result.Candidates.Single(item => item.NameState == Fat32NameState.AmbiguousDeletedLongName);
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId,
            [probable.CandidateId, ambiguous.CandidateId], fixture.Destination), CancellationToken.None);

        Assert.DoesNotContain("FAT32_Deleted", plan.Items.Single(item => item.CandidateId == probable.CandidateId).OutputName, StringComparison.Ordinal);
        Assert.StartsWith("FAT32_Deleted_", plan.Items.Single(item => item.CandidateId == ambiguous.CandidateId).OutputName, StringComparison.Ordinal);
        Assert.Equal(Fat32NameState.AmbiguousDeletedLongName, plan.Items.Single(item => item.CandidateId == ambiguous.CandidateId).OriginalNameConfidence);
    }

    [Fact]
    public async Task InvalidMetadataName_UsesContainedGeneratedFallback()
    {
        await using var fixture = await RecoveryFixture.CreateAsync(new Fat32TestFixtureBuilder().MaliciousDeletedLongName().Build());
        var session = await fixture.ScanAsync();
        var candidate = session.Result.Candidates.Single(item => item.NameState == Fat32NameState.InvalidName);
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination), CancellationToken.None);
        var name = Assert.Single(plan.Items).OutputName;
        Assert.StartsWith("FAT32_Deleted_", name, StringComparison.Ordinal);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
    }

    [Fact]
    public async Task ProgressIsMonotonicAndTerminalCountsMatchResults()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var selected = new[]
        {
            Find(session, "?REE.TXT").CandidateId,
            session.Result.Candidates.Single(item => item.NameState == Fat32NameState.ProbableDeletedLongName).CandidateId,
        };
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, selected, fixture.Destination), CancellationToken.None);
        var reports = new List<Fat32RecoveryProgress>();
        var result = await fixture.Service.RecoverAsync(plan.PlanId, new InlineProgress<Fat32RecoveryProgress>(reports.Add), CancellationToken.None);
        Assert.NotEmpty(reports);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.CompletedItems <= pair.Second.CompletedItems && pair.First.CurrentBytes <= pair.Second.CurrentBytes));
        Assert.Equal(result.CompletedItems, reports[^1].CompletedItems);
        Assert.Equal(result.SuccessCount, reports[^1].SuccessCount);
        Assert.Equal(result.WarningCount, reports[^1].WarningCount);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.SuccessCount <= pair.Second.SuccessCount &&
            pair.First.WarningCount <= pair.Second.WarningCount && pair.First.SkippedCount <= pair.Second.SkippedCount &&
            pair.First.FailedCount <= pair.Second.FailedCount));
    }

    [Fact]
    public async Task UnknownDisposedAndForeignCandidatesFailClosed()
    {
        await using var first = await RecoveryFixture.CreateAsync();
        await using var second = await RecoveryFixture.CreateAsync();
        var firstSession = await first.ScanAsync();
        var secondSession = await second.ScanAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Service.CreateRecoveryPlanAsync(
            new(firstSession.SessionId, [Find(secondSession, "?REE.TXT").CandidateId], first.Destination), CancellationToken.None));
        Assert.True(first.Service.DisposeSession(firstSession.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Service.CreateRecoveryPlanAsync(
            new(firstSession.SessionId, [Find(firstSession, "?REE.TXT").CandidateId], first.Destination), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Service.CreateRecoveryPlanAsync(
            new(Guid.NewGuid(), [], first.Destination), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeRecoveryCreatesNoOutput()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination), CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var result = await fixture.Service.RecoverAsync(plan.PlanId, null, canceled.Token);

        Assert.Equal(Fat32RecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public async Task CancellationDuringClusterStreaming_RemovesExactPartial()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidate = Find(session, "?REE.TXT");
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, [candidate.CandidateId], fixture.Destination), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<Fat32RecoveryProgress>(value =>
        {
            if (value.CurrentBytes > 0) cancellation.Cancel();
        });
        var result = await fixture.Service.RecoverAsync(plan.PlanId, progress, cancellation.Token);

        Assert.Equal(Fat32RecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Equal(Fat32RecoveryOutcome.Canceled, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public async Task CancellationBetweenCandidates_PreservesOnlyPreviouslyVerifiedOutput()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        var session = await fixture.ScanAsync();
        var candidates = new[] { Find(session, "?REE.TXT").CandidateId, Find(session, "?ERO.TXT").CandidateId };
        var plan = await fixture.Service.CreateRecoveryPlanAsync(new(session.SessionId, candidates, fixture.Destination), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<Fat32RecoveryProgress>(value =>
        {
            if (value.CompletedItems == 1) cancellation.Cancel();
        });
        var result = await fixture.Service.RecoverAsync(plan.PlanId, progress, cancellation.Token);

        Assert.Equal(Fat32RecoveryBatchOutcome.Canceled, result.Outcome);
        var recovered = Assert.Single(result.Files);
        Assert.Equal(RecoveryVerificationState.Verified, recovered.Verification);
        Assert.True(File.Exists(recovered.OutputPath));
        Assert.Single(Directory.GetFiles(fixture.Destination));
        Assert.Empty(Directory.GetFiles(fixture.Destination, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public void PublicRequestContainsNoClusterOffsetLengthOrNameOverrides()
    {
        var names = typeof(Fat32RecoveryRequest).GetProperties().Select(item => item.Name).ToArray();
        Assert.Equal(["ScanSessionId", "CandidateIds", "DestinationRoot", "Policy", "EffectivePolicy"], names);
        Assert.DoesNotContain(names, name => name.Contains("Cluster", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Offset", StringComparison.OrdinalIgnoreCase) || name.Contains("Length", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Extension", StringComparison.OrdinalIgnoreCase) || name.Contains("Confidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecoveryBudgetsMayLowerButNeverRaiseHardLimits()
    {
        new Fat32RecoveryPolicy(MaximumCandidates: 1, BufferSize: 4096).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fat32RecoveryPolicy(MaximumCandidates: Fat32RecoveryPolicy.HardLimits.MaximumCandidates + 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fat32RecoveryPolicy(BufferSize: 1024).Validate());
    }

    [Fact]
    public void ProductionRecoveryBoundaryRemainsHeadlessImageOnly()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.Infrastructure", "Fat32ImageRecoveryEngine.cs"));
        var service = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.Application", "Fat32ImageScanService.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.App", "App.xaml.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.ScanWorker", "Program.cs"));
        Assert.DoesNotContain("DllImport", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImport", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalDrive", engine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LiveVolume", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanWorker", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Fat32ImageRecoveryEngine", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Fat32ImageRecoveryEngine", worker, StringComparison.Ordinal);
    }

    private static Fat32DeletedCandidate Find(Fat32ScanSession session, string shortName) =>
        Assert.Single(session.Result.Candidates.Where(item => item.ShortName == shortName));

    private static Fat32RecoveryFileResult AssertFile(Fat32RecoveryBatchResult result, Fat32DeletedCandidate candidate) =>
        Assert.Single(result.Files.Where(item => item.CandidateId == candidate.CandidateId));

    private static async Task<string> FingerprintAsync(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))}";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private RecoveryFixture(string root, string imagePath, string destination, Fat32ImageScanService service)
        { Root = root; ImagePath = imagePath; Destination = destination; Service = service; }
        public string Root { get; }
        public string ImagePath { get; }
        public string Destination { get; }
        public Fat32ImageScanService Service { get; }

        public static async Task<RecoveryFixture> CreateAsync(byte[]? bytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"drs-fat32-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var image = Path.Combine(root, "source.img");
            var destination = Path.Combine(root, "output");
            Directory.CreateDirectory(destination);
            await File.WriteAllBytesAsync(image, bytes ?? new Fat32TestFixtureBuilder().Build());
            var factory = new RegularFileRandomAccessSourceFactory();
            var metadata = new Fat32SourceMetadataProvider(factory);
            var scanner = new Fat32MetadataScanner();
            var engine = new Fat32ImageRecoveryEngine(factory, metadata, scanner);
            return new(root, image, destination, new(factory, scanner, metadata, engine, new Fat32RecoveryPlanResolver()));
        }

        public Task<Fat32ScanSession> ScanAsync() => Service.ScanAsync(ImagePath, new(new(0)), null, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
