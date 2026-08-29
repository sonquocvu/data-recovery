# Data Recovery Studio

Phase 3 foundation for a safe, modern Windows 10/11 data-recovery application. Normal Release mode performs real, metadata-only discovery of mounted Windows volumes and their physical storage descriptors. Scanning, deleted-file discovery, preview, and recovery remain explicitly simulated. The application does **not** read raw sectors, write storage devices, or request administrator rights.

## Build

```powershell
dotnet restore DataRecoveryStudio.sln
dotnet build DataRecoveryStudio.sln -c Release --no-restore
dotnet test DataRecoveryStudio.sln -c Release --no-build
dotnet run --project src/DataRecoveryStudio.App/DataRecoveryStudio.App.csproj -c Release
```

See `docs/` for the product, architecture, safety rules, and phased roadmap.

Set `DATA_RECOVERY_STUDIO_DEVELOPMENT=1` before launch only when explicit mock-device fixtures are needed. A production discovery failure is shown as an error and never falls back to mocks.

UI inspection and development-mode instructions are in `docs/ui-quality-assurance.md`.
