# Phase 1E — Scan coverage and issue diagnostics

**Status:** Implemented on a feature branch. Local Windows build, tests, and UI verification pending. Do not merge or release until validated.

## Purpose

A disk-space change report is only as credible as its source snapshots. The prior UI retained just the first 100 scan notices in memory and displayed them during a scan, but it offered no way to inspect the complete historical issue log.

Phase 1E adds a **Scan coverage** tab that reads existing SQLite snapshots without scanning the filesystem again. It is intentionally a *diagnostic* feature, not a claim of complete disk accounting.

The new UI lets the user:
- Select any snapshot listed in the existing 100-snapshot history selector.
- Read the number of observed files, recorded hard read errors, warnings, exclusions/informational notices, file allocation sizes that could not be measured, and unknown file identities.
- See a categorized breakdown of recorded issues by severity and exact code.
- Page through every saved issue (100 per page), filtered by all, read errors, warnings, or informational notices.
- Preserve the File changes and Folder growth functionality from Phases 1C and 1D.

The data queries use existing snapshot_files and snapshot_issues tables. No new schema or migration is needed. The first-100 preview shown immediately after a scan remains in the main status pane, and all historical issues remain accessible through the new tab.

## Interpretation and limitations

- A directory enumeration failure can hide an unknown number of files and unknown storage usage. We **cannot** quantify missing bytes or turn the issue count into a coverage percentage.
- A missing file identity (IDENTITY_UNAVAILABLE) makes rename/hard-link detection less reliable; a missing allocation measurement (ALLOCATION_UNAVAILABLE) directly compromises allocation accounting. These are different limitations.
- REPARSE_EXCLUDED is a deliberate non-traversal of a junction, symlink, or mount point. It is not necessarily a scan failure, but it is an exclusion that must remain visible.
- System-managed storage such as VSS, filesystem metadata, alternate streams, and deleted-but-open files may remain unaccounted for even when there are no recorded scan errors.
- Reading issue records does not read, inspect, delete or modify the original source files. SQLite may still create ordinary temporary/journal files.
- Read-only diagnostic queries do not rebuild or mutate saved snapshots, comparisons, or folder summaries.

## Windows validation

With StorageChronicle closed, back up D:\StorageChronicleData\snapshots.db and then run:

    git status --short
    git fetch origin
    git switch -c feat/phase1e-scan-coverage --track origin/feat/phase1e-scan-coverage
    .\scripts\verify-local.ps1 -RunApp

If the branch already exists locally, switch to it and pull with --ff-only instead. Preserve uncommitted work before switching.

Expected written tests: **48** (42 prior tests + 6 ScanCoverageTests). These tests have not been executed on Windows by the assistant.

Manual acceptance:
1. Existing snapshots and comparisons still work; the folder-growth totals and paging remain unchanged.
2. Open Scan coverage, choose the earlier System32 snapshot with the previously observed read failures and notices. The summary should show those persisted counts.
3. Filter errors, warnings, information, and all notices. Verify the list and page counts agree with the category counts.
4. Choose a clean small-directory scan. It should report zero read errors but **not** claim complete volume accounting.
5. Choose a snapshot with more than 100 issues. Navigate Previous/Next and ensure records are not repeated or lost.
6. Take a new snapshot. It should immediately become selectable in Scan coverage without restarting the application.
7. Confirm no scan or source-file modification is started merely by inspecting saved coverage.
8. Compare two snapshots with an incomplete scan and confirm the original caveats remain present.

## Release gates / follow-up

- Run the local SDK build with warnings as errors and the entire xUnit suite.
- Confirm WPF startup, tab switching, paging, filtering, and saved history reopening on Windows.
- Independently check how errors are classified on inaccessible Windows and junction-heavy directories.
- Consider optional export of issue evidence and reconciliation confidence labels in subsequent phases.
- Do not turn scan errors into a fabricated percentage of total disk bytes inspected.
