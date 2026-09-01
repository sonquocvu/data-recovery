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
12. **Image-only NTFS parsing:** Phase 4A scan data comes only from in-memory bytes or an ordinary file opened with `FileMode.Open` and `FileAccess.Read`. Device namespace, `GLOBALROOT`, physical-drive, and volume-device paths are rejected before the file-opening boundary.
13. **Discovery/scan separation:** a Phase 3 `Volume` is metadata only and cannot implicitly create a Phase 4A random-access source. The WPF production route remains on clearly simulated scanning.
14. **Metadata only:** Phase 4B reads the NTFS boot sector, `$MFT`, `$MFTMirr`, `$ATTRIBUTE_LIST`, and `$Bitmap` metadata. It describes resident/non-resident `$DATA` metadata but does not publish resident bytes or read deleted-file payload clusters.
15. **Allocation estimates only:** free/allocated bitmap evidence is reported conservatively and never treated as proof that file content is intact or recoverable.
16. **Trusted image recovery only:** Phase 4C accepts opaque candidate IDs from a completed in-process image scan. It never accepts UI-supplied LCNs or run lists and re-resolves current NTFS metadata before payload access.
17. **Ordinary-file source consistency:** recovery reopens the same canonical non-reparse image read-only, validates length/timestamp/boot geometry/volume/record sequence/attribute identity/fingerprints, and uses only freshly resolved extents.
18. **Contained no-overwrite destination:** output is created only beneath a validated ordinary destination root. Reparse points, traversal, device names, unsafe NTFS names, overwrite modes, and source-image destinations are rejected.
19. **Atomic verified output:** each file is streamed through a create-new destination-side partial, flushed, moved without overwrite, and SHA-256/byte-count verified. Cancellation and failure clean partial or unverified publication paths; hash equality proves copied-byte equality only.
20. **No production recovery route:** Phase 4C is headless and image-only. Phase 3 models and normal WPF device/scan/results/recovery controls cannot invoke it.

## Enforcement strategy

- `RecoveryDestinationPolicy` intersects source and destination physical-identity sets and checks capacity before recovery orchestration, including multi-disk volumes.
- All engine-facing contracts expose read semantics; writable destination contracts are separate types.
- The final implementation will re-query identities immediately before opening handles to reduce hot-plug and mount-point races.
- Structured events record validation decisions and session IDs without placing logs on the source.
- Automated tests cover actual wrapper access flags, share flags, disposition, handle disposal, same-device aliases, multi-disk intersections, cancellation, removal/error transitions, malformed buffers, and absence of prohibited read/write API imports.
- Phase 4A tests inject the ordinary-file opener to prove rejected device paths never reach it, and hash a generated image before and after a real parser run while also comparing length and last-write time.
- Phase 4C tests use only generated temporary images and explicitly created destination roots; they prove provenance rejection, metadata revalidation, no-overwrite publication, cancellation cleanup, byte/hash equality, source preservation, and continued device-path rejection.
- Code review includes a source-write threat-model checklist before any platform adapter is merged.

The Phase 3 adapter invokes only `FindFirstVolumeW`, `FindNextVolumeW`, `FindVolumeClose`, `GetVolumePathNamesForVolumeNameW`, `GetVolumeInformationW`, `GetDiskFreeSpaceExW`, `GetDriveTypeW`, `CreateFileW`, and `DeviceIoControl`. Its only IOCTLs are `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, `IOCTL_STORAGE_QUERY_PROPERTY`, and `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`. It does not import or call `ReadFile` or `WriteFile`, does not issue an FSCTL, and does not retain device handles after enumeration.
