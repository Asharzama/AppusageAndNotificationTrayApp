using AppUsageAndNotification.Services;
using AppUsageAndNotification.TrayIcon;
using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace AppUsageAndNotification
{
    static class Program
    {
        [STAThread]
        static async Task Main(string[] args)
        {
            Application.ThreadException += (s, e) =>
            Debug.WriteLine($"❌ ThreadException: {e.Exception.Message}");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Debug.WriteLine($"❌ UnhandledException: {(e.ExceptionObject as Exception)?.Message}");
                if (e.IsTerminating) RestartApp();
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Debug.WriteLine($"❌ Task Exception: {e.Exception.Message}");
                e.SetObserved(); // ✅ Don't kill app
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Parse args from MAUI
            string userId = "";
            int deviceId = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--userId" && i + 1 < args.Length)
                    userId = args[i + 1];
                if (args[i] == "--deviceId" && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out deviceId);
            }

            AppConfig.UserId = userId;
            AppConfig.DeviceId = deviceId;

            // Fetch device info if not passed
            if (string.IsNullOrEmpty(userId) || deviceId == 0)
            {
                try
                {
                    var apiService = new Services.ApiService();
                    await apiService.FetchAndStoreDeviceInfoAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Device info fetch failed: {ex.Message}");
                }
            }

            var mutex = new System.Threading.Mutex(true, "AppUsageAndNotification_Mutex", out bool isNew);
            if (!isNew) return;

            // ✅ Keep GC from collecting mutex
            GC.KeepAlive(mutex);

            Application.Run(new TrayApplicationContext());
        }
        private static void RestartApp()
        {
            try
            {
                var exePath = System.Diagnostics.Process
                    .GetCurrentProcess().MainModule!.FileName;
                System.Diagnostics.Process.Start(exePath);
            }
            catch { }
        }
    }
}
