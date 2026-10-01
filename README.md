# PCMon 🖥️🩺

**Your PC's vital signs, minus the hospital bill.**

PCMon is a tiny, portable Windows dashboard. It shows hardware sensors (like HWMonitor), live network speed, running and not-running tasks, and installed apps you forgot existed, with a button to uninstall them.

It's one `.exe` with no installer. Double-click it and it runs.

## Status
📄 Spec stage. The full requirements are in **[SRS.md](SRS.md)**.
The SRS is written so you can hand it straight to a coding agent: *"Build this."*

## Planned features
- 🌡️ **Hardware**: CPU/GPU temps, loads, clocks, fans and voltages, with min/max
- 🌐 **Network**: live ↓/↑ per adapter and active connections
- 📋 **Tasks**: processes (Running / Not Responding) with End Task, plus Scheduled Tasks state
- 🗑️ **Unused apps**: apps you haven't opened in 90+ days, removed with their own uninstallers
- 🔒 **Zero telemetry**. PCMon doesn't phone home. It doesn't even know where home is.

## Why does it need admin?
Reading hardware sensors and Windows Prefetch data needs elevated access. PCMon only reads that data. It never writes anything except its own settings file.

## Tech
C# · .NET 8 · WPF · LibreHardwareMonitorLib, published as a single self-contained `PCMon.exe`.
