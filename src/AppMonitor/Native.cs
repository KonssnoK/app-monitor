using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AppMonitor
{
    static class Native
    {
        // ---- ntdll ----
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
        public const int SystemProcessInformation = 5;

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass, IntPtr buffer, int length, out int returnLength);
        public const int ProcessCommandLineInformation = 60;

        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
        public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

        // ---- kernel32 ----
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE s, int size);
        public const int ProcessPowerThrottling = 4;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll")]
        public static extern bool EmptyWorkingSet(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        // ---- advapi32 ----
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        public const uint TOKEN_QUERY = 8;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int retLen);
        public const int TokenUser = 1;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string machine, string db, uint access);
        public const uint SC_MANAGER_ENUMERATE_SERVICE = 4;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
            IntPtr buf, int bufSize, out int needed, out int returned, ref int resume, string group);

        [DllImport("advapi32.dll")]
        public static extern bool CloseServiceHandle(IntPtr h);

        // ---- pdh ----
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufSize, out int count, IntPtr buffer);

        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr q);

        public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
        public const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    }
}
