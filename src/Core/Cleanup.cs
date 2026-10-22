using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Rogerin.Core
{
    public class CleanupTarget
    {
        public string Name { get; set; }
        public string Group { get; set; } = "Limpeza";
        /// <summary>Pasta exibida para o tecnico.</summary>
        public string Path { get; set; }
        /// <summary>Pastas realmente limpas. Se nulo, usa <see cref="Path"/>.</summary>
        public List<string> Paths { get; set; }
        public string Pattern { get; set; } = "*";
        public bool IsDnsCache { get; set; }
        /// <summary>Desfragmenta o disco do sistema (defrag.exe /O). So aparece em HDD.</summary>
        public bool IsDiskOptimize { get; set; }
        /// <summary>Roda a Limpeza de Disco do Windows (cleanmgr.exe) com todas as opcoes marcadas.</summary>
        public bool IsDiskCleanup { get; set; }
        /// <summary>Acao sob demanda, sem arquivos para medir.</summary>
        public bool OnDemand => IsDnsCache || IsDiskOptimize || IsDiskCleanup;
        public string Risk { get; set; } = "safe";

        // Navegadores
        public string BrowserName { get; set; }
        public bool IgnoreAge { get; set; }
        public string[] Processes { get; set; }
        public int ProfileCount { get; set; }
        public bool Running { get; set; }
        public bool IsBrowser => BrowserName != null;

        public int Files { get; set; }
        public long Bytes { get; set; }
        public string SizeLabel => Bytes >= 1L << 30 ? $"{Bytes / (double)(1L << 30):0.0} GB" : $"{Bytes / (double)(1L << 20):0.0} MB";

        public IEnumerable<string> Roots => Paths ?? (Path == null ? Enumerable.Empty<string>() : new[] { Path });
    }

    /// <summary>
    /// Remove arquivos em pastas de cache conhecidas. Arquivos em uso sao ignorados.
    /// Pastas do sistema: so arquivos com mais de 1 dia. Navegadores: todo o cache (o navegador recria).
    /// </summary>
    public class Cleanup
    {
        readonly UserContext _user;
        readonly SystemInfo _sys;
        public TimeSpan MinAge { get; set; } = TimeSpan.FromDays(1);

        public Cleanup(UserContext user, SystemInfo sys = null) { _user = user; _sys = sys; }

        public List<CleanupTarget> Targets()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var profile = UserProfilePath();
            var list = new List<CleanupTarget>
            {
                new CleanupTarget { Name = "Temporários do Windows", Path = System.IO.Path.Combine(win, "Temp") },
                new CleanupTarget { Name = "Cache de download do Windows Update", Path = System.IO.Path.Combine(win, @"SoftwareDistribution\Download") },
                new CleanupTarget { Name = "Cache da Otimização de Entrega", Path = System.IO.Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
                new CleanupTarget { Name = "Relatórios de erro (WER)", Path = System.IO.Path.Combine(pd, @"Microsoft\Windows\WER") },
            };
            if (profile != null)
            {
                list.Insert(0, new CleanupTarget { Name = "Temporários do usuário", Path = System.IO.Path.Combine(profile, @"AppData\Local\Temp") });
                list.Add(new CleanupTarget { Name = "Cache de miniaturas", Path = System.IO.Path.Combine(profile, @"AppData\Local\Microsoft\Windows\Explorer"), Pattern = "thumbcache_*.db" });
            }
            list.Add(new CleanupTarget { Name = "Cache DNS", IsDnsCache = true });
            if (File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "cleanmgr.exe")))
                list.Add(new CleanupTarget { Name = "Limpeza de Disco do Windows (todas as opções)", Path = SystemDriveRoot(), IsDiskCleanup = true, Risk = "moderate" });
            if (_sys?.IsHdd == true)
                list.Add(new CleanupTarget { Name = "Desfragmentar disco do sistema agora", Group = "Disco", Path = _sys.SystemDrive, IsDiskOptimize = true, Risk = "moderate" });
            if (profile != null) list.AddRange(Browsers.Find(profile));
            return list;
        }

        static string SystemDriveRoot() => System.IO.Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

        const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        /// <summary>Numero do perfil do cleanmgr (/sagerun:N). Os valores StateFlagsNNNN sao removidos ao final.</summary>
        const int SageRun = 4242;
        static string StateFlags => $"StateFlags{SageRun:0000}";

        /// <summary>
        /// Opcoes da Limpeza de Disco que nunca sao marcadas: apagam arquivos do usuario
        /// (pasta Downloads, versoes do Historico de Arquivos), impedem "Restaurar o PC" ou apagam o Windows.old,
        /// que permite "Voltar para a versao anterior" depois de uma atualizacao grande.
        /// </summary>
        public static readonly string[] DiskCleanupExcluded = { "DownloadsFolder", "User file versions", "Windows ESD installation files", "Previous Installations" };

        /// <summary>
        /// Marca todas as opcoes (exceto as excluidas) no registro, roda "cleanmgr /sagerun" como administrador
        /// e espera terminar. Retorna o espaco liberado no disco do sistema.
        /// </summary>
        long RunDiskCleanup(CleanupTarget t)
        {
            if (IsRunning(new CleanupTarget { Processes = new[] { "cleanmgr" } }))
                throw new InvalidOperationException("A Limpeza de Disco do Windows já está aberta. Feche-a e tente de novo.");
            var drive = new DriveInfo(t.Path);
            var before = drive.AvailableFreeSpace;
            var marked = new List<string>();
            using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
            using (var caches = root.OpenSubKey(VolumeCaches, false))
            {
                foreach (var name in caches.GetSubKeyNames().Where(n => !DiskCleanupExcluded.Contains(n, StringComparer.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using (var k = root.OpenSubKey(VolumeCaches + "\\" + name, true))
                            k.SetValue(StateFlags, 2, Microsoft.Win32.RegistryValueKind.DWord);
                        marked.Add(name);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
                try { ActionRunner.WaitSystemTool("cleanmgr.exe", $"/sagerun:{SageRun}"); }
                finally
                {
                    // Nao deixa rastro: remove as marcacoes criadas para esta execucao.
                    foreach (var name in marked)
                    {
                        try { using (var k = root.OpenSubKey(VolumeCaches + "\\" + name, true)) k?.DeleteValue(StateFlags, false); }
                        catch (UnauthorizedAccessException) { }
                        catch (System.Security.SecurityException) { }
                    }
                }
            }
            drive = new DriveInfo(t.Path);
            return Math.Max(0, drive.AvailableFreeSpace - before);
        }

        string UserProfilePath()
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + _user.Sid))
                return k?.GetValue("ProfileImagePath") as string;
        }

        IEnumerable<FileInfo> CandidateFiles(CleanupTarget t)
        {
            if (t.OnDemand) yield break;
            var limit = t.IgnoreAge ? DateTime.MaxValue : DateTime.Now - MinAge;
            foreach (var root in t.Roots)
            {
                // Trava dos navegadores: so pastas com nome de cache conhecido.
                if (t.IsBrowser && !Browsers.IsAllowedCacheFolder(root)) continue;
                var dir = new DirectoryInfo(root);
                if (!dir.Exists) continue;
                foreach (var f in SafeEnumerate(dir, t.Pattern))
                {
                    if (f.LastWriteTime >= limit) continue;
                    if (t.IsBrowser && Browsers.ProtectedFiles.Contains(f.Name)) continue;
                    yield return f;
                }
            }
        }

        static IEnumerable<FileInfo> SafeEnumerate(DirectoryInfo dir, string pattern)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileInfo[] files; DirectoryInfo[] subs;
                try { files = d.GetFiles(pattern); subs = d.GetDirectories(); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var f in files) yield return f;
                foreach (var s in subs)
                    if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s); // nao segue junctions
            }
        }

        static bool IsRunning(CleanupTarget t)
        {
            if (t.Processes == null) return false;
            foreach (var name in t.Processes)
            {
                var procs = Process.GetProcessesByName(name);
                var any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public void Measure(CleanupTarget t)
        {
            var files = CandidateFiles(t).ToList();
            t.Files = files.Count;
            t.Bytes = files.Sum(f => f.Length);
            t.Running = IsRunning(t);
        }

        /// <summary>Retorna bytes liberados.</summary>
        public long Clean(CleanupTarget t)
        {
            // So o cache do resolvedor: nao libera/renova IP nem mexe no adaptador.
            if (t.IsDnsCache)
            {
                if (!Native.FlushDnsCache()) throw new InvalidOperationException("Não foi possível esvaziar o cache DNS.");
                return 0;
            }
            if (t.IsDiskCleanup) return RunDiskCleanup(t);
            // Ferramenta do proprio Windows, em prioridade baixa (sem /H) e em segundo plano.
            if (t.IsDiskOptimize)
            {
                if (IsRunning(new CleanupTarget { Processes = new[] { "Defrag" } }))
                    throw new InvalidOperationException("Já existe uma otimização de disco em andamento.");
                ActionRunner.StartSystemTool("defrag.exe", t.Path + " /O");
                return 0;
            }
            long freed = 0;
            foreach (var f in CandidateFiles(t).ToList())
            {
                try { var len = f.Length; f.Delete(); freed += len; }
                catch (IOException) { }                 // em uso pelo navegador ou pelo sistema
                catch (UnauthorizedAccessException) { }
            }
            return freed;
        }
    }
}
