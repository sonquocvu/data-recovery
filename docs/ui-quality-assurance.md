# UI quality assurance

Phase 2 keeps discovery, scanning, health estimates, previews, destinations, and recovery entirely mocked. This checklist is for visual and interaction QA on Windows; it does not validate recovery capability.

## Launch modes

Production-like Release mode hides development tools:

```powershell
dotnet run --project src\DataRecoveryStudio.App\DataRecoveryStudio.App.csproj -c Release
```

To expose development state previews and the 10,000-result catalog for a single PowerShell session:

```powershell
$env:DATA_RECOVERY_STUDIO_DEVELOPMENT = '1'
dotnet run --project src\DataRecoveryStudio.App\DataRecoveryStudio.App.csproj -c Release
Remove-Item Env:DATA_RECOVERY_STUDIO_DEVELOPMENT
```

## Screenshot matrix

Capture Device selection, Scan mode, active Scan progress, cancel confirmation, canceling state, completed Results, each preview state, empty/canceled/failed Results, Recovery destination, and Settings in:

- Dark English at 1080×680 and 1920×1080.
- Light English at 1366×768.
- Dark Vietnamese at 1366×768.
- Light Vietnamese in a maximized window.
- The available display scale, plus 150% and 200% when the test machine supports changing scale safely.

Do not add QA captures to source control.

## Interaction checks

1. Confirm the native title bar drags, snaps, maximizes inside the work area, restores, minimizes, and closes normally.
2. Navigate the sidebar, device cards, scan methods, progress controls, result filters/list, destination choices, and Settings using Tab, Shift+Tab, arrow keys where supported, Space, and Enter.
3. Confirm focus is always visible and icon-only elements expose accessible names or adjacent labels.
4. Select each mock device. The disconnected card must be visibly unavailable and must not continue.
5. Select Standard and Deep Scan in both languages. Cards must remain equal height with no clipped safety copy.
6. Start and cancel a mock scan. Decline and accept the confirmation; the canceling message must remain visible until completion.
7. Search, filter, sort, preview, and multi-select results. Confirm long names and paths trim with tooltips and complete details remain accessible.
8. In development mode, generate 10,000 items and scroll quickly while searching and switching categories. Rows should recycle without sustained UI stalls.
9. In the destination dialog, verify focus begins on the first safe destination, the source physical device is blocked, insufficient space is explained, and confirmation enables only for a safe choice.
10. Switch theme and language, restart, and verify both settings persist.

## Human visual risks to confirm

- Native title-bar colors are controlled by Windows and may not match the in-app dark palette on every Windows 10 build.
- Font fallback and Vietnamese text metrics vary by Windows/Segoe UI version.
- Actual 150%/200% rasterization, screen-reader announcements, high-contrast themes, and multi-monitor DPI transitions require physical desktop testing.
- At 1080×680, confirm the results name column remains useful with the 160px filter and 230px preview panels.
