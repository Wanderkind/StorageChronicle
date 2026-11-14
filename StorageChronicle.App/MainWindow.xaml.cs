using System.Globalization;
using System.Windows;
using StorageChronicle.Core;

namespace StorageChronicle.App;

public partial class MainWindow : Window
{
    private SqliteSnapshotRepository? _repository;
    private CancellationTokenSource? _scanCancellation;
    private bool _updatingChoices;

    private sealed record SnapshotChoice(long Id, string RootPath, DateTimeOffset When, int Files)
    {
        public string Display =>
            $"#{Id} · {When.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {RootPath} · {Files:N0} files";
    }

    private sealed record ChangeRow(string Kind, string Delta,
        string Before, string After, string Path);

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
            UpdateBaselineChoices();
    }

    private void BaselineChoice_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingChoices)
            UpdateCompareButtonState();
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
            DiskSnapshot snapshot = await Task.Run(
                () => new SnapshotScanner().Capture(root, token, progress));
            scanRunning = false;
            StatusText.Text = "Scan finished; saving metadata to SQLite …";
            long id = await Task.Run(() => store.Save(snapshot));
            RefreshChoices();
            StatusText.Text = $"Saved snapshot #{id}: {snapshot.Files.Count:N0} files, " +
                $"{snapshot.FailureCount:N0} read failures, " +
                $"{snapshot.Issues.Count:N0} total notices. " +
                $"Elapsed: {(snapshot.FinishedUtc - snapshot.StartedUtc).TotalSeconds:F1}s.";
            SummaryText.Text = string.Join(Environment.NewLine,
                snapshot.Issues.Take(100).Select(i =>
                    $"[{i.Severity}] {i.Code}: {i.Path} — {i.Detail}"));
            if (snapshot.Issues.Count > 100)
                SummaryText.AppendText($"\n… and {snapshot.Issues.Count - 100} more notices.");
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
        CompareButton.IsEnabled = false;
        StatusText.Text = "Comparing saved snapshots …";
        try
        {
            ComparisonReport report = await Task.Run(() =>
                SnapshotComparer.Compare(store.Load(baseline.Id),
                                         store.Load(current.Id)));
            ChangesGrid.ItemsSource = report.Changes.Select(c => new ChangeRow(
                c.Kind.ToString(), Signed(c.AllocatedDeltaBytes),
                c.BeforePaths.Count == 0 ? "—" : Bytes(c.BeforeAllocatedBytes),
                c.AfterPaths.Count == 0 ? "—" : Bytes(c.AfterAllocatedBytes),
                c.PrimaryPath)).ToList();

            StatusText.Text = $"Comparison #{baseline.Id} → #{current.Id}: " +
                $"{report.Changes.Count:N0} changed file groups";
            SummaryText.Text =
                $"Observed volume used-space delta: {Signed(report.VolumeUsedDeltaBytes)}\n" +
                $"Known file allocation delta:     {Signed(report.KnownAllocatedDeltaBytes)}\n" +
                $"Provisional unexplained delta:    {Signed(report.UnexplainedDeltaBytes)}\n" +
                $"Changes with unknown allocation:  {report.UnknownAllocationChanges:N0}\n\n" +
                "CAVEATS\n" + string.Join("\n", report.Caveats.Select(x => "• " + x)) +
                "\n\nA nonzero residual is not proof that Windows itself consumed that space. " +
                "Measurements are not atomic and some system usage is outside this scanner.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Comparison failed: " + ex.Message;
        }
        finally
        {
            UpdateCompareButtonState();
        }
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
