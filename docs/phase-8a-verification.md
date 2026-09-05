# Phase 8A implementation and verification report

## Delivered architecture

Phase 8A implements headless, read-only exFAT deleted-file metadata discovery for ordinary image files. No App/ScanWorker reference, registration, live route, recovery workflow, preview, or source-writing behavior was added.

| Layer | New files | Responsibility |
| --- | --- | --- |
| Core | `ExFatScanModels.cs` | Immutable contracts, evidence states, budgets, internal provenance and result attestation |
| Application | `ExFatImageScanService.cs` | Fingerprint/scan orchestration and trusted-session registry |
| Infrastructure | `ExFatImageSource.cs` | Strict regular-file boundary and budgeted full-source fingerprinting |
| Infrastructure | `ExFatStructures.cs` | Boot, entry-set, UTF-16, up-case, hash/checksum, and timestamp parsing |
| Infrastructure | `ExFatMetadataScanner.cs` | Bounded directory traversal, FAT/bitmap reads, ownership and candidate assessment |
| Tests | `ExFatTestFixtureBuilder.cs`, `Phase8AExFatScanTests.cs` | Independent deterministic fixtures, behavioral tests, and production-service proof |

README, architecture, and data-safety documents are updated. [Implementation documentation](phase-8a-exfat-image-metadata.md) records every supported metadata signature, behavior, budget, and limitation. Existing Phase 5–7 code, projects, manifests, feature gates, and the Phase 7D report are unchanged.

## Behavior and safety rules

- Parsed structures: independently validated main/backup 12-sector boot regions, extended/OEM/reserved/checksum sectors, FAT, root bitmap/up-case entries, volume label/GUID, active directories/files, and coherent deleted file/stream/name entry sets. No content signatures or recovery formats were added.
- Boot selection: valid main wins; independently valid backup is used only after main failure. Critical disagreement fails closed. Backup-only scans are partial because backup mutable flags are stale. No boot fields are merged or repaired.
- Exact source open: `FileMode.Open`, `FileAccess.Read`, `FileShare.Read`. Namespace, stream, root, directory, missing-file, device, and reparse/junction inputs are rejected before the regular-file opener.
- FAT/bitmap/up-case: bounded FAT chains and fixed evicting key/value caches; bitmap bits are allocation authority; missing coverage is never free. Up-case checksums, full bounded expansion, and mandatory mappings are verified. Name hashes use the image's table rather than host Unicode conversion.
- Deleted names: coherent cleared in-use entry types only; ordinary and reconstructed checksum evidence remain distinct. Name confidence separates verified/recovered/hash-only/probable/ambiguous evidence. Original validated UTF-16 is internal; public names are display-only.
- Allocation: valid empty streams, exact contiguous spans, or complete preserved chains only. Active overlaps are blocked; incomplete ownership/bitmap evidence is unknown; allocated bits are never free. All-free allocation suggests possible content and never proves integrity.
- Operations: all read/work loops are cancellable; deadlines interrupt blocked reads. Hard limits cover bytes, time, directories/depth/paths, entries, FAT/bitmap operations and caches, chains/clusters, up-case storage, names, ownership, candidates, diagnostics, and callbacks. One terminal report is reserved. Partial/canceled/changed/invalid scans publish no trusted session.
- Provenance: the registry binds canonical source path, length, UTC timestamp, full SHA-256, volume offset, geometry/boot evidence, scanner version, and opaque deterministic candidate IDs. Public record clones cannot authorize a session. Source changes invalidate cached sessions.

## Verification results

| Check | Result |
| --- | --- |
| `dotnet restore DataRecoveryStudio.sln` | Passed |
| `dotnet format DataRecoveryStudio.sln --no-restore` | Passed |
| Formatting verification with `--verify-no-changes` | Passed; final run clean |
| Release solution build, `--no-restore` | Passed, 0 warnings, 0 errors |
| Focused Phase 8A suite | 141 passed, 0 failed, 0 skipped |
| Final full Release suite | 508 passed, 6 skipped, 0 failed; 514 total, 2 minutes 12 seconds |
| Project/reference/native/write-API audit | 5 new production files audited; 0 prohibited matches; 0 App/worker routes; 0 existing gate/project/manifest changes |
| Manifests | Main remains `asInvoker`; worker remains `requireAdministrator` |
| Release WPF startup smoke | Final build passed: responsive hidden main window, clean exit code 0, no forced termination |
| Runtime 1026 / Application Error 1000 | No matching events in the successful smoke interval |
| `git diff --check` | Passed |

The final startup smoke ran at 2026-09-05 04:42:41 UTC and recorded `ApplicationStarting`, `ApplicationInitialized`, and `ApplicationExited`. No matching Runtime 1026 or Application Error 1000 events occurred. Whitespace checks also covered all nine new, untracked files, with zero errors.

The six full-suite skips are the existing opt-in NTFS/FAT32 live-volume and Phase 7D hardware/cancellation/removal/WPF tests. They are not counted as passes or hardware validation. Phase 7D remains **hardware validation deferred**, and both live NTFS/FAT32 gates remain disabled by default.

Initial formatting emitted WPF design-time reference warnings; building Debug references resolved them without changing application code. Initial sandboxed startup probes did not complete normal application initialization. The authorized ordinary-user smoke outside the workspace sandbox, with normal AppData logging and both live flags disabled, created a responsive hidden window, logged initialization and clean exit, and exited with code 0 without forced termination. No execution-policy setting was changed; commands were run inline.

## End-to-end source and payload proof

The deterministic fixture includes active nested content, a deleted directory, and five deleted regular files: empty, contiguous-free, preserved-chain-free, ambiguous-name, and active-ownership-conflict cases. Production Application scans preserve candidate IDs and physical-slot ordering across repeated scans and exclude active files/deleted directories.

| Evidence | Before | After |
| --- | --- | --- |
| SHA-256 | `38A83042894F83B07BF7A446E744C77749E1115E384BA8DC03C47E0637E2FAC0` | Identical |
| Source length | 1,179,648 bytes | 1,179,648 bytes |
| Last-write UTC ticks | 639241800342583418 | 639241800342583418 |

The audit instrumented every metadata-scanner read: **44 metadata read calls across two scans, zero overlaps with candidate or active-file payload ranges**. The first scan completed 15,155 metadata bytes and 2,359,296 fingerprint bytes, totaling 2,374,451 bytes. The source directory still contained exactly one file; no recovery destination was created. A trusted session was published for the completed unchanged image and revalidated successfully. Changed/canceled/partial/cloned-result cases publish none.

Full SHA-256 necessarily reads payload bytes. As explicitly agreed, the two full-image fingerprint passes are separate from the metadata-read audit and counted independently; no payload is retained, previewed, recovered, or used for candidate discovery.

Machine-readable evidence is in `artifacts/test-output/phase8a/`: `phase8a-focused-final.trx`, `full-suite-final.trx`, `source-proof.json`, `safety-audit.json`, and `startup-smoke-hidden-window.json`. Artifacts remain ignored by Git.

## Remaining limitations

Only revision-1.00, one-FAT ordinary images are supported; two-FAT/TexFAT states and unsupported metadata allocations fail closed. Backup-only/dirty/missing-metadata scans are partial. Both full hashes plus metadata must fit the 8 GiB total byte ceiling; near-4-GiB images already consume that ceiling. Record/chain/name/path budgets also limit very large trees. Sessions retain bounded per-scan metadata until disposed.

No live exFAT, exFAT recovery/preview, Deep Scan, deleted-directory traversal, filesystem repair, formatting, partition discovery, BitLocker, FAT12/16, or UI/worker integration. Allocation metadata cannot guarantee intact original content. Fixtures and injected faults prove software behavior, not physical-media or Phase 7D readiness.
