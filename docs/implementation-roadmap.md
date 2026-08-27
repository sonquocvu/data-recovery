# Implementation roadmap

## 1. Foundation and mock UI

**Objective:** establish safe boundaries and validate the complete user journey with no device access.  
**Deliverables:** five-project solution, domain contracts/policies, themes, localization, persisted settings, mock discovery/scan/results, destination validation, documentation, and tests.  
**Risks:** a polished mock may be mistaken for recovery capability; UI assumptions may not match real device behavior.  
**Test strategy:** domain and view-model unit tests, Release build, cancellation tests, DPI/keyboard/theme/language manual matrix, executable smoke test.  
**Exit criteria:** all primary mock flows work, tests pass, no raw/destructive API exists, and mocked states are visibly disclosed.

## 2. Read-only device discovery

**Objective:** enumerate supported Windows storage safely and derive stable physical identities.  
**Deliverables:** read-only Windows adapter, device/volume mapping, hot-plug monitoring, capability and access reporting, identity cache with revalidation.  
**Risks:** USB bridges expose unstable serials; mount-point races; inaccessible/offline disks; accidentally opening with write access.  
**Test strategy:** adapter contract tests, fake WMI/SetupAPI data, USB/internal/multi-volume fixtures, device removal tests, API-flag audit, Windows hardware matrix.  
**Exit criteria:** supported devices appear accurately without elevation where possible, changes are handled, and security review proves no write-capable handle.

## 3. NTFS metadata scanning

**Objective:** identify deleted NTFS records from metadata without source writes.  
**Deliverables:** bounded read abstraction, boot-sector validation, MFT/runlist/attribute parser, deleted-entry catalog, path reconstruction, incremental progress and errors.  
**Risks:** malformed attributes, integer overflow, sparse/compressed/encrypted data, MFT fragmentation, huge allocation sizes.  
**Test strategy:** synthetic byte fixtures, fuzz/property tests, corrupted images, known-answer disk images, cancellation/bad-sector tests, memory/time budgets.  
**Exit criteria:** approved fixtures match expected files/metadata, corrupt inputs cannot crash or allocate unbounded memory, and source writes remain impossible.

## 4. FAT32 and exFAT metadata scanning

**Objective:** add safe metadata recovery for common removable media.  
**Deliverables:** FAT32 and exFAT validators/parsers, deleted directory entry handling, cluster-chain analysis, names/timestamps, shared normalized catalog.  
**Risks:** partially overwritten directory sets, long-name corruption, cluster loops, variant OEM formatting.  
**Test strategy:** clean/deleted/corrupt images for both formats, loop and boundary fixtures, cross-platform formatter fixtures, cancellation and removal tests.  
**Exit criteria:** known-answer coverage meets targets, corrupt chains terminate safely, and results clearly expose uncertainty.

## 5. Deep signature scanning

**Objective:** carve documented formats from raw read-only ranges.  
**Deliverables:** signature registry, chunk overlap handling, validators, generated naming, bounded parallel pipeline, resume/checkpoint design stored off-source.  
**Risks:** false positives, fragmentation, huge claimed lengths, performance, signature collision, source-side checkpoint placement.  
**Test strategy:** mixed-format corpora, fragmented/truncated/adversarial fixtures, fuzzing, throughput and memory benchmarks, cancellation/checkpoint safety tests.  
**Exit criteria:** every format/signature is documented with validation rules, quality metrics meet agreed thresholds, and resource limits hold.

## 6. Preview and recovery workflow

**Objective:** safely preview and copy selected candidates to another physical device.  
**Deliverables:** sandbox-conscious preview providers, destination picker, identity revalidation, conflict policy, atomic output, verification, per-file result report.  
**Risks:** malicious file previews, destination disconnect/full disk, aliases bypassing validation, partial files, user confusion about estimates.  
**Test strategy:** same-device alias tests, insufficient space, name collisions, removal/full-disk injection, format preview security tests, end-to-end image fixtures.  
**Exit criteria:** same-device writes are impossible, partial outcomes are explicit/cleanable, and verified known-answer files match expected hashes.

## 7. Reliability and corrupted-device handling

**Objective:** harden sustained operation on damaged and hostile inputs.  
**Deliverables:** typed media errors, bounded retry/backoff, resumable catalogs, telemetry opt-in design, crash recovery, resource caps, diagnostic export.  
**Risks:** failing hardware degrades under reads, retry storms, sensitive filename leakage, unreproducible device behavior.  
**Test strategy:** fault-injecting readers, long soak tests, forced termination/resume, low-memory/full-disk/removal scenarios, privacy review, fuzzing gates.  
**Exit criteria:** no known corrupt fixture crashes/hangs, recovery state survives defined interruptions, and privacy/security reviews pass.

## 8. Installer, upgrade, signing, and release readiness

**Objective:** ship a trustworthy, maintainable Windows product.  
**Deliverables:** signed binaries/installer, explicit privilege model, upgrade/rollback, settings migration, SBOM/licenses, release diagnostics, support and privacy material.  
**Risks:** signing/reputation issues, installer privilege creep, upgrade data loss, dependency vulnerabilities, OS compatibility drift.  
**Test strategy:** clean/upgrade/uninstall matrices on Windows 10/11 x64, signature verification, rollback, accessibility/localization QA, vulnerability/license scans, release smoke suite.  
**Exit criteria:** signed reproducible release passes security, installation, upgrade, accessibility, compatibility, and support-readiness gates.
