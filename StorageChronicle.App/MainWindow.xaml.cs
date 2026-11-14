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
    private SnapshotCoverage? _coverage;
    private int _coverageRequestVersion;
    private int _coveragePageRequestVersion;
    private int _coveragePageOffset;
    private bool _coveragePageBusy;
    private bool _folderGrowthFirst = true;
    private bool _loadingFolderPage;
    private int _folderOffset;
    private bool _updatingChoices;
    private bool _loadingPage;
    private int _pageOffset;
    private const int PageSize = 100;
    private bool _shadowReadingInProgress;
    private bool _shadowHistoryUpdating;

    private sealed record SnapshotChoice(long Id, string RootPath, DateTimeOffset When, int Files)
    {
        public string Display =>
            $"#{Id} · {When.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {RootPath} · {Files:N0} files";
    }

    private sealed record ChangeRow(string Kind, string Delta,
        string Before, string After, string Path);

    private sealed record FolderRow(string Net, string Growth, string Reduction,
        string ChangedGroups, string UnknownGroups, string FolderPath);

    private sealed record ShadowChoice(long Id, DateTimeOffset When, ShadowProbeStatus Status)
    {
        public string Display =>
            $"#{Id} · {When.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Status}";
    }

    private sealed record ShadowAreaRow(
        string Source, string Destination, string Used, string Allocated, string Maximum);

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
            RefreshShadowHistory();
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
            CoverageSnapshotChoice.ItemsSource = choices;
            CoverageSnapshotChoice.SelectedIndex = choices.Count > 0 ? 0 : -1;
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
            token.ThrowIfCancellationRequested();
            ReconciliationEvidence evidence = await Task.Run(() =>
                store.LoadReconciliation(report.Id));
            token.ThrowIfCancellationRequested();
            ReconciliationStatusText.Text =
                $"Reconciliation: {evidence.Status} · Snapshot #{baseline.Id} → #{current.Id}";
            ReconciliationDetailsText.Text = FormatReconciliation(evidence);
            StatusText.Text = $"Comparison #{baseline.Id} → #{current.Id}: " +
                $"{report.ChangeCount:N0} changed file groups";
            SummaryText.Text =
                $"Observed free-space loss:         {Signed(evidence.FreeSpaceLossBytes)}\n" +
                $"Volume used-space delta:          {Signed(evidence.VolumeUsedChangeBytes)}\n" +
                $"Known file allocation delta:      {Signed(report.KnownAllocatedDeltaBytes)}\n" +
                $"Unassigned residual:              {Signed(evidence.UnexplainedResidualBytes)}\n" +
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
        ReconciliationStatusText.Text =
            "Compare two saved snapshots to review reconciliation evidence.";
        ReconciliationDetailsText.Text = "";
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

    private async void CoverageSnapshotChoice_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_repository is null) return;
        try { await LoadCoverageAsync(); }
        catch (Exception ex) { CoverageSummaryText.Text = "Coverage query failed: " + ex.Message; }
    }

    private async void CoverageFilterChoice_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_repository is null || _coverage is null) return;
        try { await LoadCoveragePageAsync(0); }
        catch (Exception ex) { CoverageSummaryText.Text = "Notice query failed: " + ex.Message; }
    }

    private IssueSeverity? SelectedCoverageFilter() =>
        CoverageFilterChoice.SelectedIndex switch
        {
            1 => IssueSeverity.Error,
            2 => IssueSeverity.Warning,
            3 => IssueSeverity.Info,
            _ => null
        };

    private long FilteredIssueCount(SnapshotCoverage coverage) =>
        SelectedCoverageFilter() switch
        {
            IssueSeverity.Error => coverage.ErrorCount,
            IssueSeverity.Warning => coverage.WarningCount,
            IssueSeverity.Info => coverage.InfoCount,
            _ => coverage.NoticeCount
        };

    private async Task LoadCoverageAsync()
    {
        var store = _repository;
        int version = ++_coverageRequestVersion;
        ++_coveragePageRequestVersion; // invalidate in-flight issue-page queries
        _coverage = null;
        _coveragePageOffset = 0;
        _coveragePageBusy = false;
        CoverageIssuesGrid.ItemsSource = null;
        CoverageCategoriesText.Text = "";
        CoveragePageIndicatorText.Text = "No scan notices loaded";
        UpdateCoveragePageButtons();

        if (store is null || CoverageSnapshotChoice.SelectedItem is not SnapshotChoice choice)
        {
            CoverageSummaryText.Text = "Select a saved snapshot to inspect its recorded coverage.";
            return;
        }

        CoverageSummaryText.Text = $"Loading coverage for snapshot #{choice.Id} …";
        SnapshotCoverage coverage = await Task.Run(() => store.LoadScanCoverage(choice.Id));
        if (version != _coverageRequestVersion ||
            (CoverageSnapshotChoice.SelectedItem as SnapshotChoice)?.Id != coverage.SnapshotId)
            return;

        _coverage = coverage;
        string scope = coverage.IsWholeVolume ? "Whole-volume scan root" : "Directory-scoped scan";
        CoverageSummaryText.Text =
            $"Snapshot #{coverage.SnapshotId} · {scope} · " +
            $"{coverage.FileCount:N0} observed files · " +
            $"{coverage.ErrorCount:N0} read errors · {coverage.WarningCount:N0} warnings · " +
            $"{coverage.InfoCount:N0} exclusions/info · " +
            $"{coverage.MissingAllocationCount:N0} files with unknown allocation · " +
            $"{coverage.MissingIdentityCount:N0} files with unknown identity.\n" +
            coverage.CoverageInterpretation;

        var displayedCategories = coverage.Categories.Take(9)
            .Select(c => $"{c.Severity}/{c.Code}: {c.Count:N0}");
        CoverageCategoriesText.Text = coverage.Categories.Count == 0
            ? "No scan notices recorded."
            : "Recorded issue categories: " +
              string.Join("  |  ", displayedCategories) +
              (coverage.Categories.Count > 9
                  ? $"  |  … {coverage.Categories.Count - 9:N0} more categories" : "");

        await LoadCoveragePageAsync(0);
    }

    private async Task LoadCoveragePageAsync(int offset)
    {
        var store = _repository;
        var coverage = _coverage;
        if (store is null || coverage is null) return;
        int version = ++_coveragePageRequestVersion;
        IssueSeverity? filter = SelectedCoverageFilter();
        _coveragePageBusy = true;
        UpdateCoveragePageButtons();
        try
        {
            ScanIssuePage page = await Task.Run(() =>
                store.ReadScanIssuePage(coverage.SnapshotId, offset, PageSize, filter));
            if (version != _coveragePageRequestVersion ||
                _coverage?.SnapshotId != page.SnapshotId)
                return;

            _coveragePageOffset = offset;
            CoverageIssuesGrid.ItemsSource = page.Issues;
            long count = FilteredIssueCount(coverage);
            long totalPages = (count + PageSize - 1) / PageSize;
            long currentPage = count == 0 ? 0 : offset / PageSize + 1;
            CoveragePageIndicatorText.Text =
                $"Page {currentPage:N0} of {totalPages:N0} · {count:N0} notices";
        }
        finally
        {
            if (version == _coveragePageRequestVersion)
            {
                _coveragePageBusy = false;
                UpdateCoveragePageButtons();
            }
        }
    }

    private void UpdateCoveragePageButtons()
    {
        var coverage = _coverage;
        bool ready = coverage is not null && !_coveragePageBusy;
        long total = coverage is null ? 0 : FilteredIssueCount(coverage);
        PreviousCoveragePageButton.IsEnabled = ready && _coveragePageOffset > 0;
        NextCoveragePageButton.IsEnabled = ready &&
            (long)_coveragePageOffset + PageSize < total;
    }

    private async void PreviousCoveragePageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadCoveragePageAsync(Math.Max(0, _coveragePageOffset - PageSize)); }
        catch (Exception ex) { CoverageSummaryText.Text = "Page query failed: " + ex.Message; }
    }

    private async void NextCoveragePageButton_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadCoveragePageAsync(checked(_coveragePageOffset + PageSize)); }
        catch (Exception ex) { CoverageSummaryText.Text = "Page query failed: " + ex.Message; }
    }

    private void RefreshShadowHistory()
    {
        if (_repository is null) return;
        var choices = _repository.ListShadowStorageMeasurements(100)
            .Select(x => new ShadowChoice(x.Id, x.ObservedUtc, x.Status))
            .ToArray();
        _shadowHistoryUpdating = true;
        try
        {
            ShadowHistoryChoice.ItemsSource = choices;
            ShadowHistoryChoice.SelectedIndex = choices.Length > 0 ? 0 : -1;
        }
        finally
        {
            _shadowHistoryUpdating = false;
        }
        ShowSelectedShadowMeasurement();
    }

    private void ShadowHistoryChoice_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_shadowHistoryUpdating)
            ShowSelectedShadowMeasurement();
    }

    private void ShowSelectedShadowMeasurement()
    {
        var store = _repository;
        if (store is null || ShadowHistoryChoice.SelectedItem is not ShadowChoice selected)
        {
            ShadowSummaryText.Text = "No shadow-storage measurement selected.";
            ShadowAreasGrid.ItemsSource = null;
            return;
        }

        try
        {
            ShadowStorageMeasurement latest =
                store.LoadShadowStorageMeasurement(selected.Id);
            var previous = (ShadowHistoryChoice.ItemsSource as IEnumerable<ShadowChoice>)?
                .Where(x => x.Id < latest.Id).OrderByDescending(x => x.Id).FirstOrDefault();
            ShadowStorageMeasurement? earlier = previous is null ? null :
                store.LoadShadowStorageMeasurement(previous.Id);

            string volume = System.IO.Path.GetPathRoot(RootPathInput.Text.Trim()) ?? @"C:\";
            var delta = earlier is null ? null :
                ShadowStorageEvaluator.Compare(earlier, latest, volume);

            string beforeText = delta is null ? "no previous reading" :
                Bytes(delta.EarlierAllocatedBytes);
            string allocatedText = Bytes(latest.AllocatedOnDrive(volume));
            string usedText = Bytes(latest.UsedOnDrive(volume));
            string deltaText = delta is null ? "not available" :
                Signed(delta.AllocatedDeltaBytes);

            ShadowSummaryText.Text =
                $"Reading #{latest.Id} at {latest.ObservedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                $"({latest.Status}) · {latest.Areas.Count:N0} associations\n" +
                $"Allocated on {volume}: {allocatedText} · Used: {usedText} · " +
                $"Previous allocated: {beforeText} · Change: {deltaText}\n" +
                $"{latest.Diagnostic ?? "No WMI query errors recorded."}\n" +
                "Only comparable timestamped readings show a change. These values are not " +
                "subtracted from a historical unexplained residual.";

            ShadowAreasGrid.ItemsSource = latest.Areas.Select(x =>
                new ShadowAreaRow(
                    x.SourceDrive ?? x.SourceVolumeReference,
                    x.DiffDrive ?? x.DiffVolumeReference,
                    Bytes(x.UsedBytes), Bytes(x.AllocatedBytes),
                    Bytes(x.MaximumBytes))).ToArray();
        }
        catch (Exception ex)
        {
            ShadowSummaryText.Text = "Cannot load shadow-storage reading: " + ex.Message;
            ShadowAreasGrid.ItemsSource = null;
        }
    }

    private async void MeasureShadowButton_Click(object sender, RoutedEventArgs e)
    {
        var store = _repository;
        if (_shadowReadingInProgress || store is null) return;
        _shadowReadingInProgress = true;
        MeasureShadowButton.IsEnabled = false;
        ShadowSummaryText.Text = "Querying Windows WMI shadow-storage records …";
        try
        {
            ShadowStorageMeasurement measured =
                await Task.Run(store.MeasureShadowStorageNow);
            RefreshShadowHistory();
            StatusText.Text = $"Shadow-storage reading #{measured.Id} saved: " +
                $"{measured.Status}, {measured.Areas.Count:N0} associations.";
        }
        catch (Exception ex)
        {
            ShadowSummaryText.Text = "Shadow-storage measurement failed: " + ex.Message;
        }
        finally
        {
            _shadowReadingInProgress = false;
            MeasureShadowButton.IsEnabled = true;
        }
    }

    private static string FormatReconciliation(ReconciliationEvidence r)
    {
        var lines = new List<string>
        {
            "STORAGE RECONCILIATION — OBSERVATIONS, NOT CAUSAL ATTRIBUTION",
            "",
            $"Baseline: #{r.Baseline.SnapshotId} · {r.Baseline.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"Current:  #{r.Current.SnapshotId} · {r.Current.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"Scope:    {r.Current.RootPath}",
            "",
            $"Baseline free (end):              {Bytes(r.Baseline.FreeBytesAfter)}",
            $"Current free (end):               {Bytes(r.Current.FreeBytesAfter)}",
            $"Observed free-space loss:         {Signed(r.FreeSpaceLossBytes)}",
            $"Capacity change (current - old): {Signed(r.CapacityChangeBytes)}",
            $"Volume used-space change:        {Signed(r.VolumeUsedChangeBytes)}",
            $"Known file allocation delta:     {Signed(r.KnownFileAllocationDeltaBytes)}",
            $"UNEXPLAINED RESIDUAL:            {Signed(r.UnexplainedResidualBytes)}",
            $"Unknown changed file groups:     {r.UnknownChangedFileGroups:N0}",
            "",
            "FREE-SPACE CHANGE DURING SCANS (positive means loss)",
            $"During baseline scan:            {Signed(r.BaselineScanFreeSpaceLossBytes)}",
            $"Between scan intervals:          {Signed(r.BetweenScansFreeSpaceLossBytes)}",
            $"During current scan:             {Signed(r.CurrentScanFreeSpaceLossBytes)}",
            "",
            "RECORDED OBSERVATION LIMITATIONS",
            $"Baseline: {r.Baseline.FileCount:N0} files, {r.Baseline.ErrorCount:N0} read errors, " +
                $"{r.Baseline.MissingAllocationCount:N0} unknown allocation sizes",
            $"Current:  {r.Current.FileCount:N0} files, {r.Current.ErrorCount:N0} read errors, " +
                $"{r.Current.MissingAllocationCount:N0} unknown allocation sizes",
            ""
        };
        lines.AddRange(r.Limitations.Select(x => "• " + x));
        lines.Add("");
        lines.Add("This is not an itemized explanation of Windows-managed storage.");
        lines.Add("Do not add parent and child folder totals or delete system files based on the residual.");
        return string.Join(Environment.NewLine, lines);
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
