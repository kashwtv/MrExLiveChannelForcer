using System.Diagnostics;

namespace MrExStrap.Utility
{
    public static class SystemOptimizer
    {
        private const string LOG_IDENT = "SystemOptimizer";
        private const string RobloxProcessName = "RobloxPlayerBeta";

        public static void OptimizeMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                App.Logger.WriteLine(LOG_IDENT, "Memory optimization completed — garbage collection forced.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OptimizeMemory", ex);
            }
        }

        public static void SetRobloxPriorityHigh()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3));

                    foreach (var process in Process.GetProcessesByName(RobloxProcessName))
                    {
                        try
                        {
                            process.PriorityClass = ProcessPriorityClass.High;
                            App.Logger.WriteLine(LOG_IDENT, $"Set Roblox process {process.Id} to High priority.");
                            process.Dispose();
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException(LOG_IDENT + "::SetPriority", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::SetRobloxPriorityHigh", ex);
                }
            });
        }

        public static void EnableLatencyMonitor()
        {
            App.Logger.WriteLine(LOG_IDENT, "Latency monitor initialized (framework ready for overlay implementation).");
        }
    }
}
