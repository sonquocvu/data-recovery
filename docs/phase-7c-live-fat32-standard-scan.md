# Phase 7C: gated live FAT32 Standard Scan

Phase 7C routes an eligible mounted FAT32 volume through the existing Phase 5A one-shot elevated worker and Phase 5B WPF state machine. It performs metadata discovery only. It does not preview payload, recover files, invoke Phase 7B, traverse deleted directories, provide snapshot consistency, or support exFAT/FAT12/FAT16.

## Feature gate and authority

`DATA_RECOVERY_STUDIO_ENABLE_LIVE_FAT32_STANDARD_SCAN=1` is an exact, disabled-by-default, process-environment opt-in. It is independent of `DATA_RECOVERY_STUDIO_ENABLE_LIVE_STANDARD_SCAN`, is never persisted, and creates no worker/UAC/live handle while disabled. The application never falls back to mock data after a live failure.

The grant authority accepts only the exact selected object from the current Phase 3 snapshot. FAT32 grants require one connected, mounted, local, supported FAT32 volume with a canonical volume GUID, physical identity set, and mapped disk extents. Grants are memory-only, expire, carry a random nonce and correlation ID, bind the scanner/filesystem/canonical volume/capacity/discovery generation/physical identities/disk-number set, and are consumed once. A discovery refresh invalidates an outstanding generation.

## Protocol and worker

Phase 7C introduced protocol version 2 and worker version `7C.1` with strict `LiveScanScannerKind` binding. Phase 7D advances these to protocol 3 and worker `7D.1` to carry scanner-version, bounded consistency, and cleanup evidence. Unknown scanner values still use the non-routable zero value and fail closed. Scanner kind is checked in the launch arguments, authenticated handshake, start request, grant, progress, candidate stream, and terminal result. Cross-session, cross-filesystem, duplicate-ID, malformed-enum, count, sequence, and string-bound violations terminate the protocol.

The worker re-enumerates the exact canonical volume, mount/local status, Windows filesystem, exact capacity, volume identity, and extent disk-number set before opening. Capacity policy is exact equality; no tolerance can silently retarget the grant. The worker does not accept a drive letter, physical-drive path, UNC path, or arbitrary raw path.

## Read and scan boundary

Both scanners use `LiveVolumeRandomAccessSource` and the existing exact open tuple: `GENERIC_READ`, `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `OPEN_EXISTING`, `FILE_FLAG_OVERLAPPED`, and `SafeFileHandle`. Phase 7C adds no native open, write API, modifying IOCTL/FSCTL, lock, dismount, VSS, destination, or recovery command.

The worker invokes the existing Phase 7A `Fat32MetadataScanner` with stricter worker-clamped budgets. The parser derives FAT32 classification from calculated geometry, so an external FAT32 label cannot route FAT12/FAT16 geometry. It follows active directory trees, reads directory/FAT metadata only, and does not read deleted candidate payload clusters.

## Live consistency and normalized results

Before and after scanning, the worker captures bounded evidence for critical geometry, selected/mirrored FAT state, primary/declared-backup boot relationship, root start cluster, the root directory's first cluster, and the root FAT entry. Any observed difference yields `ChangedDuringScan` and partial consistency. Candidate allocation becomes `AllocationUnknown` and recoverability becomes `Unknown`, preventing a stale optimistic classification. An unchanged scan remains `LiveBestEffort`, never a snapshot.

FAT32 IPC candidates contain only an opaque derived ID, session/scanner/filesystem, bounded display name and path, kind, size, valid timestamps, name/path/allocation states, attribute byte, recoverability, and bounded diagnostic codes. They contain no payload, directory slot, FAT page, cluster/chain, source offset, handle, serial, stack trace, or Phase 7B provenance.

## WPF behavior

The shared Phase 5B workflow handles validation, UAC, worker connection, scan, cancellation, source removal, result transfer, partial completion, timeout, and failure. FAT32 uses indeterminate progress when total work is unknown and shows directory clusters/entries, FAT entries, candidates, elapsed time, budget state, read-only status, and live best-effort disclosure. Result rows expose conservative name/path/allocation wording and metadata timestamps/attributes. Probable LFNs remain labeled probable.

Content preview remains metadata-only. `CanRecover` is always false for live sessions, selected live FAT32 candidates produce no recovery request, and Phase 7B remains regular-image-only. Deep Scan and exFAT remain unavailable.

## Controlled integration

The Windows integration test requires both:

```text
DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION=1
DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME=\\?\Volume{canonical-guid}
```

The target must resolve through the current Phase 3 snapshot and match FAT32 exactly. Drive letters and physical-drive paths are refused. The test uses small budgets and never formats, selects, repairs, writes, previews, or recovers. Without both variables it skips with:

`Set DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION=1 and DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME to run the elevated read-only live FAT32 metadata scan.`

No controlled hardware run is claimed by automated default verification; therefore the feature remains disabled by default.

## Formats and signatures

Phase 7C introduces no recovery format or file signature. It reuses the documented Phase 7A FAT32 BPB, boot signature, FSInfo, directory-entry, LFN, and 28-bit FAT parsing rules.
