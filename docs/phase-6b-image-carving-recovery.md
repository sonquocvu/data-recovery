# Phase 6B: trusted image carving recovery

Phase 6B exports contiguous bytes only from candidates retained in a live, completed Phase 6A image scan session. It is a headless regular-file workflow. It is not connected to WPF, live disks, physical drives, the elevated worker, previews, or NTFS record recovery.

## Trust and architecture

The public recovery request contains a scan-session ID, opaque candidate IDs, a destination root, and a bounded policy. It has no source path, offset, length, format, extension, validator, confidence, or evidence fields. Application orchestration resolves IDs against an in-memory session and constructs trusted provenance through internal constructors. A stable candidate ID without its live session grants no recovery authority.

Trusted provenance binds the session and candidate IDs to the canonical source path, source length, last-write timestamp, whole-source SHA-256, original scan-range identity and extent, candidate offset and length, format and registry extension, validator version, validation/length confidence, completeness, structural state, evidence fingerprint, range kind, and overlap state. Partial/invalid Phase 6A scans cannot authorize a recovery plan. Unknown and disposed sessions and unknown candidates are rejected.

Core owns immutable request, policy, plan, progress, result, diagnostic, and outcome models. `ImageDeepScanService` owns session and plan authorization. Infrastructure owns format revalidation and byte copying. `DeepScanImageRecoveryEngine` and the NTFS recovery engine use the same `RecoveryDestination` and `RecoveryFilePublication` components, while their metadata and content-reading strategies remain distinct.

## Eligibility

Default recovery requires all of the following:

- High validation and length confidence.
- `Complete` and `StructurallyValidated` states.
- A non-negative exact length wholly contained by the original scan range and source.
- A format and validator version present in the trusted fixed registry.
- No unresolved overlap unless overlap recovery is explicitly enabled.
- Per-candidate and batch byte budgets.

JPEG, PNG, GIF87a/89a, bounded PDF with a selected credible EOF, and ordinary non-ZIP64 ZIP candidates can meet those rules. Before any output is created, the current Phase 6A validator reruns with hard limits and its length, confidence, completeness, state, and structural-evidence fingerprint must exactly match scan provenance.

An advanced `AllowMediumTruncated` option applies only to a Medium, `Truncated`, `StructurallyPlausible` candidate with a bounded length produced by validation at the original scan-range end. Its flat output name contains `.partial`, its outcome is `CarvedPartialWithWarning`, and it is never described as verified-complete. Low-confidence, length-unknown, corrupt, invalid, unsupported, and arbitrary-range content remains blocked. There is no generic recover-anyway bypass.

## Contiguous copy and verification

The engine reads exactly `[trusted offset, trusted offset + trusted length)` through `IReadOnlyRandomAccessSource` using one bounded pooled buffer. Arithmetic and range containment are checked. It neither follows references nor transforms, decodes, decompresses, renders, extracts, fills gaps, removes bytes, nor appends trailing bytes.

Source bytes are SHA-256 hashed while read. The write path hashes the same streamed bytes. After a flush-to-disk and atomic publication, the final file is reopened, byte-counted, and independently SHA-256 hashed under a verification-byte budget. Success requires the trusted byte count and all hashes to match. This proves copy fidelity for the selected contiguous extent only; it does not prove original semantic integrity, usability, or malware safety.

Deep Scan carving cannot reconstruct fragmented content. A structurally valid candidate can still contain stale or semantically damaged content.

## Destination and names

Names are derived only from trusted format and image-relative offset:

`Carved_<FORMAT>_<16-digit uppercase hexadecimal offset><extension>`

Partial outputs insert `.partial` before the registry extension. Embedded ZIP filenames, PDF metadata, and source-controlled text never affect output paths. The shared Windows sanitizer, component bounds, reserved-name handling, containment checks, root/child reparse rejection, capacity preflight, and deterministic collision suffixes apply. Output is always flat.

A uniquely named hidden `.partial` file is created with `FileMode.CreateNew` in the final directory. It is flushed before an atomic, no-overwrite move. Existing files are never replaced; repeated recovery uses ` (1)`, ` (2)`, and so on within a hard attempt limit. Cancellation or failure deletes only the exact temporary or just-published file created for that item. Cleanup failure is a distinct result.

## Batch, cancellation, and limits

Planning deduplicates candidate IDs and sorts by source offset and ID. Recovery is sequential and continues after isolated item failures. A source consistency failure stops the batch. Already verified files may remain if a later item is canceled; the canceled batch reports them separately.

Hard limits cover candidates per batch, bytes per candidate, total bytes, source reads, elapsed duration, buffer size, collision attempts, diagnostics, post-write verification bytes, and exact cleanup attempts. Application policy values may lower but cannot raise the fixed Phase 6B envelope. Progress reports current candidate/name, completed/total items, and monotonic bytes. Cancellation is checked before revalidation, source reads, writes, flush/publication, post-write verification, between items, and terminal publication.

## Source consistency and safety boundary

The source is reopened through `RegularFileRandomAccessSourceFactory`, which rejects device namespaces, directories, devices, and reparse points. Canonical identity, length, timestamp, and SHA-256 must match scan provenance before recovery, before publication, and after verification. The read-only file handle uses sharing that prevents ordinary write/replacement while it is open. A mismatch returns `SourceChanged` and removes in-flight/published output.

The Phase 6B production path adds no raw-device P/Invoke, writable source stream, live-worker call, decompression, image decoding, PDF execution, WPF command, destination dialog, preview, or original-path reconstruction. The main application remains `asInvoker`, and the Phase 5B live Standard Scan gate is unchanged.

## Remaining limitations

Phase 6B does not provide live Deep Scan, live carving, fragmented carving, missing-fragment inference, original filenames/folders/timestamps, FAT/exFAT parsing, ZIP extraction, preview, malware analysis, or guarantees of semantic file integrity. ZIP64 and multi-disk ZIP remain unsupported.
