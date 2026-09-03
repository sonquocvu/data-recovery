# Phase 7D: controlled live FAT32 hardware validation

Phase 7D adds a release-readiness and evidence layer around the Phase 7C live FAT32 Standard Scan. It does not enable live recovery, Deep Scan, exFAT, FAT12/FAT16, or broader rollout. The live FAT32 feature remains disabled by default and requires its existing independent feature gate.

## Safety boundary

The harness discovers and authorizes a target in the unelevated process, then invokes the existing one-shot elevated worker. Only `LiveVolumeRandomAccessSource` opens the volume. Its native tuple remains:

- `GENERIC_READ`
- `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`
- `OPEN_EXISTING`
- `FILE_FLAG_OVERLAPPED`
- `SafeFileHandle`

The validation path contains no target-volume file creation, formatting, partitioning, initialization, repair, CHKDSK, mount/unmount, lock/dismount, ejection, VSS, service, driver, physical-drive scan, or write-capable native access. Reports are written by a separate diagnostics writer only after it proves that the destination is an ordinary non-root directory on a different mounted root than the controlled target.

## Exact authorization

A hardware run requires both values exactly:

```powershell
$env:DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION='1'
$env:DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME='<controlled FAT32 target>'
```

The target may be a canonical `\\?\Volume{GUID}` identity or a bare drive root such as `R:` or `R:\`. A drive letter is never used directly for authorization: it must resolve to exactly one canonical volume in the current Phase 3 snapshot. Paths below a drive root and all `PhysicalDriveN` spellings are rejected.

The resolved volume must be connected, available, mounted, local, supported FAT32, canonically identified, mapped to at least one physical identity and disk-number extent, and represented by an eligible device. Ambiguous, system, application-installation, virtual-bus, network, optical, RAM, inaccessible, RAW, unmounted, NTFS, exFAT, FAT12, and FAT16 targets are rejected. The harness never selects the first eligible disk.

Before elevation, an optional observer receives only display name, mount root, filesystem, capacity, a SHA-256 opaque volume identity, SHA-256 physical-identity values, and disk numbers. Raw serials, grants, nonces, sectors, payload, and candidate content are excluded.

## Production path exercised

The opt-in integration test uses production Phase 3 discovery, `LiveScanTargetGrantAuthority`, `NamedPipeLiveScanWorkerClient`, the separately elevated worker, worker-side target revalidation, `LiveVolumeRandomAccessSource`, and the Phase 7A `Fat32MetadataScanner`. Protocol 3 binds scanner kind and scanner version and carries bounded validation evidence. There is no in-process volume shortcut and no mock fallback.

Lifecycle observations include launch request, exact worker process ID, handshake, one accepted terminal, exit code, forced termination after a grace period when necessary, and process disposal. The worker marks the live source disposed only after the executor's asynchronous source scope has completed.

The report also records discovery generation, one-time grant issuance/consumption, FAT32 geometry acceptance, boot relationship, FAT mirroring and active FAT policy, root cluster, traversal counters, candidates, diagnostics, bytes read, duration, terminal result, and source-handle disposal.

## Bounded consistency evidence

The worker hashes bounded metadata immediately before and after the scan:

- canonical identity and reported capacity are rechecked through discovery and the worker grant boundary;
- critical FAT32 geometry classification;
- primary/backup boot-sector evidence and relationship;
- selected FAT evidence, FAT count, mirroring, and active FAT policy;
- root cluster and root-directory chain evidence;
- physical identity and disk-number extent sets.

Only SHA-256 evidence is reported. Raw sectors are never emitted. A missing or changed critical evidence value prevents a pass and is reported as `ChangedDuringScan`, `Inconclusive`, or `Failed` as appropriate. This is a bounded live comparison, not a snapshot and not proof that the entire filesystem stayed unchanged.

## Cancellation and removal boundaries

Controlled cancellation additionally requires:

```powershell
$env:DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_CANCELLATION='1'
```

The scenario waits for the FAT32 scan phase, requests cancellation once, then waits a bounded grace period for the worker's terminal and exit. A natural completion race is `Inconclusive`, not a cancellation pass. Canceled, removed, changed, failed, timed-out, and protocol-rejected terminals publish no candidate catalog. Only the launched worker instance may be terminated.

Manual physical removal is isolated behind:

```powershell
$env:DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_MANUAL_REMOVAL='1'
```

It requires an interactive operator. The boundary produces a sanitized prompt, never performs software eject, and recognizes removal only when the explicitly authorized canonical target disappears from a new Phase 3 snapshot. Unrelated hot-plug events do not match. Without an operator session, the scenario is skipped.

The WPF hardware smoke path is separately marked by `DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_WPF_SMOKE=1`. Secure-desktop UAC cannot be fabricated or replaced with a fake worker; environments without interactive secure-desktop automation must report this scenario as skipped.

## Report and release gate

`LiveFat32ValidationReportWriter` writes timestamped JSON beneath an ordinary diagnostics/test-output directory. The schema contains application, worker, protocol, and scanner versions; sanitized target identity; scenario and overall outcomes; lifecycle observations; timestamps; invariant results; and the safety-audit result.

Possible outcomes are `Passed`, `Failed`, `Skipped`, `Inconclusive`, `ChangedDuringScan`, `Canceled`, `SourceRemoved`, `UacDenied`, `TimedOut`, `WorkerFailed`, and `ProtocolRejected`. A controlled scan is release-ready only when it completes, every mandatory invariant passes, and the safety audit passes. Missing hardware, a skipped test, partial completion, unavailable UAC, changed evidence, or any simulated run cannot produce `Passed`.

## Running validation

Automated checks, including the expected hardware skip when no target is supplied:

```powershell
dotnet test DataRecoveryStudio.sln -c Release --no-build --no-restore --filter 'FullyQualifiedName~Phase7D'
```

Controlled hardware run:

```powershell
$env:DATA_RECOVERY_STUDIO_ENABLE_LIVE_FAT32_STANDARD_SCAN='1'
$env:DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION='1'
$env:DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME='<controlled FAT32 target>'
dotnet test DataRecoveryStudio.sln -c Release --no-build --no-restore `
  --filter 'FullyQualifiedName~Phase7D|FullyQualifiedName~LiveFat32HardwareIntegration' `
  --logger 'console;verbosity=detailed'
```

Enable cancellation or the explicitly manual scenarios only when the controlled media and an interactive session are suitable. Inspect the JSON report under `artifacts/test-output/phase7d`; never redirect it to the scanned volume.

Phase 7D introduces no recovery format or file signature. It reuses the documented Phase 7A FAT32 metadata structures and signatures.
