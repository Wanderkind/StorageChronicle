using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace StorageChronicle.Core;

/// <summary>
/// A read-only vssadmin diagnostic. Output is preserved as localized text and
/// NEVER parsed into quantitative accounting or associated with a past snapshot.
/// </summary>
public sealed record VssAdminDiagnostic(
    DateTimeOffset ObservedUtc,
    string Command,
    int? ExitCode,
    bool TimedOut,
    string Output,
    string Error)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public static class VssAdminInspector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Only a simple drive letter is accepted; no arbitrary shell commands,
    /// provider methods, filesystem paths or arguments can be injected.
    /// </summary>
    public static string SafeCommandForDrive(string drive)
    {
        if (string.IsNullOrWhiteSpace(drive) ||
            !(drive.Length == 2 || drive.Length == 3) ||
            !char.IsAsciiLetter(drive[0]) || drive[1] != ':' ||
            (drive.Length == 3 && drive[2] != '\\'))
            throw new ArgumentException("Specify a drive letter such as C: or C:\\.", nameof(drive));

        return $"vssadmin list shadowstorage /on={char.ToUpperInvariant(drive[0])}:";
    }

    /// <summary>
    /// Executes only 'vssadmin list shadowstorage /on=X:' without a shell.
    /// Does not request elevation, modify VSS settings, or invoke delete/resize.
    /// </summary>
    public static async Task<VssAdminDiagnostic> CheckAsync(
        string drive, CancellationToken cancellationToken = default)
    {
        string command = SafeCommandForDrive(drive);
        var time = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return new(time, command, null, false, "",
                "vssadmin is only available on Windows.");

        string exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "vssadmin.exe");
        var info = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("list");
        info.ArgumentList.Add("shadowstorage");
        info.ArgumentList.Add("/on=" + char.ToUpperInvariant(drive[0]) + ":");

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                return new(time, command, null, false, "",
                    "Could not launch the read-only vssadmin command.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or
                                   InvalidOperationException or UnauthorizedAccessException)
        {
            return new(time, command, null, false, "",
                $"Cannot launch vssadmin ({ex.GetType().Name}): {ex.Message}");
        }

        // Read both pipes concurrently to avoid deadlocks for verbose output.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            return new(time, command, process.ExitCode, false,
                LimitText(output), LimitText(error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(time, command, null, true, "",
                "The read-only vssadmin query timed out after 20 seconds.");
        }
        finally
        {
            // Closing a diagnostic view does not leave a command running.
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private static string LimitText(string value) =>
        value.Length <= 32_000 ? value : value[..32_000] + "\n[Output truncated]";
}
