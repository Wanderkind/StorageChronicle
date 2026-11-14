# StorageChronicle

A local, read-only Windows disk-space **snapshot comparator** built with .NET 10 and WPF.

**Status:** Phase 1A built and ran on the user's Windows machine; the Phase 1B streaming changes are **not yet compiled or tested on Windows**. Do not use the results for cleanup decisions without independently verifying the file paths and free-space measurements.

## What it does now

- Scans an existing directory or entire volume root (such as C:\). The **Browse…** button opens the native Windows folder picker; selecting a folder updates the path, but does not start scanning automatically.
- Records file paths, logical size, best-effort NTFS allocated size, file identity, link count, and timestamps.
- Records scan errors, excluded reparse points, volume capacity, and free space both before and after a scan.
- Stores snapshot metadata locally in SQLite in a single transaction. Phase 1B streams scanned records directly to SQLite rather than holding the entire file list in RAM; cancellation and database errors roll back the snapshot.
- Compares two snapshots of the same scan root, grouping hard links by file identity when available.
- Labels new, removed, grown, shrunk, moved, and changed-link file groups.
- Shows observed volume free-space loss separately from the **provisional residual** it cannot explain.
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
- Inaccessible paths are reported, not silently assumed empty.
- Some filesystems do not provide the Windows metadata this tool expects.
- Shadow copy storage, filesystem metadata, security journals, deleted-but-open files, alternate data streams, and system-managed space are not individually attributed.
- Allocated size from GetCompressedFileSizeW is not a complete measurement of all physical volume consumption.
- A hard link outside a scanned scope can complicate attribution to that directory.
- Windows APIs may require privileges for comprehensive inspection. This app does not elevate or bypass access controls.
- The comparison engine still loads both saved file inventories into memory; full-volume comparison remains a performance limitation until disk-backed comparison is implemented.
- The SQLite database contains filenames and paths: treat it as **private local data**, never commit it.
- On platforms other than Windows, the scanner records allocated size as unknown; the desktop app requires Windows.

See [Phase 1A scope and checks](docs/PHASE1A.md) and [Phase 1B streaming validation](docs/PHASE1B.md).
