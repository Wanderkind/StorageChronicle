using StorageChronicle.Core;

namespace StorageChronicle.Tests;

/// <summary>These tests deliberately never launch vssadmin or require elevation.</summary>
public sealed class VssAdminInspectorTests
{
    [Fact]
    public void Fixed_read_only_command_normalizes_drive_notation()
    {
        Assert.Equal("vssadmin list shadowstorage /on=C:",
            VssAdminInspector.SafeCommandForDrive("C:"));
        Assert.Equal("vssadmin list shadowstorage /on=C:",
            VssAdminInspector.SafeCommandForDrive(@"c:\"));
        Assert.Equal("vssadmin list shadowstorage /on=D:",
            VssAdminInspector.SafeCommandForDrive("d:"));
    }

    [Fact]
    public void Paths_extra_arguments_and_shell_tokens_are_rejected()
    {
        string[] invalid =
        [
            "", " ", "C", @"C:\Windows", "C: & delete", "C: /on=D:",
            "&&", "C:/", "C::", "/on=C:", @"\\server\share"
        ];
        foreach (string request in invalid)
            Assert.Throws<ArgumentException>(() =>
                VssAdminInspector.SafeCommandForDrive(request));
    }

    [Fact]
    public async Task Invalid_drive_is_rejected_before_executing_an_external_command()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VssAdminInspector.CheckAsync("C: & echo INJECTED"));
    }
}
