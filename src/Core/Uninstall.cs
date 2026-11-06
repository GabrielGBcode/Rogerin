using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Rogerin.Core
{
    public class InstalledApp
    {
        public string DisplayName { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public long SizeKB { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallCommand { get; set; }

        internal string RegistryPath { get; set; } // hive\...\Uninstall\{key}, para o resíduo depois

        public string SizeLabel => SizeKB <= 0 ? "" : SizeKB >= 1024 * 1024 ? $"{SizeKB / (1024.0 * 1024):0.#} GB" : $"{SizeKB / 1024.0:0} MB";
    }

    public enum ResidueKind { Registry, Folder }

    /// <summary>Um resíduo encontrado (chave de registro ou pasta) com seu caminho exato, para listar e deixar escolher.</summary>
    public class ResidueCandidate
    {
        public ResidueKind Kind { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// Lista programas instalados (o mesmo painel que "Aplicativos e recursos") e roda o desinstalador de cada um.
    /// A string de desinstalação é sempre separada em executável + argumentos pelo próprio Windows
    /// (CommandLineToArgvW): nunca passa por cmd.exe nem por nenhum shell.
    /// </summary>
    public class UninstallManager
    {
        readonly UserContext _user;
        public UninstallManager(UserContext user) { _user = user; }

        static readonly (RegistryHive Hive, RegistryView View, string Path)[] Roots =
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        public List<InstalledApp> List()
        {
            var apps = new List<InstalledApp>();
            foreach (var (hive, view, path) in Roots)
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var uninstall = root.OpenSubKey(path))
                {
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using (var key = uninstall.OpenSubKey(name))
                        {
                            var app = Read(key, hive, path + "\\" + name);
                            if (app != null) apps.Add(app);
                        }
                    }
                }
            }
            // O mesmo app pode aparecer na visão 32 e 64 bits; mantem só uma entrada por nome.
            return apps.GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static InstalledApp Read(RegistryKey key, RegistryHive hive, string path)
        {
            var name = key?.GetValue("DisplayName") as string;
            // Mesmos filtros que "Aplicativos e recursos": sem nome, componente do sistema ou sem desinstalador não aparece.
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Convert.ToInt32(key.GetValue("SystemComponent", 0)) == 1) return null;
            var cmd = (key.GetValue("UninstallString") as string) ?? (key.GetValue("QuietUninstallString") as string);
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            return new InstalledApp
            {
                DisplayName = name,
                Publisher = key.GetValue("Publisher") as string,
                Version = key.GetValue("DisplayVersion") as string,
                SizeKB = Convert.ToInt64(key.GetValue("EstimatedSize", 0L)),
                InstallLocation = key.GetValue("InstallLocation") as string,
                UninstallCommand = cmd,
                RegistryPath = (hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + path
            };
        }

        /// <summary>Códigos de saída tratados como sucesso além de 0 (3010 = ERROR_SUCCESS_REBOOT_REQUIRED, comum em instaladores MSI).</summary>
        static readonly int[] OkExitCodes = { 0, 3010 };

        /// <summary>
        /// Roda o desinstalador do app e espera terminar (o usuário pode precisar interagir com a janela dele).
        /// Lança só se não conseguir nem iniciar o processo; o código de saída vem no retorno para o chamador decidir.
        /// </summary>
        public int Uninstall(InstalledApp app)
        {
            var parts = Native.SplitCommandLine(Environment.ExpandEnvironmentVariables(app.UninstallCommand));
            if (parts.Length == 0) throw new InvalidOperationException("Comando de desinstalação vazio.");
            var psi = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            psi.Arguments = string.Join(" ", parts.Skip(1).Select(QuoteArg));
            using (var p = Process.Start(psi))
            {
                if (p == null) throw new Win32Exception("Não foi possível iniciar o desinstalador.");
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static bool ExitCodeOk(int code) => OkExitCodes.Contains(code);

        /// <summary>
        /// Remonta um argumento exatamente como CommandLineToArgvW espera (regra do CRT da Microsoft),
        /// já que .NET Framework 4.8 não tem ProcessStartInfo.ArgumentList: sem isto um argumento com
        /// espaço ou aspas quebraria ao ser passado de volta como uma única string "Arguments".
        /// </summary>
        static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
                if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
                if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
                else { sb.Append('\\', backslashes).Append(arg[i]); }
            }
            return sb.Append('"').ToString();
        }

        static bool RegistryKeyExists(string path)
        {
            var hive = path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var sub = path.Substring(path.IndexOf('\\') + 1);
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var key = root.OpenSubKey(sub))
                return key != null;
        }

        /// <summary>Nome exato da pasta do programa (ex.: "Gemini"), nunca o nome de exibição: evita pegar a pasta do fabricante.</summary>
        static string FolderName(InstalledApp app)
        {
            if (!string.IsNullOrEmpty(app.InstallLocation))
                return Path.GetFileName(app.InstallLocation.TrimEnd('\\', '/'));
            try
            {
                var parts = Native.SplitCommandLine(Environment.ExpandEnvironmentVariables(app.UninstallCommand));
                return parts.Length > 0 ? Path.GetFileName(Path.GetDirectoryName(parts[0])) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Locais padrão onde apps guardam dados próprios, no perfil do usuário logado no console (não o do técnico elevado).</summary>
        IEnumerable<string> StandardDataFolders()
        {
            var profile = _user.ProfileFolder();
            if (!string.IsNullOrEmpty(profile))
            {
                yield return Path.Combine(profile, "AppData\\Roaming");
                yield return Path.Combine(profile, "AppData\\Local");
                yield return Path.Combine(profile, "AppData\\LocalLow");
            }
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); // ProgramData
        }

        /// <summary>
        /// O que sobrou depois da desinstalação, para o técnico decidir o que limpa: a mesma chave de registro
        /// e a mesma pasta de instalação que já eram conhecidas, mais uma varredura pelo nome EXATO da pasta
        /// do programa (nunca um texto parecido) nos locais padrão de dados de app e em "Software" do registro.
        /// Cada candidato volta separado — nada é escolhido ou apagado aqui.
        /// </summary>
        public List<ResidueCandidate> CheckResidue(InstalledApp app)
        {
            var list = new List<ResidueCandidate>();
            if (RegistryKeyExists(app.RegistryPath))
                list.Add(new ResidueCandidate { Kind = ResidueKind.Registry, Path = app.RegistryPath });
            if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                list.Add(new ResidueCandidate { Kind = ResidueKind.Folder, Path = app.InstallLocation });

            var name = FolderName(app);
            if (string.IsNullOrEmpty(name)) return list;

            foreach (var baseDir in StandardDataFolders())
            {
                var candidate = Path.Combine(baseDir, name);
                if (Directory.Exists(candidate) && !list.Any(c => c.Kind == ResidueKind.Folder && string.Equals(c.Path, candidate, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new ResidueCandidate { Kind = ResidueKind.Folder, Path = candidate });
            }
            foreach (var hivePrefix in new[] { "HKCU", "HKLM" })
            {
                var regPath = hivePrefix + "\\Software\\" + name;
                if (!string.Equals(regPath, app.RegistryPath, StringComparison.OrdinalIgnoreCase) && RegistryKeyExists(regPath))
                    list.Add(new ResidueCandidate { Kind = ResidueKind.Registry, Path = regPath });
            }
            return list;
        }

        /// <summary>Apaga exatamente os candidatos recebidos (só os que o técnico marcou na confirmação).</summary>
        public void CleanResidue(IEnumerable<ResidueCandidate> chosen)
        {
            foreach (var c in chosen)
            {
                if (c.Kind == ResidueKind.Registry)
                {
                    var hive = c.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                    var sub = c.Path.Substring(c.Path.IndexOf('\\') + 1);
                    var i = sub.LastIndexOf('\\');
                    using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                    using (var parent = root.OpenSubKey(sub.Substring(0, i), true))
                        parent?.DeleteSubKeyTree(sub.Substring(i + 1), false);
                }
                else if (Directory.Exists(c.Path))
                    Directory.Delete(c.Path, true);
            }
        }
    }
}
