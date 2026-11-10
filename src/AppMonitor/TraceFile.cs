using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AppMonitor
{
    /// <summary>
    /// File format (.amtrace):
    ///   "AMTRACE1" magic, header (BinaryWriter), then a sequence of chunks: [int32 length][gzip(records)].
    /// Each chunk is independently compressed, so a crash or power loss only loses the last unflushed chunk.
    /// Records: 1 = ProcInfo, 2 = Sample, 3 = SensorDef, 4 = sensor values for the preceding sample.
    /// </summary>
    static class TraceFile
    {
        public const string Extension = ".amtrace";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("AMTRACE1");
        const byte RecProc = 1, RecSample = 2, RecSensorDef = 3, RecSensorValues = 4;

        public static void WriteHeader(Stream s, Trace t)
        {
            s.Write(Magic, 0, Magic.Length);
            using var bw = new BinaryWriter(s, Encoding.UTF8, true);
            bw.Write(t.Machine ?? "");
            bw.Write(t.CpuCount);
            bw.Write(t.TotalMemBytes);
        }

        public static void WriteProc(BinaryWriter w, ProcInfo p)
        {
            w.Write(RecProc);
            w.Write(p.Key); w.Write(p.Pid); w.Write(p.CreateTime);
            w.Write(p.Name ?? ""); w.Write(p.User ?? ""); w.Write(p.Path ?? "");
            w.Write(p.CommandLine ?? ""); w.Write(p.Services ?? "");
        }

        public static void WriteSensorDef(BinaryWriter w, SensorDef d)
        {
            w.Write(RecSensorDef);
            w.Write((byte)d.Index); w.Write((byte)d.Kind); w.Write(d.Name ?? "");
        }

        public static void WriteSample(BinaryWriter w, Sample s)
        {
            w.Write(RecSample);
            w.Write(s.Ticks); w.Write(s.IntervalSec);
            w.Write(s.Cpu); w.Write(s.Gpu); w.Write(s.MemPct); w.Write(s.MemUsedMB);
            w.Write(s.DiskReadBps); w.Write(s.DiskWriteBps); w.Write(s.DiskActivePct); w.Write(s.NetBps);
            w.Write(s.TempC); w.Write(s.CpuClockPct);
            w.Write((ushort)s.Procs.Length);
            foreach (var p in s.Procs)
            {
                w.Write(p.Key); w.Write(p.Cpu); w.Write(p.Gpu); w.Write(p.ReadBps); w.Write(p.WriteBps); w.Write(p.MemMB);
            }
            if (s.Sensors != null)
            {
                w.Write(RecSensorValues);
                w.Write((byte)s.Sensors.Length);
                foreach (var v in s.Sensors) w.Write(v);
            }
        }

        public static void WriteChunk(Stream fs, MemoryStream records)
        {
            if (records.Length == 0) return;
            var comp = new MemoryStream();
            using (var gz = new GZipStream(comp, CompressionLevel.Optimal, true))
                gz.Write(records.GetBuffer(), 0, (int)records.Length);
            var len = BitConverter.GetBytes((int)comp.Length);
            fs.Write(len, 0, 4);
            fs.Write(comp.GetBuffer(), 0, (int)comp.Length);
        }

        /// <summary>Writes a complete snapshot of a trace (used by "Save as").</summary>
        public static void Save(Trace t, string path)
        {
            List<Sample> samples; List<ProcInfo> procs; List<SensorDef> sensors;
            lock (t.Sync) { samples = new List<Sample>(t.Samples); procs = new List<ProcInfo>(t.Procs.Values); sensors = new List<SensorDef>(t.Sensors); }
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                WriteHeader(fs, t);
                var ms = new MemoryStream();
                var bw = new BinaryWriter(ms, Encoding.UTF8);
                foreach (var d in sensors) WriteSensorDef(bw, d);
                foreach (var p in procs) WriteProc(bw, p);
                for (int i = 0; i < samples.Count; i++)
                {
                    WriteSample(bw, samples[i]);
                    if (ms.Length > 1 << 20) { bw.Flush(); WriteChunk(fs, ms); ms.SetLength(0); }
                }
                bw.Flush(); WriteChunk(fs, ms);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Loads a trace. Truncated/corrupt tails (e.g. after a crash) are tolerated.</summary>
        public static Trace Load(string path)
        {
            var t = new Trace { FilePath = path };
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var magic = new byte[Magic.Length];
            if (fs.Read(magic, 0, magic.Length) != magic.Length || Encoding.ASCII.GetString(magic) != "AMTRACE1")
                throw new InvalidDataException("Not an AppMonitor trace file.");
            var br = new BinaryReader(fs, Encoding.UTF8, true);
            t.Machine = br.ReadString();
            t.CpuCount = br.ReadInt32();
            t.TotalMemBytes = br.ReadInt64();

            var lenBuf = new byte[4];
            while (true)
            {
                if (fs.Read(lenBuf, 0, 4) != 4) break;
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 512 << 20) break;
                var data = new byte[len];
                int got = 0;
                while (got < len) { int n = fs.Read(data, got, len - got); if (n <= 0) break; got += n; }
                if (got != len) break;
                try { ReadChunk(t, data); }
                catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException) { break; }
            }
            return t;
        }

        static void ReadChunk(Trace t, byte[] data)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            var raw = new MemoryStream();
            gz.CopyTo(raw);
            raw.Position = 0;
            var r = new BinaryReader(raw, Encoding.UTF8);
            while (raw.Position < raw.Length)
            {
                byte tag = r.ReadByte();
                if (tag == RecProc)
                {
                    var p = new ProcInfo
                    {
                        Key = r.ReadInt32(), Pid = r.ReadInt32(), CreateTime = r.ReadInt64(),
                        Name = r.ReadString(), User = r.ReadString(), Path = r.ReadString(),
                        CommandLine = r.ReadString(), Services = r.ReadString()
                    };
                    t.Procs[p.Key] = p;
                }
                else if (tag == RecSample)
                {
                    var s = new Sample
                    {
                        Ticks = r.ReadInt64(), IntervalSec = r.ReadSingle(),
                        Cpu = r.ReadSingle(), Gpu = r.ReadSingle(), MemPct = r.ReadSingle(), MemUsedMB = r.ReadSingle(),
                        DiskReadBps = r.ReadSingle(), DiskWriteBps = r.ReadSingle(), DiskActivePct = r.ReadSingle(), NetBps = r.ReadSingle(),
                        TempC = r.ReadSingle(), CpuClockPct = r.ReadSingle()
                    };
                    int n = r.ReadUInt16();
                    s.Procs = new ProcSample[n];
                    for (int i = 0; i < n; i++)
                        s.Procs[i] = new ProcSample { Key = r.ReadInt32(), Cpu = r.ReadSingle(), Gpu = r.ReadSingle(), ReadBps = r.ReadSingle(), WriteBps = r.ReadSingle(), MemMB = r.ReadSingle() };
                    t.Samples.Add(s);
                    if (!float.IsNaN(s.TempC)) t.HasTemp = true;
                }
                else if (tag == RecSensorDef)
                {
                    var d = new SensorDef { Index = r.ReadByte(), Kind = (SensorKind)r.ReadByte(), Name = r.ReadString() };
                    while (t.Sensors.Count <= d.Index) t.Sensors.Add(new SensorDef { Index = t.Sensors.Count });
                    t.Sensors[d.Index] = d;
                }
                else if (tag == RecSensorValues)
                {
                    int n = r.ReadByte();
                    var v = new float[n];
                    for (int i = 0; i < n; i++) v[i] = r.ReadSingle();
                    if (t.Samples.Count > 0) t.Samples[t.Samples.Count - 1].Sensors = v;
                }
                else throw new InvalidDataException("Unknown record " + tag);
            }
        }
    }

    /// <summary>Streams a live recording to disk in small compressed chunks.</summary>
    sealed class TraceWriter : IDisposable
    {
        readonly FileStream fs;
        readonly MemoryStream buf = new MemoryStream();
        readonly BinaryWriter bw;
        readonly Stopwatch sinceFlush = Stopwatch.StartNew();
        int pending;
        public string Path { get; }

        public TraceWriter(string path, Trace t)
        {
            Path = path;
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            TraceFile.WriteHeader(fs, t);
            fs.Flush();
            bw = new BinaryWriter(buf, Encoding.UTF8);
        }

        public void Proc(ProcInfo p) => TraceFile.WriteProc(bw, p);
        public void Sensor(SensorDef d) => TraceFile.WriteSensorDef(bw, d);

        public void Sample(Sample s)
        {
            TraceFile.WriteSample(bw, s);
            if (++pending >= 30 || sinceFlush.Elapsed.TotalSeconds >= 60) Flush();
        }

        public void Flush()
        {
            bw.Flush();
            if (buf.Length > 0)
            {
                TraceFile.WriteChunk(fs, buf);
                fs.Flush();
                buf.SetLength(0);
            }
            pending = 0;
            sinceFlush.Restart();
        }

        public void Dispose()
        {
            try { Flush(); } catch { }
            fs.Dispose();
        }
    }
}
