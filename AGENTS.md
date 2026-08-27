# Repository guidance

- Inspect the repository, all applicable instructions, and Git status before changing code.
- Preserve unrelated user changes.
- Never write to a source recovery device or introduce destructive disk operations.
- Keep UI, application orchestration, and storage-engine/platform logic separated.
- Use asynchronous, cancellable operations for potentially long-running work.
- Add or update behavioral tests whenever behavior changes.
- Run formatting, the Release build, and all tests before reporting completion.
- Never claim that mocked discovery, scanning, health estimation, preview, or recovery is real.
- Document every newly introduced recovery format and file signature.
