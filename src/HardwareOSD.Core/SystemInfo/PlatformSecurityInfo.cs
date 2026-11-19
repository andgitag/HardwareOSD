using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Read-only platform security state used by the System page. Unknown is distinct from off.</summary>
public sealed record PlatformSecuritySnapshot(
    bool? SecureBoot,
    int? VbsStatus,
    bool? MemoryIntegrity,
    bool? TestSigning,
    string? Note);

public static class PlatformSecurityInfo
{
    private const int SystemCodeIntegrityInformationClass = 103;
    private const uint CodeIntegrityOptionTestSign = 0x00000002;

    public static PlatformSecuritySnapshot Read()
    {
        var note = new List<string>();
        bool? secureBoot = ReadDword(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is { } sb
            ? sb != 0
            : null;
        if (secureBoot is null) note.Add("secure-boot-not-reported");

        bool? integrity = ReadDword(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
            "Enabled") is { } hvci
            ? hvci != 0
            : null;
        if (integrity is null) note.Add("memory-integrity-not-reported");

        int? vbs = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
            foreach (var item in searcher.Get())
            {
                using var management = (ManagementObject)item;
                var value = management["VirtualizationBasedSecurityStatus"];
                if (value is not null)
                {
                    vbs = Convert.ToInt32(value);
                    break;
                }
            }
        }
        catch
        {
            note.Add("vbs-not-reported");
        }

        bool? testSigning = null;
        try
        {
            var info = new SystemCodeIntegrityInformation
            {
                Length = (uint)Marshal.SizeOf<SystemCodeIntegrityInformation>(),
            };
            var status = NtQuerySystemInformation(SystemCodeIntegrityInformationClass, ref info,
                info.Length, out _);
            if (status == 0)
            {
                testSigning = (info.CodeIntegrityOptions & CodeIntegrityOptionTestSign) != 0;
            }
        }
        catch
        {
            note.Add("test-signing-not-reported");
        }

        return new PlatformSecuritySnapshot(secureBoot, vbs, integrity, testSigning,
            note.Count == 0 ? null : string.Join(",", note));
    }

    private static int? ReadDword(RegistryHive hive, string path, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(value) is int number ? number : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCodeIntegrityInformation
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        ref SystemCodeIntegrityInformation systemInformation,
        uint systemInformationLength,
        out uint returnLength);
}
