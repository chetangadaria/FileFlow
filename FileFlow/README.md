# FileFlow - automated Windows file transfer

A Visual Studio .NET 10 solution for transferring files from local folders or UNC
network shares to local folders or UNC network shares, with an independent Windows
Service, a Windows Forms notification-area manager, daily CSV logs and SNMPv2c traps.
The Windows Service supports **Windows Server 2022 Standard** and
**Windows Server 2025 Standard** (x64), including Server Core. The tray
interface requires Windows Server **Desktop Experience**.

**MSI packaging:** A WiX v7 setup project, Windows build script and GitHub
Actions MSI workflow are included. This source archive does **not** contain
a compiled `.msi` binary; see [`docs/WINDOWS-SERVER-MSI.md`](docs/WINDOWS-SERVER-MSI.md)
for build and installation steps.

## What is included

- `src/FileFlow.Manager` - WinForms app with task grid, Add/Edit/Delete, Copy/Move,
  Enabled, Repeat, Include subfolders, conflict policy, SNMP settings, test trap,
  service health, daily logs and notification-area right-click **Exit**.
- `src/FileFlow.Service` - Windows Service. A background worker scans each enabled
  rule every **five seconds** when idle, regardless of interactive sign-in.
- `src/FileFlow.Core` - configuration, file-transfer safety checks, journal,
  one-time completion persistence, status snapshots and outbound SNMPv2c traps.
- `tests/FileFlow.Core.SmokeTests` - basic copy/move/configuration/SNMP tests.
- `scripts/Build-Release.ps1` - builds self-contained Windows x64 executables.
- `scripts/Install-FileFlow.ps1` - installs an automatically starting and
  auto-restarting Windows Service and an at-login tray icon.
- `scripts/Uninstall-FileFlow.ps1` - removes the service and autostart entry.
- `docs/FILEFLOW-MIB.txt` - example notification MIB.
- `installer/FileFlow.Setup.wixproj` - WiX v7 MSI authoring for service,
  Windows Server-friendly deployment and per-machine tray auto-start.
- `scripts/Build-MSI.ps1` - one-command Windows build of published apps
  and an installable x64 MSI.
- `.github/workflows/msi-windows.yml` - Windows 2025 build and 2022/2025
  installer smoke-test workflow.
- `docs/WINDOWS-SERVER-MSI.md` - Server compatibility and MSI guide.

## Requirements

- Windows 10/11 x64 or Windows Server 2022/2025 Standard x64; Server Core
  supports the service but not WinForms/tray management.
- A Windows x64 **build** machine, and for MSI creation WiX Toolset
  `WixToolset.Sdk/7.0.1` (automatically restored through the build project).
- Visual Studio 2026 with the .NET desktop development workload and .NET 10 SDK,
  or the .NET 10 SDK and Windows build environment.
- Administrative rights to install the service or save protected configuration.
- For network folders, an appropriate **Windows service identity** that has
  read permission on source and write permission on destination. A Move task
  additionally requires permission to delete the source.

This package contains **source code**, not compiled `.exe` or `.msi` installers. It has
not been compiled/tested on Windows in this environment. Build and smoke-test it
on your own Windows machine before any production file transfers.

## Open in Visual Studio and build

1. Extract the source folder and open `FileFlow.sln` in Visual Studio.
2. Allow NuGet restore for `Microsoft.Extensions.Hosting` and
   `Microsoft.Extensions.Hosting.WindowsServices`, both version `10.0.14`.
3. Select Release / Any CPU, then **Build Solution**.
4. From the project root, run `dotnet run --project tests/FileFlow.Core.SmokeTests`
   to check the core transfer behavior in a temporary directory.
5. Run PowerShell:

   ```powershell
   .\scripts\Build-Release.ps1
   ```

   This publishes two **self-contained `win-x64`** applications in
   `publish\Service` and `publish\Manager`. End-user machines do not need the
   .NET SDK for these self-contained builds.

## Create a Windows Server MSI

On Windows with the .NET 10 SDK, execute:

```powershell
.\scripts\Build-MSI.ps1
```

After publish, this compiles `installer/FileFlow.Setup.wixproj` and produces
`dist\FileFlow-Server-x64.msi`. The MSI installs the Windows Service, tray
manager, Start Menu shortcut, per-machine sign-in startup, secure ProgramData
folders, and service failure-restart actions. MSI uninstall removes installed
components and leaves nonempty logs/configuration folders in place.

**Important:** The MSI build requires Windows build tools and NuGet access.
The MSBuild/WiX project is included and statically inspected but has not
been compiled or installed in this environment. See
`docs/WINDOWS-SERVER-MSI.md` for Server 2022/2025 testing, setup, and service
account permissions. **Do not use the PowerShell install script on top of an
MSI installation or vice versa.**

## Install on the target Windows computer (legacy script method)

Open Windows PowerShell **as Administrator** in the FileFlow project directory.
Choose one installation method:

```powershell
# Standard install. Service account = LocalSystem.
.\scripts\Install-FileFlow.ps1

# Preferred when network shares require a domain service account:
.\scripts\Install-FileFlow.ps1 -PromptForServiceCredential
```

The second option asks for a Windows credential using a PowerShell credential
prompt, not a password in source code. If a dedicated account is used, ensure it
has **Log on as a service** permission (Local Security Policy or domain policy).
For group-managed service accounts (gMSAs), configure the account using your
organization's approved deployment process, and grant it modify access under
`C:\ProgramData\FileFlow`.

The install script:

- Copies the executables to `C:\Program Files\FileFlow`.
- Creates **FileFlowService** with startup **Automatic** and Service Control
  Manager recovery/restart actions.
- Restricts `C:\ProgramData\FileFlow` so regular Users can read, while SYSTEM,
  Administrators and the optional service account can modify files.
- Registers FileFlow Manager under the machine's Run registry key. When a user
  next signs in, the manager appears in the **notification area**, initially
  hidden; the service does not depend on it.

To run the manager immediately, open:

`C:\Program Files\FileFlow\Manager\FileFlow.Manager.exe`

For **task changes**, exit the manager from its notification icon, then use
**Run as administrator** on `FileFlow.Manager.exe`. Reading status/logs works
without administrator rights. This protects the service from untrusted users
changing transfer destinations. If a manager is already running in a session,
close that tray instance before launching an elevated instance.

## Set up a transfer task

1. Open Manager, select **Transfer tasks > Add task**.
2. Give the task a name, e.g. `Office invoices`.
3. Source: `C:\Incoming` or `\\fileserver\incoming\invoices`.
4. Destination: `D:\Archive` or `\\backupserver\archive\invoices`.
5. Action: **Copy** (keep source) or **Move** (delete source *after verification*).
6. Check **Enabled** to activate the rule.
7. Check **Repeat every scan** for ongoing work. Uncheck it for exactly one
   successful pass. One-time completion persists after a service restart.
8. Optionally check **Include subfolders** and select a conflict policy:
   **Skip** (safe default) or **Overwrite** (replace changed destination files).
9. Select **Save all**. The service reloads the changes on its next 5-second pass.

**Example:** `\\NAS01\ScanInbox` -> `\\NAS02\DocumentArchive` as **Move** with
**Repeat** checked means every scan looks for new files in the inbox and moves
them to the archive. A source file that has been modified very recently is
held back; a writer holding the file open should also prevent transfer.

**One-time tasks:** A one-time rule runs one complete scan, then marks itself
completed (including when the source folder was empty). If a file was too new
or still being written, the rule waits for a subsequent scan rather than marking
completed. Editing and re-saving the task gives it a new run version and arms
another one-time pass. Just saving the overall configuration does *not* rearm it.

**Copy repeat behavior:** If destination size and last-modified timestamp match
source, the engine skips the file instead of recopying it every five seconds.
When destination content is changed but size/timestamp are unchanged, it may
not be detected; choose stronger content-based replication software if this
is required.

**Move verification:** Move is implemented as copy to a temporary destination
file, SHA-256 verification, publish destination, then source deletion. This
works across disks and SMB shares. A verified destination from a prior failed
source deletion is also recognized on retry. If a destination collision has
*different* content and conflict policy is Skip, the original source is kept.

## Logs and status

| Path | Purpose |
|------|---------|
| `C:\ProgramData\FileFlow\config.json` | Shared configuration; never store account passwords here |
| `C:\ProgramData\FileFlow\Logs\YYYY-MM-DD.csv` | New local-date daily CSV transfer/event log |
| `C:\ProgramData\FileFlow\status.json` | Service heartbeat, scan time and per-task health |
| `C:\ProgramData\FileFlow\oneshot.json` | Completed one-time task versions; preserve for reliability |

A CSV record includes local timestamp, event (COPIED, MOVED, ERROR, recovery),
task, action, source, destination and details. The manager's **Daily transfer
logs** tab previews the most recent 300 lines. Use **Open logs** to open the
folder in Explorer.

The manager minimizes to the notification area. Closing the window also hides
it there. Right-click the icon > **Exit tray application (service keeps running)**
closes only the manager. The Windows Service remains active, including at the
sign-in screen and while no user is logged in.

## Configure SNMP monitoring

1. In Manager > **SNMP and monitoring**, enable **SNMPv2c traps**.
2. Enter the SNMP monitoring/trap receiver server (IP or hostname).
3. Set UDP destination port (normally **162**), community string and heartbeat
   interval (default **60 seconds**).
4. Select **Save configuration**; optionally select **Send test trap**.
5. Permit outbound UDP to that server from the Windows Service and configure
   the monitoring system to ingest traps. The test trap originates from the
   interactive Manager process; the real service traps originate from the
   Windows Service account.

Traps use these example OIDs (see `docs/FILEFLOW-MIB.txt`):

| SNMP trap event | OID |
|---|---|
| Started | `1.3.6.1.3.9999.0.1` |
| Task failed | `1.3.6.1.3.9999.0.2` |
| Recovered | `1.3.6.1.3.9999.0.3` |
| Heartbeat | `1.3.6.1.3.9999.0.4` |
| Stopping | `1.3.6.1.3.9999.0.5` |
| Test | `1.3.6.1.3.9999.0.6` |

Each includes standard `sysUpTime.0`, `snmpTrapOID.0`, service, event, task,
message and machine name varbinds. The sender uses SNMPv2c **traps only** (no
inbound SNMP GET listener/agent, SNMP INFORM acknowledgements, or SNMPv3).
SNMPv2c is not encrypted: use a trusted management network or expand the
implementation to support authenticated/encrypted SNMPv3.

**Important:** A process that is stopped, crashed or whose machine is offline
cannot send a failure trap. Configure your monitoring server to alert when
**2-3 expected heartbeats are missed** (e.g. 180 seconds for a 60-second
interval). Use the Windows Service Control Manager recovery policy for restarts.

`1.3.6.1.3.9999` is a **development/example experimental OID**. For a production
MIB, replace it in `SnmpV2Traps.BaseOid` and `docs/FILEFLOW-MIB.txt` with a
subtree under an enterprise number registered to your organization. Traps are
sent over UDP and do not confirm arrival; checking the receiver is important.

## Troubleshooting

```powershell
Get-Service FileFlowService
Start-Service FileFlowService
Restart-Service FileFlowService
Get-Content "$env:ProgramData\FileFlow\status.json"
Get-ChildItem "$env:ProgramData\FileFlow\Logs"
```

- **UNC path inaccessible:** use `\\SERVER\SHARE\folder`, *not* `Z:\folder`.
  Mapped drives are per logon session and are not reliable in Windows Services.
  Check **both** SMB share and NTFS permissions for the service account.
- **No changes saved:** exit the tray app and run the Manager elevated. `config.json`
  is administrator-writable to prevent non-privileged redirection of moves.
- **Move does not delete:** inspect collisions and source delete permissions.
  If content differs from an existing destination and Skip is selected, no
  overwrite/deletion occurs. 
- **Files are deferred:** allow the producer to close the file and let its
  modification time age beyond two seconds. Avoid using this service as a
  transactional queue for actively changing files.
- **No SNMP alerts:** check destination port, firewall, monitoring receiver,
  community and UDP routing. Compare `FILEFLOW-MIB.txt` with the OID events.
- **No service UI when logged out:** by design Windows Services do not display
  notification icons without an interactive user session.
- **Long transfer:** processing is sequential. A large transfer can take longer
  than 5 seconds; it is never started concurrently by a second scan.

## Production considerations

This is a functional reference implementation to build and test, not a
certified enterprise file-replication product. Validate it in a staging folder
before using **Move** on important documents. Network outages, ACL changes,
multiple independent writers, source/destination time precision, disk failure,
server restarts, network shares, antivirus locks and failover scenarios require
environment-specific testing. For important workloads consider backups,
retention, rate limits and an application-level hand-off protocol (e.g., source
files renamed to `.ready` after the producer closes them). A five-second scan
is a **polling interval**, not a guaranteed five-second completion SLA.

## Uninstall

From an elevated PowerShell prompt, first exit the tray icon:

```powershell
.\scripts\Uninstall-FileFlow.ps1
# Delete configuration and logs too ONLY if you deliberately want to remove them:
.\scripts\Uninstall-FileFlow.ps1 -DeleteConfigurationAndLogs
```

The standard uninstall leaves logs, config and one-time history intact.
For an MSI installation, uninstall using **Installed apps** or
`msiexec.exe /x "C:\path\to\FileFlow-Server-x64.msi"` instead of these scripts.
