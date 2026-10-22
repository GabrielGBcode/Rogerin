using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Rogerin.Core
{
    public enum ActionStatus { Applied, NotApplied, Missing }

    public class ActionState
    {
        public ActionStatus Status { get; set; }
        public string Current { get; set; }
        /// <summary>Estado anterior gravado no backup para permitir desfazer.</summary>
        public Dictionary<string, object> Snapshot { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>Le, aplica e reverte uma acao do catalogo usando apenas APIs do Windows.</summary>
    public class ActionRunner
    {
        readonly UserContext _user;
        public ActionRunner(UserContext user) { _user = user; }

        public ActionState Read(TweakAction a)
        {
            switch (a.Type)
            {
                case "registry": return ReadRegistry(a);
                case "service": return ReadService(a);
                case "task": return ReadTask(a);
                case "appx": return ReadAppx(a);
                case "powerScheme": return ReadPower(a);
                case "command": return ReadCommand(a);
                default: throw new NotSupportedException("Tipo de ação desconhecido: " + a.Type);
            }
        }

        public void Apply(TweakAction a)
        {
            switch (a.Type)
            {
                case "registry":
                    WriteRegistry(a.Path, a.Name, a.Value, ParseKind(a.ValueType));
                    break;
                case "service":
                    Native.SetServiceStart(a.Name, StartCode(a.StartType), false);
                    if (a.StartType == "Disabled") StopService(a.Name);
                    break;
                case "task":
                    SetTaskEnabled(a.Path, a.Name, a.State != "Disabled");
                    break;
                case "appx":
                    RemoveAppx(a.Package);
                    break;
                case "powerScheme":
                    Native.SetActivePowerScheme(new Guid(a.Guid));
                    break;
                case "command":
                    RunSystemTool(a.File, a.ApplyArgs);
                    break;
            }
        }

        public void Revert(TweakAction a, Dictionary<string, object> before)
        {
            switch (a.Type)
            {
                case "registry":
                    if ((bool)before["exists"])
                        WriteRegistry(a.Path, a.Name, before["value"], (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), (string)before["kind"]));
                    else if (!string.IsNullOrEmpty(a.RemoveKeyOnRevert) && !(bool)before["keyExists"])
                        DeleteKey(a.RemoveKeyOnRevert);
                    else
                        DeleteValue(a.Path, a.Name);
                    break;
                case "service":
                    Native.SetServiceStart(a.Name, Convert.ToUInt32(before["start"]), (bool)before["delayed"]);
                    break;
                case "task":
                    SetTaskEnabled(a.Path, a.Name, (bool)before["enabled"]);
                    break;
                case "appx":
                    throw new InvalidOperationException($"{a.Package}: reinstale pela Microsoft Store ou pelo Portal da Empresa.");
                case "powerScheme":
                    Native.SetActivePowerScheme(new Guid((string)before["guid"]));
                    break;
                case "command":
                    RunSystemTool(a.File, a.RevertArgs);
                    break;
            }
        }

        #region Registro

        RegistryKey OpenKey(string path, bool write)
        {
            var i = path.IndexOf('\\');
            var hive = path.Substring(0, i).ToUpperInvariant();
            var sub = path.Substring(i + 1);
            RegistryKey root;
            switch (hive)
            {
                case "HKLM": root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); break;
                case "HKCU": root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64); sub = _user.Sid + "\\" + sub; break;
                default: throw new NotSupportedException("Hive não suportada: " + hive);
            }
            using (root)
                return write ? root.CreateSubKey(sub, true) : root.OpenSubKey(sub, false);
        }

        static string ValueName(string name) => name == "(Default)" ? "" : name;

        ActionState ReadRegistry(TweakAction a)
        {
            using (var key = OpenKey(a.Path, false))
            {
                var name = ValueName(a.Name);
                var exists = key != null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
                var value = exists ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
                var st = new ActionState
                {
                    Status = exists && Same(value, a.Value) ? ActionStatus.Applied : ActionStatus.NotApplied,
                    Current = exists ? Convert.ToString(value) : "(ausente)"
                };
                st.Snapshot["keyExists"] = key != null;
                st.Snapshot["exists"] = exists;
                st.Snapshot["value"] = value is byte[] b ? Convert.ToBase64String(b) : value;
                st.Snapshot["kind"] = exists ? key.GetValueKind(name).ToString() : null;
                return st;
            }
        }

        static bool Same(object current, object desired)
        {
            if (current == null || desired == null) return current == desired;
            // REG_DWORD volta como int com sinal: 0xFFFFFFFF chega como -1.
            if (current is int i) return long.TryParse(Convert.ToString(desired), out var d) && (uint)i == unchecked((uint)d);
            if (current is long l) return long.TryParse(Convert.ToString(desired), out var q) && l == q;
            return string.Equals(Convert.ToString(current), Convert.ToString(desired), StringComparison.Ordinal);
        }

        void WriteRegistry(string path, string name, object value, RegistryValueKind kind)
        {
            using (var key = OpenKey(path, true))
            {
                object v = value;
                if (kind == RegistryValueKind.DWord) v = unchecked((int)Convert.ToInt64(value));
                else if (kind == RegistryValueKind.QWord) v = Convert.ToInt64(value);
                else if (kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString) v = Convert.ToString(value);
                key.SetValue(ValueName(name), v, kind);
            }
        }

        void DeleteValue(string path, string name)
        {
            using (var key = OpenKey(path, true)) key.DeleteValue(ValueName(name), false);
        }

        void DeleteKey(string path)
        {
            var i = path.LastIndexOf('\\');
            using (var parent = OpenKey(path.Substring(0, i), true))
                parent.DeleteSubKeyTree(path.Substring(i + 1), false);
        }

        static RegistryValueKind ParseKind(string t) => (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), t, true);

        #endregion

        #region Servicos

        static uint StartCode(string startType)
        {
            switch (startType)
            {
                case "Automatic": return 2;
                case "Manual": return 3;
                case "Disabled": return 4;
                default: throw new ArgumentException("Tipo de inicialização inválido: " + startType);
            }
        }

        ActionState ReadService(TweakAction a)
        {
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + a.Name))
            {
                if (key?.GetValue("Start") == null)
                    return new ActionState { Status = ActionStatus.Missing, Current = "(serviço não existe)" };
                var start = Convert.ToUInt32(key.GetValue("Start"));
                var delayed = Convert.ToInt32(key.GetValue("DelayedAutostart", 0)) == 1;
                var label = start == 2 ? (delayed ? "Automático (atraso)" : "Automático") : start == 3 ? "Manual" : start == 4 ? "Desativado" : "Sistema";
                var st = new ActionState { Status = start == StartCode(a.StartType) ? ActionStatus.Applied : ActionStatus.NotApplied, Current = label };
                st.Snapshot["start"] = start;
                st.Snapshot["delayed"] = delayed;
                return st;
            }
        }

        static void StopService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) return;
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
            catch (InvalidOperationException) { /* parar e opcional; o servico fica desativado no proximo boot */ }
            catch (System.ServiceProcess.TimeoutException) { }
        }

        #endregion

        #region Tarefas agendadas (COM do Agendador de Tarefas)

        static dynamic GetTask(string folder, string name)
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            svc.Connect();
            try { return svc.GetFolder(folder).GetTask(name); }
            catch (System.Runtime.InteropServices.COMException) { return null; }
            catch (FileNotFoundException) { return null; }
        }

        ActionState ReadTask(TweakAction a)
        {
            var task = GetTask(a.Path, a.Name);
            if (task == null) return new ActionState { Status = ActionStatus.Missing, Current = "(tarefa não existe)" };
            bool enabled = task.Enabled;
            var st = new ActionState
            {
                Status = enabled == (a.State != "Disabled") ? ActionStatus.Applied : ActionStatus.NotApplied,
                Current = enabled ? "Habilitada" : "Desabilitada"
            };
            st.Snapshot["enabled"] = enabled;
            return st;
        }

        static void SetTaskEnabled(string folder, string name, bool enabled)
        {
            var task = GetTask(folder, name) ?? throw new InvalidOperationException($"Tarefa {folder}\\{name} não encontrada.");
            task.Enabled = enabled;
        }

        #endregion

        #region Apps (WinRT PackageManager)

        ActionState ReadAppx(TweakAction a)
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(_user.Sid).FirstOrDefault(p => p.Id.Name.Equals(a.Package, StringComparison.OrdinalIgnoreCase));
            var v = pkg?.Id.Version;
            return new ActionState
            {
                Status = pkg == null ? ActionStatus.Applied : ActionStatus.NotApplied,
                Current = pkg == null ? "(não instalado)" : $"instalado {v?.Major}.{v?.Minor}.{v?.Build}"
            };
        }

        static void RemoveAppx(string name)
        {
            var pm = new PackageManager();
            var all = pm.FindPackages().Where(p => p.Id.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var p in all)
                Wait(pm.RemovePackageAsync(p.Id.FullName, RemovalOptions.RemoveForAllUsers), p.Id.FullName);
            // Impede que o app volte em novos perfis de usuario.
            foreach (var family in all.Select(p => p.Id.FamilyName).Distinct())
            {
                try { Wait(pm.DeprovisionPackageForAllUsersAsync(family), family); }
                catch (Exception) { /* nem todo app e provisionado */ }
            }
        }

        static void Wait(Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op, string what)
        {
            using (var done = new ManualResetEventSlim())
            {
                op.Completed = (o, s) => done.Set();
                if (op.Status == Windows.Foundation.AsyncStatus.Started) done.Wait(TimeSpan.FromMinutes(5));
                if (op.Status == Windows.Foundation.AsyncStatus.Error)
                    throw new InvalidOperationException($"{what}: {op.GetResults()?.ErrorText ?? op.ErrorCode.Message}");
            }
        }

        #endregion

        #region Energia e ferramentas do sistema

        ActionState ReadPower(TweakAction a)
        {
            var active = Native.GetActivePowerScheme();
            var st = new ActionState
            {
                Status = active == new Guid(a.Guid) ? ActionStatus.Applied : ActionStatus.NotApplied,
                Current = active.ToString()
            };
            st.Snapshot["guid"] = active.ToString();
            return st;
        }

        ActionState ReadCommand(TweakAction a)
        {
            var probe = new TweakAction { Type = "registry", Path = a.Detect.Path, Name = a.Detect.Name, Value = a.Detect.Value };
            var st = ReadRegistry(probe);
            st.Snapshot.Clear();
            return st;
        }

        /// <summary>
        /// Unicos executaveis externos permitidos. Qualquer outro (ipconfig, netsh, powershell, cmd...)
        /// e recusado, mesmo que alguem edite o catalogo: o programa nunca libera/renova IP nem reseta a pilha de rede.
        /// </summary>
        public static readonly string[] AllowedTools = { "powercfg.exe", "defrag.exe", "cleanmgr.exe" };

        /// <summary>Somente binarios de System32 pelo caminho completo (evita sequestro de PATH).</summary>
        static ProcessStartInfo SystemTool(string file, string args)
        {
            if (!AllowedTools.Contains(file, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"'{file}' não está na lista de ferramentas permitidas.");
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
        }

        /// <summary>Inicia e nao espera: para tarefas longas como a desfragmentacao, que continuam se o programa fechar.</summary>
        public static void StartSystemTool(string file, string args)
        {
            var psi = SystemTool(file, args);
            psi.RedirectStandardOutput = psi.RedirectStandardError = false;
            Process.Start(psi)?.Dispose();
        }

        /// <summary>Inicia e espera terminar, sem limite de tempo (a Limpeza de Disco pode levar dezenas de minutos). Retorna o código de saída.</summary>
        public static int WaitSystemTool(string file, string args)
        {
            var psi = SystemTool(file, args);
            psi.RedirectStandardOutput = psi.RedirectStandardError = false;
            using (var p = Process.Start(psi))
            {
                if (p == null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static void RunSystemTool(string file, string args)
        {
            using (var p = Process.Start(SystemTool(file, args)))
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException($"{file} saiu com código {p.ExitCode}: {output.Trim()}");
            }
        }

        #endregion
    }
}
