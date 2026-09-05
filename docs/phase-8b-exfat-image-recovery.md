# Phase 8B: trusted headless exFAT image recovery

This phase reconstructs deleted logical streams from ordinary Windows image files using completed Phase 8A sessions. It supports only the revision-1.00, one-FAT subset already accepted by the production scanner. There is no new filesystem parser, content signature, container format, repair operation, preview, live source, worker route, or WPF route. Phase 7D hardware validation remains deferred.

## Composition and public contract

```csharp
var service = new ExFatImageScanService(
    new ExFatImageSourceFactory(), new ExFatMetadataScanner(),
    new ExFatSourceMetadataProvider(), new ExFatImageRecoveryEngine());
var scan = await service.ScanAsync(imagePath, new ExFatScanRequest(), null, token);
if (scan.Session is null) return;
var plan = await service.CreateRecoveryPlanAsync(new ExFatRecoveryRequest(
    scan.Session.SessionId, selectedCandidateIds, destination,
    new ExFatRecoveryPolicy()), token);
var result = await service.RecoverAsync(plan.PlanId, progress, token);
service.DisposeSession(scan.Session.SessionId);
```

Requests contain only session ID, candidate IDs, destination and explicit policy. Physical offsets, chains, names, lengths and confidence overrides cannot be supplied. Application snapshots and deduplicates IDs, rejects foreign/forged IDs, and sorts by trusted entry location and candidate ID. Public plan clones cannot change the private authorization. Plans are single-use, including canceled/failed attempts; create another plan to retry. Sessions are process-local and live until explicit disposal or source/metadata invalidation, matching Phase 8A's policy. There is no time-to-live or persisted-session import. Disposing a session cancels active recovery and removes its plans.

Only the actual production scanner result can register a session. An internal sealed authorization constructed by Application reaches Infrastructure. Test dependencies are internal; the public production engine constructor selects the ordinary-image factory, production parser and full fingerprint provider.

## Provenance and batch validation

Provenance binds canonical image path, length, UTC last-write timestamp, full SHA-256, Windows volume and 128-bit file ID, volume offset, boot evidence, geometry and scanner version. Per-file evidence binds the parent directory cluster, exact primary-entry offset, complete entry-set fingerprint, deleted checksum evidence, stream flags, first cluster, `DataLength`, `ValidDataLength`, original name/hash evidence, allocation evidence and ownership assessment.

Planning performs no image reads. Recovery preflights output/cluster limits plus two full fingerprints, known metadata-read cost from the trusted scan, and selected initialized bytes. One source handle remains open through hashing, refresh, extraction and verification using `FileMode.Open`, `FileAccess.Read`, `FileShare.Read`. Handle metadata is compared with current path identity; replacement with identical bytes and restored timestamp still changes file ID. Changed bytes with restored length/time fail the full hash.

After the first matching fingerprint, the existing production `ExFatMetadataScanner` reparses directories, entry sets, FAT, bitmap and active ownership once. Its normal algorithms derive selected layouts, retained only in an internal seal bound to that exact result. Recovery requires a completed sealed result, identical boot/geometry, matching selected provenance/evidence and exact chain coverage. There is no second parser, ownership walker or FAT reader. Changed deletion state or extraction metadata cannot authorize copying. Partial/unknown refreshes fail closed.

All eligible files are staged before the second full fingerprint. Publication begins only after that hash matches. Cheap identity/length/time checks surround extraction and publication without per-file whole-image hashes. Source invalidation stops the batch and invalidates the session. Handle sharing prevents ordinary concurrent source writes and rename/replacement; these safeguards do not claim control over hostile kernel drivers or hardware.

## Eligibility and exact reconstruction

| Layout/evidence | Default | Explicit preserved-chain opt-in |
| --- | --- | --- |
| Structurally valid empty file | Allowed | Allowed |
| NoFatChain contiguous, all required clusters free, complete ownership, no conflict | Allowed | Allowed |
| Preserved deterministic FAT chain, exact coverage, valid end marker, all required clusters free, no conflict | Blocked | Allowed |
| Allocated/partly allocated, active conflict, unknown bitmap/ownership, missing/cyclic/short/overlong/damaged chain | Blocked | Blocked |
| Untrusted extraction checksum, unsupported layout, directory | Blocked/excluded | Blocked/excluded |

`AllowPreservedFatChain` defaults to false. It does not permit guessing missing fragments. The entire required cluster set must pass allocation and ownership checks even when some bytes are uninitialized. The parser enforces `0 <= ValidDataLength <= DataLength`; recovery checks it again.

Copy exactly `ValidDataLength` bytes in fresh cluster order, then synthesize zeros through `DataLength`. Reads stop at cluster boundaries and the initialized boundary. Final-cluster padding and uninitialized disk bytes are never requested for extraction. Empty files require no payload read. Checked offsets use the trusted volume offset and geometry. A short exact read is an explicit file failure, never a reason to substitute zeros. Shared regular-file streams disable managed buffering to avoid hidden read-ahead; this describes application requests, not physical device/cache behavior.

Results retain separate copied-byte and synthesized-zero-byte counts, name evidence and fallback status. `ReconstructedCopyVerified` means exact output length and SHA-256 agree with the reconstructed logical stream. It does not prove that free clusters still contain the deleted file's original content.

## Destination, publication and cleanup

Outputs are flat. Names supported by both name-hash and valid ordinary/reconstructed deletion-checksum evidence use the internal validated name. Other name evidence uses `EXFAT_Deleted_<candidate-id>.bin`. Uncertainty remains visible. Shared Windows sanitation normalizes Unicode, handles reserved DOS names including superscript COM/LPT digits, replaces invalid components, bounds lengths and resolves collisions without overwriting. Phase 8A already rejects path-separator/ADS text inside corrupt entry sets; those scans cannot obtain recovery sessions.

The engine reuses `RecoveryDestination` and `RecoveryFilePublication`: ordinary directory validation, optional explicit creation, space estimate and owned write probe; unique `CreateNew` partials; bounded asynchronous writes; async flush plus disk flush; no-overwrite publication; and independent output SHA-256/length verification. Directory ancestors are checked and held by read-only Windows handles during creation/probing. exFAT holds its own ancestor lease until publication/cleanup ends, preventing ordinary directory rename and replacement during those operations. The handles request list-directory/read-attributes access because attribute-only handles do not enforce sharing restrictions. Publication needs directory write sharing; before enabling it, the lease creates an exclusively held, delete-on-close guard under strict sharing. The guard keeps the destination nonempty and cannot be removed by another process; Windows [rejects setting a reparse point on a nonempty directory](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/fsctl-set-reparse-point). There is no gap without an ancestor lease while sharing changes. The guard disappears when the operation closes its handle. Unavailable stable identity fails closed.

An image-file destination may be on the same host volume or in the same ordinary folder as the source. The source image path cannot be a destination directory; existing filenames and hard-link aliases are collisions, never overwritten or removed. Device namespaces, ADS paths, reparse/junction ancestors and directory escapes are rejected. Live-device restrictions were not imported.

A partial is owned only after exclusive creation succeeds. Cleanup checks its captured identity, rechecks the destination and uses only the exact owned path with bounded attempts. Shared `CleanupOwned` opens the owned output for deletion, verifies its identity on that held handle and sets disposition by handle, closing the path-check/delete race. Source handles remain read-only. Cleanup never follows a replacement, deletes a preexisting failed-create target, searches by wildcard, or removes a previously verified output. Failure to safely remove an artifact reports `CleanupFailed` with the affected former/owned path. An external actor moving/replacing an output may leave a displaced artifact that the operation cannot safely locate; cleanup does not search for it.

Isolated payload, write, collision or verification failures allow later candidates to continue. Cancellation, source invalidation, exhausted global budgets and destination-boundary failure stop the batch. Unpublished owned artifacts are cleaned; earlier verified outputs survive later failures. A disk flush may have to wait for the operating system to return. Exactly one immutable terminal result is returned, with one terminal progress report when a progress sink is supplied.

## Bounds and accounting

All numeric ceilings can be lowered, not raised. Original scan-budget ceilings also constrain recovery. No configuration increases the existing 8 GiB source ceiling.

| Bound | Default/hard maximum |
| --- | --- |
| Charged source reads per recovery operation | 8 GiB |
| Total logical output / individual logical file | 4 GiB / 2 GiB |
| Unique selected candidates | 1,000 |
| Total traversed clusters / individual chain | 1,000,000 / 262,144 |
| Payload/write and output-reread buffer | 128 KiB; minimum 4 KiB |
| Fingerprint buffer | 64 KiB |
| Collision / cleanup attempts per artifact | 1,000 / 3 |
| Batch diagnostics / progress callbacks | 1,000 / 1,000 |
| Per-file diagnostic records | At most one retained outcome diagnostic, including cleanup failure |
| Elapsed operation time | 4 hours |

The metadata refresh retains Phase 8A's directory, entry, FAT/bitmap/cache, name/path, ownership and diagnostic bounds. Selected layouts are bounded by total visited clusters. Buffers and lists are bounded independently of image size. Candidate results are bounded by selection count; diagnostics never grow without a bound.

The known read requirement is `2 * image length + metadata refresh bytes + sum(selected eligible ValidDataLength)`. Runtime accounting reserves the future second hash and payload during metadata refresh. Fingerprints count every image byte, including payload. Metadata and payload completed reads are separate; output rereads are destination reads with their own counter. `ChargedSourceBytes` charges the full requested count before every exact read, including failures, so a partially completed failed read cannot bypass the ceiling. `SourceBytes` and category counters count completed exact reads; an unobservable prefix in a failed abstraction is not claimed as a completed read. On an error-free run these totals agree exactly.

Images near 4 GiB already exhaust the ceiling with fingerprints alone. Smaller selections reduce payload/output costs but cannot reduce full hashes or ownership refresh. Known impossibility returns an actionable budget result before opening the source or creating a destination. Cancellation and linked deadlines are checked during fingerprinting, parser loops, copying, zero generation and independent rereads. Progress distinguishes validation, metadata refresh, recovery, final source validation, output verification, cleanup and completion, with no fabricated ETA.

## References and remaining limits

[Phase 8A format/signature documentation](phase-8a-exfat-image-metadata.md) remains the authority for supported structures. This phase adds logical-stream output, not another format or content signature. Native identity uses [Windows FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info); directory leases use documented [CreateFile sharing and directory flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), read-only metadata access and `OPEN_EXISTING`.

Windows ordinary-file identity and stable directory handles must be available; unsupported filesystem providers fail closed. No two-FAT/TexFAT handling, deleted-directory traversal, fragment inference, partition discovery, repair, live recovery, preview or UI/worker integration was added. No physical USB/device validation was performed. See [actual verification evidence](phase-8b-verification.md).
