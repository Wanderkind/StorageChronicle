# Phase 1F — Reconciliation evidence

**Status:** Implemented on a feature branch. Windows build and tests have NOT yet run. Do not merge or release until verified.

## Purpose

The amount of free space reported by Windows is not the same as the sum of visible file allocation sizes. Phase 1F adds a dedicated **Reconciliation** tab following a comparison of two snapshots, making the accounting difference and the evidence limits explicit.

It uses existing persisted snapshot and comparison records. No rescan, schema migration, privilege elevation, cleanup, or change to observed source files is required. The only extension to the scan coverage model is returning its already persisted volume-capacity reading.

## Definitions

- Observed free-space loss = baseline end-of-scan free bytes minus current end-of-scan free bytes. Positive = less free space.
- Volume used-space change = observed free-space loss **only when both stored volume capacities are known and equal**. If capacity changed or is missing, leave used-space change and residual unknown. We do not report zero.
- Known file allocation delta = de-duplicated allocation changes from Phase 1C's saved comparison. It is partial where file sizes, identities, or file coverage are unknown.
- Unexplained residual = volume used-space change minus known file allocation delta, when calculable. A residual is not attributable to Windows, shadow copies, any particular file, or a particular application without independent evidence.
- During-scan free-space loss = free bytes at start minus free bytes at end of each individual scan.
- Between-scans free-space loss = baseline end free bytes minus current start free bytes. This separates intervals as observations, not causes.
- Coverage diagnostics show both snapshots' recorded errors, missing allocation sizes, missing identities, exclusions, and whole-volume vs directory scope.

## Interpretation classes

- Unavailable: missing free-space measurements, unknown capacity, or changed volume capacity prevent a constant-capacity used-space reconciliation.
- Limited: a numerical reconciliation exists but scan scope, recorded errors, missing metadata, unknown deltas, excluded reparse points, or scan-time free-space drift undermine completeness.
- Provisional: the numerical calculation is possible and no specific monitored coverage/consistency limitation is recorded; it is **still not a proof of complete volume accounting**.

All three statuses retain the caution that Windows-managed metadata, VSS, inaccessible data, live filesystem changes, and allocation measurement differences can remain unexplained. Phase 1F does not claim a coverage percentage or a causal diagnosis.

## Implementation

- ReconciliationEvaluator.Evaluate: pure, deterministic computation and consistency checks.
- SqliteSnapshotRepository.LoadReconciliation: read-only access to existing persisted comparison and two scan-coverage records.
- WPF Reconciliation tab: selected comparison, free-space before/after values, capacity change, known file delta, provisional residual, scan drift, and explicit limitations.
- Eight ReconciliationTests cases: constant and changed capacity; unavailable readings; directory scope, hard read errors, missing file metadata; comparison consistency; database integration; nonexistent ID.

## Validation on Windows

1. Preserve uncommitted work, close StorageChronicle, back up D:\StorageChronicleData\snapshots.db.
2. Switch to feat/phase1f-reconciliation and run .\scripts\verify-local.ps1 -RunApp.
3. Expected test count: 56 (48 previous tests + 8 new tests), no warnings or failures. This has not been verified.
4. Compare two existing System32 snapshots. Open Reconciliation. Inspect free-space loss, known allocation delta and unassigned residual.
5. Confirm status Limited for directory-scoped scans and any snapshots with recorded read failures.
6. Confirm the existing File changes, Folder growth and Scan coverage tabs still work.
7. Change the chosen snapshot pair, compare again, and verify the previous reconciliation report is cleared before new evidence appears.
8. Make sure no original snapshot records are modified and no source files are changed.

## Remaining work

- Profile full C: snapshots and comparisons under real Windows permissions and filesystem features.
- Add independent measurements of system-managed space (e.g. VSS) as *separate, evidenced observations*, never guesses derived from the residual.
- Add evidence export and versioned review of measurement methods.
- Do not create a cleanup or deletion recommendation merely because a residual is positive.
