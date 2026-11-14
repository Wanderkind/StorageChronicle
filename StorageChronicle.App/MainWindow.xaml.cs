using System.Globalization;
using System.Windows;
using StorageChronicle.Core;

namespace StorageChronicle.App;

public partial class MainWindow : Window
{
    private SqliteSnapshotRepository? _repository;
    private CancellationTokenSource? _scanCancellation;

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
        BaselineChoice.ItemsSource = choices;
        CurrentChoice.ItemsSource = choices;
        CurrentChoice.SelectedIndex = choices.Count > 0 ? 0 : -1;
        BaselineChoice.SelectedIndex = choices.Count > 1 ? 1 : -1;
        CompareButton.IsEnabled = choices.Count > 1;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null) return;
        string root = RootPathInput.Text.Trim();
        if (!System.IO.Directory.Exists(root))
        {
            MessageBox.Show("Select an existing directory or volume root.",
                "Invalid path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Scanning " + root + " (no files will be modified) …";
        try
        {
            var token = _scanCancellation.Token;
            DiskSnapshot snapshot = await Task.Run(
                () => new SnapshotScanner().Capture(root, token));
            StatusText.Text = "Scan finished; saving metadata to SQLite …";
            long id = await Task.Run(() => _repository.Save(snapshot));
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
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            CompareButton.IsEnabled = BaselineChoice.Items.Count > 1;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _scanCancellation?.Cancel();

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null ||
            BaselineChoice.SelectedItem is not SnapshotChoice baseline ||
            CurrentChoice.SelectedItem is not SnapshotChoice current)
        {
            MessageBox.Show("Select a baseline and a current snapshot.");
            return;
        }
        if (baseline.Id == current.Id)
        {
            MessageBox.Show("Select two different snapshots.");
            return;
        }
        CompareButton.IsEnabled = false;
        StatusText.Text = "Comparing saved snapshots …";
        try
        {
            ComparisonReport report = await Task.Run(() =>
                SnapshotComparer.Compare(_repository.Load(baseline.Id),
                                         _repository.Load(current.Id)));
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
            CompareButton.IsEnabled = true;
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
