# Phase 1A — Snapshot comparison implementation and validation

## Core contract

1. Record each observed file with logical bytes, nullable allocated bytes, nullable NTFS identity, hard-link count, and UTC last-write timestamp.
2. Preserve root, scan interval, volume capacity/free bytes, exclusions, and read failures for every snapshot.
3. Never treat an unknown allocated size as zero.
4. Persist one snapshot in one SQLite transaction; incomplete database writes must not become selectable snapshots.
5. Compare snapshots of **the same root**, using file identity where available and path fallback otherwise. The WPF selector lists only earlier baselines of the currently selected root; mismatched roots must not be selectable.
6. De-duplicate hard-linked paths within a snapshot by identity when calculating allocated-size changes.
7. Report observed free-space delta, known file allocation delta, and provisional residual separately:
   volume used delta = baseline free-after − current free-after;
   provisional residual = volume used delta − known allocation delta.
8. Report provenance and coverage limitations; a residual is **not** a diagnosis.
9. Do not delete, compact, elevate, modify, or follow reparse points automatically.

## Known gaps to address before a production release

- Scanner currently builds an in-memory file list. Use streaming writes or bounded batches for large volumes.
- Single directory enumeration can be affected by concurrent changes. Introduce VSS-backed consistency later if necessary.
- GetCompressedFileSizeW and GetFileInformationByHandle can fail per file. Those failures are recorded as notices.
- Snapshot save is synchronous; cancellation only applies to the filesystem traversal.
- Identity-group comparison does not yet represent every multiple-path/hard-link edge case accurately; verify identical metadata across every observed path in a group.
- Directory-size reconciliation is inherently partial because the free-space reading applies to the whole volume.
- File allocation does not cover all Windows filesystem metadata or VSS storage.
- Add explicit CSV/JSON evidence export, access-control tests, millions-of-files performance tests, and a journal-assisted incremental scanner in subsequent phases.
- There is no deletion or cleanup UI by design.

## Test and verification checklist

Run on a Windows machine with .NET 10 SDK:

    dotnet --version
    dotnet restore
    dotnet build -warnaserror
    dotnet test --no-restore --logger "console;verbosity=normal"
    dotnet run --project StorageChronicle.App

Then manually verify:

1. Snapshot a test folder with several small files. Observe added, removed, grown, and shrunk changes correctly.
2. Attempt to select two snapshots from different roots. Expect the UI to refuse the mismatched comparison; after scanning the same root twice, expect a compatible baseline.
3. Rename a file without changing its identity. Expect a move with zero allocated delta.
4. Create a hard link. Expect a link-path change without an additional allocated-size charge.
5. Create or shrink a sparse/compressed file. Confirm logical size and allocated size differ appropriately.
6. Add a junction pointing to an unrelated tree. Confirm it is **not** traversed.
7. Try a restricted directory. Confirm a visible scan failure and an incomplete status, not a false zero.
8. Compare two full scans of a quiet test volume. Record free-space drift, known allocation delta, and residual.
9. Cancel a scan. Confirm that no snapshot is saved.
10. Examine database path and confirm it stays off the scanned volume when possible.

## Delivery policy

Feature work belongs on feat/phase1a-snapshots. Do not merge to main or create a release before Windows build/tests and independent review. No GitHub Actions runs are required; local validation is preferred.
