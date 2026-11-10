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
- **More → Restart as administrator** shows users and command lines for system processes as well. CPU/IO numbers for all processes are collected either way.

## Fan speed and temperatures

Windows has no standard fan-speed API. The app reads fan speeds and temperatures through the bundled [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) library:

| Sensors | Needs |
|---|---|
| GPU fans and GPU temperature (NVIDIA/AMD) | nothing, works as a normal user |
| Motherboard fans (CPU fan, case fans) and real CPU temperature | administrator rights **and** the [PawnIO](https://pawnio.eu) driver (one-time install) |

- The status bar shows which sensors were found. **More → Get the PawnIO driver…** explains how to install it.
- Busy periods show the fastest fan speed during each period.
- GPUs stop their fans completely when cool ("zero-RPM" mode), so "stopped" at idle is normal.
- Without any sensors, the app falls back to the ACPI thermal zone temperature. That value is often fake.

## Build

Requires the .NET SDK (any recent version):

```
dotnet build src/AppMonitor -c Release
```

The output is `src/AppMonitor/bin/Release/net48/AppMonitor.exe`. Copy that single file anywhere.
