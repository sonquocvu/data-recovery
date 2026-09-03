# FAT32 Standard Scan metadata support (Phase 7A)

Phase 7A is the read-only FAT32 metadata scanner. Its original service boundary accepts ordinary image files. Phase 7C also invokes the same parser inside the isolated worker over an explicitly authorized live read-only volume source. Neither path reads deleted-file payload, traverses deleted directories, or parses exFAT/FAT12/FAT16. Only the separately gated Phase 7C path participates in WPF routing; recovery remains image-only.

## Trust and I/O boundary

`Fat32ImageScanService` accepts a regular, non-reparse-point file through the existing `RegularFileRandomAccessSourceFactory`. It captures the canonical path, length, last-write time, and full SHA-256 before and after scanning. A trusted session is published only when those values match and FAT32 geometry was validated. Scanner reads are bounded random-access reads. Directory cluster contents are read only while following validated active directory chains; file payload clusters are never read. No destination API is accepted.

## Geometry and metadata policy

The parser derives FAT32 classification from calculated cluster count rather than the non-authoritative filesystem-type text. It validates the BPB, supported sector and cluster sizes, reserved/FAT/data regions, FAT capacity, source bounds, root cluster, filesystem version, active FAT index, and boot signature with checked arithmetic. A valid primary sector wins. A structurally valid declared backup is used only if the primary is unusable; critical disagreement is diagnostic and fields are never merged. FSInfo is advisory, is never repaired, and never determines allocation.

FAT entries are read through a sector-sized FIFO cache bounded by `MaximumFatCacheBytes`. Values are masked to 28 bits. Mirroring-disabled volumes use the validated active FAT. Mirrored volumes use FAT 0 and compare other copies as relevant entries are read; disagreement never selects the more favorable copy.

## Names and paths

Short names use a deterministic OEM fallback: printable ASCII bytes are preserved and other bytes become `?`. This is intentionally lossy and the name state retains uncertainty. Deleted short names always show `?` for the destroyed first byte. The `0x05` escape is honored for active entries. Control characters and path separators are replaced for display only and are never used as destination paths.

Active LFNs require valid structure, ordinal order, terminator/padding, UTF-16, and checksum. Deleted LFNs are accepted only as a contiguous group immediately before the deleted short entry. Their destroyed ordinals are handled using physical reverse order, with a bounded search over plausible missing first bytes. A unique checksum match is `ProbableDeletedLongName`; otherwise it remains `AmbiguousDeletedLongName`. This is evidence, not original-name certainty.

Paths are built only from active traversal context. Deleted directories are candidates but their former children are never traversed.

## Allocation wording

The scanner reads FAT metadata, not content. Zero-length files and deleted directories have distinct states. A long-enough, cycle-free allocated chain is reported as `PreservedAllocatedChain`, explicitly unusual stale/reused evidence. When deletion-cleared FAT entries are free, only the minimum contiguous span from the recorded first cluster is examined. All-free, mixed, and all-allocated spans map to `PossiblyRecoverableContiguous`, `PartiallyOverwrittenOrReused`, and `OverwrittenOrReused`. Copy disagreement becomes `AllocationUnknown`; invalid metadata becomes `DamagedMetadata`. None of these states guarantees recovery.

## Limits

Hard ceilings cover bytes read, duration, progress frequency, directory depth/count/clusters/entries, FAT entries and chain length, visited clusters, LFN slots, names, paths, candidates, diagnostics, and FAT cache memory. Callers may lower but cannot raise those ceilings. Budget termination returns validated partial metadata with an explicit diagnostic. Cancellation is checked throughout parsing, traversal, name work, allocation analysis, and final publication.
