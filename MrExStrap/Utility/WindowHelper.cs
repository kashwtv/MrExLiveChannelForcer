using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MrExStrap.Utility
{
    public static class WindowHelper
    {
        private const string LOG_IDENT = "WindowHelper";
        private const string RobloxProcessName = "RobloxPlayerBeta";
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x0008;

        public static void SetAlwaysOnTop(bool enabled)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(TimeSpan.FromSeconds(6));

                    var hWnd = FindRobloxMainWindow();
                    if (hWnd == IntPtr.Zero)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Roblox window not found for always-on-top.");
                        return;
                    }

                    IntPtr hwndInsertAfter = enabled ? new IntPtr(-1) : new IntPtr(-2);

                    if (!SetWindowPos(hWnd, hwndInsertAfter, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
                    {
                        int err = Marshal.GetLastWin32Error();
                        App.Logger.WriteLine(LOG_IDENT, $"SetWindowPos failed: {err}");
                    }

                    App.Logger.WriteLine(LOG_IDENT, enabled ? "Set window to always-on-top." : "Removed always-on-top.");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::SetAlwaysOnTop", ex);
                }
            });
        }

        private static IntPtr FindRobloxMainWindow()
        {
            var pids = new HashSet<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName(RobloxProcessName))
                {
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }

            if (pids.Count == 0)
                return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!pids.Contains((int)pid)) return true;

                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;

                var classBuf = new System.Text.StringBuilder(128);
                GetClassName(hWnd, classBuf, classBuf.Capacity);
                string className = classBuf.ToString();

                if (className.StartsWith("WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("ROBLOX", StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        #region P/Invoke

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        #endregion
    }
}
