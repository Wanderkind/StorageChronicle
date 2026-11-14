# StorageChronicle pre-main audit — 2026-11-14

## Decision

**Draft integration candidate — NOT ready to merge or release.** Source reviewed against the retained
`fix/phase1g-vss-privilege-guidance` branch (`7638680`) and corrected on
`review/pre-main-hardening-20261114`. Local Windows verification of the final review head
is still required. GitHub Actions must not be used to compensate for missing local evidence.

## Confirmed by earlier user-provided Windows results

- Phases 1A–1C built, and the 34-test Phase 1C suite passed.
- The subsequent Scan coverage tab displayed saved notices.
- The Phase 1F Reconciliation view displayed file-growth, free-space change and an unassigned residual.
- Phase 1G's WMI query returned Unavailable with a provider initialization failure.
- A read-only vssadmin query succeeded in an administrator PowerShell session but returned exit code 2 / access denied inside an ordinary, non-elevated app process.
- The user reports the intervening stages working, but the **complete final 74-test suite and this review head are not yet validated**.

Do not extrapolate those historical observations into a verified release build.

## High-value correction in this review

**H1 — conflicting observations of one hard-linked file identity could be counted as precise growth.**

The original CompareGroups algorithm selected the first observed path within an identity group.
If a live scan encountered multiple hard links to the same underlying file while it changed,
the group's logical or allocated size readings could disagree. Selecting a representative
arbitrarily could yield a precise but unsupported allocated-byte delta.

**Correction:**
- Detect contradictory logical/allocated byte readings within a file-identity group.
- Classify that group as AllocationUnknown and leave its allocation delta null.
- Preserve known accounting for consistent hard-link observations.
- Use the same shared classification for in-memory and SQLite-backed comparisons.
- Increase the saved-comparison algorithm version from phase1c-v1 to
  phase1c-v2-hardlink-consistency; old saved comparison caches remain intact
  but are not reused as current results.
- Add regression tests for both comparers, folder summary reconciliation and
  old-cache version isolation.

**This correction is source-reviewed only. It must pass Windows tests before merge.**

## Other safety and accounting review observations

- SnapshotScanner uses non-following traversal for reparse points; inaccessible files and
  directories result in recorded issues, not a fabricated zero.
- Streaming capture persists each snapshot in a single SQLite transaction. A cancelled
  capture rolls back rather than exposing half a scan.
- Disk-backed comparison and folder summaries also use atomic transactions and pagination.
- The comparison residual measures **unaccounted difference**, not proven Windows/VSS usage.
- Full-volume free-space readings cannot be reconciled against an inventory of only System32
  or another individual directory.
- Shadow-copy storage is measurable only where the OS/provider/permissions allow it.
  The WMI provider failed on the user's Windows client. The fallback runs a fixed
  read-only vssadmin command and does not elevate the app automatically.
- Administrative vssadmin readings were not collected at the original snapshot timestamps;
  they must never be subtracted from older reconciliation residuals.
- SQLite metadata contains potentially private file paths. Do not commit snapshot databases.
- Existing snapshots and historical comparison caches must remain intact during the merge;
  no schema-destructive migration is proposed.

## Known limitations to carry into the first preview release

1. No coherent VSS provider measurement on ordinary non-elevated Windows processes.
   Administrator PowerShell is an external diagnostic, not automatic history.
2. No VSS/USN-supported atomic snapshot boundary. File growth during scanning can
   reduce comparability.
3. Unavailable file identity prevents guaranteed hard-link de-duplication or rename matching.
   The app gives a warning; some known allocation totals can remain provisional.
4. Disk-backed sorting and comparison cache consume SQLite temporary/on-disk storage;
   large C: scans and million-file comparisons need explicit performance assessment.
5. Snapshot capture and comparison are read-only for source files, but write metadata to
   the configured database directory. Choose D: when monitoring C:.
6. No background scheduling, installation packaging, privileged VSS helper, or automatic cleanup.
7. Native WPF UI still requires manual accessibility, interaction, cancellation and path-selection tests.
8. No verified signed installer or release artifact exists.

## Required merge gate: verify the exact review head on Windows

Run in a normal PowerShell session with the desktop application closed, after backing up
the existing SQLite database:

    cd D:\Dev\StorageChronicle
    git status --short
    git fetch origin
    git switch -c review/pre-main-hardening-20261114 --track origin/review/pre-main-hardening-20261114
    .\scripts\verify-release-candidate.ps1

If the branch already exists locally, switch to it and pull --ff-only instead.
The release verification script restores packages, builds with warnings as errors,
runs the suite with a TRX report, asserts at least **74** tests passed with zero
failures/skips, records the exact commit and SDK, and saves evidence under
artifacts/qa/release-<timestamp>/ (Git ignored).

Next, run:

    .\scripts\verify-release-candidate.ps1 -RunApp

Manual acceptance:
- Original snapshots remain selectable; no database content is lost.
- Compare older pairs and inspect file, folder, reconciliation and coverage tabs.
- Verify a controlled folder test including a rename, growth, deletion and a hard link.
- Verify cancellation and paging still work.
- Confirm the Shadow storage fallback says when the app is unelevated and that
  Copy admin commands copies only /on and /for queries.
- Confirm old phase1c-v1 cache rows coexist with new v2 comparison records when
  the same snapshot pair is recomputed.
- Keep a record of database file size before and after the comparison.
- For large C: scans, check actual error counts and temp-disk usage; do not infer
  precision merely from a low residual.

## Additional pre-release hardening

- The WPF comparison screen now uses separate progress flags for file comparison and folder summarization; late callbacks from one phase cannot suppress the other phase's progress.
- Added `scripts/build-preview-package.ps1`, which requires a clean working tree, exact-commit `verification.txt`, the matching TRX report, at least 74 passing tests, and zero failures or skips before invoking a local self-contained win-x64 publish.
- The packaging script creates a portable ZIP and SHA-256 manifest under ignored `artifacts/preview/`; it does **not** create a GitHub release, install software, alter monitored files or elevate privileges.
- Package generation and the extracted executable still require independent Windows testing; a passing Debug xUnit suite alone does not validate a Release publish.

### Local-only preview procedure (after the merge gate passes)

1. Run `scripts/verify-release-candidate.ps1` on the **exact review head** and preserve its printed evidence path.
2. Run `scripts/build-preview-package.ps1 -VerificationFile <absolute path to verification.txt>` on that same commit.
3. Verify the ZIP's SHA-256 against the manifest, extract the entire archive and launch `StorageChronicle.App.exe` as an ordinary user.
4. Confirm previous SQLite snapshots remain selectable; perform file/folder/coverage/reconciliation/VSS smoke tests on the extracted build before any GitHub release.

## Integration and release policy

- Keep this PR in **draft** until the exact commit's verification evidence and
  manual acceptance are attached or summarized.
- Do not merge directly by force, rewrite main history, delete the active working branch,
  create a release or launch GitHub Actions as an implicit validation method.
- After passing the gate and reviewing results, merge the PR into main.
- Only then build and independently smoke-test a Windows release package.
- Subsequent features should branch from updated main and close via short-lived PRs.
