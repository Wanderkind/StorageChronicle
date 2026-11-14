namespace StorageChronicle.Core;

/// <summary>Compares observations by stable NTFS file identity where possible,
/// with a path-based fallback when identity could not be read.</summary>
public static class SnapshotComparer
{
    public static ComparisonReport Compare(DiskSnapshot baseline, DiskSnapshot current)
    {
        if (!PathComparer.SamePath(baseline.RootPath, current.RootPath))
            throw new ArgumentException("Snapshots must cover the same scan root.");
        if (!PathComparer.SameVolume(baseline.VolumeRoot, current.VolumeRoot))
            throw new ArgumentException("Snapshots are from different volumes.");

        var before = Index(baseline.Files);
        var after = Index(current.Files);
        var keys = new HashSet<string>(before.Keys, PathComparer.Instance);
        keys.UnionWith(after.Keys);
        var changes = new List<FileChange>();
        var unknownCount = 0;
        long knownDelta = 0;

        foreach (string key in keys)
        {
            before.TryGetValue(key, out var oldFiles);
            after.TryGetValue(key, out var newFiles);
            oldFiles ??= [];
            newFiles ??= [];
            FileChange? change = CompareGroups(oldFiles, newFiles);
            if (change is null) continue;
            if (change.AllocatedDeltaBytes is null) unknownCount++;
            else knownDelta = checked(knownDelta + change.AllocatedDeltaBytes.Value);
            changes.Add(change);
        }

        changes.Sort((a, b) =>
        {
            int magnitude = Nullable.Compare(
                b.AllocatedDeltaBytes is long bd ? Math.Abs((decimal)bd) : (decimal?)null,
                a.AllocatedDeltaBytes is long ad ? Math.Abs((decimal)ad) : (decimal?)null);
            return magnitude != 0 ? magnitude :
                PathComparer.Instance.Compare(a.PrimaryPath, b.PrimaryPath);
        });

        long? usedDelta = baseline.FreeBytesAfter.HasValue && current.FreeBytesAfter.HasValue
            ? checked(baseline.FreeBytesAfter.Value - current.FreeBytesAfter.Value) : null;
        long? unexplained = usedDelta.HasValue
            ? checked(usedDelta.Value - knownDelta) : null;

        var caveats = new List<string>();
        if (baseline.StartedUtc > current.StartedUtc)
            caveats.Add("The selected baseline is newer than the current snapshot; the comparison direction is reversed.");
        if (baseline.VolumeCapacityBytes.HasValue && current.VolumeCapacityBytes.HasValue &&
            baseline.VolumeCapacityBytes.Value != current.VolumeCapacityBytes.Value)
            caveats.Add("The volume capacity changed between snapshots; free-space changes require additional interpretation.");
        if (baseline.Files.Any(x => x.FileIdentity is null) ||
            current.Files.Any(x => x.FileIdentity is null))
            caveats.Add("Some file identities were unavailable, so a rename or replacement may appear as an addition and deletion.");
        if (!baseline.IsWholeVolume || !current.IsWholeVolume)
            caveats.Add("Directory-scoped files cannot explain changes elsewhere on the volume.");
        if (!baseline.IsComplete || !current.IsComplete)
            caveats.Add("At least one snapshot contains inaccessible or unreadable paths.");
        if (unknownCount > 0)
            caveats.Add($"{unknownCount} changed file group(s) have unknown allocated size.");
        int missingReadings = baseline.Files.Count(x => x.AllocatedBytes is null) +
                              current.Files.Count(x => x.AllocatedBytes is null);
        if (missingReadings > 0)
            caveats.Add($"{missingReadings} file observation(s) lack allocation measurements; allocation-only changes may be invisible.");
        if (baseline.Issues.Any(x => x.Code == "REPARSE_EXCLUDED") ||
            current.Issues.Any(x => x.Code == "REPARSE_EXCLUDED"))
            caveats.Add("Reparse points were excluded, including junctions and symlinks.");
        if (Drifted(baseline) || Drifted(current))
            caveats.Add("Free space changed during at least one scan; observations were not atomic.");
        if (usedDelta is null)
            caveats.Add("Actual volume free-space readings were unavailable.");
        caveats.Add("File allocation excludes some filesystem metadata, shadow storage and other system-managed usage.");

        return new ComparisonReport(baseline.Id, current.Id, changes, knownDelta,
            unknownCount, usedDelta, unexplained,
            true, caveats);
    }

    /// <summary>
    /// Shared single-file-identity group comparison used by both in-memory
    /// and database-streaming comparison; avoids diverging classification.
    /// </summary>
    internal static FileChange? CompareGroups(
        IReadOnlyList<FileRecord> oldFiles,
        IReadOnlyList<FileRecord> newFiles)
    {
            FileRecord? oldRepresentative = oldFiles.FirstOrDefault();
            FileRecord? newRepresentative = newFiles.FirstOrDefault();

            long? oldSize = oldRepresentative?.AllocatedBytes;
            long? newSize = newRepresentative?.AllocatedBytes;
            long oldLogical = oldRepresentative?.LogicalBytes ?? 0;
            long newLogical = newRepresentative?.LogicalBytes ?? 0;
            bool missingSize = (oldRepresentative is not null && oldSize is null) ||
                               (newRepresentative is not null && newSize is null);
            long? difference = missingSize ? null :
                checked((newSize ?? 0) - (oldSize ?? 0));

            var oldPaths = oldFiles.Select(x => x.Path)
                .OrderBy(x => x, PathComparer.Instance).ToArray();
            var newPaths = newFiles.Select(x => x.Path)
                .OrderBy(x => x, PathComparer.Instance).ToArray();
            bool pathsDiffer = !oldPaths.SequenceEqual(newPaths, PathComparer.Instance);

            ChangeKind? kind;
            if (oldRepresentative is null) kind = ChangeKind.Added;
            else if (newRepresentative is null) kind = ChangeKind.Deleted;
            else if (difference > 0) kind = ChangeKind.Grew;
            else if (difference < 0) kind = ChangeKind.Shrank;
            else if (oldLogical != newLogical) kind = ChangeKind.LogicalSizeChanged;
            else if (pathsDiffer)
                kind = oldPaths.Length == 1 && newPaths.Length == 1
                    ? ChangeKind.Moved : ChangeKind.LinksChanged;
            else kind = null;

            if (kind is null) return null;
            return new FileChange(kind.Value, newPaths.FirstOrDefault() ??
                oldPaths.FirstOrDefault() ?? "(unknown)", oldPaths, newPaths,
                oldSize, newSize, difference, oldLogical, newLogical);
    }

    private static bool Drifted(DiskSnapshot s) =>
        s.FreeBytesBefore.HasValue && s.FreeBytesAfter.HasValue &&
        s.FreeBytesBefore.Value != s.FreeBytesAfter.Value;

    private static Dictionary<string, List<FileRecord>> Index(
        IReadOnlyList<FileRecord> files)
    {
        var groups = new Dictionary<string, List<FileRecord>>(PathComparer.Instance);
        foreach (var file in files)
        {
            string key = file.FileIdentity is not null
                ? "ID:" + file.FileIdentity : "PATH:" + file.Path;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = [];
            group.Add(file);
        }
        return groups;
    }
}
