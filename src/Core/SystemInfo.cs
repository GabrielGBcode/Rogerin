using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace Rogerin.Core
{
    public class SystemInfo
    {
        public string Caption { get; private set; }
        public string WindowsMajor { get; private set; }
        public int Build { get; private set; }
        public int Ubr { get; private set; }
        public string DisplayVersion { get; private set; }
        public string Edition { get; private set; }
        public string ComputerName { get; private set; }
        public bool PartOfDomain { get; private set; }
        public string Domain { get; private set; }
        public bool IsAdmin { get; private set; }
        public string Cpu { get; private set; }
        public int Cores { get; private set; }
        public int Threads { get; private set; }
        public string Gpu { get; private set; }
        public string Manufacturer { get; private set; }
        public string Model { get; private set; }
        public string SerialNumber { get; private set; }
        public double RamGB { get; private set; }
        public string SystemDrive { get; private set; }
        public double DiskFreeGB { get; private set; }
        public double DiskTotalGB { get; private set; }
        /// <summary>"HDD", "SSD" ou null quando o Windows nao informa (comum em VMs e controladoras RAID).</summary>
        public string DiskMedia { get; private set; }
        /// <summary>Saude informada pelo Windows (SMART): "Saudável", "Alerta", "Com falha" ou null.</summary>
        public string DiskHealth { get; private set; }
        public string DiskModel { get; private set; }
        public double UptimeHours { get; private set; }

        public bool IsHdd => DiskMedia == "HDD";
        public bool DiskUnhealthy => DiskHealth != null && DiskHealth != "Saudável";

        public string OsLabel => $"{Caption} {DisplayVersion} · build {Build}.{Ubr}";
        public string DomainLabel => PartOfDomain ? $"Domínio {Domain}" : $"Grupo de trabalho {Domain}";

        public static SystemInfo Collect()
        {
            var info = new SystemInfo
            {
                ComputerName = Environment.MachineName,
                IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
                SystemDrive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\')
            };

            using (var cv = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                info.Build = int.Parse((string)cv.GetValue("CurrentBuildNumber", "0"));
                info.Ubr = Convert.ToInt32(cv.GetValue("UBR", 0));
                info.DisplayVersion = (string)cv.GetValue("DisplayVersion", cv.GetValue("ReleaseId", ""));
                info.Edition = (string)cv.GetValue("EditionID", "");
            }
            info.WindowsMajor = info.Build >= 22000 ? "11" : "10";

            info.Caption = Wmi("Win32_OperatingSystem", "Caption") ?? $"Windows {info.WindowsMajor}";
            info.Cpu = (Wmi("Win32_Processor", "Name") ?? "").Trim();
            info.Cores = Convert.ToInt32(Wmi("Win32_Processor", "NumberOfCores") ?? "0");
            info.Threads = Convert.ToInt32(Wmi("Win32_Processor", "NumberOfLogicalProcessors") ?? "0");
            info.Gpu = PhysicalGpus();
            info.SerialNumber = Real(Wmi("Win32_BIOS", "SerialNumber"));

            using (var s = new ManagementObjectSearcher("SELECT PartOfDomain, Domain, TotalPhysicalMemory, Manufacturer, Model FROM Win32_ComputerSystem"))
            using (var cs = s.Get().Cast<ManagementObject>().First())
            {
                info.PartOfDomain = (bool)cs["PartOfDomain"];
                info.Domain = (string)cs["Domain"];
                info.RamGB = Math.Round(Convert.ToDouble(cs["TotalPhysicalMemory"]) / (1L << 30), 1);
                info.Manufacturer = Real((string)cs["Manufacturer"]);
                info.Model = Real((string)cs["Model"]);
            }

            var drive = new DriveInfo(info.SystemDrive);
            info.DiskFreeGB = Math.Round(drive.AvailableFreeSpace / (double)(1L << 30), 1);
            info.DiskTotalGB = Math.Round(drive.TotalSize / (double)(1L << 30), 1);
            info.ReadSystemDisk();
            info.UptimeHours = Math.Round(Native.GetTickCount64() / 3_600_000.0, 1);
            return info;
        }

        /// <summary>
        /// Acha o disco fisico da unidade do sistema pela API de armazenamento do Windows (WMI)
        /// e le o tipo de midia (MediaType 3 = HDD, 4 = SSD) e a saude.
        /// </summary>
        void ReadSystemDisk()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                var letter = SystemDrive.TrimEnd(':');
                string diskNumber;
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'")))
                    diskNumber = s.Get().Cast<ManagementObject>().Select(o => Convert.ToString(o["DiskNumber"])).FirstOrDefault();
                if (diskNumber == null) return;

                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT FriendlyName, MediaType, SpindleSpeed, HealthStatus FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'")))
                {
                    var d = s.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (d == null) return;
                    DiskModel = (d["FriendlyName"] as string)?.Trim();
                    var media = Convert.ToInt32(d["MediaType"] ?? 0);
                    var spindle = d["SpindleSpeed"] == null ? -1L : Convert.ToInt64(d["SpindleSpeed"]);
                    // Sem MediaType, a rotacao ainda diferencia: SSD informa 0 RPM; uint.MaxValue = desconhecido.
                    DiskMedia = media == 3 ? "HDD" : media == 4 ? "SSD"
                        : spindle > 0 && spindle < uint.MaxValue ? "HDD" : spindle == 0 ? "SSD" : null;
                    switch (Convert.ToInt32(d["HealthStatus"] ?? 5))
                    {
                        case 0: DiskHealth = "Saudável"; break;
                        case 1: DiskHealth = "Alerta"; break;
                        case 2: DiskHealth = "Com falha"; break;
                    }
                }
            }
            catch (ManagementException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        // Placas-mae genericas trazem textos de exemplo no lugar de fabricante, modelo e serie.
        static readonly string[] Placeholders = { "system manufacturer", "system product name", "system serial number",
            "to be filled by o.e.m.", "default string", "not applicable", "none", "0" };

        static string Real(string value)
        {
            var v = value?.Trim();
            return string.IsNullOrEmpty(v) || Placeholders.Contains(v.ToLowerInvariant()) ? null : v;
        }

        /// <summary>Lista as GPUs ignorando adaptadores virtuais (acesso remoto, monitores virtuais).</summary>
        static string PhysicalGpus()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                {
                    var all = s.Get().Cast<ManagementObject>()
                        .Select(o => new { Name = (o["Name"] as string)?.Trim(), Pnp = o["PNPDeviceID"] as string ?? "" })
                        .Where(g => !string.IsNullOrEmpty(g.Name)).ToList();
                    // GPUs reais ficam no barramento PCI; virtuais sao ROOT\ ou SWD\.
                    var real = all.Where(g => g.Pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)).ToList();
                    return string.Join(" · ", (real.Count > 0 ? real : all).Select(g => g.Name).Distinct());
                }
            }
            catch (ManagementException) { return null; }
        }

        static string Wmi(string cls, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}"))
                    return s.Get().Cast<ManagementObject>().Select(o => o[prop] == null ? null : Convert.ToString(o[prop])).FirstOrDefault();
            }
            catch (ManagementException) { return null; }
        }
    }
}
