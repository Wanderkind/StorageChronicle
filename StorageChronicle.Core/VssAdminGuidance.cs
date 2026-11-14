namespace StorageChronicle.Core;

/// <summary>
/// Pure, conservative user guidance for the fixed, read-only VSS diagnostics.
/// A nonzero exit code is not enough to prove why a command failed.
/// </summary>
public static class VssAdminGuidance
{
    public static string AdministratorCommands(string drive)
    {
        string on = VssAdminInspector.SafeCommandForDrive(drive);
        return on + Environment.NewLine +
            on.Replace(" /on=", " /for=", StringComparison.Ordinal);
    }

    public static string Explain(VssAdminDiagnostic result, bool? currentProcessElevated)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
            return "The command completed. This is a current, timestamped shadow-storage listing, " +
                "not evidence of historical growth. No numeric values have been imported.";

        string privilege = currentProcessElevated switch
        {
            false => "StorageChronicle is running without an elevated administrator token. " +
                "Administrative permission is a likely explanation for this failure, " +
                "but the exit code alone does not prove the cause.",
            true => "StorageChronicle already has an elevated administrator token. " +
                "Check the command output for a provider, VSS, or volume-specific error.",
            _ => "The application's elevation status could not be determined. " +
                "Check the command output and retry the same read-only commands " +
                "in an administrator PowerShell window if appropriate."
        };
        return privilege + Environment.NewLine +
            "Use 'Copy admin commands' to copy the /on and /for checks. " +
            "No UAC prompt is initiated, and the application itself does not elevate.";
    }
}
