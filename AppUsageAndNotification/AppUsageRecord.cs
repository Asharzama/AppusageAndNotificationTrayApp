using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppUsageAndNotification
{
    public class AppUsageRecord
    {
        public string AppName { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public DateTime LastSeen { get; set; }
        public int FocusCount { get; set; }

        public TimeSpan Duration => EndTime - StartTime;
        public TimeSpan TotalTime { get; set; }
    }
}
