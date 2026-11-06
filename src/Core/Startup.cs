using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Rogerin.Core
{
    public class StartupEntry
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public string Source { get; set; }
        public bool Enabled { get; set; }

        // Onde fica o flag de habilitado/desabilitado (StartupApproved), para alternar.
        internal string ApprovedHive { get; set; }
        internal string ApprovedKey { get; set; }
        internal string ApprovedValue { get; set; }
    }

    /// <summary>
    /// Lista e habilita/desabilita itens de inicialização (Run do usuário e da máquina, e a pasta Inicializar).
    /// Desabilitar só grava o mesmo flag que o Gerenciador de Tarefas usa (StartupApproved): o programa continua
    /// instalado e registrado, só não abre no próximo logon. Reversível a qualquer momento nesta mesma tela.
    /// </summary>
    public class StartupManager
    {
        readonly UserContext _user;
        public StartupManager(UserContext user) { _user = user; }

        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ApprovedFolderPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        public List<StartupEntry> List()
        {
            var entries = new List<StartupEntry>();
            AddRunKeys(entries, RegistryHive.Users, _user.Sid, "Inicialização (usuário)");
            AddRunKeys(entries, RegistryHive.LocalMachine, null, "Inicialização (todos os usuários)");
            var profile = _user.ProfileFolder();
            if (!string.IsNullOrEmpty(profile))
                AddFolder(entries, Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"), "Pasta Inicializar (usuário)", common: false);
            AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Pasta Inicializar (todos os usuários)", common: true);
            return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        void AddRunKeys(List<StartupEntry> entries, RegistryHive hive, string sid, string source)
        {
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var run = root.OpenSubKey(sid == null ? RunPath : sid + "\\" + RunPath))
            using (var approved = root.OpenSubKey(sid == null ? ApprovedRunPath : sid + "\\" + ApprovedRunPath))
            {
                if (run == null) return;
                foreach (var name in run.GetValueNames())
                {
                    entries.Add(new StartupEntry
                    {
                        Name = name,
                        Command = Convert.ToString(run.GetValue(name)),
                        Source = source,
                        Enabled = IsEnabled(approved, name),
                        ApprovedHive = hive == RegistryHive.Users ? "HKCU" : "HKLM",
                        ApprovedKey = (sid == null ? "" : sid + "\\") + ApprovedRunPath,
                        ApprovedValue = name
                    });
                }
            }
        }

        void AddFolder(List<StartupEntry> entries, string folder, string source, bool common)
        {
            if (!Directory.Exists(folder)) return;
            var hive = common ? RegistryHive.LocalMachine : RegistryHive.Users;
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var approved = root.OpenSubKey(common ? ApprovedFolderPath : _user.Sid + "\\" + ApprovedFolderPath))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    var name = Path.GetFileName(file);
                    entries.Add(new StartupEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Source = source,
                        Enabled = IsEnabled(approved, name),
                        ApprovedHive = hive == RegistryHive.Users ? "HKCU" : "HKLM",
                        ApprovedKey = (hive == RegistryHive.Users ? _user.Sid + "\\" : "") + ApprovedFolderPath,
                        ApprovedValue = name
                    });
                }
            }
        }

        static bool IsEnabled(RegistryKey approved, string name)
        {
            var value = approved?.GetValue(name) as byte[];
            return value == null || value.Length == 0 || value[0] == 0x02;
        }

        /// <summary>Alterna habilitado/desabilitado. O item nunca é removido do Run nem a pasta é apagada.</summary>
        public void Toggle(StartupEntry entry)
        {
            using (var root = RegistryKey.OpenBaseKey(entry.ApprovedHive == "HKCU" ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.CreateSubKey(entry.ApprovedKey, true))
            {
                var current = key.GetValue(entry.ApprovedValue) as byte[] ?? new byte[12];
                current[0] = entry.Enabled ? (byte)0x03 : (byte)0x02;
                key.SetValue(entry.ApprovedValue, current, RegistryValueKind.Binary);
            }
            entry.Enabled = !entry.Enabled;
        }
    }
}
