# Phase 4C: verified NTFS image recovery

Phase 4C is a headless, development/test-only content-recovery engine for ordinary NTFS disk-image files. It is not composed into the WPF device, scan, results, preview, or recovery workflow. It cannot accept a Phase 3 `Volume`, a device namespace, a caller-supplied run list, or a raw LCN range.

## Trust and source provenance

`ImageRecoveryService` creates a recovery session only after a completed production `NtfsMetadataScanner` scan. The Application layer retains the session in process and exposes random candidate IDs. Recovery plans contain those IDs and descriptive metadata only; they do not contain physical extents.

The retained provenance includes the session ID, canonical image path, image length and UTC last-write ticks, volume offset and validated volume length, boot-geometry SHA-256 fingerprint, MFT record and sequence, selected unnamed `$DATA` attribute identity, stream name, and a SHA-256 fingerprint of normalized candidate metadata. Unknown candidates, cross-session IDs, disposed sessions, and partial/invalid scans are rejected.

Before payload access, Infrastructure reopens the image through `RegularFileRandomAccessSourceFactory`, which rejects device/extended namespaces and ordinary-file reparse points. Its read handle uses `FileAccess.Read`, `FileMode.Open`, and `FileShare.Read`, preventing newly opened writers or replacement while recovery owns the handle. Length, timestamp, canonical identity, boot geometry, declared volume length, record sequence, deleted state, attribute identity, sizes, flags, extents, and the complete candidate fingerprint are revalidated. The production parser applies USA fixups and resolves `$ATTRIBUTE_LIST` records again. Freshly resolved runs—not scan-time caller data—are used for extraction.

Metadata is checked before atomic publication and after destination verification. A mismatch produces `SourceChanged`; stale run lists are never used. Length/timestamp/geometry changes that invalidate the session produce a fatal source outcome.

## Supported NTFS content

Phase 4C introduces no new on-disk signature or recovery format. It consumes the NTFS `$DATA` (`0x80`) structures documented by Phases 4A and 4B.

Supported primary content is the unnamed stream in these forms:

- structurally valid resident data, including zero-length data;
- uncompressed, unencrypted, non-sparse non-resident data;
- contiguous or fragmented runs, including negative signed LCN deltas and reads crossing clusters/extents.

Resident bytes remain internal to the parser/recovery boundary and are not placed in UI models. Non-resident content is streamed with a reusable bounded buffer. Physical ranges are checked against both the NTFS volume and backing image. Only logical bytes are written. Bytes from initialized size to logical EOF are zero-filled: NTFS defines that range as logically zero even though allocated clusters may contain unrelated stale bytes. Allocated padding after logical EOF is never copied.

Compressed, encrypted, sparse, conflicting, incomplete, or damaged layouts are blocked explicitly. Named alternate data streams remain visible as scan metadata but are not exported and never become `filename:stream` paths.

## Allocation policy

Default extraction allows `ResidentDataAvailable`, `ZeroLength`, and `PossiblyRecoverable`. `PartiallyOverwritten`, `Overwritten`, and `Unknown` require separate explicit policy flags and successful attempts retain an allocation warning. `MetadataOnly`, `UnsupportedLayout`, and `DamagedMetadata` are blocked. Allocation evidence is never presented as proof of file integrity.

## Destination and path safety

The destination must be an existing ordinary directory unless explicit creation is requested. It cannot equal the source image, use a device namespace, be a file, or contain an existing reparse-point component. Capacity is estimated through the destination drive and writability is checked with a bounded create-new probe.

`Flat` is the default output layout. `OriginalFoldersWhenSafe` is opt-in. Every metadata-derived component is Unicode Form C normalized, has separators, colons, controls, and Windows-invalid characters replaced, loses trailing dots/spaces, and is bounded. Empty, dot, and dot-dot components receive stable fallbacks. DOS device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, and `LPT1`–`LPT9`) are prefixed safely. Long components use a deterministic hash suffix. Combined paths are normalized and checked with Windows case-insensitive containment beneath the validated root. Existing and newly created directory components are rechecked for reparse points immediately before file creation and publication.

## Atomic writing and verification

Each item uses a collision-resistant `.partial` file in the final directory with `FileMode.CreateNew`. Source and write-path SHA-256 values and byte counts are updated while streaming. The partial is flushed to disk and closed, then moved on the same filesystem to a deterministic collision candidate (`name`, `name (1)`, and so on) with overwrite disabled. A concurrent collision advances to the next bounded candidate. Existing files are never replaced.

The published file is reopened read-only and SHA-256 hashed. Success requires expected logical byte count, source/write-path hash equality, destination hash equality, and destination length equality. `RecoveredAndVerified` means only that published bytes equal bytes read or logically synthesized during this operation; it does not prove that deleted content is original or semantically intact.

Cancellation and normal failures delete partial files. A failure after publication deletes the unverified final file. Cleanup failure is a separate non-success outcome. Batch processing is sequential, deduplicates candidate IDs in request order, keeps handles and buffers bounded, continues after isolated item failures, and stops on cancellation or fatal session/source invalidation. Progress counts and bytes are monotonic, and final completion is published once.

## Fixture and test strategy

`NtfsTestFixtureBuilder` generates resident values and deterministic non-resident payload clusters in memory. Phase 4C tests write only explicitly created temporary `.img` files and recover only below explicitly created temporary destination roots. Coverage includes resident/zero-length data, fragmented and negative-delta runs, initialized tails, allocation opt-ins, named ADS blocking, source timestamp and restored-timestamp sequence mutations, safe names, collisions, destination creation policy, cancellation cleanup, mixed batches, progress monotonicity, hashes, source preservation, and device-path rejection.

## Production-code safety audit

The Phase 4C audit found no new P/Invoke, `ReadFile`, `WriteFile`, `GENERIC_READ`, `GENERIC_WRITE`, volume lock/dismount, writable source stream, source-image write, or destination overwrite mode. Recovery source access is the existing managed regular-file reader (`FileMode.Open`, `FileAccess.Read`). Recovery's `FileAccess.Write` matches are limited to the harmless destination writability probe and create-new partial output. The recovery move explicitly sets `overwrite: false`; recovery deletes only its exact probe, partial, or unverified published path.

Existing `CreateFileW`, physical-drive strings, and storage P/Invoke declarations remain confined to Phase 3 discovery. `MetadataOpenOptions.ReadOnlyMetadata` fixes desired access at `0` and permits only metadata `DeviceIoControl` queries; it is not referenced by Phase 4C. Existing `FileMode.Create`/overwrite matches belong to the user settings store's atomic settings-file replacement, not recovery. The WPF composition has no `ImageRecoveryService`, `NtfsImageRecoveryEngine`, or `IImageRecoveryService` reference. The application manifest remains `asInvoker`.

## Limitations

There is no physical-disk or live-volume recovery, WPF integration, preview, compressed/encrypted/sparse recovery, ADS export, FAT/exFAT support, ACL/owner/EFS restoration, original timestamp restoration, bad-sector retry policy, or guarantee of intact original content. The ordinary-file source identity uses canonical path, length, timestamp, and boot/metadata fingerprints rather than a full-image pre-scan hash; the end-to-end fixture test separately proves its controlled source image is byte-for-byte unchanged.
