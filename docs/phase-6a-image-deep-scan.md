# Phase 6A: image-only Deep Scan

Phase 6A adds a production, headless signature-carving detector for ordinary image files and memory-backed fixtures. It discovers normalized candidates; it does not copy candidate bytes, recover files, create previews, decompress archives, render documents, or connect to live volumes or physical drives. The WPF application and elevated scan worker do not reference this workflow.

## Architecture and trust boundary

- `DataRecoveryStudio.Core` owns immutable requests, budgets, ranges, progress, diagnostics, validation states, candidates, results, sessions, and contracts.
- `DataRecoveryStudio.Application.ImageDeepScanService` owns regular-file source lifetime, before/after source fingerprint verification, immutable session retention, stale-session rejection, cancellation, and disposal.
- `DataRecoveryStudio.Infrastructure` owns the fixed signature registry, reusable-buffer chunk scanner, structural validators, range providers, SHA-256 metadata capture, deduplication, and overlap policy.
- All reads use `IReadOnlyRandomAccessSource`. `RegularFileRandomAccessSourceFactory` continues to reject device namespaces, devices, directories, and reparse points.
- The registry is constructed from trusted code. It performs no reflection, plugin activation, runtime code loading, or user-supplied validator execution.

## Registered formats and signatures

Matching is byte-exact and case-sensitive. Every signature is at candidate-relative offset zero. A hit starts bounded structural validation and never creates Medium or High confidence by itself.

| Priority | Format ID | Signature (hex/text) | Extension | Initial support |
|---:|---|---|---|---|
| 500 | PNG | `89 50 4E 47 0D 0A 1A 0A` | `.png` | PNG 1.x structural chunks and CRCs; unknown critical chunks are unsupported |
| 400 | JPEG | `FF D8 FF` | `.jpg` | Baseline DCT and progressive DCT framing; uncommon SOF variants are unsupported |
| 300 | GIF | `GIF87a`, `GIF89a` | `.gif` | GIF87a/GIF89a block framing |
| 200 | PDF | `%PDF-` | `.pdf` | PDF 1.0–1.7 and 2.0 detection, including bounded incremental-update EOF selection |
| 100 | ZIP | `50 4B 03 04` | `.zip` | Ordinary single-disk archives, encrypted-entry metadata, and data-descriptor metadata; ZIP64 and multi-disk archives are unsupported |

Registry construction rejects empty/oversized signatures, duplicate format IDs, invalid size bounds, invalid offsets, and indistinguishable signatures at equal priority. Descriptor order is priority-descending and then ordinal format ID.

## Scanner and ranges

The scanner rents one `ChunkSize + longest-signature-minus-one` buffer from `ArrayPool<byte>`. The default chunk is 256 KiB; accepted chunks are 4 KiB through 4 MiB. Only the trailing overlap is copied to the next read. A `(format ID, candidate offset)` set prevents overlap duplicates. Reads are sequential within sorted ranges; validation is sequential and independently bounded. Result order is offset, format priority, and stable candidate ID.

`WholeImageDeepScanRangeProvider` validates one explicit source-relative extent. `ExplicitDeepScanRangeProvider` rejects negative, overflowing, out-of-source, or over-budget input; it sorts and merges overlapping or adjacent extents into deterministic internal ranges. These providers are headless internal/application inputs, not WPF or arbitrary-JSON contracts.

`NtfsUnallocatedDeepScanRangeProvider` is image-only. It runs the existing production NTFS MFT parser, obtains the validated `$Bitmap` runlist added to `StandardScanResult`, reconstructs that virtual stream through `NtfsVirtualStream`, and exposes coalesced free-cluster extents. Bitmap bytes beyond initialized, validated coverage are unknown and never free. Boot, MFT bootstrap, MFT mirror bootstrap, and `$Bitmap` storage clusters are conservatively excluded even if a damaged bitmap claims they are free. Range count, eligible bytes, bitmap reads, MFT reads, and inspected cluster coverage are bounded. A short/damaged bitmap returns partial coverage with `NTFS_BITMAP_INCOMPLETE`.

## Validation and confidence rules

- **High**: structural validation reached a credible, bounded terminator and exact length. Completeness is `Complete`.
- **Medium**: multiple consistent structural facts were observed, but the candidate ended before its required terminator. Completeness is normally `Truncated`.
- **Low**: signature-only, unsupported, corrupt-retained, length-uncertain, or validation-budget-limited content.
- Structurally invalid candidates are suppressed by default. `RetainInvalidCandidates` exists only for diagnostics/tests.

JPEG walks marker framing and length-delimited segments, validates baseline/progressive frame basics, handles SOS entropy byte stuffing and restart markers, and accepts an exact length only at an EOI found in marker/entropy context. PNG requires a valid signature, first IHDR, constrained IHDR fields, legal chunk names, CRC-valid scanned chunks, at least one IDAT, and a zero-length CRC-valid IEND. GIF validates the logical screen descriptor, color-table bounds, image/extension sub-block chains, LZW minimum code size, and trailer without decoding LZW data. PDF requires a supported version plus a bounded last plausible `%%EOF`, preceding `startxref`, a candidate-relative plausible xref target, and trailer/xref-stream evidence; otherwise it remains Low/unknown. ZIP requires a local header, bounded single-disk EOCD, exact candidate-relative central-directory extent, bounded central entries, and corresponding local headers; it never trusts uncompressed sizes for allocation or decompresses entries.

Each validator receives hard limits for candidate bytes, source reads, structural elements, nesting, terminator search, metadata strings, and a shared deadline. Global scan limits cover source bytes, ranges, hits, validations, returned candidates, total validation bytes, diagnostics, duration, overlap comparisons, and progress frequency. Exhaustion returns an explicit partial or `BudgetLimited` result. Cancellation is checked around source reads, parser loops, validation, normalization, and terminal publication; already accepted candidates are retained in a `Canceled` result.

## Candidate identity, deduplication, and overlap

Candidate IDs are SHA-256-derived opaque identifiers over format, offset, normalized length, validator version, and structural evidence, truncated to 128 displayed bits. They are stable for deterministic rescans of unchanged content. Equal `(format, offset)` hits collapse. Exact duplicate extents choose confidence, then registry priority, then ID. A low-confidence repeated same-format header inside a structurally validated enclosing candidate (for example a later ZIP local header) is treated as that format's structural content. Other contained or partially overlapping validated formats remain visible with `CANDIDATE_OVERLAP`. Comparisons stop at a hard budget.

## Safety and limitations

The production Phase 6A path introduces no raw-device names, live-volume handles, P/Invoke, worker calls, write APIs, destinations, recovery publication, decompression, rendering, scripting, or WPF command routing. The main manifest remains `asInvoker`; the live Standard Scan feature gate is unchanged. The headless service SHA-256 hashes the regular source before and after scanning and also compares canonical path, length, and last-write timestamp. Session reuse repeats that proof and rejects stale sources.

Detection is structural evidence, not a guarantee of file integrity, content usability, malware safety, or successful future recovery. PDF length assessment is intentionally conservative. ZIP64, multi-disk ZIP, uncommon JPEG variants, unknown critical PNG chunks, FAT/exFAT, live Deep Scan, carving recovery, preview, and live recovery remain unavailable.
