using System.Runtime.InteropServices;

namespace AirDropAnywhere.Core
{
    internal static class Interop
    {
        [DllImport("libnative.so", EntryPoint = "StartAWDLBrowsing", SetLastError = true)]
        public static extern void StartAWDLBrowsing();
        
        [DllImport("libnative.so", EntryPoint = "StopAWDLBrowsing", SetLastError = true)]
        public static extern void StopAWDLBrowsing();
    }
}