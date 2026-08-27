# Product requirements

## Product goals

Data Recovery Studio helps Windows users safely discover and restore accidentally deleted files from internal drives, external hard drives, and USB storage. It should make the safest next action obvious, explain uncertainty honestly, preserve useful metadata when available, and remain responsive during long work.

Phase 1 establishes the product architecture, safety rules, accessible localized UI shell, and realistic mock workflows. It performs no real device discovery or recovery.

## Target users and platforms

- Home users recovering accidentally deleted photos, documents, and media.
- IT support staff performing a careful first-pass logical recovery.
- Small organizations that need an understandable, auditable recovery workflow.
- Windows 10 version 22H2 and supported Windows 11 releases, x64, at 100–200% display scaling.

The first real file-system targets are NTFS, FAT32, and exFAT. Physically damaged media and forensic evidence acquisition are specialist workflows outside the MVP.

## Core behavior

### Standard Scan

Reads file-system metadata in read-only mode to locate deleted entries. It prioritizes speed and attempts to preserve original names, timestamps, paths, and folder hierarchy. Results depend on available metadata and are never guaranteed.

### Deep Scan

Reads source sectors sequentially in read-only mode and identifies content using documented file signatures. It prioritizes completeness, takes longer, and may return files with generated names, no timestamps, and no original folder structure. Fragmentation can make carved results incomplete.

### Discovery and selection

Users select an online supported volume grouped by stable physical device identity, inspect capacity, free space, file system, connection, and device type, then choose a scan mode. Unsupported or disconnected devices cannot start a scan.

### Results workflow

Users can search by file or path, filter by category/folder/recoverability, sort by name/size/date/status, preview supported content, inspect metadata, select one or many items, and choose a destination. Loading, empty, canceled, failed, and completed states must have useful next actions.

### Recovery workflow

The destination chooser summarizes selected size and available capacity, clearly warns against the source device, validates stable physical identities, and blocks insufficient or same-device targets. Recovery copies selected readable data to a different physical device and reports per-file outcomes without overstating success.

### Recovery health states

- **Excellent**: mock/planned evidence suggests content extents are intact.
- **Good**: likely usable but some uncertainty exists.
- **Poor**: overwrite, fragmentation, or read errors may affect content.
- **Unknown**: insufficient evidence for an estimate.

Phase 1 labels these as mock estimates, not forensic assessments.

## Accessibility and localization

- Keyboard access, logical tab order, visible focus indicators, and usable screen-reader names.
- Text and controls remain usable at 200% scaling and common desktop window sizes.
- Never encode meaning by color alone; statuses include text and shape/icon cues.
- Meet WCAG 2.1 AA contrast targets where applicable to desktop UI.
- All primary UI strings come from a localization service. English is the fallback; English and Vietnamese can be switched at runtime.
- Layout must tolerate text expansion and culturally appropriate number/date formatting.

## Explicit MVP non-goals

- Physical repair, clean-room recovery, firmware work, RAID reconstruction, BitLocker bypass, password recovery, secure deletion, undelete guarantees, disk repair, partition editing, boot repair, forensic imaging/certification, macOS/Linux UI, cloud/mobile recovery, and network scanning.
- Writing anything to a source device or recovering in place.
- Automatic repair, formatting, partitioning, defragmentation, cleanup, or elevation-by-default.

## Known limitations

- Reused sectors can make overwritten data irrecoverable.
- SSD TRIM may erase deleted content quickly even when metadata remains.
- Fragmented files are difficult or impossible to reconstruct with signature carving alone.
- Bad sectors can create partial files and longer scans; the app can report and skip them but cannot repair hardware.
- Physically damaged or unstable devices may worsen with continued reads and should be handled by specialists.
- Encryption, compression, sparse files, alternate data streams, corrupted allocation metadata, and vendor-specific storage layers require additional validation.
- A positive preview or health estimate does not guarantee a complete recovered file.

## MVP acceptance criteria

1. Discover supported volumes read-only and group them by stable physical device ID.
2. Complete cancellable NTFS, FAT32, and exFAT metadata scans without writing to a source.
3. Deep-scan the documented MVP signatures with bounded memory and error-tolerant reads.
4. Search, filter, sort, preview supported formats, and multi-select results.
5. Block any destination on the source physical device and reject insufficient capacity.
6. Recover selected files to a different physical device with conflict-safe naming and per-file results.
7. Handle cancellation, removal, bad sectors, permission errors, and corrupted metadata without process crashes.
8. Persist theme/language settings and operate in English and Vietnamese.
9. Pass automated safety/application tests and a documented Windows 10/11, DPI, keyboard, and accessibility test matrix.
10. Clearly distinguish estimates, partial outcomes, mocks, and unsupported cases; never guarantee recovery.
