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

    /// <summary>A acao mudou parte do que devia (entra no backup para poder desfazer) e explica o que faltou.</summary>
    public class PartialApplyException : Exception
    {
        public PartialApplyException(string message) : base(message) { }
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
                case "powerPlan": return ReadPowerPlan(a);
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
                case "powerPlan":
                    ApplyPowerPlan(a);
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
                case "powerPlan":
                    RevertPowerPlan(before);
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

        static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static readonly Guid StandbyIdle = new Guid("29f6ac1e-5ba1-4ba6-a5a2-5d2c5c0c9b5c");

        /// <summary>
        /// Copias com GUID fixo, criadas quando o plano original nao aparece (o Desempenho maximo vem oculto;
        /// o Alto desempenho some em alguns notebooks). GUID fixo: a deteccao e o desfazer reconhecem a copia.
        /// </summary>
        static readonly Dictionary<Guid, Guid> PowerPlanCopies = new Dictionary<Guid, Guid>
        {
            [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = new Guid("2b849f14-6f0c-491e-8546-f72df55d6d07"),
            [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = new Guid("c7ae6a49-1231-491f-8da1-f4de738bfa2b"),
        };

        static readonly Dictionary<Guid, string> PowerPlanNames = new Dictionary<Guid, string>
        {
            [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Desempenho máximo",
            [new Guid("2b849f14-6f0c-491e-8546-f72df55d6d07")] = "Desempenho máximo",
            [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "Alto desempenho",
            [new Guid("c7ae6a49-1231-491f-8da1-f4de738bfa2b")] = "Alto desempenho",
            [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Equilibrado",
            [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Economia de energia",
        };

        public static string PowerPlanName(string guid) =>
            System.Guid.TryParse(guid, out var g) && PowerPlanNames.TryGetValue(g, out var n) ? n : guid;

        /// <summary>Planos aceitos para a acao: os da lista e as copias deles.</summary>
        static IEnumerable<Guid> PowerPlanCandidates(TweakAction a)
        {
            foreach (var s in a.Guids)
            {
                var g = new Guid(s);
                yield return g;
                if (PowerPlanCopies.TryGetValue(g, out var copy)) yield return copy;
            }
        }

        static bool PowerPlanNeverSleeps(Guid plan)
        {
            var video = Native.ReadPowerValue(plan, SubVideo, VideoIdle);
            var sleep = Native.ReadPowerValue(plan, SubSleep, StandbyIdle);
            // Sem valor = a configuracao nao existe neste PC (ex.: sem estado de suspensao): nao ha o que desligar.
            bool never(uint? v) => v == null || v == 0;
            return never(video.ac) && never(video.dc) && never(sleep.ac) && never(sleep.dc);
        }

        static string Minutes(uint? seconds) => seconds == null ? "padrão" : seconds == 0 ? "nunca" : $"{seconds / 60} min";

        ActionState ReadPowerPlan(TweakAction a)
        {
            var active = Native.GetActivePowerScheme();
            var ok = PowerPlanCandidates(a).Contains(active) && (!a.NeverSleep || PowerPlanNeverSleeps(active));
            var video = Native.ReadPowerValue(active, SubVideo, VideoIdle);
            var sleep = Native.ReadPowerValue(active, SubSleep, StandbyIdle);
            var st = new ActionState
            {
                Status = ok ? ActionStatus.Applied : ActionStatus.NotApplied,
                Current = $"{PowerPlanName(active.ToString())} · vídeo {Minutes(video.ac)} · suspender {Minutes(sleep.ac)} (na tomada)"
            };
            st.Snapshot["guid"] = active.ToString();
            // Tempos de espera dos planos que podem ser alterados (os da lista e o ativo), para o desfazer devolver.
            var existing = Native.PowerSchemes();
            foreach (var g in PowerPlanCandidates(a).Where(existing.Contains).Concat(new[] { active }).Distinct())
            {
                var v = Native.ReadPowerValue(g, SubVideo, VideoIdle);
                var s = Native.ReadPowerValue(g, SubSleep, StandbyIdle);
                // Valor ausente fica vazio: o desfazer nao tem o que devolver e deixa como esta.
                st.Snapshot["timeouts:" + g] = $"{v.ac},{v.dc},{s.ac},{s.dc}";
            }
            return st;
        }

        /// <summary>
        /// Ativa o primeiro plano da lista que der certo (Desempenho maximo, senao Alto desempenho),
        /// duplicando o modelo oculto do Windows quando preciso, e zera os tempos de video e suspensao.
        /// Se nenhum ficar ativo (Modern Standby so aceita o Equilibrado), zera os tempos no plano atual
        /// e avisa com <see cref="PartialApplyException"/>.
        /// </summary>
        static void ApplyPowerPlan(TweakAction a)
        {
            var errors = new List<string>();
            foreach (var s in a.Guids)
            {
                var g = new Guid(s);
                try
                {
                    var existing = Native.PowerSchemes();
                    var plan = g;
                    if (!existing.Contains(g))
                    {
                        if (!PowerPlanCopies.TryGetValue(g, out plan)) throw new InvalidOperationException($"Plano {PowerPlanName(s)} não existe.");
                        if (!existing.Contains(plan)) Native.DuplicatePowerScheme(g, plan);
                    }
                    if (a.NeverSleep)
                    {
                        Native.WritePowerValue(plan, SubVideo, VideoIdle, 0, 0);
                        Native.WritePowerValue(plan, SubSleep, StandbyIdle, 0, 0);
                    }
                    Native.SetActivePowerScheme(plan);
                    // Com Modern Standby o Windows aceita a chamada e continua no Equilibrado: confere.
                    if (Native.GetActivePowerScheme() != plan)
                        throw new InvalidOperationException("o Windows não manteve o plano ativo (Modern Standby?).");
                    DeleteRootTask(PowerPlanTask); // ativado agora: a ativacao agendada nao e mais necessaria
                    return;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
                {
                    errors.Add($"{PowerPlanName(s)}: {ex.Message}");
                }
            }
            var current = Native.GetActivePowerScheme();
            if (a.NeverSleep)
            {
                Native.WritePowerValue(current, SubVideo, VideoIdle, 0, 0);
                Native.WritePowerValue(current, SubSleep, StandbyIdle, 0, 0);
                Native.SetActivePowerScheme(current);
            }
            var message = $"Desempenho máximo e Alto desempenho não ficaram ativos agora ({string.Join(" | ", errors)}). " +
                          (a.NeverSleep ? $"Desligar vídeo e Suspender ficaram em Nunca no plano {PowerPlanName(current.ToString())}. " : "");
            var forced = PolicyPowerPlan();
            if (forced != null)
                message += $"Uma política de grupo (GPO) força o plano {PowerPlanName(forced)} neste computador: o Windows volta para ele a cada reinício ou gpupdate. Peça ao TI para liberar a política \"Selecionar um plano de energia ativo\". ";

            // Modern Standby ja liberado (PlatformAoAcOverride=0), so falta reiniciar: o plano criado e selecionado no proximo boot.
            var created = Native.PowerSchemes();
            var pending = PowerPlanCandidates(a).FirstOrDefault(created.Contains);
            if (Native.ModernStandby() && ModernStandbyUnlockPending() && pending != Guid.Empty)
            {
                SchedulePowerPlanAtBoot(pending, a.NeverSleep);
                message += $"Agendado: depois de reiniciar, o Windows seleciona o plano {PowerPlanName(pending.ToString())} sozinho (tarefa \"{PowerPlanTask}\"). Reinicie o computador.";
            }
            else if (Native.ModernStandby())
                message += "Este PC usa Modern Standby: aplique \"Liberar os planos de energia\" e aplique este item de novo.";
            throw new PartialApplyException(message);
        }

        public const string PowerPlanTask = "Rogerin - Selecionar plano de energia";

        /// <summary>Plano imposto por GPO ("Selecionar um plano de energia ativo"), ou null se nao houver.</summary>
        static string PolicyPowerPlan()
        {
            using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings"))
                return k?.GetValue("ActivePowerScheme") as string;
        }

        /// <summary>
        /// Chamado ao abrir o Rogerin: se a tarefa de reinicio ainda existe, o plano nao foi confirmado.
        /// Tenta ativar agora (o reinicio ja liberou os planos) e apaga a tarefa quando o plano fica ativo.
        /// Devolve a mensagem para o log, ou null se nao havia nada pendente.
        /// </summary>
        public static string ActivatePendingPowerPlan()
        {
            dynamic task;
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                task = svc.GetFolder("\\").GetTask(PowerPlanTask);
            }
            catch (System.Runtime.InteropServices.COMException) { return null; } // nao existe
            catch (FileNotFoundException) { return null; }

            Guid plan = Guid.Empty;
            var timeouts = new List<string>();
            dynamic actions = task.Definition.Actions;
            for (int i = 1; i <= actions.Count; i++) // colecao COM comeca em 1
            {
                string args = actions.Item(i).Arguments;
                if (args.StartsWith("-setactive ")) Guid.TryParse(args.Substring("-setactive ".Length), out plan);
                else timeouts.Add(args);
            }
            if (plan == Guid.Empty || !Native.PowerSchemes().Contains(plan))
            {
                DeleteRootTask(PowerPlanTask);
                return null;
            }
            var name = PowerPlanName(plan.ToString());
            try
            {
                if (timeouts.Count > 0)
                {
                    Native.WritePowerValue(plan, SubVideo, VideoIdle, 0, 0);
                    Native.WritePowerValue(plan, SubSleep, StandbyIdle, 0, 0);
                }
                Native.SetActivePowerScheme(plan);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"[perf.power-plan] Plano {name} ainda não ficou ativo: {ex.Message}";
            }
            if (Native.GetActivePowerScheme() != plan)
            {
                var forced = PolicyPowerPlan();
                return forced != null
                    ? $"[perf.power-plan] Plano {name} não ficou ativo: uma política de grupo (GPO) força o plano {PowerPlanName(forced)}. Peça ao TI para liberar a política \"Selecionar um plano de energia ativo\"."
                    : $"[perf.power-plan] Plano {name} não ficou ativo: o Windows voltou para {PowerPlanName(Native.GetActivePowerScheme().ToString())}. Confira se \"Liberar os planos de energia\" está aplicado e reinicie de novo.";
            }
            DeleteRootTask(PowerPlanTask);
            return $"[perf.power-plan] OK Plano {name} ativado depois do reinício.";
        }

        static bool ModernStandbyUnlockPending()
        {
            using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                return k?.GetValue("PlatformAoAcOverride") is int v && v == 0;
        }

        /// <summary>
        /// Tarefa do Agendador que roda como SYSTEM ao ligar o PC: "powercfg -setactive" no plano e,
        /// por garantia, video e suspensao em Nunca. Vale por 7 dias e o Windows a apaga depois;
        /// o Rogerin tambem a apaga quando consegue ativar o plano ou no desfazer.
        /// </summary>
        static void SchedulePowerPlanAtBoot(Guid plan, bool neverSleep)
        {
            const int TASK_TRIGGER_BOOT = 8, TASK_TRIGGER_LOGON = 9, TASK_ACTION_EXEC = 0, TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_SERVICE_ACCOUNT = 5;
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            svc.Connect();
            dynamic def = svc.NewTask(0);
            def.RegistrationInfo.Description = "Criada pelo Rogerin: seleciona o plano de energia depois do reinício que libera os planos (Modern Standby). Apaga-se sozinha.";
            def.RegistrationInfo.Author = "Rogerin";
            def.Settings.Enabled = true;
            def.Settings.StartWhenAvailable = true;
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.ExecutionTimeLimit = "PT5M";
            def.Settings.DeleteExpiredTaskAfter = "PT0S";
            // Ao ligar e tambem a cada logon: se o servico de energia ainda nao aceitou a troca no boot
            // (ou outro processo voltou para o Equilibrado), o logon refaz. O Rogerin apaga a tarefa
            // assim que confirma o plano ativo.
            var end = DateTime.Now.AddDays(7).ToString("yyyy-MM-dd'T'HH:mm:ss");
            foreach (var (type, delay) in new[] { (TASK_TRIGGER_BOOT, "PT30S"), (TASK_TRIGGER_LOGON, "PT20S") })
            {
                dynamic trigger = def.Triggers.Create(type);
                trigger.Delay = delay;
                trigger.EndBoundary = end;
            }
            var powercfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
            var args = new List<string> { "-setactive " + plan };
            if (neverSleep)
                args.AddRange(new[] { "-change -monitor-timeout-ac 0", "-change -monitor-timeout-dc 0", "-change -standby-timeout-ac 0", "-change -standby-timeout-dc 0" });
            foreach (var arg in args)
            {
                dynamic action = def.Actions.Create(TASK_ACTION_EXEC);
                action.Path = powercfg;
                action.Arguments = arg;
            }
            svc.GetFolder("\\").RegisterTaskDefinition(PowerPlanTask, def, TASK_CREATE_OR_UPDATE, "SYSTEM", null, TASK_LOGON_SERVICE_ACCOUNT, null);
        }

        static void DeleteRootTask(string name)
        {
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                svc.GetFolder("\\").DeleteTask(name, 0);
            }
            catch (System.Runtime.InteropServices.COMException) { } // nao existe
            catch (FileNotFoundException) { }
        }

        static void RevertPowerPlan(Dictionary<string, object> before)
        {
            DeleteRootTask(PowerPlanTask);
            var existing = Native.PowerSchemes();
            foreach (var kv in before.Where(x => x.Key.StartsWith("timeouts:")))
            {
                var plan = new Guid(kv.Key.Substring("timeouts:".Length));
                if (!existing.Contains(plan)) continue;
                var v = ((string)kv.Value).Split(',').Select(x => x.Length == 0 ? (uint?)null : uint.Parse(x)).ToArray();
                Native.WritePowerValue(plan, SubVideo, VideoIdle, v[0], v[1]);
                Native.WritePowerValue(plan, SubSleep, StandbyIdle, v[2], v[3]);
            }
            Native.SetActivePowerScheme(new Guid((string)before["guid"]));
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

        /// <summary>
        /// Inicia e espera terminar, sem limite de tempo (a Limpeza de Disco pode levar dezenas de minutos). Retorna o código de saída.
        /// <paramref name="whileWaiting"/> roda a cada 2 segundos enquanto a ferramenta estiver aberta.
        /// </summary>
        public static int WaitSystemTool(string file, string args, Action whileWaiting = null)
        {
            var psi = SystemTool(file, args);
            psi.RedirectStandardOutput = psi.RedirectStandardError = false;
            using (var p = Process.Start(psi))
            {
                if (p == null) return -1;
                while (!p.WaitForExit(2000))
                {
                    try { whileWaiting?.Invoke(); }
                    catch (Exception) { } // so ajuda a ferramenta; nunca interrompe a espera
                }
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
