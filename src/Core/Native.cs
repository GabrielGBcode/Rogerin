using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Rogerin.Core
{
    /// <summary>Chamadas Win32 usadas pelo motor. Nenhum script ou shell e invocado.</summary>
    static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Barra de titulo escura (Windows 10 20H1+ e 11). Em versoes antigas a chamada so falha em silencio.</summary>
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));     // valor antigo, Windows 10 1809-1909
        }

        /// <summary>Esvazia o cache do cliente DNS (equivale a ipconfig /flushdns, sem mexer no IP).</summary>
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        public static extern bool FlushDnsCache();

        #region Janelas de ferramentas do sistema

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Manda WM_NULL e WM_MOUSEMOVE para as janelas (e controles) dos processos informados, como se o mouse
        /// passasse por cima. A janela de "Pacotes de driver" / "Limpeza do Windows Update" do cleanmgr /sagerun
        /// termina o trabalho mas so fecha quando recebe alguma mensagem de entrada; sem isso fica parada.
        /// </summary>
        public static void NudgeWindows(ICollection<int> processIds)
        {
            const uint WM_NULL = 0x0000, WM_MOUSEMOVE = 0x0200;
            EnumWindowsProc nudge = (hwnd, _) =>
            {
                PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, IntPtr.Zero);
                return true;
            };
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (processIds.Contains(pid))
                {
                    nudge(hwnd, IntPtr.Zero);
                    EnumChildWindows(hwnd, nudge, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            GC.KeepAlive(nudge);
        }

        #endregion

        #region Sessao do usuario interativo

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        const int WTSUserName = 5, WTSDomainName = 7;

        public static string QuerySessionString(uint sessionId, bool domain)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, domain ? WTSDomainName : WTSUserName, out var buf, out _))
                return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { WTSFreeMemory(buf); }
        }

        #endregion

        #region Servicos

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string binary,
            string group, IntPtr tag, string deps, string account, string password, string display);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref uint delayedAutoStart);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_NO_CHANGE = 0xFFFFFFFF;
        const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

        /// <param name="start">2 = automatico, 3 = manual, 4 = desativado</param>
        public static void SetServiceStart(string name, uint start, bool delayed)
        {
            var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var svc = OpenService(scm, name, SERVICE_CHANGE_CONFIG);
                if (svc == IntPtr.Zero) throw new Win32Exception();
                try
                {
                    if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                        throw new Win32Exception();
                    if (start == 2)
                    {
                        uint flag = delayed ? 1u : 0u;
                        ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref flag);
                    }
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type, uint start,
            uint error, string binary, string group, IntPtr tag, string deps, string account, string password);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeleteService(IntPtr service);

        const uint SC_MANAGER_CREATE_SERVICE = 0x0002, DELETE = 0x10000, SERVICE_ALL_ACCESS = 0xF01FF;
        const int ERROR_SERVICE_DOES_NOT_EXIST = 1060, ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SERVICE_FAILURE_ACTIONS
        {
            public uint ResetPeriod;   // segundos sem falha para zerar a contagem
            public string RebootMsg;
            public string Command;
            public uint ActionCount;
            public IntPtr Actions;     // SC_ACTION[]
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SC_ACTION { public uint Type; public uint Delay; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

        const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4, SC_ACTION_RESTART = 1;

        /// <summary>
        /// Cria um servico automatico (LocalSystem). <paramref name="deps"/>: nomes separados por \0, terminando em \0\0.
        /// Com <paramref name="restartDelay"/>, o Windows reinicia o servico sozinho se ele travar ou parar com erro.
        /// </summary>
        public static void CreateAutoService(string name, string display, string binaryPath, string deps, TimeSpan? restartDelay = null)
        {
            var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var svc = CreateService(scm, name, display, SERVICE_ALL_ACCESS, 0x10 /* WIN32_OWN_PROCESS */, 2 /* AUTO_START */,
                    1 /* ERROR_NORMAL */, binaryPath, null, IntPtr.Zero, deps, null, null);
                if (svc == IntPtr.Zero) throw new Win32Exception();
                try { if (restartDelay != null) SetRestartOnFailure(svc, restartDelay.Value); }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>Reinicia nas tres primeiras falhas; a contagem zera apos 1 dia sem falhas.</summary>
        static void SetRestartOnFailure(IntPtr svc, TimeSpan delay)
        {
            var actions = new[]
            {
                new SC_ACTION { Type = SC_ACTION_RESTART, Delay = (uint)delay.TotalMilliseconds },
                new SC_ACTION { Type = SC_ACTION_RESTART, Delay = (uint)delay.TotalMilliseconds },
                new SC_ACTION { Type = SC_ACTION_RESTART, Delay = (uint)delay.TotalMilliseconds },
            };
            int size = Marshal.SizeOf(typeof(SC_ACTION));
            var buf = Marshal.AllocHGlobal(size * actions.Length);
            try
            {
                for (int i = 0; i < actions.Length; i++)
                    Marshal.StructureToPtr(actions[i], buf + i * size, false);
                var info = new SERVICE_FAILURE_ACTIONS { ResetPeriod = 86400, ActionCount = (uint)actions.Length, Actions = buf };
                if (!ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS, ref info)) throw new Win32Exception();
                // Tambem quando o processo encerra "normalmente" mas com codigo de erro, nao so quando trava.
                uint onNonCrash = 1;
                ChangeServiceConfig2(svc, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, ref onNonCrash);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Remove o servico. Retorna false se ele nao existia.</summary>
        public static bool RemoveService(string name)
        {
            var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var svc = OpenService(scm, name, DELETE);
                if (svc == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_DOES_NOT_EXIST) return false;
                    throw new Win32Exception(err);
                }
                try
                {
                    if (!DeleteService(svc) && Marshal.GetLastWin32Error() != ERROR_SERVICE_MARKED_FOR_DELETE)
                        throw new Win32Exception();
                    return true;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string existing, string newName, uint flags);

        /// <summary>Agenda a exclusao do arquivo para o proximo reinicio (arquivo em uso).</summary>
        public static void DeleteOnReboot(string path)
        {
            if (!MoveFileEx(path, null, 0x4 /* MOVEFILE_DELAY_UNTIL_REBOOT */)) throw new Win32Exception();
        }

        #endregion

        #region Linha de comando

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
        static extern IntPtr LocalFree_CmdLine(IntPtr mem); // mesma função LocalFree de Energia; nome diferente só para clareza no uso

        /// <summary>
        /// Separa uma linha de comando (ex.: string de desinstalação do registro) em executável e argumentos
        /// do mesmo jeito que o próprio Windows faz (CommandLineToArgvW), sem nenhum shell (cmd.exe) envolvido.
        /// </summary>
        public static string[] SplitCommandLine(string cmdLine)
        {
            var ptr = CommandLineToArgvW(cmdLine, out var argc);
            if (ptr == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var args = new string[argc];
                for (int i = 0; i < argc; i++)
                    args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size));
                return args;
            }
            finally { LocalFree_CmdLine(ptr); }
        }

        #endregion

        #region Windows Installer

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiOpenPackageEx(string path, uint options, out IntPtr product);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiGetProductProperty(IntPtr product, string property, System.Text.StringBuilder value, ref uint length);

        [DllImport("msi.dll")]
        static extern uint MsiCloseHandle(IntPtr handle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern int MsiQueryProductState(string productCode);

        /// <summary>Le propriedades de um .msi sem instalar: abrir o pacote nao roda nenhuma acao dele.</summary>
        public static string[] MsiProperties(string msiPath, params string[] names)
        {
            var rc = MsiOpenPackageEx(msiPath, 1 /* MSIOPENPACKAGEFLAGS_IGNOREMACHINESTATE */, out var h);
            if (rc != 0) throw new Win32Exception((int)rc);
            try
            {
                var values = new string[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var sb = new System.Text.StringBuilder(512);
                    uint len = (uint)sb.Capacity;
                    rc = MsiGetProductProperty(h, names[i], sb, ref len);
                    if (rc != 0) throw new Win32Exception((int)rc, $"{names[i]} não lido do pacote");
                    values[i] = sb.ToString();
                }
                return values;
            }
            finally { MsiCloseHandle(h); }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiEnumProducts(uint index, System.Text.StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiGetProductInfo(string productCode, string property, System.Text.StringBuilder value, ref uint length);

        /// <summary>ProductCode de todos os produtos do Windows Installer instalados.</summary>
        public static System.Collections.Generic.IEnumerable<string> MsiProducts()
        {
            for (uint i = 0; ; i++)
            {
                var sb = new System.Text.StringBuilder(39);
                if (MsiEnumProducts(i, sb) != 0) yield break; // 259 = acabou a lista
                yield return sb.ToString();
            }
        }

        /// <summary>Propriedade de um produto instalado (ProductName, VersionString, InstallLocation...). Null se nao houver.</summary>
        public static string MsiProductInfo(string productCode, string property)
        {
            var sb = new System.Text.StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return MsiGetProductInfo(productCode, property, sb, ref len) == 0 ? sb.ToString() : null;
        }

        /// <summary>True se o produto (ProductCode) esta instalado nesta maquina.</summary>
        public static bool MsiInstalled(string productCode) => MsiQueryProductState(productCode) == 5; // INSTALLSTATE_DEFAULT

        #endregion

        #region Memória (liberar RAM)

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferSize, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008, SE_PRIVILEGE_ENABLED = 0x0002;

        /// <summary>Habilita um privilégio no token do próprio processo (ex.: SeProfileSingleProcessPrivilege). Falso se não concedido ao usuário.</summary>
        static bool EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) return false;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        const int SystemMemoryListInformation = 0x50;
        // Comandos de MEMORY_LIST_COMMAND: 2 = esvaziar working sets, 3 = lista modificada, 4 = standby, 5 = standby de baixa prioridade.
        static readonly int[] MemoryPurgeCommands = { 2, 3, 4, 5 };

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        /// <summary>RAM física livre, em GB.</summary>
        public static double AvailablePhysicalMemoryGB()
        {
            var s = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref s) ? s.AvailPhys / (double)(1L << 30) : 0;
        }

        /// <summary>
        /// Esvazia as listas de memória em espera do Windows (standby/modified/working sets): processos que não
        /// estão em uso liberam RAM de volta para o sistema. Não encerra nem altera nenhum processo.
        /// Equivalente ao botão de liberar memória de otimizadores comerciais, só com API do Windows.
        /// </summary>
        public static void PurgeMemoryLists()
        {
            if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) throw new Win32Exception("Privilégio SeProfileSingleProcessPrivilege não concedido a este usuário.");
            foreach (var cmd in MemoryPurgeCommands)
            {
                var value = cmd;
                var rc = NtSetSystemInformation(SystemMemoryListInformation, ref value, sizeof(int));
                if (rc < 0) throw new Win32Exception(rc, $"NtSetSystemInformation(comando {cmd}) falhou (0x{rc:X8}).");
            }
        }

        #endregion

        #region Energia

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        public static Guid GetActivePowerScheme()
        {
            var rc = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
            if (rc != 0) throw new Win32Exception((int)rc, "PowerGetActiveScheme: " + new Win32Exception((int)rc).Message);
            try { return (Guid)Marshal.PtrToStructure(ptr, typeof(Guid)); }
            finally { LocalFree(ptr); }
        }

        public static void SetActivePowerScheme(Guid scheme)
        {
            var rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (rc != 0)
                throw new Win32Exception((int)rc, rc == 2
                    ? "Plano de energia não existe neste computador (comum em notebooks com Modern Standby)."
                    : new Win32Exception((int)rc).Message);
        }

        [DllImport("powrprof.dll")]
        static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, byte[] output, uint outputSize);

        /// <summary>
        /// True se o PC usa Modern Standby (S0 de baixo consumo). Nesse modo o Windows so oferece o plano Equilibrado:
        /// Alto desempenho e Desempenho maximo ficam ocultos e nao ficam ativos.
        /// </summary>
        public static bool ModernStandby()
        {
            const int SystemPowerCapabilities = 4;
            var caps = new byte[76]; // SYSTEM_POWER_CAPABILITIES
            if (CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, caps, (uint)caps.Length) != 0) return false;
            return caps[20] != 0; // AoAc
        }

        [DllImport("powrprof.dll")]
        static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint accessFlags, uint index, ref Guid buffer, ref uint size);

        [DllImport("powrprof.dll")]
        static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, ref IntPtr destination);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

        /// <summary>Planos de energia visiveis no Painel de Controle (o Desempenho maximo fica oculto ate ser duplicado).</summary>
        public static List<Guid> PowerSchemes()
        {
            const uint ACCESS_SCHEME = 16;
            var list = new List<Guid>();
            for (uint i = 0; ; i++)
            {
                var g = Guid.Empty;
                uint size = 16;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, ref g, ref size) != 0) break;
                list.Add(g);
            }
            return list;
        }

        /// <summary>Equivale a "powercfg -duplicatescheme origem destino": cria o plano com um GUID fixo.</summary>
        public static void DuplicatePowerScheme(Guid source, Guid destination)
        {
            var buf = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.StructureToPtr(destination, buf, false);
                var ptr = buf;
                var rc = PowerDuplicateScheme(IntPtr.Zero, ref source, ref ptr);
                if (rc != 0) throw new Win32Exception((int)rc, $"PowerDuplicateScheme ({source}): {new Win32Exception((int)rc).Message}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        const uint ERROR_FILE_NOT_FOUND = 2;

        static Win32Exception PowerError(uint rc, string call, Guid scheme, Guid setting) =>
            new Win32Exception((int)rc, $"{call} (plano {scheme}, configuração {setting}): {new Win32Exception((int)rc).Message}");

        /// <summary>
        /// Valor (na tomada e na bateria) de uma configuracao do plano, em segundos para os tempos de espera.
        /// Null quando o plano nao grava valor proprio (planos de fabricante ou de GPO): vale o padrao do Windows.
        /// </summary>
        public static (uint? ac, uint? dc) ReadPowerValue(Guid scheme, Guid subGroup, Guid setting)
        {
            uint? ac = null, dc = null;
            var rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var a);
            if (rc == 0) ac = a; else if (rc != ERROR_FILE_NOT_FOUND) throw PowerError(rc, "PowerReadACValueIndex", scheme, setting);
            rc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var d);
            if (rc == 0) dc = d; else if (rc != ERROR_FILE_NOT_FOUND) throw PowerError(rc, "PowerReadDCValueIndex", scheme, setting);
            return (ac, dc);
        }

        /// <summary>
        /// Grava o valor no plano (null = nao mexe). So vale no plano ativo depois de <see cref="SetActivePowerScheme"/>.
        /// Configuracao que nao existe neste PC (ex.: "Suspender apos" em maquina sem S0 nem S3) e ignorada:
        /// nao ha o que gravar, e o erro impedia de ativar o plano.
        /// </summary>
        public static void WritePowerValue(Guid scheme, Guid subGroup, Guid setting, uint? ac, uint? dc)
        {
            uint rc;
            if (ac.HasValue && (rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, ac.Value)) != 0 && rc != ERROR_FILE_NOT_FOUND)
                throw PowerError(rc, "PowerWriteACValueIndex", scheme, setting);
            if (dc.HasValue && (rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, dc.Value)) != 0 && rc != ERROR_FILE_NOT_FOUND)
                throw PowerError(rc, "PowerWriteDCValueIndex", scheme, setting);
        }

        #endregion
    }
}
