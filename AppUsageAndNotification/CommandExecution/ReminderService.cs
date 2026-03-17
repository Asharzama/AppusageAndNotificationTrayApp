using AppUsageAndNotification.Helper;
using AppUsageAndNotification.Services;
using AppUsageAndNotification.TrayIcon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AppUsageAndNotification.Services.ApiService;

namespace AppUsageAndNotification.CommandExecution
{
    public class ReminderService
    {
        private readonly ApiService _apiService;

        public ReminderService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task CheckAndShowRemindersAsync()
        {
            try
            {
                if (!AppConfig.IsReady) return;

                var reminders = await _apiService.GetRemindersAsync(AppConfig.UserId);

                if (reminders.Count == 0)
                {
                    Debug.WriteLine("✅ No pending reminders.");
                    return;
                }

                Debug.WriteLine($"🔔 Found {reminders.Count} reminder(s).");

                foreach (var reminder in reminders)
                    await ProcessReminderAsync(reminder);
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("CheckAndShowRemindersAsync", ex.Message);
            }
        }

        private async Task ProcessReminderAsync(Reminder reminder)
        {
            try
            {
                ShowBalloonNotification(reminder.Title, reminder.Message);

                var marked = await _apiService.MarkReminderAsExecutedAsync(reminder.Id);
                if (!marked)
                    await _apiService.LogErrorAsync("Reminder Mark Failed",
                        $"Failed to mark reminder {reminder.Id} as executed.");
            }
            catch (Exception ex)
            {
                await _apiService.LogErrorAsync("ProcessReminderAsync",
                    $"Reminder {reminder.Id}: {ex.Message}");
            }
        }

        private void ShowBalloonNotification(string title, string message)
        {
            try
            {
                var trayContext = TrayApplicationContext.Instance;
                trayContext?.ShowNotification(title, message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ ShowBalloonNotification: {ex.Message}");
            }
        }
    }
}
