using System;
using System.IO;

namespace Rogerin.Core
{
    /// <summary>Trilha de auditoria em %ProgramData%\Rogerin\logs, um arquivo por dia.</summary>
    public class Log
    {
        readonly object _lock = new object();
        /// <summary>(linha completa do arquivo, mensagem curta)</summary>
        public event Action<string, string> Written;

        public string FilePath =>
            Path.Combine(Engine.DataDir, "logs", $"rogerin-{DateTime.Now:yyyyMMdd}.log");

        public void Write(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.MachineName} {Environment.UserName} {message}";
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Written?.Invoke(line, message);
        }
    }
}
