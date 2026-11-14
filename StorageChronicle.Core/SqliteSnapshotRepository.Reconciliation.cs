namespace StorageChronicle.Core;

/// <summary>
/// Builds evidence solely from stored comparison and scan records, without
/// modifying source files or changing cached comparison results.
/// </summary>
public sealed partial class SqliteSnapshotRepository
{
    public ReconciliationEvidence LoadReconciliation(long comparisonId)
    {
        StoredComparison comparison = LoadComparison(comparisonId);
        SnapshotCoverage baseline = LoadScanCoverage(comparison.BaselineId);
        SnapshotCoverage current = LoadScanCoverage(comparison.CurrentId);
        return ReconciliationEvaluator.Evaluate(comparison, baseline, current);
    }
}
