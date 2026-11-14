# Phase 1B — streaming scans and transactional persistence

**Scope:** Improve full-drive capture memory usage. This phase does not add USN monitoring, elevation, VSS, cleanup, or a disk-backed comparison engine.

## What changed

- SnapshotScanner.Scan emits FileRecord / ScanIssue callbacks as items are discovered.
- SqliteSnapshotRepository.CaptureAndSave inserts them immediately in one SQLite transaction using reused prepared commands.
- Snapshot metadata is finalized and committed only after successful traversal; cancellation or exceptions roll back all rows.
- The UI calls CaptureAndSave instead of collecting every file in memory. It retains at most 100 issues for display; all issues remain in SQLite.
- Progress reports continue approximately every 250 observed files.
- Legacy volume roots (such as C: versus C:\) remain comparable with new snapshots.
- A database inside the scanned tree is flagged with SNAPSHOT_DATABASE_IN_SCAN; for best accuracy, set STORAGECHRONICLE_DB to another volume.
- Old snapshots remain in the same SQLite schema and can be read as before; no migration required.

## Verification (Windows)

From the feature branch, with no uncommitted user changes:

    .\scripts\verify-local.ps1

Expected test coverage includes all Phase 1A tests plus StreamingSnapshotTests:
- Two streamed captures persist file records and can be compared to detect changes.
- Cancel after 250 files: no snapshot is visible (transaction rollback).
- Pre-cancel before scan: no snapshot is created.
- Scan callback raises IOException: exception propagates, is not mislabeled as FILE_READ_FAILED.
- Streaming scan counts match actual observations.
- Database inside scanned root produces a warning.
- Nonexistent input directory does not create a snapshot.
- Old bare-drive volume roots compare with canonical drive roots on Windows.

Manual acceptance:
1. Back up D:\StorageChronicleData\snapshots.db before testing changes. No destructive operation is expected.
2. Reopen the app; confirm previous snapshot IDs and contents are still selectable.
3. Use Browse… to choose a small test folder, take snapshot, modify one file and add a file, take a second snapshot, then compare.
4. Cancel a scan partway through and confirm no new snapshot was added.
5. Scan a larger directory (do not start with the entire C: volume). Observe progress and memory usage.
6. Confirm no new files appear in the scanned source directory, unless the database is intentionally stored there.
7. Confirm accurate notice counts and first-100 issue preview.

## Known limitations and release gates

- **Comparison memory remains unbounded relative to total file count:** SqliteSnapshotRepository.Load materializes both complete snapshots during comparison. Streaming comparison is the next performance milestone.
- SQLite insertion is synchronous and holds a transaction for the full scan; long scans may block concurrent database writers. No automatic scan scheduling is enabled.
- Live NTFS contents may change during a scan; a snapshot is an interval, not a consistent frozen point.
- Some system files remain inaccessible without elevated privileges; the scanner records failures instead of claiming full coverage.
- Hard links, compression, sparse files, alternate data streams, filesystem metadata, and VSS require additional accounting validation.
- A raw database path is locally sensitive metadata and must not be committed or shared publicly.
- **Do not release or merge until** a Windows build, automated tests, two-snapshot comparison, and cancellation rollback pass. No GitHub Actions are required.
