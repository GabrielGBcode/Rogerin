using System;
using System.Security.Principal;
using Microsoft.Win32;

namespace Rogerin.Core
{
    /// <summary>
    /// Em empresa o tecnico costuma elevar com outra conta (UAC "por cima do ombro").
    /// Nesse caso HKCU seria o perfil do administrador, nao o do usuario da maquina.
    /// Por isso os ajustes de HKCU vao para HKEY_USERS\&lt;SID do usuario logado no console&gt;.
    /// </summary>
    public class UserContext
    {
        public string Sid { get; private set; }
        public string Account { get; private set; }
        public bool IsDifferentFromElevatedUser { get; private set; }

        /// <summary>Perfil de um backup antigo. Null se o perfil nao esta carregado (usuario nao logado).</summary>
        public static UserContext Loaded(string sid, string account)
        {
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64).OpenSubKey(sid))
                return hive == null ? null : new UserContext { Sid = sid, Account = account };
        }

        public static UserContext Resolve()
        {
            var self = WindowsIdentity.GetCurrent();
            var ctx = new UserContext { Sid = self.User.Value, Account = self.Name };

            var session = Native.WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return ctx;

            var user = Native.QuerySessionString(session, domain: false);
            var domain = Native.QuerySessionString(session, domain: true);
            if (string.IsNullOrEmpty(user)) return ctx;

            try
            {
                var account = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
                var sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64).OpenSubKey(sid))
                {
                    // Perfil precisa estar carregado (usuario logado) para receber os ajustes.
                    if (hive == null) return ctx;
                }
                ctx.IsDifferentFromElevatedUser = !string.Equals(sid, ctx.Sid, StringComparison.OrdinalIgnoreCase);
                ctx.Sid = sid;
                ctx.Account = account;
            }
            catch (IdentityNotMappedException) { }
            catch (SystemException) { }
            return ctx;
        }
    }
}
