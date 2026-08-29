# Mandatory data-safety invariants

These invariants are release-blocking requirements, not recommendations.

1. **Metadata-only discovery:** Phase 3 opens volume GUID paths (without the trailing slash) and `\\.\PhysicalDriveN` only for metadata queries. Every `CreateFileW` call uses `dwDesiredAccess = 0`, `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `OPEN_EXISTING`, no flags, and a null template. Handles are `SafeFileHandle` instances disposed immediately after enumeration queries.
2. **No source-side artifacts:** scan data, indexes, caches, logs, settings, temporary files, previews, and recovered files must never be written to any volume on the source physical device.
3. **Different physical destination:** recovery to the same physical source device is blocked, even when source and destination have different drive letters or partitions.
4. **Stable identity:** validation uses an OS-derived stable physical-device identity plus revalidation at operation start. Drive letters, mount paths, labels, and volume names are insufficient identity.
5. **Safe cancellation:** long operations accept cancellation, stop at bounded safe points, dispose handles, preserve a consistent result state, and never report cancellation as success.
6. **Removal tolerance:** device removal becomes a typed interrupted outcome; the UI remains responsive and retains already cataloged mock/real results when safe.
7. **Bad-sector tolerance:** bounded retries and recoverable offset errors replace unbounded loops or process crashes. Partial data is identified honestly.
8. **No destructive APIs:** format, partition, initialize, clean, repair, defragment, trim, delete, overwrite, and disk-management mutation APIs are prohibited.
9. **No automatic source writes:** no format, repair, cleanup, journal modification, mount mutation, or other automatic write operation is permitted.
10. **No guarantees:** UI, logs, documentation, and results must describe likelihood and observed outcomes; recovery success is never guaranteed.
11. **Least privilege:** the manifest remains `asInvoker`. Phase 3 does not request elevation when optional model, serial, media, or geometry metadata is unavailable.

## Enforcement strategy

- `RecoveryDestinationPolicy` intersects source and destination physical-identity sets and checks capacity before recovery orchestration, including multi-disk volumes.
- All engine-facing contracts expose read semantics; writable destination contracts are separate types.
- The final implementation will re-query identities immediately before opening handles to reduce hot-plug and mount-point races.
- Structured events record validation decisions and session IDs without placing logs on the source.
- Automated tests cover actual wrapper access flags, share flags, disposition, handle disposal, same-device aliases, multi-disk intersections, cancellation, removal/error transitions, malformed buffers, and absence of prohibited read/write API imports.
- Code review includes a source-write threat-model checklist before any platform adapter is merged.

The Phase 3 adapter invokes only `FindFirstVolumeW`, `FindNextVolumeW`, `FindVolumeClose`, `GetVolumePathNamesForVolumeNameW`, `GetVolumeInformationW`, `GetDiskFreeSpaceExW`, `GetDriveTypeW`, `CreateFileW`, and `DeviceIoControl`. Its only IOCTLs are `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, `IOCTL_STORAGE_QUERY_PROPERTY`, and `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`. It does not import or call `ReadFile` or `WriteFile`, does not issue an FSCTL, and does not retain device handles after enumeration.
