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
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface IScanWorkerLauncher
{
    IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce);
}

public sealed class ElevatedScanWorkerLauncher : IScanWorkerLauncher
{
    public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce)
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
        var process = Process.Start(start) ?? throw new InvalidOperationException("The scan worker did not start.");
        return new ScanWorkerProcess(process);
    }

    private sealed class ScanWorkerProcess(Process process) : IScanWorkerProcess
    {
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

public sealed class NamedPipeLiveScanWorkerClient : ILiveScanWorkerClient
{
    private readonly string _installationDirectory;
    private readonly TrustedScanWorkerPathResolver _pathResolver;
    private readonly IScanWorkerLauncher _launcher;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _cancelGracePeriod;
    private readonly IStructuredLogger? _logger;

    public NamedPipeLiveScanWorkerClient(
        string installationDirectory,
        TrustedScanWorkerPathResolver? pathResolver = null,
        IScanWorkerLauncher? launcher = null,
        TimeSpan? connectionTimeout = null,
        TimeSpan? cancelGracePeriod = null,
        IStructuredLogger? logger = null)
    {
        _installationDirectory = installationDirectory;
        _pathResolver = pathResolver ?? new TrustedScanWorkerPathResolver();
        _launcher = launcher ?? new ElevatedScanWorkerLauncher();
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        _cancelGracePeriod = cancelGracePeriod ?? TimeSpan.FromSeconds(3);
        _logger = logger;
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
        var sessionId = Guid.NewGuid();
        if (cancellationToken.IsCancellationRequested) return Failure(sessionId, LiveScanTerminalStatus.Canceled, "CanceledBeforeElevation");

        string workerPath;
        try
        {
            workerPath = _pathResolver.Resolve(_installationDirectory);
        }
        catch (FileNotFoundException)
        {
            return Failure(sessionId, LiveScanTerminalStatus.WorkerMissing, "WorkerMissing");
        }
        catch
        {
            return Failure(sessionId, LiveScanTerminalStatus.WorkerStartFailed, "WorkerPathRejected");
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
            progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, LiveScanClientPhase.RequestingPermission));
            try
            {
                process = _launcher.Launch(workerPath, pipeName, sessionId, grant.Nonce);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return Failure(sessionId, LiveScanTerminalStatus.PermissionDeclined, "UacDeclined");
            }
            catch
            {
                return Failure(sessionId, LiveScanTerminalStatus.WorkerStartFailed, "WorkerStartFailed");
            }

            progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, LiveScanClientPhase.LaunchingWorker));
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(budgets.EffectiveMaximumDuration);
            try
            {
                progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, LiveScanClientPhase.ConnectingSecureChannel));
                var connection = pipe.WaitForConnectionAsync(overall.Token);
                var exited = process.WaitForExitAsync(overall.Token);
                var timeout = Task.Delay(_connectionTimeout, overall.Token);
                var completed = await Task.WhenAny(connection, exited, timeout).ConfigureAwait(false);
                if (completed == exited)
                {
                    return Failure(sessionId, LiveScanTerminalStatus.WorkerCrashed, "WorkerExitedBeforeConnection");
                }

                if (completed == timeout)
                {
                    return Failure(sessionId, LiveScanTerminalStatus.SecureConnectionFailed, "ConnectionTimeout");
                }

                await connection.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(sessionId, LiveScanTerminalStatus.Canceled, "CanceledDuringConnection");
            }

            var helloEnvelope = await ReadWithIdleTimeoutAsync(pipe, budgets.EffectiveMaximumIdleDuration, overall.Token).ConfigureAwait(false);
            if (helloEnvelope.Kind != LiveScanMessageKind.HandshakeHello || helloEnvelope.SessionId != sessionId)
                throw new LiveScanProtocolException("The worker handshake envelope is invalid.");
            var hello = LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeHello>(helloEnvelope);
            if (hello.ProtocolVersion != LiveScanProtocol.Version || helloEnvelope.ProtocolVersion != LiveScanProtocol.Version)
                return Failure(sessionId, LiveScanTerminalStatus.WorkerVersionMismatch, "ProtocolVersionMismatch");
            if (!hello.WorkerVersion.Equals(LiveScanProtocol.WorkerVersion, StringComparison.Ordinal))
                return Failure(sessionId, LiveScanTerminalStatus.WorkerVersionMismatch, "WorkerVersionMismatch");
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hello.Nonce), Convert.FromHexString(grant.Nonce)))
                throw new LiveScanProtocolException("The worker handshake nonce is invalid.");

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
                new LiveScanHandshakeAccepted(LiveScanProtocol.Version), overall.Token).ConfigureAwait(false);
            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.StartScan,
                new LiveScanStartRequest(grant, budgets), overall.Token).ConfigureAwait(false);

            progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, "Progress.Phase.Metadata"));
            var accumulator = new LiveScanResultAccumulator(sessionId, progress);
            while (true)
            {
                LiveScanMessageEnvelope envelope;
                try
                {
                    envelope = await ReadWithIdleTimeoutAsync(pipe, budgets.EffectiveMaximumIdleDuration, overall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await RequestCancellationAsync(pipe, sessionId, process).ConfigureAwait(false);
                    return Failure(sessionId, LiveScanTerminalStatus.Canceled, "Canceled");
                }
                catch (OperationCanceledException)
                {
                    await RequestCancellationAsync(pipe, sessionId, process).ConfigureAwait(false);
                    return Failure(sessionId, LiveScanTerminalStatus.TimedOut, "ScanTimeout");
                }
                catch (TimeoutException)
                {
                    await RequestCancellationAsync(pipe, sessionId, process).ConfigureAwait(false);
                    return Failure(sessionId, LiveScanTerminalStatus.TimedOut, "WorkerIdleTimeout");
                }
                catch (EndOfStreamException)
                {
                    return Failure(sessionId, LiveScanTerminalStatus.WorkerCrashed, "WorkerDisconnected");
                }

                if (envelope.SessionId != sessionId || envelope.ProtocolVersion != LiveScanProtocol.Version)
                    throw new LiveScanProtocolException("A worker message has the wrong session or protocol version.");
                if (envelope.Kind == LiveScanMessageKind.TerminalResult) return accumulator.Complete(envelope);
                accumulator.Accept(envelope);
            }
        }
        catch (LiveScanProtocolException)
        {
            return Failure(sessionId, LiveScanTerminalStatus.ProtocolFailure, "ProtocolViolation");
        }
        catch (Exception) when (process is { HasExited: true })
        {
            return Failure(sessionId, LiveScanTerminalStatus.WorkerCrashed, "WorkerCrashed");
        }
        catch
        {
            return Failure(sessionId, LiveScanTerminalStatus.SecureConnectionFailed, "SecureConnectionFailed");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill();
                process.Dispose();
            }
        }
    }

    private async Task RequestCancellationAsync(Stream pipe, Guid sessionId, IScanWorkerProcess process)
    {
        try
        {
            await LiveScanProtocolCodec.WriteAsync(pipe, sessionId, LiveScanMessageKind.Cancel,
                new LiveScanCancelRequest("ParentCanceled"), CancellationToken.None).ConfigureAwait(false);
            using var grace = new CancellationTokenSource(_cancelGracePeriod);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill();
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

    private static LiveScanResult Failure(Guid sessionId, LiveScanTerminalStatus status, string reason) => new(
        sessionId,
        new(status, LiveScanConsistency.Partial, 0, 0, 0, 0, true, reason),
        [],
        []);
}
