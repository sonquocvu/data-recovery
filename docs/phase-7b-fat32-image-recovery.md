# FAT32 image recovery (Phase 7B)

Phase 7B is a production, headless recovery path for deleted regular-file candidates produced by a completed Phase 7A scan of an ordinary FAT32 image file. It is not connected to WPF, the elevated scan worker, live volumes, physical disks, Deep Scan, exFAT, FAT12, or FAT16. It never traverses deleted directories and never guesses fragmented chains.

## Architecture and provenance

Core defines immutable recovery requests, policies, progress, plans, outcomes, and trusted engine contracts. `Fat32ImageScanService` in Application retains completed scan sessions, deduplicates opaque candidate IDs, applies policy, and stores non-serializable trusted plans. Infrastructure owns output-name resolution, source/candidate revalidation, active ownership analysis, cluster-plan derivation, payload streaming, and verification.

The public request contains only a scan-session ID, opaque candidate IDs, a destination root, and policy. It has no cluster, chain, source-offset, file-length, extension, name-confidence, or allocation override. Production candidate IDs include the in-process session ID, source SHA-256 identity, volume offset, parent directory cluster, exact slot offset, and kind. Internal provenance additionally binds canonical source path, length, timestamp, SHA-256, validated geometry, scanner version, parent cluster, exact directory-slot source offset, candidate metadata/name evidence, first cluster, logical size, allocation assessment, and an evidence fingerprint. Trusted plan constructors are internal and plans are held only in memory.

Unknown, disposed, stale, partial, and foreign sessions fail closed. Duplicate IDs are recovered once. FAT32 IDs are session-scoped and are not accepted by the typed NTFS or Deep Scan recovery APIs.

## Fresh validation and ownership

Before payload output, recovery reopens the source through `RegularFileRandomAccessSourceFactory`, which rejects device namespaces, devices, directories, and reparse points and opens only for reading. It verifies canonical identity, length, last-write timestamp, and full SHA-256. It reruns the production Phase 7A scanner with the trusted volume offset/session identity and requires a completed scan with the same geometry fingerprint. This rereads primary/backup boot evidence, validates FAT selection/mirroring, traverses the active directory tree, rereads the exact deleted slot, and recomputes names, first cluster, logical size, allocation, and candidate evidence. Changed source or candidate evidence is never silently replaced.

A separate bounded ownership pass follows the root and every reachable active directory chain and follows active regular-file FAT chains without reading their payload. It rejects cycles, invalid/free/bad/reserved entries, mirrored-copy disagreement, cross-links, short active chains, invalid clusters, and budget exhaustion. Ownership is a bounded dictionary/set of referenced clusters, not a whole-volume array. If analysis is incomplete, all non-empty deleted-file extraction fails closed. Any candidate plan intersecting an active file or directory returns `ActiveClusterConflict`.

## Eligibility and cluster plans

The default policy allows valid deleted zero-length regular files and `PossiblyRecoverableContiguous` files only. For a non-empty contiguous candidate, the engine computes the exact ceiling of logical size divided by cluster size, begins at the trusted first cluster, and requires every physically consecutive cluster to remain free, in range, mirrored consistently, and absent from active ownership. It reads exactly logical EOF and never copies cluster padding, searches nearby clusters, substitutes clusters, or invokes Deep Scan. The outcome states that deletion often clears FAT chain metadata and that contiguous recovery is heuristic.

Preserved allocated chains are blocked by default. Explicit `AllowPreservedFatChain` follows the freshly read FAT chain in order, including non-contiguous links. The chain must be exact-length for the logical content, end at EOC on the final required cluster, be acyclic/in-range, have no bad/reserved/copy-disagreement state, and avoid active ownership. A short chain and unexpected extra chain are both rejected conservatively.

Explicit `AllowDeterministicDamagedContent` can copy only the trusted contiguous span when every FAT entry is structurally valid and ownership-free. It does not enable arbitrary cluster substitution or fragmentation guesses. Results are always `RecoveredDamagedWithWarning`, never intact/fully recoverable. Allocation-unknown, malformed, ambiguous, or non-deterministic cases remain blocked.

## Names and destination publication

Verified and probable long names may be used after the shared Windows filename sanitizer; probable confidence remains separately reported. Ambiguous or invalid long names use `FAT32_Deleted_<DIRECTORY_CLUSTER>_<SLOT_OFFSET>.bin`. A short name with its destroyed first byte retains a visible replacement after sanitization; the missing character is never invented. Output is always flat. Path separators, invalid/control Unicode, reserved DOS names, trailing dots/spaces, and excessive component lengths are handled by the same `RecoveryDestination` implementation used by NTFS and Deep Scan recovery.

The shared destination path validates canonical containment, existing ancestors, reparse points, capacity, writability, and separation from the source image. Recovery uses an exact session-owned `CreateNew` partial, bounded asynchronous writes, flush-to-disk, and an atomic move with overwrite disabled and bounded deterministic collision suffixes. Cleanup targets only the exact partial or unverified published path; it never recursively deletes a destination. Cleanup failure is a distinct outcome.

## Copy verification and source preservation

Source bytes and bytes passed to the writer are SHA-256 hashed during streaming. The selected source plan is reread and rehashed before publication to detect in-operation content mutation. The published file is reopened and its exact byte count and SHA-256 are checked. The source canonical identity, length, timestamp, and whole-image SHA-256 are checked before opening, before publication, and after output verification. Ordinary Windows file sharing also prevents modification while the recovery reader is open.

This proves copy fidelity only for the selected recovery plan. It does not prove that a contiguous heuristic selected original clusters, that a preserved chain still belonged to the deleted entry, that content was not overwritten before scanning, that a reconstructed name is exact, or that semantic file content is valid.

## Batch, cancellation, and budgets

Batches are deterministic and sequential. Active ownership is built once per unchanged operation; each candidate is independently revalidated and isolated failures do not stop later items. Fatal source change and cancellation stop the batch. Already published verified files remain accurately reported, while the in-flight partial/unverified output is removed. Progress is monotonic in completed items and copied bytes and reports success, warning, skipped, and failed counts. Cancellation is checked before validation, throughout scanning/ownership/FAT work, between cluster reads and writes, around flush/publication/verification, between candidates, and before terminal publication.

Hard policy ceilings cover candidates, logical bytes per item and batch, source reads, clusters per item and batch, FAT entries, active files, ownership clusters, collision attempts, diagnostics, duration, buffer size, and cleanup attempts. Callers may lower but never raise these ceilings. Scanner revalidation retains its independent Phase 7A budgets.

## Deterministic proof and limitations

The generated FAT32 fixture contains known cluster payloads. Production scan-to-recovery tests verify empty SHA-256, one- and multi-cluster contiguous reads, non-contiguous preserved-chain order, exact logical EOF, no padding, safe names, no overwrite, no escaping output, no partials, and unchanged source SHA-256/length/timestamp. Additional tests cover ownership conflicts/incompleteness, bad/reserved/disagreeing FAT entries, damaged opt-in, source mutation, foreign/disposed sessions, deduplication, cancellation, budgets, progress, and WPF/worker isolation.

No new carved format or file signature is introduced by Phase 7B. FAT32 recovery relies only on Phase 7A directory/FAT metadata. Remaining limitations are image-only operation, no deleted-directory traversal, no fragmented-chain guessing after cleared FAT entries, no live FAT32 route, no exFAT/FAT12/FAT16, no preview, no semantic content validator, and no guarantee of original content integrity or exact original filenames.
