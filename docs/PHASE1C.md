# Phase 1C — disk-backed snapshot comparison

**Status: not yet validated on Windows. Do not merge or release.**

Phase 1B already writes file records directly into SQLite during a scan. Phase 1C makes comparison scalable too:

- SqliteSnapshotRepository.CompareAndStore(baselineId,currentId,token,progress) reads two sorted file-group cursors from SQLite and writes changed groups to SQLite in one transaction.
- Classification shares SnapshotComparer.CompareGroups with the existing in-memory engine.
- A custom SQLite collation uses .NET's platform-appropriate path comparer for consistent grouping, including non-ASCII paths.
- Per-file-key groups are the only file inventories held in managed memory during comparison; SQLite may build disk-backed temporary sort data.
- Comparisons are cached by the immutable pair of snapshot IDs. Repeating the same comparison reuses its stored result.
- ReadComparisonPage(id,offset,pageSize) loads at most 500 changed groups (100 in the UI). Known changes are sorted by largest absolute allocated-size difference; unknown allocations are shown after known changes.
- The UI supports Previous / Next, progress during comparison, and cancellation.
- Cancellations and failures before commit roll back comparison tables without changing existing snapshots.
- The original snapshot and issue tables are unchanged. The new comparison tables are created automatically when opening the existing database.

## Critical limitations

1. Results can be as accurate as the source snapshots, no more. Files scanned during a live system change are not atomic. Access failures and excluded reparse points remain visible.
2. SQLite uses a file-backed temporary store for sorting, but sorting can still require substantial I/O and storage. Do not assume a fixed maximum RAM use by SQLite or the OS.
3. File groups with many hard links still need an in-memory list of paths. The group is bounded by the size of one identity group, not by the size of the drive.
4. SQLite caches comparison changes persistently. This can grow the database; retention or cleanup controls are a future milestone. **Never manually delete the original snapshot database simply to clear comparison caches.**
5. The database contains sensitive paths and should remain off GitHub and backed up. On a C: scan, prefer a location on D: for the database.
6. The comparison's input-file sorting query uses a custom SC_ORDINAL_NOCASE collation registered by the new application. The persistent result-page index uses SQLite's built-in NOCASE collation for older-build compatibility (ASCII-only secondary tie-break sorting).
7. No VSS, USN journal, system metadata attribution, elevation, background scheduling or deletion is included.
8. Results are read-only diagnostics, not a justification to remove any protected Windows file.

## Windows validation

With the application closed, back up the SQLite database and its journal sidecars if present (or use SQLite's online backup API if the database must remain open).

Then check out feat/phase1c-disk-backed-comparison (preserve uncommitted work first) and run:

    .\scripts\verify-local.ps1 -RunApp

Verify:

1. All original Phase 1A and Phase 1B tests still pass; DiskBackedComparisonTests is included.
2. Existing snapshots remain available. Compare an existing pair; confirm the same summary and changed files as in the previous version.
3. Create two snapshots of a small controlled folder. Enlarge one file, remove another, add a third. Confirm the results are correct.
4. Generate more than 100 small changed files in a test folder, compare, and confirm Previous / Next yield non-overlapping pages.
5. Cancel a long comparison. Confirm no partial cached comparison and no lost snapshots.
6. Compare the same pair again. Confirm the persisted comparison is reused and no new duplicate rows are added.
7. Measure CPU, disk I/O, memory, temp-disk usage and database size when comparing 10k / 100k / 1m file records. Use synthetic snapshots before any full C: comparison.
8. Confirm both logical and allocated sizes are understood and that inaccessible files and unknown allocations remain explicitly flagged.

## Follow-up before release

- Confirm the .NET SQLite driver permits two active readers and an insert statement on one connection within the same transaction on Windows; test a real case with hundreds of change groups.
- Independently verify parity between in-memory and SQLite-backed comparison for case-only renames, non-ASCII filenames, hard links with inconsistent metadata, and unknown allocations.
- Add comparison-cache retention and user-visible storage management.
- Introduce folder-level summaries and ranked top growth contributors for large, real-world diagnostic reports.
- Consider keyset paging if users frequently navigate deep into hundreds of thousands of changed groups.
