namespace StorageChronicle.Core;

/// <summary>
/// Read-only recursive filesystem scan. Records are emitted to callbacks as they
/// are observed, so a full-volume scan need not retain every file in memory.
/// Reparse points are recorded as exclusions, never traversed.
/// </summary>
public sealed class SnapshotScanner
{
    internal readonly record struct ScanRoot(string RootPath, string VolumeRoot);

    internal static ScanRoot ResolveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Choose an existing directory.", nameof(root));

        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected scan root is a reparse point. Choose its real target directory instead.");

        string volume = Path.GetPathRoot(full)
            ?? throw new IOException("Cannot identify volume root.");
        string normalized = PathComparer.SamePath(full, volume)
            ? volume : Path.TrimEndingDirectorySeparator(full);
        return new(normalized, volume);
    }

    /// <summary>
    /// Stream records and notices. Callback exceptions propagate and abort the scan;
    /// they are NOT disguised as filesystem read errors.
    /// </summary>
    public DiskScanSummary Scan(
        string root,
        Action<FileRecord> onFile,
        Action<ScanIssue> onIssue,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(onFile);
        ArgumentNullException.ThrowIfNull(onIssue);
        ScanRoot resolved = ResolveRoot(root);

        int files = 0, notices = 0, errors = 0;

        void EmitIssue(ScanIssue issue)
        {
            onIssue(issue);
            notices = checked(notices + 1);
            if (issue.Severity == IssueSeverity.Error)
                errors = checked(errors + 1);
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        var (capacity, freeBefore, startError) = ReadSpace(resolved.VolumeRoot);
        if (startError is not null)
            EmitIssue(startError);

        var pending = new Stack<string>();
        pending.Push(resolved.RootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerator<string> enumerator;
            try
            {
                enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                EmitIssue(new(directory, "DIRECTORY_ENUMERATION_FAILED",
                    ex.Message, IssueSeverity.Error));
                continue;
            }

            using (enumerator)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string child;
                    try
                    {
                        if (!enumerator.MoveNext())
                            break;
                        child = enumerator.Current;
                    }
                    catch (Exception ex) when (IsExpectedFileException(ex))
                    {
                        EmitIssue(new(directory, "DIRECTORY_ENUMERATION_FAILED",
                            ex.Message, IssueSeverity.Error));
                        break;
                    }

                    FileRecord? record = null;
                    ScanIssue? fileError = null;
                    ScanIssue? reparse = null;
                    ScanIssue? allocationWarning = null;
                    ScanIssue? identityWarning = null;
                    string? nextDirectory = null;

                    // Only OS metadata operations are inside this catch. A SQLite
                    // exception thrown by a callback must abort the entire scan.
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            reparse = new(child, "REPARSE_EXCLUDED",
                                "Junction, symlink or other reparse point not followed.",
                                IssueSeverity.Info);
                        }
                        else if ((attributes & FileAttributes.Directory) != 0)
                        {
                            nextDirectory = child;
                        }
                        else
                        {
                            var file = new FileInfo(child);
                            long logical = file.Length;
                            var metadata = WindowsFileMetadata.Get(child);
                            if (metadata.AllocationWarning is not null)
                                allocationWarning = new(child, "ALLOCATION_UNAVAILABLE",
                                    metadata.AllocationWarning, IssueSeverity.Warning);
                            if (metadata.IdentityWarning is not null)
                                identityWarning = new(child, "IDENTITY_UNAVAILABLE",
                                    metadata.IdentityWarning, IssueSeverity.Warning);

                            record = new(file.FullName, logical,
                                metadata.AllocatedBytes, metadata.Identity,
                                metadata.LinkCount, new DateTimeOffset(file.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception ex) when (IsExpectedFileException(ex))
                    {
                        fileError = new(child, "FILE_READ_FAILED", ex.Message,
                            IssueSeverity.Error);
                    }

                    if (fileError is not null) { EmitIssue(fileError); continue; }
                    if (reparse is not null) { EmitIssue(reparse); continue; }
                    if (nextDirectory is not null) { pending.Push(nextDirectory); continue; }
                    if (allocationWarning is not null) EmitIssue(allocationWarning);
                    if (identityWarning is not null) EmitIssue(identityWarning);
                    if (record is not null)
                    {
                        onFile(record);
                        files = checked(files + 1);
                        if (files % 250 == 0)
                            progress?.Report(new(files, notices, directory));
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (_, freeAfter, endError) = ReadSpace(resolved.VolumeRoot);
        if (endError is not null) EmitIssue(endError);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(files, notices, resolved.RootPath));

        return new(resolved.RootPath, resolved.VolumeRoot,
            started, DateTimeOffset.UtcNow, capacity, freeBefore, freeAfter,
            files, notices, errors);
    }

    /// <summary>
    /// Compatibility path for small scans and deterministic tests; retains records
    /// in memory. The WPF app uses repository.CaptureAndSave instead.
    /// </summary>
    public DiskSnapshot Capture(string root, CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var records = new List<FileRecord>();
        var issues = new List<ScanIssue>();
        var summary = Scan(root, records.Add, issues.Add, cancellationToken, progress);
        records.Sort((a, b) => PathComparer.Instance.Compare(a.Path, b.Path));
        return new(0, summary.RootPath, summary.VolumeRoot, summary.StartedUtc,
            summary.FinishedUtc, summary.VolumeCapacityBytes,
            summary.FreeBytesBefore, summary.FreeBytesAfter, records, issues);
    }

    private static (long? Capacity, long? Free, ScanIssue? Issue) ReadSpace(string root)
    {
        try
        {
            var volume = new DriveInfo(root);
            if (!volume.IsReady)
                throw new IOException("The volume is not ready.");
            return (volume.TotalSize, volume.TotalFreeSpace, null);
        }
        catch (Exception ex) when (IsExpectedFileException(ex) ||
                                    ex is ArgumentException)
        {
            return (null, null, new(root, "VOLUME_SPACE_UNAVAILABLE",
                ex.Message, IssueSeverity.Warning));
        }
    }

    private static bool IsExpectedFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or
            System.ComponentModel.Win32Exception or NotSupportedException;
}
