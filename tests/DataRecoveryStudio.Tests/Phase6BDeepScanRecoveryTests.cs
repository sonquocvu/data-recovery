using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase6BDeepScanRecoveryTests
{
    [Fact]
    public async Task EndToEnd_ScansPlansAndCarvesAllFormatsByteForByte()
    {
        using var workspace = new CarvingWorkspace();
        var fixtures = new Dictionary<string, byte[]>
        {
            ["JPEG"] = CreateJpeg(),
            ["PNG"] = CreatePng(),
            ["GIF"] = CreateGif("GIF89a"),
            ["PDF"] = CreatePdf(),
            ["ZIP"] = CreateZip("payload/unsafe-name.txt", "zip-content"),
        };
        var image = new List<byte>(Enumerable.Repeat((byte)0xA5, 4095));
        var expectedOffsets = new Dictionary<string, long>();
        foreach (var fixture in fixtures)
        {
            expectedOffsets[fixture.Key] = image.Count;
            image.AddRange(fixture.Value);
            image.AddRange(Enumerable.Repeat((byte)0x5A, 37));
        }

        await workspace.WriteImageAsync(image.ToArray());
        var sourceBefore = await ProofAsync(workspace.ImagePath);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider(), new(ChunkSize: 4096)), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(item => item.CandidateId).ToArray(), workspace.OutputPath), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryBatchOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.Files.Count);
        foreach (var output in result.Files)
        {
            var expected = fixtures[output.FormatId];
            Assert.Equal(expected, await File.ReadAllBytesAsync(output.OutputPath!));
            Assert.Equal(expected.Length, output.BytesWritten);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), output.Sha256);
            Assert.Equal(RecoveryVerificationState.Verified, output.Verification);
            Assert.Equal(DeepScanRecoveryFileOutcome.CarvedAndCopyVerified, output.Outcome);
            Assert.Equal($"Carved_{output.FormatId}_{expectedOffsets[output.FormatId]:X16}{Extension(output.FormatId)}", Path.GetFileName(output.OutputPath));
            Assert.StartsWith(Path.GetFullPath(workspace.OutputPath) + Path.DirectorySeparatorChar, Path.GetFullPath(output.OutputPath!), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(Directory.GetFiles(workspace.OutputPath, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(sourceBefore, await ProofAsync(workspace.ImagePath));
        Console.WriteLine($"PHASE6B_E2E files={result.Files.Count} bytes={result.VerifiedBytes} runtimeMs={result.Elapsed.TotalMilliseconds:F3} sourceSha256={sourceBefore.Sha256}");
    }

    [Fact]
    public async Task RequestsUseSessionCandidateIdsAndRejectUnknownOrDisposedOwnership()
    {
        var requestProperties = typeof(DeepScanRecoveryRequest).GetProperties().Select(item => item.Name).ToArray();
        Assert.DoesNotContain(requestProperties, name => name.Contains("Offset", StringComparison.Ordinal) || name.Contains("Length", StringComparison.Ordinal) || name.Contains("Format", StringComparison.Ordinal) || name.Contains("Extension", StringComparison.Ordinal));
        Assert.Empty(typeof(TrustedDeepScanRecoveryBatchRequest).GetConstructors());

        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var candidateId = Assert.Single(session.Result.Candidates).CandidateId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRecoveryPlanAsync(
            new(session.SessionId, [Guid.NewGuid().ToString("N")], workspace.OutputPath), CancellationToken.None));
        var duplicatePlan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [candidateId, candidateId], workspace.OutputPath), CancellationToken.None);
        Assert.Single(duplicatePlan.Items);
        Assert.True(service.DisposeSession(session.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRecoveryPlanAsync(
            new(session.SessionId, [candidateId], workspace.OutputPath), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverAsync(duplicatePlan.PlanId, null, CancellationToken.None));
    }

    [Fact]
    public async Task DefaultPolicyBlocksTruncatedAndExplicitPartialPolicyUsesVisibleName()
    {
        using var workspace = new CarvingWorkspace();
        var truncated = CreateJpeg()[..^2];
        await workspace.WriteImageAsync(truncated);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var id = Assert.Single(session.Result.Candidates).CandidateId;

        var blockedPlan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [id], workspace.OutputPath), CancellationToken.None);
        var blocked = await service.RecoverAsync(blockedPlan.PlanId, null, CancellationToken.None);
        var allowedPlan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(AllowMediumTruncated: true)), CancellationToken.None);
        var allowed = await service.RecoverAsync(allowedPlan.PlanId, null, CancellationToken.None);

        Assert.False(Assert.Single(blockedPlan.Items).IsEligible);
        Assert.Equal(DeepScanRecoveryFileOutcome.SkippedByPolicy, Assert.Single(blocked.Files).Outcome);
        var partial = Assert.Single(allowed.Files);
        Assert.Equal(DeepScanRecoveryFileOutcome.CarvedPartialWithWarning, partial.Outcome);
        Assert.Contains(".partial.", Path.GetFileName(partial.OutputPath), StringComparison.Ordinal);
        Assert.Equal(truncated, await File.ReadAllBytesAsync(partial.OutputPath!));
    }

    [Fact]
    public async Task LowConfidenceAndZip64CandidatesRemainBlocked()
    {
        using var workspace = new CarvingWorkspace();
        var zip = CreateZip("one", "two");
        var eocd = Find(zip, [0x50, 0x4B, 0x05, 0x06]);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 10, 2), ushort.MaxValue);
        var input = Join(Encoding.ASCII.GetBytes("%PDF-1.7\nnoise without xref or EOF"), new byte[16], zip);
        await workspace.WriteImageAsync(input);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);

        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(item => item.CandidateId).ToArray(), workspace.OutputPath), CancellationToken.None);
        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.All(plan.Items, item => Assert.False(item.IsEligible));
        Assert.All(result.Files, item => Assert.Equal(DeepScanRecoveryFileOutcome.SkippedByPolicy, item.Outcome));
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task CollisionNeverOverwritesAndRepeatedRecoveryUsesDeterministicSuffix()
    {
        using var workspace = new CarvingWorkspace();
        var png = CreatePng();
        await workspace.WriteImageAsync(png);
        var existing = Path.Combine(workspace.OutputPath, "Carved_PNG_0000000000000000.png");
        await File.WriteAllTextAsync(existing, "existing");
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath), CancellationToken.None);

        var first = Assert.Single((await service.RecoverAsync(plan.PlanId, null, CancellationToken.None)).Files);
        var second = Assert.Single((await service.RecoverAsync(plan.PlanId, null, CancellationToken.None)).Files);

        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        Assert.Equal("Carved_PNG_0000000000000000 (1).png", Path.GetFileName(first.OutputPath));
        Assert.Equal("Carved_PNG_0000000000000000 (2).png", Path.GetFileName(second.OutputPath));
        Assert.Equal(png, await File.ReadAllBytesAsync(first.OutputPath!));
    }

    [Fact]
    public async Task AtomicPublicationFailureCleansPartialAndPreservesExistingFile()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng());
        var existing = Path.Combine(workspace.OutputPath, "Carved_PNG_0000000000000000.png");
        await File.WriteAllTextAsync(existing, "existing");
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(
            session.SessionId,
            [Assert.Single(session.Result.Candidates).CandidateId],
            workspace.OutputPath,
            new(MaximumCollisionAttempts: 1)), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryFileOutcome.DestinationFailed, Assert.Single(result.Files).Outcome);
        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        Assert.DoesNotContain(Directory.GetFiles(workspace.OutputPath), path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourceChangeAfterScanReturnsSourceChangedWithoutOutput()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreateGif("GIF87a"));
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath), CancellationToken.None);
        await using (var mutation = new FileStream(workspace.ImagePath, FileMode.Append, FileAccess.Write, FileShare.Read)) await mutation.WriteAsync(new byte[] { 9 });

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryBatchOutcome.SourceChanged, result.Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
        Assert.Contains(result.Diagnostics, item => item.Code == "SOURCE_CHANGED");
    }

    [Fact]
    public async Task CandidateRevalidationMismatchPreventsPartialCreation()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng());
        var factory = new RegularFileRandomAccessSourceFactory();
        var metadata = new DeepScanSourceMetadataProvider(factory);
        var recovery = new DeepScanImageRecoveryEngine(factory, metadata, new AlwaysChangedRevalidator());
        var service = CreateService(recoveryEngine: recovery);
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryFileOutcome.CandidateChanged, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task ExactShortReadDuringCarvingCleansPartialAndReportsSourceFailure()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng(idatLength: 128 * 1024));
        var regularFactory = new RegularFileRandomAccessSourceFactory();
        var metadata = new DeepScanSourceMetadataProvider(regularFactory);
        var recovery = new DeepScanImageRecoveryEngine(new FailOnSecondZeroOffsetFactory(regularFactory), metadata, new DeepScanCandidateRevalidator());
        var service = new ImageDeepScanService(regularFactory, new DeepScanEngine(), metadata, recovery);
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath, new(BufferSize: 4096)), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryFileOutcome.SourceReadFailed, Assert.Single(result.Files).Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task SourceChangeDuringRecoveryCleansExactPartial()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng(idatLength: 128 * 1024));
        var factory = new RegularFileRandomAccessSourceFactory();
        var stable = new DeepScanSourceMetadataProvider(factory);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath), CancellationToken.None);
        var changed = session.Source with { Sha256 = new string('0', 64) };
        var recovery = new DeepScanImageRecoveryEngine(factory, new SequencedMetadataProvider(session.Source, changed), new DeepScanCandidateRevalidator());
        var isolatedService = CreateService(recoveryEngine: recovery);
        var isolatedSession = await isolatedService.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var isolatedPlan = await isolatedService.CreateRecoveryPlanAsync(new(isolatedSession.SessionId, [Assert.Single(isolatedSession.Result.Candidates).CandidateId], workspace.OutputPath), CancellationToken.None);

        var result = await isolatedService.RecoverAsync(isolatedPlan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryBatchOutcome.SourceChanged, result.Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task CancellationMidFileCleansPartialAndRetainsNoFinalName()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng(idatLength: 2 * 1024 * 1024));
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath, new(BufferSize: 4096)), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DeepScanRecoveryProgress>(item =>
        {
            if (item.CurrentBytes >= 4096) cancellation.Cancel();
        });

        var result = await service.RecoverAsync(plan.PlanId, progress, cancellation.Token);

        Assert.Equal(DeepScanRecoveryBatchOutcome.Canceled, result.Outcome);
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    [Fact]
    public async Task MixedBatchContinuesAfterPolicyBlockedItemAndProgressIsMonotonic()
    {
        using var workspace = new CarvingWorkspace();
        var input = Join(CreatePng(), new byte[10], CreateJpeg()[..^2]);
        await workspace.WriteImageAsync(input);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(item => item.CandidateId).ToArray(), workspace.OutputPath), CancellationToken.None);
        var reports = new List<DeepScanRecoveryProgress>();

        var result = await service.RecoverAsync(plan.PlanId, new InlineProgress<DeepScanRecoveryProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(DeepScanRecoveryBatchOutcome.CompletedWithFailures, result.Outcome);
        Assert.Contains(result.Files, item => item.Outcome == DeepScanRecoveryFileOutcome.CarvedAndCopyVerified);
        Assert.Contains(result.Files, item => item.Outcome == DeepScanRecoveryFileOutcome.SkippedByPolicy);
        Assert.Equal(reports.OrderBy(item => item.CurrentBytes).Select(item => item.CurrentBytes), reports.Select(item => item.CurrentBytes));
    }

    [Fact]
    public async Task RecoveryBudgetsAndOverlapDefaultAreEnforced()
    {
        using var workspace = new CarvingWorkspace();
        var png = CreatePng(idatLength: 8192);
        await workspace.WriteImageAsync(png);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var id = Assert.Single(session.Result.Candidates).CandidateId;
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [id], workspace.OutputPath, new(MaximumBytesPerCandidate: 4096)), CancellationToken.None);

        Assert.False(Assert.Single(plan.Items).IsEligible);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepScanRecoveryPolicy(BufferSize: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepScanRecoveryPolicy(MaximumCandidatesPerBatch: 1001).Validate());
    }

    [Fact]
    public async Task OverlapIsBlockedByDefaultAndExplicitPolicyKeepsCandidatesIndependent()
    {
        using var workspace = new CarvingWorkspace();
        var bytes = new byte[40];
        bytes[0] = 1; bytes[1] = 2; bytes[10] = 3; bytes[11] = 4;
        await workspace.WriteImageAsync(bytes);
        var registry = new FileSignatureRegistry([
            Descriptor("OUTER", 2, [1, 2]),
            Descriptor("INNER", 1, [3, 4]),
        ]);
        var factory = new RegularFileRandomAccessSourceFactory();
        var metadata = new DeepScanSourceMetadataProvider(factory);
        var service = new ImageDeepScanService(factory, new DeepScanEngine(registry), metadata,
            new DeepScanImageRecoveryEngine(factory, metadata, new DeepScanCandidateRevalidator(registry)));
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        Assert.All(session.Result.Candidates, item => Assert.True(item.HasOverlapWarning));

        var blocked = await service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(item => item.CandidateId).ToArray(), workspace.OutputPath), CancellationToken.None);
        var allowed = await service.CreateRecoveryPlanAsync(new(session.SessionId, session.Result.Candidates.Select(item => item.CandidateId).ToArray(), workspace.OutputPath, new(AllowOverlappingCandidates: true)), CancellationToken.None);
        var result = await service.RecoverAsync(allowed.PlanId, null, CancellationToken.None);

        Assert.All(blocked.Items, item => Assert.False(item.IsEligible));
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, item => Assert.Equal(DeepScanRecoveryFileOutcome.CarvedAndCopyVerified, item.Outcome));
    }

    [Fact]
    public async Task DestinationDeviceNamespaceIsRejectedAndNoSourceWriteOccurs()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng());
        var before = await ProofAsync(workspace.ImagePath);
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var plan = await service.CreateRecoveryPlanAsync(new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], "\\\\.\\PhysicalDrive9"), CancellationToken.None);

        var result = await service.RecoverAsync(plan.PlanId, null, CancellationToken.None);

        Assert.Equal(DeepScanRecoveryBatchOutcome.DestinationUnavailable, result.Outcome);
        Assert.Equal(before, await ProofAsync(workspace.ImagePath));
    }

    [Fact]
    public async Task CancellationBeforePlanningCreatesNoPlanOrOutput()
    {
        using var workspace = new CarvingWorkspace();
        await workspace.WriteImageAsync(CreatePng());
        var service = CreateService();
        var session = await service.ScanAsync(workspace.ImagePath, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateRecoveryPlanAsync(
            new(session.SessionId, [Assert.Single(session.Result.Candidates).CandidateId], workspace.OutputPath), cancellation.Token));
        Assert.Empty(Directory.GetFiles(workspace.OutputPath));
    }

    private static ImageDeepScanService CreateService(ITrustedDeepScanRecoveryEngine? recoveryEngine = null)
    {
        var factory = new RegularFileRandomAccessSourceFactory();
        var metadata = new DeepScanSourceMetadataProvider(factory);
        var registry = FileSignatureRegistry.CreateDefault();
        return new(factory, new DeepScanEngine(registry), metadata, recoveryEngine ?? new DeepScanImageRecoveryEngine(factory, metadata, new DeepScanCandidateRevalidator(registry)));
    }

    private static FileSignatureDescriptor Descriptor(string id, int priority, byte[] signature) =>
        new(id, ".bin", "test", priority, 1, 1024, [new(signature)], [], [], new FixedValidator(), "test-1");

    private static byte[] CreateJpeg()
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0 };
        bytes.AddRange([0xFF, 0xC0, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0]);
        bytes.AddRange([0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 63, 0]);
        bytes.AddRange([0x11, 0xFF, 0, 0x22, 0xFF, 0xD0, 0x33, 0xFF, 0xD9]);
        return bytes.ToArray();
    }

    private static byte[] CreatePng(int idatLength = 0)
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8; ihdr[9] = 2;
        WriteChunk(stream, "IHDR", ihdr);
        WriteChunk(stream, "IDAT", new byte[idatLength]);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var value = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)data.Length));
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(value); stream.Write(typeBytes); stream.Write(data);
        var crc = 0xFFFFFFFFU;
        foreach (var item in typeBytes.Concat(data))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320U & (uint)-(int)(crc & 1));
        }

        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        stream.Write(value);
    }

    private static byte[] CreateGif(string version)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(version));
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255]);
        bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0, 0x3B]);
        return bytes.ToArray();
    }

    private static byte[] CreatePdf()
    {
        var prefix = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\n";
        var xref = Encoding.ASCII.GetByteCount(prefix);
        return Encoding.ASCII.GetBytes(prefix + "xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n" + xref + "\n%%EOF");
    }

    private static byte[] CreateZip(string name, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, 1024, false);
            writer.Write(content);
        }

        return stream.ToArray();
    }

    private static string Extension(string format) => format switch
    {
        "JPEG" => ".jpg",
        "PNG" => ".png",
        "GIF" => ".gif",
        "PDF" => ".pdf",
        "ZIP" => ".zip",
        _ => throw new InvalidOperationException(),
    };

    private static byte[] Join(params byte[][] values)
    {
        var output = new byte[values.Sum(item => item.Length)];
        var offset = 0;
        foreach (var value in values) { value.CopyTo(output, offset); offset += value.Length; }
        return output;
    }

    private static int Find(byte[] bytes, byte[] pattern)
    {
        for (var index = 0; index <= bytes.Length - pattern.Length; index++)
        {
            if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) return index;
        }

        return -1;
    }

    private static async Task<(string Sha256, long Length, long Timestamp)> ProofAsync(string path)
    {
        var info = new FileInfo(path); info.Refresh();
        return (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private sealed class AlwaysChangedRevalidator : IDeepScanCandidateRevalidator
    {
        public ValueTask<DeepScanCandidateRevalidationResult> RevalidateAsync(IReadOnlyRandomAccessSource source, TrustedDeepScanCandidate candidate, DeepScanRecoveryPolicy policy, DateTimeOffset deadlineUtc, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DeepScanCandidateRevalidationResult(false,
                new(DeepScanValidationState.StructurallyInvalid, DeepScanCompleteness.Corrupt, DeepScanConfidence.Low, null, DeepScanConfidence.Low, [], [], 0),
                "CANDIDATE_CHANGED", "Injected revalidation mismatch."));
    }

    private sealed class FixedValidator : ICarvedCandidateValidator
    {
        public ValueTask<CandidateValidationResult> ValidateAsync(IReadOnlyRandomAccessSource source, CandidateValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CandidateValidationResult(
                DeepScanValidationState.StructurallyValidated,
                DeepScanCompleteness.Complete,
                DeepScanConfidence.High,
                20,
                DeepScanConfidence.High,
                ["fixed-overlap"],
                [],
                0));
    }

    private sealed class SequencedMetadataProvider(DeepScanSourceFingerprint first, DeepScanSourceFingerprint later) : IDeepScanSourceMetadataProvider
    {
        private int _calls;
        public ValueTask<DeepScanSourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Interlocked.Increment(ref _calls) == 1 ? first : later);
    }

    private sealed class FailOnSecondZeroOffsetFactory(IReadOnlyImageSourceFactory inner) : IReadOnlyImageSourceFactory
    {
        public async ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken cancellationToken) =>
            new FailOnSecondZeroOffsetSource(await inner.OpenAsync(path, cancellationToken));
    }

    private sealed class FailOnSecondZeroOffsetSource(IReadOnlyRandomAccessSource inner) : IReadOnlyRandomAccessSource
    {
        private int _zeroOffsetReads;
        public long Length => inner.Length;
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (offset == 0 && Interlocked.Increment(ref _zeroOffsetReads) == 2) throw new EndOfStreamException("Injected exact short read.");
            await inner.ReadExactlyAsync(offset, destination, cancellationToken);
        }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class CarvingWorkspace : IDisposable
    {
        public CarvingWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DataRecoveryStudioTests", "Phase6B", Guid.NewGuid().ToString("N"));
            ImagePath = Path.Combine(Root, "source.img");
            OutputPath = Path.Combine(Root, "output");
            Directory.CreateDirectory(OutputPath);
        }

        public string Root { get; }
        public string ImagePath { get; }
        public string OutputPath { get; }
        public Task WriteImageAsync(byte[] bytes) => File.WriteAllBytesAsync(ImagePath, bytes);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DataRecoveryStudioTests", "Phase6B")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
