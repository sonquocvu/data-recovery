# Architecture

## Components and dependency rules

| Project | Responsibility | May depend on |
|---|---|---|
| `DataRecoveryStudio.Core` | Immutable domain records, enums, service contracts, validation and safety policies | BCL only |
| `DataRecoveryStudio.Application` | Use-case orchestration, navigation, commands, view models, filtering and presentation rules | Core |
| `DataRecoveryStudio.Infrastructure` | Windows metadata-only storage discovery, native buffer parsing, mock scan/result adapters, explicit-development mock discovery, JSON settings, localization, and structured logging | Application, Core |
| `DataRecoveryStudio.App` | WPF composition root, views, dialogs, themes, converters, UI-only behavior | Application, Core, Infrastructure |
| `DataRecoveryStudio.Tests` | Behavioral tests across non-UI boundaries | Core, Application, Infrastructure |

Core never references WPF, storage APIs, or infrastructure. Application code receives contracts through constructors. Infrastructure implements those contracts. The App composition root creates and connects implementations; this gives dependency injection without a service-locator or runtime container dependency.

```mermaid
flowchart LR
  WPF[WPF App / Views] --> Application
  WPF --> Infrastructure
  Infrastructure --> Application
  Infrastructure --> Core
  Application --> Core
  Core --> Policies[Safety policies]
  Infrastructure -. future .-> Broker[Minimal privileged read-only broker]
  Broker -. read-only .-> Device[Physical device]
```

## Main models and interfaces

Core models distinguish `PhysicalDisk` hardware metadata from scannable `Volume` filesystem metadata. A volume carries a set of physical identities because Windows volumes may span disks. Stable physical IDs are opaque, hashed value objects and are never inferred from drive letters or display labels.

## Phase 3 discovery flow

`WindowsStorageDiscoveryService` runs on a worker task behind `IDeviceDiscoveryService`. `WindowsStorageNative` exclusively owns the P/Invoke declarations and safe handles. Enumeration flows from volume GUID names to mount paths, filesystem/capacity metadata, disk extents, and optional per-disk storage descriptor/seek-penalty/geometry metadata. Pure parsers validate all byte counts, declared sizes, offsets, null terminators, extent counts, checked arithmetic, and bounded buffers before producing domain data.

Failures in optional physical metadata produce partial labels and session-only identities. A failure for one volume is logged by operation and Win32 code without serials and does not stop other volumes. A top-level enumeration failure is surfaced to the Devices page with Retry; mocks are never substituted.

Normal mode uses real discovery. `DATA_RECOVERY_STUDIO_DEVELOPMENT=1` selects the existing mock device provider explicitly. Both modes continue to use the simulated scan and result services in Phase 3.

WPF receives `WM_DEVICECHANGE`, debounces message bursts for 450 ms, and requests a cancellable refresh. Refresh generations prevent an obsolete result from replacing a newer list. Selected-volume removal invalidates Scan Options; removal during a simulated scan cancels that development session and clears its source/result context.

Key contracts are `IDeviceDiscoveryService`, `IScanService`, `IRecoveryCatalogService`, `ISettingsStore`, `ILocalizationService`, and `IStructuredLogger`. `RecoveryDestinationPolicy` is pure Core logic so every UI or future engine entry point can share the same rule.

## Navigation

`MainViewModel` owns one instance of each page view model and a `PageKind` state. Views are selected by WPF data templates. Child view models request transitions via narrow callbacks; they do not create windows or resolve services. Back navigation is explicit, and scan cancellation transitions into a canceled results state.

## Settings and localization

Settings are a versioned JSON document stored under `%LocalAppData%/DataRecoveryStudio/settings.json`, written atomically through a temporary file and replace/move. Invalid files fall back safely to defaults. `SettingsViewModel` applies theme/language changes immediately and persists them asynchronously.

Localization uses stable keys behind `ILocalizationService`, an English fallback dictionary, and a Vietnamese dictionary. The service raises a language-change notification so all bound primary navigation/page text refreshes without rebuilding view models. Additional cultures can be added without view changes.

## Planned scan pipeline

```mermaid
flowchart LR
  Select[Validated source] --> Open[Read-only device handle]
  Open --> Snapshot[Capture identity / geometry]
  Snapshot --> Reader[Bounded async block reader]
  Reader --> Parser[Filesystem parser or signature matcher]
  Parser --> Normalize[Normalize candidates]
  Normalize --> Estimate[Evidence-based health estimate]
  Estimate --> Catalog[Incremental result catalog]
```

Standard scanners will use isolated NTFS/FAT/exFAT parsers. Deep scanners will consume the same read-only block abstraction and a documented signature registry. Neither receives a write-capable handle. Bounded channels provide backpressure and memory limits.

## Planned recovery pipeline

Validate request and physical identities → reserve a collision-safe destination → re-open the source read-only → read known extents or carved ranges → write only through a destination abstraction → optionally verify size/hash → atomically finalize the destination file → return per-file results. Failures leave explicit partial outcomes and cleanable temporary files only on the destination.

## Cancellation, progress, and errors

Every long operation accepts a `CancellationToken`. Cancellation is cooperative at bounded read/work intervals, closes handles in `finally`, and never converts canceled work to success. Progress uses immutable snapshots with phase, bytes, counts, elapsed time, and estimates; UI throttling will prevent dispatcher overload.

Expected media failures are typed, recoverable records associated with offsets. Bad sectors are retried under a bounded policy then skipped/reported. Device removal ends the active reader gracefully and preserves the current catalog. Unexpected exceptions are logged with event names and correlation/session IDs, converted to safe user messages at the application boundary, and never leak raw paths unnecessarily.

## Raw-device isolation and privilege

Phase 1 has no raw-device implementation. Future Windows access belongs in Infrastructure behind `IReadOnlyBlockDevice`, with safe handles and read-only access flags. If elevation is unavoidable, a minimal separately deployed broker will expose only allow-listed read operations over an authenticated local IPC channel. The WPF process and domain/application assemblies remain unprivileged and cannot call destructive disk-management APIs.
