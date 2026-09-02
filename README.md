# Data Recovery Studio

Phase 5B foundation for a safe, modern Windows 10/11 data-recovery application. Normal Release mode performs real, metadata-only discovery of mounted Windows volumes. WPF can route an eligible NTFS Standard Scan to the isolated Phase 5A elevated worker only when the controlled-preview feature flag is explicitly enabled. The worker returns bounded normalized metadata only. Live content preview, live recovery, Deep Scan, and FAT/exFAT scanning remain unavailable. The main application stays unelevated; only the one-shot worker requests administrator approval when a gated scan starts.

## Build

```powershell
dotnet restore DataRecoveryStudio.sln
dotnet build DataRecoveryStudio.sln -c Release --no-restore
dotnet test DataRecoveryStudio.sln -c Release --no-build
dotnet run --project src/DataRecoveryStudio.App/DataRecoveryStudio.App.csproj -c Release
```

See `docs/` for the product, architecture, safety rules, and phased roadmap.

The Phase 4C recovery boundary, trust model, destination rules, verification semantics, and limitations are documented in `docs/phase-4c-image-recovery.md`. Phase 4A and Phase 4B documents remain as preceding foundation records.

The Phase 5A worker, target grants, exact native flags, IPC bounds, consistency semantics, opt-in integration test, and limitations are documented in `docs/phase-5a-live-ntfs-scan.md`.

The Phase 5B feature gate, WPF state machine, cancellation/navigation policy, metadata-only results, and remaining product restrictions are documented in `docs/phase-5b-wpf-live-standard-scan.md`.

Set `DATA_RECOVERY_STUDIO_DEVELOPMENT=1` before launch only when explicit mock-device fixtures are needed. A production discovery failure is shown as an error and never falls back to mocks.

Set `DATA_RECOVERY_STUDIO_ENABLE_LIVE_STANDARD_SCAN=1` only for controlled live Standard Scan validation. The flag is disabled by default and is not persisted as a setting.

UI inspection and development-mode instructions are in `docs/ui-quality-assurance.md`.
