# Phase 5A: isolated live NTFS metadata scan

Phase 5A adds a headless boundary for scanning metadata on a real mounted Windows NTFS volume. It does not connect the WPF Standard Scan command, preview, payload recovery, Deep Scan, or FAT/exFAT scanning to live storage.

## Privilege and process model

`DataRecoveryStudio.ScanWorker` is a Windows-only x64 `WinExe` with a `requireAdministrator` manifest. It handles one authenticated scan session and exits. The main `DataRecoveryStudio.App` manifest remains `asInvoker`; normal startup performs only Phase 3 discovery and never launches the worker. Build output copies the worker executable, dependency manifest, and runtime configuration beside the main application so the parent resolves one deterministic installation-relative filename. The resolver canonicalizes the path and rejects missing files, directories, device files, temporary-directory locations, and any file or parent directory that is a reparse point. The launcher uses Windows `runas` directly with `ProcessStartInfo.ArgumentList`; it never searches `PATH` or invokes a command shell.

The worker is not code-signed by this repository. Packaging must sign and verify both executables before claiming publisher verification. Current trust is limited to deterministic path validation and protected installation-directory permissions.

## Authorization and revalidation

`LiveScanTargetGrantAuthority` holds the current Phase 3 discovery snapshot in memory. It issues a short-lived immutable grant only for the exact `StorageDevice` instance in that snapshot. A refresh advances the generation, invalidating older grants. Grants contain a random 256-bit nonce, normalized non-sensitive volume identity, physical-identity set, capacity, mount display, NTFS status, and canonical volume GUID path. A grant is consumed once and is never persisted.

Only `\\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}` is accepted as worker authority. One trailing separator is normalized away. Drive letters, physical disks, UNC, `GLOBALROOT`, `HarddiskVolume`, ordinary paths, path components, traversal, control characters, empty GUIDs, and alternate namespaces fail closed.

Before opening scan access, the elevated worker performs volume-only revalidation. It requires the volume to remain mounted, local, online, NTFS, capacity/volume-identity equal, and mapped to exactly the same current disk-number extent set captured by Phase 3. The opaque stable physical-identity set remains in the grant, while the current extent set proves that the same selected mapping is still present during this single process lifetime. Revalidation opens only the volume with the Phase 3 zero-access metadata API to issue the allow-listed `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`; it never opens `PhysicalDriveN`.

## Native read boundary

The worker's dedicated scan-data opener calls `CreateFileW` once with:

- desired access `GENERIC_READ` (`0x80000000`) only;
- share mode `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE` (`7`);
- creation disposition `OPEN_EXISTING` (`3`);
- `FILE_FLAG_OVERLAPPED` for cancellable random access;
- no write, append, delete, locking, dismount, or modifying control operation.

This API returns a `SafeFileHandle`. `LiveVolumeRandomAccessSource` keeps the handle private, validates every checked range, limits a read to 1 MiB, loops deterministic partial reads through `RandomAccess.ReadAsync`, enforces a total byte ceiling, maps recognized removal errors, and disposes the handle on every terminal path. It exposes neither a stream nor a write method. Phase 3 zero-access metadata handles remain a separate API. The production Phase 4A/4B `NtfsMetadataScanner` consumes this source unchanged.

## IPC and result boundary

The parent creates a randomized single-instance named pipe with `CurrentUserOnly`; no listener uses TCP or HTTP. The worker receives only pipe name, session ID, nonce, and protocol version on its safely separated argument list. It sends an authenticated hello before the parent transmits the target grant. Every subsequent message carries protocol version and session ID.

Messages are 32-bit length-prefixed JSON with a 1 MiB maximum, maximum depth 16, unknown fields rejected, no polymorphic activation, no type names, and explicit enum validation. Candidate batches are at most 256 records, diagnostic batches at most 128, and aggregate counts are bounded. Progress must be monotonic and is throttled to at most four routine updates per second. The command surface is exactly handshake, handshake acceptance, start scan, progress, candidate batch, diagnostic batch, cancel, and one terminal result. There is no arbitrary-offset read, raw-byte response, destination write, recovery, file browsing, or process command.

Candidates contain normalized metadata summaries only. Resident bytes, boot sectors, MFT records, bitmap blocks, payload bytes, native handles, and data runs do not cross IPC.

## Consistency and lifecycle

The worker fingerprints the validated boot geometry and record-zero MFT bootstrap before enumeration and reads them again afterward. Changed boot geometry, unreadable second bootstrap, or changed MFT bootstrap produces `ChangedDuringScan` and a partial result. An unchanged result is still labeled `LiveBestEffort`; it is not a point-in-time snapshot. Record-sequence and structural diagnostics continue to come from the production NTFS parser. Phase 5A does not use VSS, lock, or dismount the volume.

Parent requests can only lower worker hard limits: 1 GiB bytes, 1,000,000 MFT records, 100,000 candidates, 1,000 diagnostics, 256 candidates per batch, 30 minutes total, and two minutes idle. Progress uses a bounded dropping channel; result batches use pipe backpressure. Cancellation is idempotent. Pipe loss cancels the scan, the source is disposed, and the worker exits. The parent first requests graceful cancellation, then may terminate only the exact process it launched after a bounded grace period. UAC denial, missing worker, version mismatch, startup failure, crash, connection failure, timeout, removal, cancellation, partial result, and changed-live-filesystem results are distinct sanitized outcomes.

## Opt-in integration

The integration test is skipped unless both variables are explicitly supplied:

```text
DATA_RECOVERY_STUDIO_RUN_LIVE_SCAN_INTEGRATION=1
DATA_RECOVERY_STUDIO_LIVE_SCAN_VOLUME=<volume GUID or current discovered mount path>
```

The target is resolved through a fresh Phase 3 snapshot, must be mounted NTFS, and is scanned with an 8 MiB/64-record/64-candidate budget. It does not print discovered names, recover content, or write to the source. Interactive UAC availability is not bypassed.

## Known limitations

- No production WPF command invokes this service.
- No live preview or live deleted-file payload recovery exists.
- Results are live best-effort, never snapshot-consistent.
- Code-signature verification awaits a signed packaging pipeline.
- FAT, FAT32, exFAT, RAW, physical disks, BitLocker-unavailable volumes, compressed/encrypted/sparse recovery, Deep Scan, VSS, services, and drivers remain out of scope.
