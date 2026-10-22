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

        bool _selected;
        public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(); } }

        public bool InLevel(Level level) =>
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
                    return Target.Running ? text + " O navegador está aberto: feche-o para limpar tudo." : text;
                }
                return $"Apaga arquivos com mais de 1 dia em {Target.Path}. Não pode ser desfeito.";
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
