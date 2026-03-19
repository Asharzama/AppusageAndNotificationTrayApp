using AppUsageAndNotification.AppUsage;
using AppUsageAndNotification.CommandExecution;
using AppUsageAndNotification.Helper;
using AppUsageAndNotification.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;       
using System.IO;            
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AppUsageAndNotification.TrayIcon
{
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon _trayIcon = null!;
        private readonly ApiService _apiService = new ApiService();
        private readonly CommandExecutorService _commandExecutor;
        private readonly ReminderService _reminderService;
        private readonly ForegroundAppTracker _appTracker;
        private readonly AppInstallMonitorService _appInstallMonitor;
        private System.Timers.Timer _masterTimer = null!;
        private int _tickCount = 0;

        public static TrayApplicationContext? Instance { get; private set; }

        public TrayApplicationContext()
        {
            Instance = this;
            _commandExecutor = new CommandExecutorService(_apiService);
            _reminderService = new ReminderService(_apiService);
            _appTracker = new ForegroundAppTracker();
            _appInstallMonitor = new AppInstallMonitorService(
                _apiService, _commandExecutor);

            InitializeTray();
            StartTimers();
            RegisterStartup();
            _appTracker.Start();
        }

        private void InitializeTray()
        {
            var iconPath = Path.Combine(
                AppContext.BaseDirectory, "Assets", "tray-icon.ico");

            _trayIcon = new NotifyIcon
            {
                Icon = File.Exists(iconPath)
                    ? new Icon(iconPath)
                    : SystemIcons.Application,
                Text = "Safe4Sure",
                Visible = true,
                ContextMenuStrip = BuildContextMenu()
            };

            _trayIcon.Click += (s, e) =>
            {
                if (((MouseEventArgs)e).Button == MouseButtons.Left)
                    AppHelper.OpenSafe4SureApp();
            };

            _trayIcon.DoubleClick += (s, e) => AppHelper.OpenSafe4SureApp();
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("🔓 Open Safe4Sure");
            openItem.Click += (s, e) => AppHelper.OpenSafe4SureApp();

            var exitItem = new ToolStripMenuItem("❌ Exit");
            exitItem.Click += (s, e) =>
            {
                _trayIcon.Visible = false;
                _appTracker.Stop();
                _masterTimer?.Stop();
                Application.Exit();
            };

            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            return menu;
        }

        private void StartTimers()
        {
            _masterTimer = new System.Timers.Timer(30 * 1000);
            _masterTimer.Elapsed += async (s, e) =>
            {
                _masterTimer.Stop();
                try
                {
                    await _commandExecutor.CheckAndExecutePendingCommandsAsync();
                    await _reminderService.CheckAndShowRemindersAsync();

                    await _appInstallMonitor.CheckAndInstallNewAppsAsync();
                    _tickCount++;
                    if (_tickCount % 10 == 0)
                    {
                        var records = _appTracker.GetTopApps(50);
                        if (records.Count > 0)
                            await _apiService.PostAppUsageAsync(
                                records, AppConfig.UserId, AppConfig.DeviceId);

                    }
                }
                catch (Exception ex)
                {
                    await _apiService.LogErrorAsync("Master Timer", ex.Message);
                }
                finally
                {
                    _masterTimer.Start();
                }
            };
            _masterTimer.AutoReset = false;
            _masterTimer.Start();

            Task.Run(async () =>
            {
                try
                {
                    await _commandExecutor.CheckAndExecutePendingCommandsAsync();
                    await _reminderService.CheckAndShowRemindersAsync();
                    await _appInstallMonitor.CheckAndInstallNewAppsAsync();
                }
                catch (Exception ex)
                {
                    await _apiService.LogErrorAsync("Startup Task", ex.Message);
                }
            });
        }

        private void RegisterStartup()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule!.FileName;
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("Safe4SureTrayApp", $"\"{exePath}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ RegisterStartup: {ex.Message}");
            }
        }

        public void ShowNotification(string title, string message)
        {
            _trayIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
            _trayIcon.BalloonTipClicked += (s, e) => AppHelper.OpenSafe4SureApp();
        }
    }
}
