# app-monitor

A portable, lightweight Windows resource recorder for catching **what made the fan spin up**, after the fact.

It runs in the tray and records CPU, GPU, disk, memory, network, CPU clock boost, and temperature. For each sample it also records which process (and which user) was using them. It builds a timeline you can search back through later, so you no longer have to open Task Manager after the spike has already ended.

## Features

- **Tracealyzer-style timeline**:
  - System graphs (CPU and GPU stacked by process, disk, memory, CPU clock, temperature, network) sit on top.
  - One swimlane per process sits below, all on a shared time axis.
  - Mouse controls: wheel to zoom, drag to select a range, right-drag to pan, overview strip to jump.
- **Busy periods**: a list of sustained high-CPU/GPU periods (likely fan triggers) and the processes behind them. Click one to jump to it.
- **Who used what**: a per-process table for any selected range: average and peak CPU/GPU, CPU time, disk read/write, peak memory, user, and command line. Svchost processes show the services they host.
- **Live view** while recording. **Start/Stop** with one button.
- **Traces are always saved**:
  - Recording streams to `traces\trace_<date>.amtrace` next to the exe, in crash-safe compressed chunks.
  - Use **Open…** to load old traces and **Save as…** to copy one. **Export CSV…** exports the table.
- **Portable**: a single `AppMonitor.exe` (~3 MB, libraries embedded) for .NET Framework 4.8, which ships with Windows 10/11. No install is needed.
- **Light on resources**:
  - One kernel call per sample collects data for every process.
  - Per-process details are looked up once per process, and only for processes that matter.
  - It runs at below-normal priority with EcoQoS and stops drawing while hidden.
  - Typical cost is around 0.1% CPU and 40–60 MB RAM (including the sensor library). Traces take about 0.5 MB per hour at the 2 s interval.

## Usage

```
AppMonitor.exe                  open the window
AppMonitor.exe --record --tray  start recording silently in the tray
AppMonitor.exe some.amtrace     open a trace
```

- Closing or minimizing the window while recording keeps it recording in the tray.
- **More → Start with Windows** registers `--record --tray` at logon, so it is always recording when the fan goes off.
- **More → Restart as administrator** shows users and command lines for system processes as well. CPU/IO numbers for all processes are collected either way. With [PawnIO](https://pawnio.eu) installed, it also enables motherboard fans and CPU temperature (see below).

## Fan speed and temperatures

Windows has no standard fan-speed API. The app reads fan speeds and temperatures through the bundled [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) library:

| Sensors | Needs |
|---|---|
| GPU fans and GPU temperature (NVIDIA/AMD) | nothing, works as a normal user |
| Motherboard fans (CPU fan, case fans) and real CPU temperature | administrator rights **and** the [PawnIO](https://pawnio.eu) driver (one-time install) |

**Quick setup for motherboard fans:**
1. Install PawnIO from <https://pawnio.eu>.
2. Use **More → Restart as administrator**.
3. Check that the status bar lists the new sensors.

- PawnIO is a separate, opt-in install; AppMonitor doesn't bundle it.
- Drivers installed by vendor tools such as Gigabyte Control Center can't be used instead.
- See **[docs/sensors.md](docs/sensors.md)** for details: what PawnIO is, uninstalling it, how fans are chosen, and running alongside vendor tools.

## Third-party components

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0): embedded in the exe. Reads fan speeds and temperatures.
- [PawnIO](https://pawnio.eu) ([source](https://github.com/namazso/PawnIO)): not bundled. It's the optional driver LibreHardwareMonitor uses for motherboard and CPU sensors.

## Build

Requires the .NET SDK (any recent version):

```
dotnet build src/AppMonitor -c Release
```

The output is `src/AppMonitor/bin/Release/net48/AppMonitor.exe`. Copy that single file anywhere.
