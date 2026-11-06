using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rogerin.Core
{
    /// <summary>
    /// Localiza as pastas de cache dos navegadores instalados para o usuario logado.
    /// So entram pastas cujo nome esta em <see cref="CacheFolders"/>: senhas, favoritos, cookies,
    /// historico, extensoes e preenchimento automatico ficam fora por construcao.
    /// </summary>
    public static class Browsers
    {
        class Spec
        {
            public string Name;
            public string[] Processes;
            public string[] LocalRoots = new string[0];   // relativos a AppData\Local
            public string[] RoamingRoots = new string[0]; // relativos a AppData\Roaming
            public bool Firefox;
        }

        static readonly Spec[] Known =
        {
            new Spec { Name = "Google Chrome", Processes = new[] { "chrome" }, LocalRoots = new[] { @"Google\Chrome\User Data" } },
            new Spec { Name = "Microsoft Edge", Processes = new[] { "msedge" }, LocalRoots = new[] { @"Microsoft\Edge\User Data" } },
            new Spec { Name = "Brave", Processes = new[] { "brave" }, LocalRoots = new[] { @"BraveSoftware\Brave-Browser\User Data" } },
            new Spec { Name = "Vivaldi", Processes = new[] { "vivaldi" }, LocalRoots = new[] { @"Vivaldi\User Data" } },
            new Spec { Name = "Chromium", Processes = new[] { "chrome" }, LocalRoots = new[] { @"Chromium\User Data" } },
            new Spec { Name = "Opera", Processes = new[] { "opera" }, LocalRoots = new[] { @"Opera Software\Opera Stable" }, RoamingRoots = new[] { @"Opera Software\Opera Stable" } },
            new Spec { Name = "Opera GX", Processes = new[] { "opera" }, LocalRoots = new[] { @"Opera Software\Opera GX Stable" }, RoamingRoots = new[] { @"Opera Software\Opera GX Stable" } },
            new Spec { Name = "Mozilla Firefox", Processes = new[] { "firefox" }, LocalRoots = new[] { @"Mozilla\Firefox\Profiles" }, Firefox = true },
        };

        /// <summary>Nomes de pasta que podem ser esvaziados. Nada fora destas pastas e apagado.</summary>
        public static readonly HashSet<string> CacheFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Chromium (Chrome, Edge, Brave, Vivaldi, Opera)
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GrShaderCache", "GraphiteDawnCache", "ShaderCache", "CacheStorage", "ScriptCache",
            // Firefox
            "cache2", "startupCache", "thumbnails", "jumpListCache", "shader-cache",
        };

        /// <summary>
        /// Segunda trava: arquivos que guardam dados do usuario. Nunca sao apagados, mesmo que
        /// aparecam dentro de uma pasta de cache.
        /// </summary>
        public static readonly HashSet<string> ProtectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Login Data", "Login Data For Account", "Bookmarks", "Bookmarks.bak", "Cookies", "History", "Web Data",
            "Preferences", "Secure Preferences", "Local State", "Favicons", "Top Sites", "Shortcuts",
            "places.sqlite", "favicons.sqlite", "logins.json", "key4.db", "cert9.db", "cookies.sqlite",
            "formhistory.sqlite", "prefs.js", "sessionstore.jsonlz4", "bookmarkbackups",
        };

        static readonly string[] ChromiumProfileCaches =
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            @"Service Worker\CacheStorage", @"Service Worker\ScriptCache",
        };
        static readonly string[] ChromiumRootCaches = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };
        static readonly string[] FirefoxProfileCaches = { "cache2", "startupCache", "thumbnails", "jumpListCache", "shader-cache" };

        /// <summary>Uma entrada de limpeza por navegador instalado.</summary>
        public static IEnumerable<CleanupTarget> Find(string userProfile)
        {
            var local = Path.Combine(userProfile, @"AppData\Local");
            var roaming = Path.Combine(userProfile, @"AppData\Roaming");

            foreach (var b in Known)
            {
                var roots = b.LocalRoots.Select(r => Path.Combine(local, r))
                    .Concat(b.RoamingRoots.Select(r => Path.Combine(roaming, r)))
                    .Where(Directory.Exists).ToList();
                if (roots.Count == 0) continue;

                var caches = new List<string>();
                var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    if (b.Firefox)
                    {
                        foreach (var p in SafeDirs(root))
                        {
                            profiles.Add(Path.GetFileName(p));
                            caches.AddRange(FirefoxProfileCaches.Select(c => Path.Combine(p, c)));
                        }
                        continue;
                    }
                    caches.AddRange(ChromiumRootCaches.Select(c => Path.Combine(root, c)));
                    // O proprio root (Opera) e cada subpasta podem ser perfis: Default, Profile 1, Guest Profile...
                    foreach (var p in new[] { root }.Concat(SafeDirs(root)))
                    {
                        var hasCache = ChromiumProfileCaches.Any(c => Directory.Exists(Path.Combine(p, c)));
                        if (!hasCache) continue;
                        if (File.Exists(Path.Combine(p, "Preferences"))) profiles.Add(Path.GetFileName(p));
                        caches.AddRange(ChromiumProfileCaches.Select(c => Path.Combine(p, c)));
                    }
                }

                var existing = caches.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (existing.Count == 0) continue;
                yield return new CleanupTarget
                {
                    Name = "Cache do " + b.Name,
                    BrowserName = b.Name,
                    Group = "Navegadores",
                    Path = roots[0],
                    Paths = existing,
                    IgnoreAge = true,
                    Processes = b.Processes,
                    ProfileCount = Math.Max(1, profiles.Count),
                };
            }
        }

        /// <summary>True se a pasta e uma pasta de cache permitida (pelo ultimo segmento do caminho).</summary>
        public static bool IsAllowedCacheFolder(string path) =>
            CacheFolders.Contains(Path.GetFileName(path.TrimEnd('\\')));

        static IEnumerable<string> SafeDirs(string root)
        {
            try { return Directory.GetDirectories(root); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
            catch (IOException) { return Enumerable.Empty<string>(); }
        }
    }
}
