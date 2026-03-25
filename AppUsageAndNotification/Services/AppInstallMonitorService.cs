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

        private static readonly string CacheDir = @"C:\TrayLogs";
        private static readonly string CacheFile = Path.Combine(CacheDir, "apps_cache.json");

        //private static readonly string InstallCacheFile =
        //    Path.Combine(CacheDir, "installed_apps_cache.txt");

        //private static readonly string UninstallCacheFile =
        //    Path.Combine(CacheDir, "uninstalled_apps_cache.txt");

        public AppInstallMonitorService(
            ApiService apiService,
            CommandExecutorService commandExecutor)
        {
            _apiService = apiService;
            _commandExecutor = commandExecutor;

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
        public async Task CheckAndUninstallAppsAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var appsToUninstall = await _apiService
                    .GetUninstalledAppListAsync(AppConfig.UserId);

                if (appsToUninstall.Count == 0)
                {
                    Debug.WriteLine("📋 No apps in uninstall list.");
                    return;
                }

                var cache = LoadCache();

                var pendingApps = appsToUninstall
                    .Where(a => !cache.TryGetValue(a.AppName, out var status) ||
                                status != "uninstalled")
                    .ToList();

                if (pendingApps.Count == 0)
                {
                    Debug.WriteLine("✅ No new apps to uninstall.");
                    return;
                }

                Debug.WriteLine($"🗑️ Apps to uninstall: {string.Join(", ", pendingApps.Select(a => a.AppName))}");

                foreach (var app in pendingApps)
                {
                    string packageId = app.MasterDetails.PackageId;
                    string source = app.MasterDetails.Source;

                    var success = await _commandExecutor
                        .ExecuteUninstallScriptPublicAsync(packageId, source);

                    await _apiService.LogErrorAsync(
                        success ? "App Uninstalled" : "App Uninstall Failed",
                        success
                            ? $"Uninstalled: {app.AppName} ({packageId})"
                            : $"Failed: {app.AppName} ({packageId})");

                    if (success)
                    {
                        cache[app.AppName] = "uninstalled";
                        SaveCache(cache);
                        Debug.WriteLine($"💾 {app.AppName} → uninstalled");
                    }
                }
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("CheckAndUninstallAppsAsync", ex.Message);
            }
        }

        public async Task CheckAndInstallNewAppsAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var appsToInstall = await _apiService
                    .GetInstalledAppListAsync(AppConfig.UserId);

                if (appsToInstall.Count == 0)
                {
                    Debug.WriteLine("📋 No apps in install list.");
                    return;
                }

                var cache = LoadCache();

                var newApps = appsToInstall
                    .Where(a => !cache.TryGetValue(a.AppName, out var status) ||
                                status != "installed")
                    .ToList();

                if (newApps.Count == 0)
                {
                    Debug.WriteLine("✅ No new apps to install.");
                    return;
                }

                Debug.WriteLine($"🆕 New apps: {string.Join(", ", newApps.Select(a => a.AppName))}");

                foreach (var app in newApps)
                {
                    string packageId = app.MasterDetails.PackageId;
                    string source = app.MasterDetails.Source;

                    var success = await _commandExecutor
                        .ExecuteInstallScriptPublicAsync(packageId, null, source);

                    await _apiService.LogErrorAsync(
                        success ? "App Installed" : "App Install Failed",
                        success
                            ? $"Installed: {app.AppName} ({packageId})"
                            : $"Failed: {app.AppName} ({packageId})");

                    if (success)
                    {
                        cache[app.AppName] = "installed";
                        SaveCache(cache);
                        Debug.WriteLine($"💾 {app.AppName} → installed");
                    }
                }
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("CheckAndInstallNewAppsAsync", ex.Message);
            }
        }


        private static Dictionary<string, string> LoadCache()
        {
            try
            {
                if (!File.Exists(CacheFile))
                    return new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);

                var json = File.ReadAllText(CacheFile);
                return System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(json,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        })
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ LoadCache: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveCache(Dictionary<string, string> cache)
        {
            try
            {
                EnsureCacheDirectory();
                var json = System.Text.Json.JsonSerializer.Serialize(cache,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                File.WriteAllText(CacheFile, json);
                Debug.WriteLine($"💾 Cache saved: {CacheFile}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ SaveCache: {ex.Message}");
            }
        }
    }
}
