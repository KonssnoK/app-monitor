using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace AppMonitor
{
    /// <summary>
    /// Fan RPM and real temperatures via LibreHardwareMonitor.
    /// - GPU (NVIDIA/AMD vendor APIs): works as a normal user, no driver needed.
    /// - Motherboard fans (Super I/O chip) and CPU temperature: need administrator rights and the PawnIO driver.
    /// Everything here runs on the collector thread.
    /// </summary>
    sealed class HardwareSensors : IDisposable
    {
        Computer pc;
        readonly List<ISensor> sensors = new List<ISensor>();
        readonly List<IHardware> toUpdate = new List<IHardware>();
        public readonly List<SensorDef> Defs = new List<SensorDef>();
        public string Status = "";

        public static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool PawnIoInstalled()
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
            catch { return false; }
        }

        public void Open()
        {
            bool elevated = IsElevated(), pawn = PawnIoInstalled();
            bool lowLevel = elevated && pawn;
            pc = new Computer { IsGpuEnabled = true, IsMotherboardEnabled = lowLevel, IsCpuEnabled = lowLevel };
            pc.Open();

            var fans = new List<(ISensor s, string name)>();
            var temps = new List<(ISensor s, string name)>();
            foreach (var hw in All(pc.Hardware))
            {
                hw.Update();
                string prefix = Prefix(hw);
                var hwTemps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                // motherboard headers reading 0 at startup are almost always unconnected; GPU fans are kept
                // even at 0 because GPUs stop their fans when cool (zero-RPM mode)
                bool isGpu = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel;
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan && (isGpu || s.Value > 0)))
                    fans.Add((s, (prefix + s.Name).Trim()));

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        var cpu = hwTemps.FirstOrDefault(s => s.Name.Contains("Tctl")) ?? hwTemps.FirstOrDefault(s => s.Name.Contains("Package")) ?? hwTemps.FirstOrDefault();
                        if (cpu != null) temps.Insert(0, (cpu, "CPU"));
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        foreach (var s in hwTemps.Where(s => s.Name == "GPU Core" || s.Name == "GPU Hot Spot"))
                            temps.Add((s, prefix + s.Name.Replace("GPU ", "")));
                        break;
                    case HardwareType.SuperIO:
                        // board temperature probes; skip disconnected ones that read nonsense
                        foreach (var s in hwTemps.Where(s => s.Value > 1 && s.Value < 115).Take(4))
                            temps.Add((s, "Board " + s.Name));
                        break;
                }
            }

            foreach (var (s, name) in fans.Take(10)) Add(s, name, SensorKind.Fan);
            foreach (var (s, name) in temps.Take(16 - Defs.Count)) Add(s, name, SensorKind.Temp);

            int nFans = Defs.Count(d => d.Kind == SensorKind.Fan);
            string hint = !elevated ? "motherboard fans and CPU temp need admin (More menu)"
                        : !pawn ? "motherboard fans and CPU temp need the PawnIO driver (More menu)"
                        : "";
            Status = $"{nFans} fan(s), {Defs.Count - nFans} temp sensor(s)" + (hint.Length > 0 ? " · " + hint : "");
        }

        void Add(ISensor s, string name, SensorKind kind)
        {
            Defs.Add(new SensorDef { Index = Defs.Count, Name = name, Kind = kind });
            sensors.Add(s);
            if (!toUpdate.Contains(s.Hardware)) toUpdate.Add(s.Hardware);
        }

        static string Prefix(IHardware hw)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    string n = hw.Name.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("(TM)", "").Replace("Intel(R) ", "").Trim();
                    return n + " ";
                case HardwareType.Cpu: return "CPU ";
                default: return "";
            }
        }

        static IEnumerable<IHardware> All(IEnumerable<IHardware> list)
        {
            foreach (var h in list)
            {
                yield return h;
                foreach (var sub in All(h.SubHardware)) yield return sub;
            }
        }

        public float[] Read()
        {
            if (sensors.Count == 0) return null;
            foreach (var hw in toUpdate) hw.Update();
            var v = new float[sensors.Count];
            for (int i = 0; i < v.Length; i++) v[i] = sensors[i].Value ?? float.NaN;
            return v;
        }

        public void Dispose()
        {
            try { pc?.Close(); } catch { }
            pc = null;
        }
    }
}
