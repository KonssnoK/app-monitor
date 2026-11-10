using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AppMonitor
{
    static class Program
    {
        const string MutexName = @"Local\AppMonitor_SingleInstance";
        const string ShowEventName = @"Local\AppMonitor_Show";
        static Mutex mutex;

        [STAThread]
        static void Main(string[] args)
        {
            // Library DLLs (LibreHardwareMonitor & deps) are embedded in the exe to keep it a single portable file.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            Run(args);
        }

        static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            lock (loaded)
            {
                if (loaded.TryGetValue(name, out var a)) return a;
                using var s = typeof(Program).Assembly.GetManifestResourceStream("lib." + name + ".dll");
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                a = Assembly.Load(ms.ToArray());
                loaded[name] = a;
                return a;
            }
        }
        static readonly System.Collections.Generic.Dictionary<string, Assembly> loaded = new System.Collections.Generic.Dictionary<string, Assembly>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(string[] args)
        {
            if (!AcquireSingleInstance())
            {
                // already running: bring the existing window up instead
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
                return;
            }

            BeStealthy();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var form = new MainForm(args);
            ThreadPool.RegisterWaitForSingleObject(showEvent, (s, timedOut) => { if (form.IsHandleCreated) form.ActivateFromOtherInstance(); }, null, -1, false);
            Application.Run(form);
            ReleaseSingleInstance();
        }

        public static bool AcquireSingleInstance()
        {
            mutex = new Mutex(true, MutexName, out bool created);
            if (!created) { mutex.Dispose(); mutex = null; }
            return created;
        }

        public static void ReleaseSingleInstance()
        {
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            mutex = null;
        }

        /// <summary>Run below normal priority and opt into EcoQoS so the monitor never competes with real work.</summary>
        static void BeStealthy()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            try
            {
                var st = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 }; // EXECUTION_SPEED throttling
                Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref st, Marshal.SizeOf(st));
            }
            catch { }
        }
    }
}
