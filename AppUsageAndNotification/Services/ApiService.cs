using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Management;
using AppUsageAndNotification.Helper;

namespace AppUsageAndNotification.Services
{
    public class ApiService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private const string BaseUrl = "https://mdmwindowapi.safe4sure.ai";

        // ── Device Info ──────────────────────────────────────────────
        public async Task FetchAndStoreDeviceInfoAsync()
        {
            try
            {
                var deviceUniqueId = DeviceHelper.GetMacAddress();
                if (string.IsNullOrEmpty(deviceUniqueId))
                    throw new Exception("Could not get device unique ID.");

                var url = $"{BaseUrl}/api/WindowDeviceInfo/GetWindowDeviceInfo/" +
                          $"UniqueId?deviceUniqueId={deviceUniqueId}";

                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<DeviceInfoResult>>(url);

                if (response == null || !response.IsSucced || response.Result == null)
                    throw new Exception(response?.ErrorMessage ?? "Null response");

                AppConfig.UserId = response.Result.UserId;
                AppConfig.DeviceId = response.Result.Id;

                Debug.WriteLine($"✅ Device info — UserId: {AppConfig.UserId}, " +
                               $"DeviceId: {AppConfig.DeviceId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ FetchAndStoreDeviceInfoAsync: {ex.Message}");
                throw;
            }
        }

        // ── App Usage ────────────────────────────────────────────────
        public async Task PostAppUsageAsync(
            List<AppUsageRecord> records, string userId, int deviceId)
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var validRecords = records
                    .Where(r => !string.IsNullOrWhiteSpace(r.AppName) &&
                                r.TotalTime.TotalSeconds > 1)
                    .ToList();

                if (validRecords.Count == 0) return;

                var payload = validRecords.ConvertAll(r => new AppUsagePayload
                {
                    Id = 0,
                    AppName = r.AppName,
                    TimeStamp = DateTime.Now,
                    Duration = (int)(r.EndTime - r.StartTime).TotalSeconds,
                    UserId = userId,
                    StartTime = r.StartTime.ToUniversalTime(),
                    EndTime = r.EndTime.ToUniversalTime(),
                    AppCategory = "General",
                    RiskLevel = "Low",
                    SpamStatus = false,
                    DeviceId = deviceId
                });

                var response = await _httpClient.PostAsJsonAsync(
                    $"{BaseUrl}/api/WindowsApplicationUsage/bulk", payload);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    await LogErrorAsync("PostAppUsage Failed",
                        $"{response.StatusCode}: {body}");
                }
            }
            catch (Exception ex)
            {
                await LogErrorAsync("PostAppUsageAsync", ex.Message);
            }
        }

        // ── Commands ─────────────────────────────────────────────────
        public async Task<List<WindowCommand>> GetPendingCommandsAsync(int deviceId)
        {
            try
            {
                var url = $"{BaseUrl}/api/WindowCommand/GetWindowCommand/UserSessioned" +
                          $"?deviceId={deviceId}&isExecuted=false&isUserSession=true";

                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<List<WindowCommand>>>(url);

                return response?.IsSucced == true
                    ? response.Result ?? new List<WindowCommand>()
                    : new List<WindowCommand>();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("GetPendingCommandsAsync", ex.Message);
                return new List<WindowCommand>();
            }
        }

        public async Task MarkCommandExecutedAsync(int commandId)
        {
            try
            {
                var url = $"{BaseUrl}/api/WindowCommand/{commandId}/execution-status";
                var request = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = JsonContent.Create(new { isExecuted = true })
                };
                await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                await LogErrorAsync("MarkCommandExecutedAsync", ex.Message);
            }
        }

        // ── Reminders ────────────────────────────────────────────────
        public async Task<List<Reminder>> GetRemindersAsync(string userId)
        {
            try
            {
                var url = $"{BaseUrl}/api/Reminder/get-reminders/{userId}";
                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<List<Reminder>>>(url);

                return response?.IsSucced == true
                    ? response.Result ?? new List<Reminder>()
                    : new List<Reminder>();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("GetRemindersAsync", ex.Message);
                return new List<Reminder>();
            }
        }

        public async Task<bool> MarkReminderAsExecutedAsync(int reminderId)
        {
            try
            {
                var url = $"{BaseUrl}/api/Reminder/mark-as-executed/{reminderId}";
                var request = new HttpRequestMessage(HttpMethod.Patch, url);
                request.Headers.Add("Accept", "*/*");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                await LogErrorAsync("MarkReminderAsExecutedAsync", ex.Message);
                return false;
            }
        }

        // ── App Install List ─────────────────────────────────────────
        public async Task<List<string>> GetInstalledAppListAsync(string userId)
        {
            try
            {
                var url = $"{BaseUrl}/api/Windowapplicationinstaller/" +
                          $"GetListOFInstalledApplicationByUserId?UserId={userId}";

                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<List<string>>>(url);

                return response?.IsSucced == true
                    ? response.Result ?? new List<string>()
                    : new List<string>();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("GetInstalledAppListAsync", ex.Message);
                return new List<string>();
            }
        }

        // ── App Uninstall List ────────────────────────────────────────────
        public async Task<List<string>> GetUninstalledAppListAsync(string userId)
        {
            try
            {
                var url = $"{BaseUrl}/api/Windowapplicationinstaller/" +
                          $"GetListOFUnInstalledApplicationByUserId?UserId={userId}";

                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<List<string>>>(url);

                return response?.IsSucced == true
                    ? response.Result ?? new List<string>()
                    : new List<string>();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("GetUninstalledAppListAsync", ex.Message);
                return new List<string>();
            }
        }
        public async Task<List<MasterApplicationDetail>> GetMasterAppListAsync()
        {
            try
            {
                var url = $"{BaseUrl}/api/MasterApplicationDetails";
                var response = await _httpClient
                    .GetFromJsonAsync<ApiResponseModel<List<MasterApplicationDetail>>>(url);

                return response?.IsSucced == true
                    ? response.Result ?? new List<MasterApplicationDetail>()
                    : new List<MasterApplicationDetail>();
            }
            catch (Exception ex)
            {
                await LogErrorAsync("GetMasterAppListAsync", ex.Message);
                return new List<MasterApplicationDetail>();
            }
        }

        // ── Error Logging ────────────────────────────────────────────
        public async Task LogErrorAsync(string errorTitle, string errorLog)
        {
            try
            {
                var payload = new WindowsServiceLog
                {
                    Id = 0,
                    ErrorTitle = errorTitle,
                    DeviceId = AppConfig.DeviceId,
                    ErrorLog = errorLog,
                    LogTimestamp = DateTime.Now
                };

                await _httpClient.PostAsJsonAsync(
                    $"{BaseUrl}/api/WindowsServiceLog", payload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ LogErrorAsync failed: {ex.Message}");
            }
        }
    }

    // ── Models ───────────────────────────────────────────────────────
    public class ApiResponseModel<T>
    {
        [JsonPropertyName("result")]
        public T? Result { get; set; }

        [JsonPropertyName("isSucced")]
        public bool IsSucced { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    public class DeviceInfoResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = "";

        [JsonPropertyName("deviceUniqueId")]
        public string DeviceUniqueId { get; set; } = "";
    }

    public class AppUsagePayload
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("appName")]
        public string AppName { get; set; } = "";

        [JsonPropertyName("timeStamp")]
        public DateTime TimeStamp { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = "";

        [JsonPropertyName("startTime")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime EndTime { get; set; }

        [JsonPropertyName("appCategory")]
        public string AppCategory { get; set; } = "";

        [JsonPropertyName("riskLevel")]
        public string RiskLevel { get; set; } = "";

        [JsonPropertyName("spamStatus")]
        public bool SpamStatus { get; set; }

        [JsonPropertyName("deviceId")]
        public int DeviceId { get; set; }
    }

    public class WindowCommand
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("deviceId")]
        public int DeviceId { get; set; }

        [JsonPropertyName("isExecuted")]
        public bool IsExecuted { get; set; }

        [JsonPropertyName("scriptId")]
        public int ScriptId { get; set; }

        [JsonPropertyName("scriptModel")]
        public ScriptModel? ScriptModel { get; set; }
    }

    public class ScriptModel
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("policyName")]
        public string PolicyName { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("script")]
        public string Script { get; set; } = "";

        [JsonPropertyName("commandType")]
        public string CommandType { get; set; } = "";

        [JsonPropertyName("parameters")]
        public string? Parameters { get; set; }
    }

    public class Reminder
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("reminderTime")]
        public DateTime ReminderTime { get; set; }

        [JsonPropertyName("isExecuted")]
        public bool IsExecuted { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = "";
    }

    public class WindowsServiceLog
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("errorTitle")]
        public string ErrorTitle { get; set; } = "";

        [JsonPropertyName("deviceId")]
        public int DeviceId { get; set; }

        [JsonPropertyName("errorLog")]
        public string ErrorLog { get; set; } = "";

        [JsonPropertyName("logTimestamp")]
        public DateTime LogTimestamp { get; set; }
    }
    public class MasterApplicationDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("appName")]
        public string AppName { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("packageId")]
        public string PackageId { get; set; } = "";

        [JsonPropertyName("installerType")]
        public string InstallerType { get; set; } = "";

        [JsonPropertyName("installType")]
        public string InstallType { get; set; } = "";

        [JsonPropertyName("metaData")]
        public string MetaData { get; set; } = "";
    }
}
