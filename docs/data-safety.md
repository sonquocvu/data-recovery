# Mandatory data-safety invariants

These invariants are release-blocking requirements, not recommendations.

1. **Read-only source:** every source device and volume must be opened with read-only access. No component may obtain a write-capable source handle.
2. **No source-side artifacts:** scan data, indexes, caches, logs, settings, temporary files, previews, and recovered files must never be written to any volume on the source physical device.
3. **Different physical destination:** recovery to the same physical source device is blocked, even when source and destination have different drive letters or partitions.
4. **Stable identity:** validation uses an OS-derived stable physical-device identity plus revalidation at operation start. Drive letters, mount paths, labels, and volume names are insufficient identity.
5. **Safe cancellation:** long operations accept cancellation, stop at bounded safe points, dispose handles, preserve a consistent result state, and never report cancellation as success.
6. **Removal tolerance:** device removal becomes a typed interrupted outcome; the UI remains responsive and retains already cataloged mock/real results when safe.
7. **Bad-sector tolerance:** bounded retries and recoverable offset errors replace unbounded loops or process crashes. Partial data is identified honestly.
8. **No destructive APIs:** format, partition, initialize, clean, repair, defragment, trim, delete, overwrite, and disk-management mutation APIs are prohibited.
9. **No automatic source writes:** no format, repair, cleanup, journal modification, mount mutation, or other automatic write operation is permitted.
10. **No guarantees:** UI, logs, documentation, and results must describe likelihood and observed outcomes; recovery success is never guaranteed.
11. **Least privilege:** future privileged code is isolated, minimal, allow-listed, auditable, and never hosts UI or broad application logic. Phase 1 requests no elevation.

## Enforcement strategy

- `RecoveryDestinationPolicy` compares stable `PhysicalDeviceId` values and capacity before recovery orchestration.
- All engine-facing contracts expose read semantics; writable destination contracts are separate types.
- The final implementation will re-query identities immediately before opening handles to reduce hot-plug and mount-point races.
- Structured events record validation decisions and session IDs without placing logs on the source.
- Automated tests cover same-device aliases, capacity, cancellation, removal/error transitions, and absence of prohibited API imports.
- Code review includes a source-write threat-model checklist before any platform adapter is merged.

The current Phase 1 adapter is entirely mock-based and never enumerates, opens, reads, or writes a real storage device.
