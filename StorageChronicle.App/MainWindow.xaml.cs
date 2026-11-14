using System.Globalization;
using System.Windows;
using StorageChronicle.Core;

namespace StorageChronicle.App;

public partial class MainWindow : Window
{
    private SqliteSnapshotRepository? _repository;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _compareCancellation;
    private StoredComparison? _activeComparison;
    private StoredFolderSummary? _activeFolderSummary;
    private bool _folderGrowthFirst = true;
    private bool _loadingFolderPage;
    private int _folderOffset;
    private bool _updatingChoices;
    private bool _loadingPage;
    private int _pageOffset;
    private const int PageSize = 100;

    private sealed record SnapshotChoice(long Id, string RootPath, DateTimeOffset When, int Files)
    {
        public string Display =>
            $"#{Id} · {When.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {RootPath} · {Files:N0} files";
    }

    private sealed record ChangeRow(string Kind, string Delta,
        string Before, string After, string Path);

    private sealed record FolderRow(string Net, string Growth, string Reduction,
        string ChangedGroups, string UnknownGroups, string FolderPath);

    public MainWindow()
    {
        InitializeComponent();
        RootPathInput.Text = System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        try
        {
            string dbPath = DetermineDatabasePath();
            _repository = new SqliteSnapshotRepository(dbPath);
            DatabaseLocationText.Text = "Metadata database (local only): " + dbPath;
            RefreshChoices();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not initialize database: " + ex.Message;
            ScanButton.IsEnabled = CompareButton.IsEnabled = false;
        }
    }

    private static string DetermineDatabasePath()
    {
        string? configured = Environment.GetEnvironmentVariable("STORAGECHRONICLE_DB");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // Prefer D: if available so monitoring C: does not grow its own database.
        if (System.IO.Directory.Exists(@"D:\"))
            return @"D:\StorageChronicleData\snapshots.db";

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageChronicle", "snapshots.db");
    }

    private void RefreshChoices()
    {
        if (_repository is null) return;
        var choices = _repository.List()
            .Select(s => new SnapshotChoice(s.Id, s.RootPath, s.StartedUtc, s.FileCount))
            .ToList();
        ClearComparisonView();
        _updatingChoices = true;
        try
        {
            CurrentChoice.ItemsSource = choices;
            CurrentChoice.SelectedIndex = choices.Count > 0 ? 0 : -1;
        }
        finally
        {
            _updatingChoices = false;
        }
        UpdateBaselineChoices();
    }

    private void CurrentChoice_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingChoices)
        {
            ClearComparisonView();
            UpdateBaselineChoices();
        }
    }

    private void BaselineChoice_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingChoices)
        {
            ClearComparisonView();
            UpdateCompareButtonState();
        }
    }

    private void UpdateBaselineChoices()
    {
        var current = CurrentChoice.SelectedItem as SnapshotChoice;
        var all = CurrentChoice.ItemsSource as IEnumerable<SnapshotChoice>;
        var candidates = all?.Where(x => current is not null &&
                x.Id < current.Id &&
                PathComparer.SamePath(x.RootPath, current.RootPath)).ToArray()
            ?? [];

        _updatingChoices = true;
        try
        {
            BaselineChoice.ItemsSource = candidates;
            BaselineChoice.SelectedIndex = candidates.Length > 0 ? 0 : -1;
        }
        finally
        {
            _updatingChoices = false;
        }

        ComparisonHintText.Text = current is null ? "Take a snapshot to begin."
            : candidates.Length == 0
                ? "No earlier snapshot for this directory. Scan the same directory again to enable comparison."
                : $"Select one of {candidates.Length:N0} earlier snapshot(s) of the same directory.";
        UpdateCompareButtonState();
    }

    private void UpdateCompareButtonState()
    {
        CompareButton.IsEnabled =
            _repository is not null && _scanCancellation is null &&
            _compareCancellation is null && !_loadingPage &&
            BaselineChoice.SelectedItem is SnapshotChoice baseline &&
            CurrentChoice.SelectedItem is SnapshotChoice current &&
            baseline.Id < current.Id &&
            PathComparer.SamePath(baseline.RootPath, current.RootPath);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string selectedPath = RootPathInput.Text.Trim();
        string startingDirectory = System.IO.Directory.Exists(selectedPath)
            ? selectedPath
            : System.IO.Path.GetPathRoot(selectedPath) is string driveRoot &&
              System.IO.Directory.Exists(driveRoot)
                ? driveRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a directory to scan",
            Multiselect = false,
            InitialDirectory = startingDirectory
        };

        // Cancelling the dialog does not modify the current path or create a snapshot.
        if (dialog.ShowDialog(this) == true &&
            System.IO.Directory.Exists(dialog.FolderName))
        {
            RootPathInput.Text = dialog.FolderName;
            RootPathInput.CaretIndex = RootPathInput.Text.Length;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var store = _repository;
        if (store is null) return;
        string root = RootPathInput.Text.Trim();
        if (!System.IO.Directory.Exists(root))
        {
            MessageBox.Show("Select an existing directory or volume root.",
                "Invalid path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Scanning " + root + " (no files will be modified) …";
        bool scanRunning = true;
        try
        {
            var token = _scanCancellation.Token;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (scanRunning)
                    StatusText.Text = $"Scanning {root}: {p.FileCount:N0} files, " +
                        $"{p.NoticeCount:N0} notices · {p.CurrentDirectory}";
            });
            SavedScanResult saved = await Task.Run(
                () => store.CaptureAndSave(root, token, progress));
            scanRunning = false;
            RefreshChoices();
            var summary = saved.Summary;
            StatusText.Text = $"Saved snapshot #{saved.Id}: {summary.FileCount:N0} files, " +
                $"{summary.ErrorCount:N0} read failures, " +
                $"{summary.NoticeCount:N0} total notices. " +
                $"Elapsed: {(summary.FinishedUtc - summary.StartedUtc).TotalSeconds:F1}s.";
            SummaryText.Text = string.Join(Environment.NewLine,
                saved.FirstIssues.Select(i =>
                    $"[{i.Severity}] {i.Code}: {i.Path} — {i.Detail}"));
            if (summary.NoticeCount > saved.FirstIssues.Count)
                SummaryText.AppendText($"\n… and {summary.NoticeCount - saved.FirstIssues.Count} more notices.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled. No incomplete snapshot was saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            scanRunning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            UpdateCompareButtonState();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _scanCancellation?.Cancel();

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        var store = _repository;
        if (store is null ||
            BaselineChoice.SelectedItem is not SnapshotChoice baseline ||
            CurrentChoice.SelectedItem is not SnapshotChoice current)
        {
            MessageBox.Show("Select a baseline and a current snapshot.");
            return;
        }
        if (baseline.Id >= current.Id ||
            !PathComparer.SamePath(baseline.RootPath, current.RootPath))
        {
            MessageBox.Show("Select an earlier baseline from the same directory.");
            return;
        }

        _compareCancellation = new CancellationTokenSource();
        BaselineChoice.IsEnabled = false;
        CurrentChoice.IsEnabled = false;
        ScanButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        CancelCompareButton.IsEnabled = true;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        StatusText.Text = "Comparing saved snapshots on disk …";
        ClearComparisonView();
        bool showComparisonProgress = true;
        try
        {
            var token = _compareCancellation.Token;
            var progress = new Progress<ComparisonProgress>(p =>
            {
                if (showComparisonProgress)
                    StatusText.Text = $"Comparing: {p.ProcessedGroups:N0} file groups reviewed, " +
                        $"{p.ChangedGroups:N0} changes …";
            });
            StoredComparison report = await Task.Run(
                () => store.CompareAndStore(baseline.Id, current.Id, token, progress));
            showComparisonProgress = false;
            _activeComparison = report;
            await LoadChangePageAsync(0);

            var folderProgress = new Progress<FolderBuildProgress>(p =>
            {
                if (showComparisonProgress)
                    StatusText.Text = $"Summarizing folders: {p.ProcessedChanges:N0} " +
                        $"of {p.TotalChanges:N0} file changes …";
            });
            StoredFolderSummary folders = await Task.Run(() =>
                store.BuildFolderSummary(report.Id, token, folderProgress));
            showComparisonProgress = false;
            _activeFolderSummary = folders;
            FolderTotalsText.Text =
                $"{folders.RootPath} · Net known allocation change: " +
                $"{Signed(folders.RootKnownNetDeltaBytes)} · " +
                $"{folders.FolderCount:N0} descendant folders with changes · " +
                $"{folders.RootUnknownAllocationGroups:N0} unknown groups";
            await LoadFolderPageAsync(0);
            StatusText.Text = $"Comparison #{baseline.Id} → #{current.Id}: " +
                $"{report.ChangeCount:N0} changed file groups";
            SummaryText.Text =
                $"Observed volume used-space delta: {Signed(report.VolumeUsedDeltaBytes)}\n" +
                $"Known file allocation delta:     {Signed(report.KnownAllocatedDeltaBytes)}\n" +
                $"Provisional unexplained delta:    {Signed(report.UnexplainedDeltaBytes)}\n" +
                $"Changes with unknown allocation:  {report.UnknownAllocationChanges:N0}\n" +
                $"Folder totals (known net):       {Signed(folders.RootKnownNetDeltaBytes)}\n" +
                $"Directories with changes:       {folders.FolderCount:N0}\n\n" +
                "FOLDER ACCOUNTING\n" +
                "Folder rows include descendants, and parent/child rows overlap. " +
                "Hard-linked files are assigned to one canonical path per snapshot. " +
                "Moves between directories may shift folder totals without using more space. " +
                "Unknown allocations are excluded from known net deltas.\n\n" +
                "CAVEATS\n" + string.Join("\n", report.Caveats.Select(x => "• " + x)) +
                "\n\nResults are paged from SQLite. A nonzero residual does not identify " +
                "a particular Windows component, and measurements are not atomic.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled. Partial folder summaries were rolled back; " +
                "any already committed file comparison remains cached.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Comparison failed: " + ex.Message;
        }
        finally
        {
            showComparisonProgress = false;
            _compareCancellation.Dispose();
            _compareCancellation = null;
            CancelCompareButton.IsEnabled = false;
            BaselineChoice.IsEnabled = true;
            CurrentChoice.IsEnabled = true;
            ScanButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
            UpdateCompareButtonState();
            UpdatePageButtons();
            UpdateFolderPageButtons();
        }
    }

    private void CancelCompareButton_Click(object sender, RoutedEventArgs e) =>
        _compareCancellation?.Cancel();

    private void ClearComparisonView()
    {
        _activeComparison = null;
        _activeFolderSummary = null;
        FolderTotalsText.Text = "No folder summary loaded";
        _pageOffset = 0;
        _folderOffset = 0;
        _folderGrowthFirst = true;
        ChangesGrid.ItemsSource = null;
        FolderGrid.ItemsSource = null;
        FolderPageIndicatorText.Text = "No folder summary loaded";
        FolderSortButton.Content = "Sort: growth";
        FolderSortButton.IsEnabled = false;
        PreviousFolderPageButton.IsEnabled = false;
        NextFolderPageButton.IsEnabled = false;
        PageIndicatorText.Text = "No comparison loaded";
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
    }

    private void UpdatePageButtons()
    {
        StoredComparison? report = _activeComparison;
        PreviousPageButton.IsEnabled =
            report is not null && _pageOffset > 0 && !_loadingPage &&
            _compareCancellation is null;
        NextPageButton.IsEnabled =
            report is not null && (long)_pageOffset + PageSize < report.ChangeCount &&
            !_loadingPage && _compareCancellation is null;
    }

    private async Task LoadChangePageAsync(int offset)
    {
        var store = _repository;
        var report = _activeComparison;
        if (store is null || report is null || _loadingPage)
            return;
        _loadingPage = true;
        UpdatePageButtons();
        try
        {
            ComparisonPage page = await Task.Run(() =>
                store.ReadComparisonPage(report.Id, offset, PageSize));
            if (_activeComparison?.Id != page.ComparisonId)
                return;
            _pageOffset = offset;
            ChangesGrid.ItemsSource = page.Changes.Select(c => new ChangeRow(
                c.Kind.ToString(), Signed(c.AllocatedDeltaBytes),
                c.BeforePaths.Count == 0 ? "—" : Bytes(c.BeforeAllocatedBytes),
                c.AfterPaths.Count == 0 ? "—" : Bytes(c.AfterAllocatedBytes),
                c.PrimaryPath)).ToList();

            long pages = (report.ChangeCount + PageSize - 1) / PageSize;
            long displayedPage = report.ChangeCount == 0 ? 0 : offset / PageSize + 1;
            PageIndicatorText.Text = $"Page {displayedPage:N0} of {pages:N0} · " +
                $"{report.ChangeCount:N0} changes";
        }
        finally
        {
            _loadingPage = false;
            UpdateCompareButtonState();
            UpdatePageButtons();
        }
    }

    private async void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadChangePageAsync(Math.Max(0, _pageOffset - PageSize)); }
        catch (Exception ex) { StatusText.Text = "Page load failed: " + ex.Message; }
    }

    private async void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadChangePageAsync(checked(_pageOffset + PageSize)); }
        catch (Exception ex) { StatusText.Text = "Page load failed: " + ex.Message; }
    }

    private void UpdateFolderPageButtons()
    {
        StoredFolderSummary? summary = _activeFolderSummary;
        bool ready = summary is not null &&
            !_loadingFolderPage && _compareCancellation is null;
        FolderSortButton.IsEnabled = ready;
        PreviousFolderPageButton.IsEnabled = ready && _folderOffset > 0;
        NextFolderPageButton.IsEnabled = ready &&
            (long)_folderOffset + PageSize < summary!.FolderCount;
    }

    private async Task LoadFolderPageAsync(int offset)
    {
        var store = _repository;
        var summary = _activeFolderSummary;
        if (store is null || summary is null || _loadingFolderPage)
            return;
        _loadingFolderPage = true;
        UpdateFolderPageButtons();
        try
        {
            FolderPage page = await Task.Run(() =>
                store.ReadFolderPage(summary.ComparisonId, offset,
                    PageSize, _folderGrowthFirst));
            if (_activeFolderSummary?.ComparisonId != page.ComparisonId)
                return;
            _folderOffset = offset;
            FolderGrid.ItemsSource = page.Folders.Select(x => new FolderRow(
                Signed(x.KnownNetDeltaBytes),
                Bytes(x.KnownGrowthBytes),
                Bytes(x.KnownReductionBytes),
                x.ChangedGroupCount.ToString("N0", CultureInfo.InvariantCulture),
                x.UnknownAllocationGroups.ToString("N0", CultureInfo.InvariantCulture),
                x.FolderPath)).ToList();

            long pages = (summary.FolderCount + PageSize - 1) / PageSize;
            long displayed = summary.FolderCount == 0 ? 0 : offset / PageSize + 1;
            FolderPageIndicatorText.Text =
                $"Page {displayed:N0} of {pages:N0} · " +
                $"{summary.FolderCount:N0} folders";
        }
        finally
        {
            _loadingFolderPage = false;
            UpdateFolderPageButtons();
        }
    }

    private async void PreviousFolderPageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadFolderPageAsync(Math.Max(0, _folderOffset - PageSize)); }
        catch (Exception ex) { StatusText.Text = "Folder page failed: " + ex.Message; }
    }

    private async void NextFolderPageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadFolderPageAsync(checked(_folderOffset + PageSize)); }
        catch (Exception ex) { StatusText.Text = "Folder page failed: " + ex.Message; }
    }

    private async void FolderSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingFolderPage || _activeFolderSummary is null)
            return;
        _folderGrowthFirst = !_folderGrowthFirst;
        FolderSortButton.Content =
            _folderGrowthFirst ? "Sort: growth" : "Sort: most freed";
        try { await LoadFolderPageAsync(0); }
        catch (Exception ex) { StatusText.Text = "Folder sort failed: " + ex.Message; }
    }

    private static string Signed(long? value) => value is null
        ? "unknown" : (value > 0 ? "+" : "") + Bytes(value);

    private static string Bytes(long? bytes)
    {
        if (bytes is null) return "unknown";
        double amount = Math.Abs((double)bytes.Value);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return (bytes < 0 ? "-" : "") +
            amount.ToString(unit == 0 ? "N0" : "N2", CultureInfo.InvariantCulture) +
            " " + units[unit];
    }
}
