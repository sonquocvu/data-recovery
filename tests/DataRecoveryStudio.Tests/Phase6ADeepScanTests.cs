using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase6ADeepScanTests
{
    [Fact]
    public void Registry_IsDeterministicAndRejectsEmptyOrAmbiguousSignatures()
    {
        var registry = FileSignatureRegistry.CreateDefault();
        Assert.Equal(["PNG", "JPEG", "GIF", "PDF", "ZIP"], registry.Descriptors.Select(item => item.FormatId));
        Assert.Equal(8, registry.LongestSignatureLength);

        Assert.Throws<ArgumentException>(() => new FileSignatureRegistry([
            Descriptor("A", 1, []),
        ]));
        Assert.Throws<ArgumentException>(() => new FileSignatureRegistry([
            Descriptor("A", 1, [1, 2]),
            Descriptor("B", 1, [1, 2]),
        ]));
        var resolved = new FileSignatureRegistry([
            Descriptor("A", 1, [1, 2]),
            Descriptor("B", 2, [1, 2]),
        ]);
        Assert.Equal("B", resolved.Descriptors[0].FormatId);
        Assert.Throws<ArgumentException>(() => new FileSignatureRegistry([
            Descriptor("unsafe", 1, [4]) with { SuggestedExtension = ".\\bad" },
        ]));
    }

    [Fact]
    public async Task Scanner_DetectsAllSupportedCompleteFormatsWithHighConfidence()
    {
        var fixtures = new Dictionary<string, byte[]>
        {
            ["JPEG"] = CreateJpeg(),
            ["PNG"] = CreatePng(),
            ["GIF"] = CreateGif("GIF89a"),
            ["PDF"] = CreatePdf(),
            ["ZIP"] = CreateZip(("one.txt", "one"), ("two.txt", "two")),
        };

        foreach (var fixture in fixtures)
        {
            await using var source = new MemoryRandomAccessSource(fixture.Value);
            var result = await ScanAsync(source);
            var candidate = Assert.Single(result.Candidates.Where(item => item.FormatId == fixture.Key));
            Assert.Equal(DeepScanOutcome.Completed, result.Outcome);
            Assert.True(candidate.ValidationState == DeepScanValidationState.StructurallyValidated, $"{fixture.Key}: {candidate.ValidationState}; {candidate.Completeness}; {string.Join(',', candidate.DiagnosticCodes)}; {string.Join(',', candidate.DetectionEvidence)}");
            Assert.Equal(DeepScanCompleteness.Complete, candidate.Completeness);
            Assert.Equal(DeepScanConfidence.High, candidate.ValidationConfidence);
            Assert.Equal(fixture.Value.Length, candidate.LogicalLength);
            Assert.True(candidate.IsRecoverySupported);
        }
    }

    [Fact]
    public async Task ChunkBoundarySignature_IsDetectedExactlyOnce()
    {
        var png = CreatePng();
        var bytes = new byte[4092 + png.Length];
        png.CopyTo(bytes, 4092);
        await using var source = new MemoryRandomAccessSource(bytes);

        var result = await ScanAsync(source, new DeepScanBudget(ChunkSize: 4096));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(4092, candidate.StartOffset);
        Assert.Equal(1, result.Metrics.SignatureHits);
        Assert.InRange(result.Metrics.PeakScanBufferBytes, 4096, 4096 + FileSignatureRegistry.MaximumSignatureLength);
    }

    [Fact]
    public async Task ExplicitRanges_NormalizeAdjacencyAndDoNotScanOutside()
    {
        var first = CreatePng();
        var second = CreateGif("GIF87a");
        var bytes = new byte[4096];
        first.CopyTo(bytes, 100);
        second.CopyTo(bytes, 1000);
        await using var source = new MemoryRandomAccessSource(bytes);
        var provider = new ExplicitDeepScanRangeProvider([
            new(90, 200, "caller-a", DeepScanRangeKind.Explicit),
            new(290, 100, "caller-b", DeepScanRangeKind.Explicit),
        ]);

        var result = await new DeepScanEngine().ScanAsync(source, new(provider, new(ChunkSize: 4096)), null, CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Equal("PNG", result.Candidates[0].FormatId);
        Assert.Equal("explicit:00000", result.Candidates[0].SourceRangeIdentity);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new ExplicitDeepScanRangeProvider([new(-1, 1, "bad", DeepScanRangeKind.Explicit)])
                .GetRangesAsync(source, new(), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedAndTruncatedFormats_AreConservative()
    {
        var invalidPng = CreatePng();
        invalidPng[^1] ^= 0xFF;
        var truncatedJpeg = CreateJpeg()[..^2];
        var pdfNoise = Encoding.ASCII.GetBytes("%PDF-1.7\nthis is arbitrary text without a trailer");
        var input = Join(invalidPng, new byte[32], truncatedJpeg, new byte[32], pdfNoise);
        await using var source = new MemoryRandomAccessSource(input);

        var normal = await ScanAsync(source);
        Assert.DoesNotContain(normal.Candidates, item => item.FormatId == "PNG");
        Assert.Contains(normal.Candidates, item => item.FormatId == "JPEG" && item.Completeness == DeepScanCompleteness.Truncated);
        Assert.Contains(normal.Candidates, item => item.FormatId == "PDF" && item.ValidationConfidence == DeepScanConfidence.Low && item.LogicalLength is null);

        await using var retainedSource = new MemoryRandomAccessSource(input);
        var retained = await new DeepScanEngine().ScanAsync(retainedSource, new(new WholeImageDeepScanRangeProvider(), RetainInvalidCandidates: true), null, CancellationToken.None);
        Assert.Contains(retained.Candidates, item => item.FormatId == "PNG" && item.ValidationState == DeepScanValidationState.StructurallyInvalid);
        Assert.Contains(retained.Diagnostics, item => item.Code == "PNG_INVALID_CHUNK_OR_CRC");
    }

    [Fact]
    public async Task Jpeg_DoesNotTreatMetadataEoiOrStuffedEntropyAsTerminator()
    {
        var jpeg = CreateJpeg(includeFalseEoiInMetadata: true);
        await using var source = new MemoryRandomAccessSource(jpeg);
        var candidate = Assert.Single((await ScanAsync(source)).Candidates);
        Assert.True(candidate.LogicalLength == jpeg.Length, $"{candidate.ValidationState}; {candidate.Completeness}; {string.Join(',', candidate.DiagnosticCodes)}; {string.Join(',', candidate.DetectionEvidence)}");
        Assert.Equal(DeepScanConfidence.High, candidate.ValidationConfidence);
    }

    [Fact]
    public async Task ProgressiveJpegAndMultiplePngIdatChunksValidate()
    {
        var jpeg = CreateJpeg(progressive: true);
        var png = CreatePng(idatChunks: 2);
        await using var source = new MemoryRandomAccessSource(Join(jpeg, png));

        var result = await ScanAsync(source);

        Assert.Contains(result.Candidates, item => item.FormatId == "JPEG" && item.DetectionEvidence.Contains("Progressive SOF2"));
        Assert.Contains(result.Candidates, item => item.FormatId == "PNG" && item.ValidationState == DeepScanValidationState.StructurallyValidated);
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public async Task Gif_VersionsAndSubBlocksAreBounded(string version)
    {
        var gif = CreateGif(version, multipleSubBlocks: true);
        await using var source = new MemoryRandomAccessSource(gif);
        Assert.Equal(gif.Length, Assert.Single((await ScanAsync(source)).Candidates).LogicalLength);
        await using var truncated = new MemoryRandomAccessSource(gif[..^1]);
        Assert.Equal(DeepScanCompleteness.Truncated, Assert.Single((await ScanAsync(truncated)).Candidates).Completeness);
    }

    [Fact]
    public async Task Zip64Sentinel_IsUnsupportedAndNotClaimedComplete()
    {
        var zip = CreateZip(("a", "b"));
        var eocd = Find(zip, [0x50, 0x4B, 0x05, 0x06]);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(eocd + 10, 2), ushort.MaxValue);
        await using var source = new MemoryRandomAccessSource(zip);

        var candidate = Assert.Single((await ScanAsync(source)).Candidates);
        Assert.True(candidate.ValidationState == DeepScanValidationState.UnsupportedVariant, $"{candidate.ValidationState}; {candidate.Completeness}; {string.Join(',', candidate.DiagnosticCodes)}; {string.Join(',', candidate.DetectionEvidence)}");
        Assert.Contains("ZIP64_UNSUPPORTED", candidate.DiagnosticCodes);
    }

    [Fact]
    public async Task ZipDataDescriptorAndEncryptedMetadataRemainDetectableWithoutExtraction()
    {
        var descriptorZip = CreateZipWithDataDescriptor();
        var encryptedZip = CreateZip(("secret.txt", "opaque"));
        var central = Find(encryptedZip, [0x50, 0x4B, 0x01, 0x02]);
        BinaryPrimitives.WriteUInt16LittleEndian(encryptedZip.AsSpan(6, 2), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(encryptedZip.AsSpan(6, 2)) | 1));
        BinaryPrimitives.WriteUInt16LittleEndian(encryptedZip.AsSpan(central + 8, 2), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(encryptedZip.AsSpan(central + 8, 2)) | 1));

        await using var descriptorSource = new MemoryRandomAccessSource(descriptorZip);
        await using var encryptedSource = new MemoryRandomAccessSource(encryptedZip);
        var descriptorCandidate = Assert.Single((await ScanAsync(descriptorSource)).Candidates);
        var encryptedCandidate = Assert.Single((await ScanAsync(encryptedSource)).Candidates);

        Assert.Contains("Data-descriptor entry metadata present", descriptorCandidate.DetectionEvidence);
        Assert.Contains("Encrypted entry metadata present", encryptedCandidate.DetectionEvidence);
        Assert.Equal(DeepScanConfidence.High, encryptedCandidate.ValidationConfidence);
    }

    [Fact]
    public async Task PdfIncrementalUpdateUsesLastPlausibleEof()
    {
        var first = CreatePdf();
        var prefix = Encoding.ASCII.GetString(first) + "\n2 0 obj\n<<>>\nendobj\n";
        var xref = Encoding.ASCII.GetByteCount(prefix);
        var incremental = Encoding.ASCII.GetBytes(prefix + "xref\n1 1\n0000000000 00000 n \ntrailer\n<< /Size 2 >>\nstartxref\n" + xref + "\n%%EOF");
        await using var source = new MemoryRandomAccessSource(incremental);

        var candidate = Assert.Single((await ScanAsync(source)).Candidates);

        Assert.Equal(incremental.Length, candidate.LogicalLength);
        Assert.Equal(DeepScanConfidence.High, candidate.ValidationConfidence);
    }

    [Fact]
    public async Task RandomZeroAndRepeatedPrefixes_DoNotCreateMediumOrHighCandidates()
    {
        var random = new byte[1024 * 1024];
        new Random(12345).NextBytes(random);
        Array.Clear(random, 0, 4096);
        for (var index = 8192; index < 16384; index += 3)
        {
            random[index] = 0xFF;
            random[index + 1] = 0xD8;
            random[index + 2] = 0xFF;
        }

        await using var source = new MemoryRandomAccessSource(random);
        var result = await ScanAsync(source, new(MaximumSignatureHits: 10_000, MaximumCandidatesValidated: 10_000, MaximumCandidatesReturned: 10_000));
        Assert.DoesNotContain(result.Candidates, item => item.ValidationConfidence is DeepScanConfidence.Medium or DeepScanConfidence.High);
    }

    [Fact]
    public async Task ScanBudgetsReturnExplicitPartialResult()
    {
        var png = CreatePng();
        var input = Join(png, png, png);
        await using var source = new MemoryRandomAccessSource(input);
        var budget = new DeepScanBudget(MaximumSignatureHits: 1, MaximumCandidatesValidated: 10, MaximumCandidatesReturned: 10);

        var result = await ScanAsync(source, budget);

        Assert.Equal(DeepScanOutcome.Partial, result.Outcome);
        Assert.True(result.IsBudgetLimited);
        Assert.Equal("MaximumSignatureHits", result.PartialReason);
        Assert.Contains(result.Diagnostics, item => item.Code == "SIGNATURE_BUDGET_REACHED");
    }

    [Fact]
    public async Task ValidatorBudgetProducesBudgetLimitedCandidate()
    {
        var png = CreatePng(idatLength: 20_000);
        await using var source = new MemoryRandomAccessSource(png);
        var budget = new DeepScanBudget(MaximumBytesInspectedPerCandidate: 4096, MaximumTerminatorSearchDistance: 4096);

        var candidate = Assert.Single((await ScanAsync(source, budget)).Candidates);

        Assert.Equal(DeepScanCompleteness.BudgetLimited, candidate.Completeness);
        Assert.Equal(DeepScanConfidence.Low, candidate.ValidationConfidence);
    }

    [Fact]
    public async Task HugeDeclaredLengthAndSourceShortReadAreBounded()
    {
        var png = CreatePng();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33, 4), uint.MaxValue);
        await using var source = new MemoryRandomAccessSource(png);
        var retained = await new DeepScanEngine().ScanAsync(source, new(new WholeImageDeepScanRangeProvider(), RetainInvalidCandidates: true), null, CancellationToken.None);
        Assert.Equal(DeepScanValidationState.StructurallyInvalid, Assert.Single(retained.Candidates).ValidationState);

        await using var shortSource = new ShortReadSource(new byte[8192]);
        var shortResult = await ScanAsync(shortSource);
        Assert.Equal(DeepScanOutcome.Partial, shortResult.Outcome);
        Assert.Contains(shortResult.Diagnostics, item => item.Code == "SOURCE_SHORT_READ");
    }

    [Fact]
    public async Task PartiallyOverlappingValidatedCandidatesRemainWithWarnings()
    {
        var registry = new FileSignatureRegistry([
            Descriptor("OUTER", 2, [1, 2], new FixedValidator(20)),
            Descriptor("INNER", 1, [3, 4], new FixedValidator(20)),
        ]);
        var bytes = new byte[40];
        bytes[0] = 1; bytes[1] = 2;
        bytes[10] = 3; bytes[11] = 4;
        await using var source = new MemoryRandomAccessSource(bytes);

        var result = await new DeepScanEngine(registry).ScanAsync(source, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, item => Assert.True(item.HasOverlapWarning));
        Assert.Contains(result.Diagnostics, item => item.Code == "CANDIDATE_OVERLAP");
    }

    [Fact]
    public async Task StableIdsOrderingOverlapAndAdjacentCandidatesAreDeterministic()
    {
        var input = Join(CreatePng(), CreateGif("GIF89a"), CreateZip(("x", "y")));
        await using var firstSource = new MemoryRandomAccessSource(input);
        await using var secondSource = new MemoryRandomAccessSource(input);
        var first = await ScanAsync(firstSource);
        var second = await ScanAsync(secondSource);

        Assert.Equal(first.Candidates.Select(item => item.CandidateId), second.Candidates.Select(item => item.CandidateId));
        Assert.Equal(first.Candidates.OrderBy(item => item.StartOffset).Select(item => item.StartOffset), first.Candidates.Select(item => item.StartOffset));
        Assert.Equal(3, first.Candidates.Count);
    }

    [Fact]
    public async Task ProgressIsMonotonicAndTerminalPublishedOnce()
    {
        var bytes = new byte[64 * 1024];
        CreatePng().CopyTo(bytes, 4090);
        await using var source = new MemoryRandomAccessSource(bytes);
        var reports = new List<DeepScanProgress>();
        var progress = new InlineProgress<DeepScanProgress>(reports.Add);

        var result = await new DeepScanEngine().ScanAsync(source, new(new WholeImageDeepScanRangeProvider(), new(ChunkSize: 4096, MinimumProgressInterval: TimeSpan.Zero)), progress, CancellationToken.None);

        Assert.Equal(DeepScanOutcome.Completed, result.Outcome);
        Assert.Equal(reports.OrderBy(item => item.BytesExamined).Select(item => item.BytesExamined), reports.Select(item => item.BytesExamined));
        Assert.Single(reports.Where(item => item.Phase == DeepScanPhase.Completed));
        Assert.Equal(100d, reports[^1].Percentage);
    }

    [Fact]
    public async Task CancellationDuringScanningReturnsPartialCandidatesAndNeverCompleted()
    {
        var bytes = new byte[256 * 1024];
        CreatePng().CopyTo(bytes, 0);
        using var cancellation = new CancellationTokenSource();
        await using var inner = new MemoryRandomAccessSource(bytes);
        await using var source = new CancelAfterReadsSource(inner, cancellation, 3);
        var reports = new List<DeepScanProgress>();

        var result = await new DeepScanEngine().ScanAsync(source, new(new WholeImageDeepScanRangeProvider(), new(ChunkSize: 4096, MinimumProgressInterval: TimeSpan.Zero)), new InlineProgress<DeepScanProgress>(reports.Add), cancellation.Token);

        Assert.Equal(DeepScanOutcome.Canceled, result.Outcome);
        Assert.DoesNotContain(reports, item => item.Phase == DeepScanPhase.Completed);
        Assert.Contains(result.Diagnostics, item => item.Code == "CANCELLATION");
    }

    [Fact]
    public async Task CancellationDuringValidationWinsBeforeCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var registry = new FileSignatureRegistry([
            Descriptor("CANCEL", 1, [9, 9], new CancelingValidator(cancellation)),
        ]);
        await using var source = new MemoryRandomAccessSource([9, 9, 0, 0]);
        var reports = new List<DeepScanProgress>();

        var result = await new DeepScanEngine(registry).ScanAsync(source, new(new WholeImageDeepScanRangeProvider(), new(MinimumProgressInterval: TimeSpan.Zero)), new InlineProgress<DeepScanProgress>(reports.Add), cancellation.Token);

        Assert.Equal(DeepScanOutcome.Canceled, result.Outcome);
        Assert.DoesNotContain(reports, item => item.Phase == DeepScanPhase.Completed);
    }

    [Fact]
    public async Task HeadlessImageServiceUsesRegularFileBoundaryFingerprintAndWritesNothing()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "fixture.img");
        await File.WriteAllBytesAsync(path, Join(new byte[100], CreatePng()));
        var before = new FileInfo(path);
        var factory = new RegularFileRandomAccessSourceFactory();
        var service = new ImageDeepScanService(factory, new DeepScanEngine(), new DeepScanSourceMetadataProvider(factory));

        var session = await service.ScanAsync(path, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        var retained = await service.GetSessionAsync(session.SessionId, CancellationToken.None);

        Assert.Single(retained.Result.Candidates);
        Assert.Equal(before.Length, new FileInfo(path).Length);
        Assert.Equal(before.LastWriteTimeUtc, new FileInfo(path).LastWriteTimeUtc);
        Assert.Single(Directory.GetFiles(directory));
        Console.WriteLine($"SOURCE_PROOF sha256={session.Source.Sha256} length={session.Source.Length} lastWriteUtcTicks={session.Source.LastWriteTimeUtcTicks}");
        Assert.True(service.DisposeSession(session.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSessionAsync(session.SessionId, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ScanAsync("\\\\.\\PhysicalDrive0", new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None));
    }

    [Fact]
    public async Task HeadlessSessionRejectsChangedSourceFingerprint()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "mutable.img");
        await File.WriteAllBytesAsync(path, CreatePng());
        var factory = new RegularFileRandomAccessSourceFactory();
        var service = new ImageDeepScanService(factory, new DeepScanEngine(), new DeepScanSourceMetadataProvider(factory));
        var session = await service.ScanAsync(path, new(new WholeImageDeepScanRangeProvider()), null, CancellationToken.None);
        await using (var mutation = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await mutation.WriteAsync(new byte[] { 1 });
        }

        var exception = await Assert.ThrowsAsync<IOException>(() => service.GetSessionAsync(session.SessionId, CancellationToken.None));

        Assert.Contains("SOURCE_CHANGED", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSessionAsync(session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task NtfsUnallocatedRangesExposeFreeClustersAndExcludeAllocatedLookalikes()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        var bitmap = new byte[16];
        SetBit(bitmap, 51);
        fixture.WriteClusterPayload(40, bitmap);
        fixture.AddRecord(6, 2, true, false,
            fixture.FileName(5, 1, "$Bitmap", 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), bitmap.Length, fixture.ClusterSize, bitmap.Length, highestVcn: 0));
        fixture.WriteClusterPayload(50, CreatePng());
        fixture.WriteClusterPayload(51, CreateGif("GIF89a"));
        await using var source = new MemoryRandomAccessSource(fixture.Build());
        var provider = new NtfsUnallocatedDeepScanRangeProvider();

        var result = await new DeepScanEngine().ScanAsync(source, new(provider, new(ChunkSize: 4096, MaximumSourceBytesScanned: source.Length)), null, CancellationToken.None);

        Assert.Contains(result.Candidates, item => item.FormatId == "PNG" && item.RangeKind == DeepScanRangeKind.NtfsUnallocated);
        Assert.DoesNotContain(result.Candidates, item => item.FormatId == "GIF");
    }

    [Fact]
    public async Task ShortNtfsBitmapNeverTreatsUnknownCoverageAsFree()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.WriteClusterPayload(40, [0]);
        fixture.AddRecord(6, 2, true, false,
            fixture.FileName(5, 1, "$Bitmap", 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), 1, fixture.ClusterSize, 1, highestVcn: 0));
        fixture.WriteClusterPayload(50, CreatePng());
        await using var source = new MemoryRandomAccessSource(fixture.Build());

        var rangeResult = await new NtfsUnallocatedDeepScanRangeProvider().GetRangesAsync(source, new(MaximumSourceBytesScanned: source.Length), CancellationToken.None);

        Assert.False(rangeResult.IsComplete);
        Assert.All(rangeResult.Ranges, range => Assert.True(range.End <= 8L * fixture.ClusterSize));
        Assert.Contains(rangeResult.Diagnostics, item => item.Code == "NTFS_BITMAP_INCOMPLETE");
    }

    [Fact]
    public async Task LargerSyntheticScanReportsBoundedStrategyAndFindings()
    {
        const int length = 16 * 1024 * 1024;
        var bytes = new byte[length];
        new Random(77).NextBytes(bytes);
        CreatePng().CopyTo(bytes, 8 * 1024 * 1024 - 3);
        await using var source = new MemoryRandomAccessSource(bytes);

        var result = await ScanAsync(source);

        Assert.Equal(length, result.Metrics.BytesExamined);
        Assert.InRange(result.Metrics.PeakScanBufferBytes, 4096, 4 * 1024 * 1024 + 64);
        Assert.Contains(result.Candidates, item => item.FormatId == "PNG");
        Console.WriteLine($"SYNTHETIC_SCAN bytes={result.Metrics.BytesExamined} runtimeMs={result.Metrics.Elapsed.TotalMilliseconds:F3} peakScanBuffer={result.Metrics.PeakScanBufferBytes} hits={result.Metrics.SignatureHits} accepted={result.Metrics.CandidatesAccepted}");
    }

    private static async Task<DeepScanResult> ScanAsync(IReadOnlyRandomAccessSource source, DeepScanBudget? budget = null) =>
        await new DeepScanEngine().ScanAsync(source, new(new WholeImageDeepScanRangeProvider(), budget), null, CancellationToken.None);

    private static FileSignatureDescriptor Descriptor(string id, int priority, byte[] signature, ICarvedCandidateValidator? validator = null) =>
        new(id, ".x", "test", priority, 1, 1024, [new(signature)], [], [], validator ?? new AlwaysInvalidValidator(), "1");

    private static byte[] CreateJpeg(bool includeFalseEoiInMetadata = false, bool progressive = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4 };
        bytes.AddRange(includeFalseEoiInMetadata ? [0xFF, 0xD9] : [0, 0]);
        bytes.AddRange([0xFF, progressive ? (byte)0xC2 : (byte)0xC0, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0]);
        bytes.AddRange([0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 63, 0]);
        bytes.AddRange([0x11, 0xFF, 0, 0x22, 0xFF, 0xD0, 0x33, 0xFF, 0xD9]);
        return bytes.ToArray();
    }

    private static byte[] CreatePng(int idatLength = 0, int idatChunks = 1)
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WritePngChunk(stream, "IHDR", ihdr);
        for (var index = 0; index < idatChunks; index++) WritePngChunk(stream, "IDAT", new byte[idatLength]);
        WritePngChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = TestCrc32(typeBytes.Concat(data).ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        stream.Write(length);
    }

    private static uint TestCrc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFU;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320U & (uint)-(int)(crc & 1));
        }

        return ~crc;
    }

    private static byte[] CreateGif(string version, bool multipleSubBlocks = false)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(version));
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0]);
        bytes.AddRange([0, 0, 0, 255, 255, 255]);
        bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2]);
        bytes.AddRange(multipleSubBlocks ? [1, 0x44, 1, 0x01, 0] : [2, 0x44, 0x01, 0]);
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    private static byte[] CreatePdf()
    {
        var prefix = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\n";
        var xref = Encoding.ASCII.GetByteCount(prefix);
        return Encoding.ASCII.GetBytes(prefix + "xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n" + xref + "\n%%EOF");
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(item.Content);
            }
        }

        return memory.ToArray();
    }

    private static byte[] CreateZipWithDataDescriptor()
    {
        using var memory = new MemoryStream();
        using (var wrapper = new NonSeekableWriteStream(memory))
        using (var archive = new ZipArchive(wrapper, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("descriptor.txt", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, 1024, false);
            writer.Write("descriptor");
        }

        return memory.ToArray();
    }

    private static byte[] Join(params byte[][] values)
    {
        var result = new byte[values.Sum(value => value.Length)];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }

    private static int Find(byte[] bytes, byte[] pattern)
    {
        for (var index = 0; index <= bytes.Length - pattern.Length; index++)
        {
            if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) return index;
        }

        return -1;
    }

    private static void SetBit(byte[] bitmap, int cluster) => bitmap[cluster / 8] |= (byte)(1 << (cluster & 7));

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DataRecoveryStudioTests", "Phase6A", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AlwaysInvalidValidator : ICarvedCandidateValidator
    {
        public ValueTask<CandidateValidationResult> ValidateAsync(IReadOnlyRandomAccessSource source, CandidateValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ValidationResultsForTest());

        private static CandidateValidationResult ValidationResultsForTest() =>
            new(DeepScanValidationState.StructurallyInvalid, DeepScanCompleteness.Corrupt, DeepScanConfidence.Low, null, DeepScanConfidence.Low, [], [], 0);
    }

    private sealed class FixedValidator(long length) : ICarvedCandidateValidator
    {
        public ValueTask<CandidateValidationResult> ValidateAsync(IReadOnlyRandomAccessSource source, CandidateValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CandidateValidationResult(DeepScanValidationState.StructurallyValidated, DeepScanCompleteness.Complete, DeepScanConfidence.High, length, DeepScanConfidence.High, ["fixed"], [], 0));
    }

    private sealed class CancelingValidator(CancellationTokenSource cancellation) : ICarvedCandidateValidator
    {
        public ValueTask<CandidateValidationResult> ValidateAsync(IReadOnlyRandomAccessSource source, CandidateValidationContext context, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CancelAfterReadsSource(IReadOnlyRandomAccessSource inner, CancellationTokenSource cancellation, int cancelAtRead) : IReadOnlyRandomAccessSource
    {
        private int _reads;
        public long Length => inner.Length;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            await inner.ReadExactlyAsync(offset, destination, cancellationToken);
            if (Interlocked.Increment(ref _reads) == cancelAtRead) cancellation.Cancel();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShortReadSource(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        public long Length => bytes.Length;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
            ValueTask.FromException(new EndOfStreamException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }
}
