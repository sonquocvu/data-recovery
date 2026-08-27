# Data Recovery Studio

Phase 1 foundation for a safe, modern Windows 10/11 data-recovery application. All device discovery, scanning, preview, and recovery behavior is currently simulated. The application does **not** access raw disks or request administrator rights.

## Build

```powershell
dotnet restore DataRecoveryStudio.sln
dotnet build DataRecoveryStudio.sln -c Release --no-restore
dotnet test DataRecoveryStudio.sln -c Release --no-build
dotnet run --project src/DataRecoveryStudio.App/DataRecoveryStudio.App.csproj -c Release
```

See `docs/` for the product, architecture, safety rules, and phased roadmap.
