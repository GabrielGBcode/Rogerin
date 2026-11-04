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
    /// Pastas do sistema: so arquivos criados e alterados ha mais de <see cref="MinAge"/>, e as subpastas que ficam vazias.
    /// Navegadores: todo o cache (o navegador recria), com o navegador fechado antes.
    /// </summary>
    public class Cleanup
    {
        readonly UserContext _user;
        readonly SystemInfo _sys;
        /// <summary>Protege arquivos de instalacoes em andamento. Conta a data de criacao: arquivo extraido de zip guarda a data antiga de alteracao.</summary>
        public TimeSpan MinAge { get; set; } = TimeSpan.FromHours(2);

        public Cleanup(UserContext user, SystemInfo sys = null) { _user = user; _sys = sys; }

        public List<CleanupTarget> Targets()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var profile = UserProfilePath();
            var list = new List<CleanupTarget>
            {
                new CleanupTarget { Name = "Temporários do Windows", Path = System.IO.Path.Combine(win, "Temp") },
                new CleanupTarget { Name = "Temporários dos serviços do Windows", Path = System.IO.Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\Temp"),
                    Paths = new List<string>
                    {
                        System.IO.Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\Temp"),
                        System.IO.Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Temp"),
                        System.IO.Path.Combine(Environment.SystemDirectory, @"config\systemprofile\AppData\Local\Temp"),
                    } },
                // So os rastros *.pf: Layout.ini e a pasta ReadyBoot ficam. O Windows recria os .pf conforme os programas abrem.
                new CleanupTarget { Name = "Prefetch do Windows", Path = System.IO.Path.Combine(win, "Prefetch"), Pattern = "*.pf" },
                new CleanupTarget { Name = "Cache de download do Windows Update", Path = System.IO.Path.Combine(win, @"SoftwareDistribution\Download") },
                new CleanupTarget { Name = "Cache da Otimização de Entrega", Path = System.IO.Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") },
                new CleanupTarget { Name = "Relatórios de erro (WER)", Path = System.IO.Path.Combine(pd, @"Microsoft\Windows\WER") },
            };
            if (profile != null)
            {
                // Todos os perfis da maquina: o %TEMP% do tecnico que elevou com outra conta tambem entra.
                var temps = UserProfiles().Select(p => System.IO.Path.Combine(p, @"AppData\Local\Temp")).Where(Directory.Exists).ToList();
                var own = System.IO.Path.Combine(profile, @"AppData\Local\Temp");
                if (!temps.Contains(own, StringComparer.OrdinalIgnoreCase)) temps.Insert(0, own);
                list.Insert(0, new CleanupTarget { Name = temps.Count > 1 ? "Temporários dos usuários" : "Temporários do usuário", Path = own, Paths = temps });
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
                // O cleanmgr abre uma segunda janela (Pacotes de driver / Windows Update) que, em alguns PCs,
                // termina mas so fecha quando o mouse passa por cima. O "toque" periodico faz isso por nos.
                try { ActionRunner.WaitSystemTool("cleanmgr.exe", $"/sagerun:{SageRun}", NudgeDiskCleanup); }
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

        static void NudgeDiskCleanup()
        {
            var ids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("cleanmgr"))
                using (p) ids.Add(p.Id);
            if (ids.Count > 0) Native.NudgeWindows(ids);
        }

        /// <summary>Pastas de perfil de usuario (C:\Users\*) registradas no ProfileList.</summary>
        static IEnumerable<string> UserProfiles()
        {
            const string list = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(list))
            {
                if (k == null) yield break;
                foreach (var sid in k.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-") || n.StartsWith("S-1-12-1-")))
                    using (var p = k.OpenSubKey(sid))
                        if (p?.GetValue("ProfileImagePath") is string path) yield return Environment.ExpandEnvironmentVariables(path);
            }
        }

        string UserProfilePath()
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + _user.Sid))
                return k?.GetValue("ProfileImagePath") as string;
        }

        DateTime Limit(CleanupTarget t) => t.IgnoreAge ? DateTime.MaxValue : DateTime.Now - MinAge;

        IEnumerable<FileInfo> CandidateFiles(CleanupTarget t)
        {
            if (t.OnDemand) yield break;
            var limit = Limit(t);
            foreach (var root in t.Roots)
            {
                // Trava dos navegadores: so pastas com nome de cache conhecido.
                if (t.IsBrowser && !Browsers.IsAllowedCacheFolder(root)) continue;
                var dir = new DirectoryInfo(root);
                if (!dir.Exists) continue;
                foreach (var f in SafeEnumerate(dir, t.Pattern))
                {
                    if (f.LastWriteTime >= limit || f.CreationTime >= limit) continue;
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

        /// <summary>Processos do navegador na sessao do usuario logado no console (em servidor RDS, as outras sessoes ficam).</summary>
        static List<Process> BrowserProcesses(CleanupTarget t)
        {
            var session = (int)Native.WTSGetActiveConsoleSessionId();
            var mine = new List<Process>();
            foreach (var p in t.Processes.SelectMany(n => Process.GetProcessesByName(n)))
            {
                bool same;
                try { same = p.SessionId == session; }
                catch (InvalidOperationException) { same = false; } // saiu durante a busca
                if (same) mine.Add(p); else p.Dispose();
            }
            return mine;
        }

        /// <summary>
        /// Fecha o navegador antes de limpar: primeiro pede para as janelas fecharem (como clicar no X),
        /// depois de alguns segundos encerra o que sobrou (processos em segundo plano, Inicialização turbo do Edge).
        /// </summary>
        public static void CloseBrowser(CleanupTarget t)
        {
            if (t.Processes == null) return;
            var until = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < until)
            {
                var procs = BrowserProcesses(t);
                if (procs.Count == 0) return;
                var windows = 0;
                foreach (var p in procs)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) { p.CloseMainWindow(); windows++; } }
                    catch (InvalidOperationException) { }
                    p.Dispose();
                }
                if (windows == 0) break; // so processos em segundo plano: nao ha janela para esperar
                System.Threading.Thread.Sleep(500);
            }
            foreach (var p in BrowserProcesses(t))
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (InvalidOperationException) { }              // ja saiu
                catch (System.ComponentModel.Win32Exception) { }    // sem permissao ou encerrando
                p.Dispose();
            }
            System.Threading.Thread.Sleep(1000); // o Windows solta os arquivos de cache logo depois
        }

        /// <summary>Apaga as subpastas que ficaram vazias (a raiz fica). Nao segue junctions.</summary>
        static void RemoveEmptyDirs(DirectoryInfo dir, DateTime limit, bool isRoot = true)
        {
            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            foreach (var s in subs)
                if ((s.Attributes & FileAttributes.ReparsePoint) == 0) RemoveEmptyDirs(s, limit, false);
            if (isRoot) return;
            try
            {
                if (dir.CreationTime < limit && !dir.EnumerateFileSystemInfos().Any()) dir.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Measure(CleanupTarget t)
        {
            var files = CandidateFiles(t).ToList();
            t.Files = files.Count;
            t.Bytes = files.Sum(f => f.Length);
            t.Running = IsRunning(t);
        }

        /// <summary>Retorna bytes liberados. <paramref name="progress"/> recebe a fração concluída (0 a 1).</summary>
        public long Clean(CleanupTarget t, IProgress<double> progress = null)
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
            if (t.IsBrowser) CloseBrowser(t);
            long freed = 0;
            var files = CandidateFiles(t).ToList();
            int done = 0, reported = -1;
            foreach (var f in files)
            {
                try
                {
                    var len = f.Length;
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete(); freed += len;
                }
                catch (IOException) { }                 // em uso pelo navegador ou pelo sistema
                catch (UnauthorizedAccessException) { }
                var pct = ++done * 100 / files.Count;
                if (pct != reported) { reported = pct; progress?.Report(pct / 100.0); }
            }
            // Temporarios: as pastas que sobram vazias tambem saem (instaladores deixam muitas).
            if (!t.IsBrowser && t.Pattern == "*")
                foreach (var root in t.Roots.Where(Directory.Exists))
                    RemoveEmptyDirs(new DirectoryInfo(root), Limit(t));
            progress?.Report(1);
            return freed;
        }
    }
}
