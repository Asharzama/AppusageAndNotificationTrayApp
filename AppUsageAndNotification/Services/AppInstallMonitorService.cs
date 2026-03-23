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

                // ✅ Fetch master list for packageId and source
                var masterApps = await _apiService.GetMasterAppListAsync();

                var cachedUninstalls = LoadCache(UninstallCacheFile);
                var newUninstalls = appsToUninstall
                    .Except(cachedUninstalls, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (newUninstalls.Count == 0)
                {
                    Debug.WriteLine("✅ No new uninstalls needed.");
                    return;
                }

                Debug.WriteLine($"🗑️ Apps to uninstall: {string.Join(", ", newUninstalls)}");

                foreach (var appName in newUninstalls)
                {
                    string packageId = appName;
                    string source = "chocolaty";

                    var masterApp = masterApps.FirstOrDefault(m =>
                        m.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

                    if (masterApp != null)
                    {
                        source = masterApp.Source;

                        if (!string.IsNullOrWhiteSpace(masterApp.MetaData))
                        {
                            try
                            {
                                //var meta = System.Text.Json.JsonSerializer
                                //    .Deserialize<AppMetaData>(masterApp.MetaData,
                                //        new System.Text.Json.JsonSerializerOptions
                                //        {
                                //            PropertyNameCaseInsensitive = true
                                //        });

                                //// Use uninstall-specific packageId if available
                                //// e.g. AnyDesk uninstalls as "anydesk.portable"
                                //var uninstallPkg = meta?.Uninstall?.Packages
                                //    ?.FirstOrDefault()?.PackageId;

                                //packageId = !string.IsNullOrWhiteSpace(uninstallPkg)
                                //    ? uninstallPkg
                                //    : masterApp.PackageId;

                                Debug.WriteLine($"🗑️ Uninstall packageId: {packageId}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"⚠️ MetaData parse failed: {ex.Message}");
                                packageId = masterApp.PackageId;
                            }
                        }
                        else
                        {
                            packageId = masterApp.PackageId;
                        }

                        Debug.WriteLine($"🗑️ Master app: {appName} " +
                                       $"→ packageId={packageId}, source={source}");
                    }

                    var success = await _commandExecutor
                        .ExecuteUninstallScriptPublicAsync(packageId, source);

                    await _apiService.LogErrorAsync(
                        success ? "App Uninstalled" : "App Uninstall Failed",
                        success
                            ? $"Uninstalled: {appName} (packageId={packageId})"
                            : $"Failed: {appName} (packageId={packageId})");

                    if(success)
                    {
                        var allProcessed = cachedUninstalls
                            .Union(newUninstalls, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        SaveCache(UninstallCacheFile, allProcessed);
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

                var latestApps = await _apiService
                    .GetInstalledAppListAsync(AppConfig.UserId);
                var masterApps = await _apiService.GetMasterAppListAsync();
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
                    string packageId = string.Empty;
                    string? installParams = null;
                    string? source = string.Empty;
                    var masterApp = masterApps.FirstOrDefault(m =>
                        m.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

                    if (masterApp != null)
                    {
                        // ✅ Use packageId from master list
                        packageId = masterApp.PackageId;
                        source = masterApp.Source;
                        // ✅ Extract install params from metadata if available
                        //if (!string.IsNullOrWhiteSpace(masterApp.MetaData))
                        //{
                        //    try
                        //    {
                        //        var meta = System.Text.Json.JsonSerializer
                        //            .Deserialize<AppMetaData>(masterApp.MetaData,
                        //                new System.Text.Json.JsonSerializerOptions
                        //                {
                        //                    PropertyNameCaseInsensitive = true
                        //                });

                        //        installParams = meta?.Install?.Params;

                        //        Debug.WriteLine($"📋 MetaData params: {installParams}");
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        Debug.WriteLine($"⚠️ MetaData parse failed: {ex.Message}");
                        //    }
                        //}

                        Debug.WriteLine($"📦 Master app found: {appName} " +
                                       $"→ packageId={packageId}, params={installParams}");
                        var success = await _commandExecutor.ExecuteInstallScriptPublicAsync(packageId,installParams,source);

                        await _apiService.LogErrorAsync(
                            success ? "App Installed" : "App Install Failed",
                            success
                                ? $"Installed: {appName} (packageId={packageId})"
                                : $"Failed: {appName} (packageId={packageId})");

                        if(success) SaveCache(InstallCacheFile, latestApps);
                    }
                    else
                    {
                        Debug.WriteLine($"📦 No master app found for: {appName} " +
                                       $"— using appName as packageId");
                    }

                    
                }

            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync(
                    "CheckAndInstallNewAppsAsync", ex.Message);
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
