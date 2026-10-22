using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace Rogerin.Core
{
    public class DetectSpec
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public object Value { get; set; }
    }

    public class TweakAction
    {
        public string Type { get; set; }

        // registry
        public string Path { get; set; }
        public string Name { get; set; }
        public string ValueType { get; set; }
        public object Value { get; set; }
        public string RemoveKeyOnRevert { get; set; }

        // service
        public string StartType { get; set; }

        // task
        public string State { get; set; }

        // appx
        public string Package { get; set; }

        // powerScheme
        public string Guid { get; set; }

        // command
        public string File { get; set; }
        public string ApplyArgs { get; set; }
        public string RevertArgs { get; set; }
        public DetectSpec Detect { get; set; }

        public string Describe()
        {
            switch (Type)
            {
                case "registry": return $@"{Path}\{Name} = {Value}";
                case "service": return $"Serviço {Name} → {StartType}";
                case "task": return $@"Tarefa {Path}\{Name} → {State}";
                case "appx": return $"Remover app {Package}";
                case "powerScheme": return $"Plano de energia {Guid}";
                case "command": return $"{File} {ApplyArgs}";
                default: return Type;
            }
        }
    }

    public class Tweak
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Risk { get; set; }
        public List<string> Os { get; set; } = new List<string>();
        public int MinBuild { get; set; }
        public bool RebootRequired { get; set; }
        public bool GpoManaged { get; set; }
        /// <summary>"hdd" ou "ssd": so vale para esse tipo de disco do sistema. Se o tipo for desconhecido, o ajuste aparece.</summary>
        public string Media { get; set; }
        public List<TweakAction> Actions { get; set; } = new List<TweakAction>();

        public bool AppliesTo(SystemInfo sys) =>
            Os.Contains(sys.WindowsMajor) && sys.Build >= MinBuild &&
            (Media == null || sys.DiskMedia == null || Media.Equals(sys.DiskMedia, StringComparison.OrdinalIgnoreCase));
    }

    public class Catalog
    {
        public List<Tweak> Tweaks { get; private set; }

        class TweakFile { public List<Tweak> Tweaks { get; set; } }

        public static Catalog Load()
        {
            var tweaks = new JavaScriptSerializer().Deserialize<TweakFile>(ReadResource("Rogerin.tweaks.json")).Tweaks;
            var dup = tweaks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) throw new InvalidDataException("Ids duplicados no catálogo: " + string.Join(", ", dup));
            return new Catalog { Tweaks = tweaks };
        }

        static string ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("Recurso embutido ausente: " + name);
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
    }
}
