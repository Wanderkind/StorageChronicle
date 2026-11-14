# Phase 1G — On-demand Windows shadow-copy storage observations

**Status:** Source committed; Windows build, tests and WMI runtime acceptance pending. Do not merge or release.

## Purpose

The prior reconciliation report can show unexplained space, but cannot establish that it was consumed by shadow copies. Phase 1G adds a **Shadow storage** tab with an explicit "Measure VSS now" action. It records separate, timestamped measurements of WMI-reported shadow-copy differential-area storage.

Microsoft documents Win32_ShadowStorage in ROOT\CIMV2. Each association has:
- Volume: original volume (which is shadowed).
- DiffVolume: volume physically holding shadow-copy differential data.
- UsedSpace: used bytes **inside the shadow storage allocation**.
- AllocatedSpace: allocated differential-area bytes on DiffVolume.
- MaxSpace: configured maximum, not currently consumed space.

The app retrieves the read-only properties and resolves referenced Win32_Volume objects to drive letters where possible. It never calls Create, Delete, Resize or other mutating WMI methods. It never attempts privilege escalation.

## What is recorded

- Observation timestamp (UTC), query status (Complete / Partial / Unavailable), provider failure message if present.
- All enumerated volume-association references and any resolvable drive letters.
- Used, allocated, and maximum bytes, represented as nullable numbers.
- The database stores observations in new additive tables, shadow_measurements and shadow_areas, independent of all existing snapshot tables.

Observations are **on demand**. An observation taken at 21:00 cannot be attributed to a filesystem snapshot captured at 18:00; the UI must not imply otherwise.

## Comparison

When at least two timestamped readings exist, the UI displays the difference in *allocated* differential-area space for the selected drive (the volume on which VSS stores its data).

- Positive allocated delta means more differential-area space was allocated at the later reading.
- Negative allocated delta means less was allocated.
- Null means no defensible number is available because a query is partial/unavailable, volume mapping is unknown, or allocated bytes were not reported.
- Complete results with no reported associations yield 0 for *the WMI result*, not a proof that the system has no shadow copies or system-managed storage.
- The comparison does not subtract a VSS delta from the unexplained reconciliation residual, because the measurements may cover different time windows or overlap other accounting categories.

## Build and test

1. Close StorageChronicle and preserve local uncommitted work.
2. Back up the local SQLite snapshot database while the app is closed. If it is open, use SQLite's backup API instead of a raw copy.
3. Checkout feat/phase1g-vss-observations and run scripts/verify-local.ps1 -RunApp.
4. The added System.Management package is pinned to 10.0.14; NuGet restore is required.
5. Expected written test count after the Windows client fallback: 66 (previous 56 plus 7 Phase 1G tests and 3 command-validation tests). These tests have NOT been executed by the assistant.
6. Verify the existing File changes, Folder growth, Reconciliation and Scan coverage tabs still work.
7. Open Shadow storage. Click Measure VSS now. A reading should be saved even if WMI reports Unavailable due to provider permissions; no elevation prompt is initiated by the app.
8. Click again at a later time. Verify a numerical delta is shown **only** when both readings report Complete with known storage-drive mappings.
9. Inspect C: and D: associations. Do not interpret shadow-copy storage for C: placed on D: as free-space use on C:.
10. Verify existing filesystem snapshots and comparisons remain intact after using the new tab.

## Windows client fallback (2026-11-14)

A Windows 11 test returned `ManagementException: 초기화 실패입니다` (initialization failure) when querying `Win32_ShadowStorage`. The WMI error is recorded as Unavailable, which **does not imply zero shadow-copy storage**. Microsoft's legacy class reference lists no supported Windows client version.

The app now includes a separate **Check via vssadmin** button. It:
- Resolves the selected scan root to a drive letter.
- Executes **only** `vssadmin list shadowstorage /on=X:` through `ProcessStartInfo.ArgumentList` with `UseShellExecute=false`.
- Captures standard output and errors, limits the operation to 20 seconds, and shows the timestamp and exit code.
- Does not request elevation, modify VSS, or parse localized units into numeric measurements.
- Does not store this textual diagnostic as a numerical VSS reading or use it to adjust a historical reconciliation residual.

If the command reports access denied, open an elevated PowerShell window *yourself* and run:

    vssadmin list shadowstorage /on=C:

For associations **of shadow copies originating on C:** (rather than stored on C:), use:

    vssadmin list shadowstorage /for=C:

These are both read-only. Do **not** use `vssadmin resize`, `delete` or `create` to debug this feature.

Verify locally:
1. Run the repo's scripts/verify-local.ps1 -RunApp and confirm the three added VssAdminInspectorTests cases pass.
2. Open Shadow storage and click **Check via vssadmin** with C: selected. Confirm a command line, timestamp, exit code and raw response appear.
3. Confirm a WMI-unavailable reading remains unavailable, while the independent command can display results if permissions permit.
4. Confirm existing snapshots and prior WMI measurements remain unchanged. The CLI output is intentionally not saved in the snapshot database.

## Confirmed Windows client behavior (2026-11-14)

On the user's Windows 11 machine, `vssadmin list shadowstorage /on=C:` returned exit code 2 from the unelevated WPF app, while the same read-only command succeeded in an administrator PowerShell session. This strongly suggests elevation/permissions are the immediate difference, but an exit code alone is not a universal error classification.

The successful elevated listing showed one association storing approximately 1.52 GB **used** and 2.00 GB **allocated** on C:, with a 4.32 GB configured maximum. A second C:-storage association showed zero usage/allocation and an unbounded maximum. These were *current values* and do not establish any change during older filesystem snapshot intervals.

The corrective branch adds `Copy admin commands`, which copies only these fixed queries for the selected drive:

    vssadmin list shadowstorage /on=C:
    vssadmin list shadowstorage /for=C:

It also checks whether the WPF process currently has an elevated administrator token and provides conservative guidance if a read-only query fails. It does not elevate the app, schedule a privileged process, execute untrusted input as a command, or import localized text into numerical VSS history. The /on and /for views can overlap: **do not add their totals together**.

Five tests in VssAdminGuidanceTests cover safe fixed commands and privilege messaging. The new test suite should have 71 tests in total, pending local Windows verification.

## Known limitations and follow-up

- WMI Win32_ShadowStorage availability and access rights vary across Windows editions, configuration and privileges. Unavailable must not be treated as zero.
- The original source volume may be different from the differential-area storage volume. Allocation should be attributed to the latter.
- WMI Get calls can be slow or time out; the query runs off the UI thread but cannot be cancelled mid-WMI call in this first version.
- An unlettered volume cannot be mapped to a drive letter and causes drive-specific numeric totals to be unknown rather than silently ignored.
- WMI is not a replacement for a scan of filesystem metadata, VSS provider internals, NTFS metadata, and files inaccessible to the caller.
- Future work: align reliable independent system measurements with filesystem snapshot times; add a comparison audit and separate categories for Windows metadata, pagefile, hibernation, restore points, and other system files where actually measurable.
