# Phase 8A: headless exFAT image metadata scanning

This document records the Phase 8A scanning scope. [Phase 8B](phase-8b-exfat-image-recovery.md) subsequently adds headless image recovery from these sessions, stable Windows file IDs, and exact shared source reads without managed read-ahead. The metadata parser still reads no file payload.

Phase 8A discovers deleted regular-file metadata in ordinary exFAT image files. It has no recovery, preview, carving, live-volume, worker, device-discovery, or WPF route. Phase 7D hardware validation remains deferred and its existing release-readiness evidence is unchanged. Both live NTFS and FAT32 feature gates remain disabled by default.

## Components and use

`Core/ExFatScanModels.cs` defines immutable filesystem-specific requests, budgets, geometry, boot evidence, timestamp/name/path/allocation states, metadata-only candidates/results, and sessions. Physical provenance is internal. Requests accept a volume offset and lowered budgets; callers cannot submit bitmap addresses, clusters, chains, candidate lengths, or allocation overrides.

`Application/ExFatImageScanService.cs` orchestrates source fingerprinting, the scanner, cancellation, consistency checks, and a private concurrent trusted-session registry. It does not reference Infrastructure. Production composition is explicit and headless:

```csharp
var service = new ExFatImageScanService(
    new ExFatImageSourceFactory(),
    new ExFatMetadataScanner(),
    new ExFatSourceMetadataProvider());
var scan = await service.ScanAsync(imagePath, new ExFatScanRequest(), progress, token);
// scan.Result always describes metadata. scan.Session is null unless trust publication succeeded.
```

Infrastructure owns `ExFatImageSource.cs`, `ExFatStructures.cs`, and `ExFatMetadataScanner.cs`. Dependencies are supplied by a trusted composition root, following the other headless image services. The lower-level random-access scanner accepts memory sources for deterministic tests; these are not live scan routes. App and ScanWorker contain no exFAT registrations or new references.

## Source boundary and consistency

The exFAT factory rejects device namespaces, volume/device aliases, GLOBALROOT, PhysicalDrive/HarddiskVolume forms, roots, missing files, directories, alternate streams, device/reparse attributes, and junction/symlink ancestors before calling the existing regular-file opener. Named pipes through device namespaces are rejected before opening. Production uses exactly `FileMode.Open`, `FileAccess.Read`, `FileShare.Read`, asynchronous random-access options. No source-writing API is introduced.

One read-only source handle stays open through both fingerprint passes and metadata scanning. Each fingerprint binds canonical path, length, last-write UTC ticks, and full SHA-256; the provider checks path metadata against the open source before and after hashing. Completed sessions additionally bind volume offset, validated geometry fingerprint, independent boot-region evidence, scanner version, and immutable candidates with internal provenance. `GetSessionAsync` fingerprints again and removes stale/unreadable sessions; `DisposeSession` removes authorization.

**Full-source hashing reads every image byte.** As explicitly agreed for this phase, before/after fingerprint passes are audited separately from scanner metadata reads. Their bytes count toward the same total source-byte and duration budgets and are reported separately in progress. No fingerprint bytes become candidates, previews, recovery data, or retained payload. The metadata scanner issues reads only for bootstrap metadata, FAT entries, active directories, root-discovered bitmap bytes, and the up-case stream. Candidate assessment never issues a read against a candidate's stream layout. A deleted entry pointing to currently active metadata is an ownership conflict, not authorization to read it as deleted content.

File sharing excludes ordinary concurrent writers. Before/after checks detect observed changes, but do not claim to prove absence of an adversarial transient change-and-restore that bypasses operating-system sharing. Changed, canceled, partial, or invalid scans publish no trusted session. A public record clone cannot preserve the production result's identity attestation. The registry retains only the actual trusted result, not a caller-submitted candidate catalog.

## Structures, signatures, and boot policy

This phase adds an exFAT filesystem metadata parser, **not a recovery format or content signature**. It recognizes the filesystem-name bytes `EXFAT   ` at boot offset 3 and jump bytes `EB 76 90`. It parses both 12-sector boot regions, the eight extended boot sectors, opaque OEM parameter records/reserved tail, reserved boot sector, and repeated checksum sector. Main boot signature is `55 AA` at byte 510; extended boot signatures are little-endian `0xAA550000` at the end of each extended sector. Reserved/required-zero ranges are checked under this conservative profile.

Boot checksums rotate right one bit and add each byte modulo 2^32 over sectors 0–10, excluding offsets 106, 107, and 112. Every 32-bit word of sector 11 must match. Mutable flags and percent-in-use therefore do not need a recomputed checksum. Percent-in-use is advisory, including an explicit advisory diagnostic for invalid bounds; it never determines allocation.

Each boot region must independently validate identity, signatures, revision 1.00, shifts, flags, FAT count, region ordering, FAT capacity, cluster count/domain, root cluster, and checked image/volume bounds. Logical sectors 512/1024/2048/4096 and power-of-two clusters through 32 MiB are supported. Volumes must be at least 1 MiB. PartitionOffset and serial/DriveSelect are metadata; PartitionOffset is not added a second time to the caller's image volume offset. Serial disagreement is advisory and does not alter geometry authority.

The main region wins when valid. With an invalid main, backup discovery probes the four specified sector-size positions and requires an independently valid backup; multiple valid backup interpretations fail closed. No fields are merged. Independently valid main/backup critical-geometry disagreement fails before root traversal. Backup-only discovery is explicitly partial: backup mutable flags are stale, so this version does not publish a completed session or optimistic allocation labels from fallback alone. Dirty/media-failure flags likewise reduce the scan to partial/unknown evidence.

Only one-FAT, revision-1.00 layouts are supported. Two-FAT/TexFAT layouts are rejected, including active-FAT switching/transaction replay. Invalid active-FAT selection on a single-FAT image is rejected. Supporting two FATs would require a separately reviewed TexFAT consistency policy.

## FAT, bitmap, and up-case metadata

FAT reads are exact four-byte little-endian metadata reads. FAT header entries are checked. Traversal distinguishes zero/missing links, valid next clusters, bad `0xFFFFFFF7`, reserved/invalid values, and the sole exFAT terminal value `0xFFFFFFFF`. It rejects premature/excess chains, domain violations, and cycles. Contiguous streams do not consult stale FAT links. Chains are bounded before materialization; the FAT is never automatically loaded in full.

Root entries `0x81` and `0x82` are the only authority for allocation-bitmap and up-case locations. Duplicate, conflicting, malformed, unreadable, or invalid-length metadata invalidates the relevant evidence. Both streams require valid FAT layouts and are added to active ownership before use. `0x83` volume labels are validated as UTF-16 metadata; `0xA0` volume GUID entries have validated set checksums and structure. Unsupported critical/TexFAT/benign allocations cause partial discovery rather than being silently assumed irrelevant.

Bitmap length must equal ceil(ClusterCount / 8). Cluster 2 is bit 0 of byte 0; least-significant bits come first. The scanner queries bytes on demand through the validated bitmap stream. Missing/out-of-range/failed coverage is never free. Active metadata/stream ownership must agree with allocated bitmap bits; disagreement makes ownership incomplete. A missing FAT link in a bitmap-allocated candidate is explicit FAT/bitmap disagreement. Preserved FAT links in bitmap-free deleted clusters are stale layout evidence, not allocation authority.

FAT and bitmap caches use fixed direct-mapped arrays and evict on collisions. Key and value storage both count against configured byte limits (plus fixed CLR array headers); a budget smaller than one entry disables caching. No dictionary resize can exceed the configured cache storage. A bitmap query reads exactly one byte and never eagerly loads a bitmap.

The up-case stream checksum is validated over its stored representation. Both uncompressed and identity-run-compressed tables must expand to exactly 65,536 mappings; runs cannot overrun, be zero-length, or leave coverage incomplete. Mandatory ASCII mappings are checked. Name hashes rotate/add both bytes of each code unit after lookup in that table. Host Unicode case conversion is never substituted. Missing or invalid up-case evidence reduces name confidence and results in partial discovery.

## Entry sets, names, timestamps, and paths

The reader traverses active directories breadth first, with bounded 512-byte reads and 32-byte slots. It carries entry sets across buffers and nonadjacent clusters, respects end markers, and supports FAT chains, contiguous directories, nested directories, and empty directories. Deleted directories are never queued. Active file payload is never read. Directory cycles and overlaps are isolated using visited starts and active cluster ownership.

File primary `0x85`, stream `0xC0`, and filename `0xC1` entries use ordinary active checksums. Deleted candidates require coherent cleared forms `0x05`, `0x40`, `0x41`. The parser validates secondary count, exactly one initial stream extension, exact required filename-slot count, permissible benign nonallocating secondaries, flags, size/valid-length/first-cluster relationships, attributes, UTF-16, illegal characters, and unused filename padding. Orphans, mixed in-use structures, unknown critical secondaries, nulls, malformed surrogate pairs, conflicting streams, and integer overflows cannot create candidates. Active checksum/name-hash defects invalidate ownership completeness; active names are compared using the on-disk table for duplicate detection.

Deleted set evidence distinguishes ordinary checksum matches from matches obtained by restoring entry-type in-use bits. Verified/hash-matched structures produce `VerifiedDeletedName` or `ChecksumRecoveredName` respectively. A matching hash without checksum gives `HashVerifiedName`, never trusted stream metadata. Missing up-case with checksum gives `ProbableName`; mismatches give `AmbiguousName`. Incoherent names are rejected rather than inventing a filename. Reserved damaged/fallback states allow future extensions without claiming certainty today.

Original validated UTF-16 names remain in internal provenance. Display formatting replaces Unicode format controls and does not generate destination filenames. Paths through uncertain active names carry `NameUncertain`; partial results mark candidates `IsPartial`. Timestamp models retain local date/time, validity, ten-millisecond increments, and signed UTC-offset minutes, including the full exFAT offset range beyond DateTimeOffset's limits. Unknown offsets are explicit and do not assume the host timezone. Bad timestamps are isolated candidate diagnostics.

## Allocation and future Phase 8B boundary

Candidate spans use checked ceil(DataLength / ClusterSize), excluding final-cluster padding from logical size. ValidDataLength stays separate; bytes beyond it are not proof of initialized data. Structurally valid empty streams have first cluster zero and no contiguous flag. Nonempty contiguous candidates require trusted checksum-backed stream fields and exact bitmap/ownership coverage. FAT candidates additionally require a complete deterministic preserved chain. No fragments are guessed and no clusters before FirstCluster are used.

Assessments distinguish zero length; contiguous or preserved-chain all-free/mixed/fully-allocated spans; active ownership conflicts; unavailable/unknown evidence; missing/cyclic/damaged chains; FAT/bitmap disagreement; and damaged metadata. Unknown or incomplete ownership never leaves an optimistic assessment. All-free spans are labeled only `AllocationSuggestsPossibleContent`; allocated/conflicting/damaged streams are blocked and unknown evidence stays unknown. Neither free allocation nor a preserved chain guarantees intact, original, unmodified content.

Candidate IDs hash source SHA-256 identity, scanner version, geometry fingerprint, volume offset, parent cluster, exact primary slot offset, entry-set fingerprint, stream fields, and name evidence in invariant formatting. Ordering is by physical primary slot. Internal provenance includes these bindings, raw validated name, and ordinary/reconstructed checksum plus name-hash evidence. Public candidate models omit physical cluster/offset/chain and all payload bytes.

The Application-owned registry is the only Phase 8A future-recovery authorization boundary. No Phase 8B engine or recovery request exists yet. Future work must resolve opaque session/candidate IDs in that registry and revalidate source and metadata; copying public display models cannot authorize recovery.

## Limits, cancellation, and complexity

Every numeric limit may be lowered but not raised above the defaults below. Invalid configurations are rejected. One diagnostic and one terminal callback slot are mandatory.

| Resource | Hard/default ceiling |
| --- | ---: |
| Total bytes, including both full fingerprint passes | 8 GiB |
| Entire service duration | 4 hours |
| Directories / depth | 100,000 / 128 |
| Directory bytes / entries | 256 MiB / 2,000,000 |
| FAT queries / key-value cache | 4,000,000 / 256 KiB |
| Chains / individual chain length / cumulative clusters | 200,000 / 262,144 / 1,000,000 |
| Bitmap queries / key-value cache | 4,000,000 / 64 KiB |
| Encoded up-case bytes / expanded lookup storage | 128 KiB / 128 KiB |
| Entry-set secondaries / filename code units | 255 / 255 |
| Total filename code units | 4,000,000 |
| Individual path / total materialized path code units | 32,768 / 4,000,000 |
| Active stream and cluster ownership records | 1,000,000 |
| Candidates / diagnostics / progress callbacks | 100,000 / 1,000 / 1,000 |

The full-image service can complete only if both hashes plus metadata fit the byte ceiling (thus an image near 4 GiB already consumes the budget). Read budgets reserve the entire requested range before an exact read, including failed reads; progress counts only completed reads. This is an intentional bounded profile, not an unrestricted large-image promise. Standalone metadata scans do not hash payload and use the same metadata limits. Reduced up-case storage below a full lookup yields an explicit budget-limited partial result.

Cancellation is checked around reads and during boot work, chain/slot/set traversal, bitmap/name/ownership work, candidate publication, and session publication. Linked deadline tokens interrupt cancellable blocked reads. Progress contains actual completed-read counters, separate fingerprint bytes, directory/entry/FAT/bitmap/set/candidate/diagnostic counts, elapsed time, and budget state; it contains no ETA. A failed exact read may have transferred an unobservable prefix inside the source abstraction, which is not counted as a completed read. Callback exhaustion throttles progress, reserving exactly one terminal report. Other budget exhaustion returns explicit partial diagnostics; diagnostic exhaustion replaces the last retained diagnostic with the budget reason.

Global invalid boot geometry stops safely. Local corruption is diagnosed and isolated where subsequent directory metadata remains trustworthy. Incomplete scans may retain already-published validated metadata, downgrade optimistic allocations, and never publish trusted sessions. Some early budget/cancellation exits occur before candidate assessment and therefore return no candidates.

Work is O(image bytes for two hashes + bounded directory entries + bounded chain/FAT/bitmap queries + 65,536 up-case mappings). Memory is O(bounded ownership/chain records + retained candidates/names/paths + fixed caches), not O(volume or FAT size). A directory read buffer is at most 512 bytes; one entry set at most 8 KiB; up-case encoded and decoded storage are separately bounded. Managed collection/object overhead remains proportional to explicit record ceilings. Session memory is retained until its owner calls `DisposeSession`.

## Tests and verification

`ExFatTestFixtureBuilder` creates deterministic ordinary images with independent checksum implementations, all four sector sizes, variable clusters, FAT/contiguous files and directories, boundary-spanning sets, bitmap states, and controlled corruptions. No external or licensed image is used. These are synthetic filesystem fixtures, not evidence of hardware validation.

`Phase8AExFatScanTests` includes an end-to-end production Application service scan with five deleted candidates (empty, free contiguous, preserved chain, ambiguous name, active allocation conflict), active nested content and excluded deleted directories. An instrumented source asserts that **every scanner read** is disjoint from fixture payload ranges, including active file payload; fingerprint passes are separately counted. It verifies stable IDs/order, SHA-256/length/timestamp equality, exactly one file in the source directory, and trusted publication only for a completed unchanged source. TRX output records the exact fingerprint and audit counters. Separate tests exercise consistency/cancellation faults and cloned-result rejection; injected faults are labeled as test doubles.

Run the solution restore, format, format verification, Release build, full tests, and the focused `FullyQualifiedName~Phase8A` suite. Project/source/manifest tests guard the absence of new App/worker/native/write dependencies. A separate Release startup smoke and Windows Application-event inspection supplement the automated WPF tests. Keep controlled Phase 7D hardware tests skipped unless their original explicit hardware gates are satisfied.

On-disk field interpretation was checked against the [Microsoft exFAT specification](https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification). The policies and limits above describe this implementation's conservative supported subset.

## Deliberate exclusions

No live exFAT, exFAT recovery/preview, Deep Scan, deleted-directory traversal, FAT12/16, TexFAT replay, filesystem repair/formatting, partition discovery, BitLocker, worker integration, or WPF routing. Unsupported metadata allocation types, stale backup flags, and unavailable required metadata prevent trusted completion. Allocation evidence never establishes content integrity.
