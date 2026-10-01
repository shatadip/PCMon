# PCMON — Software Requirements Specification (v1.0)

*PCMON by Shatadip Majumder.* Show this byline in the window title, under the sidebar logo, in the About box and in the exe's file description.

> **How to use this doc:** Paste the whole file into your coding agent and say *"Build this."*
> Every requirement is written as an instruction. **MUST** = required for v1. **SHOULD** = add it if it's cheap.

---

## 1. What PCMON is

PCMON is a **single portable `.exe`** for Windows 10/11 that puts everything on one dashboard:

- **Hardware**: temperatures, loads, clocks, fans and voltages, like HWMonitor
- **Network**: live upload and download speed, plus who is connected to what
- **Tasks**: what's running, what's not, and what has stopped responding
- **Unused apps**: installed apps you haven't opened in ages, with a button to uninstall them

There is no installer, no setup wizard, no services, no telemetry and no "Would you like to install McAfee?". You double-click it and it runs.

*Tagline: "Your PC's vital signs, minus the hospital bill."*

## 2. Tech stack (fixed)

| Item | Choice |
|---|---|
| Language / UI | C# 12, **.NET 8, WPF** (MVVM, `CommunityToolkit.Mvvm`) |
| Hardware sensors | `LibreHardwareMonitorLib` (NuGet) |
| Scheduled tasks | `TaskScheduler` NuGet (dahall) or the Task Scheduler COM API |
| Charts | `LiveChartsCore.SkiaSharpView.WPF` (or a hand-drawn `Polyline` sparkline if lighter) |
| Packaging | One self-contained file, see §7 |
| Privileges | App manifest `requireAdministrator`, because sensors and Prefetch need it |
| CPU sensor driver | LHM ≥ 0.9.5 reads CPU temps, clocks and power through the **PawnIO** driver. PCMON never installs it. Without it, PCMON shows a banner and falls back to the ACPI thermal zone (WMI) |

## 3. UI layout

One resizable window (default 1100×720, min 900×600). It has a left nav rail with 5 pages, a status bar at the bottom, and dark/light theme that follows Windows.

```
┌──────────┬─────────────────────────────────────────────┐
│ ◉ Dash   │  [CPU 34% 61°C] [GPU 12% 48°C] [RAM 9.1/16] │
│ ▣ HW     │  [Disk C: 41%]  [↓ 2.4 MB/s ↑ 180 KB/s]      │
│ ⇅ Net    │  ── 60-second sparklines for each ──         │
│ ☰ Tasks  │  Top 5 processes by CPU / RAM                │
│ 🗑 Unused │                                              │
├──────────┴─────────────────────────────────────────────┤
│ Status: "All systems nominal. Suspiciously nominal."    │
└────────────────────────────────────────────────────────┘
```

## 4. Functional requirements

### 4.1 Dashboard (home)
- **FR-1 MUST**: Show tiles for CPU and GPU, RAM (used/total) and network (current ↓/↑, VPN on/off, alert count). The CPU and GPU tiles lead with **temperature in °C**, with load % (and VRAM) underneath, and the sparkline plots both load and temperature. Add a **Storage** card with one usage bar per fixed disk, showing used %, free space and temp if available.
- **FR-2 MUST**: Each tile has a 60-second sparkline. Refresh every **1 s** on a background thread, and never block the UI thread. Disks get bars instead of sparklines: "% used" barely moves, and PCs with many drives would push everything else off screen.
- **FR-3 MUST**: Colour each tile **green / amber / red** by threshold. The defaults are: CPU/GPU temp ≥ 80 °C amber, ≥ 90 °C red. RAM ≥ 85 % amber, ≥ 95 % red. Disk ≥ 90 % amber.
- **FR-4 MUST**: Show the top 5 processes by CPU and by RAM. Clicking one jumps to that process on the Tasks page.

### 4.2 Hardware (the HWMonitor page)
- **FR-5 MUST**: Show a tree view grouped as **Device → Sensor type → Sensor**, covering CPU, GPU, motherboard, RAM, storage and battery.
- **FR-6 MUST**: Columns are **Sensor | Current | Min | Max** with units. Min and max count from app start.
- **FR-7 MUST**: Include a "Reset Min/Max" button.
- **FR-8 MUST**: If a sensor isn't available, show `—`. Never crash and never show `NaN`.

### 4.3 Network
- **FR-9 MUST**: For each active adapter, show the name, type (Wi-Fi/Ethernet), IPv4, live ↓/↑ speed, and totals since app start. Calculate speed from `NetworkInterface.GetIPStatistics()` deltas.
- **FR-10 MUST**: Show a 2-minute incoming/outgoing line chart for the selected adapter.
- **FR-11 MUST**: Show a table of IPv4 + IPv6 TCP connections: **Direction | Process | PID | Protocol | Local IP | Port | Remote IP | Port | Remote is | State**. Get it via `GetExtendedTcpTable`.
  - A connection is **↓ Incoming** when its local port is one this PC listens on; otherwise it's **↑ Outgoing**.
  - "Remote is" shows Internet, LAN (private ranges) or Loopback.
  - Filter chips offer All / Incoming / Outgoing, and checkboxes hide listening sockets and loopback.
- **FR-11a MUST**: Show **VPN status** from local data only: the adapter is a known VPN driver or a PPP connection, plus whether it's Up. "All traffic" vs "split tunnel" comes from `GetBestInterface` for a public address, which is a routing-table lookup and sends nothing.
- **FR-11b MUST**: Add an **attack watch**, with a status strip (VPN · Firewall · Antivirus · Alerts) and a Security tab.
  - **Live, every 2 s:** SYN floods (≥ 20 half-open connections from one address), port scans (one address touching ≥ 10 local ports in 60 s), repeated hits on RDP/SSH/SMB/VNC/FTP/Telnet/WinRM (≥ 15 in 60 s), and an informational list of sensitive services listening on all networks.
  - **Every 2 min:** firewall profiles (registry, GPO first), antivirus from Security Center, Defender detections from the last 7 days (quarantined = warning, not removed = critical), and Security-log event 4625.
  - **Event 4625:** remote failed sign-ins (logon types 3/8/10 with a source IP) are warnings, and ≥ 10 from one IP in 24 h is critical. Local wrong-password prompts are informational only.
  - All wording says "possible". PCMON is not an antivirus.

### 4.4 Tasks (running vs. not)
- **FR-12 MUST**: Add a **Processes** tab with the columns Name, PID, Status, CPU %, RAM (MB) and Path. Status is **Running**, **Not Responding** (a windowed process where `Responding == false`) or **Suspended**. Make the table sortable and searchable.
- **FR-13 MUST**: Add an **End Task** control, available from right-click or a button. It asks for confirmation first (see §6). Show critical system processes (`csrss`, `wininit`, `lsass`, `smss`, `services`, `System`) with a lock icon, and do not let the user kill them.
- **FR-14 MUST**: Add a **Scheduled Tasks** tab, which is **read-only**. Columns are Name, State (**Running / Ready / Disabled**), Last Run, Next Run and Last Result. Use a filter chip to show Running, Not running or All.

### 4.5 Unused apps
- **FR-15 MUST**: List installed apps from `HKLM` and `HKCU` `…\Uninstall`, in both 64-bit and `WOW6432Node` views. Skip `SystemComponent=1`, entries with `ParentKeyName` (updates) and entries without an `UninstallString`.
- **FR-16 MUST**: Estimate **"Last used"** in this order:
  1. The newest `C:\Windows\Prefetch\*.pf` whose exe name matches an exe in the app's `InstallLocation`
  2. The UserAssist registry (ROT13-decoded) last-run time
  3. Otherwise show **"Unknown ¯\\\_(ツ)\_/¯"**

  Show Name, Publisher, Size, Installed On and Last Used.
- **FR-17 MUST**: By default show only apps unused for **≥ 90 days**. Filter chips offer 30 / 60 / 90 / 180 days and "Show all", and a checkbox hides apps with unknown usage.
- **FR-18 MUST**: Each row has a checkbox, and there is a **"Uninstall selected"** button. PCMON runs each app's **own** `UninstallString`, one at a time, and waits for each to exit. It never deletes files or registry keys itself and never does silent mass uninstalls.
- **FR-19 MUST**: Always show the confirmation dialog first, listing the selected apps. After the uninstallers finish, refresh the list.

## 5. Non-functional requirements
- **NFR-1 Footprint**: Idle CPU use is ≤ 2 % on a 4-core machine at a 1 s refresh. RAM use is ≤ 200 MB. Cold start is ≤ 3 s.
- **NFR-2 Portable**: No installer and no services. The only file PCMON writes is `%LOCALAPPDATA%\PCMon\settings.json`, which stores window size, theme and the unused-days threshold. Deleting the exe removes the app.
- **NFR-3 Privacy**: PCMON makes **zero** outbound network calls. No telemetry, no update checks and no "anonymous usage data".
- **NFR-4 Robustness**: Wrap every sensor, WMI and registry read in try/catch. Log errors to `%LOCALAPPDATA%\PCMon\pcmon.log`, rolling at 1 MB. The UI never shows a raw exception.
- **NFR-5 Compatibility**: Windows 10 22H2+ and Windows 11, x64. It must run on machines without a dedicated GPU, in which case the GPU tile shows "Integrated / N/A".
- **NFR-6 Accessibility**: The app is fully keyboard navigable, and colour is never the only signal. Every amber or red tile also shows an icon.

## 6. Humour spec (light sprinkle, professional everywhere else)
Humour lives **only** in status-bar messages, empty states, confirmations and the About box. Data labels and numbers stay serious. Keep the messages below in one `Strings.cs` so they're easy to change. Keep emoji out of in-app strings, because WPF renders newer emoji as empty boxes.

The status bar shows the single most important message, in this priority order:

| Situation | Message |
|---|---|
| Critical security alert | "Someone's knocking: {alert}. See Network → Security." |
| CPU ≥ 90 °C | "CPU is hotter than your takes. Check cooling." |
| GPU ≥ 90 °C | "GPU is running a fever. Check cooling." |
| RAM ≥ 95 % | "RAM is full. Chrome has entered the chat." |
| Any temp ≥ 80 °C | "Things are warming up. Keep an eye on the amber tiles." |
| Security warning(s) | "{n} security alert(s) on Network → Security. Probably fine. Probably." |
| RAM ≥ 85 % | "Memory is getting crowded. Maybe close a tab. Or forty." |
| Disk ≥ 90 % | "A disk is nearly full. Time for some digital decluttering." |
| Net idle 60 s | "The internet is quiet. Too quiet." |
| All green | "All systems nominal. Suspiciously nominal." |

Elsewhere in the app:

| Situation | Message |
|---|---|
| End-task confirm | "End **{name}**? It had hopes and dreams. (Unsaved work will be lost.)" |
| Kill blocked (critical) | "Nice try. Killing **{name}** would blue-screen us both." |
| No unused apps | "No unused apps found. You use everything? Respect." |
| Uninstall confirm | "Uninstall {n} app(s)? They won't be missed. Probably." |
| Sensor unavailable | "This sensor is shy." (shown as a tooltip on `—`) |
| About box | "PCMON v1.0 by Shatadip Majumder — Made with C#, coffee, and mild thermal anxiety." |

## 7. Build and deliverable
- **MUST** produce exactly **one file**: `PCMON.exe`. It is self-contained, so the user needs no .NET install.
  ```
  dotnet publish src/PCMon -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none
  ```
  (No trimming. WPF doesn't support it.)
- **MUST** embed an app icon, a version number and a `requireAdministrator` manifest.
- **MUST** use this repo layout: `src/PCMon/` (app) and `README.md`, which has a screenshot, a download link and a "Why does it need admin?" section.
- **SHOULD** add a GitHub Actions workflow that builds `PCMON.exe` when a `v*` tag is pushed and attaches it to a GitHub Release.

## 8. Out of scope (v1)
Installer or MSI, auto-update, fan control or overclocking, starting and stopping services, per-process bandwidth, pop-up notifications, blocking connections or editing firewall rules, public-IP or GeoIP lookups (these would need network calls), CSV export, and a tray icon. *PCMON watches. It does not tinker.*

## 9. Acceptance checklist
- [ ] Double-click `PCMON.exe` on a clean Win 11 VM. After the UAC prompt, the dashboard loads in ≤ 3 s.
- [ ] CPU temp, load and fan values match HWMonitor within ±2 units.
- [ ] Downloading a large file shows ↓ speed within ±10 % of Task Manager.
- [ ] A frozen app shows **Not Responding**, and End Task closes it after confirmation.
- [ ] Trying to kill `lsass` is blocked with the joke message.
- [ ] Scheduled Tasks shows the correct Running / Ready / Disabled state.
- [ ] Unused Apps lists an app unopened for 90+ days. Uninstall launches that app's own uninstaller, and the list refreshes afterwards.
- [ ] Wireshark shows no outbound traffic from PCMON.
- [ ] Deleting the exe and the `%LOCALAPPDATA%\PCMon` folder leaves no trace.
