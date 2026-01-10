using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Input;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using NetSonar.Avalonia.Extensions;
using ZLinq;

namespace NetSonar.Avalonia.SystemOS;

public static class SystemAware
{
    /// <summary>
    /// Gets the key modifiers for the Control key or the Meta key on macOS.
    /// </summary>
    public static KeyModifiers ControlOrMeta => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
    public static string ControlOrMetaString => OperatingSystem.IsMacOS() ? "Meta" : "Ctrl";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer); //Used to use ref with comment below
    // but ref doesn't work.(Use of [In, Out] instead of ref
    //causes access violation exception on windows xp
    //comment: most probably caused by MEMORYSTATUSEX being declared as a class
    //(at least at pinvoke.net). On Win7, ref and struct work.

    // Alternate Version Using "ref," And Works With Alternate Code Below.
    // Also See Alternate Version Of [MEMORYSTATUSEX] Structure With
    // Fields Documented.
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    static extern bool _GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static MEMORYSTATUSEX GetMemoryStatus()
    {
        var statEX = new MEMORYSTATUSEX();
        if (OperatingSystem.IsWindows())
        {
            GlobalMemoryStatusEx(statEX);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!File.Exists("/proc/meminfo")) return statEX;
            try
            {
                const ushort factor = 1024;
                var result = File.ReadAllText("/proc/meminfo");
                //var result = "MemTotal:        8288440 kB\nMemFree:         5616380 kB\nMemAvailable:    6885408 kB\nBuffers:           63240 kB\nCached:          1390996 kB\nSwapCached:            0 kB\nActive:           272516 kB\nInactive:        1773312 kB\nActive(anon):       1888 kB\nInactive(anon):   596168 kB\nActive(file):     270628 kB\nInactive(file):  1177144 kB\nUnevictable:           0 kB\nMlocked:               0 kB\nSwapTotal:       2097148 kB\nSwapFree:        2097148 kB\nDirty:               172 kB\nWriteback:             0 kB\nAnonPages:        591608 kB\nMapped:           292288 kB\nShmem:              6464 kB\nKReclaimable:      86228 kB\nSlab:             170544 kB\nSReclaimable:      86228 kB\nSUnreclaim:        84316 kB\nKernelStack:       12160 kB\nPageTables:        13992 kB\nNFS_Unstable:          0 kB\nBounce:                0 kB\nWritebackTmp:          0 kB\nCommitLimit:     6241368 kB\nCommitted_AS:    3588500 kB\nVmallocTotal:   34359738367 kB\nVmallocUsed:       61060 kB\nVmallocChunk:          0 kB\nPercpu:            91136 kB\nHardwareCorrupted:     0 kB\nAnonHugePages:         0 kB\nShmemHugePages:        0 kB\nShmemPmdMapped:        0 kB\nFileHugePages:         0 kB\nFilePmdMapped:         0 kB\nHugePages_Total:       0\nHugePages_Free:        0\nHugePages_Rsvd:        0\nHugePages_Surp:        0\nHugepagesize:       2048 kB\nHugetlb:               0 kB\nDirectMap4k:      216464 kB\nDirectMap2M:     4169728 kB\nDirectMap1G:     5242880 kB";
                var matches = Regex.Matches(result, @"(\S+):\s*([0-9]+).(\S+)");
                foreach (Match match in matches)
                {
                    if (!match.Success || match.Groups.Count < 2) continue;
                    ulong value = ulong.Parse(match.Groups[2].Value) * factor;
                    switch (match.Groups[1].Value)
                    {
                        case "MemTotal":
                            statEX.ullTotalPhys = value;
                            statEX.ullTotalVirtual = value;
                            continue;
                        case "MemFree":
                            statEX.ullAvailPhys = value;
                            continue;
                        case "MemAvailable":
                            statEX.ullAvailVirtual = value;
                            continue;
                        case "SwapTotal":
                            statEX.ullTotalPageFile = value;
                            continue;
                        case "SwapFree":
                            statEX.ullAvailPageFile = value;
                            continue;
                    }
                }
            }
            catch (Exception e)
            {
                App.HandleSafeException(e, "GetMemoryStatus");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                var result = GetProcessOutput("vm_stat");

                if (string.IsNullOrWhiteSpace(result)) return statEX;

                //var result = "Mach Virtual Memory Statistics: (page size of 4096 bytes)\nPages free:                             3044485.\nPages active:                            400375.\nPages inactive:                          235679.\nPages speculative:                       189311.\nPages throttled:                              0.\nPages wired down:                        324269.\nPages purgeable:                          27417.\n\"Translation faults\":                   5500903.\nPages copy-on-write:                     388354.\nPages zero filled:                      2724856.\nPages reactivated:                          410.\nPages purged:                               972.\nFile-backed pages:                       400847.\nAnonymous pages:                         424518.\nPages stored in compressor:                   0.\nPages occupied by compressor:                 0.\nDecompressions:                               0.\nCompressions:                                 0.\nPageins:                                 354428.\nPageouts:                                     0.\nSwapins:                                      0.\nSwapouts:                                     0.";
                var matchPageSize = Regex.Match(result, @"page size of ([0-9]+) bytes");

                if (!matchPageSize.Success || matchPageSize.Groups.Count < 2) return statEX;
                ushort pageSize = ushort.Parse(matchPageSize.Groups[1].Value);

                var matches = Regex.Matches(result, @"(.*):\s*([0-9]+)");
                foreach (Match match in matches)
                {
                    if (!match.Success || match.Groups.Count < 2) continue;

                    ulong value = ulong.Parse(match.Groups[2].Value) * pageSize;

                    if (match.Groups[1].Value.StartsWith("\"Translation faults\"")) break;
                    if (match.Groups[1].Value.StartsWith("Pages "))
                    {
                        statEX.ullTotalPhys += value;
                    }

                    switch (match.Groups[1].Value)
                    {
                        case "Pages free":
                        case "Pages inactive":
                        case "Pages purgeable":
                        case "Pages throttled":
                        case "Pages speculative":
                            statEX.ullAvailPhys += value;
                            continue;
                    }
                }

                statEX.ullTotalVirtual = statEX.ullTotalPhys;
                statEX.ullAvailVirtual = statEX.ullAvailPhys;
            }
            catch (Exception e)
            {
                App.HandleSafeException(e, "GetMemoryStatus");
            }
        }

        return statEX;
    }

    public static string? GetProcessorName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                /*ManagementObjectSearcher mos =
                    new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");
                foreach (ManagementObject mo in mos.Get())
                {
                    Console.WriteLine(mo["Name"]);
                }*/
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString")?.ToString();
            }

            if (OperatingSystem.IsLinux())
            {
                if (!File.Exists("/proc/cpuinfo")) return null;
                //var result = "processor\t: 0\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 0\ncpu cores\t: 6\napicid\t\t: 0\ninitial apicid\t: 0\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:\n\nprocessor\t: 1\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 1\ncpu cores\t: 6\napicid\t\t: 1\ninitial apicid\t: 1\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:\n\nprocessor\t: 2\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 2\ncpu cores\t: 6\napicid\t\t: 2\ninitial apicid\t: 2\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:\n\nprocessor\t: 3\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 3\ncpu cores\t: 6\napicid\t\t: 3\ninitial apicid\t: 3\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:\n\nprocessor\t: 4\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 4\ncpu cores\t: 6\napicid\t\t: 4\ninitial apicid\t: 4\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:\n\nprocessor\t: 5\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 158\nmodel name\t: Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz\nstepping\t: 12\nmicrocode\t: 0xffffffff\ncpu MHz\t\t: 3600.011\ncache size\t: 16384 KB\nphysical id\t: 0\nsiblings\t: 6\ncore id\t\t: 5\ncpu cores\t: 6\napicid\t\t: 5\ninitial apicid\t: 5\nfpu\t\t: yes\nfpu_exception\t: yes\ncpuid level\t: 22\nwp\t\t: yes\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht syscall nx pdpe1gb rdtscp lm constant_tsc arch_perfmon nopl xtopology tsc_reliable nonstop_tsc cpuid pni pclmulqdq ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt tsc_deadline_timer aes xsave avx f16c rdrand hypervisor lahf_lm abm 3dnowprefetch invpcid_single ssbd ibrs ibpb stibp fsgsbase tsc_adjust bmi1 avx2 smep bmi2 invpcid rdseed adx smap clflushopt xsaveopt xsavec xgetbv1 xsaves arat flush_l1d arch_capabilities\nbugs\t\t: spectre_v1 spectre_v2 spec_store_bypass mds swapgs itlb_multihit srbds\nbogomips\t: 7200.02\nclflush size\t: 64\ncache_alignment\t: 64\naddress sizes\t: 45 bits physical, 48 bits virtual\npower management:";
                var result = File.ReadAllText("/proc/cpuinfo");
                var match = Regex.Match(result, @"model name.*:.(.*)");
                if (!match.Success || match.Groups.Count < 2)
                {
                    return null;
                }

                return match.Groups[1].ToString();
            }

            if (OperatingSystem.IsMacOS())
            {
                return GetProcessOutput("sysctl", "-n machdep.cpu.brand_string")?.TrimEnd('\r', '\n');
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "GetProcessorName");
        }

        return null;
    }

    public static string? GetGraphicCardName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                //HKEY_LOCAL_MACHINE\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000
                //HKEY_LOCAL_MACHINE\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0002") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0003") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0004") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0005") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0006") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0007") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0008") ??
                                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0009");

                var gpu = key?.GetValue("DriverDesc")?.ToString();
                if (gpu is null) return null;

                try
                {
                    var gpuMemBytes = key!.GetValue("HardwareInformation.qwMemorySize");
                    if (gpuMemBytes is not null)
                    {
                        gpu += $" {ConverterExtension.ToFileSizeString(Convert.ToInt64(gpuMemBytes), 1)}";
                    }
                }
                catch (Exception e)
                {
                    App.HandleSafeException(e, "GetGraphicCardName");
                }

                return gpu;
            }

            if (OperatingSystem.IsLinux())
            {
                return GetProcessOutput("bash", "-c \"lspci | grep ' VGA ' | cut -d' ' -f 1 | xargs -i lspci -v -s {} | grep 'Subsystem:' | cut -d':' -f 2\"").Trim();
            }

            if (OperatingSystem.IsMacOS())
            {
                return GetProcessOutput("bash", "-c \"system_profiler SPDisplaysDataType | grep ' Chipset Model: ' | cut -d':' -f 2\"").Trim();
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "GetGraphicCardName");
        }

        return null;
    }

    /// <summary>
    /// Normalize the executable extension for the current OS.
    /// </summary>
    /// <param name="path"></param>
    /// <returns>Normalized executable with the extension.</returns>
    public static string NormalizeExecutableExtension(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                 return $"{path}.exe";
        }

        return path;
    }

    /// <summary>
    /// Try to find a file under all path's of <c>PATH</c> environment variable
    /// </summary>
    /// <param name="file">File to found by exploring all path under <c>PATH</c> environment variable</param>
    /// <param name="result">The returned full file full path. Returns <c>null</c> if not found.</param>
    /// <returns><c>true</c> if file was found, otherwise <c>false</c>.</returns>
    public static bool TryFindEnvFile(string file, [NotNullWhen(true)] out string? result)
    {
        var separator = ';';
        result = null;

        var checkPath = Environment.CurrentDirectory;
        var fullPath = Path.Combine(checkPath, file);
        if (File.Exists(fullPath))
        {
            result = fullPath;
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            checkPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrWhiteSpace(checkPath))
            {
                fullPath = Path.Combine(checkPath, file);
                if (File.Exists(fullPath))
                {
                    result = fullPath;
                    return true;
                }
            }

            if (Environment.Is64BitOperatingSystem)
            {
                checkPath = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
                if (!string.IsNullOrWhiteSpace(checkPath))
                {
                    fullPath = Path.Combine(checkPath, file);
                    if (File.Exists(fullPath))
                    {
                        result = fullPath;
                        return true;
                    }
                }
            }
        }
        else
        {
            separator = ':';
        }

        string? envPath;
        if (OperatingSystem.IsWindows())
        {
            envPath = $"{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)};{Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)}"; }
        else
        {
            envPath = Environment.GetEnvironmentVariable("PATH");
        }

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var paths = envPath.Split(separator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var path in paths.AsValueEnumerable().Distinct())
            {
                fullPath = Path.Combine(path, file);
                if (File.Exists(fullPath))
                {
                    result = fullPath;
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SelectFileOnExplorer(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            StartProcess("explorer.exe", $"/select,\"{filePath}\"");
        }
        else
        {
            var path = Path.GetDirectoryName(filePath);
            if (path is null) return false;
            StartProcess(path);
        }

        return true;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using (Process.Start(new ProcessStartInfo(url.Replace("&", "^&")) { UseShellExecute = true })) { }
            }
            else if (OperatingSystem.IsLinux())
            {
                using (Process.Start("xdg-open", url)) { }
            }
            else if (OperatingSystem.IsMacOS())
            {
                using (Process.Start("open", url)) { }
            }
            else
            {
                // throw
            }
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "SystemAware.OpenUrl");
        }

    }

    public static void StartProcess(string name, string? arguments = null, bool waitForCompletion = false, int waitTimeout = Timeout.Infinite)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(name, arguments!) { UseShellExecute = true });
            if (process is null) return;
            if (waitForCompletion) process.WaitForExit(waitTimeout);
        }
        catch (Exception e)
        {
            App.HandleSafeException(e, "SystemAware.StartProcess");
        }
    }

    public static (string standardOutput, string errorOutput) GetProcessOutputWithErrors(string filename, string? arguments = null, string? workingDirectory = null, int timeout = Timeout.Infinite)
    {
        using var process = new Process();
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.FileName = filename;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        var standardOutput = new StringBuilder();
        var errorOutput = new StringBuilder();


        using var outputWaitHandle = new AutoResetEvent(false);
        using var errorWaitHandle = new AutoResetEvent(false);

        void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                outputWaitHandle.Set();
            }
            else
            {
                standardOutput.AppendLine(e.Data);
            }
        }

        void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                errorWaitHandle.Set();
            }
            else
            {
                errorOutput.AppendLine(e.Data);
            }
        }


        process.OutputDataReceived += ProcessOnOutputDataReceived;
        process.ErrorDataReceived += ProcessOnErrorDataReceived;

        process.Start();

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        process.WaitForExit(timeout);
        outputWaitHandle.WaitOne(timeout);
        errorWaitHandle.WaitOne(timeout);

        process.OutputDataReceived -= ProcessOnOutputDataReceived;
        process.ErrorDataReceived -= ProcessOnErrorDataReceived;

        return (standardOutput.ToString(), errorOutput.ToString());
    }

    public static string GetProcessOutput(string filename, string? arguments = null, string? workingDirectory = null, int timeout = Timeout.Infinite)
    {
        using var process = new Process();
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.FileName = filename;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        var stringBuilder = new StringBuilder();

        using var outputWaitHandle = new AutoResetEvent(false);

        void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                outputWaitHandle.Set();
            }
            else
            {
                stringBuilder.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += ProcessOnOutputDataReceived;

        process.Start();

        process.BeginOutputReadLine();

        process.WaitForExit(timeout);
        outputWaitHandle.WaitOne(timeout);

        process.OutputDataReceived -= ProcessOnOutputDataReceived;

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Produces a beep tone from speaker
    /// </summary>
    /// <param name="frequency">The sound frequency</param>
    /// <param name="duration">The duration of the beep in milliseconds</param>
    /// <param name="waitForCompletion">True to wait for beep ends to continue, otherwise run in background</param>
    public static void Beep(int frequency = 800, int duration = 150, bool waitForCompletion = false)
    {
        frequency = Math.Clamp(frequency, 20, 20_000);
        duration = Math.Max(40, duration);

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416 // Validate platform compatibility
            if (waitForCompletion) { Console.Beep(frequency, duration); }
            else { Task.Run(() => Console.Beep(frequency, duration)); }
#pragma warning restore CA1416 // Validate platform compatibility

        }
        else
        {
            var seconds = Math.Round(duration / 1000.0, 3, MidpointRounding.AwayFromZero);
            StartProcess("bash", $"-c \"if [ '$(command -v speaker-test)' ]; then speaker-test -t sine -f {frequency} -l 1 > /dev/null & sleep {seconds} && kill -9 $!; else echo -e '\\a'; fi\"",
                waitForCompletion);
        }
    }

    /// <summary>
    /// Gets the main name of the operative system
    /// </summary>
    public static string OperatingSystemName
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "Windows";
            if (OperatingSystem.IsMacOS()) return "macOS";
            if (OperatingSystem.IsLinux()) return "Linux";
            if (OperatingSystem.IsFreeBSD()) return "FreeBSD";
            if (OperatingSystem.IsAndroid()) return "Android";
            if (OperatingSystem.IsIOS()) return "iOS";
            if (OperatingSystem.IsTvOS()) return "Tv OS";
            if (OperatingSystem.IsWatchOS()) return "WatchOS";
            if (OperatingSystem.IsBrowser()) return "Browser";
            return "Unknown";
        }
    }

    /// <summary>
    /// Gets the main name of the operative system with architecture
    /// </summary>
    public static string OperatingSystemNameWithArch => $"{OperatingSystemName} {RuntimeInformation.OSArchitecture}";
}