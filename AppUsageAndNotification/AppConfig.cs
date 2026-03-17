using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppUsageAndNotification
{
    public static class AppConfig
    {
        public static string UserId { get; set; } = "";
        public static int DeviceId { get; set; } = 0;

        public static bool IsReady =>
            !string.IsNullOrEmpty(UserId) && DeviceId > 0; 
    }
}
