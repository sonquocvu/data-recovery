namespace DataRecoveryStudio.Core;

public enum LiveFat32ValidationOutcome
{
    Passed = 1,
    Failed = 2,
    Skipped = 3,
    Inconclusive = 4,
    ChangedDuringScan = 5,
    Canceled = 6,
    SourceRemoved = 7,
    UacDenied = 8,
    TimedOut = 9,
    WorkerFailed = 10,
    ProtocolRejected = 11,
}

public enum LiveFat32ValidationScenarioKind
{
    ControlledScan = 1,
    ControlledCancellation = 2,
    ManualSourceRemoval = 3,
    WpfSmoke = 4,
    SafetyAudit = 5,
}

public enum LiveScanWorkerLifecycleEventKind
{
    LaunchRequested = 1,
    WorkerStarted = 2,
    HandshakeCompleted = 3,
    TerminalAccepted = 4,
    WorkerExited = 5,
    WorkerTerminatedAfterGracePeriod = 6,
    WorkerDisposed = 7,
    UacDenied = 8,
}

public enum Fat32BootRelationship
{
    Unknown = 0,
    PrimaryOnly = 1,
    PrimaryAndBackupMatch = 2,
    PrimaryAndBackupDiffer = 3,
    BackupSelected = 4,
}

public sealed record LiveScanWorkerLifecycleEvent(
    LiveScanWorkerLifecycleEventKind Kind,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    int? ProcessId = null,
    int? ExitCode = null);

public sealed record LiveFat32SanitizedTarget(
    string DisplayName,
    string MountPath,
    string FileSystem,
    long CapacityBytes,
    string OpaqueVolumeIdentity,
    IReadOnlyList<string> HashedPhysicalIdentities,
    IReadOnlyList<int> ExtentDiskNumbers);

public sealed record LiveFat32ValidationInvariant(
    string Code,
    bool Passed,
    string DetailCode);

public sealed record LiveFat32ValidationObservations(
    LiveScanScannerKind ScannerKind,
    long DiscoveryGeneration,
    bool GrantIssued,
    bool GrantConsumed,
    int? WorkerProcessId,
    bool HandshakeCompleted,
    bool Fat32GeometryValidated,
    Fat32BootRelationship BootRelationship,
    bool? FatMirroringEnabled,
    int? FatCount,
    int? ActiveFatIndex,
    uint? RootDirectoryCluster,
    int DirectoriesVisited,
    int DirectoryEntriesExamined,
    int FatEntriesInspected,
    int CandidateCount,
    int DiagnosticCount,
    long BytesRead,
    TimeSpan Duration,
    int? WorkerExitCode,
    bool SourceHandleDisposed);

public sealed record LiveFat32ValidationScenarioResult(
    LiveFat32ValidationScenarioKind Scenario,
    LiveFat32ValidationOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    LiveScanTerminalStatus? TerminalStatus,
    IReadOnlyList<LiveScanWorkerLifecycleEvent> WorkerLifecycle,
    IReadOnlyList<LiveFat32ValidationInvariant> Invariants,
    string DetailCode)
{
    public LiveFat32ValidationObservations? Observations { get; init; }
}

public sealed record LiveFat32ValidationReport(
    string ApplicationVersion,
    string WorkerVersion,
    int ProtocolVersion,
    string ScannerVersion,
    LiveFat32SanitizedTarget? Target,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<LiveFat32ValidationScenarioResult> Scenarios,
    IReadOnlyList<LiveFat32ValidationInvariant> MandatoryInvariants,
    bool SafetyAuditPassed,
    LiveFat32ValidationOutcome OverallOutcome);

public static class LiveFat32ReleaseReadiness
{
    public static LiveFat32ValidationOutcome Evaluate(
        IReadOnlyList<LiveFat32ValidationScenarioResult> scenarios,
        IReadOnlyList<LiveFat32ValidationInvariant> mandatoryInvariants,
        bool safetyAuditPassed)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(mandatoryInvariants);
        var controlled = scenarios.SingleOrDefault(item => item.Scenario == LiveFat32ValidationScenarioKind.ControlledScan);
        if (controlled is null) return LiveFat32ValidationOutcome.Skipped;
        if (controlled.Outcome != LiveFat32ValidationOutcome.Passed) return controlled.Outcome;
        return safetyAuditPassed && mandatoryInvariants.Count > 0 && mandatoryInvariants.All(invariant => invariant.Passed)
            ? LiveFat32ValidationOutcome.Passed
            : LiveFat32ValidationOutcome.Failed;
    }
}
