using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Rogerin.Core;

namespace Rogerin
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum Level { Basic, Recommended, Complete }

    /// <summary>Uma linha clicavel na tela Ajustes: um ajuste do catalogo ou um item de limpeza.</summary>
    public abstract class OptionItem : Observable
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string Category { get; }
        public abstract string Risk { get; }
        public abstract bool CanSelect { get; }
        public abstract bool IsVisible { get; }
        public abstract string StatusLabel { get; }
        protected abstract string Tone { get; }
        public virtual string Details => null;
        public virtual bool GpoManaged => false;
        public virtual bool RebootRequired => false;
        /// <summary>Só o item de Liberar RAM mostra um botão de executar isolado, fora da seleção em lote.</summary>
        public virtual Visibility MemoryActionVisibility => Visibility.Collapsed;

        bool _selected;
        public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(); } }

        public virtual bool InLevel(Level level) =>
            level == Level.Complete || Risk == "safe" || (level == Level.Recommended && Risk == "moderate");

        static Brush B(string key) => (Brush)Application.Current.Resources[key];
        public Brush StatusBg => B(Tone + "Bg");
        public Brush StatusFg => B(Tone + "Fg");
        public string RiskLabel => Risk == "safe" ? "Seguro" : Risk == "moderate" ? "Moderado" : "Avançado";
        public Brush RiskFg => B(Risk == "safe" ? "OkFg" : Risk == "moderate" ? "WarnFg" : "BadFg");
        public Visibility GpoVisibility => GpoManaged ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RebootVisibility => RebootRequired ? Visibility.Visible : Visibility.Collapsed;
        public double RowOpacity => CanSelect ? 1.0 : 0.55;

        protected void RaiseState()
        {
            Raise(nameof(StatusLabel)); Raise(nameof(StatusBg)); Raise(nameof(StatusFg));
            Raise(nameof(CanSelect)); Raise(nameof(RowOpacity)); Raise(nameof(Details));
            if (!CanSelect) IsSelected = false;
        }
    }

    public class TweakItem : OptionItem
    {
        public Tweak Tweak { get; }
        public TweakItem(Tweak t) { Tweak = t; }

        public override string Name => Tweak.Name;
        public override string Description => Tweak.Description;
        public override string Category => Tweak.Category;
        public override string Risk => Tweak.Risk;
        public override bool GpoManaged => Tweak.GpoManaged;
        public override bool RebootRequired => Tweak.RebootRequired;

        TweakReport _report;
        public TweakReport Report { get => _report; set { _report = value; Raise(); RaiseState(); } }

        public override bool IsVisible => _report == null || _report.Status != TweakStatus.NotApplicable;
        public override bool CanSelect => _report != null && _report.Status != TweakStatus.NotApplicable && _report.Status != TweakStatus.Applied;

        public override string StatusLabel
        {
            get
            {
                switch (_report?.Status)
                {
                    case TweakStatus.Applied: return "Já aplicado";
                    case TweakStatus.NotApplied: return "Pendente";
                    case TweakStatus.Partial: return "Parcial";
                    case TweakStatus.NotApplicable: return "Não se aplica";
                    case TweakStatus.Error: return "Erro";
                    default: return "Analisando…";
                }
            }
        }

        protected override string Tone => _report?.Status switch
        {
            TweakStatus.Applied => "Ok",
            TweakStatus.NotApplied => "Warn",
            TweakStatus.Partial => "Info",
            TweakStatus.Error => "Bad",
            _ => "Na"
        };

        public override string Details => _report == null ? null : string.Join("\n", _report.Actions.Select(a =>
            $"{a.Action.Describe()}\n    atual: {(a.Error ?? a.State?.Current)}"));
    }

    public class CleanupItem : OptionItem
    {
        public const string CategoryName = "Limpeza";
        public CleanupTarget Target { get; }
        public CleanupItem(CleanupTarget t) { Target = t; }

        bool _measured;
        public void Measured() { _measured = true; RaiseState(); Raise(nameof(Description)); }

        public override string Name => Target.Name;
        public override string Description
        {
            get
            {
                if (Target.IsDnsCache) return "Esvazia o cache de nomes. O IP e a conexão continuam iguais.";
                if (Target.IsDiskCleanup) return "Abre a Limpeza de Disco do Windows como administrador com todas as opções marcadas, inclusive Limpeza do Windows Update e Lixeira, e espera terminar (pode levar vários minutos). Não apaga a pasta Downloads, as versões do Histórico de Arquivos, os arquivos usados para Restaurar o PC nem o Windows.old. Não pode ser desfeito.";
                if (Target.IsDiskOptimize) return $"Roda a ferramenta Otimizar Unidades do Windows em {Target.Path}, em segundo plano e com prioridade baixa. Pode levar horas e deixar o disco ocupado enquanto roda: prefira fora do horário de uso.";
                if (Target.IsBrowser)
                {
                    var profiles = Target.ProfileCount == 1 ? "1 perfil" : $"{Target.ProfileCount} perfis";
                    var text = $"Cache de páginas, imagens, scripts e GPU ({profiles}). Senhas, favoritos, histórico, cookies e extensões são preservados.";
                    return Target.Running ? text + " O navegador está aberto: o Rogerin vai fechá-lo antes de limpar." : text;
                }
                if (Target.Pattern == "*.pf")
                    return $"Apaga os rastros de inicialização de programas (*.pf) com mais de 2 horas em {Target.Path}. O Windows os recria: os programas podem abrir um pouco mais devagar na primeira vez. Não pode ser desfeito.";
                var more = Target.Roots.Count() > 1 ? $" e mais {Target.Roots.Count() - 1} pastas" : "";
                return $"Apaga arquivos com mais de 2 horas e as pastas que ficarem vazias em {Target.Path}{more}. Arquivos em uso ficam. Não pode ser desfeito.";
            }
        }
        public override string Category => Target.Group;
        public override string Risk => Target.Risk;
        public override bool IsVisible => true;
        public override bool CanSelect => Target.OnDemand || (_measured && Target.Files > 0);
        public override string StatusLabel => Target.OnDemand ? "Sob demanda"
            : !_measured ? "Medindo…" : Target.Files == 0 ? "Já limpo" : Target.SizeLabel;
        protected override string Tone => Target.OnDemand ? "Na" : !_measured ? "Na" : Target.Files == 0 ? "Ok" : "Warn";
        public override string Details => Target.IsDnsCache ? "DnsFlushResolverCache (dnsapi.dll)"
            : Target.IsDiskOptimize ? $@"{System.Environment.SystemDirectory}\defrag.exe {Target.Path} /O"
            : Target.IsDiskCleanup ? $@"{System.Environment.SystemDirectory}\cleanmgr.exe /sagerun" + "\nfora: " + string.Join(", ", Cleanup.DiskCleanupExcluded)
            : string.Join("\n", Target.Roots) + $"\n{Target.Files:N0} arquivos";
    }


    /// <summary>
    /// Liberar RAM, dentro da lista de Otimização: sempre a primeira opção, sempre marcada em qualquer
    /// nível e sempre selecionável (não é "aplicado uma vez e pronto" como os outros itens). O rótulo
    /// é uma recomendação do nível de uso atual da RAM, não um status de "aplicado/pendente".
    /// </summary>
    public class MemoryItem : OptionItem
    {
        public const string CategoryName = "Otimização";

        double _usedPercent;
        bool _measured;
        public void Measured(double totalGB, double freeGB)
        {
            _usedPercent = totalGB > 0 ? 100.0 * (totalGB - freeGB) / totalGB : 0;
            _freeGB = freeGB;
            _measured = true;
            RaiseState(); Raise(nameof(Description));
        }
        double _freeGB;

        public override string Name => "Liberar RAM";
        public override string Description => !_measured ? "Medindo o uso de memória…"
            : $"Esvazia as listas de memória em espera do Windows (standby, modificada e working sets). Não encerra nem altera nenhum processo. Uso atual: {_usedPercent:0}% ({_freeGB:0.#} GB livres).";
        public override string Category => CategoryName;
        public override string Risk => "safe";
        public override bool IsVisible => true;
        public override bool CanSelect => true;
        public override bool InLevel(Level level) => true; // sempre marcado nos 3 níveis

        public override string StatusLabel => !_measured ? "Medindo…" : _usedPercent <= 30 ? "Em dia" : _usedPercent <= 75 ? "Moderado" : "Recomendado";
        protected override string Tone => !_measured ? "Na" : _usedPercent <= 30 ? "Ok" : _usedPercent <= 75 ? "Info" : "Warn";
        public override Visibility MemoryActionVisibility => Visibility.Visible;
    }

    public class StartupRow : Observable
    {
        public StartupEntry Entry { get; }
        public StartupRow(StartupEntry e) { Entry = e; }
        public string Name => Entry.Name;
        public string Command => Entry.Command;
        public string Source => Entry.Source;
        public string StatusText => Entry.Enabled ? "Habilitado" : "Desabilitado";
        public string ToggleLabel => Entry.Enabled ? "Desabilitar" : "Habilitar";
        public void Refresh() { Raise(nameof(StatusText)); Raise(nameof(ToggleLabel)); }
    }

    /// <summary>Um resíduo encontrado (chave de registro ou pasta), com caixinha própria na confirmação.</summary>
    public class ResidueRow : Observable
    {
        public ResidueCandidate Candidate { get; }
        public ResidueRow(ResidueCandidate c) { Candidate = c; Checked = true; }
        public string Label => (Candidate.Kind == ResidueKind.Registry ? "Chave de registro: " : "Pasta: ") + Candidate.Path;

        bool _checked;
        public bool Checked { get => _checked; set { _checked = value; Raise(); } }
    }

    public class UninstallRow
    {
        public InstalledApp App { get; }
        public UninstallRow(InstalledApp a) { App = a; }
        public string Name => App.DisplayName;
        public string Publisher => App.Publisher;
        public string Version => App.Version;
        public string Size => App.SizeLabel;
    }

    public class BackupItem
    {
        public BackupFile Backup { get; }
        public BackupItem(BackupFile b) { Backup = b; }
        public string When => System.DateTime.TryParse(Backup.Created, out var d) ? d.ToString("dd/MM/yyyy HH:mm") : Backup.Created;
        public string Operator => Backup.Operator;
        public string TargetUser => Backup.TargetUser;
        public string Summary => $"{Backup.Entries.Count} alterações · " +
            string.Join(", ", Backup.Entries.Select(e => e.TweakName).Distinct().Take(3)) +
            (Backup.Entries.Select(e => e.TweakId).Distinct().Count() > 3 ? "…" : "");
    }
}
