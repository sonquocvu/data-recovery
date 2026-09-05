using System.Buffers.Binary;
using System.IO.Pipes;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using static DataRecoveryStudio.Tests.Phase8CLiveExFatTests;

namespace DataRecoveryStudio.Tests;

public sealed class Phase8CWorkerTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("filesystem")]
    [InlineData("capacity")]
    [InlineData("extent")]
    [InlineData("disk")]
    [InlineData("identity")]
    [InlineData("removed")]
    [InlineData("network")]
    public async Task WorkerIndependentlyRevalidatesMetadataIdentityAndFullExtents(string mutation)
    {
        var native = new RevalidationNative(mutation);
        var grant = Grant() with
        {
            PhysicalDeviceIdentities = [StorageMetadataNormalizer.CreateIdentity(7, null, VolumePath).Id.Value],
        };
        if (mutation == "identity") grant = grant with { PhysicalDeviceIdentities = ["replaced-identity"] };
        var validator = new WindowsVolumeOnlyLiveScanTargetValidator(native);
        if (mutation == "same") Assert.Equal(grant, (await validator.ValidateAsync(grant, default)).Grant);
        else await Assert.ThrowsAsync<LiveScanTargetValidationException>(() => validator.ValidateAsync(grant, default));
        Assert.Equal(native.Opens, native.Disposals);
    }

    [Fact]
    public async Task AuthenticatedPipePublishesProductionExFatMetadataWithOneTerminal()
    {
        var factory = new InstrumentedFactory(Fixture().Bytes);
        var executor = new LiveScanExecutor(new AcceptingValidator(), factory, new NtfsMetadataScanner());
        var (result, exit) = await RunHost(executor);
        Assert.Equal(0, exit);
        Assert.Equal(LiveScanTerminalStatus.Completed, result!.Terminal.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.Terminal.SourceHandleDisposed);
        Assert.True(factory.Handle!.IsClosed);
    }

    [Theory]
    [InlineData("cancel", LiveScanTerminalStatus.Canceled)]
    [InlineData("timeout", LiveScanTerminalStatus.TimedOut)]
    [InlineData("crash", LiveScanTerminalStatus.Failed)]
    [InlineData("remove", LiveScanTerminalStatus.SourceRemoved)]
    [InlineData("pipe", LiveScanTerminalStatus.Failed)]
    public async Task SimulatedLifecycleFailuresCancelAndCleanUp(string mode, LiveScanTerminalStatus expected)
    {
        // Automated lifecycle simulation; no process elevation or recovery device access.
        var executor = new ControlledExecutor(mode);
        var (result, exit) = await RunHost(executor, mode);
        Assert.True(executor.Cleaned);
        if (mode == "pipe") Assert.NotEqual(0, exit);
        else
        {
            Assert.Equal(0, exit);
            Assert.Equal(expected, result!.Terminal.Status);
            Assert.Empty(result.Candidates);
        }
    }

    [Theory]
    [InlineData("scanner")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("extents")]
    [InlineData("null")]
    public async Task ForgedStartIsRejectedBeforeExecutor(string mutation)
    {
        var grant = Grant();
        var args = new ScanWorkerArguments($"DataRecoveryStudio.LiveScan.{grant.CorrelationId:N}.ABC", grant.CorrelationId,
            grant.Nonce, LiveScanProtocol.Version, Kind);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = Server(args.PipeName);
        var executor = new ControlledExecutor("crash");
        var host = new NamedPipeScanWorkerHost(executor).RunAsync(args, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        _ = await LiveScanProtocolCodec.ReadAsync(server, deadline.Token);
        await LiveScanProtocolCodec.WriteAsync(server, args.SessionId, LiveScanMessageKind.HandshakeAccepted,
            new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = Kind }, deadline.Token);
        grant = mutation switch
        {
            "scanner" => grant with { ScannerKind = LiveScanScannerKind.Fat32StandardMetadata },
            "nonce" => grant with { Nonce = new string('F', 64) },
            "expired" => grant with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "extents" => grant with { Extents = [] },
            _ => grant with { FileSystem = null! },
        };
        await LiveScanProtocolCodec.WriteAsync(server, args.SessionId, LiveScanMessageKind.StartScan,
            new LiveScanStartRequest(grant, new()) { ScannerKind = Kind }, deadline.Token);
        Assert.Equal(22, await host.WaitAsync(deadline.Token));
        Assert.False(executor.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task OldWorkerProtocolExitsBeforeConnecting()
    {
        var executor = new ControlledExecutor("crash");
        Assert.Equal(20, await new NamedPipeScanWorkerHost(executor).RunAsync(
            new("DataRecoveryStudio.LiveScan.old.ABC", Guid.NewGuid(), new string('A', 64), 3, Kind), default));
        Assert.False(executor.Started.Task.IsCompleted);
    }

    private static async Task<(LiveScanResult? Result, int Exit)> RunHost(ILiveScanExecutor executor, string mode = "completed")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var grant = Grant();
        var args = new ScanWorkerArguments($"DataRecoveryStudio.LiveScan.{grant.CorrelationId:N}.ABC",
            grant.CorrelationId, grant.Nonce, LiveScanProtocol.Version, Kind);
        await using var server = Server(args.PipeName);
        var host = new NamedPipeScanWorkerHost(executor).RunAsync(args, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        var hello = LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeHello>(await LiveScanProtocolCodec.ReadAsync(server, deadline.Token));
        Assert.Equal(Kind, hello.ScannerKind);
        Assert.Equal(grant.Nonce, hello.Nonce);
        await LiveScanProtocolCodec.WriteAsync(server, args.SessionId, LiveScanMessageKind.HandshakeAccepted,
            new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = Kind }, deadline.Token);
        await LiveScanProtocolCodec.WriteAsync(server, args.SessionId, LiveScanMessageKind.StartScan,
            new LiveScanStartRequest(grant, new(MaximumDuration: mode == "timeout" ? TimeSpan.FromMilliseconds(100) : null))
            { ScannerKind = Kind }, deadline.Token);
        if (executor is ControlledExecutor controlled) await controlled.Started.Task.WaitAsync(deadline.Token);
        if (mode == "pipe")
        {
            await server.DisposeAsync();
            return (null, await host.WaitAsync(deadline.Token));
        }
        if (mode == "cancel")
            await LiveScanProtocolCodec.WriteAsync(server, args.SessionId, LiveScanMessageKind.Cancel,
                new LiveScanCancelRequest("AutomatedCancellation"), deadline.Token);
        var accumulator = new LiveScanResultAccumulator(args.SessionId, null, Kind);
        while (true)
        {
            var envelope = await LiveScanProtocolCodec.ReadAsync(server, deadline.Token);
            if (envelope.Kind != LiveScanMessageKind.TerminalResult) { accumulator.Accept(envelope); continue; }
            var result = accumulator.Complete(envelope);
            var exit = await host.WaitAsync(deadline.Token);
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await LiveScanProtocolCodec.ReadAsync(server, deadline.Token));
            return (result, exit);
        }
    }

    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class ControlledExecutor(string mode) : ILiveScanExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cleaned { get; private set; }
        public async Task<LiveScanExecutionResult> ExecuteAsync(Guid sessionId, LiveScanTargetGrant grant,
            LiveScanBudgets requestedBudgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                if (mode == "crash") throw new InvalidOperationException("Simulated executor crash");
                if (mode == "remove") throw new LiveSourceRemovedException();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
            finally { Cleaned = true; }
        }
    }
    private sealed class AcceptingValidator : ILiveScanTargetValidator
    {
        public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken token) =>
            Task.FromResult(new ValidatedLiveScanTarget(grant));
    }
    private sealed class RevalidationNative(string mutation) : IWindowsStorageNative
    {
        public int Opens { get; private set; }
        public int Disposals { get; private set; }
        public IReadOnlyList<string> EnumerateVolumeNames(CancellationToken token) => mutation == "removed" ? [] : [VolumePath + "\\"];
        public IReadOnlyList<string> GetVolumePathNames(string volumeName) => ["F:\\"];
        public NativeVolumeInformation GetVolumeInformation(string volumeName) => new("Fixture", mutation == "filesystem" ? "NTFS" : "exFAT");
        public NativeDiskSpace GetDiskSpace(string path) => new(mutation == "capacity" ? 1179647UL : 1179648UL, 1024);
        public WindowsDriveType GetDriveType(string rootPath) => mutation == "network" ? WindowsDriveType.Remote : WindowsDriveType.Removable;
        public IMetadataDeviceHandle OpenMetadataDevice(string path, MetadataOpenOptions options)
        {
            Assert.Equal(MetadataOpenOptions.ReadOnlyMetadata, options);
            Assert.True(path == VolumePath || path == "\\\\.\\PhysicalDrive7");
            Opens++;
            return new MetadataHandle(() => Disposals++);
        }
        public byte[] QueryDevice(IMetadataDeviceHandle handle, uint code, ReadOnlySpan<byte> input, int initialCapacity = 4096)
        {
            // Deliberately unavailable physical descriptor: existing session identity fallback is rechecked.
            if (code != StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents) throw new IOException("Simulated optional metadata unavailable");
            var bytes = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), mutation == "disk" ? 8 : 7);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), mutation == "extent" ? 2097152 : 1048576);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 1179648);
            return bytes;
        }
    }
    private sealed class MetadataHandle(Action disposed) : IMetadataDeviceHandle
    {
        public bool IsInvalid => false;
        public void Dispose() => disposed();
    }
}
