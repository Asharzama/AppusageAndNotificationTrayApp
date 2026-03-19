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
    string? packageId, string? installParams = null)
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

                await File.WriteAllTextAsync(
                    tempScriptPath,
                    GetInstallScript(),
                    System.Text.Encoding.UTF8);

                Debug.WriteLine($"📦 Installing: {packageId} with params: {installParams}");

                // ✅ Build arguments — include Params only if available
                var arguments = $"-ExecutionPolicy Bypass -NonInteractive " +
                               $"-WindowStyle Hidden " +
                               $"-File \"{tempScriptPath}\" " +
                               $"-AppName \"{packageId}\"";

                if (!string.IsNullOrWhiteSpace(installParams))
                    arguments += $" -Params \"{installParams}\"";

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
                    await _apiService.LogErrorAsync("Install Timeout",
                        $"{packageId} timed out.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(output))
                    Debug.WriteLine($"📤 Output: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.WriteLine($"⚠️ Error: {error}");

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
    string packageId, string? installParams = null)
    => await ExecuteInstallScriptAsync(packageId, installParams);

        private async Task<bool> ExecuteUninstallScriptAsync(string? appName)
        {
            string? tempScriptPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(appName))
                {
                    await _apiService.LogErrorAsync("Uninstall Failed", "AppName is missing.");
                    return false;
                }

                tempScriptPath = Path.Combine(
                    Path.GetTempPath(), $"{Guid.NewGuid()}_UninstallApp.ps1");

                return true;
 
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("ExecuteUninstallScriptAsync",
                    $"{appName}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempScriptPath) && File.Exists(tempScriptPath))
                    File.Delete(tempScriptPath);
            }
        }


        private async Task<int> RunProcessAsync(string fileName, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ RunProcessAsync ({fileName}): {ex.Message}");
                return -1;
            }
        }

        // ─── Embedded Uninstall PS1 ───────────────────────────────────
        private static string GetUninstallScriptContent() => @"
param(
    [Parameter(Mandatory=$true)]
    [string]$AppName
)

# ============================================================
# SUPPRESS ALL CONFIRMATIONS AND PROGRESS
# ============================================================
$ConfirmPreference        = 'None'
$ErrorActionPreference    = 'Continue'
$ProgressPreference       = 'SilentlyContinue'
$WarningPreference        = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ============================================================
# LOGGING
# ============================================================
$LogDir  = 'C:\Logs'
$LogFile = ""$LogDir\uninstall_$($AppName)_$(Get-Date -Format 'yyyyMMdd_HHmmss').log""
if (!(Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $ts   = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = ""[$ts] [$Level] $Message""
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
    Write-Host $line
}

Write-Log ""=== UNINSTALL START === Target: $AppName""
Write-Log ""PS $($PSVersionTable.PSVersion) | User: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)""

# ============================================================
# STEP 0: Kill matching processes
# ============================================================
Write-Log 'STEP 0: Killing processes...'
try {
    $procs = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like ""*$AppName*"" -or $_.MainWindowTitle -like ""*$AppName*"" }
    foreach ($p in $procs) {
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Write-Log ""  Killed: $($p.Name) (PID=$($p.Id))"" 'SUCCESS'
        } catch {
            Write-Log ""  Could not kill $($p.Name): $_"" 'WARN'
        }
    }
    if (-not $procs) { Write-Log '  No matching processes found' 'INFO' }
} catch {
    Write-Log ""  STEP 0 error: $_"" 'ERROR'
}

# ============================================================
# STEP 1: AppX / Store App
# ============================================================
Write-Log 'STEP 1: Store apps (AppX)...'
try {
    $store = Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like ""*$AppName*"" -or $_.PackageFamilyName -like ""*$AppName*"" }
    if ($store) {
        foreach ($a in $store) {
            try {
                Remove-AppxPackage -Package $a.PackageFullName -AllUsers -ErrorAction Stop
                Write-Log ""  Removed AppX: $($a.PackageFullName)"" 'SUCCESS'
            } catch {
                Write-Log ""  AppX removal failed: $_"" 'ERROR'
            }
        }
        # Also remove provisioned package
        $prov = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like ""*$AppName*"" }
        foreach ($pp in $prov) {
            try {
                Remove-AppxProvisionedPackage -Online -PackageName $pp.PackageName -ErrorAction Stop | Out-Null
                Write-Log ""  Removed provisioned: $($pp.PackageName)"" 'SUCCESS'
            } catch {
                Write-Log ""  Provisioned removal failed: $_"" 'WARN'
            }
        }
    } else {
        Write-Log '  No Store apps found' 'INFO'
    }
} catch {
    Write-Log ""  STEP 1 error: $_"" 'ERROR'
}

# ============================================================
# STEP 2: Winget
# ============================================================
Write-Log 'STEP 2: Winget uninstall...'
try {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        # Try exact name first
        $wOut = winget uninstall `
            --name $AppName `
            --silent `
            --force `
            --disable-interactivity `
            --accept-source-agreements 2>&1
        Write-Log ""  Winget output: $wOut""

        if ($LASTEXITCODE -eq 0) {
            Write-Log '  Winget succeeded' 'SUCCESS'
        } else {
            # Try with --id as fallback
            Write-Log ""  Trying winget with --id..."" 'WARN'
            $wOut2 = winget uninstall `
                --id $AppName `
                --silent `
                --force `
                --disable-interactivity `
                --accept-source-agreements 2>&1
            Write-Log ""  Winget --id output: $wOut2""
            if ($LASTEXITCODE -eq 0) {
                Write-Log '  Winget --id succeeded' 'SUCCESS'
            } else {
                Write-Log ""  Winget failed (exit=$LASTEXITCODE)"" 'WARN'
            }
        }
    } else {
        Write-Log '  Winget not available' 'WARN'
    }
} catch {
    Write-Log ""  STEP 2 error: $_"" 'ERROR'
}

# ============================================================
# STEP 3: Chocolatey
# ============================================================
Write-Log 'STEP 3: Chocolatey uninstall...'
try {
    $chocoCmd = Get-Command choco -ErrorAction SilentlyContinue
    if ($chocoCmd) {

        # ✅ Clear stale lock files first
        $chocoLib = 'C:\ProgramData\chocolatey\lib'
        if (Test-Path $chocoLib) {
            Get-ChildItem -Path $chocoLib -Filter '*.lock' -Recurse -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue
            Get-ChildItem -Path $chocoLib -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^[a-f0-9]{40}$' } |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log '  Cleared Chocolatey lock files' 'INFO'
        }

        # ✅ Enable global confirmation to suppress prompts
        choco feature enable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null

        # ✅ Set env to suppress interactive prompts
        $env:ChocolateyEnvironmentDebug   = 'false'
        $env:ChocolateyEnvironmentVerbose = 'false'
        [System.Environment]::SetEnvironmentVariable('CHOCOLATEY_NO_COLOR', 'true', 'Process')

        $chocoArgs = @(
            'uninstall', $AppName,
            '-y',
            '--yes',
            '--force',
            '--no-progress',
            '--remove-dependencies',
            '--ignore-checksums',
            '--no-color',
            '--confirm',
            '--timeout', '600'
        )

        $cOut = & choco @chocoArgs 2>&1
        Write-Log ""  Choco output: $cOut""

        if ($LASTEXITCODE -eq 0) {
            Write-Log '  Chocolatey succeeded' 'SUCCESS'
        } else {
            Write-Log ""  Chocolatey failed (exit=$LASTEXITCODE)"" 'WARN'
        }

        # ✅ Restore confirmation setting
        choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null

    } else {
        Write-Log '  Chocolatey not available' 'WARN'
    }
} catch {
    Write-Log ""  STEP 3 error: $_"" 'ERROR'
}

# ============================================================
# STEP 4: Registry EXE/MSI uninstall with popup auto-dismisser
# ============================================================
Write-Log 'STEP 4: Registry uninstall...'

# ✅ Background job to auto-dismiss any confirmation dialogs
function Start-PopupWatcher {
    $job = Start-Job -ScriptBlock {
        try {
            $shell = New-Object -ComObject WScript.Shell
            $end   = (Get-Date).AddSeconds(180)
            $dismissTitles = @(
                '*uninstall*', '*confirm*', '*warning*',
                '*remove*', '*delete*', '*yes*', '*are you sure*'
            )
            while ((Get-Date) -lt $end) {
                $wins = Get-Process -ErrorAction SilentlyContinue |
                    Where-Object { $_.MainWindowTitle -ne '' }
                foreach ($w in $wins) {
                    $t = $w.MainWindowTitle.ToLower()
                    $isDialog = $false
                    foreach ($pattern in $dismissTitles) {
                        if ($t -like $pattern) { $isDialog = $true; break }
                    }
                    if ($isDialog) {
                        try {
                            $null = $shell.AppActivate($w.Id)
                            Start-Sleep -Milliseconds 200
                            $shell.SendKeys('{ENTER}')
                            Start-Sleep -Milliseconds 100
                            # Also try Y key as fallback
                            $shell.SendKeys('Y')
                            $shell.SendKeys('{ENTER}')
                        } catch { }
                    }
                }
                Start-Sleep -Milliseconds 300
            }
        } catch { }
    }
    return $job
}

try {
    $regPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    $found = $false
    foreach ($rp in $regPaths) {
        $entries = Get-ItemProperty $rp -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like ""*$AppName*"" }

        foreach ($entry in $entries) {
            $found = $true
            Write-Log ""  Found registry entry: $($entry.DisplayName)""

            # ✅ Prefer quiet uninstall string
            $uStr = $entry.QuietUninstallString
            if (-not $uStr) { $uStr = $entry.UninstallString }
            if (-not $uStr) {
                Write-Log '  No uninstall string found' 'WARN'
                continue
            }

            Write-Log ""  Uninstall string: $uStr""

            if ($uStr -match 'msiexec') {
                # ✅ MSI silent uninstall — no prompts
                $null = $uStr -match '\{[A-F0-9\-]+\}'
                $code = $Matches[0]
                Write-Log ""  MSI product code: $code""
                $msiResult = Start-Process 'msiexec.exe' `
                    -ArgumentList ""/x $code /qn /norestart REBOOT=ReallySuppress"" `
                    -Wait `
                    -WindowStyle Hidden `
                    -PassThru
                Write-Log ""  MSI exit code: $($msiResult.ExitCode)"" $(if ($msiResult.ExitCode -eq 0) {'SUCCESS'} else {'WARN'})
            } else {
                # ✅ EXE uninstall with popup watcher
                if ($uStr -match '""') {
                    $exePath = ($uStr -split '""')[1]
                } else {
                    $exePath = ($uStr -split ' ')[0]
                }

                Write-Log ""  EXE path: $exePath""

                if (-not (Test-Path $exePath -ErrorAction SilentlyContinue)) {
                    Write-Log ""  EXE not found: $exePath"" 'WARN'
                    continue
                }

                # ✅ Start popup dismisser
                $wJob = Start-PopupWatcher

                $exeResult = Start-Process -FilePath $exePath `
                    -ArgumentList '/S /s /silent /verysilent /quiet /norestart /qn' `
                    -Wait `
                    -WindowStyle Hidden `
                    -PassThru

                Stop-Job   -Job $wJob -ErrorAction SilentlyContinue
                Remove-Job -Job $wJob -Force -ErrorAction SilentlyContinue

                Write-Log ""  EXE exit code: $($exeResult.ExitCode)"" $(if ($exeResult.ExitCode -eq 0) {'SUCCESS'} else {'WARN'})
            }
        }
    }
    if (-not $found) { Write-Log '  Not found in registry' 'INFO' }
} catch {
    Write-Log ""  STEP 4 error: $_"" 'ERROR'
}

# ============================================================
# STEP 5: Delete leftover folders
# ============================================================
Write-Log 'STEP 5: Leftover folders...'
try {
    $roots = @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramData,
        $env:APPDATA,
        $env:LOCALAPPDATA
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $dirs = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like ""*$AppName*"" }
        foreach ($d in $dirs) {
            try {
                # ✅ Take ownership and grant full access before deleting
                & takeown /f ""$($d.FullName)"" /r /d y 2>&1 | Out-Null
                & icacls ""$($d.FullName)"" /grant ""$($env:USERNAME):F"" /t /q 2>&1 | Out-Null
                & icacls ""$($d.FullName)"" /grant ""Administrators:F"" /t /q 2>&1 | Out-Null
                Remove-Item -Path $d.FullName -Recurse -Force -ErrorAction Stop
                Write-Log ""  Deleted: $($d.FullName)"" 'SUCCESS'
            } catch {
                Write-Log ""  Could not delete $($d.FullName): $_"" 'ERROR'
            }
        }
    }
} catch {
    Write-Log ""  STEP 5 error: $_"" 'ERROR'
}

# ============================================================
# STEP 6: Remove leftover registry keys
# ============================================================
Write-Log 'STEP 6: Leftover registry keys...'
try {
    $regRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $regRoots) {
        if (-not (Test-Path $root)) { continue }
        $keys = Get-ChildItem -Path $root -ErrorAction SilentlyContinue |
            Where-Object {
                (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName `
                    -like ""*$AppName*""
            }
        foreach ($k in $keys) {
            try {
                Remove-Item -Path $k.PSPath -Recurse -Force -ErrorAction Stop
                Write-Log ""  Removed key: $($k.PSPath)"" 'SUCCESS'
            } catch {
                Write-Log ""  Failed key: $($k.PSPath): $_"" 'ERROR'
            }
        }
    }
} catch {
    Write-Log ""  STEP 6 error: $_"" 'ERROR'
}

# ============================================================
# STEP 7: Remove leftover shortcuts
# ============================================================
Write-Log 'STEP 7: Leftover shortcuts...'
try {
    $lnkRoots = @(
        ""$env:PUBLIC\Desktop"",
        ""$env:USERPROFILE\Desktop"",
        ""$env:APPDATA\Microsoft\Windows\Start Menu\Programs"",
        'C:\ProgramData\Microsoft\Windows\Start Menu\Programs',
        ""$env:APPDATA\Microsoft\Internet Explorer\Quick Launch"",
        ""$env:USERPROFILE\AppData\Roaming\Microsoft\Windows\Start Menu\Programs""
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($dir in $lnkRoots) {
        $links = Get-ChildItem -Path $dir -Filter '*.lnk' `
            -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like ""*$AppName*"" }
        foreach ($lnk in $links) {
            try {
                Remove-Item -Path $lnk.FullName -Force -ErrorAction Stop
                Write-Log ""  Removed shortcut: $($lnk.FullName)"" 'SUCCESS'
            } catch {
                Write-Log ""  Shortcut failed: $_"" 'ERROR'
            }
        }
    }
} catch {
    Write-Log ""  STEP 7 error: $_"" 'ERROR'
}

# ============================================================
# STEP 8: Refresh Windows environment
# ============================================================
Write-Log 'STEP 8: Refreshing environment...'
try {
    # ✅ Notify Windows Explorer to refresh
    $null = [System.Runtime.InteropServices.Marshal]::GetActiveObject('Shell.Application')
    Write-Log '  Environment refreshed' 'SUCCESS'
} catch {
    Write-Log '  Refresh skipped (non-critical)' 'INFO'
}

Write-Log ""=== UNINSTALL COMPLETE === Log: $LogFile"" 'SUCCESS'
exit 0
";

        // ─── Embedded PS1 Script ─────────────────────────────────────
        private static string GetInstallScript()
        {
            var lines = new[]
            {
        "param(",
        "    [Parameter(Mandatory=$true)]",
        "    [string]$AppName,",
        "    [Parameter(Mandatory=$false)]",
        "    [string]$Params",
        ")",
        "",
        "if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {",
        "    Write-Host 'Chocolatey not found. Installing...'",
        "    Set-ExecutionPolicy Bypass -Scope Process -Force",
        "    [System.Net.ServicePointManager]::SecurityProtocol = 3072",
        "    iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))",
        "    refreshenv",
        "}",
        "",
        "$installCommand = 'choco install ' + $AppName + ' -y --no-progress --force'",
        "",
        "if ($Params -and $Params.Trim() -ne '') {",
        "    $installCommand += ' --params=' + $Params",
        "}",
        "",
        "Write-Host ('Executing: ' + $installCommand)",
        "Invoke-Expression $installCommand",
        "",
        "if ($LASTEXITCODE -eq 0) {",
        "    Write-Host ($AppName + ' installed successfully!')",
        "} else {",
        "    Write-Host ('Failed to install ' + $AppName)",
        "    exit 1",
        "}",
    };

            return string.Join(Environment.NewLine, lines);
        }

        private static string GetInstallScriptContent() => @"
param(
    [Parameter(Mandatory=$true)]
    [string]$AppName
)

# ============================================================
# SUPPRESS ALL CONFIRMATIONS AND PROGRESS
# ============================================================
$ConfirmPreference        = 'None'
$ErrorActionPreference    = 'Continue'
$ProgressPreference       = 'SilentlyContinue'
$WarningPreference        = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ============================================================
# LOGGING
# ============================================================
$LogDir  = 'C:\Logs'
$LogFile = ""$LogDir\AppInstall_$($AppName)_$(Get-Date -Format 'yyyyMMdd_HHmmss').log""
if (!(Test-Path $LogDir)) {
    New-Item -Path $LogDir -ItemType Directory -Force | Out-Null
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $ts   = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = ""[$ts] [$Level] $Message""
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
    Write-Host $line
}

Write-Log ""=== INSTALL START === Target: $AppName""
Write-Log ""PS $($PSVersionTable.PSVersion) | User: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)""

# ============================================================
# ENABLE WINGET POLICY
# ============================================================
try {
    $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppInstaller'
    if (!(Test-Path $policyPath)) {
        New-Item -Path $policyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $policyPath -Name EnableAppInstaller -Value 1 -Type DWord -Force
    Write-Log 'Winget policy enabled' 'INFO'
} catch {
    Write-Log ""Policy set failed: $_"" 'WARN'
}

# ============================================================
# STEP 1: Winget
# ============================================================
Write-Log 'STEP 1: Trying Winget...'
try {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Log 'Winget detected'

        $productId  = $null
        $sourceHint = $null

        # ── Search exact ──────────────────────────────────────
        Write-Log ""Searching exact: $AppName""
        $searchOutput = winget search $AppName `
            --exact `
            --accept-source-agreements `
            --disable-interactivity 2>&1

        foreach ($line in $searchOutput) {
            if ($line -match 'msstore') {
                $tokens = $line -split '\s{2,}'
                if ($tokens.Count -ge 2) {
                    $productId  = $tokens[1].Trim()
                    $sourceHint = 'msstore'
                    Write-Log ""  MS Store ID: $productId"" 'INFO'
                    break
                }
            }
        }

        # ── Show to extract ID if not found via search ────────
        if (-not $productId) {
            $showOutput = winget show $AppName `
                --accept-source-agreements `
                --disable-interactivity 2>&1
            foreach ($line in $showOutput) {
                if ($line -match '\[(.*?)\]') {
                    $productId  = $Matches[1]
                    $sourceHint = 'winget'
                    Write-Log ""  Winget ID: $productId"" 'INFO'
                    break
                }
            }
        }

        # ── Fuzzy search fallback ─────────────────────────────
        if (-not $productId) {
            Write-Log '  Exact not found — trying fuzzy search' 'WARN'
            $fuzzyOutput = winget search $AppName `
                --accept-source-agreements `
                --disable-interactivity 2>&1
            foreach ($line in $fuzzyOutput) {
                if ($line -match 'msstore') {
                    $tokens = $line -split '\s{2,}'
                    if ($tokens.Count -ge 2) {
                        $productId  = $tokens[1].Trim()
                        $sourceHint = 'msstore'
                        Write-Log ""  Fuzzy MS Store ID: $productId"" 'INFO'
                        break
                    }
                }
                if ($line -match '\[(.*?)\]') {
                    $productId  = $Matches[1]
                    $sourceHint = 'winget'
                    Write-Log ""  Fuzzy Winget ID: $productId"" 'INFO'
                    break
                }
            }
        }

        if ($productId) {
            if ($sourceHint -eq 'msstore') {
                Write-Log ""Installing via Winget MS Store: $productId"" 'INFO'
                $wResult = winget install `
                    --id $productId `
                    --source msstore `
                    --silent `
                    --accept-package-agreements `
                    --accept-source-agreements `
                    --disable-interactivity `
                    --force 2>&1
                Write-Log ""  Winget output: $wResult""
            } else {
                Write-Log ""Installing via Winget (machine scope): $productId"" 'INFO'
                $wResult = winget install `
                    --id $productId `
                    --scope machine `
                    --silent `
                    --accept-package-agreements `
                    --accept-source-agreements `
                    --disable-interactivity `
                    --force 2>&1
                Write-Log ""  Winget output: $wResult""
            }

            if ($LASTEXITCODE -eq 0) {
                Write-Log ""SUCCESS: Installed via Winget"" 'SUCCESS'

                # ── Provision MS Store app for all users ──────
                if ($sourceHint -eq 'msstore') {
                    Write-Log 'Provisioning for all users...' 'INFO'
                    try {
                        $appxPkg = Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
                            Where-Object {
                                $_.PackageFamilyName -match [regex]::Escape($productId) -or
                                $_.Name -match [regex]::Escape($AppName)
                            } | Select-Object -First 1

                        if ($appxPkg) {
                            $manifestPath = Join-Path $appxPkg.InstallLocation 'AppxManifest.xml'
                            if (Test-Path $manifestPath) {
                                Add-AppxPackage `
                                    -Path $manifestPath `
                                    -Register `
                                    -DisableDevelopmentMode `
                                    -ErrorAction SilentlyContinue
                                try {
                                    Add-AppxProvisionedPackage `
                                        -Online `
                                        -PackagePath $manifestPath `
                                        -SkipLicense `
                                        -ErrorAction SilentlyContinue | Out-Null
                                    Write-Log '  Provisioned for future users' 'SUCCESS'
                                } catch {
                                    if ($_ -match '0x8051100f') {
                                        Write-Log '  Already provisioned - OK' 'INFO'
                                    } else {
                                        Write-Log ""  Provision skipped: $_"" 'WARN'
                                    }
                                }
                            }
                        }
                    } catch {
                        Write-Log ""  Provision error: $_"" 'WARN'
                    }
                }

                Write-Log ""=== INSTALL COMPLETE === Log: $LogFile"" 'SUCCESS'
                exit 0
            } else {
                Write-Log ""  Winget failed (exit=$LASTEXITCODE) — trying Chocolatey"" 'WARN'
            }
        } else {
            Write-Log '  No Winget ID found — trying Chocolatey' 'WARN'
        }
    } else {
        Write-Log '  Winget not available' 'WARN'
    }
} catch {
    Write-Log ""  STEP 1 error: $_"" 'ERROR'
}

# ============================================================
# STEP 2: Chocolatey
# ============================================================
Write-Log 'STEP 2: Trying Chocolatey...'
try {
    $chocoCmd = Get-Command choco -ErrorAction SilentlyContinue

    if (-not $chocoCmd) {
        Write-Log '  Installing Chocolatey...' 'INFO'
        try {
            Set-ExecutionPolicy Bypass -Scope Process -Force
            [System.Net.ServicePointManager]::SecurityProtocol =
                [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
            Invoke-Expression ((New-Object System.Net.WebClient).DownloadString(
                'https://community.chocolatey.org/install.ps1'))
            Start-Sleep 5
            Write-Log '  Chocolatey installed' 'SUCCESS'
        } catch {
            Write-Log ""  Chocolatey install failed: $_"" 'ERROR'
        }
    }

    $env:Path += ';C:\ProgramData\chocolatey\bin'
    $chocoCmd = Get-Command choco -ErrorAction SilentlyContinue

    if ($chocoCmd) {

        # ✅ Clear stale lock files
        $chocoLib = 'C:\ProgramData\chocolatey\lib'
        if (Test-Path $chocoLib) {
            Get-ChildItem -Path $chocoLib -Filter '*.lock' -Recurse -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue
            Get-ChildItem -Path $chocoLib -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^[a-f0-9]{40}$' } |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log '  Cleared Chocolatey lock files' 'INFO'
        }

        # ✅ Suppress all prompts
        choco feature enable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null
        $env:ChocolateyEnvironmentDebug   = 'false'
        $env:ChocolateyEnvironmentVerbose = 'false'
        [System.Environment]::SetEnvironmentVariable('CHOCOLATEY_NO_COLOR', 'true', 'Process')

        # ── Search exact first ────────────────────────────────
        Write-Log ""  Searching Chocolatey (exact): $AppName"" 'INFO'
        $pkg = $null
        $chocoSearch = choco search $AppName --exact --no-progress 2>&1
        foreach ($line in $chocoSearch) {
            if ($line -match ""^$AppName\s"") {
                $pkg = $line.Split(' ')[0]
                Write-Log ""  Found exact: $pkg"" 'INFO'
                break
            }
        }

        # ── Fuzzy search fallback ─────────────────────────────
        if (-not $pkg) {
            Write-Log '  Exact not found — fuzzy search' 'WARN'
            $chocoSearch2 = choco search $AppName --no-progress 2>&1
            foreach ($line in $chocoSearch2) {
                if ($line -match ""^$AppName"") {
                    $pkg = $line.Split(' ')[0]
                    Write-Log ""  Found fuzzy: $pkg"" 'INFO'
                    break
                }
            }
        }

        if ($pkg) {
            Write-Log ""  Installing: $pkg"" 'INFO'

            $chocoArgs = @(
                'install', $pkg,
                '-y',
                '--yes',
                '--force',
                '--no-progress',
                '--allow-downgrade',
                '--ignore-checksums',
                '--accept-license',
                '--no-color',
                '--confirm',
                '--timeout', '600'
            )

            $cResult = & choco @chocoArgs 2>&1
            Write-Log ""  Choco output: $cResult""

            if ($LASTEXITCODE -eq 0) {
                Write-Log 'SUCCESS: Installed via Chocolatey' 'SUCCESS'
                choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null
                Write-Log ""=== INSTALL COMPLETE === Log: $LogFile"" 'SUCCESS'
                exit 0
            } else {
                Write-Log ""  Chocolatey failed (exit=$LASTEXITCODE)"" 'WARN'
                choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null
            }
        } else {
            Write-Log ""  Package not found in Chocolatey: $AppName"" 'WARN'
            choco feature disable -n allowGlobalConfirmation --no-progress 2>&1 | Out-Null
        }
    } else {
        Write-Log '  Chocolatey not available after install attempt' 'ERROR'
    }
} catch {
    Write-Log ""  STEP 2 error: $_"" 'ERROR'
}

# ============================================================
# STEP 3: Winget direct name fallback (no ID lookup)
# ============================================================
Write-Log 'STEP 3: Winget direct name fallback...'
try {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $wFallback = winget install `
            --name $AppName `
            --silent `
            --accept-package-agreements `
            --accept-source-agreements `
            --disable-interactivity `
            --force 2>&1
        Write-Log ""  Winget fallback output: $wFallback""
        if ($LASTEXITCODE -eq 0) {
            Write-Log 'SUCCESS: Installed via Winget fallback' 'SUCCESS'
            Write-Log ""=== INSTALL COMPLETE === Log: $LogFile"" 'SUCCESS'
            exit 0
        } else {
            Write-Log ""  Winget fallback failed (exit=$LASTEXITCODE)"" 'WARN'
        }
    }
} catch {
    Write-Log ""  STEP 3 error: $_"" 'ERROR'
}

Write-Log 'ERROR: All installation methods failed' 'ERROR'
Write-Log ""=== INSTALL FAILED === Log: $LogFile"" 'ERROR'
exit 1
";
    }
}
