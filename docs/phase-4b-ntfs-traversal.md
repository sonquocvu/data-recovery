# Phase 4B: NTFS MFT traversal and allocation analysis

Phase 4B remains an image-only, read-only metadata engine. It is not connected to Windows volume discovery or the WPF scan buttons, and it does not expose or recover deleted-file content.

## Automatic MFT bootstrap and mirror policy

`StandardScanRequest` now supplies only a validated NTFS volume offset and safety budgets. The scanner reads the boot sector, validates the declared volume against the image length, and reads file record zero at the boot sector's `$MFT` LCN. Its unnamed non-resident `$DATA` stream provides logical, allocated, and initialized MFT sizes, the derived complete-record count, and the initial VCN-to-LCN map.

Resident or non-resident `$ATTRIBUTE_LIST` (`0x20`) entries on record zero can reference extension records. The bootstrap follows those records only through already validated MFT coverage, validates record and base sequence references, and deterministically merges additional unnamed `$DATA` extents. The final map must cover the declared MFT logical size without VCN gaps, overlaps, sparse ranges, physical overlaps, negative LCNs, or ranges outside the declared volume and backing image.

The scanner validates mirrored record zero at `$MFTMirr`. A valid primary is preferred. A valid mirror is used only when primary record zero is structurally unusable; mirror bytes are never combined with primary bytes. Invalid mirrors and primary/mirror data-layout disagreements produce stable diagnostics. Phase 4B does not assume that the mirror contains the complete MFT.

## Virtual metadata streams

`NtfsVirtualStream` maps exact virtual byte reads across one or more run-list extents. Reads can cross cluster and extent boundaries and are split into bounded source reads. Every source read counts toward the shared byte budget and observes cancellation. Sparse metadata is never silently synthesized as zeroes.

All arithmetic for virtual offsets, VCNs, LCNs, physical offsets, sizes, and record positions is checked. Extents are bounded by `MaximumMftExtents` or `MaximumDataRuns`. Virtual and physical overlap, VCN gaps, zero-length runs, negative resolved LCNs, and out-of-volume ranges are rejected.

## Attribute lists, extensions, streams, and links

The bounded `$ATTRIBUTE_LIST` parser reads type, entry length, name location, lowest VCN, referenced record/sequence, and attribute ID. Resident values are parsed from the MFT record. Non-resident values are read through the same virtual-stream infrastructure and capped by `MaximumAttributeListBytes`.

Extension resolution validates record range, sequence number, base-record ownership, recursion depth, duplicate references, cycles, and total extension count. Missing or stale extensions preserve usable base metadata but mark the logical candidate damaged. Extension records never become independent candidates.

Non-resident attribute extents are grouped by stream name and ordered by lowest VCN. Merging requires consistent resident form, flags, compression unit, sizes, contiguous VCNs, and non-overlapping runs. Named and unnamed `$DATA` streams remain separate. Normalized stream metadata exposes logical/allocated/initialized size, runs, sparse/compressed/encrypted flags, allocation state, and completeness.

All valid `$FILE_NAME` links are retained. DOS-only aliases are removed when a Win32 or Win32+DOS name exists for the same parent. Primary selection is deterministic: Win32, Win32+DOS, POSIX, DOS, then ordinal name and parent record. Each retained hard link has its own bounded, sequence-validated path state. Alternate-path count and total path characters are capped independently.

## `$Bitmap` and recoverability

The filesystem `$Bitmap` record is located by its `$FILE_NAME` metadata in the traversed MFT. Its unnamed non-resident `$DATA` stream is resolved and validated like other streams. Allocation bits use NTFS least-significant-bit-first ordering: cluster `N` maps to byte `N / 8`, bit `N % 8`.

The bitmap reader loads only fixed-size blocks and evicts old blocks above `MaximumBitmapCacheBytes`; it never loads the whole bitmap. Range queries return entirely free, entirely allocated, mixed, outside coverage, or unknown. Queries spanning bytes and fragmented bitmap extents use exact virtual reads and the shared byte/cancellation budget.

Recoverability rules are deliberately conservative:

- valid resident unnamed data: `ResidentDataAvailable`;
- valid zero-length unnamed data: `ZeroLength`;
- valid, uncompressed, unencrypted, non-sparse runs whose bitmap bits are all free: `PossiblyRecoverable`;
- mixed free and allocated bits: `PartiallyOverwritten`;
- all bits allocated: `Overwritten`;
- missing/damaged/short bitmap: `Unknown`;
- compressed, encrypted, or unsupported sparse layout: `UnsupportedLayout`;
- incomplete records, extension composition, or runs: `DamagedMetadata`;
- directories or records without unnamed data: `MetadataOnly` where their metadata is otherwise sound.

These states are allocation evidence only. They do not prove content integrity and are not mapped to the UI's simulated Excellent/Good/Poor labels.

## Bounds, complexity, and cancellation

MFT record bytes are streamed one record at a time. The scanner retains only bounded parsed metadata needed for extension and path resolution, not raw record buffers or the entire MFT. Memory is `O(min(derived records, MaximumRecords) * bounded parsed metadata + MaximumBitmapCacheBytes + bounded results/diagnostics)`. Attribute counts, list bytes/entries, extension records/depth, extents/runs, filenames, paths, diagnostics, total source bytes, and record count all have explicit budgets.

Processing is sequential; there is no task-per-record or unbounded parallelism. Cancellation is checked during boot and mirror reads, extension bootstrap, every virtual read segment, record traversal, attribute-list resolution, path building, bitmap blocks/ranges, candidate analysis, progress callbacks, and final publication.

## On-disk structures and limitations

Phase 4B introduces parsing of `$ATTRIBUTE_LIST` type `0x20` and NTFS `$Bitmap` allocation bits. It introduces no file-content signature or recovery format. Existing `NTFS    `, `55 AA`, `FILE`, USA, `$STANDARD_INFORMATION`, `$FILE_NAME`, `$DATA`, run-list, and attribute-end signatures remain documented by Phase 4A.

Remaining limitations include no deleted-file payload reads, content verification, preview/recovery, live-device scanning, FAT/exFAT, BitLocker, compressed/encrypted/sparse recovery, full `$MFTMirr` reconstruction, NTFS index traversal, allocation-journal analysis, or real-media retry policy. Allocation state can change after imaging and cannot establish that every byte of a file is intact.
