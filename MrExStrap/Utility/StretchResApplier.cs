using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MrExStrap.Utility
{
    public static class StretchResApplier
    {
        private const string LOG_IDENT = "StretchResApplier";
        private const string RobloxProcessName = "RobloxPlayerBeta";

        public static readonly IReadOnlyDictionary<StretchResolution, (int Width, int Height)> Resolutions =
            new Dictionary<StretchResolution, (int, int)>
            {
                { StretchResolution.Res_1024x768,  (1024, 768)  },
                { StretchResolution.Res_1280x960,  (1280, 960)  },
                { StretchResolution.Res_1280x1024, (1280, 1024) },
                { StretchResolution.Res_1440x1080, (1440, 1080) },
                { StretchResolution.Res_1600x1200, (1600, 1200) },
                { StretchResolution.Res_1920x1440, (1920, 1440) },
                { StretchResolution.Res_1280x800,  (1280, 800)  },
                { StretchResolution.Res_1680x1050, (1680, 1050) },
                { StretchResolution.Res_1920x1200, (1920, 1200) },
            };

        public static string GetDisplayName(StretchResolution res)
        {
            if (res == StretchResolution.Disabled) return "Disabled";
            if (res == StretchResolution.Custom) return "Custom";
            if (Resolutions.TryGetValue(res, out var size))
                return $"{size.Width}x{size.Height}";
            return res.ToString();
        }

        public static void ScheduleApply(StretchResolution resolution, int customWidth = 0, int customHeight = 0)
        {
            int width, height;

            if (resolution == StretchResolution.Custom)
            {
                width = customWidth;
                height = customHeight;
            }
            else if (Resolutions.TryGetValue(resolution, out var size))
            {
                width = size.Width;
                height = size.Height;
            }
            else
            {
                return;
            }

            if (width <= 0 || height <= 0)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(TimeSpan.FromSeconds(6));
                    ApplyNow(width, height);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Schedule", ex);
                }
            });
        }

        private static void ApplyNow(int width, int height)
        {
            var hWnd = FindRobloxMainWindow();

            if (hWnd == IntPtr.Zero)
            {
                App.Logger.WriteLine(LOG_IDENT, "No Roblox window found for stretch res.");
                return;
            }

            ShowWindow(hWnd, SW_RESTORE);

            GetWindowRect(hWnd, out RECT currentRect);
            int screenW = currentRect.Right - currentRect.Left;
            int screenH = currentRect.Bottom - currentRect.Top;

            int x = Math.Max(0, (screenW - width) / 2 + currentRect.Left);
            int y = Math.Max(0, (screenH - height) / 2 + currentRect.Top);

            if (!SetWindowPos(hWnd, IntPtr.Zero, x, y, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED))
            {
                int err = Marshal.GetLastWin32Error();
                App.Logger.WriteLine(LOG_IDENT, $"SetWindowPos failed: {err}");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Applied stretch resolution {width}x{height} to Roblox window.");
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

                var classBuf = new StringBuilder(128);
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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        #endregion
    }
}
