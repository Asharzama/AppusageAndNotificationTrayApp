using AppUsageAndNotification.CommandExecution;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppUsageAndNotification.Services
{
    public class AppInstallMonitorService
    {
        private readonly ApiService _apiService;
        private readonly CommandExecutorService _commandExecutor;

        // ✅ Store in user's AppData\Local\Safe4Sure — always accessible
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Safe4Sure");

        private static readonly string InstallCacheFile =
            Path.Combine(CacheDir, "installed_apps_cache.txt");

        private static readonly string UninstallCacheFile =
            Path.Combine(CacheDir, "uninstalled_apps_cache.txt");

        public AppInstallMonitorService(
            ApiService apiService,
            CommandExecutorService commandExecutor)
        {
            _apiService = apiService;
            _commandExecutor = commandExecutor;

            // ✅ Ensure cache directory exists on startup
            EnsureCacheDirectory();
        }

        private static void EnsureCacheDirectory()
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                {
                    Directory.CreateDirectory(CacheDir);
                    Debug.WriteLine($"✅ Cache dir created: {CacheDir}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ EnsureCacheDirectory: {ex.Message}");
            }
        }

        public async Task CheckAndInstallNewAppsAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var latestApps = await _apiService
                    .GetInstalledAppListAsync(AppConfig.UserId);

                if (latestApps.Count == 0) return;

                var cachedApps = LoadCache(InstallCacheFile);
                var newApps = latestApps
                    .Except(cachedApps, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (newApps.Count == 0)
                {
                    Debug.WriteLine("✅ No new apps to install.");
                    return;
                }

                Debug.WriteLine($"🆕 New apps: {string.Join(", ", newApps)}");

                foreach (var appName in newApps)
                {
                    var success = await _commandExecutor
                        .ExecuteInstallScriptPublicAsync(appName);

                    await _apiService.LogErrorAsync(
                        success ? "App Installed" : "App Install Failed",
                        success
                            ? $"Installed: {appName}"
                            : $"Failed: {appName}");
                }

                SaveCache(InstallCacheFile, latestApps);
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync(
                    "CheckAndInstallNewAppsAsync", ex.Message);
            }
        }

        public async Task CheckAndUninstallAppsAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var appsToUninstall = await _apiService
                    .GetUninstalledAppListAsync(AppConfig.UserId);

                if (appsToUninstall.Count == 0)
                {
                    Debug.WriteLine("✅ No apps to uninstall.");
                    return;
                }

                var cachedUninstalls = LoadCache(UninstallCacheFile);
                var newUninstalls = appsToUninstall
                    .Except(cachedUninstalls, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (newUninstalls.Count == 0)
                {
                    Debug.WriteLine("✅ No new uninstalls needed.");
                    return;
                }

                Debug.WriteLine($"🗑️ Uninstalling: {string.Join(", ", newUninstalls)}");

                foreach (var appName in newUninstalls)
                {
                    var success = await _commandExecutor
                        .ExecuteUninstallScriptPublicAsync(appName);

                    await _apiService.LogErrorAsync(
                        success ? "App Uninstalled" : "App Uninstall Failed",
                        success
                            ? $"Uninstalled: {appName}"
                            : $"Failed: {appName}");
                }

                var allProcessed = cachedUninstalls
                    .Union(newUninstalls, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                SaveCache(UninstallCacheFile, allProcessed);
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync(
                    "CheckAndUninstallAppsAsync", ex.Message);
            }
        }

        private static List<string> LoadCache(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return new List<string>();
                return File.ReadAllLines(filePath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ LoadCache: {ex.Message}");
                return new List<string>();
            }
        }

        private static void SaveCache(string filePath, List<string> items)
        {
            try
            {
                EnsureCacheDirectory();
                File.WriteAllLines(filePath, items);
                Debug.WriteLine($"💾 Cache saved: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ SaveCache: {ex.Message}");
            }
        }
    }
}
