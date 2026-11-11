# Fan speeds and temperatures

Windows has no standard way for programs to read fan speeds or real temperatures. AppMonitor reads them through the [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) library, which is embedded in the exe. For motherboard sensors, the library relies on the [PawnIO](https://pawnio.eu) driver.

## What you get, and what it needs

| Sensors | Source | Needs |
|---|---|---|
| GPU fans, GPU core and hot-spot temperature | NVIDIA / AMD driver APIs | nothing, works as a normal user |
| Motherboard fans (CPU fan, pump, case fans) | the motherboard's fan controller chip ("Super I/O") | administrator rights **and** PawnIO |
| CPU temperature (AMD Tctl / Intel Package) | the CPU's own sensors | administrator rights **and** PawnIO |
| Motherboard temperatures (up to 4) | the motherboard's fan controller chip | administrator rights **and** PawnIO |

Without any of these sensors, the Temperature lane falls back to the Windows ACPI "thermal zone" value. That value is often fake or never changes.

## PawnIO

[PawnIO](https://pawnio.eu) ([source](https://github.com/namazso/PawnIO), [modules](https://github.com/namazso/PawnIO.Modules)) is a signed, open-source Windows kernel driver that gives programs controlled access to hardware. Instead of allowing arbitrary hardware access, it only runs small, reviewed modules, such as "read the fan controller chip". LibreHardwareMonitor uses it in place of the older WinRing0 driver, which Microsoft's vulnerable-driver blocklist and antivirus tools now block.

AppMonitor does **not** bundle or install PawnIO. Installing a kernel driver goes against a portable app, so PawnIO is a separate, one-time install that you opt into.

### Install

1. Download the installer from <https://pawnio.eu> and run it. Choose the signed version.
2. In AppMonitor, choose **More → Restart as administrator**. AppMonitor only uses PawnIO when it is running elevated.
3. Check the status bar at the bottom right. It should list more sensors, for example `Sensors: 3 fan(s), 6 temp sensor(s)`, with no hint about admin rights or PawnIO.

**More → Get the PawnIO driver…** in the app explains the same steps and opens the website.

To check from a terminal that the driver is installed:

```powershell
Get-Service PawnIO
```

### Uninstall

Remove **PawnIO** from *Settings → Apps → Installed apps*. AppMonitor then goes back to GPU sensors only, and keeps working.

### Vendor tools (Gigabyte Control Center, Armoury Crate, iCUE, …)

Motherboard utilities install their own drivers, for example `gdrv3` and `MyPortIO` from Gigabyte Control Center. Only the vendor's own software can use those drivers, so they don't help AppMonitor. You still need PawnIO.

PawnIO and the vendor tools can be installed side by side. If both read the fan chip at exactly the same moment, a reading can occasionally come out wrong. This only causes a rare odd value, never damage.

## How sensors are chosen

- **Motherboard fans** are included only if they are spinning when recording starts. Empty fan headers report 0, and this rule leaves them out. A side effect: a fan that happens to be stopped at that moment is also skipped.
- **GPU fans** are always included. GPUs stop their fans completely when cool ("zero-RPM" mode), so the timeline shows them as "stopped" at idle. That's normal.
- **Fan names** come from the fan chip, so they are often generic, such as `Fan #1`. To find out which header is which, watch which fan rises with CPU load: that's the CPU fan or pump.
- **Motherboard temperatures** are only included if they read between 1 and 115 °C. This filters out unconnected probes.

## Where sensors show up

- **Fans** lane: one line per fan, in RPM.
- **Temperature** lane: one line per sensor, with the peak in the visible range.
- The hover tooltip shows every fan and temperature reading at that moment.
- **Busy periods** list the fastest fan speed during each period.
- Traces store sensor readings, so opening an old trace shows them too.

## Starting with Windows

**More → Start with Windows** starts AppMonitor at logon as a normal user, without admin rights. After a reboot it records GPU sensors only, until you use **Restart as administrator** again. Starting it elevated automatically would need a Task Scheduler task set to "Run with highest privileges".
