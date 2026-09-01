using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase4CImageRecoveryTests
{
    [Fact]
    public async Task EndToEnd_RecoversResidentAndFragmentedBytes_VerifiesHashes_AndPreservesSource()
    {
        using var workspace = new RecoveryTestWorkspace();
        var resident = "resident-content"u8.ToArray();
        var first = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var second = Enumerable.Range(0, 1024).Select(index => (byte)(255 - (index % 251))).ToArray();
        var expectedFragmented = first.Concat(second.Take(477)).ToArray();
        var fixture = CreateBaseFixture(12, bitmapLcn: 80)
            .WriteClusterPayload(40, first)
            .WriteClusterPayload(55, second);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "resident.txt", 1, resident.Length, resident.Length),
            fixture.ResidentData(resident));
        fixture.AddRecord(8, 4, false, false,
            fixture.FileName(5, 1, "fragmented.bin", 1, expectedFragmented.Length, 2048),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1), (55, 1)), expectedFragmented.Length, 2048, expectedFragmented.Length, highestVcn: 1));
        await workspace.WriteImageAsync(fixture.Build());
        var sourceHash = await HashFileAsync(workspace.ImagePath);
        var sourceInfo = new FileInfo(workspace.ImagePath);
        var originalLength = sourceInfo.Length;
        var originalTimestamp = sourceInfo.LastWriteTimeUtc;

        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var selected = session.Candidates.Where(item => item.Candidate.Name is "resident.txt" or "fragmented.bin").Select(item => item.Id).ToArray();
        var plan = await service.CreatePlanAsync(new(session.SessionId, selected, workspace.OutputPath), CancellationToken.None);
        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, item => Assert.Equal(RecoveryVerificationState.Verified, item.Verification));
        Assert.Equal(resident, await File.ReadAllBytesAsync(result.Files.Single(item => item.MftRecordNumber == 7).OutputPath!));
        Assert.Equal(expectedFragmented, await File.ReadAllBytesAsync(result.Files.Single(item => item.MftRecordNumber == 8).OutputPath!));
        Assert.All(result.Files, item => Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.OutputPath!))), item.Sha256));
        Assert.Equal(sourceHash, await HashFileAsync(workspace.ImagePath));
        sourceInfo.Refresh();
        Assert.Equal(originalLength, sourceInfo.Length);
        Assert.Equal(originalTimestamp, sourceInfo.LastWriteTimeUtc);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath, "*.partial", SearchOption.AllDirectories));
        Assert.All(result.Files, item => Assert.StartsWith(Path.GetFullPath(workspace.OutputPath) + Path.DirectorySeparatorChar, Path.GetFullPath(item.OutputPath!), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InitializedTail_IsRecoveredAsDocumentedLogicalZeroes()
    {
        using var workspace = new RecoveryTestWorkspace();
        var payload = Enumerable.Repeat((byte)0xA7, 2048).ToArray();
        var fixture = CreateBaseFixture(10, 80).WriteClusterPayload(40, payload);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "initialized.bin", 1, 1500, 2048),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 2)), 1500, 2048, 600, highestVcn: 1));
        await workspace.WriteImageAsync(fixture.Build());

        var result = await RecoverAllAsync(workspace);
        var recovered = await File.ReadAllBytesAsync(Assert.Single(result.Files).OutputPath!);

        Assert.Equal(1500, recovered.Length);
        Assert.All(recovered.Take(600), value => Assert.Equal(0xA7, value));
        Assert.All(recovered.Skip(600), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ZeroLengthResidentFile_IsPublishedAndVerified()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "empty.txt", 1), fixture.ResidentData([]));
        await workspace.WriteImageAsync(fixture.Build());

        var file = Assert.Single((await RecoverAllAsync(workspace)).Files);

        Assert.Equal(RecoveryFileOutcomeCode.RecoveredAndVerified, file.Outcome);
        Assert.Equal(0, new FileInfo(file.OutputPath!).Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), file.Sha256);
    }

    [Fact]
    public async Task SourceTimestampMutation_InvalidatesSessionBeforeCreatingOutput()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "changed.txt", 1, 3, 3), fixture.ResidentData("abc"u8.ToArray()));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, [Assert.Single(session.Candidates).Id], workspace.OutputPath), CancellationToken.None);
        File.SetLastWriteTimeUtc(workspace.ImagePath, File.GetLastWriteTimeUtc(workspace.ImagePath).AddSeconds(5));

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.FatalSourceFailure, result.Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task UnknownCandidateAndDisposedSession_AreRejectedWithoutEngineAccess()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "owned.txt", 1, 1, 1), fixture.ResidentData([1]));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(
            new(session.SessionId, [new(Guid.NewGuid())], workspace.OutputPath), CancellationToken.None));
        Assert.True(service.DisposeSession(session.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(
            new(session.SessionId, [Assert.Single(session.Candidates).Id], workspace.OutputPath), CancellationToken.None));
    }

    [Fact]
    public async Task NamedAds_IsPreservedInScanMetadata_ButOnlyUnnamedDataIsRecovered()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "streams.txt", 1, 4, 4),
            fixture.ResidentData("main"u8.ToArray()),
            fixture.ResidentData("secret"u8.ToArray(), "private"));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        Assert.Contains(Assert.Single(session.Candidates).Candidate.DataStreams, stream => stream.Name == "private");

        var plan = await service.CreatePlanAsync(new(session.SessionId, [Assert.Single(session.Candidates).Id], workspace.OutputPath), CancellationToken.None);
        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal("main", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Assert.Single(result.Files).OutputPath!)));
        Assert.DoesNotContain(Directory.GetFiles(workspace.OutputPath), path => path.IndexOf(':', 3) >= 0);
    }

    [Fact]
    public async Task CollisionHandling_NeverOverwritesAndPreservesExtensions()
    {
        using var workspace = new RecoveryTestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.OutputPath, "same.txt"), "existing");
        var fixture = CreateBaseFixture(11, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "same.txt", 1, 1, 1), fixture.ResidentData([1]));
        fixture.AddRecord(8, 4, false, false, fixture.FileName(5, 1, "same.txt", 1, 1, 1), fixture.ResidentData([2]));
        await workspace.WriteImageAsync(fixture.Build());

        var result = await RecoverAllAsync(workspace);

        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(workspace.OutputPath, "same.txt")));
        Assert.Contains(result.Files, item => Path.GetFileName(item.OutputPath) == "same (1).txt");
        Assert.Contains(result.Files, item => Path.GetFileName(item.OutputPath) == "same (2).txt");
    }

    [Fact]
    public async Task DuplicateCandidateIds_AreRecoveredOnceInDeterministicOrder()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "once.txt", 1, 1, 1), fixture.ResidentData([7]));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var id = Assert.Single(session.Candidates).Id;

        var plan = await service.CreatePlanAsync(new(session.SessionId, [id, id, id], workspace.OutputPath), CancellationToken.None);
        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Single(plan.Items);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task PartialAllocation_IsBlockedByDefault_AndWarnedWhenExplicitlyAllowed()
    {
        using var workspace = new RecoveryTestWorkspace();
        var bitmap = new byte[128];
        bitmap[40 / 8] |= (byte)(1 << (40 & 7));
        var fixture = CreateBaseFixture(10, 80, bitmap)
            .WriteClusterPayload(40, Enumerable.Repeat((byte)1, 1024).ToArray())
            .WriteClusterPayload(50, Enumerable.Repeat((byte)2, 1024).ToArray());
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "partial.bin", 1, 2048, 2048),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1), (50, 1)), 2048, 2048, 2048, highestVcn: 1));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var id = Assert.Single(session.Candidates).Id;

        var blockedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath), CancellationToken.None);
        var blocked = await service.RecoverAsync(blockedPlan.PlanId, null, CancellationToken.None);
        var allowedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(AllowPartiallyOverwritten: true)), CancellationToken.None);
        var allowed = await service.RecoverAsync(allowedPlan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryFileOutcomeCode.SkippedByPolicy, Assert.Single(blocked.Files).Outcome);
        Assert.Equal(RecoveryFileOutcomeCode.RecoveredWithAllocationWarning, Assert.Single(allowed.Files).Outcome);
    }

    [Fact]
    public async Task CompressedEncryptedAndSparseStreams_AreExplicitlyBlocked()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(12, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "compressed.bin", 1, 1, 1024), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), 1, 1024, 1, highestVcn: 0, flags: 0x0001));
        fixture.AddRecord(8, 4, false, false, fixture.FileName(5, 1, "encrypted.bin", 1, 1, 1024), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((41, 1)), 1, 1024, 1, highestVcn: 0, flags: 0x4000));
        fixture.AddRecord(9, 5, false, false, fixture.FileName(5, 1, "sparse.bin", 1, 1, 1024), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((null, 1)), 1, 1024, 1, highestVcn: 0, flags: 0x8000));
        await workspace.WriteImageAsync(fixture.Build());

        var result = await RecoverAllAsync(workspace);

        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, item => Assert.Equal(RecoveryFileOutcomeCode.UnsupportedLayout, item.Outcome));
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task CancellationMidFile_RemovesPartialAndDoesNotPublishFinalName()
    {
        using var workspace = new RecoveryTestWorkspace();
        var content = Enumerable.Repeat((byte)0x5A, 2 * 1024 * 1024).ToArray();
        var clusters = content.Length / 1024;
        var fixture = CreateBaseFixture(10, 3000).WriteClusterPayload(40, content);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "cancel.bin", 1, content.Length, content.Length),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, clusters)), content.Length, content.Length, content.Length, highestVcn: clusters - 1));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, [Assert.Single(session.Candidates).Id], workspace.OutputPath, new(AllowUnknown: true, BufferSize: 4096)), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<RecoveryProgress>(value =>
        {
            if (value.CurrentBytes >= 4096)
            {
                cancellation.Cancel();
            }
        });

        var result = await service.RecoverAsync(plan.PlanId, progress, cancellation.Token);

        Assert.Equal(RecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task NegativeLcnMovement_IsRecoveredAcrossFragmentedExtents()
    {
        using var workspace = new RecoveryTestWorkspace();
        var high = Enumerable.Repeat((byte)0x11, 1024).ToArray();
        var low = Enumerable.Repeat((byte)0x22, 1024).ToArray();
        var fixture = CreateBaseFixture(10, 80).WriteClusterPayload(50, high).WriteClusterPayload(40, low);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "negative-delta.bin", 1, 2048, 2048),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((50, 1), (40, 1)), 2048, 2048, 2048, highestVcn: 1));
        await workspace.WriteImageAsync(fixture.Build());

        var file = Assert.Single((await RecoverAllAsync(workspace)).Files);

        Assert.Equal(high.Concat(low).ToArray(), await File.ReadAllBytesAsync(file.OutputPath!));
    }

    [Fact]
    public async Task RestoredTimestampCannotHideChangedMftSequence()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "sequence.txt", 1, 1, 1), fixture.ResidentData([1]));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, [Assert.Single(session.Candidates).Id], workspace.OutputPath), CancellationToken.None);
        var timestamp = File.GetLastWriteTimeUtc(workspace.ImagePath);
        await using (var writable = new FileStream(workspace.ImagePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            writable.Position = fixture.MftOffset + (7L * fixture.RecordSize) + 16;
            await writable.WriteAsync(new byte[] { 4, 0 });
        }

        File.SetLastWriteTimeUtc(workspace.ImagePath, timestamp);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.FatalSourceFailure, result.Outcome);
        Assert.Equal(RecoveryFileOutcomeCode.SourceChanged, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task MixedBatch_ContinuesAfterPolicyFailureAndKeepsVerifiedSuccess()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(11, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "good.txt", 1, 2, 2), fixture.ResidentData("ok"u8.ToArray()));
        fixture.AddRecord(8, 4, false, false, fixture.FileName(5, 1, "blocked.bin", 1, 1, 1024), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), 1, 1024, 1, highestVcn: 0, flags: 0x0001));
        await workspace.WriteImageAsync(fixture.Build());

        var result = await RecoverAllAsync(workspace);

        Assert.Equal(RecoveryBatchOutcome.CompletedWithFailures, result.Outcome);
        Assert.Contains(result.Files, item => item.Outcome == RecoveryFileOutcomeCode.RecoveredAndVerified);
        Assert.Contains(result.Files, item => item.Outcome == RecoveryFileOutcomeCode.UnsupportedLayout);
        Assert.Single(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task Progress_IsMonotonicAndPublishesOneFinalCompletion()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(11, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "one.txt", 1, 3, 3), fixture.ResidentData("one"u8.ToArray()));
        fixture.AddRecord(8, 4, false, false, fixture.FileName(5, 1, "two.txt", 1, 3, 3), fixture.ResidentData("two"u8.ToArray()));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, session.Candidates.Select(candidate => candidate.Id).ToArray(), workspace.OutputPath), CancellationToken.None);
        var reports = new List<RecoveryProgress>();

        var result = await service.RecoverAsync(plan.PlanId, new InlineProgress<RecoveryProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.Completed, result.Outcome);
        Assert.True(reports.Zip(reports.Skip(1), (left, right) => right.CurrentBytes >= left.CurrentBytes).All(value => value));
        Assert.Equal(1, reports.Count(item => item.CompletedCount == plan.Items.Count && item.CurrentCandidateId is null));
    }

    [Fact]
    public async Task MissingDestination_IsUnavailableUnlessExplicitCreationIsEnabled()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "create.txt", 1, 1, 1), fixture.ResidentData([9]));
        await workspace.WriteImageAsync(fixture.Build());
        var missing = Path.Combine(workspace.Root, "new-output");
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var id = Assert.Single(session.Candidates).Id;
        var blockedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], missing), CancellationToken.None);
        var blocked = await service.RecoverAsync(blockedPlan.PlanId, null, CancellationToken.None);
        var allowedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], missing, new(CreateDestinationIfMissing: true)), CancellationToken.None);
        var allowed = await service.RecoverAsync(allowedPlan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.DestinationUnavailable, blocked.Outcome);
        Assert.Equal(RecoveryBatchOutcome.Completed, allowed.Outcome);
        Assert.Single(Directory.GetFiles(missing));
    }

    [Fact]
    public async Task UnknownAllocation_RequiresExplicitOptInAndCarriesWarning()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot().WriteClusterPayload(40, Enumerable.Repeat((byte)3, 1024).ToArray());
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "unknown.bin", 1, 16, 1024),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), 16, 1024, 16, highestVcn: 0));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var id = Assert.Single(session.Candidates).Id;
        var blockedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath), CancellationToken.None);
        var blocked = await service.RecoverAsync(blockedPlan.PlanId, null, CancellationToken.None);
        var allowedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(AllowUnknown: true)), CancellationToken.None);
        var allowed = await service.RecoverAsync(allowedPlan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryFileOutcomeCode.SkippedByPolicy, Assert.Single(blocked.Files).Outcome);
        Assert.Equal(RecoveryFileOutcomeCode.RecoveredWithAllocationWarning, Assert.Single(allowed.Files).Outcome);
        Assert.Contains("inconclusive", Assert.Single(allowed.Files).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullyAllocatedEvidence_UsesSeparateDamagedContentOptIn()
    {
        using var workspace = new RecoveryTestWorkspace();
        var bitmap = new byte[128];
        bitmap[40 / 8] |= (byte)(1 << (40 & 7));
        var fixture = CreateBaseFixture(10, 80, bitmap).WriteClusterPayload(40, Enumerable.Repeat((byte)4, 1024).ToArray());
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "allocated.bin", 1, 16, 1024),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), 16, 1024, 16, highestVcn: 0));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var id = Assert.Single(session.Candidates).Id;

        var blockedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(AllowPartiallyOverwritten: true)), CancellationToken.None);
        var blocked = await service.RecoverAsync(blockedPlan.PlanId, null, CancellationToken.None);
        var allowedPlan = await service.CreatePlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(AllowOverwritten: true)), CancellationToken.None);
        var allowed = await service.RecoverAsync(allowedPlan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryFileOutcomeCode.SkippedByPolicy, Assert.Single(blocked.Files).Outcome);
        Assert.Equal(RecoveryFileOutcomeCode.RecoveredWithAllocationWarning, Assert.Single(allowed.Files).Outcome);
        Assert.Contains("currently allocated", Assert.Single(allowed.Files).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OriginalFolderLayout_SanitizesDotDotParentAndRemainsContained()
    {
        using var workspace = new RecoveryTestWorkspace();
        var fixture = CreateBaseFixture(12, 80);
        fixture.AddRecord(9, 2, true, true, fixture.FileName(5, 1, "..", 1, directory: true));
        fixture.AddRecord(7, 3, false, false, fixture.FileName(9, 2, "safe.txt", 1, 2, 2), fixture.ResidentData("ok"u8.ToArray()));
        await workspace.WriteImageAsync(fixture.Build());

        var result = await RecoverAllAsync(workspace, new(OutputLayout: RecoveryOutputLayout.OriginalFoldersWhenSafe));
        var path = Assert.Single(result.Files).OutputPath!;

        Assert.StartsWith(Path.GetFullPath(workspace.OutputPath) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}", path, StringComparison.Ordinal);
        Assert.Equal("ok", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DestinationReparsePoint_IsRejectedWhenPlatformCanCreateOne()
    {
        using var workspace = new RecoveryTestWorkspace();
        var target = Path.Combine(workspace.Root, "target");
        var link = Path.Combine(workspace.Root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var fixture = CreateBaseFixture(10, 80);
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "link.txt", 1, 1, 1), fixture.ResidentData([1]));
        await workspace.WriteImageAsync(fixture.Build());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, [Assert.Single(session.Candidates).Id], link), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(RecoveryBatchOutcome.DestinationUnavailable, result.Outcome);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void LongUnicodeNames_AreNormalizedBoundedAndDeterministic()
    {
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 200)) + ".txt";

        var first = RecoveryDestination.SanitizeComponent(decomposed, "fallback");
        var second = RecoveryDestination.SanitizeComponent(decomposed, "fallback");

        Assert.Equal(first, second);
        Assert.True(first.Length <= 120);
        Assert.Equal(first, first.Normalize(System.Text.NormalizationForm.FormC));
        Assert.EndsWith(".txt", first, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..", "fallback")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("a:b/c\\d?.txt", "a_b_c_d_.txt")]
    [InlineData("name. ", "name")]
    public void FilenameSanitization_HandlesTraversalReservedAndInvalidComponents(string unsafeName, string expected)
    {
        Assert.Equal(expected, RecoveryDestination.SanitizeComponent(unsafeName, "fallback"));
    }

    [Fact]
    public async Task DeviceNamespace_RemainsRejectedForRecoverySessions()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<NotSupportedException>(() => service.ScanAsync("\\\\.\\PhysicalDrive0", new(new(0)), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ScanAsync("\\\\.\\C:", new(new(0)), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ScanAsync("\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\", new(new(0)), null, CancellationToken.None));
    }

    private static NtfsTestFixtureBuilder CreateBaseFixture(int records, long bitmapLcn, byte[]? bitmap = null)
    {
        var fixture = new NtfsTestFixtureBuilder(records).AddRoot();
        var bytes = bitmap ?? new byte[128];
        fixture.WriteClusterPayload(bitmapLcn, bytes);
        fixture.AddRecord(6, 1, true, false,
            fixture.FileName(5, 1, "$Bitmap", 1, bytes.Length, fixture.ClusterSize),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((bitmapLcn, 1)), bytes.Length, fixture.ClusterSize, bytes.Length, highestVcn: 0));
        return fixture;
    }

    private static IImageRecoveryService CreateService()
    {
        var factory = new RegularFileRandomAccessSourceFactory();
        var scanner = new NtfsMetadataScanner();
        var metadata = new RecoverySourceMetadataProvider(factory);
        return new ImageRecoveryService(
            new StandardImageScanService(factory, scanner),
            metadata,
            new NtfsImageRecoveryEngine(factory, metadata, scanner));
    }

    private static async Task<RecoveryBatchResult> RecoverAllAsync(RecoveryTestWorkspace workspace, RecoveryPolicy? policy = null)
    {
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new(0)), null, CancellationToken.None);
        var plan = await service.CreatePlanAsync(new(session.SessionId, session.Candidates.Select(item => item.Id).ToArray(), workspace.OutputPath, policy), CancellationToken.None);
        return await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);
    }

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecoveryTestWorkspace : IDisposable
    {
        public RecoveryTestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio-Phase4C", Guid.NewGuid().ToString("N"));
            OutputPath = Path.Combine(Root, "output");
            Directory.CreateDirectory(OutputPath);
            ImagePath = Path.Combine(Root, "fixture.img");
        }

        public string Root { get; }
        public string ImagePath { get; }
        public string OutputPath { get; }

        public Task WriteImageAsync(byte[] image) => File.WriteAllBytesAsync(ImagePath, image);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
