using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace AppUsageAndNotification.Helper
{
    public static class DeviceHelper
    {
        public static string GetMacAddress()
        {
            string uuid = string.Empty;
            using (ManagementClass mc = new ManagementClass("Win32_ComputerSystemProduct"))
            {
                foreach (ManagementObject mo in mc.GetInstances())
                {
                    uuid = mo["UUID"].ToString();
                    break; // Usually only one instance
                }
            }
            return uuid;
        }
    }
}
