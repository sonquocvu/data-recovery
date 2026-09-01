# Data Recovery Studio

Phase 4C foundation for a safe, modern Windows 10/11 data-recovery application. Normal Release mode performs real, metadata-only discovery of mounted Windows volumes. Headless services can scan controlled ordinary NTFS disk-image files and recover resident or uncompressed/non-sparse fragmented deleted-file content into a separate ordinary directory with trusted scan provenance, no-overwrite atomic publication, and SHA-256 verification. These services remain deliberately disconnected from discovered devices and the WPF workflow. UI scanning, preview, and recovery remain explicitly simulated. The application does **not** read live-device sectors, write source media, perform production recovery, or request administrator rights.

## Build

```powershell
dotnet restore DataRecoveryStudio.sln
dotnet build DataRecoveryStudio.sln -c Release --no-restore
dotnet test DataRecoveryStudio.sln -c Release --no-build
dotnet run --project src/DataRecoveryStudio.App/DataRecoveryStudio.App.csproj -c Release
```

See `docs/` for the product, architecture, safety rules, and phased roadmap.

The Phase 4C recovery boundary, trust model, destination rules, verification semantics, and limitations are documented in `docs/phase-4c-image-recovery.md`. Phase 4A and Phase 4B documents remain as preceding foundation records.

Set `DATA_RECOVERY_STUDIO_DEVELOPMENT=1` before launch only when explicit mock-device fixtures are needed. A production discovery failure is shown as an error and never falls back to mocks.

UI inspection and development-mode instructions are in `docs/ui-quality-assurance.md`.
