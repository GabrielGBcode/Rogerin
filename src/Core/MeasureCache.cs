using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Rogerin.Core
{
    public class CachedMeasure
    {
        public int Files { get; set; }
        public long Bytes { get; set; }
        /// <summary>Data da medicao, formato ISO ("o").</summary>
        public string When { get; set; }
    }

    /// <summary>
    /// Ultima medicao de cada limpeza, so para mostrar um valor aproximado na hora enquanto a medicao nova roda.
    /// Nunca libera a selecao de um item: so a medicao nova faz isso.
    /// </summary>
    public static class MeasureCache
    {
        static string FilePath => Path.Combine(Engine.DataDir, "measure-cache.json");

        public static Dictionary<string, CachedMeasure> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Dictionary<string, CachedMeasure>();
                return new JavaScriptSerializer().Deserialize<Dictionary<string, CachedMeasure>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string, CachedMeasure>();
            }
            catch (Exception) { return new Dictionary<string, CachedMeasure>(); } // cache ruim vira cache vazio
        }

        public static void Save(Dictionary<string, CachedMeasure> data)
        {
            try
            {
                Directory.CreateDirectory(Engine.DataDir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(data));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
