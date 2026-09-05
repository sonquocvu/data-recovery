# Phase 8B implementation and verification report

Verified on Windows x64, 2026-09-05. Phase 8B implements trusted, headless exFAT logical-stream reconstruction from ordinary image files. Phase 7D hardware validation remains deferred; no USB access was requested or performed. Existing live gates, App/ScanWorker composition, projects and manifests remain unchanged.

## Delivered scope

Core contains immutable ID-only recovery requests, explicit bounded policy, plans, evidence-preserving outcomes and progress. Application owns completed Phase 8A sessions and single-use recovery plans; foreign IDs, forged sessions, disposal and source invalidation cannot authorize recovery. Infrastructure reuses the production Phase 8A parser once per batch and derives selected extraction layouts from its sealed fresh result.

Default recovery permits structurally valid empty files and all-free contiguous NoFatChain layouts with complete ownership evidence. Preserved FAT chains require explicit opt-in. Allocated spans, active conflicts, unavailable metadata, damaged chains and untrusted extraction metadata remain blocked. The engine copies only initialized bytes and zero-fills the justified logical tail, then independently verifies exact length and reconstructed SHA-256. Filename uncertainty uses safe deterministic fallbacks without changing allocation confidence.

Shared destination validation, sanitation, no-overwrite publication and independent reread infrastructure are reused. Concrete boundary fixes include ADS rejection, disabled managed source read-ahead, Windows file-ID binding, repeated ancestor checks, guarded directory leases, reserved superscript DOS names, exclusive-create ownership, and identity-checked deletion by held output handle. No new filesystem parser, file signature or recovery container was introduced. See [behavior, architecture and limits](phase-8b-exfat-image-recovery.md).

## Final verification results

| Check | Result |
| --- | --- |
| `dotnet restore DataRecoveryStudio.sln` | Passed; dependencies up to date |
| `dotnet format DataRecoveryStudio.sln --no-restore` | Passed |
| Formatting with `--verify-no-changes` | Passed, exit 0 |
| `dotnet build DataRecoveryStudio.sln -c Release --no-restore` | Passed, 0 warnings, 0 errors |
| Focused Phase 8A/8B tests | 221 passed: 141 Phase 8A and 80 Phase 8B; 0 failed/skipped |
| Shared Phase 4C/6B/7B recovery regressions | 68 passed, 0 failed/skipped |
| Full Release suite | 588 passed, 0 failed, 6 skipped; 594 total, 2 min 11 sec |
| Source/write-boundary audit | 8 exFAT files; 0 prohibited API matches; destination/native helpers reviewed separately |
| App/worker route and existing gate/project/manifest audit | 0 route matches; 0 changes against the Phase 7 baseline |
| Release WPF startup smoke | Responsive hidden main window, clean exit 0, no forced termination |
| Application event/log inspection | Starting, Initialized and Exited recorded; 0 matching Runtime 1026/Application Error 1000 events |
| Git whitespace check | Passed |

The six skipped tests are the existing opt-in Phase 5A live NTFS test, Phase 7C live FAT32 test, and four Phase 7D hardware checks. No additional test was skipped. Synthetic fixtures and injected failures verify software behavior, not hardware readiness.

Final tests used the solution's `x64/Release` output with `--no-build --no-restore`. Focused filters were `FullyQualifiedName~Phase8A|FullyQualifiedName~Phase8B` and `FullyQualifiedName~Phase4C|FullyQualifiedName~Phase6B|FullyQualifiedName~Phase7B`. The full suite used no filter. TRX artifacts retain all individual outcomes.

The final startup smoke began at **2026-09-05 10:22:27.7451791 UTC** using the built Release executable, normal ordinary-user AppData logging, development mode disabled and both live gates disabled. It found the process's own hidden main window, checked responsiveness and requested a normal close. Initialization was logged at 10:22:29.6901188 UTC and exit at 10:22:29.7218377 UTC. No PowerShell execution-policy setting was changed.

## Production scan-to-recovery proof

The end-to-end test writes an independently generated exFAT image, scans it through the production Phase 8A Application service, and recovers through the production Phase 8B engine and Application plan service. Instrumentation delegates to the real source factory, fingerprint provider and metadata scanner. It observes exact read requests; injected faults are confined to separate tests.

Fourteen requested IDs, supplied in reversed and duplicate order, resolve to seven deterministic candidates. Six logical streams are verified; the active ownership conflict remains blocked. A preexisting `one.bin` survives, and publication uses `one (1).bin`. The uncertain name uses `EXFAT_Deleted_<candidate-id>.bin`. Every output is contained directly in the selected destination; no source overwrite, leftover partial or leftover guard remains.

| Candidate | Logical bytes | Copied bytes | Synthesized zeros | Result |
| --- | ---: | ---: | ---: | --- |
| `zero.txt` | 0 | 0 | 0 | Verified empty stream |
| `one.bin` | 37 | 37 | 0 | Verified, collision suffix |
| `multi.bin` | 1,031 | 1,031 | 0 | Verified contiguous stream |
| `tail.bin` | 1,300 | 519 | 781 | Verified initialized prefix plus exact zero tail |
| `fragment.bin` | 700 | 700 | 0 | Verified preserved chain with explicit opt-in |
| `uncertain.bin` | 21 | 21 | 0 | Verified bytes under generated fallback name |
| `conflict.bin` | 31 | 0 | 0 | Blocked active ownership conflict |

The test compares every output byte to independently prepared expected content and checks all output SHA-256 values. Every metadata request is disjoint from declared file-payload ranges. Every extraction request lies within an eligible initialized range; no uninitialized bytes, blocked payload or final-cluster padding is requested. Separate tests prove the preserved chain is blocked by default, and cover nonzero volume offsets and different sector/cluster geometry.

Before and after recovery, the source has exactly:

- Length: **1,179,648 bytes**.
- Last-write UTC: **2026-09-05T10:21:27.6775259Z**; ticks **639242004876775259**.
- SHA-256: **79B15A17162F47337D0F22F51FA4759AA5518E0D605DDA66E941FFA32AD460AF**.

The recovery batch opens one stable source handle, refreshes metadata once, and fingerprints exactly twice, independently of candidate count:

| Read/write category | Bytes |
| --- | ---: |
| Full source fingerprints | 2,359,296 |
| Metadata, allocation and ownership refresh | 14,642 |
| Initialized payload reads / completed copies | 2,308 |
| Total completed and charged source reads | 2,376,246 |
| Synthesized logical zeros | 781 |
| Independent destination verification reads | 3,089 |

The full hashes intentionally include all image bytes. This is separate from the metadata scanner's prohibition on payload reads, consistent with the approved interpretation. The initial Phase 8A scan has its own bounded operation; the table above accounts for every read in the recovery operation. All reconstructed stream hashes and exact counters are retained in the JSON proof artifact.

## Fault and safety coverage

Behavioral tests cover foreign/forged/disposed/invalid/partial/canceled sessions, single-use plans and disposal during extraction; equal-length mutations with restored timestamps and identical-content file replacement; metadata attestation and changed deletion/entry/geometry/layout bytes; exact empty/contiguous/fragmented/tail semantics; allocation conflicts, missing bitmap/ownership and damaged chains; safe Unicode/reserved/fallback names, traversal rejection, collisions, junctions, ADS and source hard-link aliases; short/read/write failures, space failures, hash/length mismatch, cleanup failure and replacement preservation; cancellation at every phase and inside copying/verification; blocked-read cancellation/deadline; preservation of earlier verified outputs; preflight/runtime budgets, charged failed reads and progress limits.

Verification exposed a real Windows directory-sharing defect: attribute-only handles did not prevent rename once staged streams closed. The final implementation requests directory-list/read-attributes access, keeps delete sharing disabled, and uses an exclusive delete-on-close guard before enabling the write sharing required for child-file publication. The regression test attempts destination and ancestor renames after staged streams close, attempts guard deletion, and verifies successful publication and guard removal. Cleanup verifies file identity again on the deletion handle, avoiding a path-check/delete race. Final focused, shared and full runs all pass after these fixes.

## Evidence and remaining limits

Machine-readable artifacts are in ignored `artifacts/test-output/phase8b/`: `phase8ab-focused-final.trx`, `shared-recovery-final.trx`, `full-suite-final.trx`, `source-proof.json`, `safety-audit.json`, and `startup-smoke-hidden-window.json`. Fixture images and recovered outputs live only in owned temporary test directories and are removed after assertions; their hashes and metrics remain in these artifacts.

Only Windows ordinary-file providers supporting stable identities and directory handles are accepted. The 8 GiB total source-read ceiling includes both full hashes and all metadata/payload requests; near-4-GiB images cannot fit even before selected payload. Failed exact reads are charged in full, while completed-read counters exclude any unobservable failed prefix. Policy limits cannot be raised. No live exFAT, UI/worker integration, preview, repair, deleted-directory recovery or fragment guessing is present. Verified hashes demonstrate fidelity of logical reconstruction, not guaranteed original-content restoration.
