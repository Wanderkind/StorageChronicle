# StorageChronicle

A local, read-only Windows disk-space **snapshot comparator** built with .NET 10 and WPF.

**Status:** Phases 1A–1G are consolidated in a **draft pre-main review branch**. Historical Windows checks confirmed many functions, but the final review head, its comparison-accounting correction, and the full 74-test suite have **not yet passed Windows verification**. Do not merge or release until the documented gate passes. An ordinary desktop process cannot run privileged VSS diagnostics; no automatic elevation is performed.

## What it does now

- Scans an existing directory or entire volume root (such as C:\). The **Browse…** button opens the native Windows folder picker; selecting a folder updates the path, but does not start scanning automatically.
- Records file paths, logical size, best-effort NTFS allocated size, file identity, link count, and timestamps.
- Records scan errors, excluded reparse points, volume capacity, and free space both before and after a scan.
- Stores snapshot metadata locally in SQLite in a single transaction. Phase 1B streams scanned records directly to SQLite rather than holding the entire file list in RAM; cancellation and database errors roll back the snapshot.
- Compares two snapshots of the same scan root, grouping hard links by file identity when available. Phase 1C streams identities from SQLite and saves changes in a comparison cache, rather than loading both entire snapshots into managed memory.
- Labels new, removed, grown, shrunk, moved, and changed-link file groups.
- Phase 1D adds a **Folder growth** tab: cumulative known allocation changes across nested directories, including growth/reduction columns and unknown-allocation counts. It pages and sorts folder summaries from SQLite.
- Phase 1E adds **Scan coverage**, allowing inspection of saved scan read failures, missing allocation and identity metadata, exclusion categories, and the complete paginated issue list—without rescanning.
- Phase 1F adds a **Reconciliation** tab: observed free-space loss, constant-capacity used-space change, known file allocation delta, unassigned residual, scan-time free-space drift, and explicit measurement limitations.
- Separates observed free-space loss from the **provisional residual** it cannot explain; missing or changed volume capacity disables used-space reconciliation rather than substituting zero.
- Phase 1G adds an opt-in **Shadow storage** tab with timestamped WMI measurements (may be unavailable on Windows clients) and a separate read-only **Check via vssadmin** fallback showing unparsed localized diagnostic output. Neither retroactively attributes VSS usage to earlier scans nor subtracts it from residuals.
- The Windows client privilege-guidance correction adds **Copy admin commands** for fixed /on and /for checks and reports whether the app has an elevated administrator token. No automatic UAC or elevation is performed.
- Never deletes or modifies scanned files.

## Start on Windows

Requires the .NET 10 SDK and NuGet package restore:

    dotnet restore
    dotnet build
    dotnet test
    dotnet run --project StorageChronicle.App

Take a snapshot of a test directory, change a few files, take another snapshot, and compare them. Scan C:\ only after this smaller test works.

The database is saved to D:\StorageChronicleData\snapshots.db if D:\ exists; otherwise to %LOCALAPPDATA%\StorageChronicle\snapshots.db. Override with the STORAGECHRONICLE_DB environment variable (absolute path to a .db file). **Keep the database off the monitored volume whenever practical**, or it will contribute to the volume's own usage changes.

## Limitations

This is a file-inventory comparator, not yet a full Windows disk accounting engine.

- Scans are not atomic. Live files can change between observations.
- NTFS file identity is used where obtainable; fallback path matching cannot prove renames.
- Reparse points (junctions, symlinks, mount points) are deliberately not followed.
- Inaccessible paths are reported, not silently assumed empty. **Zero recorded scan errors is not proof of complete disk coverage**; no coverage percentage is inferred.
- Some filesystems do not provide the Windows metadata this tool expects.
- Shadow copy storage, filesystem metadata, security journals, deleted-but-open files, alternate data streams, and system-managed space are not individually attributed.
- Allocated size from GetCompressedFileSizeW is not a complete measurement of all physical volume consumption.
- A hard link outside a scanned scope can complicate attribution to that directory.
- Windows APIs may require privileges for comprehensive inspection. This app does not elevate or bypass access controls.
- Comparison detail and folder summaries are paged (100 rows at a time). Folder totals include descendants and are non-additive across ancestors; file moves can shift folder allocation without consuming volume space. Hardlinks have one canonical attributed path.
- Comparison detail is paged (100 changes at a time). SQLite may use temporary disk files for large sorts, and stored comparison results can increase the database size. The original in-memory comparison engine remains for compatibility and unit testing; the WPF app uses the disk-backed path.
- The SQLite database contains filenames and paths: treat it as **private local data**, never commit it.
- On platforms other than Windows, the scanner records allocated size as unknown; the desktop app requires Windows.

For the final integration gate, see [Pre-main audit and release verification](docs/PRE_MAIN_AUDIT_20261114.md) and run `scripts/verify-release-candidate.ps1` on Windows. It generates Git-ignored TRX evidence under `artifacts/qa/`. Do not treat an unassigned volume-space residual as a file-cleanup recommendation.

See [Phase 1A scope and checks](docs/PHASE1A.md), [Phase 1B streaming validation](docs/PHASE1B.md),  [Phase 1C disk-backed comparison](docs/PHASE1C.md), and [Phase 1D folder growth summaries](docs/PHASE1D.md). See also [Phase 1E scan coverage](docs/PHASE1E.md) [Phase 1F reconciliation](docs/PHASE1F.md), and [Phase 1G VSS observations](docs/PHASE1G.md).
