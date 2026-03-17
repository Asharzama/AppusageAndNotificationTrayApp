using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace AppUsageAndNotification.AppUsage
{
    public class ForegroundAppTracker
    {
        // ── Win32 P/Invoke ──────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        // ────────────────────────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint GW_CHILD = 5;

        private readonly Dictionary<string, AppUsageRecord> _usageMap = new();
        private System.Timers.Timer? _pollTimer;
        private string? _lastAppName;
        private DateTime _lastSwitchTime;
        private readonly object _lock = new();

        // Fires whenever the foreground app changes
        public event EventHandler<AppUsageRecord>? AppSwitched;
        private static uint GetRealProcessId(IntPtr hwnd, uint fallbackPid)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);

            try
            {
                var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("ApplicationFrameHost",
                    StringComparison.OrdinalIgnoreCase))
                    return pid;
            }
            catch { return fallbackPid; }

            uint realPid = pid;

            EnumChildWindows(hwnd, (childHwnd, lParam) =>
            {
                GetWindowThreadProcessId(childHwnd, out uint childPid);
                try
                {
                    var childProcess = Process.GetProcessById((int)childPid);
                    if (!childProcess.ProcessName.Equals("ApplicationFrameHost",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        realPid = childPid;
                        return false;
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            return realPid;
        }
        public void Start()
        {
            _lastSwitchTime = DateTime.Now;

            _pollTimer = new System.Timers.Timer(1000); // poll every 1 second
            _pollTimer.Elapsed += OnPollTick;
            _pollTimer.AutoReset = true;
            _pollTimer.Start();
        }

        public void Stop()
        {
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
        }
        private static bool ShouldSkipProcess(string processName)
        {
            var skipList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "SystemSettings",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "LockApp",
        "LogonUI",
        "dwm",
        "explorer",
        "TextInputHost",
    };

            return skipList.Contains(processName);
        }
        private void OnPollTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                var titleBuilder = new StringBuilder(256);
                GetWindowText(hwnd, titleBuilder, 256);
                var windowTitle = titleBuilder.ToString();

                GetWindowThreadProcessId(hwnd, out uint fallbackPid);
                uint realPid = GetRealProcessId(hwnd, fallbackPid);
                Process? process = null;
                try
                {
                    process = Process.GetProcessById((int)realPid);
                }
                catch
                {
                    return; // Process already exited
                }

                var appName = GetFriendlyName(process);
                if (ShouldSkipProcess(process.ProcessName))
                    return;
                lock (_lock)
                {
                    if (appName != _lastAppName)
                    {
                        var now = DateTime.Now;

                        if (_lastAppName != null && _usageMap.TryGetValue(_lastAppName, out var prevRecord))
                        {
                            prevRecord.EndTime = now;
                            prevRecord.TotalTime = prevRecord.EndTime - prevRecord.StartTime;
                            Debug.WriteLine($"📊 {_lastAppName}: {prevRecord.StartTime:T} → {prevRecord.EndTime:T} = {prevRecord.TotalTime.TotalSeconds:F0}s");
                        }

                        _lastAppName = appName;
                        _lastSwitchTime = now;

                        if (!_usageMap.TryGetValue(appName, out var record))
                        {
                            record = new AppUsageRecord
                            {
                                AppName = appName,
                                ProcessName = process.ProcessName,
                                StartTime = now,    
                                EndTime = now,
                                FocusCount = 0
                            };
                            _usageMap[appName] = record;
                        }
                        else
                        {
                            record.StartTime = now;
                        }

                        record.WindowTitle = windowTitle;
                        record.LastSeen = now;
                        record.FocusCount++;

                        AppSwitched?.Invoke(this, record);
                    }
                    else
                    {
                        if (_usageMap.TryGetValue(appName, out var record))
                        {
                            record.EndTime = DateTime.Now;
                            record.TotalTime = record.EndTime - record.StartTime;
                            record.LastSeen = DateTime.Now;
                            record.WindowTitle = windowTitle;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Poll error: {ex.Message}");
            }
        }

        private void AccumulateTime(string appName, TimeSpan duration)
        {
            if (!_usageMap.TryGetValue(appName, out var record))
            {
                record = new AppUsageRecord { AppName = appName };
                _usageMap[appName] = record;
            }
            record.TotalTime += duration;
        }

        private static string GetFriendlyName(Process process)
        {
            try
            {
                var info = process?.MainModule?.FileVersionInfo;
                if (!string.IsNullOrWhiteSpace(info?.ProductName))
                {
                    // ✅ Skip generic Windows product name
                    if (!info.ProductName.Contains("Microsoft® Windows® Operating System",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return info.ProductName;
                    }
                }
            }
            catch { }

            try
            {
                var name = process?.ProcessName ?? "Unknown";
                return name switch
                {
                    "CalculatorApp" => "Calculator",
                    "msedge" => "Microsoft Edge",
                    "chrome" => "Google Chrome",
                    "firefox" => "Firefox",
                    "WINWORD" => "Microsoft Word",
                    "EXCEL" => "Microsoft Excel",
                    "POWERPNT" => "Microsoft PowerPoint",
                    "Teams" => "Microsoft Teams",
                    "Spotify" => "Spotify",
                    "code" => "Visual Studio Code",
                    "devenv" => "Visual Studio",
                    _ => name
                };
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>Returns top N apps sorted by total usage time.</summary>
        public List<AppUsageRecord> GetTopApps(int count = 10)
        {
            lock (_lock)
            {
                // Flush current app's live duration before returning
                if (_lastAppName != null)
                {
                    AccumulateTime(_lastAppName, DateTime.Now - _lastSwitchTime);
                    _lastSwitchTime = DateTime.Now;
                }

                return _usageMap.Values
                    .OrderByDescending(r => r.TotalTime)
                    .Take(count)
                    .ToList();
            }
        }

        public Dictionary<string, AppUsageRecord> GetAllUsage()
        {
            lock (_lock)
            {
                return new Dictionary<string, AppUsageRecord>(_usageMap);
            }
        }
    }
}
