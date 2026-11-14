using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class VssAdminGuidanceTests
{
    private static VssAdminDiagnostic Diagnostic(int? exitCode) =>
        new(DateTimeOffset.UnixEpoch,
            "vssadmin list shadowstorage /on=C:",
            exitCode, false, "", "");

    [Fact]
    public void Copyable_administrator_commands_are_fixed_read_only_and_show_both_views()
    {
        string commands = VssAdminGuidance.AdministratorCommands(@"c:\");
        string[] lines = commands.Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.Equal("vssadmin list shadowstorage /on=C:", lines[0]);
        Assert.Equal("vssadmin list shadowstorage /for=C:", lines[1]);
        Assert.DoesNotContain("delete", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resize", commands, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Untrusted_drive_arguments_are_rejected_before_copying()
    {
        Assert.Throws<ArgumentException>(() =>
            VssAdminGuidance.AdministratorCommands("C: & del"));
    }

    [Fact]
    public void Unelevated_failure_explains_likely_permissions_without_claiming_proof()
    {
        string message = VssAdminGuidance.Explain(Diagnostic(2), false);
        Assert.Contains("without an elevated administrator token", message);
        Assert.Contains("does not prove the cause", message);
        Assert.Contains("does not elevate", message);
    }

    [Fact]
    public void Elevated_failure_does_not_mislabel_it_as_permissions()
    {
        string message = VssAdminGuidance.Explain(Diagnostic(2), true);
        Assert.Contains("already has an elevated administrator token", message);
        Assert.DoesNotContain("likely explanation", message);
    }

    [Fact]
    public void Successful_query_keeps_historical_growth_unattributed()
    {
        string message = VssAdminGuidance.Explain(Diagnostic(0), false);
        Assert.Contains("current, timestamped", message);
        Assert.Contains("not evidence of historical growth", message);
    }
}
