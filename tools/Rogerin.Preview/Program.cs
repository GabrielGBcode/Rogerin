using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rogerin.Core;

namespace Rogerin.Preview
{
    /// <summary>
    /// Uso: Rogerin.Preview.exe &lt;pasta-de-saida&gt;
    /// 1) grava audit.json com o estado real do sistema (somente leitura);
    /// 2) abre a janela fora da tela e salva PNG de cada pagina e da confirmacao (sem confirmar).
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "preview");
            Directory.CreateDirectory(outDir);

            var sys = SystemInfo.Collect();
            var user = UserContext.Resolve();
            var engine = new Engine(Catalog.Load(), sys, user, new Log());
            var audit = engine.Catalog.Tweaks.Select(t => engine.Audit(t)).Select(r => new
            {
                id = r.Tweak.Id, name = r.Tweak.Name, category = r.Tweak.Category, risk = r.Tweak.Risk,
                status = r.Status.ToString(), gpo = r.Tweak.GpoManaged, description = r.Tweak.Description,
                actions = r.Actions.Select(a => new { what = a.Action.Describe(), current = a.Error ?? a.State?.Current, state = a.State?.Status.ToString() })
            }).ToList();
            var cleanup = new Cleanup(user, sys);
            var targets = cleanup.Targets();
            foreach (var t in targets) cleanup.Measure(t);
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new
            {
                generated = DateTime.Now.ToString("o"),
                system = sys, user = new { user.Account, user.IsDifferentFromElevatedUser },
                tweaks = audit,
                cleanup = targets.Select(t => new { t.Name, t.Group, t.Path, Roots = t.Roots.ToList(), t.Files, t.Bytes, t.Running })
            });
            File.WriteAllText(Path.Combine(outDir, "audit.json"), json);
            foreach (var g in audit.GroupBy(a => a.status)) Console.WriteLine($"{g.Key,-14} {g.Count()}");

            // So carrega os recursos do App. Nao chama app.Run(): isso dispararia App.OnStartup,
            // que exige administrador e abriria uma caixa de aviso na tela.
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var win = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowInTaskbar = false };
            var code = 0;
            win.Loaded += async (s, e) =>
            {
                try { await Capture(win, outDir); }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                finally { win.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            };
            win.Show();
            Dispatcher.Run();
            return code;
        }

        static T Find<T>(Window w, string name) where T : class => (T)w.FindName(name);

        static async Task Capture(MainWindow win, string outDir)
        {
            // espera a analise inicial terminar
            for (int i = 0; i < 900 && (!win.IsEnabled || Find<RadioButton>(win, "LevelBasic").IsChecked != true); i++)
                await Task.Delay(100);
            await Settle();
            Snap(win, Path.Combine(outDir, "1-ajustes.png"));

            var scroll = Find<ScrollViewer>(win, "TweakScroll");
            scroll.ScrollToVerticalOffset(360);
            await Settle();
            Snap(win, Path.Combine(outDir, "2-ajustes-lista.png"));

            scroll.ScrollToEnd();
            await Settle();
            Snap(win, Path.Combine(outDir, "3-ajustes-limpeza.png"));
            scroll.ScrollToTop();

            // Nivel Recomendado + tela de confirmacao (somente simulacao). "Aplicar agora" NAO e acionado.
            Find<RadioButton>(win, "LevelRecommended").IsChecked = true;
            await Settle();
            typeof(MainWindow).GetMethod("Review_Click", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(win, new object[] { null, null });
            var overlay = Find<Grid>(win, "ConfirmOverlay");
            for (int i = 0; i < 300 && overlay.Visibility != Visibility.Visible; i++) await Task.Delay(100);
            await Settle();
            Snap(win, Path.Combine(outDir, "4-confirmacao.png"));
            typeof(MainWindow).GetMethod("ConfirmCancel_Click", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(win, new object[] { null, null });

            Find<RadioButton>(win, "NavComputer").IsChecked = true;
            await Settle();
            Snap(win, Path.Combine(outDir, "5-computador.png"));

            Find<RadioButton>(win, "NavStartup").IsChecked = true;
            await Settle();
            Snap(win, Path.Combine(outDir, "6-inicializacao.png"));

            Find<RadioButton>(win, "NavUninstall").IsChecked = true;
            await Settle();
            Snap(win, Path.Combine(outDir, "7-desinstalar.png"));

            Find<RadioButton>(win, "NavHistory").IsChecked = true;
            await Settle();
            Snap(win, Path.Combine(outDir, "8-historico.png"));
        }

        static async Task Settle()
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);
        }

        static void Snap(Window w, string file)
        {
            var root = (FrameworkElement)w.Content;
            root.UpdateLayout();
            const double scale = 1.5;
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.Resources["Ground"], null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
            Console.WriteLine("salvo " + file);
        }
    }
}
