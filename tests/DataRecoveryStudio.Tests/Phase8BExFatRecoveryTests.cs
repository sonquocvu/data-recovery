using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase8BExFatRecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task ProductionScanThenRecoveryReconstructsMixedBatchAndAuditsEveryReadCategory()
    {
        var f = new ExFatTestFixtureBuilder();
        f.AddFile("zero.txt", 0, 0);
        f.AddFile("one.bin", 20, 37);
        f.AddFile("multi.bin", 22, 1031);
        f.AddFile("tail.bin", 26, 1300, valid: 519);
        f.AddFile("fragment.bin", 30, 700, contiguous: false); f.SetFat(30, 34); f.SetFat(34, uint.MaxValue);
        f.PayloadRanges.Add((f.Offset(30), 512)); f.PayloadRanges.Add((f.Offset(34), 512));
        f.AddFile("uncertain.bin", 36, 21, wrongHash: true);
        f.AddFile("conflict.bin", 38, 31); f.AddFile("active.bin", 38, 31, deleted: false); f.SetAllocated(38);
        foreach (var range in f.PayloadRanges)
            for (var i = 0; i < range.Length; i++) f.Bytes[range.Offset + i] = (byte)(i * 17 + range.Offset % 251 + 1);
        var audit = new ReadAudit(f.PayloadRanges);
        using var h = new Harness(f, audit: audit);
        var session = await h.Scan();
        var before = Fingerprint(h.Image);
        File.WriteAllText(Path.Combine(h.Destination, "one.bin"), "preexisting");
        var capture = new Capture();
        var plan = await h.Plan(session, new(AllowPreservedFatChain: true), session.Result.Candidates.Reverse().Select(c => c.CandidateId).Concat(session.Result.Candidates.Select(c => c.CandidateId)).ToArray());
        Assert.Equal(7, plan.Items.Count);
        var result = await h.Service.RecoverAsync(plan.PlanId, capture, default);
        Assert.Equal(ExFatRecoveryBatchOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(session.Result.Candidates.Select(c => c.CandidateId), result.Files.Select(c => c.CandidateId));
        var expected = new Dictionary<string, byte[]>
        {
            ["zero.txt"] = [],
            ["one.bin"] = f.Bytes.AsSpan(f.Offset(20), 37).ToArray(),
            ["multi.bin"] = f.Bytes.AsSpan(f.Offset(22), 1031).ToArray(),
            ["tail.bin"] = f.Bytes.AsSpan(f.Offset(26), 519).ToArray().Concat(new byte[781]).ToArray(),
            ["fragment.bin"] = f.Bytes.AsSpan(f.Offset(30), 512).ToArray().Concat(f.Bytes.AsSpan(f.Offset(34), 188).ToArray()).ToArray(),
            ["uncertain.bin"] = f.Bytes.AsSpan(f.Offset(36), 21).ToArray(),
        };
        foreach (var candidate in session.Result.Candidates)
        {
            var item = result.Files.Single(r => r.CandidateId == candidate.CandidateId);
            if (candidate.DisplayName == "conflict.bin") { Assert.Equal(ExFatRecoveryOutcome.Blocked, item.Outcome); Assert.Null(item.OutputPath); continue; }
            Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, item.Outcome);
            Assert.Equal(RecoveryVerificationState.Verified, item.Verification);
            Assert.Equal(expected[candidate.DisplayName], File.ReadAllBytes(item.OutputPath!));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected[candidate.DisplayName])), item.Sha256);
            Assert.Equal(h.Destination, Path.GetDirectoryName(item.OutputPath));
            Assert.Equal(candidate.ValidDataLength, item.CopiedBytes);
            Assert.Equal(candidate.LogicalSize - candidate.ValidDataLength, item.SynthesizedZeroBytes);
            Assert.Equal(candidate.DisplayName == "uncertain.bin", item.UsedFallbackName);
            if (item.UsedFallbackName) Assert.Equal("EXFAT_Deleted_" + candidate.CandidateId + ".bin", Path.GetFileName(item.OutputPath));
        }
        Assert.Equal("preexisting", File.ReadAllText(Path.Combine(h.Destination, "one.bin")));
        Assert.True(File.Exists(Path.Combine(h.Destination, "one (1).bin")));
        Assert.Empty(Directory.GetFiles(h.Destination, "*.partial"));
        Assert.Equal(before, Fingerprint(h.Image));
        Assert.Equal(1, audit.Opens); Assert.Equal(1, audit.Scans); Assert.Equal(2, audit.Fingerprints);
        Assert.Equal(2L * f.Bytes.Length, audit.FingerprintBytes);
        Assert.Equal(2308, audit.PayloadBytes);
        Assert.Equal(audit.FingerprintBytes, result.Metrics.FingerprintBytes);
        Assert.Equal(audit.MetadataBytes, result.Metrics.MetadataBytes);
        Assert.Equal(audit.PayloadBytes, result.Metrics.PayloadBytes);
        Assert.Equal(audit.FingerprintBytes + audit.MetadataBytes + audit.PayloadBytes, result.Metrics.SourceBytes);
        Assert.Equal(3089, result.Metrics.OutputVerificationBytes);
        Assert.Equal(781, result.Metrics.SynthesizedZeroBytes);
        Assert.Single(capture.Values.Where(p => p.IsTerminal));
        Assert.Equal(result.Metrics, capture.Values.Last());
        Assert.All(audit.PayloadReads, r => Assert.Contains(new[]
        {
            (f.Offset(20), 37), (f.Offset(22), 1031), (f.Offset(26), 519),
            (f.Offset(30), 512), (f.Offset(34), 188), (f.Offset(36), 21),
        }, allowed => r.Offset >= allowed.Item1 && r.Offset + r.Bytes <= allowed.Item1 + allowed.Item2));
        var after = Fingerprint(h.Image);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Proof = "Phase8B production scan and reconstruction",
            SourceBefore = new { before.Sha256, before.Length, before.Ticks },
            SourceAfter = new { after.Sha256, after.Length, after.Ticks },
            result.Metrics,
            audit.Opens,
            audit.Scans,
            audit.Fingerprints,
            Files = result.Files
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(512, 512)]
    [InlineData(513, 513)]
    [InlineData(131079, 131079)]
    [InlineData(1300, 519)]
    [InlineData(1025, 0)]
    [InlineData(513, 512)]
    public async Task ExactLengthsCopyOnlyInitializedDataAndNeverPadding(int size, int valid)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("data.bin", size == 0 ? 0u : 20u, size, valid: valid);
        f.Bytes.AsSpan(f.Offset(20), Math.Max(512, (size + 511) / 512 * 512)).Fill(0xD7);
        var audit = new ReadAudit(f.PayloadRanges);
        using var h = new Harness(f, audit: audit);
        var session = await h.Scan(); var result = await h.Recover(session);
        var file = Assert.Single(result.Files);
        Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, file.Outcome);
        Assert.Equal(Enumerable.Repeat((byte)0xD7, valid).Concat(new byte[size - valid]).ToArray(), File.ReadAllBytes(file.OutputPath!));
        Assert.Equal(valid, audit.PayloadBytes);
        Assert.All(audit.PayloadReads, r => Assert.True(r.Offset + r.Bytes <= f.Offset(20) + valid));
        Assert.Equal(valid, file.CopiedBytes); Assert.Equal(size - valid, file.SynthesizedZeroBytes);
    }

    [Theory]
    [InlineData(512, 1, 4096)]
    [InlineData(4096, 4, 8192)]
    public async Task RecoveryUsesValidatedVolumeOffsetAndClusterGeometry(int sector, int sectorsPerCluster, int volumeOffset)
    {
        var f = new ExFatTestFixtureBuilder(sector, sectorsPerCluster);
        f.AddFile("offset.bin", 20, f.ClusterSize + 9, valid: f.ClusterSize + 3);
        var data = Enumerable.Range(0, f.ClusterSize + 3).Select(i => (byte)(i * 7)).ToArray();
        f.WritePayload([20, 21], data);
        using var h = new Harness(f, volumeOffset: volumeOffset); var session = await h.Scan();
        var result = await h.Recover(session);
        Assert.Equal(data.Concat(new byte[6]).ToArray(), File.ReadAllBytes(Assert.Single(result.Files).OutputPath!));
        Assert.Equal(volumeOffset, session.VolumeOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedChainsRequireExplicitPolicyAndPreserveOrder(bool allow)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("chain", 28, 600, contiguous: false); f.SetFat(28, 20); f.SetFat(20, uint.MaxValue);
        f.Bytes.AsSpan(f.Offset(28), 512).Fill(0xAB); f.Bytes.AsSpan(f.Offset(20), 512).Fill(0xCD);
        using var h = new Harness(f); var session = await h.Scan();
        var result = await h.Recover(session, new(AllowPreservedFatChain: allow));
        var file = Assert.Single(result.Files);
        Assert.Equal(allow ? ExFatRecoveryOutcome.ReconstructedCopyVerified : ExFatRecoveryOutcome.Blocked, file.Outcome);
        if (allow) Assert.Equal(Enumerable.Repeat((byte)0xAB, 512).Concat(Enumerable.Repeat((byte)0xCD, 88)).ToArray(), File.ReadAllBytes(file.OutputPath!));
        else Assert.Empty(Directory.GetFiles(h.Destination));
    }

    [Theory]
    [InlineData("allocated")]
    [InlineData("partial")]
    [InlineData("conflict")]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("bad")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("checksum")]
    public async Task UntrustworthyBytePlansAreBlocked(string damage)
    {
        var f = new ExFatTestFixtureBuilder();
        var chain = damage is "missing" or "cycle" or "bad" or "short" or "long";
        f.AddFile("blocked", 20, 513, contiguous: !chain, badChecksum: damage == "checksum");
        if (damage is "allocated" or "partial") f.SetAllocated(20);
        if (damage == "allocated") f.SetAllocated(21);
        if (damage == "conflict") { f.AddFile("active", 20, 513, deleted: false); f.SetAllocated(20); f.SetAllocated(21); }
        if (damage == "cycle") f.SetFat(20, 20);
        if (damage == "bad") f.SetFat(20, 0xFFFFFFF7);
        if (damage == "short") f.SetFat(20, uint.MaxValue);
        if (damage == "long") { f.SetFat(20, 22); f.SetFat(22, 24); f.SetFat(24, uint.MaxValue); }
        using var h = new Harness(f); var session = await h.Scan();
        var result = await h.Recover(session, new(AllowPreservedFatChain: true));
        Assert.Equal(ExFatRecoveryOutcome.Blocked, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination)); Assert.Equal(0, result.Metrics.PayloadBytes);
    }

    [Theory]
    [InlineData("bitmap")]
    [InlineData("ownership")]
    [InlineData("geometry")]
    [InlineData("canceled")]
    public async Task IncompleteOrInvalidScansHaveNoRecoverySession(string damage)
    {
        var f = Simple();
        if (damage == "bitmap") f.Bytes[f.RootSlot(0)] = 1;
        if (damage == "ownership") f.AddFile("broken", 30, 1000, deleted: false, contiguous: false);
        if (damage == "geometry") { f.Bytes[3] = 0; f.Bytes[f.SectorSize * 12 + 3] = 0; }
        using var h = new Harness(f); using var cts = new CancellationTokenSource();
        if (damage == "canceled") cts.Cancel();
        var scan = await h.Service.ScanAsync(h.Image, new(), null, cts.Token);
        Assert.Null(scan.Session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.CreateRecoveryPlanAsync(new(Guid.NewGuid(), scan.Result.Candidates.Select(c => c.CandidateId).ToArray(), h.Destination, new()), default));
    }

    [Fact]
    public async Task RegistryRejectsForeignDisposedAndForgedIdentifiersAndSingleUsePlans()
    {
        using var h = new Harness(Simple()); using var other = new Harness(Simple());
        var session = await h.Scan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Plan(session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session, ids: ["forged"]));
        var first = await h.Plan(session);
        await h.Service.RecoverAsync(first.PlanId, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecoverAsync(first.PlanId, null, default));
        var plan = await h.Plan(session);
        Assert.True(h.Service.DisposeSession(session.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecoverAsync(plan.PlanId, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session));
        Assert.Empty(typeof(TrustedExFatRecoveryRequest).GetConstructors());
        Assert.Equal(new[] { "CandidateIds", "Destination", "Policy", "SessionId" }, typeof(ExFatRecoveryRequest).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("entry")]
    [InlineData("active")]
    [InlineData("geometry")]
    [InlineData("layout")]
    [InlineData("replace")]
    public async Task SourceMutationOrReplacementInvalidatesSessionBeforeAnyOutput(string change)
    {
        var f = Simple(); using var h = new Harness(f); var session = await h.Scan(); var plan = await h.Plan(session);
        var bytes = File.ReadAllBytes(h.Image); var timestamp = File.GetLastWriteTimeUtc(h.Image);
        if (change == "replace") File.Move(h.Image, Path.Combine(h.Root, "old.img"));
        else bytes[change switch { "payload" => f.Offset(20), "entry" => f.RootSlot(3) + 4, "active" => f.RootSlot(3), "geometry" => 100, _ => f.RootSlot(4) + 1 }] ^= 0x80;
        File.WriteAllBytes(h.Image, bytes); File.SetLastWriteTimeUtc(h.Image, timestamp);
        var result = await h.Service.RecoverAsync(plan.PlanId, null, default);
        Assert.Equal(ExFatRecoveryBatchOutcome.SourceChanged, result.Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session));
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("geometry")]
    [InlineData("candidate")]
    public async Task FreshProductionAttestationAndProvenanceAreMandatory(string change)
    {
        var audit = new ReadAudit([]) { AlterScan = change };
        using var h = new Harness(Simple(), audit: audit); var session = await h.Scan();
        var result = await h.Recover(session);
        Assert.Equal(ExFatRecoveryBatchOutcome.MetadataChanged, result.Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination));
    }

    [Theory]
    [InlineData("report.txt", "report.txt")]
    [InlineData("caf\u00e9-\u4e2d.bin", "caf\u00e9-\u4e2d.bin")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("COM\u00b9.txt", "_COM\u00b9.txt")]
    public async Task FlatNamesUseSharedSanitization(string name, string expected)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile(name, 20, 3);
        using var h = new Harness(f); var session = await h.Scan(); var result = await h.Recover(session);
        var item = Assert.Single(result.Files);
        Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, item.Outcome);
        Assert.Equal(expected, Path.GetFileName(item.OutputPath));
        Assert.Equal(h.Destination, Path.GetDirectoryName(item.OutputPath));
    }

    [Theory]
    [InlineData("..\\escape:ads.bin")]
    [InlineData("a/b.bin")]
    public async Task TraversalAndAdsMetadataCannotObtainATrustedSession(string name)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile(name, 20, 3);
        using var h = new Harness(f);
        Assert.Null((await h.Service.ScanAsync(h.Image, new(), null, default)).Session);
        Assert.Empty(Directory.GetFiles(h.Destination));
        var safe = RecoveryDestination.SanitizeComponent(name, "fallback");
        Assert.DoesNotContain(':', safe); Assert.DoesNotContain('/', safe); Assert.DoesNotContain('\\', safe);
    }

    [Fact]
    public async Task MissingDestinationRequiresExplicitCreationAndSourcePathIsNeverADestination()
    {
        using var h = new Harness(Simple()); var session = await h.Scan();
        Directory.Delete(h.Destination);
        Assert.Equal(ExFatRecoveryBatchOutcome.DestinationUnavailable, (await h.Recover(session)).Outcome);
        Assert.False(Directory.Exists(h.Destination));
        Assert.Equal(ExFatRecoveryBatchOutcome.Completed, (await h.Recover(session, new(CreateDestinationIfMissing: true))).Outcome);
        var before = Fingerprint(h.Image);
        var plan = await h.Service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(c => c.CandidateId).ToArray(), h.Image, new()), default);
        Assert.Equal(ExFatRecoveryBatchOutcome.DestinationUnavailable, (await h.Service.RecoverAsync(plan.PlanId, null, default)).Outcome);
        Assert.Equal(before, Fingerprint(h.Image));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("verify")]
    [InlineData("mismatch")]
    public async Task IsolatedFailuresContinueWithoutPartialsOrFalseSuccess(string fault)
    {
        var f = Simple(two: true); var audit = new ReadAudit(f.PayloadRanges) { PayloadFault = fault is "read" or "short" ? fault : null };
        var files = new FaultFiles(fault);
        using var h = new Harness(f, audit, files); var session = await h.Scan(); var before = Fingerprint(h.Image);
        var result = await h.Recover(session);
        Assert.Equal(ExFatRecoveryBatchOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(fault is "read" or "short" ? ExFatRecoveryOutcome.SourceReadFailed : fault == "write" ? ExFatRecoveryOutcome.DestinationFailed : ExFatRecoveryOutcome.VerificationFailed, result.Files[0].Outcome);
        Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, result.Files[1].Outcome);
        Assert.Single(Directory.GetFiles(h.Destination)); Assert.Empty(Directory.GetFiles(h.Destination, "*.partial"));
        Assert.Equal(before, Fingerprint(h.Image));
        if (fault is "read" or "short") Assert.Equal(result.Metrics.SourceBytes + 10, result.Metrics.ChargedSourceBytes);
    }

    [Fact]
    public async Task InsufficientSpaceStopsBeforeArtifactsAndCleanupFailureReportsOnlyOwnedPath()
    {
        using (var h = new Harness(Simple(), files: new FaultFiles("space")))
        {
            var result = await h.Recover(await h.Scan());
            Assert.Equal(ExFatRecoveryBatchOutcome.DestinationUnavailable, result.Outcome);
            Assert.Empty(Directory.GetFiles(h.Destination));
        }
        using (var h = new Harness(Simple(), files: new FaultFiles("cleanup")))
        {
            File.WriteAllText(Path.Combine(h.Destination, "unrelated.txt"), "keep");
            var result = await h.Recover(await h.Scan());
            var file = Assert.Single(result.Files);
            Assert.Equal(ExFatRecoveryOutcome.CleanupFailed, file.Outcome);
            var diagnostic = Assert.Single(file.Diagnostics);
            Assert.Equal(h.Destination, Path.GetDirectoryName(diagnostic.OwnedPath));
            Assert.True(File.Exists(diagnostic.OwnedPath));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(h.Destination, "unrelated.txt")));
        }
    }

    [Fact]
    public async Task FailedExclusiveCreateDoesNotClaimOrDeleteUnrelatedFile()
    {
        var files = new FaultFiles("create-collision"); using var h = new Harness(Simple(), files: files);
        var result = await h.Recover(await h.Scan());
        Assert.Equal(ExFatRecoveryOutcome.DestinationFailed, Assert.Single(result.Files).Outcome);
        Assert.Equal("unrelated", File.ReadAllText(files.UnrelatedPath!));
        Assert.Equal(0, files.CleanupCalls);
    }

    [Theory]
    [InlineData(ExFatRecoveryPhase.ValidatingSource)]
    [InlineData(ExFatRecoveryPhase.RevalidatingMetadata)]
    [InlineData(ExFatRecoveryPhase.Recovering)]
    [InlineData(ExFatRecoveryPhase.ValidatingSourceAfterCopy)]
    [InlineData(ExFatRecoveryPhase.VerifyingOutput)]
    public async Task CancellationAtEveryPhaseCleansStagedArtifactsAndHasOneTerminal(ExFatRecoveryPhase phase)
    {
        using var h = new Harness(Simple()); var session = await h.Scan(); using var cts = new CancellationTokenSource();
        var capture = new Capture(p => { if (p.Phase == phase) cts.Cancel(); });
        var result = await h.Recover(session, capture: capture, token: cts.Token);
        Assert.Equal(ExFatRecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination));
        Assert.Single(capture.Values.Where(p => p.IsTerminal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInsideCopyOrVerificationPreservesEarlierVerifiedOutput(bool verification)
    {
        using var cts = new CancellationTokenSource();
        var files = new FaultFiles(verification ? "cancel-second-verify" : "none", cts);
        using var h = new Harness(Simple(two: true), files: files); var session = await h.Scan();
        var capture = new Capture(p => { if (!verification && p.CopiedBytes > 0) cts.Cancel(); });
        var result = await h.Recover(session, capture: capture, token: cts.Token);
        Assert.Equal(ExFatRecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination, "*.partial"));
        if (verification)
        {
            Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, result.Files[0].Outcome);
            Assert.True(File.Exists(result.Files[0].OutputPath)); Assert.Single(Directory.GetFiles(h.Destination));
        }
        else Assert.Empty(Directory.GetFiles(h.Destination));
    }

    [Fact]
    public async Task SourceInvalidationAfterStagingStopsWholeBatchBeforePublication()
    {
        var audit = new ReadAudit([]) { ChangeFinalFingerprint = true };
        using var h = new Harness(Simple(two: true), audit); var session = await h.Scan();
        var result = await h.Recover(session);
        Assert.Equal(ExFatRecoveryBatchOutcome.SourceChanged, result.Outcome);
        Assert.All(result.Files, r => Assert.Equal(ExFatRecoveryOutcome.SourceChanged, r.Outcome));
        Assert.Empty(Directory.GetFiles(h.Destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session));
    }

    [Fact]
    public async Task HeldSourceHandleRejectsNormalWriteOrReplacementDuringExtraction()
    {
        using var h = new Harness(Simple()); var session = await h.Scan(); var before = Fingerprint(h.Image); var attempts = 0;
        var capture = new Capture(p =>
        {
            if (p.Phase != ExFatRecoveryPhase.Recovering || attempts++ != 0) return;
            Assert.Throws<IOException>(() => File.WriteAllBytes(h.Image, [0]));
            Assert.Throws<IOException>(() => File.Move(h.Image, Path.Combine(h.Root, "moved.img")));
        });
        Assert.Equal(ExFatRecoveryBatchOutcome.Completed, (await h.Recover(session, capture: capture)).Outcome);
        Assert.True(attempts > 0); Assert.Equal(before, Fingerprint(h.Image));
    }

    [Fact]
    public async Task HeldDestinationAndAncestorsCannotBeRenamedAfterStagedStreamsClose()
    {
        using var h = new Harness(Simple()); var session = await h.Scan(); var attempts = 0;
        string? guard = null;
        var capture = new Capture(p =>
        {
            if (p.Phase == ExFatRecoveryPhase.Recovering && guard is null)
            {
                guard = Assert.Single(Directory.GetFiles(h.Destination, ".drs-recovery-guard-*.tmp"));
                Assert.Throws<IOException>(() => File.Delete(guard));
            }
            if (p.Phase != ExFatRecoveryPhase.VerifyingOutput || attempts++ != 0) return;
            Assert.Throws<IOException>(() => Directory.Move(h.Destination, h.Destination + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(h.Root, h.Root + "-moved"));
        });
        var result = await h.Recover(session, capture: capture);
        Assert.True(result.Outcome == ExFatRecoveryBatchOutcome.Completed, JsonSerializer.Serialize(result));
        Assert.True(attempts > 0);
        Assert.NotNull(guard); Assert.False(File.Exists(guard));
    }

    [Fact]
    public async Task DisposingSessionDuringCopyCancelsAndCleansItsPlan()
    {
        using var h = new Harness(Simple()); var session = await h.Scan();
        var capture = new Capture(p => { if (p.CopiedBytes > 0) h.Service.DisposeSession(session.SessionId); });
        Assert.Equal(ExFatRecoveryBatchOutcome.Canceled, (await h.Recover(session, capture: capture)).Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session));
    }

    [Fact]
    public async Task RemovedSourceInvalidatesTheSession()
    {
        using var h = new Harness(Simple()); var session = await h.Scan(); var plan = await h.Plan(session);
        File.Delete(h.Image);
        Assert.Equal(ExFatRecoveryBatchOutcome.SourceChanged, (await h.Service.RecoverAsync(plan.PlanId, null, default)).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingFingerprintReadIsInterruptedByCancellationOrDeadline(bool deadline)
    {
        using var cts = new CancellationTokenSource();
        var audit = new ReadAudit([]) { BlockFingerprint = true };
        using var h = new Harness(Simple(), audit); var session = await h.Scan();
        var plan = await h.Plan(session, deadline ? new(MaximumDuration: TimeSpan.FromMilliseconds(100)) : new());
        var pending = h.Service.RecoverAsync(plan.PlanId, null, cts.Token);
        await audit.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!deadline) cts.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(deadline ? ExFatRecoveryBatchOutcome.BudgetExceeded : ExFatRecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(h.Destination));
    }

    [Fact]
    public async Task RuntimeMetadataTraversalLimitStopsBeforeDestinationWrites()
    {
        using var h = new Harness(Simple()); var session = await h.Scan();
        var result = await h.Recover(session, new(MaximumTotalClusters: 1));
        Assert.Equal(ExFatRecoveryBatchOutcome.BudgetExceeded, result.Outcome);
        Assert.True(result.Metrics.FingerprintBytes > 0); Assert.True(result.Metrics.MetadataBytes > 0);
        Assert.Empty(Directory.GetFiles(h.Destination));
    }

    [Fact]
    public async Task BoundedCollisionExhaustionKeepsExistingFileAndContinues()
    {
        using var h = new Harness(Simple(two: true)); var session = await h.Scan();
        File.WriteAllText(Path.Combine(h.Destination, "first.bin"), "keep");
        var result = await h.Recover(session, new(MaximumCollisionAttempts: 1));
        Assert.Equal(ExFatRecoveryOutcome.DestinationFailed, result.Files[0].Outcome);
        Assert.Equal(ExFatRecoveryOutcome.ReconstructedCopyVerified, result.Files[1].Outcome);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(h.Destination, "first.bin")));
        Assert.Empty(Directory.GetFiles(h.Destination, "*.partial"));
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("replace-output")]
    public async Task IndependentVerificationRejectsWrongLengthAndCleanupDoesNotDeleteReplacement(string fault)
    {
        using var h = new Harness(Simple(), files: new FaultFiles(fault)); var result = await h.Recover(await h.Scan());
        var item = Assert.Single(result.Files);
        Assert.Equal(fault == "truncate" ? ExFatRecoveryOutcome.VerificationFailed : ExFatRecoveryOutcome.CleanupFailed, item.Outcome);
        if (fault == "truncate") Assert.Empty(Directory.GetFiles(h.Destination));
        else Assert.Equal("unrelated replacement", File.ReadAllText(Assert.Single(item.Diagnostics).OwnedPath!));
    }

    [Fact]
    public async Task DeletedDirectoriesNeverAppearInRecoveryCatalog()
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("directory", 20, 512, directory: true);
        using var h = new Harness(f); var session = await h.Scan();
        Assert.Empty(session.Result.Candidates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Plan(session, ids: ["directory"]));
    }

    public static IEnumerable<object[]> PreflightBudgets()
    {
        yield return [new ExFatRecoveryPolicy(MaximumSourceBytes: 1)];
        yield return [new ExFatRecoveryPolicy(MaximumOutputBytes: 1)];
        yield return [new ExFatRecoveryPolicy(MaximumFileBytes: 1)];
        yield return [new ExFatRecoveryPolicy(MaximumChainLength: 0)];
        yield return [new ExFatRecoveryPolicy(MaximumTotalClusters: 0)];
        yield return [new ExFatRecoveryPolicy(MaximumDuration: TimeSpan.FromTicks(1))];
    }
    [Theory]
    [MemberData(nameof(PreflightBudgets))]
    public async Task ImpossibleBudgetsFailBeforeOpeningSourceOrDestination(ExFatRecoveryPolicy policy)
    {
        var audit = new ReadAudit([]); using var h = new Harness(Simple(), audit); var session = await h.Scan();
        var result = await h.Recover(session, policy);
        Assert.Equal(ExFatRecoveryBatchOutcome.BudgetExceeded, result.Outcome);
        Assert.Equal(0, audit.Opens); Assert.Equal(0, result.Metrics.SourceBytes);
        Assert.Empty(Directory.GetFiles(h.Destination)); Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task KnownMetadataReadsAreIncludedInPreflightAndExactBudgetCanComplete()
    {
        var f = Simple(); using var h = new Harness(f); var session = await h.Scan();
        var result = await h.Recover(session, new(MaximumSourceBytes: 2L * f.Bytes.Length + 10));
        Assert.Equal(ExFatRecoveryBatchOutcome.BudgetExceeded, result.Outcome);
        Assert.Equal(0, result.Metrics.FingerprintBytes);
        Assert.Empty(Directory.GetFiles(h.Destination));
        var plan = await h.Plan(session);
        result = await h.Recover(session, new(MaximumSourceBytes: plan.MinimumSourceBytes));
        Assert.Equal(ExFatRecoveryBatchOutcome.Completed, result.Outcome);
        Assert.Equal(plan.MinimumSourceBytes, result.Metrics.SourceBytes);
    }

    [Fact]
    public async Task LoweredCandidateAndProgressLimitsRemainBounded()
    {
        using var h = new Harness(Simple(two: true)); var session = await h.Scan();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Plan(session, new(MaximumCandidates: 1)));
        var capture = new Capture(); var result = await h.Recover(session, new(MaximumProgressCallbacks: 1), capture);
        Assert.True(Assert.Single(capture.Values).IsTerminal); Assert.Equal(2, result.Files.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatRecoveryPolicy(MaximumSourceBytes: long.MaxValue).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatRecoveryPolicy(BufferSize: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatRecoveryPolicy(MaximumDiagnostics: 0).Validate());
    }

    [Fact]
    public async Task ExistingSourceHardLinkCannotBeOverwrittenByOutputPublication()
    {
        using var h = new Harness(Simple()); var session = await h.Scan(); var before = Fingerprint(h.Image);
        var alias = Path.Combine(h.Destination, "first.bin");
        Assert.True(CreateHardLink(alias, h.Image, IntPtr.Zero), Marshal.GetLastWin32Error().ToString());
        Assert.Equal(RegularFileIdentity.CapturePath(h.Image), RegularFileIdentity.CapturePath(alias));
        var result = await h.Recover(session);
        Assert.Equal("first (1).bin", Path.GetFileName(Assert.Single(result.Files).OutputPath));
        Assert.Equal(before, Fingerprint(alias)); Assert.Equal(before, Fingerprint(h.Image));
    }

    [Fact]
    public async Task DestinationJunctionAndAdsAreRejectedWithoutEscape()
    {
        using var h = new Harness(Simple()); var session = await h.Scan();
        var link = Path.Combine(h.Root, "junction"); var outside = Path.Combine(h.Root, "outside"); Directory.CreateDirectory(outside);
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
        try
        {
            foreach (var destination in new[] { link, h.Destination + ":ads" })
            {
                var plan = await h.Service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(c => c.CandidateId).ToArray(), destination, new()), default);
                Assert.Equal(ExFatRecoveryBatchOutcome.DestinationUnavailable, (await h.Service.RecoverAsync(plan.PlanId, null, default)).Outcome);
                Assert.Empty(Directory.GetFiles(outside));
            }
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void RecoveryHasNoAppOrWorkerCompositionAndSourceOpenerRemainsReadOnly()
    {
        var root = RepositoryRoot();
        foreach (var project in new[] { "DataRecoveryStudio.App", "DataRecoveryStudio.ScanWorker" })
            foreach (var file in Directory.GetFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
                Assert.DoesNotContain("ExFatImageRecovery", File.ReadAllText(file), StringComparison.Ordinal);
        var source = File.ReadAllText(Path.Combine(root, "src/DataRecoveryStudio.Infrastructure/ReadOnlyRandomAccessSources.cs"));
        Assert.Contains("FileAccess.Read,", source, StringComparison.Ordinal); Assert.Contains("FileShare.Read,", source, StringComparison.Ordinal);
        Assert.Contains("bufferSize: 1,", source, StringComparison.Ordinal);
    }

    private static ExFatTestFixtureBuilder Simple(bool two = false)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("first.bin", 20, 10); f.Bytes.AsSpan(f.Offset(20), 512).Fill(0x71);
        if (two) { f.AddFile("second.bin", 22, 10); f.Bytes.AsSpan(f.Offset(22), 512).Fill(0x72); }
        return f;
    }
    private static (string Sha256, long Length, long Ticks) Fingerprint(string path) =>
        (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks);
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    private sealed class Harness : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "DataRecoveryStudioTests", "Phase8B", Guid.NewGuid().ToString("N"));
        internal string Image => Path.Combine(Root, "fixture.img");
        internal string Destination => Path.Combine(Root, "output");
        internal ExFatImageScanService Service { get; }
        private readonly int _volumeOffset;
        internal Harness(ExFatTestFixtureBuilder fixture, ReadAudit? audit = null, RecoveryFileOperations? files = null, int volumeOffset = 0)
        {
            _volumeOffset = volumeOffset;
            Directory.CreateDirectory(Destination); File.WriteAllBytes(Image, new byte[volumeOffset].Concat(fixture.Bytes).ToArray());
            audit ??= new([]);
            var engine = new ExFatImageRecoveryEngine(new AuditedFactory(audit), new AuditedScanner(audit), new AuditedFingerprint(audit), files ?? new());
            Service = new(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), new ExFatSourceMetadataProvider(), engine);
        }
        internal async Task<ExFatScanSession> Scan()
        {
            var scan = await Service.ScanAsync(Image, new(_volumeOffset), null, default);
            Assert.Equal(ExFatScanOutcome.Completed, scan.Result.Outcome);
            Assert.NotNull(scan.Session); return scan.Session!;
        }
        internal Task<ExFatRecoveryPlan> Plan(ExFatScanSession session, ExFatRecoveryPolicy? policy = null, string[]? ids = null) =>
            Service.CreateRecoveryPlanAsync(new(session.SessionId, ids ?? session.Result.Candidates.Select(c => c.CandidateId).ToArray(), Destination, policy ?? new()), default);
        internal async Task<ExFatRecoveryBatchResult> Recover(ExFatScanSession session, ExFatRecoveryPolicy? policy = null, Capture? capture = null, CancellationToken token = default) =>
            await Service.RecoverAsync((await Plan(session, policy)).PlanId, capture, token);
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DataRecoveryStudioTests", "Phase8B")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup root.");
            Directory.Delete(Root, true);
        }
    }
    private sealed class Capture(Action<ExFatRecoveryProgress>? callback = null) : IProgress<ExFatRecoveryProgress>
    {
        internal readonly List<ExFatRecoveryProgress> Values = [];
        public void Report(ExFatRecoveryProgress value)
        {
            if (Values.Count != 0) { Assert.True(value.SourceBytes >= Values[^1].SourceBytes); Assert.True(value.CopiedBytes >= Values[^1].CopiedBytes); }
            Values.Add(value); callback?.Invoke(value);
        }
    }
    private sealed class ReadAudit(List<(long Offset, long Length)> payload)
    {
        internal readonly List<(long Offset, long Length)> Payload = payload;
        internal readonly List<(long Offset, int Bytes)> PayloadReads = [];
        internal string Category = "payload";
        internal int Opens, Scans, Fingerprints;
        internal long FingerprintBytes, MetadataBytes, PayloadBytes;
        internal string? PayloadFault, AlterScan;
        internal bool ChangeFinalFingerprint;
        internal bool BlockFingerprint;
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class AuditedFactory(ReadAudit audit) : IReadOnlyImageSourceFactory
    {
        public async ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken token)
        { audit.Opens++; return new AuditedSource(await new ExFatImageSourceFactory().OpenAsync(path, token), audit); }
    }
    private sealed class AuditedSource(IReadOnlyRandomAccessSource inner, ReadAudit audit) : IReadOnlyRandomAccessSource, IRegularFileIdentitySource
    {
        public long Length => inner.Length;
        public string FileIdentity => ((IRegularFileIdentitySource)inner).FileIdentity;
        public void ValidateIdentity(string path) => ((IRegularFileIdentitySource)inner).ValidateIdentity(path);
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            if (audit.BlockFingerprint && audit.Category == "fingerprint")
            { audit.Blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            if (audit.Category == "metadata")
                Assert.DoesNotContain(audit.Payload, r => offset < r.Offset + r.Length && r.Offset < offset + destination.Length);
            if (audit.Category == "payload" && audit.PayloadFault is not null)
            {
                var fault = audit.PayloadFault; audit.PayloadFault = null;
                if (fault == "short") { await inner.ReadExactlyAsync(offset, destination[..1], token); throw new EndOfStreamException("Injected short read."); }
                throw new IOException("Injected payload failure.");
            }
            await inner.ReadExactlyAsync(offset, destination, token);
            if (audit.Category == "fingerprint") audit.FingerprintBytes += destination.Length;
            else if (audit.Category == "metadata") audit.MetadataBytes += destination.Length;
            else { audit.PayloadBytes += destination.Length; audit.PayloadReads.Add((offset, destination.Length)); }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class AuditedScanner(ReadAudit audit) : IExFatMetadataScanner
    {
        public async Task<ExFatScanResult> ScanAsync(IReadOnlyRandomAccessSource source, ExFatScanRequest request, IProgress<ExFatScanProgress>? progress, CancellationToken token)
        {
            audit.Scans++; audit.Category = "metadata";
            try
            {
                var result = await new ExFatMetadataScanner().ScanAsync(source, request, progress, token);
                return audit.AlterScan switch
                {
                    "clone" => result with { Outcome = ExFatScanOutcome.Completed },
                    "geometry" => result with { Geometry = result.Geometry! with { SerialNumber = 1 } },
                    "candidate" => result with { Candidates = [] },
                    _ => result,
                };
            }
            finally { audit.Category = "payload"; }
        }
    }
    private sealed class AuditedFingerprint(ReadAudit audit) : IExFatSourceMetadataProvider
    {
        public async ValueTask<ExFatSourceFingerprint> CaptureAsync(string path, IReadOnlyRandomAccessSource source, ExFatScanRequest request, CancellationToken token)
        {
            audit.Fingerprints++; audit.Category = "fingerprint";
            try
            {
                var result = await new ExFatSourceMetadataProvider().CaptureAsync(path, source, request, token);
                return audit.ChangeFinalFingerprint && audit.Fingerprints == 2 ? result with { Sha256 = new string('0', 64) } : result;
            }
            finally { audit.Category = "payload"; }
        }
    }
    private sealed class FaultFiles(string fault, CancellationTokenSource? cancel = null) : RecoveryFileOperations
    {
        private int _creates, _verifications;
        internal int CleanupCalls;
        internal string? UnrelatedPath;
        internal override RecoveryDestination OpenDestination(string path, string source, bool create, long bytes) =>
            fault == "space" ? throw new IOException("Injected insufficient capacity.") : base.OpenDestination(path, source, create, bytes);
        internal override FileStream CreatePartial(string path, int bufferSize)
        {
            if (++_creates == 1)
            {
                if (fault == "create-collision") { File.WriteAllText(path, "unrelated"); UnrelatedPath = path; }
                if (fault is "write" or "cleanup") return new FailingStream(path, bufferSize);
            }
            return base.CreatePartial(path, bufferSize);
        }
        internal override Task<(string Sha256, long Bytes)> VerifyAsync(string path, int bufferSize, long bytes, CancellationToken token, Action<int> bytesRead)
        {
            _verifications++;
            if (_verifications == 1)
            {
                if (fault == "verify") throw new IOException("Injected independent reread error.");
                if (fault == "mismatch") return Task.FromResult((new string('0', 64), bytes));
                if (fault == "truncate") File.WriteAllBytes(path, [1]);
                if (fault == "replace-output") { File.Move(path, path + ".displaced"); File.WriteAllText(path, "unrelated replacement"); }
            }
            if (_verifications == 2 && fault == "cancel-second-verify") cancel!.Cancel();
            return base.VerifyAsync(path, bufferSize, bytes, token, bytesRead);
        }
        internal override bool Cleanup(string path, RegularFileIdentity identity, RecoveryDestination destination, int attempts)
        { CleanupCalls++; return fault != "cleanup" && base.Cleanup(path, identity, destination, attempts); }
    }
    private sealed class FailingStream(string path, int bufferSize) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await base.WriteAsync(buffer[..1], token); throw new IOException("Injected write error after a partial write."); }
    }
}
