# Data Recovery Studio

Phase 8C foundation for a safe, modern Windows 10/11 data-recovery application. Normal Release mode performs real, metadata-only discovery of mounted Windows volumes. WPF can route eligible NTFS, FAT32 and exFAT Standard Scans to the isolated Phase 5A elevated worker only when their independent controlled-preview flags are explicitly enabled. Phase 7D hardware validation remains deferred; its existing release-readiness result and safety gates are unchanged. Separately, headless image-only services provide NTFS recovery, bounded JPEG/PNG/GIF/PDF/ZIP carving, FAT32 metadata scanning and conservative recovery, and exFAT metadata scanning with trusted deleted-file logical stream reconstruction. Live exFAT reuses the production metadata parser and exposes metadata-only results through the existing worker/WPF workflow. Live content preview, live recovery, live Deep Scan, fragmented FAT guessing, deleted-directory recovery, and FAT12/FAT16 support remain unavailable.

See [Phase 8C live exFAT](docs/phase-8c-live-exfat-standard-scan.md) and its [verification report](docs/phase-8c-verification.md) for protocol v4, bounded consistency evidence, read accounting, and recovery isolation.

## Build

```powershell
dotnet restore DataRecoveryStudio.sln
dotnet build DataRecoveryStudio.sln -c Release --no-restore
dotnet test DataRecoveryStudio.sln -c Release --no-build
dotnet run --project src/DataRecoveryStudio.App/DataRecoveryStudio.App.csproj -c Release
```

See `docs/` for the product, architecture, safety rules, and phased roadmap.

Phase 8A's structures, boot policy, bounded metadata reads, separately audited full-image fingerprints, conservative evidence, and future session-provenance boundary are documented in [docs/phase-8a-exfat-image-metadata.md](docs/phase-8a-exfat-image-metadata.md).

The [Phase 8A verification report](docs/phase-8a-verification.md) records test totals, source-preservation and payload-read proofs, safety audits, and Release startup results.

[Phase 8B recovery](docs/phase-8b-exfat-image-recovery.md) documents trusted sessions, conservative eligibility, initialized-byte copying and zero tails, safe publication, and total read budgets. Its [verification report](docs/phase-8b-verification.md) records production scan-to-recovery evidence and regression results. Verified output hashes establish copy fidelity for the reconstructed stream, not guaranteed restoration of the original content.

The Phase 4C recovery boundary, trust model, destination rules, verification semantics, and limitations are documented in `docs/phase-4c-image-recovery.md`. Phase 4A and Phase 4B documents remain as preceding foundation records.

The Phase 5A worker, target grants, exact native flags, IPC bounds, consistency semantics, opt-in integration test, and limitations are documented in `docs/phase-5a-live-ntfs-scan.md`.

The Phase 5B feature gate, WPF state machine, cancellation/navigation policy, metadata-only results, and remaining product restrictions are documented in `docs/phase-5b-wpf-live-standard-scan.md`.

The Phase 6A registry, format signatures, validation rules, range strategies, budgets, overlap policy, source fingerprint, safety boundary, and limitations are documented in `docs/phase-6a-image-deep-scan.md`.

The Phase 6B trusted-session model, candidate revalidation, contiguous carving, shared destination safety, atomic publication, SHA-256 copy verification, and limitations are documented in `docs/phase-6b-image-carving-recovery.md`.

The Phase 7A FAT32 metadata parser and Phase 7B trusted FAT32 image-recovery provenance, active ownership analysis, eligibility policy, atomic publication, byte/hash proof, and limitations are documented in `docs/FAT32_STANDARD_SCAN.md` and `docs/phase-7b-fat32-image-recovery.md`.

The Phase 7C independent FAT32 gate, scanner-bound worker protocol, live consistency evidence, WPF presentation, recovery lockout, and controlled integration procedure are documented in `docs/phase-7c-live-fat32-standard-scan.md`.

The Phase 7D explicit target resolver, controlled hardware/cancellation boundaries, sanitized JSON evidence report, and release-readiness rules are documented in `docs/phase-7d-hardware-validation.md`.

Set `DATA_RECOVERY_STUDIO_DEVELOPMENT=1` before launch only when explicit mock-device fixtures are needed. A production discovery failure is shown as an error and never falls back to mocks.

Set `DATA_RECOVERY_STUDIO_ENABLE_LIVE_STANDARD_SCAN=1` only for controlled live Standard Scan validation. The flag is disabled by default and is not persisted as a setting.

Set `DATA_RECOVERY_STUDIO_ENABLE_LIVE_FAT32_STANDARD_SCAN=1` only for controlled live FAT32 metadata validation. It is disabled by default, independent of the NTFS flag, and is not persisted.

Set `DATA_RECOVERY_STUDIO_ENABLE_LIVE_EXFAT_STANDARD_SCAN=1` only for controlled live exFAT metadata validation. It is disabled by default, independent of both other flags, and is not persisted.

UI inspection and development-mode instructions are in `docs/ui-quality-assurance.md`.
