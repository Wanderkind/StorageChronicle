namespace StorageChronicle.Core;

/// <summary>
/// Non-destructive, non-elevated scan. Reparse points are intentionally excluded
/// (never followed), so the scan cannot accidentally traverse another volume.
/// </summary>
public sealed class SnapshotScanner
{
    public DiskSnapshot Capture(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Choose an existing directory.", nameof(root));

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(fullRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected scan root is a reparse point. Choose its real target directory instead.");
        string volumeRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(fullRoot) ?? throw new IOException("Cannot identify volume root."));

        var issues = new List<ScanIssue>();
        var records = new List<FileRecord>();
        var started = DateTimeOffset.UtcNow;
        var (capacity, freeBefore) = ReadSpace(volumeRoot, issues);
        var pending = new Stack<string>();
        pending.Push(fullRoot);

        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Enumerating via an explicit enumerator also captures failures that
            // occur in MoveNext(), after enumeration has already begun.
            try
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new(child, "REPARSE_EXCLUDED",
                                "Junction, symlink or other reparse point not followed.",
                                IssueSeverity.Info));
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(child);
                            continue;
                        }

                        var file = new FileInfo(child);
                        long logical = file.Length;
                        var observation = WindowsFileMetadata.Get(child);
                        if (observation.AllocationWarning is not null)
                            issues.Add(new(child, "ALLOCATION_UNAVAILABLE",
                                observation.AllocationWarning, IssueSeverity.Warning));
                        if (observation.IdentityWarning is not null)
                            issues.Add(new(child, "IDENTITY_UNAVAILABLE",
                                observation.IdentityWarning, IssueSeverity.Warning));
                        records.Add(new(file.FullName, logical, observation.AllocatedBytes,
                            observation.Identity, observation.LinkCount,
                            new DateTimeOffset(file.LastWriteTimeUtc)));
                    }
                    catch (Exception ex) when (IsExpectedFileException(ex))
                    {
                        issues.Add(new(child, "FILE_READ_FAILED", ex.Message, IssueSeverity.Error));
                    }
                }
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                issues.Add(new(directory, "DIRECTORY_ENUMERATION_FAILED", ex.Message,
                    IssueSeverity.Error));
            }
        }

        var (_, freeAfter) = ReadSpace(volumeRoot, issues);
        records.Sort((a, b) => PathComparer.Instance.Compare(a.Path, b.Path));
        return new DiskSnapshot(0, fullRoot, volumeRoot, started,
            DateTimeOffset.UtcNow, capacity, freeBefore, freeAfter, records, issues);
    }

    private static (long? Capacity, long? Free) ReadSpace(
        string root, List<ScanIssue> issues)
    {
        try
        {
            var volume = new DriveInfo(root);
            if (!volume.IsReady)
                throw new IOException("The volume is not ready.");
            return (volume.TotalSize, volume.TotalFreeSpace);
        }
        catch (Exception ex) when (IsExpectedFileException(ex) ||
                                    ex is ArgumentException)
        {
            issues.Add(new(root, "VOLUME_SPACE_UNAVAILABLE", ex.Message,
                IssueSeverity.Warning));
            return (null, null);
        }
    }

    private static bool IsExpectedFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or
            System.ComponentModel.Win32Exception or NotSupportedException;
}
