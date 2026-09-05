# Phase 8C implementation and verification

Verified on Windows x64, 2026-09-06. Live exFAT Standard Scan is implemented behind its independent, exact, non-persisted gate and remains disabled by default. Phase 7D hardware validation remains deferred. No USB scan or hardware test was run.

## Final checks

| Check | Result |
| --- | --- |
| `dotnet restore DataRecoveryStudio.sln` | Passed; dependencies up to date |
| `dotnet format DataRecoveryStudio.sln --no-restore` | Passed |
| Formatting with `--verify-no-changes --no-restore` | Passed, exit 0 |
| `dotnet build DataRecoveryStudio.sln -c Release --no-restore` | Passed, 0 warnings, 0 errors |
| Focused Phase 8A/8B/8C | **296 passed**, 0 failed/skipped: 141 Phase 8A, 80 Phase 8B, 75 Phase 8C |
| Full Release regression suite | **664 passed, 0 failed, 6 skipped; 670 total**, 2 min 13 sec |
| Release startup, exFAT gate absent | Responsive main window, normal exit 0, no worker observed |
| Release startup, exFAT gate enabled | Responsive main window, normal exit 0, no worker observed |
| Startup logs | Both runs recorded Starting, Initialized and Exited, with the expected independent gate state |
| Application Error 1000 / .NET Runtime 1026 inspection | No matching application crash events in the smoke interval |
| Whitespace check | `git diff --check` passed |

Final tests ran against the solution's x64 Release output using `--no-build --no-restore`. The focused filter was `FullyQualifiedName~Phase8A|FullyQualifiedName~Phase8B|FullyQualifiedName~Phase8C`; the full run had no filter. TRX evidence is in `tests/DataRecoveryStudio.Tests/TestResults/phase8c-affected-final.trx` and `phase8c-final.trx`.

The six skips remain the existing Phase 5A live NTFS test, Phase 7C live FAT32 test, and four Phase 7D hardware checks. The new Phase 8C suite has no skipped tests. Existing protocol-version assertions were updated to v4/8C.1, and the old exFAT unsupported-capability expectation now checks the independent disabled gate. An initial timing-sensitive Phase 2 cancellation assertion passed in subsequent complete runs; no unrelated implementation change was made for it.

## Automated coverage and safety findings

The production exFAT fixture builder and production metadata scanner run through `LiveScanExecutor` and the production bounded `LiveVolumeRandomAccessSource`, with an instrumented native-reader substitute. Separate authorization tests exercise the real Windows target validator with deterministic metadata-query responses. Pipe tests run the real authenticated host and framing code; injected lifecycle failures are simulations, not elevated hardware runs.

Coverage includes exact feature gating, no startup launch, source eligibility, exact snapshot selection, replay/expiry/generation/scanner rejection, extent overflow and mapping, independent worker filesystem/capacity/extent/physical-identity checks, unsupported geometry, required DTO fields, invalid enums/timestamps/valid-data length, nulls, version/session substitutions, aggregate arithmetic, byte budgets and sample-cache bounds.

Fixture tests hash the in-memory source outside the worker before/after scanning and compare all actual native-read ranges against known payload ranges. Stable fixtures preserve their source bytes and exclude deleted payload reads. A separate stale-directory fixture deliberately causes metadata reads to overlap a candidate range and verifies active ownership conflict reporting. This prevents an incorrect claim of physical separation on corrupt or changing filesystems.

Boot flags, backup bytes, root descriptors, root FAT chains and up-case changes, plus unreadable required post evidence, produce ChangedDuringScan and discard candidates. Damaged metadata and unusable backup evidence remain Partial; partial allocation confidence is reduced. Tests compare actual returned read bytes with terminal accounting, including partial native reads followed by failure, and prove retries cannot reclaim spent bytes. Samples have independent 8 MiB and 32,768-range caps. There is no whole-volume fingerprint in the live route.

Real named-pipe tests verify scanner-bound production exFAT result publication and one terminal result, forged starts, old-version rejection, cancellation, timeout, removal, simulated executor failure, pipe loss and cleanup. Existing parent launch-failure tests additionally cover exFAT UAC denial, missing worker, simulated process crash and pre-launch cancellation. WPF tests cover cancellation/completion races, late progress, navigation, source removal, changed extents, unrelated refreshes, and direct destination-command lockout.

The main application's manifest and all project files are unchanged. Native volume-open flags remain unchanged; no native write/repair API or modifying control code was added. Worker and App composition contain no image scan service, whole-source fingerprint provider, image recovery service, or extraction engine. Phase 8B destination-race implementation files are unchanged: `RecoveryDirectoryLease.cs`, `RecoveryOwnedFileDeletion.cs`, `RecoveryFileOperations.cs`, `RecoveryDestination.cs`, `RecoveryFilePublication.cs`, and `ExFatImageRecoveryEngine.cs`. All Phase 8A/8B regressions and the full shared recovery suites pass.

## WPF rendering evidence

The 10,000-result test expands production parser DTOs into a clearly synthetic presentation dataset. It uses a real STA WPF dispatcher, `MainWindow`, recycled DataGrid rows, actual `ScrollIntoView`/layout calls at seven positions, and `RenderTargetBitmap`. It asserts bounded realized row counts, no binding errors, language/theme changes, working search/sort bindings, metadata-only preview, and an inert direct recovery-destination command.

Measurements from the final focused run:

| Operation | Measured time |
| --- | ---: |
| 10,000-result ingestion, mapping, subscriptions, filtering and bulk replacement | 112.8 ms |
| English / Dark: layout, seven scroll positions and raster render | 133.2 ms |
| English / Light: same rendering work | 104.7 ms |
| Vietnamese / Dark: same rendering work | 136.3 ms |
| Vietnamese / Light: same rendering work | 120.9 ms |

These are separate software timings from one run, not hardware readiness or interactive frame-rate claims. Render artifacts are under `tests/DataRecoveryStudio.Tests/bin/x64/Release/net8.0-windows/TestResults/phase8c-ui/`, with both the top and scrolled metadata panel for each language/theme. Image inspection checked the English Dark and Vietnamese Light panels and prompted fixes to the live subtitle, wrapping safety notice and filesystem-neutral attribute label. All display bindings in the touched workflow views are explicit OneWay; user-editable search, sort and selection retain the appropriate bindings.

## Startup smoke

Smoke interval: **2026-09-06 06:49:05.868–06:49:12.678 UTC**. Both runs used the built Release executable, ordinary AppData logging, development mode disabled and NTFS/FAT32 gates absent. The first also omitted the exFAT gate; the second set only that gate to exact `1`. Process environment values were restored afterward.

The check launched each app hidden, observed its own responsive main window, watched for worker processes during initialization and a two-second observation interval, then requested normal window closure. PIDs were 27232 and 26168; both exited 0 with no forced termination. No worker was observed. Automated composition/workflow tests independently verify that startup and source selection do not invoke the worker boundary. The logs confirmed `windows-metadata-only` for the absent gate and `gated-live-standard-scan` for the enabled gate. JSON evidence is `tests/DataRecoveryStudio.Tests/TestResults/phase8c-startup-smoke.json`.

## Changed-file inventory

36 files changed or added; no unrelated pre-existing changes were present at task start.

| Area | Actual files |
| --- | --- |
| Core | `ExFatScanModels.cs`, `LiveScanModels.cs`, `Models.cs`, new `LiveExFatModels.cs` |
| Application | `LiveScanServices.cs`, `LiveScanWorkflow.cs`, `MainViewModel.cs`, `ResultsViewModel.cs`, `ScanViewModels.cs` |
| Infrastructure | `DictionaryLocalizationService.cs`, `ExFatMetadataScanner.cs`, `LiveScanExecution.cs`, `LiveScanProtocolCodec.cs`, `LiveScanWorkerClient.cs`, `LiveVolumeAccess.cs`, `NamedPipeScanWorkerHost.cs`, `NativeStorageParser.cs`, `WindowsStorageDiscoveryService.cs`; new `LiveExFatExecution.cs`, `LiveExFatLocalization.cs` |
| WPF App | `App.xaml.cs`; `Views/DeviceSelectionView.xaml`, `Views/ResultsView.xaml`, `Views/ScanModeView.xaml`, `Views/ScanProgressView.xaml` |
| Tests | `Phase5ALiveScanTests.cs`, `Phase5BWorkflowTests.cs`, `Phase7DHardwareValidationTests.cs`; new `Phase8CLiveExFatTests.cs`, `Phase8CWorkerTests.cs`, `Phase8CWpfTests.cs` |
| Documentation | `README.md`, `docs/architecture.md`, `docs/data-safety.md`; new `docs/phase-8c-live-exfat-standard-scan.md`, `docs/phase-8c-verification.md` |

No automated failures remain. Real-device UAC/mixed-integrity IPC, controller-specific volume I/O, physical removal timing and hardware readiness remain unverified and intentionally deferred. Matching sampled metadata does not establish snapshot consistency or content recoverability. Live recovery, live Deep Scan and image recovery UI remain unavailable. See [implementation and limitations](phase-8c-live-exfat-standard-scan.md).
