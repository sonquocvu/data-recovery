# Phase 3: Windows storage discovery

Phase 3 discovers real Windows storage metadata without reading storage content. It does not implement scanning, deleted-file discovery, filesystem parsing, carving, preview, or recovery.

## Native API boundary

All storage P/Invoke declarations, constants, safe handles, and variable buffers are confined to `DataRecoveryStudio.Infrastructure`. Core and Application receive normalized models only.

The adapter enumerates volume GUID paths with `FindFirstVolumeW` / `FindNextVolumeW`, mount paths with `GetVolumePathNamesForVolumeNameW`, labels and filesystems with `GetVolumeInformationW`, capacity/free space with `GetDiskFreeSpaceExW`, and mount type with `GetDriveTypeW`. It maps volumes through `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, and queries optional physical descriptors, seek penalty, and geometry through `IOCTL_STORAGE_QUERY_PROPERTY` and `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`.

Every volume or physical-disk handle uses desired access `0`, share mode `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, and `OPEN_EXISTING`. Access is never raised when metadata is denied. Handles are disposed after their bounded metadata queries. There are no raw-sector reads, `ReadFile`/`WriteFile` imports, write access, modifying IOCTLs/FSCTLs, volume locks, mounts, repairs, TRIM, or elevation.

## Physical identity

Identity material is normalized by removing nulls and padding, collapsing whitespace, and normalizing case. A reliable serial plus descriptor model/vendor/bus yields High confidence. Descriptor plus physical disk number, or disk number alone, yields SessionOnly confidence. A hashed volume GUID is an Unknown-confidence display fallback and does not make the volume selectable when physical mapping is unavailable. Identity material is SHA-256 hashed; raw serials are not shown or logged.

Each volume stores a set of backing physical identities. Destination safety rejects any non-empty intersection between source and destination sets, which covers aliases, separate partitions, and multi-disk volumes.

## Supported states

Mounted local NTFS, FAT32, and exFAT volumes are selectable when Windows provides required capacity and physical mapping. Device presentation distinguishes internal SSD, internal HDD, external drive, USB/removable device, and unknown classification when optional media metadata is unavailable.

Network, optical, RAM-disk, unknown drive types, inaccessible/unmounted volumes, unsupported filesystems, RAW/locked volumes, disconnected volumes, and volumes without safe physical mapping are displayed as unavailable with a localized reason. These are Phase 3 limitations, not permanent product claims.

## Refresh and removal lifecycle

Manual refresh and debounced `WM_DEVICECHANGE` notifications use the same asynchronous discovery path. An active refresh is canceled/superseded, stale generations are discarded, duplicate volume GUIDs are suppressed, and results are deterministically ordered. A selected volume that disappears invalidates Scan Options. If a simulated scan is active, it is canceled with an explicit development-state explanation and stale result/source context is cleared.

## Privileges and limitations

The manifest is `asInvoker`. Optional storage descriptors may be unavailable on some Windows versions, drivers, virtual disks, locked volumes, or policy-restricted machines; discovery degrades to partial metadata without elevation. Physical classification can remain Unknown. A USB connect/disconnect check requires interactive hardware and cannot be automated on every build machine.

## Manual safety verification

1. Launch the Release app without elevation and confirm real mounted volumes appear and mock labels do not.
2. Compare displayed mount path, filesystem, capacity, and free space with Windows.
3. Refresh repeatedly, then attach/detach USB storage and confirm one coalesced refresh.
4. Select a device, remove it, and confirm selection invalidation. Repeat during a simulated scan and confirm cancellation without a claimed read failure.
5. Audit all `CreateFileW` calls and confirm access `0`, approved share flags, and `OPEN_EXISTING`.
6. Search imports/source for raw read/write or modifying storage controls and confirm none exist.
7. Confirm the process runs asInvoker for at least 120 seconds without new .NET Runtime or Application Error events.
