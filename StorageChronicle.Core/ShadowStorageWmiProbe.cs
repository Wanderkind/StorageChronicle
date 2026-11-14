using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace StorageChronicle.Core;

/// <summary>
/// On-demand, read-only Win32_ShadowStorage query. Never calls WMI Create,
/// Resize, Delete or any VSS mutating operation. No elevation is attempted.
/// </summary>
public sealed class ShadowStorageWmiProbe
{
    public ShadowStorageMeasurement Measure()
    {
        var observed = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows())
            return new(0, observed, ShadowProbeStatus.Unavailable,
                "Shadow-storage WMI measurement requires Windows.", []);

        var areas = new List<ShadowStorageArea>();
        var diagnostics = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Volume,DiffVolume,UsedSpace,AllocatedSpace,MaxSpace FROM Win32_ShadowStorage");
            searcher.Options.Timeout = TimeSpan.FromSeconds(12);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    string source = Convert.ToString(item["Volume"], CultureInfo.InvariantCulture) ?? "";
                    string destination = Convert.ToString(item["DiffVolume"], CultureInfo.InvariantCulture) ?? "";
                    string? sourceDrive = ResolveDrive(source, diagnostics);
                    string? destinationDrive = ResolveDrive(destination, diagnostics);
                    long? used = ReadNonnegative(item["UsedSpace"], "UsedSpace", diagnostics);
                    long? allocated = ReadNonnegative(item["AllocatedSpace"], "AllocatedSpace", diagnostics);
                    long? maximum = ReadNonnegative(item["MaxSpace"], "MaxSpace", diagnostics);
                    areas.Add(new(source, destination, sourceDrive, destinationDrive,
                        used, allocated, maximum));
                }
            }
            return new(0, observed,
                diagnostics.Count == 0 ? ShadowProbeStatus.Complete : ShadowProbeStatus.Partial,
                diagnostics.Count == 0 ? null : string.Join(" | ", diagnostics.Take(12)),
                areas);
        }
        catch (Exception ex) when (ex is ManagementException or
            UnauthorizedAccessException or COMException or
            System.Security.SecurityException or InvalidOperationException)
        {
            return new(0, observed, ShadowProbeStatus.Unavailable,
                $"WMI shadow-storage query failed ({ex.GetType().Name}): {ex.Message}",
                []);
        }
    }

    private static string? ResolveDrive(string reference, List<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            diagnostics.Add("One WMI association did not specify its volume reference.");
            return null;
        }

        try
        {
            using var volume = new ManagementObject(reference);
            volume.Get();
            string? drive = Convert.ToString(volume["DriveLetter"], CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(drive) ? null : drive.Trim();
        }
        catch (Exception ex) when (ex is ManagementException or
            UnauthorizedAccessException or COMException or
            System.Security.SecurityException or InvalidOperationException)
        {
            diagnostics.Add($"Cannot resolve volume reference ({ex.GetType().Name}).");
            return null;
        }
    }

    private static long? ReadNonnegative(
        object? value, string property, List<string> diagnostics)
    {
        if (value is null || value is DBNull)
        {
            diagnostics.Add($"{property} was not reported.");
            return null;
        }
        try
        {
            ulong raw = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            if (raw > long.MaxValue)
            {
                // MaxSpace may use an unbounded sentinel; retain unknown instead.
                if (property != "MaxSpace")
                    diagnostics.Add($"{property} exceeded signed 64-bit range.");
                return null;
            }
            return checked((long)raw);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            diagnostics.Add($"{property} could not be interpreted as bytes.");
            return null;
        }
    }
}
