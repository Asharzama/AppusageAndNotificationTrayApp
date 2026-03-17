using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Threading.Tasks;

namespace AppUsageAndNotification.Helper
{
    public static class AppHelper
    {
        private const string AppProcessName = "Safe4Sure"; // 🔁 your MAUI exe name without .exe
        private const string AppExeName = "MdMApp.exe"; // 🔁 your MAUI exe name

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public static void OpenSafe4SureApp()
        {
            try
            {
                // Bring to foreground if already running
                var processes = Process.GetProcessesByName(AppProcessName);
                if (processes.Length > 0)
                {
                    var hwnd = processes[0].MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                        Debug.WriteLine("✅ Safe4Sure brought to foreground.");
                        return;
                    }
                }

                // Launch if not running
                var exePath = Path.Combine(AppContext.BaseDirectory, AppExeName);
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                    Debug.WriteLine("✅ Safe4Sure launched.");
                }
                else
                {
                    Debug.WriteLine($"❌ Safe4Sure.exe not found at: {exePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ OpenSafe4SureApp failed: {ex.Message}");
            }
        }
    }
}
