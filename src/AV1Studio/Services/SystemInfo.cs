using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AV1Studio.Native;

namespace AV1Studio.Services;

/// <summary>A display adapter found on this PC. AV1 capabilities are NOT inferred from the name — they come
/// from real FFmpeg tests (see HardwareCapabilities).</summary>
public sealed record GpuInfo(string Name, string Vendor, bool IsIntegrated);

public sealed class SystemInfo
{
    public string Cpu { get; init; } = "Unknown CPU";
    public string CpuVendor { get; init; } = "Unknown";
    public string Architecture { get; init; } = RuntimeInformation.OSArchitecture.ToString();
    public int LogicalCores { get; init; }
    public ulong RamBytes { get; init; }
    public List<GpuInfo> Gpus { get; init; } = new();
    public string WindowsVersion { get; init; } = RuntimeInformation.OSDescription;

    public string Summary =>
        $"{Cpu} ({LogicalCores} threads, {Architecture}) · {Util.Fmt.Bytes((long)RamBytes)} RAM" +
        (Gpus.Count > 0 ? " · " + string.Join(", ", Gpus.Select(g => g.Name)) : "");

    public static SystemInfo Detect()
    {
        string cpu = "Unknown CPU", vendor = "Unknown";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu;
            vendor = (k?.GetValue("VendorIdentifier") as string)?.Trim() switch
            {
                "GenuineIntel" => "Intel",
                "AuthenticAMD" => "AMD",
                string v when v.Length > 0 => v,
                _ => cpu.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) ? "Qualcomm" : "Unknown",
            };
        }
        catch { }

        ulong ram = 0;
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (Win32.GlobalMemoryStatusEx(ref mem)) ram = mem.ullTotalPhys;

        var gpus = new List<GpuInfo>();
        try
        {
            // Display adapter device class
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string name && seen.Add(name) && !IsVirtual(name))
                        gpus.Add(Classify(name));
                }
            }
        }
        catch { }

        string windows = RuntimeInformation.OSDescription;
        try
        {
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = nt?.GetValue("ProductName") as string;
            var display = nt?.GetValue("DisplayVersion") as string;
            var build = nt?.GetValue("CurrentBuild") as string;
            if (int.TryParse(build, out var b) && b >= 22000 && product?.Contains("Windows 10") == true)
                product = product.Replace("Windows 10", "Windows 11"); // ProductName is not updated on Windows 11
            if (product != null) windows = $"{product} {display} (build {build})".Trim();
        }
        catch { }

        return new SystemInfo
        {
            Cpu = cpu, CpuVendor = vendor, LogicalCores = Environment.ProcessorCount, RamBytes = ram, Gpus = gpus,
            WindowsVersion = windows,
        };
    }

    private static bool IsVirtual(string name) =>
        Regex.IsMatch(name, "Basic Display|Basic Render|Remote Display|Virtual|Parsec|Meta Virtual|Hyper-V|Citrix|VMware|VirtualBox", RegexOptions.IgnoreCase);

    /// <summary>Vendor and integrated/discrete classification by name (informational only).</summary>
    public static GpuInfo Classify(string name)
    {
        if (Regex.IsMatch(name, "NVIDIA|GeForce|Quadro|Tesla", RegexOptions.IgnoreCase))
            return new GpuInfo(name, "NVIDIA", false);
        if (Regex.IsMatch(name, @"Radeon|AMD|ATI\b", RegexOptions.IgnoreCase))
            return new GpuInfo(name, "AMD", !Regex.IsMatch(name, @"\bRX\b|Pro W|FirePro|Instinct", RegexOptions.IgnoreCase));
        if (Regex.IsMatch(name, "Intel", RegexOptions.IgnoreCase))
            return new GpuInfo(name, "Intel", !Regex.IsMatch(name, @"\bArc\b.*A\d{3}|\bArc\b\s*B\d{3}", RegexOptions.IgnoreCase));
        if (Regex.IsMatch(name, "Qualcomm|Adreno", RegexOptions.IgnoreCase))
            return new GpuInfo(name, "Qualcomm", true);
        return new GpuInfo(name, "Other", false);
    }
}
