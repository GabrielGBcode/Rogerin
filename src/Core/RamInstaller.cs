using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Rogerin.Core
{
    /// <summary>
    /// Baixa o instalador oficial do Rogerin-RAM (Release do proprio repositorio) e o abre.
    /// Nada e executado sem o tecnico confirmar antes. O instalador pede administrador pelo proprio manifesto.
    /// </summary>
    public static class RamInstaller
    {
        public const string FileName = "Rogerin-RAM-Setup-1.0.0.exe";
        public const string Url = "https://github.com/GabrielGBcode/Rogerin/releases/download/v1.0.0/" + FileName; // tag fixa: uma Release mais nova nao pode quebrar este link

        /// <summary>Baixa para a pasta temporaria e devolve o caminho. Lanca excecao se falhar.</summary>
        public static async Task<string> DownloadAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var destino = Path.Combine(Path.GetTempPath(), FileName);
            using (var http = new HttpClient())
            using (var resposta = await http.GetAsync(Url))
            {
                resposta.EnsureSuccessStatusCode();
                using (var arquivo = File.Create(destino))
                    await resposta.Content.CopyToAsync(arquivo);
            }
            return destino;
        }

        public static void Open(string caminho) => Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true });
    }
}
