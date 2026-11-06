namespace Rogerin.Core
{
    public struct MemoryBoostResult
    {
        public double BeforeFreeGB;
        public double AfterFreeGB;
        public double FreedGB => AfterFreeGB - BeforeFreeGB > 0 ? AfterFreeGB - BeforeFreeGB : 0;
    }

    /// <summary>
    /// Ação imediata, não uma configuração: não entra no catálogo nem no backup/desfazer.
    /// Esvazia as listas de memória em espera do Windows (não mexe em nenhum processo do usuário).
    /// </summary>
    public static class MemoryBoost
    {
        public static MemoryBoostResult Run()
        {
            var before = Native.AvailablePhysicalMemoryGB();
            Native.PurgeMemoryLists();
            var after = Native.AvailablePhysicalMemoryGB();
            return new MemoryBoostResult { BeforeFreeGB = before, AfterFreeGB = after };
        }
    }
}
