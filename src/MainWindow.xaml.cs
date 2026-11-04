using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Rogerin.Core;

namespace Rogerin
{
    public partial class MainWindow : Window
    {
        readonly Log _log = new Log();
        Engine _engine;
        Cleanup _cleanup;
        readonly ObservableCollection<OptionItem> _items = new ObservableCollection<OptionItem>();
        ICollectionView _view;
        Func<Task> _pendingConfirm;
        bool _bulk; // evita que a selecao em massa desmarque o cartao de nivel

        IEnumerable<TweakItem> Tweaks => _items.OfType<TweakItem>();
        IEnumerable<CleanupItem> Cleanups => _items.OfType<CleanupItem>();

        public MainWindow()
        {
            InitializeComponent();
            _log.Written += (line, message) => Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
                StatusLine.Text = message;
            }));
            SourceInitialized += (s, e) => Native.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Loaded += async (s, e) => await InitAsync();
        }

        async Task InitAsync()
        {
            await Busy("Analisando o sistema…", async () =>
            {
                var catalog = Catalog.Load();
                var (sys, user) = await Task.Run(() => (SystemInfo.Collect(), UserContext.Resolve()));
                _engine = new Engine(catalog, sys, user, _log);
                _cleanup = new Cleanup(user, sys);
            });
            if (_engine == null) return;


            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"Versão {v.Major}.{v.Minor}.{v.Build}\n{_engine.Catalog.Tweaks.Count} ajustes no catálogo\n© 2026 Gabriel Gomes Batista";
            _log.Write($"Sessão iniciada. Operador {Environment.UserDomainName}\\{Environment.UserName}; ajustes de usuário vão para {_engine.User.Account}.");

            var sys2 = _engine.System;
            OsText.Text = sys2.OsLabel;
            MachineText.Text = $"{sys2.ComputerName} · {sys2.DomainLabel}";
            UserText.Text = _engine.User.Account;

            foreach (var t in _engine.Catalog.Tweaks) Add(new TweakItem(t));
            foreach (var c in _cleanup.Targets()) Add(new CleanupItem(c));
            _view = CollectionViewSource.GetDefaultView(_items);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptionItem.Category)));
            _view.Filter = o => ((OptionItem)o).IsVisible;
            OptionList.ItemsSource = _view;

            HistoryIntro.Text = $"Cada aplicação gera um backup em {Engine.BackupDir}. Desfazer devolve os valores exatos que existiam antes. " +
                                "Apps removidos e arquivos limpos não voltam pelo desfazer.";
            RenderBanners();
            // Plano de energia agendado para depois do reinicio (Modern Standby): confirma ou ativa agora.
            var pending = await Task.Run(() =>
            {
                try { return ActionRunner.ActivatePendingPowerPlan(); }
                catch (Exception ex) { return "[perf.power-plan] Ativação pendente falhou: " + ex.Message; }
            });
            if (pending != null) _log.Write(pending);
            await RefreshAsync();
            LoadHistory();

            LevelBasic.IsChecked = true; // ponto de partida seguro
        }

        void Add(OptionItem item)
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(OptionItem.IsSelected)) return;
                if (!_bulk) ClearLevel();
                UpdateSelection();
            };
            _items.Add(item);
        }

        /// <summary>Le o estado de todos os ajustes e mede a limpeza. Somente leitura.</summary>
        async Task RefreshAsync()
        {
            await Busy("Analisando ajustes…", async () =>
            {
                var reports = await Task.Run(() => Tweaks.Select(i => _engine.Audit(i.Tweak)).ToList());
                var list = Tweaks.ToList();
                for (int i = 0; i < reports.Count; i++) list[i].Report = reports[i];
                _view.Refresh();

                BusyText.Text = "Medindo arquivos temporários…";
                await Task.Run(() => { foreach (var c in Cleanups) _cleanup.Measure(c.Target); });
                foreach (var c in Cleanups) c.Measured();
            });
            UpdateLevelCounts();
            UpdateSelection();
            RenderComputer();
        }

        #region Selecao

        Level? CheckedLevel =>
            LevelBasic.IsChecked == true ? Level.Basic :
            LevelRecommended.IsChecked == true ? Level.Recommended :
            LevelComplete.IsChecked == true ? Level.Complete : (Level?)null;

        void Level_Checked(object sender, RoutedEventArgs e)
        {
            if (_bulk || _engine == null) return;
            var level = (Level)Enum.Parse(typeof(Level), (string)((FrameworkElement)sender).Tag);
            SetSelection(i => i.InLevel(level));
        }

        void SelectAll_Click(object sender, RoutedEventArgs e) { ClearLevel(); SetSelection(i => true); }
        void SelectNone_Click(object sender, RoutedEventArgs e) { ClearLevel(); SetSelection(i => false); }

        void SelectGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = (string)((FrameworkElement)sender).Tag;
            var items = _items.Where(i => i.Category == group && i.CanSelect).ToList();
            var select = !items.All(i => i.IsSelected);
            ClearLevel();
            _bulk = true;
            foreach (var i in items) i.IsSelected = select;
            _bulk = false;
            UpdateSelection();
        }

        void SetSelection(Func<OptionItem, bool> rule)
        {
            _bulk = true;
            foreach (var i in _items) i.IsSelected = i.CanSelect && rule(i);
            _bulk = false;
            UpdateSelection();
        }

        void ClearLevel()
        {
            _bulk = true;
            LevelBasic.IsChecked = LevelRecommended.IsChecked = LevelComplete.IsChecked = false;
            _bulk = false;
        }

        void UpdateLevelCounts()
        {
            string Count(Level l)
            {
                var n = _items.Count(i => i.CanSelect && i.InLevel(l));
                return n == 0 ? "Nada pendente" : n == 1 ? "1 item pendente" : $"{n} itens pendentes";
            }
            CountBasic.Text = Count(Level.Basic);
            CountRecommended.Text = Count(Level.Recommended);
            CountComplete.Text = Count(Level.Complete);
        }

        void UpdateSelection()
        {
            var t = Tweaks.Count(i => i.IsSelected);
            var c = Cleanups.Count(i => i.IsSelected);
            var parts = new List<string>();
            if (t > 0) parts.Add(t == 1 ? "1 ajuste" : $"{t} ajustes");
            if (c > 0) parts.Add(c == 1 ? "1 limpeza" : $"{c} limpezas");
            SelectionText.Text = parts.Count == 0 ? "Nada selecionado" : string.Join(" e ", parts) + (t + c == 1 ? " selecionado" : " selecionados");

            var bytes = Cleanups.Where(i => i.IsSelected).Sum(i => i.Target.Bytes);
            var reboot = Tweaks.Any(i => i.IsSelected && i.RebootRequired);
            var hints = new List<string>();
            if (bytes > 0) hints.Add("libera cerca de " + new CleanupTarget { Bytes = bytes }.SizeLabel);
            if (reboot) hints.Add("algum item requer reinício");
            if (hints.Count > 0) StatusLine.Text = char.ToUpper(hints[0][0]) + string.Join(" · ", hints).Substring(1);
            ApplyButton.IsEnabled = t + c > 0;
        }

        #endregion

        #region Aplicar

        async void Review_Click(object sender, RoutedEventArgs e)
        {
            var tweaks = Tweaks.Where(i => i.IsSelected).Select(i => i.Tweak).ToList();
            var cleans = Cleanups.Where(i => i.IsSelected).ToList();
            if (tweaks.Count + cleans.Count == 0) return;

            var lines = new List<string>();
            await Busy("Simulando…", async () => lines = await Task.Run(() => _engine.Preview(tweaks).ToList()));
            foreach (var c in cleans)
                lines.Add(c.Target.IsDnsCache ? "Limpeza: esvaziar cache DNS (o IP e a conexão não mudam)"
                    : c.Target.IsDiskCleanup ? $"Limpeza: rodar a Limpeza de Disco do Windows em {c.Target.Path} com todas as opções (inclui Limpeza do Windows Update e esvaziar a Lixeira; pasta Downloads fica)."
                    : c.Target.IsDiskOptimize ? $"Disco: iniciar a desfragmentação de {c.Target.Path} em segundo plano (defrag.exe /O). O disco fica ocupado até terminar."
                    : c.Target.IsBrowser ? $"Navegador: fechar o {c.Target.BrowserName} e apagar o cache ({c.Target.Files:N0} arquivos, {c.Target.SizeLabel}). Senhas, favoritos, histórico e cookies ficam."
                        + (c.Target.Running ? " ATENÇÃO: o navegador está aberto e será fechado. Peça ao usuário para salvar o que estiver preenchendo." : "")
                    : c.Target.Pattern != "*" ? $"Limpeza: apagar {c.Target.Files:N0} arquivos {c.Target.Pattern} ({c.Target.SizeLabel}) em {c.Target.Path}"
                    : $"Limpeza: apagar {c.Target.Files:N0} arquivos antigos ({c.Target.SizeLabel}) e as pastas vazias em {c.Target.Path}"
                        + (c.Target.Roots.Count() > 1 ? $" e mais {c.Target.Roots.Count() - 1} pastas" : ""));
            if (lines.Count == 0) { _log.Write("Os itens marcados já estão aplicados."); return; }

            var restore = RestorePointBox.IsChecked == true && tweaks.Count > 0;
            var intro = $"{lines.Count} alterações. Antes de cada ajuste o valor atual é salvo para desfazer" +
                        (restore ? ", e um ponto de restauração é criado." : ".") +
                        (cleans.Any(c => !c.Target.OnDemand || c.Target.IsDiskCleanup) ? " Arquivos apagados não voltam." : "");
            ShowConfirm("Confirmar otimização", intro, lines, "Aplicar agora", () => ApplyAsync(tweaks, cleans, restore));
        }

        async Task ApplyAsync(List<Tweak> tweaks, List<CleanupItem> cleans, bool restore)
        {
            await Busy("Aplicando…", async () =>
            {
                var progress = new Progress<string>(m => BusyText.Text = m);
                if (tweaks.Count > 0)
                {
                    var results = await Task.Run(() => _engine.Apply(tweaks, restore, progress));
                    var reboot = results.Any(r => r.Tweak.RebootRequired && r.Status == TweakStatus.Applied);
                    _log.Write($"Ajustes: {results.Count(r => r.Status == TweakStatus.Applied)} de {results.Count} aplicados." +
                               (reboot ? " Reinicie o computador para concluir." : " Alguns itens valem após sair e entrar de novo na conta."));
                }
                long freed = 0;
                // Barra geral da limpeza: cada item vale uma parte igual; itens com arquivos avançam arquivo a arquivo.
                CleanupBar.Value = 0;
                CleanupPercent.Text = "0%";
                CleanupOverlay.Visibility = cleans.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                for (int i = 0; i < cleans.Count; i++)
                {
                    var c = cleans[i];
                    var step = i;
                    var label = c.Target.IsDiskCleanup ? "Limpeza de Disco do Windows em andamento (pode levar vários minutos)"
                        : c.Target.IsBrowser && c.Target.Running ? $"Fechando o {c.Target.BrowserName} e limpando"
                        : "Limpando: " + c.Name;
                    void Show(double fraction)
                    {
                        var total = (step + fraction) / cleans.Count;
                        CleanupBar.Value = total;
                        CleanupPercent.Text = $"{(int)(total * 100)}%";
                        CleanupStep.Text = $"Item {step + 1} de {cleans.Count}";
                        CleanupItemText.Text = label + "…";
                        BusyText.Text = $"{label}… {(int)(total * 100)}%";
                    }
                    Show(0);
                    var itemProgress = new Progress<double>(Show);
                    try
                    {
                        var f = await Task.Run(() => _cleanup.Clean(c.Target, itemProgress));
                        freed += f;
                        _log.Write(c.Target.IsDnsCache ? "Cache DNS esvaziado (IP e conexão não foram alterados)."
                            : c.Target.IsDiskCleanup ? $"Limpeza de Disco do Windows concluída: {new CleanupTarget { Bytes = f }.SizeLabel} liberados em {c.Target.Path}."
                            : c.Target.IsDiskOptimize ? $"Desfragmentação de {c.Target.Path} iniciada em segundo plano. Acompanhe em Otimizar Unidades (dfrgui)."
                            : $"Limpeza: {c.Name} liberou {new CleanupTarget { Bytes = f }.SizeLabel}.");
                    }
                    catch (Exception ex) { _log.Write($"Limpeza: {c.Name}: {ex.Message}"); }
                    Show(1);
                }
                CleanupOverlay.Visibility = Visibility.Collapsed;
                if (cleans.Any(c => !c.Target.OnDemand || c.Target.IsDiskCleanup)) _log.Write($"Limpeza concluída: {new CleanupTarget { Bytes = freed }.SizeLabel} liberados.");
            });
            await RefreshAsync();
            LoadHistory();
            ClearLevel();
            SetSelection(i => false);
        }

        #endregion

        #region Computador

        void RenderBanners()
        {
            Banners.Children.Clear();
            var sys = _engine.System;
            if (_engine.User.IsDifferentFromElevatedUser)
                AddBanner("InfoBg", "InfoFg", $"Você elevou com outra conta. Ajustes de usuário serão gravados no perfil de {_engine.User.Account}, que está logado nesta máquina.");
            if (sys.PartOfDomain)
                AddBanner("InfoBg", "InfoFg", $"Computador no domínio {sys.Domain}. Itens marcados \"GPO pode sobrescrever\" podem ser revertidos por política no próximo gpupdate.");
            if (sys.DiskTotalGB > 0 && sys.DiskFreeGB / sys.DiskTotalGB < 0.1)
                AddBanner("WarnBg", "WarnFg", "Menos de 10% livre no disco do sistema. Marque os itens de Limpeza.");
            if (sys.DiskUnhealthy)
                AddBanner("BadBg", "BadFg", $"O Windows informa o disco do sistema como \"{sys.DiskHealth}\" (SMART). Disco em 100% pode ser defeito físico: faça backup e troque o disco antes de otimizar.");
            else if (sys.IsHdd)
                AddBanner("InfoBg", "InfoFg", "O disco do sistema é um HDD. A categoria Disco traz ajustes para reduzir o uso em 100%; a solução definitiva é trocar por um SSD.");
        }

        void AddBanner(string bg, string fg, string text)
        {
            Banners.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource(bg),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource(fg), FontSize = 13 }
            });
        }

        void RenderComputer()
        {
            var sys = _engine.System;
            var applicable = Tweaks.Where(t => t.Report.Status != TweakStatus.NotApplicable).ToList();
            var applied = applicable.Count(t => t.Report.Status == TweakStatus.Applied);
            ScoreText.Text = $"{applied} de {applicable.Count}";
            ScoreSub.Text = $"{applicable.Count - applied} pendentes";

            RamText.Text = $"{sys.RamGB:0.#} GB";
            CpuText.Text = $"{sys.Cores} núcleos · {sys.Threads} threads";
            DiskText.Text = $"{sys.DiskFreeGB:0} GB";
            DiskBar.Value = sys.DiskTotalGB > 0 ? 100 * (sys.DiskTotalGB - sys.DiskFreeGB) / sys.DiskTotalGB : 0;
            DiskSub.Text = $"de {sys.DiskTotalGB:0} GB em {sys.SystemDrive}" + (sys.DiskMedia != null ? $" · {sys.DiskMedia}" : "");
            UptimeText.Text = sys.UptimeHours >= 48 ? $"{sys.UptimeHours / 24:0} dias" : $"{sys.UptimeHours:0} h";
            UptimeSub.Text = sys.UptimeHours >= 24 * 7 ? "Reiniciar ajuda a concluir atualizações pendentes." : "Sem necessidade de reinício.";

            SpecList.ItemsSource = new[]
            {
                new KeyValuePair<string, string>("Sistema operacional", sys.Caption),
                new KeyValuePair<string, string>("Versão", $"{sys.DisplayVersion} · build {sys.Build}.{sys.Ubr} · {sys.Edition}"),
                new KeyValuePair<string, string>("Nome do computador", sys.ComputerName),
                new KeyValuePair<string, string>("Domínio", sys.DomainLabel),
                new KeyValuePair<string, string>("Usuário logado", _engine.User.Account),
                new KeyValuePair<string, string>("Fabricante e modelo", $"{sys.Manufacturer} {sys.Model}".Trim()),
                new KeyValuePair<string, string>("Número de série", sys.SerialNumber),
                new KeyValuePair<string, string>("Processador", sys.Cpu),
                new KeyValuePair<string, string>("Placa de vídeo", sys.Gpu),
                new KeyValuePair<string, string>("Memória RAM", $"{sys.RamGB:0.#} GB"),
                new KeyValuePair<string, string>("Disco do sistema", $"{sys.DiskFreeGB:0} GB livres de {sys.DiskTotalGB:0} GB"),
                new KeyValuePair<string, string>("Tipo de disco", string.Join(" · ", new[] { sys.DiskMedia ?? "Não identificado", sys.DiskModel }.Where(x => !string.IsNullOrEmpty(x)))),
                new KeyValuePair<string, string>("Saúde do disco (SMART)", sys.DiskHealth),
            }.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();

            CategorySummary.ItemsSource = applicable.GroupBy(t => t.Category).Select(g => new
            {
                Name = g.Key,
                Text = $"{g.Count(x => x.Report.Status == TweakStatus.Applied)} de {g.Count()} aplicados",
                Ratio = g.Count(x => x.Report.Status == TweakStatus.Applied) / (double)g.Count()
            }).ToList();
        }

        #endregion


        #region Historico

        void LoadHistory()
        {
            HistoryList.ItemsSource = _engine.ListBackups().Select(b => new BackupItem(b)).ToList();
        }

        void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!(HistoryList.SelectedItem is BackupItem item)) { _log.Write("Selecione uma aplicação na lista para desfazer."); return; }
            var lines = item.Backup.Entries.Select(x => $"{x.TweakName}: {x.Action.Describe()}").ToList();
            ShowConfirm("Desfazer aplicação", $"Os valores de antes de {item.When} serão devolvidos.", lines, "Desfazer",
                async () =>
                {
                    List<string> errors = null;
                    await Busy("Desfazendo…", async () =>
                    {
                        var progress = new Progress<string>(m => BusyText.Text = m);
                        errors = await Task.Run(() => _engine.Undo(item.Backup, progress));
                    });
                    _log.Write(errors == null ? "Desfazer interrompido." :
                        errors.Count == 0 ? "Desfazer concluído." : $"Desfazer concluído com {errors.Count} avisos: " + string.Join(" | ", errors));
                    LoadHistory();
                    await RefreshAsync();
                });
        }

        void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            var dir = System.IO.Path.GetDirectoryName(_log.FilePath);
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }

        #endregion

        #region Infra

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageTweaks == null) return;
            PageTweaks.Visibility = NavTweaks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageComputer.Visibility = NavComputer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageHistory.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavHistory.IsChecked == true && _engine != null) LoadHistory();
        }

        void ShowConfirm(string title, string intro, IList<string> lines, string okText, Func<Task> onOk)
        {
            ConfirmTitle.Text = title;
            ConfirmIntro.Text = intro;
            ConfirmList.ItemsSource = lines;
            ConfirmOk.Content = okText;
            _pendingConfirm = onOk;
            ConfirmOverlay.Visibility = Visibility.Visible;
            ConfirmOk.Focus();
        }

        void ConfirmCancel_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _pendingConfirm = null;
        }

        async void ConfirmOk_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            var action = _pendingConfirm;
            _pendingConfirm = null;
            if (action != null) await action();
        }

        async Task Busy(string message, Func<Task> work)
        {
            BusyText.Text = message;
            IsEnabled = false;
            try { await work(); }
            catch (Exception ex) { _log.Write("ERRO: " + ex.Message); }
            finally { IsEnabled = true; BusyText.Text = ""; CleanupOverlay.Visibility = Visibility.Collapsed; }
        }

        #endregion
    }
}
