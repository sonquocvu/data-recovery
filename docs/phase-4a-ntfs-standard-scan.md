# Phase 4A: image-only NTFS Standard Scan

Phase 4A adds a production NTFS metadata parser for controlled disk-image fixtures. It is a headless engine foundation, not live-device scanning or file recovery. The normal WPF route remains explicitly simulated and Phase 3 device discovery remains metadata-only.

## Boundaries and composition

`IReadOnlyRandomAccessSource` exposes only a total length and asynchronous exact-range reads. The memory source owns a defensive copy. The file source accepts an ordinary path, resolves it with `Path.GetFullPath`, rejects directories, device/reparse-point attributes and device namespaces, then opens an existing file with `FileMode.Open`, `FileAccess.Read`, `FileShare.Read`, asynchronous and random-access options. It never creates a missing path and exposes no stream or write operation.

Rejected namespace forms include `\\.\`, `\\?\`, `\??\`, `GLOBALROOT`, physical-drive names, and volume-device names, using case-insensitive checks before and after normalization. This conservatively rejects extended-length namespace paths as well as raw devices. No Phase 4A code uses P/Invoke, `CreateFileW`, `ReadFile`, or `WriteFile`.

`StandardImageScanService` in Application owns source lifetime, budget validation, cancellation, and completion-race checks. `NtfsMetadataScanner` in Infrastructure owns binary parsing. Core contains only contracts and normalized models. No parser structure reaches WPF, and no discovered `Volume` can be converted to an image source.

## Documented on-disk structures and signatures

The production parser recognizes and validates:

- NTFS boot sectors with OEM identifier `NTFS    ` at byte 3 and signature `55 AA` at bytes 510–511;
- bytes per sector, sectors per cluster, total sectors, `$MFT` and `$MFTMirr` LCNs;
- positive clusters-per-file-record and negative power-of-two file-record-size encodings;
- MFT `FILE` records, header bounds, sequence number, base-record reference, flags, used/allocated sizes, and Update Sequence Array sector fixups;
- bounded resident `$STANDARD_INFORMATION` (`0x10`) and `$FILE_NAME` (`0x30`) attributes;
- bounded resident/non-resident `$DATA` (`0x80`) attributes, named streams, logical/allocated/initialized sizes, and run lists;
- run-list terminator `00`, multi-byte unsigned lengths, signed relative LCN deltas, and sparse runs with a zero offset-field size;
- attribute end marker `FF FF FF FF`.

No file-content signature or recovery format is introduced. Resident value bytes are not returned, and non-resident payload clusters are never read.

## Normalization and honesty

Only structurally valid `FILE` records whose in-use flag is clear become deleted candidates. Active records remain available internally only for parent-path reconstruction. Win32 names are preferred over Win32+DOS, POSIX, and DOS names. Names are bounded and path separators/control characters are neutralized.

Paths use record and sequence references, a visited-record set, and a hard depth budget. Results distinguish `Complete`, `Orphaned`, `StaleParent`, `CycleDetected`, and `Invalid`; incomplete candidates use a non-path `[unresolved]\` display prefix and missing names use `$MFT-N`.

Candidate assessment is deliberately limited to `MetadataOnly` or `DamagedMetadata` and always states that recoverability has not been verified. The parser never maps metadata to the UI's mock Excellent/Good/Poor ratings.

## Corruption, budgets, and cancellation

Invalid global geometry returns `InvalidVolume` with a stable diagnostic code. A malformed individual record or attribute produces a sanitized, image-relative diagnostic and scanning continues where its next record boundary is trustworthy. Bad run lists produce an `Unknown` data layout without hiding the candidate. Diagnostic storage is capped and reports truncation.

Budgets cover records, attributes per record, total bytes read, diagnostics, path depth, filename characters, and data runs. Reaching a scan-wide record/byte/range limit returns `Partial` with an explicit reason. All offsets, sizes, products, and additions use checked or pre-checked arithmetic. Processing is sequential and bounded.

Cancellation is checked before and after reads, between records, during exact-read loops, while normalizing candidates, and after progress callbacks. A token that wins during the final progress callback throws cancellation instead of publishing completion.

## Deterministic fixtures and current limitations

Tests build minimal NTFS bytes in memory and temporary ordinary files. They cover 512/1024-byte sectors, both record-size encodings, USA success/failure, active/deleted files and directories, resident/non-resident/named data, sparse and negative-delta runs, namespaces, complete/degraded paths, malformed records/attributes, arithmetic attacks, all safety budgets, cancellation races, deterministic ordering, opener rejection, WPF independence, and unchanged-source proof.

At the end of Phase 4A this scanner required an explicit MFT record count and a contiguous MFT range. Phase 4B supersedes those two limitations with metadata-derived fragmented traversal, attribute-list resolution, and conservative bitmap analysis. File-content reads, preview/recovery, FAT/exFAT, live-device scanning, BitLocker, and real-media retry handling remain outside scope.
