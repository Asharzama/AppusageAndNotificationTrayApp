using AppUsageAndNotification.Services;
using Microsoft.VisualBasic.Devices;
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static AppUsageAndNotification.Services.ApiService;

namespace AppUsageAndNotification.CommandExecution
{
    public class CommandExecutorService
    {
        private readonly ApiService _apiService;
        private static readonly HttpClient _httpClient = new HttpClient();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        [DllImport("kernel32.dll")]
        private static extern int WTSGetActiveConsoleSessionId();

        //[DllImport("wtsapi32.dll", SetLastError = true)]
        //private static extern bool WTSQueryUserToken(int sessionId, out IntPtr Token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
    IntPtr ProcessHandle,
    uint DesiredAccess,
    out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            string lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        private const uint MAXIMUM_ALLOWED = 0x02000000;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize;
            public uint dwXCountChars, dwYCountChars;
            public uint dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        public CommandExecutorService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task CheckAndExecutePendingCommandsAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var commands = await _apiService.GetPendingCommandsAsync(AppConfig.DeviceId);
                if (commands.Count == 0) return;

                Debug.WriteLine($"📋 Found {commands.Count} pending command(s).");
                foreach (var command in commands)
                    await ExecuteCommandAsync(command);
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync(
                    "CheckAndExecutePendingCommandsAsync", ex.Message);
            }
        }

        private async Task ExecuteCommandAsync(WindowCommand command)
        {
            try
            {
                Debug.WriteLine($"⚙️ Executing: {command.ScriptModel?.PolicyName}");

                if (command.ScriptModel == null)
                {
                    await _apiService.LogErrorAsync("Command Failed",
                        $"Command {command.Id} has no script model.");
                    return;
                }

                bool success = false;

                switch (command.ScriptModel.PolicyName?.ToLower())
                {
                    case "remotelockdevice":
                        success = ExecuteLockInActiveSession();
                        break;

                    case "installapp":
                    case "install_app":
                        success = await ExecuteInstallScriptAsync(
                            command.ScriptModel.Parameters);
                        break;

                    case "uninstallapp":
                    case "uninstall_app":
                        success = await ExecuteUninstallScriptAsync(
                            command.ScriptModel.Parameters);
                        break;

                    default:
                        await _apiService.LogErrorAsync("Unknown Policy",
                            $"Command {command.Id} — unknown: {command.ScriptModel.PolicyName}");
                        return;
                }

                if (success)
                {
                    await _apiService.MarkCommandExecutedAsync(command.Id);
                    Debug.WriteLine($"✅ Command {command.Id} done.");
                }
                else
                {
                    await _apiService.LogErrorAsync("Command Failed",
                        $"Command {command.Id} ({command.ScriptModel.PolicyName}) failed.");
                }
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("ExecuteCommandAsync",
                    $"Command {command.Id}: {ex.Message}");
            }
        }

        // ─── Remote Lock ─────────────────────────────────────────────
        private bool ExecuteLockInActiveSession()
        {
            try
            {
                int sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId < 0)
                {
                    _ = _apiService.LogErrorAsync("Lock Failed", "No active session.");
                    return false;
                }

                bool locked = LockWorkStation();
                if (!locked)
                    _ = _apiService.LogErrorAsync("LockWorkStation Failed",
                        $"Win32Error: {Marshal.GetLastWin32Error()}");

                return locked;
            }
            catch (Exception ex)
            {
                _ = _apiService.LogErrorAsync("Lock Exception", ex.Message);
                return false;
            }
        }

        private async Task<bool> ExecuteInstallScriptAsync(
    string? packageId, string? installParams = null, string source = "chocolaty")
        {
            string? tempScriptPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    await _apiService.LogErrorAsync("Install Failed", "PackageId is missing.");
                    return false;
                }

                tempScriptPath = Path.Combine(
                    Path.GetTempPath(),
                    $"{Guid.NewGuid()}_InstallApp.ps1");

                // ✅ Pass source to get correct script
                await File.WriteAllTextAsync(
                    tempScriptPath,
                    GetInstallScript(source),
                    System.Text.Encoding.UTF8);

                Debug.WriteLine($"📦 Installing: {packageId} source={source} params={installParams}");

                // ✅ Winget uses -AppId, Chocolatey uses -AppName
                var isWinget = source.Equals("winget", StringComparison.OrdinalIgnoreCase);
                var paramName = isWinget ? "AppId" : "AppName";
                var extraParam = isWinget ? "CustomArgs" : "Params";

                var arguments = $"-ExecutionPolicy Bypass -NonInteractive " +
                               $"-WindowStyle Hidden " +
                               $"-File \"{tempScriptPath}\" " +
                               $"-{paramName} \"{packageId}\"";

                if (!string.IsNullOrWhiteSpace(installParams))
                    arguments += $" -{extraParam} \"{installParams}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                var completed = await Task.Run(
                    () => process.WaitForExit(10 * 60 * 1000));

                if (!completed)
                {
                    process.Kill();
                    await _apiService.LogErrorAsync("Install Timeout", $"{packageId} timed out.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(output))
                    Debug.WriteLine($"📤 Output: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.WriteLine($"⚠️ Error: {error}");

                await _apiService.LogErrorAsync(
                    process.ExitCode == 0 ? "App Installed" : "App Install Failed",
                    process.ExitCode == 0
                        ? $"Installed: {packageId} via {source}"
                        : $"Failed (exit={process.ExitCode}): {packageId}\n{error}");

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("ExecuteInstallScriptAsync",
                    $"{packageId}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempScriptPath) &&
                    File.Exists(tempScriptPath))
                    File.Delete(tempScriptPath);
            }
        }

        public async Task<bool> ExecuteInstallScriptPublicAsync(
            string packageId, string? installParams = null, string source = "chocolaty")
            => await ExecuteInstallScriptAsync(packageId, installParams, source);

        public async Task<bool> IsAppInstalledAsync(string packageId, string source)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "winget",
                        Arguments = $"list --id {packageId} --source {source} --accept-source-agreements",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                return output.Contains(packageId, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ IsAppInstalledAsync error: {ex.Message}");
                return false;
            }
        }
        private async Task<bool> ExecuteUninstallScriptAsync(
    string? packageId, string source = "chocolaty")
        {
            string? tempScriptPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    await _apiService.LogErrorAsync("Uninstall Failed", "PackageId is missing.");
                    return false;
                }

                tempScriptPath = Path.Combine(
                    Path.GetTempPath(),
                    $"{Guid.NewGuid()}_UninstallApp.ps1");

                await File.WriteAllTextAsync(
                    tempScriptPath,
                    GetUninstallScript(source),
                    System.Text.Encoding.UTF8);

                Debug.WriteLine($"🗑️ Uninstalling: {packageId} source={source}");

                // ✅ Winget uses -AppId, Chocolatey uses -AppName
                var isWinget = source.Equals("winget", StringComparison.OrdinalIgnoreCase);
                var paramName = isWinget ? "AppId" : "AppName";

                var arguments = $"-ExecutionPolicy Bypass -NonInteractive " +
                               $"-WindowStyle Hidden " +
                               $"-File \"{tempScriptPath}\" " +
                               $"-{paramName} \"{packageId}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                var completed = await Task.Run(
                    () => process.WaitForExit(10 * 60 * 1000));

                if (!completed)
                {
                    process.Kill();
                    await _apiService.LogErrorAsync("Uninstall Timeout", $"{packageId} timed out.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(output))
                    Debug.WriteLine($"📤 Output: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.WriteLine($"⚠️ Error: {error}");

                await _apiService.LogErrorAsync(
                    process.ExitCode == 0 ? "App Uninstalled" : "App Uninstall Failed",
                    process.ExitCode == 0
                        ? $"Uninstalled: {packageId} via {source}"
                        : $"Failed (exit={process.ExitCode}): {packageId}\n{error}");

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("ExecuteUninstallScriptAsync",
                    $"{packageId}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempScriptPath) &&
                    File.Exists(tempScriptPath))
                    File.Delete(tempScriptPath);
            }
        }

        public async Task<bool> ExecuteUninstallScriptPublicAsync(
            string packageId, string source = "chocolaty")
            => await ExecuteUninstallScriptAsync(packageId, source);

        // ─── Embedded PS1 Script ─────────────────────────────────────
        private static string GetInstallScript(string source = "chocolaty")
        {
            // ✅ Winget script
            if (source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new[]
                {
            "param(",
            "    [Parameter(Mandatory=$true)]",
            "    [string]$AppId,",
            "    [Parameter(Mandatory=$false)]",
            "    [string]$CustomArgs",
            ")",
            "",
            "if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {",
            "    Write-Host 'winget is not available on this system.'",
            "    exit 1",
            "}",
            "",
            "$cmd = 'winget install --id ' + $AppId + ' --silent --accept-package-agreements --accept-source-agreements'",
            "",
            "if ($CustomArgs -and $CustomArgs.Trim() -ne '') {",
            "    $cmd += ' --override ' + $CustomArgs",
            "}",
            "",
            "Write-Host ('Executing: ' + $cmd)",
            "Invoke-Expression $cmd",
            "",
            "if ($LASTEXITCODE -eq 0) {",
            "    Write-Host ($AppId + ' installed successfully!')",
            "} else {",
            "    Write-Host ('Failed to install ' + $AppId)",
            "    exit 1",
            "}",
        };
                return string.Join(Environment.NewLine, lines);
            }

            // ✅ Chocolatey script (default)
            var chocoLines = new[]
            {
        "param(",
        "    [Parameter(Mandatory=$true)]",
        "    [string]$AppName,",
        "    [Parameter(Mandatory=$false)]",
        "    [string]$Params",
        ")",
        "",
        "# Fix Chocolatey permissions",
        "$chocoDir = 'C:\\ProgramData\\chocolatey'",
        "if (Test-Path $chocoDir) {",
        "    $subDirs = @(",
        "        'C:\\ProgramData\\chocolatey\\.chocolatey',",
        "        'C:\\ProgramData\\chocolatey\\lib',",
        "        'C:\\ProgramData\\chocolatey\\logs'",
        "    )",
        "    foreach ($dir in $subDirs) {",
        "        if (!(Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }",
        "        icacls $dir /grant 'Everyone:(OI)(CI)F' /T /Q 2>&1 | Out-Null",
        "    }",
        "    $libDir = 'C:\\ProgramData\\chocolatey\\lib'",
        "    if (Test-Path $libDir) {",
        "        Get-ChildItem -Path $libDir -Filter '*.lock' -Recurse -ErrorAction SilentlyContinue |",
        "            Remove-Item -Force -ErrorAction SilentlyContinue",
        "        Get-ChildItem -Path $libDir -Directory -ErrorAction SilentlyContinue |",
        "            Where-Object { $_.Name -match '^[a-f0-9]{40}$' } |",
        "            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue",
        "    }",
        "}",
        "",
        "if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {",
        "    Write-Host 'Chocolatey not found. Installing...'",
        "    Set-ExecutionPolicy Bypass -Scope Process -Force",
        "    [System.Net.ServicePointManager]::SecurityProtocol = 3072",
        "    iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))",
        "}",
        "",
        "choco feature enable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null",
        "",
        "$installCommand = 'choco install ' + $AppName + ' -y --no-progress --force --ignore-checksums'",
        "",
        "if ($Params -and $Params.Trim() -ne '') {",
        "    $installCommand += ' --params=' + $Params",
        "}",
        "",
        "Write-Host ('Executing: ' + $installCommand)",
        "Invoke-Expression $installCommand",
        "",
        "choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null",
        "",
        "if ($LASTEXITCODE -eq 0) {",
        "    Write-Host ($AppName + ' installed successfully!')",
        "} else {",
        "    Write-Host 'Retrying after permission fix...'",
        "    icacls 'C:\\ProgramData\\chocolatey' /grant 'Everyone:(OI)(CI)F' /T /Q 2>&1 | Out-Null",
        "    Invoke-Expression $installCommand",
        "    if ($LASTEXITCODE -eq 0) {",
        "        Write-Host ($AppName + ' installed successfully on retry!')",
        "    } else {",
        "        Write-Host ('Failed to install ' + $AppName)",
        "        exit 1",
        "    }",
        "}",
    };
            return string.Join(Environment.NewLine, chocoLines);
        }
        private static string GetUninstallScript(string source = "chocolaty")
        {
            // ✅ Winget uninstall
            if (source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new[]
                {
            "param(",
            "    [Parameter(Mandatory=$true)]",
            "    [string]$AppId",
            ")",
            "",
            "if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {",
            "    Write-Host 'winget is not available on this system.'",
            "    exit 1",
            "}",
            "",
            "$cmd = 'winget uninstall --id ' + $AppId + ' --silent --accept-source-agreements --force'",
            "",
            "Write-Host ('Executing: ' + $cmd)",
            "Invoke-Expression $cmd",
            "",
            "if ($LASTEXITCODE -eq 0) {",
            "    Write-Host ($AppId + ' uninstalled successfully!')",
            "} else {",
            "    Write-Host ('Failed to uninstall ' + $AppId)",
            "    exit 1",
            "}",
        };
                return string.Join(Environment.NewLine, lines);
            }

            // ✅ Chocolatey uninstall (default)
            var chocoLines = new[]
            {
        "param(",
        "    [Parameter(Mandatory=$true)]",
        "    [string]$AppName",
        ")",
        "",
        "if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {",
        "    Write-Host 'Chocolatey is not installed. Cannot uninstall.'",
        "    exit 1",
        "}",
        "",
        "# Fix permissions before uninstall",
        "$chocoDir = 'C:\\ProgramData\\chocolatey'",
        "if (Test-Path $chocoDir) {",
        "    $subDirs = @(",
        "        'C:\\ProgramData\\chocolatey\\.chocolatey',",
        "        'C:\\ProgramData\\chocolatey\\lib',",
        "        'C:\\ProgramData\\chocolatey\\logs'",
        "    )",
        "    foreach ($dir in $subDirs) {",
        "        if (!(Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }",
        "        icacls $dir /grant 'Everyone:(OI)(CI)F' /T /Q 2>&1 | Out-Null",
        "    }",
        "    $libDir = 'C:\\ProgramData\\chocolatey\\lib'",
        "    if (Test-Path $libDir) {",
        "        Get-ChildItem -Path $libDir -Filter '*.lock' -Recurse -ErrorAction SilentlyContinue |",
        "            Remove-Item -Force -ErrorAction SilentlyContinue",
        "        Get-ChildItem -Path $libDir -Directory -ErrorAction SilentlyContinue |",
        "            Where-Object { $_.Name -match '^[a-f0-9]{40}$' } |",
        "            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue",
        "    }",
        "}",
        "",
        "choco feature enable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null",
        "",
        "Write-Host ('Uninstalling ' + $AppName + ' silently...')",
        "choco uninstall $AppName -y --no-progress --force --remove-dependencies",
        "",
        "choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null",
        "",
        "if ($LASTEXITCODE -eq 0) {",
        "    Write-Host ($AppName + ' uninstalled successfully!')",
        "} else {",
        "    Write-Host 'Retrying after permission fix...'",
        "    icacls 'C:\\ProgramData\\chocolatey' /grant 'Everyone:(OI)(CI)F' /T /Q 2>&1 | Out-Null",
        "    choco uninstall $AppName -y --no-progress --force",
        "    if ($LASTEXITCODE -eq 0) {",
        "        Write-Host ($AppName + ' uninstalled successfully on retry!')",
        "    } else {",
        "        Write-Host ('Failed to uninstall ' + $AppName)",
        "        exit 1",
        "    }",
        "}",
    };
            return string.Join(Environment.NewLine, chocoLines);
        }

    }
}
