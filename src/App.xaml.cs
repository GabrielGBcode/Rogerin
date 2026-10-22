using System;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace Rogerin
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // O manifesto ja exige elevacao; esta checagem cobre execucao com UAC desligado ou via ferramentas de terceiros.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show("O Rogerin precisa ser executado como Administrador.\n\nClique com o botão direito no arquivo e escolha \"Executar como administrador\".",
                    "Rogerin", MessageBoxButton.OK, MessageBoxImage.Stop);
                Shutdown(1);
                return;
            }

            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
            new MainWindow().Show();
        }

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, "Rogerin: erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
