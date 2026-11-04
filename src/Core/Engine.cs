using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Web.Script.Serialization;

namespace Rogerin.Core
{
    public enum TweakStatus { Applied, NotApplied, Partial, NotApplicable, Error }

    public class ActionReport
    {
        public TweakAction Action { get; set; }
        public ActionState State { get; set; }
        public string Error { get; set; }
    }

    public class TweakReport
    {
        public Tweak Tweak { get; set; }
        public TweakStatus Status { get; set; }
        public List<ActionReport> Actions { get; set; } = new List<ActionReport>();
    }

    public class BackupEntry
    {
        public string TweakId { get; set; }
        public string TweakName { get; set; }
        public TweakAction Action { get; set; }
        public Dictionary<string, object> Before { get; set; }
    }

    public class BackupFile
    {
        public string Created { get; set; }
        public string Computer { get; set; }
        public string Operator { get; set; }
        public string TargetUser { get; set; }
        /// <summary>SID do perfil que recebeu os ajustes de HKCU. Backups antigos nao tem.</summary>
        public string TargetSid { get; set; }
        public List<BackupEntry> Entries { get; set; } = new List<BackupEntry>();

        public string FilePath { get; set; }
    }

    public class Engine
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rogerin");
        public static string BackupDir => Path.Combine(DataDir, "backups");

        public Catalog Catalog { get; }
        public SystemInfo System { get; }
        public UserContext User { get; }
        readonly ActionRunner _runner;
        readonly Log _log;

        public Engine(Catalog catalog, SystemInfo system, UserContext user, Log log)
        {
            Catalog = catalog; System = system; User = user; _log = log;
            _runner = new ActionRunner(user);
        }

        /// <summary>Somente leitura.</summary>
        public TweakReport Audit(Tweak t)
        {
            var r = new TweakReport { Tweak = t };
            if (!t.AppliesTo(System)) { r.Status = TweakStatus.NotApplicable; return r; }

            foreach (var a in t.Actions)
            {
                try { r.Actions.Add(new ActionReport { Action = a, State = _runner.Read(a) }); }
                catch (Exception ex) { r.Actions.Add(new ActionReport { Action = a, Error = ex.Message }); }
            }
            var relevant = r.Actions.Where(x => x.State == null || x.State.Status != ActionStatus.Missing).ToList();
            if (relevant.Any(x => x.Error != null)) r.Status = TweakStatus.Error;
            else if (relevant.All(x => x.State.Status == ActionStatus.Applied)) r.Status = TweakStatus.Applied;
            else if (relevant.Any(x => x.State.Status == ActionStatus.Applied)) r.Status = TweakStatus.Partial;
            else r.Status = TweakStatus.NotApplied;
            return r;
        }

        /// <summary>Lista o que mudaria, sem alterar nada.</summary>
        public IEnumerable<string> Preview(IEnumerable<Tweak> tweaks)
        {
            foreach (var t in tweaks.Where(x => x.AppliesTo(System)))
                foreach (var ar in Audit(t).Actions.Where(x => x.State?.Status == ActionStatus.NotApplied))
                    yield return $"{t.Name}: {ar.Action.Describe()} (atual: {ar.State.Current})";
        }

        public List<TweakReport> Apply(IList<Tweak> tweaks, bool restorePoint, IProgress<string> progress)
        {
            var applicable = tweaks.Where(t => t.AppliesTo(System)).ToList();
            if (restorePoint)
            {
                progress?.Report("Criando ponto de restauração…");
                var rp = CreateRestorePoint($"Rogerin {DateTime.Now:yyyy-MM-dd HH:mm}");
                _log.Write(rp == null ? "Ponto de restauração criado." : "Ponto de restauração NÃO criado: " + rp);
                if (rp != null) progress?.Report("Aviso: " + rp);
            }

            var backup = new BackupFile
            {
                Created = DateTime.Now.ToString("o"),
                Computer = System.ComputerName,
                Operator = Environment.UserDomainName + "\\" + Environment.UserName,
                TargetUser = User.Account,
                TargetSid = User.Sid
            };
            var file = Path.Combine(BackupDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var results = new List<TweakReport>();
            foreach (var t in applicable)
            {
                progress?.Report("Aplicando: " + t.Name);
                foreach (var a in t.Actions)
                {
                    ActionState before;
                    try { before = _runner.Read(a); }
                    catch (Exception ex) { _log.Write($"[{t.Id}] leitura falhou: {ex.Message}"); continue; }
                    if (before.Status != ActionStatus.NotApplied) continue;
                    string partial = null;
                    try { _runner.Apply(a); }
                    catch (PartialApplyException ex) { partial = ex.Message; }
                    catch (Exception ex) { _log.Write($"[{t.Id}] ERRO {a.Describe()}: {ex.Message}"); continue; }
                    backup.Entries.Add(new BackupEntry { TweakId = t.Id, TweakName = t.Name, Action = a, Before = before.Snapshot });
                    // Salvo a cada item: se o programa fechar no meio, o que ja mudou ainda pode ser desfeito.
                    // Se nao der para salvar, a excecao para a aplicacao: sem backup nao ha como desfazer.
                    SaveBackup(backup, file);
                    _log.Write(partial == null
                        ? $"[{t.Id}] OK {a.Describe()} (antes: {before.Current})"
                        : $"[{t.Id}] PARCIAL {a.Describe()} (antes: {before.Current}): {partial}");
                }
                results.Add(Audit(t));
            }
            if (backup.Entries.Count > 0)
            {
                _log.Write("Backup salvo: " + file);
                progress?.Report("Backup salvo: " + file);
            }
            return results;
        }

        /// <summary>Grava num .tmp e troca: uma queda no meio da gravacao nao corrompe o backup anterior.</summary>
        static void SaveBackup(BackupFile backup, string file)
        {
            Directory.CreateDirectory(BackupDir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(backup));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        public List<BackupFile> ListBackups()
        {
            if (!Directory.Exists(BackupDir)) return new List<BackupFile>();
            var json = new JavaScriptSerializer();
            return Directory.GetFiles(BackupDir, "backup-*.json")
                .OrderByDescending(f => f)
                .Select(f =>
                {
                    try { var b = json.Deserialize<BackupFile>(File.ReadAllText(f)); b.FilePath = f; return b; }
                    catch (Exception) { return null; }
                })
                .Where(b => b != null)
                .ToList();
        }

        public List<string> Undo(BackupFile backup, IProgress<string> progress)
        {
            var errors = new List<string>();
            // Os ajustes de HKCU voltam para o perfil que os recebeu, nao para quem esta logado agora.
            var target = backup.TargetSid == null
                ? (string.Equals(backup.TargetUser, User.Account, StringComparison.OrdinalIgnoreCase) ? User : null)
                : backup.TargetSid == User.Sid ? User : UserContext.Loaded(backup.TargetSid, backup.TargetUser);
            var userRunner = target == null ? null : target == User ? _runner : new ActionRunner(target);

            foreach (var e in Enumerable.Reverse(backup.Entries))
            {
                // Apps removidos nao voltam pelo desfazer (aviso na tela de Historico); nao contam como erro.
                if (e.Action.Type == "appx") { _log.Write($"[{e.TweakId}] {e.Action.Package} não volta pelo desfazer."); continue; }
                var userScoped = e.Action.Type == "registry" && e.Action.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase);
                if (userScoped && userRunner == null)
                {
                    errors.Add($"{e.TweakName}: ajuste de {backup.TargetUser}, que não está logado agora. Faça login com essa conta e desfaça de novo.");
                    continue;
                }
                progress?.Report("Revertendo: " + e.TweakName);
                try
                {
                    (userScoped ? userRunner : _runner).Revert(e.Action, e.Before);
                    _log.Write($"[{e.TweakId}] REVERTIDO {e.Action.Describe()}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{e.TweakName}: {ex.Message}");
                    _log.Write($"[{e.TweakId}] ERRO ao reverter: {ex.Message}");
                }
            }
            var done = backup.FilePath + ".restored";
            if (errors.Count == 0 && File.Exists(backup.FilePath)) File.Move(backup.FilePath, done);
            return errors;
        }

        /// <summary>Retorna null em caso de sucesso ou a mensagem de erro.</summary>
        static string CreateRestorePoint(string description)
        {
            const string frequency = "SystemRestorePointCreationFrequency";
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var sr = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", true))
                {
                    // Sem isto o Windows ignora o pedido se houver outro ponto nas ultimas 24 h, e ainda responde sucesso.
                    var old = sr.GetValue(frequency);
                    sr.SetValue(frequency, 0, RegistryValueKind.DWord);
                    try
                    {
                        using (var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null))
                        {
                            var args = cls.GetMethodParameters("CreateRestorePoint");
                            args["Description"] = description;
                            args["RestorePointType"] = 12; // MODIFY_SETTINGS
                            args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
                            var result = cls.InvokeMethod("CreateRestorePoint", args, null);
                            var rc = Convert.ToUInt32(result["ReturnValue"]);
                            return rc == 0 ? null : $"Proteção do Sistema desativada ou bloqueada por política (código {rc}).";
                        }
                    }
                    finally
                    {
                        if (old == null) sr.DeleteValue(frequency, false);
                        else sr.SetValue(frequency, old, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
