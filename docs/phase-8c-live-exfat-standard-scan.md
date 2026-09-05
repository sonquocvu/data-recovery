# Phase 8C: live exFAT Standard Scan

Live exFAT metadata scanning uses the existing one-shot elevated worker and WPF workflow. It is disabled by default. Enable it for a process with the exact environment value `DATA_RECOVERY_STUDIO_ENABLE_LIVE_EXFAT_STANDARD_SCAN=1`. The flag is independent of the NTFS and FAT32 flags and is never saved in settings. Discovery and opening Scan Options do not launch a worker; explicit Start is required. Production discovery failures do not select mock services.

Phase 7D hardware validation remains deferred. No USB, live recovery, live Deep Scan, image recovery UI, filesystem repair, or new hardware test framework is included.

## Routing and authorization

Core adds `ExFatStandardMetadata`, presentation-only exFAT evidence, and volume disk extents. Application evaluates the exact selected object from its current discovery snapshot, checks the independent gate, then issues and consumes a one-time grant. The grant binds generation, expiry, nonce, correlation/session, canonical volume GUID, capacity, filesystem, physical identities, disk numbers, and ordered extent offsets/lengths. Invalid, overflowing, overlapping, missing, or insufficient extents cannot authorize exFAT scanning.

Discovery retains full extents from its existing metadata query. The worker independently re-enumerates the authorized volume and checks mounting, local drive type, filesystem, GUID/capacity identity, complete extents, and physical identities using the existing storage-descriptor discovery queries. Physical descriptors can supply only session identity when serial metadata is unavailable; equality of that weaker identity is not proof against an indistinguishable device replacement. Physical-drive handles are used only by the existing metadata queries, never as scan-data sources.

The source still opens only a canonical volume GUID using `GENERIC_READ`, `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `OPEN_EXISTING`, `FILE_FLAG_OVERLAPPED`, and `SafeFileHandle`. No native flags, imports, modifying IOCTLs, locks, dismounts, services, VSS, or drivers were added. The application remains `asInvoker`.

## Parser and image boundary

`LiveScanExecutor.ExecuteExFatAsync` invokes the production `ExFatMetadataScanner` through the bounded live random-access source. It does not invoke `ExFatImageScanService`, an image fingerprint provider, a trusted image-session registry, an extraction plan, or a destination/publication component.

The only parser extension is an internal metadata-read observer. With no observer, Phase 8A/8B image parsing, fingerprint accounting, and recovery provenance retain their existing behavior. The live route retains no selected recovery layouts and exports no source offsets, first clusters, raw ranges, or file content through candidate DTOs.

Supported structures remain the Phase 8A subset: exFAT revision 1.00, one FAT, validated main/backup boot geometry, bounded root and active-directory traversal, allocation bitmap, up-case stream, deleted entry sets, contiguous layouts and preserved FAT-chain evidence. Two-FAT/TexFAT and unsupported geometry remain rejected. No recovery format, carving signature, or filesystem parser was introduced. See [the Phase 8A format specification](phase-8a-exfat-image-metadata.md).

## Consistency evidence

Every completed parser read is classified as boot, FAT, bitmap, up-case, or directory metadata. Live scanning does not intentionally request deleted-file payload. Corrupt or stale directory descriptors can nevertheless point into candidate ranges. The production ownership analysis marks those overlaps as active ownership conflicts; tests explicitly exercise that case. Physical separation of all metadata and candidate bytes is not promised on a changing volume.

The before evidence records parser-requested ranges during bootstrap, from initial boot reads through completion of root metadata and bitmap/up-case stream discovery. Coverage includes:

- Main and backup boot regions and required probes, including geometry, serials, mutable flags, FAT configuration, and main/backup relationship.
- FAT header, root-chain entries, root directory blocks actually examined, and bitmap/up-case descriptors found there.
- FAT entries needed while processing root active entries and locating bitmap/up-case streams, plus the up-case bytes read during bootstrap.

Each distinct `(offset, length)` stores a SHA-256 digest. Repeated observations of a sampled range that differ already mark the scan changed. A digest over sorted ranges and their digests binds both contents and coverage. After traversal, the worker rereads every required sample on the same held source. Changed bytes, a missing initial observation, or unreadable required post evidence prevents a successful consistency result. No whole-volume hash is calculated.

Later directory traversal, allocation bitmap contents, and later FAT reads are **not** covered by this bootstrap comparison. Matching samples establish only that those observed structures matched at the two observations; they do not establish a snapshot, stable allocation, or original file content. Changes between samples or outside sampled ranges may be missed.

Changed results discard candidates. Cancellation, removal, timeout, and other abnormal outcomes also follow the established discard policy. Partial parser outcomes, damaged candidate metadata, unusable backup evidence, or inconsistent boot serials remain Partial. Partial candidates explicitly retain damaged/partial flags; allocation becomes unknown except that active ownership conflicts remain visible. Name evidence remains separate from recoverability.

## Budgets and progress

Live metadata traversal and consistency rereads share the requested byte ceiling, default 256 MiB and hard maximum 1 GiB. The exFAT source receives that exact ceiling, without the historical NTFS/FAT32 consistency allowance. Each bootstrap sample reserves its required future reread before traversal spends more bytes. Samples are additionally limited to 32,768 ranges and 8 MiB of sampled content; the cache retains digests, not that content.

The live source charges each successful native partial read immediately, including bytes returned before a later exact-read failure. The exFAT observer also records those failed-operation bytes in traversal or consistency metrics. Tests compare instrumented native-read totals with terminal totals and prove that retries cannot reclaim spent bytes. Unread or rejected requests do not count as successfully returned bytes.

The parser retains Phase 8A hard bounds for directory bytes/depth, chains, bitmap queries, up-case validation, ownership records, filename/path totals, caches, candidates and diagnostics. Existing live directory/FAT limits can lower the corresponding exFAT limits. Default live duration is ten minutes, hard maximum thirty minutes; the deadline covers traversal and post evidence. Read-driven progress supplements the parser's bounded callbacks and includes consistency rereads. Updates are throttled to 100 ms, capped at 18,000 regular updates plus final reporting; IPC channels and batches remain bounded.

Progress displays directories, directory entries, FAT entries, candidates, elapsed time, and phase/budget status. Unknown totals remain indeterminate with no fabricated ETA. Ingestion measurements are separate from actual WPF layout, scrolling, and raster-render measurements.

## Protocol v4 and presentation

Protocol changes are incompatible with v3: scanner-bound candidate/diagnostic batches, exFAT candidate and terminal evidence, and grant extents. Protocol version is **4**, worker version **8C.1**. Parent/worker mismatch is rejected before scanning. Scanner kind is bound in launch arguments, handshake, start, progress, batches and terminal results; unknown/numeric launch aliases and cross-filesystem fields are rejected.

Wire validation checks required exFAT fields, nulls, enum domains, timestamp validity/offsets, valid-data length, scalar and aggregate arithmetic, text lengths, sequences, session identity, duplicate candidates, evidence hashes and sample bounds. ExFAT progress/terminal counters have hard ceilings and cannot invent a total. Aggregate candidate text is capped at 16 million characters; standalone diagnostics have separate count/string limits, and aggregate logical sizes cannot overflow `Int64`. Existing authentication, framing, single-client pipes, exact-process exit/kill/disposal controls, and bounded message sizes are preserved.

Results show name confidence, path evidence, logical size, valid-data length, timestamp validity and offsets, attributes, layout, allocation uncertainty, active conflicts, and partial/damaged state. English and Vietnamese use the existing Dark/Light theme resources. Display bindings are explicitly OneWay; search, sorting and user selection retain their editable bindings. Rows recycle and bulk replacement avoids per-item collection notifications.

`ResultsViewModel` keeps live sessions separate from recovery sessions: `Session` is null, `CanRecover` is false, and `SelectedFiles` is empty even after direct selection commands. The destination entry point repeats the capability check. Live candidates cannot enter the Phase 8B trusted session registry or produce image recovery requests. Preview remains metadata-only.

## Verification and remaining hardware work

See [Phase 8C verification](phase-8c-verification.md) for actual commands, totals, rendering measurements, startup smoke evidence, and changed-file inventory. Instrumented fixture sources and simulated lifecycle failures are automated software coverage, not hardware validation. The existing six opt-in hardware/live tests remain skipped; no additional hardware test was introduced. Real-device UAC, mixed-integrity IPC, controller-specific I/O behavior and removal timing remain deferred under the existing Phase 7D infrastructure.
