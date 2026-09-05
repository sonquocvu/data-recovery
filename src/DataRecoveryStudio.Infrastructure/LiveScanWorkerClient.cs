using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal interface ITrustedWorkerFileSystem
{
    string GetFullPath(string path);
    bool FileExists(string path);
    FileAttributes GetAttributes(string path);
}

internal sealed class TrustedWorkerFileSystem : ITrustedWorkerFileSystem
{
    public string GetFullPath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}

public sealed class TrustedScanWorkerPathResolver
{
    public const string WorkerFileName = "DataRecoveryStudio.ScanWorker.exe";
    private readonly ITrustedWorkerFileSystem _fileSystem;

    public TrustedScanWorkerPathResolver() : this(new TrustedWorkerFileSystem())
    {
    }

    internal TrustedScanWorkerPathResolver(ITrustedWorkerFileSystem fileSystem) => _fileSystem = fileSystem;

    public string Resolve(string installationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        var root = _fileSystem.GetFullPath(installationDirectory);
        var target = _fileSystem.GetFullPath(Path.Combine(root, WorkerFileName));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The worker resolved outside the installation directory.");
        if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The elevated worker cannot be launched from a temporary directory.");
        if (!_fileSystem.FileExists(target)) throw new FileNotFoundException("The scan worker is missing.", target);
        var attributes = _fileSystem.GetAttributes(target);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidOperationException("The scan worker path is not a trusted ordinary file.");
        for (var directory = Path.GetDirectoryName(target); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if ((_fileSystem.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The scan worker path traverses a reparse point.");
        }

        return target;
    }
}

public interface IScanWorkerProcess : IDisposable
{
    int Id => 0;
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface IScanWorkerLauncher
{
    IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce);
    IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce, LiveScanScannerKind scannerKind) =>
        Launch(workerPath, pipeName, sessionId, nonce);
}

public sealed class ElevatedScanWorkerLauncher : IScanWorkerLauncher
{
    public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce)
        => Launch(workerPath, pipeName, sessionId, nonce, LiveScanScannerKind.NtfsStandardMetadata);

    public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce, LiveScanScannerKind scannerKind)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(workerPath),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--session");
        start.ArgumentList.Add(sessionId.ToString("D"));
        start.ArgumentList.Add("--nonce");
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add("--protocol");
        start.ArgumentList.Add(LiveScanProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--scanner");
        start.ArgumentList.Add(scannerKind.ToString());
        var process = Process.Start(start) ?? throw new InvalidOperationException("The scan worker did not start.");
        return new ScanWorkerProcess(process);
    }

    private sealed class ScanWorkerProcess(Process process) : IScanWorkerProcess
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }

        public void Dispose() => process.Dispose();
    }
}

public sealed class NamedPipeLiveScanWorkerClient : IProductionLiveScanWorkerClient
{
    private readonly string _installationDirectory;
    private readonly TrustedScanWorkerPathResolver _pathResolver;
    private readonly IScanWorkerLauncher _launcher;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _cancelGracePeriod;
    private readonly IStructuredLogger? _logger;
    private readonly IProgress<LiveScanWorkerLifecycleEvent>? _lifecycle;

    public NamedPipeLiveScanWorkerClient(
        string installationDirectory,
        TrustedScanWorkerPathResolver? pathResolver = null,
        IScanWorkerLauncher? launcher = null,
        TimeSpan? connectionTimeout = null,
        TimeSpan? cancelGracePeriod = null,
        IStructuredLogger? logger = null,
        IProgress<LiveScanWorkerLifecycleEvent>? lifecycle = null)
    {
        _installationDirectory = installationDirectory;
        _pathResolver = pathResolver ?? new TrustedScanWorkerPathResolver();
        _launcher = launcher ?? new ElevatedScanWorkerLauncher();
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        _cancelGracePeriod = cancelGracePeriod ?? TimeSpan.FromSeconds(3);
        _logger = logger;
        _lifecycle = lifecycle;
    }

    public async Task<LiveScanResult> ScanAsync(
        LiveScanTargetGrant grant,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(budgets);
        budgets.Validate();
        var sessionId = grant.CorrelationId;
        if (sessionId == Guid.Empty)
            return Failure(Guid.NewGuid(), grant.ScannerKind, LiveScanTerminalStatus.ProtocolFailure, "MissingGrantCorrelation");
        if (grant.ScannerKind is not (LiveScanScannerKind.NtfsStandardMetadata or LiveScanScannerKind.Fat32StandardMetadata or LiveScanScannerKind.ExFatStandardMetadata))
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.ProtocolFailure, "UnknownScannerKind");
        if (cancellationToken.IsCancellationRequested) return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.Canceled, "CanceledBeforeElevation");

        string workerPath;
        try
        {
            workerPath = _pathResolver.Resolve(_installationDirectory);
        }
        catch (FileNotFoundException)
        {
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerMissing, "WorkerMissing");
        }
        catch
        {
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerStartFailed, "WorkerPathRejected");
        }

        var pipeName = $"DataRecoveryStudio.LiveScan.{sessionId:N}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);
        IScanWorkerProcess? process = null;
        try
        {
            progress?.Report(ClientProgress(grant.ScannerKind, LiveScanClientPhase.RequestingPermission));
            try
            {
                ReportLifecycle(LiveScanWorkerLifecycleEventKind.LaunchRequested, sessionId);
                process = _launcher.Launch(workerPath, pipeName, sessionId, grant.Nonce, grant.ScannerKind);
                ReportLifecycle(LiveScanWorkerLifecycleEventKind.WorkerStarted, sessionId, process.Id);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                ReportLifecycle(LiveScanWorkerLifecycleEventKind.UacDenied, sessionId);
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.PermissionDeclined, "UacDeclined");
            }
            catch
            {
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerStartFailed, "WorkerStartFailed");
            }

            progress?.Report(ClientProgress(grant.ScannerKind, LiveScanClientPhase.LaunchingWorker));
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(budgets.EffectiveMaximumDuration);
            try
            {
                progress?.Report(ClientProgress(grant.ScannerKind, LiveScanClientPhase.ConnectingSecureChannel));
                var connection = pipe.WaitForConnectionAsync(overall.Token);
                var exited = process.WaitForExitAsync(overall.Token);
                var timeout = Task.Delay(_connectionTimeout, overall.Token);
                var completed = await Task.WhenAny(connection, exited, timeout).ConfigureAwait(false);
                if (completed == exited)
                {
                    return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerCrashed, "WorkerExitedBeforeConnection");
                }

                if (completed == timeout)
                {
                    return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.SecureConnectionFailed, "ConnectionTimeout");
                }

                await connection.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.Canceled, "CanceledDuringConnection");
            }

            var helloEnvelope = await ReadWithIdleTimeoutAsync(pipe, budgets.EffectiveMaximumIdleDuration, overall.Token).ConfigureAwait(false);
            if (helloEnvelope.Kind != LiveScanMessageKind.HandshakeHello || helloEnvelope.SessionId != sessionId)
                throw new LiveScanProtocolException("The worker handshake envelope is invalid.");
            var hello = LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeHello>(helloEnvelope);
            if (hello.ProtocolVersion != LiveScanProtocol.Version || helloEnvelope.ProtocolVersion != LiveScanProtocol.Version)
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerVersionMismatch, "ProtocolVersionMismatch");
            if (!hello.WorkerVersion.Equals(LiveScanProtocol.WorkerVersion, StringComparison.Ordinal))
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerVersionMismatch, "WorkerVersionMismatch");
            if (hello.ScannerKind != grant.ScannerKind)
                return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.ProtocolFailure, "ScannerKindMismatch");
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hello.Nonce), Convert.FromHexString(grant.Nonce)))
                throw new LiveScanProtocolException("The worker handshake nonce is invalid.");
            ReportLifecycle(LiveScanWorkerLifecycleEventKind.HandshakeCompleted, sessionId, process.Id);

            if (_logger is not null)
            {
                await _logger.LogAsync(
                    "Information",
                    "LiveScanWorkerAuthenticated",
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "LiveStandardScan",
                        ["correlationId"] = sessionId,
                        ["protocolVersion"] = hello.ProtocolVersion,
                        ["workerVersion"] = hello.WorkerVersion,
                    },
                    overall.Token).ConfigureAwait(false);
            }

            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.HandshakeAccepted,
                new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = grant.ScannerKind }, overall.Token).ConfigureAwait(false);
            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.StartScan,
                new LiveScanStartRequest(grant, budgets) { ScannerKind = grant.ScannerKind }, overall.Token).ConfigureAwait(false);

            progress?.Report(ClientProgress(grant.ScannerKind, grant.ScannerKind == LiveScanScannerKind.ExFatStandardMetadata ? "LiveScan.ExFat.Boot" : grant.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata
                ? "Progress.Phase.Fat32.ReadingBootSectors"
                : "Progress.Phase.Metadata"));
            var accumulator = new LiveScanResultAccumulator(sessionId, progress, grant.ScannerKind);
            while (true)
            {
                LiveScanMessageEnvelope envelope;
                try
                {
                    envelope = await ReadWithIdleTimeoutAsync(pipe, budgets.EffectiveMaximumIdleDuration, overall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var canceled = await CompleteAfterCancellationAsync(
                        pipe, sessionId, process, accumulator).ConfigureAwait(false);
                    return OverrideCanceled(canceled, sessionId, grant.ScannerKind, LiveScanTerminalStatus.Canceled, "ParentCanceled");
                }
                catch (OperationCanceledException)
                {
                    var timedOut = await CompleteAfterCancellationAsync(
                        pipe, sessionId, process, accumulator).ConfigureAwait(false);
                    return OverrideCanceled(timedOut, sessionId, grant.ScannerKind, LiveScanTerminalStatus.TimedOut, "ScanTimeout");
                }
                catch (TimeoutException)
                {
                    var timedOut = await CompleteAfterCancellationAsync(
                        pipe, sessionId, process, accumulator).ConfigureAwait(false);
                    return OverrideCanceled(timedOut, sessionId, grant.ScannerKind, LiveScanTerminalStatus.TimedOut, "WorkerIdleTimeout");
                }
                catch (EndOfStreamException)
                {
                    return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerCrashed, "WorkerDisconnected");
                }

                if (envelope.SessionId != sessionId || envelope.ProtocolVersion != LiveScanProtocol.Version)
                    throw new LiveScanProtocolException("A worker message has the wrong session or protocol version.");
                if (envelope.Kind == LiveScanMessageKind.TerminalResult)
                {
                    var result = accumulator.Complete(envelope);
                    ReportLifecycle(LiveScanWorkerLifecycleEventKind.TerminalAccepted, sessionId, process.Id);
                    try
                    {
                        using var exitGrace = new CancellationTokenSource(_cancelGracePeriod);
                        await process.WaitForExitAsync(exitGrace.Token).ConfigureAwait(false);
                        ReportLifecycle(LiveScanWorkerLifecycleEventKind.WorkerExited, sessionId, process.Id, process.ExitCode);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    return cancellationToken.IsCancellationRequested
                        ? OverrideCanceled(result, sessionId, grant.ScannerKind, LiveScanTerminalStatus.Canceled, "ParentCanceled")
                        : SuppressIncompleteCandidates(result);
                }
                accumulator.Accept(envelope);
            }
        }
        catch (LiveScanProtocolException)
        {
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.ProtocolFailure, "ProtocolViolation");
        }
        catch (Exception) when (process is { HasExited: true })
        {
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.WorkerCrashed, "WorkerCrashed");
        }
        catch
        {
            return Failure(sessionId, grant.ScannerKind, LiveScanTerminalStatus.SecureConnectionFailed, "SecureConnectionFailed");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    ReportLifecycle(LiveScanWorkerLifecycleEventKind.WorkerTerminatedAfterGracePeriod, sessionId, process.Id);
                }
                process.Dispose();
                ReportLifecycle(LiveScanWorkerLifecycleEventKind.WorkerDisposed, sessionId, process.Id);
            }
        }
    }

    private static LiveScanResult OverrideCanceled(LiveScanResult? result, Guid session, LiveScanScannerKind kind,
        LiveScanTerminalStatus status, string reason) => result is null ? Failure(session, kind, status, reason)
        : result with
        {
            Candidates = [],
            Terminal = result.Terminal with
            { Status = status, Consistency = LiveScanConsistency.Partial, CandidateCount = 0, IsPartial = true, ReasonCode = reason }
        };

    private async Task<LiveScanResult?> CompleteAfterCancellationAsync(
        Stream pipe,
        Guid sessionId,
        IScanWorkerProcess process,
        LiveScanResultAccumulator accumulator)
    {
        try
        {
            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.Cancel,
                new LiveScanCancelRequest("ParentCanceled"), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            using var grace = new CancellationTokenSource(_cancelGracePeriod);
            while (true)
            {
                var envelope = await ReadWithIdleTimeoutAsync(pipe, _cancelGracePeriod, grace.Token).ConfigureAwait(false);
                if (envelope.SessionId != sessionId || envelope.ProtocolVersion != LiveScanProtocol.Version)
                    throw new LiveScanProtocolException("A worker message has the wrong session or protocol version.");
                if (envelope.Kind != LiveScanMessageKind.TerminalResult)
                {
                    accumulator.Accept(envelope);
                    continue;
                }

                var result = accumulator.Complete(envelope);
                ReportLifecycle(LiveScanWorkerLifecycleEventKind.TerminalAccepted, sessionId, process.Id);
                try
                {
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    ReportLifecycle(LiveScanWorkerLifecycleEventKind.WorkerExited, sessionId, process.Id, process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                }
                return SuppressIncompleteCandidates(result);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
        {
            return null;
        }
    }

    private static async Task<LiveScanMessageEnvelope> ReadWithIdleTimeoutAsync(Stream pipe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await LiveScanProtocolCodec.ReadAsync(pipe, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }
    }

    private static LiveScanResult Failure(Guid sessionId, LiveScanScannerKind scannerKind, LiveScanTerminalStatus status, string reason) => new(
        sessionId,
        new(status, LiveScanConsistency.Partial, 0, 0, 0, 0, true, reason)
        {
            ScannerKind = scannerKind,
            FileSystem = scannerKind == LiveScanScannerKind.ExFatStandardMetadata ? "exFAT" : scannerKind == LiveScanScannerKind.Fat32StandardMetadata ? "FAT32" : "NTFS",
        },
        [],
        []);

    internal static LiveScanResult SuppressIncompleteCandidates(LiveScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Terminal.Status is LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial
            ? result
            : result with
            {
                Terminal = result.Terminal with { CandidateCount = 0 },
                Candidates = [],
            };
    }

    private static LiveScanProgressDto ClientProgress(LiveScanScannerKind scannerKind, string phase) =>
        new(0, 0, 0, 0, phase) { ScannerKind = scannerKind };

    private void ReportLifecycle(
        LiveScanWorkerLifecycleEventKind kind,
        Guid correlationId,
        int? processId = null,
        int? exitCode = null)
    {
        try
        {
            _lifecycle?.Report(new(kind, correlationId, DateTimeOffset.UtcNow, processId, exitCode));
        }
        catch
        {
        }
    }
}
