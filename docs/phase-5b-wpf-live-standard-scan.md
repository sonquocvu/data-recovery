# Phase 5B: gated WPF live NTFS Standard Scan

Phase 5B connects the WPF Standard Scan workflow to the Phase 5A elevated worker through an application-owned orchestration boundary. It does not add a recovery path, content preview, Deep Scan, FAT/exFAT scanning, or snapshot consistency.

## Release gate and composition

Live WPF scanning is disabled unless `DATA_RECOVERY_STUDIO_ENABLE_LIVE_STANDARD_SCAN` is exactly `1`. Production startup with the gate disabled constructs no worker client and cannot request elevation or open a live scan handle. Development mock scanning still requires `DATA_RECOVERY_STUDIO_DEVELOPMENT=1`; development mode takes precedence and does not mix mock and live candidates. Production never substitutes mock scanning or mock results after a live failure.

The main process remains `asInvoker`. An eligible scan is revalidated against the newest Phase 3 discovery snapshot, receives a one-time grant, and then delegates process, UAC, secure-pipe, bounded scan, and shutdown work to Phase 5A. The potentially blocking Windows elevation call runs away from the WPF dispatcher. The application cannot dismiss the Windows secure-desktop prompt; a cancellation requested while it is visible prevents a late authenticated worker from receiving a scan request and closes that exact process through the bounded lifecycle.

## Capability rules

Capability is an explicit model, not text inferred by the UI. Development mock, feature disabled, live NTFS available, disconnected, unsupported filesystem/device, unmapped identity, and Deep Scan not implemented are distinct states. Only one connected, mounted, local, supported NTFS volume with a canonical current volume identity and mapped physical disk numbers can enable live Standard Scan. FAT32 and exFAT remain visible with a coming-later explanation. Deep Scan stays visible but disabled for every real device.

## UI state and cancellation

The live state machine explicitly permits transitions among validation, Windows permission, worker launch, secure connection, scanning, result receipt, cancellation, and sanitized terminal outcomes. Invalid and post-terminal transitions are ignored. Each operation has a new correlation token; late progress from older operations is rejected. Start and completion publish once, repeated cancellation is idempotent, and accepted cancellation wins over normal completion. Partial candidates are discarded on user cancellation and source removal.

Sidebar navigation, Back, and native window close use the same application-owned safe-cancel confirmation. Keep scanning is the default and Escape/window close dismisses the dialog. Confirmed window close waits for the worker terminal lifecycle before closing the main window, so no scan is intentionally left hidden.

## Progress and results

Live progress displays source name, safe mount path, filesystem, Standard Scan, read-only and best-effort disclosures, records examined, candidates found, elapsed time, and a percentage only when the worker provides a meaningful record denominator. Elevation, launch, connection, and bootstrap are indeterminate. No fake bytes, file counts, or remaining time are generated.

Live candidate catalogs validate session IDs, candidate IDs, aggregate bounds, string bounds, uniqueness, and terminal counts before display. Mapping occurs away from the dispatcher, final ordering is deterministic, and the observable result view receives a single bulk reset. The DataGrid retains recycling virtualization. The scan summary reports records, candidate count, duration, partial/truncation state, and changed-during-scan consistency.

The preview pane is metadata-only. It does not read resident data, non-resident clusters, or render payloads. Recoverability wording is conservative and allocation status is explicitly an estimate. Selecting live results never enables recovery, returns no `SelectedFiles`, and cannot open the destination dialog or reach Phase 4C.

## Remaining limitations

- Controlled hardware validation is still required before enabling the feature by default.
- Results are live best-effort metadata, not a point-in-time snapshot.
- Live payload preview and live file recovery are unavailable.
- Deep Scan and content-signature scanning are unavailable for real devices.
- FAT32, exFAT, RAW/locked, network, optical, RAM-disk, inaccessible, unmapped, and disconnected targets cannot start a live scan.
- Worker code-signature verification still depends on a signed packaging pipeline.

No recovery format or file signature is introduced by Phase 5B.
