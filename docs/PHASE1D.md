# Phase 1D — Folder-level growth summaries

**Status:** Source implemented and committed; Windows build/test/manual verification pending. Do not merge or release without validation.

## Scope and purpose

A file-level diff is not enough to answer "which directory consumed the space?"
Phase 1D adds a read-only **Folder growth** tab alongside **File changes**.

- Comparison #N is built as in Phase 1C and remains immutable.
- BuildFolderSummary(comparisonId) streams the saved changed file groups from SQLite.
- It attributes each changed file-identity group to one canonical before/after path, then updates all ancestor directory totals inside the scanned root.
- Folder results are cached under a versioned algorithm in SQLite, and recomputation is atomic.
- ReadFolderPage loads at most 500 folder rows (100 in the WPF UI), sorted by net allocation change (largest growth or most freed).
- A folder's values include its descendants. Parent/child rows therefore overlap and must not be summed.
- The scan root's known net change is displayed separately. This includes files directly under that root, which do not appear as a child-folder row.

## Accounting semantics

For each changed file group with *both* before/after allocation known:

1. Pick the first (ordinal-sorted) observed path on each side as a canonical owner, even if the file has multiple hard links.
2. Subtract its previous allocated bytes from all ancestors of its old path up to the scan root.
3. Add its current allocated bytes to all ancestors of its new path up to the scan root.
4. Calculate the group's **net** contribution within each ancestor. Positive values contribute to folder growth; negative values to folder reduction.
5. Increment a changed-group count in every affected ancestor.

If either allocation is unknown, do **not** assign a known byte delta. Instead increment an unknown-allocation-group count for the affected directory ancestry. This preserves uncertainty.

Because the same file identity may move between folders without allocating any additional disk space, *folder inventory growth is not necessarily additional volume consumption*. The scanned root total, but not the sum of all folder rows, reconciles to the Phase 1C known file-allocation delta.

A hard-linked file is assigned to just one path (chosen by sorting) to avoid double counting; that does **not** establish a unique physical owner. Directory junctions, symlinks and inaccessible paths remain excluded from scanning as before.

## Storage and schema

Additive tables only:
- comparison_folder_summaries: per-comparison, per-folder cumulative known net, positive, negative, changed and unknown counts
- comparison_folder_summary_runs: algorithm/version completion marker

The existing snapshots, file records and comparison caches are not rewritten. The summary builder uses one SQLite transaction, rolls back on cancellation/errors, and checks that the scanned-root known net and unknown-group count match the existing file-level comparison. Previous builds can open the database without requiring the new folder tables.

The app and database remain read-only with respect to scanned source files. Building summaries does write extra SQLite metadata to the configured database location (prefer D: for C: monitoring).

## Windows verification checklist

1. Close the app and preserve any uncommitted local work.
2. Back up D:\StorageChronicleData\snapshots.db with the app closed.
3. Switch to feat/phase1d-folder-growth, run scripts/verify-local.ps1.
4. Confirm the existing suite and the new FolderSummaryTests pass, then run the WPF app.
5. Compare two existing snapshots using the File changes tab. Confirm no changes in existing numeric results.
6. Switch to Folder growth: confirm the root total, paged directory rows and growth/freed sorting.
7. Compare a controlled test directory with a nested folder, a renamed file, and a file moved between folders.
8. Confirm a direct file under the scan root affects root totals but does not create a fictitious child directory.
9. Confirm an incomplete folder calculation cannot damage the cached file comparison; retry should complete.
10. Confirm the on-disk database grows only by expected summary metadata and the app does not modify source files.

## Unverified limitations and release gates

- The code has not been built or run on Windows in this session.
- SQLite upserts across many ancestor directories can be I/O intensive; performance must be profiled on synthetic large snapshots before full C: comparisons.
- Two file paths that differ by case or Unicode normalization on Windows may be coalesced by the case-insensitive folder key; retain original paths as display labels.
- Root and parent rows contain descendant contributions and **must not be summed together**.
- No snapshot scheduling, USN journal, VSS, process attribution or automatic cleanup.
- Volume free-space changes remain provisional and unaccounted components remain unknown.
- Retention and deletion of *comparison cache metadata* are future tasks. Never manually delete source snapshots or the snapshot database.
