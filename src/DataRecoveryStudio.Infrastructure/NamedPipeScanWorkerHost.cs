using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading.Channels;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed record ScanWorkerArguments(string PipeName, Guid SessionId, string Nonce, int ProtocolVersion)
{
    public static ScanWorkerArguments Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 8) throw new ArgumentException("The worker requires exactly four named arguments.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (!values.TryAdd(arguments[index], arguments[index + 1])) throw new ArgumentException("A worker argument was duplicated.");
        }

        if (!values.TryGetValue("--pipe", out var pipe) ||
            !values.TryGetValue("--session", out var sessionText) ||
            !values.TryGetValue("--nonce", out var nonce) ||
            !values.TryGetValue("--protocol", out var protocolText) ||
            values.Count != 4 ||
            pipe.Length > 128 || !pipe.StartsWith("DataRecoveryStudio.LiveScan.", StringComparison.Ordinal) ||
            pipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.') ||
            !Guid.TryParseExact(sessionText, "D", out var session) || session == Guid.Empty ||
            nonce.Length != 64 || nonce.Any(character => !Uri.IsHexDigit(character)) ||
            !int.TryParse(protocolText, System.Globalization.CultureInfo.InvariantCulture, out var protocol))
        {
            throw new ArgumentException("The worker arguments are malformed.");
        }

        return new(pipe, session, nonce.ToUpperInvariant(), protocol);
    }
}

public sealed class NamedPipeScanWorkerHost(ILiveScanExecutor executor)
{
    public async Task<int> RunAsync(ScanWorkerArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.ProtocolVersion != LiveScanProtocol.Version) return 20;
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(
            ".",
            arguments.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
            await LiveScanProtocolCodec.WriteAsync(
                pipe,
                arguments.SessionId,
                LiveScanMessageKind.HandshakeHello,
                new LiveScanHandshakeHello(LiveScanProtocol.Version, LiveScanProtocol.WorkerVersion, arguments.Nonce),
                connectionTimeout.Token).ConfigureAwait(false);
            var acceptedEnvelope = await LiveScanProtocolCodec.ReadAsync(pipe, connectionTimeout.Token).ConfigureAwait(false);
            if (acceptedEnvelope.SessionId != arguments.SessionId || acceptedEnvelope.Kind != LiveScanMessageKind.HandshakeAccepted ||
                acceptedEnvelope.ProtocolVersion != LiveScanProtocol.Version ||
                LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeAccepted>(acceptedEnvelope).ProtocolVersion != LiveScanProtocol.Version)
            {
                throw new LiveScanProtocolException("The parent did not accept the authenticated handshake.");
            }

            var startEnvelope = await LiveScanProtocolCodec.ReadAsync(pipe, connectionTimeout.Token).ConfigureAwait(false);
            if (startEnvelope.SessionId != arguments.SessionId || startEnvelope.Kind != LiveScanMessageKind.StartScan ||
                startEnvelope.ProtocolVersion != LiveScanProtocol.Version)
            {
                throw new LiveScanProtocolException("The first post-handshake command must start one scan.");
            }

            var start = LiveScanProtocolCodec.ReadPayload<LiveScanStartRequest>(startEnvelope);
            ValidateStart(start, arguments);
            return await ExecuteOneScanAsync(pipe, arguments.SessionId, start, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 21;
        }
        catch (Exception exception) when (exception is IOException or LiveScanProtocolException or ArgumentException)
        {
            return 22;
        }
    }

    private async Task<int> ExecuteOneScanAsync(
        NamedPipeClientStream pipe,
        Guid sessionId,
        LiveScanStartRequest start,
        CancellationToken outerCancellation)
    {
        var budgets = LiveScanHardLimits.Clamp(start.Budgets);
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        scanCancellation.CancelAfter(budgets.EffectiveMaximumDuration);
        using var listenerStop = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        var cancelReceived = 0;
        var listener = ListenForCancellationAsync(pipe, sessionId, scanCancellation, listenerStop.Token, () => Interlocked.Exchange(ref cancelReceived, 1));
        var progressChannel = Channel.CreateBounded<LiveScanProgressDto>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var progressWriter = WriteProgressAsync(pipe, sessionId, progressChannel.Reader, CancellationToken.None);
        LiveScanExecutionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(
                sessionId,
                start.Grant,
                budgets,
                new ChannelProgress(progressChannel.Writer),
                scanCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var status = Volatile.Read(ref cancelReceived) != 0 || outerCancellation.IsCancellationRequested
                ? LiveScanTerminalStatus.Canceled
                : LiveScanTerminalStatus.TimedOut;
            execution = Failure(status, status == LiveScanTerminalStatus.Canceled ? "Canceled" : "WorkerDurationLimit");
        }
        catch (LiveScanTargetValidationException exception)
        {
            execution = Failure(exception.Status, exception.ReasonCode);
        }
        catch (LiveSourceRemovedException)
        {
            execution = Failure(LiveScanTerminalStatus.SourceRemoved, "SourceRemoved");
        }
        catch (UnauthorizedAccessException)
        {
            execution = Failure(LiveScanTerminalStatus.AccessDenied, "AccessDenied");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            execution = Failure(LiveScanTerminalStatus.AccessDenied, "AccessDenied");
        }
        catch
        {
            execution = Failure(LiveScanTerminalStatus.Failed, "UnexpectedWorkerFailure");
        }

        progressChannel.Writer.TryComplete();
        try
        {
            await progressWriter.ConfigureAwait(false);
            var candidateSequence = 0;
            foreach (var batch in execution.Candidates.Chunk(budgets.CandidateBatchSize))
            {
                await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.CandidateBatch,
                    new LiveScanCandidateBatchDto(candidateSequence++, batch), scanCancellation.Token).ConfigureAwait(false);
            }

            var diagnosticSequence = 0;
            foreach (var batch in execution.Diagnostics.Chunk(LiveScanProtocol.MaximumDiagnosticBatchSize))
            {
                await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.DiagnosticBatch,
                    new LiveScanDiagnosticBatchDto(diagnosticSequence++, batch), scanCancellation.Token).ConfigureAwait(false);
            }

            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.TerminalResult,
                execution.Terminal, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            scanCancellation.Cancel();
            return 23;
        }
        finally
        {
            listenerStop.Cancel();
            try { await listener.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task ListenForCancellationAsync(
        Stream pipe,
        Guid sessionId,
        CancellationTokenSource scanCancellation,
        CancellationToken listenerStop,
        Action onCancel)
    {
        try
        {
            var envelope = await LiveScanProtocolCodec.ReadAsync(pipe, listenerStop).ConfigureAwait(false);
            if (envelope.SessionId != sessionId || envelope.ProtocolVersion != LiveScanProtocol.Version || envelope.Kind != LiveScanMessageKind.Cancel)
                throw new LiveScanProtocolException("Only cancellation is accepted after a scan starts.");
            var cancel = LiveScanProtocolCodec.ReadPayload<LiveScanCancelRequest>(envelope);
            if (string.IsNullOrWhiteSpace(cancel.ReasonCode) || cancel.ReasonCode.Length > 128)
                throw new LiveScanProtocolException("The cancellation reason is invalid.");
            onCancel();
            scanCancellation.Cancel();
        }
        catch (OperationCanceledException) when (listenerStop.IsCancellationRequested)
        {
        }
        catch
        {
            scanCancellation.Cancel();
        }
    }

    private static async Task WriteProgressAsync(
        Stream pipe,
        Guid sessionId,
        ChannelReader<LiveScanProgressDto> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var update in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.Progress, update, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateStart(LiveScanStartRequest start, ScanWorkerArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(start.Grant);
        ArgumentNullException.ThrowIfNull(start.Budgets);
        start.Budgets.Validate();
        _ = CanonicalVolumeGuidPath.Parse(start.Grant.CanonicalVolumeGuidPath);
        if (start.Grant.GrantId == Guid.Empty || start.Grant.DiscoveryGeneration <= 0 || start.Grant.ExpiresAt <= DateTimeOffset.UtcNow ||
            start.Grant.Nonce.Length != 64 || start.Grant.PhysicalDeviceIdentities.Count is 0 or > 128 ||
            start.Grant.PhysicalDeviceIdentities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
            start.Grant.PhysicalDiskNumbers.Count is 0 or > 128 || start.Grant.PhysicalDiskNumbers.Any(number => number < 0) ||
            !start.Grant.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            start.Grant.FileSystem.Length > 16 || start.Grant.VolumeIdentity.Length is 0 or > 256 ||
            start.Grant.DisplayMountPath.Length > 260 ||
            HasProhibitedCharacters(start.Grant.FileSystem) || HasProhibitedCharacters(start.Grant.VolumeIdentity) ||
            HasProhibitedCharacters(start.Grant.DisplayMountPath) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(start.Grant.Nonce), Convert.FromHexString(arguments.Nonce)))
        {
            throw new LiveScanProtocolException("The scan grant is invalid or does not belong to this session.");
        }
    }

    private static bool HasProhibitedCharacters(string value) =>
        value.Any(character => character == '\0' || char.IsControl(character));

    private static LiveScanExecutionResult Failure(LiveScanTerminalStatus status, string reason) => new(
        new(status, LiveScanConsistency.Partial, 0, 0, 0, 0, true, reason),
        [],
        []);

    private sealed class ChannelProgress(ChannelWriter<LiveScanProgressDto> writer) : IProgress<LiveScanProgressDto>
    {
        public void Report(LiveScanProgressDto value) => writer.TryWrite(value);
    }
}
