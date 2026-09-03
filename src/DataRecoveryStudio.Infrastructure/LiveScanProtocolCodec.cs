using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed record LiveScanMessageEnvelope(
    int ProtocolVersion,
    Guid SessionId,
    LiveScanMessageKind Kind,
    JsonElement Payload);

public sealed class LiveScanProtocolException(string message) : IOException(message)
{
}

public static class LiveScanProtocolCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = LiveScanProtocol.MaximumSerializerDepth,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        Guid sessionId,
        LiveScanMessageKind kind,
        T payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (sessionId == Guid.Empty || !Enum.IsDefined(kind)) throw new LiveScanProtocolException("The message envelope is invalid.");
        var envelope = new LiveScanMessageEnvelope(
            LiveScanProtocol.Version,
            sessionId,
            kind,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > LiveScanProtocol.MaximumMessageBytes)
        {
            throw new LiveScanProtocolException("The message exceeds the protocol size limit.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<LiveScanMessageEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > LiveScanProtocol.MaximumMessageBytes)
        {
            throw new LiveScanProtocolException("The incoming message length is invalid.");
        }

        var bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        LiveScanMessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LiveScanMessageEnvelope>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new LiveScanProtocolException($"Malformed protocol JSON: {exception.Message}");
        }

        if (envelope is null || envelope.ProtocolVersion <= 0 || envelope.SessionId == Guid.Empty ||
            !Enum.IsDefined(envelope.Kind) || envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new LiveScanProtocolException("The incoming message envelope is invalid.");
        }

        return envelope;
    }

    public static T ReadPayload<T>(LiveScanMessageEnvelope envelope)
    {
        try
        {
            return envelope.Payload.Deserialize<T>(JsonOptions) ??
                throw new LiveScanProtocolException("The message payload is missing.");
        }
        catch (JsonException exception)
        {
            throw new LiveScanProtocolException($"The message payload is malformed: {exception.Message}");
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The peer disconnected during a protocol message.");
            read = checked(read + count);
        }
    }
}

internal sealed class LiveScanResultAccumulator(
    Guid sessionId,
    IProgress<LiveScanProgressDto>? progress,
    LiveScanScannerKind scannerKind = LiveScanScannerKind.NtfsStandardMetadata)
{
    private readonly List<LiveScanCandidateDto> _candidates = [];
    private readonly HashSet<Guid> _candidateIds = [];
    private readonly List<LiveScanDiagnosticDto> _diagnostics = [];
    private int _candidateSequence;
    private int _diagnosticSequence;
    private long _records;
    private long _bytes;
    private int _found;
    private int _directories;
    private int _directoryEntries;
    private int _fatEntries;
    private bool _terminal;

    public void Accept(LiveScanMessageEnvelope envelope)
    {
        if (_terminal) throw new LiveScanProtocolException("A message followed the terminal result.");
        switch (envelope.Kind)
        {
            case LiveScanMessageKind.Progress:
                var update = LiveScanProtocolCodec.ReadPayload<LiveScanProgressDto>(envelope);
                ValidateProgress(update);
                progress?.Report(update);
                break;
            case LiveScanMessageKind.CandidateBatch:
                var candidates = LiveScanProtocolCodec.ReadPayload<LiveScanCandidateBatchDto>(envelope);
                if (candidates.Sequence != _candidateSequence++ || candidates.Candidates.Count > LiveScanProtocol.MaximumCandidateBatchSize ||
                    _candidates.Count + candidates.Candidates.Count > LiveScanProtocol.MaximumCandidates)
                {
                    throw new LiveScanProtocolException("The candidate stream exceeded its sequence or count bound.");
                }

                foreach (var candidate in candidates.Candidates)
                {
                    ValidateCandidate(candidate);
                    if (!_candidateIds.Add(candidate.CandidateId))
                        throw new LiveScanProtocolException("The candidate stream contains a duplicate identity.");
                }
                _candidates.AddRange(candidates.Candidates);
                break;
            case LiveScanMessageKind.DiagnosticBatch:
                var diagnostics = LiveScanProtocolCodec.ReadPayload<LiveScanDiagnosticBatchDto>(envelope);
                if (diagnostics.Sequence != _diagnosticSequence++ || diagnostics.Diagnostics.Count > LiveScanProtocol.MaximumDiagnosticBatchSize ||
                    _diagnostics.Count + diagnostics.Diagnostics.Count > LiveScanProtocol.MaximumDiagnostics)
                {
                    throw new LiveScanProtocolException("The diagnostic stream exceeded its sequence or count bound.");
                }

                foreach (var diagnostic in diagnostics.Diagnostics)
                {
                    ValidateString(diagnostic.Code, 128);
                    ValidateString(diagnostic.Operation, 128);
                    if (!Enum.IsDefined(diagnostic.Severity)) throw new LiveScanProtocolException("A diagnostic enum is invalid.");
                }

                _diagnostics.AddRange(diagnostics.Diagnostics);
                break;
            case LiveScanMessageKind.TerminalResult:
                throw new InvalidOperationException("Use Complete for the terminal message.");
            default:
                throw new LiveScanProtocolException("The worker sent a command that is not valid after handshake.");
        }
    }

    public LiveScanResult Complete(LiveScanMessageEnvelope envelope)
    {
        if (_terminal || envelope.Kind != LiveScanMessageKind.TerminalResult)
        {
            throw new LiveScanProtocolException("Exactly one terminal result is required.");
        }

        var terminal = LiveScanProtocolCodec.ReadPayload<LiveScanTerminalResultDto>(envelope);
        if (!Enum.IsDefined(terminal.Status) || !Enum.IsDefined(terminal.Consistency) || terminal.ScannerKind != scannerKind ||
            !terminal.FileSystem.Equals(ExpectedFileSystem(scannerKind), StringComparison.OrdinalIgnoreCase) ||
            terminal.CandidateCount != _candidates.Count || terminal.DiagnosticCount != _diagnostics.Count ||
            terminal.RecordsProcessed < 0 || terminal.BytesRead < 0 || terminal.DirectoriesExamined < 0 ||
            terminal.DirectoryEntriesExamined < 0 || terminal.FatEntriesInspected < 0)
        {
            throw new LiveScanProtocolException("The terminal result does not match the bounded result stream.");
        }

        if (terminal.ReasonCode is not null) ValidateString(terminal.ReasonCode, 128);
        if (!Enum.IsDefined(terminal.Fat32BootRelationship) || terminal.Fat32FatCount is < 0 or > 4 ||
            terminal.Fat32ActiveFatIndex is < 0 or > 3)
            throw new LiveScanProtocolException("The terminal FAT32 evidence is invalid.");
        if (terminal.ConsistencyEvidenceBefore is not null) ValidateEvidenceHash(terminal.ConsistencyEvidenceBefore);
        if (terminal.ConsistencyEvidenceAfter is not null) ValidateEvidenceHash(terminal.ConsistencyEvidenceAfter);
        ValidateOptionalEvidence(terminal.Fat32GeometryEvidenceBefore, terminal.Fat32GeometryEvidenceAfter);
        ValidateOptionalEvidence(terminal.Fat32BootEvidenceBefore, terminal.Fat32BootEvidenceAfter);
        ValidateOptionalEvidence(terminal.Fat32SelectedFatEvidenceBefore, terminal.Fat32SelectedFatEvidenceAfter);
        ValidateOptionalEvidence(terminal.Fat32RootChainEvidenceBefore, terminal.Fat32RootChainEvidenceAfter);
        if (terminal.Fat32BootRelationshipAfter is not null && !Enum.IsDefined(terminal.Fat32BootRelationshipAfter.Value))
            throw new LiveScanProtocolException("The FAT32 after-scan boot relationship is invalid.");
        if (scannerKind == LiveScanScannerKind.Fat32StandardMetadata &&
            !string.Equals(terminal.Fat32ScannerVersion, Fat32ScannerVersions.MetadataPhase7A, StringComparison.Ordinal))
            throw new LiveScanProtocolException("The FAT32 scanner version is invalid.");
        if (scannerKind == LiveScanScannerKind.NtfsStandardMetadata &&
            (terminal.Fat32GeometryValidated || terminal.Fat32BootRelationship != Fat32BootRelationship.Unknown ||
             terminal.Fat32MirroringEnabled is not null || terminal.Fat32FatCount is not null ||
             terminal.Fat32ActiveFatIndex is not null || terminal.Fat32RootDirectoryCluster is not null ||
             terminal.ConsistencyEvidenceBefore is not null || terminal.ConsistencyEvidenceAfter is not null ||
             terminal.Fat32ScannerVersion is not null || terminal.Fat32BootRelationshipAfter is not null ||
             terminal.Fat32GeometryEvidenceBefore is not null || terminal.Fat32GeometryEvidenceAfter is not null ||
             terminal.Fat32BootEvidenceBefore is not null || terminal.Fat32BootEvidenceAfter is not null ||
             terminal.Fat32SelectedFatEvidenceBefore is not null || terminal.Fat32SelectedFatEvidenceAfter is not null ||
             terminal.Fat32RootChainEvidenceBefore is not null || terminal.Fat32RootChainEvidenceAfter is not null))
            throw new LiveScanProtocolException("An NTFS terminal contains FAT32-only evidence.");
        _terminal = true;
        return new(sessionId, terminal, _candidates.ToArray(), _diagnostics.ToArray());
    }

    private void ValidateProgress(LiveScanProgressDto value)
    {
        if (value.ScannerKind != scannerKind || value.RecordsProcessed < _records || value.BytesRead < _bytes ||
            value.CandidatesFound < _found || value.DirectoriesExamined < _directories ||
            value.DirectoryEntriesExamined < _directoryEntries || value.FatEntriesInspected < _fatEntries ||
            value.TotalRecords < 0 || (value.TotalRecords > 0 && value.TotalRecords < value.RecordsProcessed))
        {
            throw new LiveScanProtocolException("Worker progress is not monotonic.");
        }

        ValidateString(value.Phase, 256);
        _records = value.RecordsProcessed;
        _bytes = value.BytesRead;
        _found = value.CandidatesFound;
        _directories = value.DirectoriesExamined;
        _directoryEntries = value.DirectoryEntriesExamined;
        _fatEntries = value.FatEntriesInspected;
    }

    private void ValidateCandidate(LiveScanCandidateDto candidate)
    {
        if (candidate.CandidateId == Guid.Empty || candidate.SourceSessionId != sessionId || candidate.ScannerKind != scannerKind ||
            !candidate.FileSystem.Equals(ExpectedFileSystem(scannerKind), StringComparison.OrdinalIgnoreCase) || candidate.MftRecordNumber < 0 ||
            candidate.LogicalSize < 0 || candidate.Streams.Count > 128 || candidate.DiagnosticCodes.Count > 128 ||
            !Enum.IsDefined(candidate.Category) || !Enum.IsDefined(candidate.PathState) || !Enum.IsDefined(candidate.Recoverability))
        {
            throw new LiveScanProtocolException("A candidate is outside the normalized result bounds.");
        }

        if (scannerKind == LiveScanScannerKind.Fat32StandardMetadata)
        {
            if (candidate.MftRecordNumber != 0 || candidate.SequenceNumber != 0 || candidate.Streams.Count != 0 ||
                candidate.Fat32Kind is null || candidate.Fat32NameState is null || candidate.Fat32PathState is null || candidate.Fat32Allocation is null ||
                !Enum.IsDefined(candidate.Fat32Kind.Value) || !Enum.IsDefined(candidate.Fat32NameState.Value) ||
                !Enum.IsDefined(candidate.Fat32PathState.Value) || !Enum.IsDefined(candidate.Fat32Allocation.Value))
                throw new LiveScanProtocolException("A FAT32 candidate contains missing or cross-filesystem fields.");
        }
        else if (candidate.Fat32Kind is not null || candidate.Fat32NameState is not null || candidate.Fat32PathState is not null ||
                 candidate.Fat32Allocation is not null || candidate.CreatedAt is not null || candidate.LastAccessedAt is not null ||
                 candidate.AttributeFlags != 0)
        {
            throw new LiveScanProtocolException("An NTFS candidate contains FAT32-only fields.");
        }

        ValidateString(candidate.Name, 255);
        ValidateString(candidate.OriginalPath, LiveScanProtocol.MaximumStringCharacters);
        foreach (var stream in candidate.Streams)
        {
            ValidateString(stream.Name, 255);
            if (stream.LogicalSize < 0 || !Enum.IsDefined(stream.Storage) || !Enum.IsDefined(stream.AllocationState))
                throw new LiveScanProtocolException("A stream summary is invalid.");
        }

        foreach (var code in candidate.DiagnosticCodes) ValidateString(code, 128);
    }

    private static string ExpectedFileSystem(LiveScanScannerKind value) => value switch
    {
        LiveScanScannerKind.NtfsStandardMetadata => "NTFS",
        LiveScanScannerKind.Fat32StandardMetadata => "FAT32",
        _ => throw new LiveScanProtocolException("The scanner kind is invalid."),
    };

    private static void ValidateString(string value, int maximum)
    {
        if (value is null || value.Length > maximum || value.Any(character => character == '\0' || char.IsControl(character)))
        {
            throw new LiveScanProtocolException("A protocol string is invalid or exceeds its bound.");
        }
    }

    private static void ValidateEvidenceHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new LiveScanProtocolException("A consistency evidence hash is invalid.");
    }

    private static void ValidateOptionalEvidence(string? before, string? after)
    {
        if (before is not null) ValidateEvidenceHash(before);
        if (after is not null) ValidateEvidenceHash(after);
    }
}
