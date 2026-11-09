# FileFlow on Windows Server 2022 and 2025 Standard (x64)

## Platform support and deployment model

The application targets .NET 10, officially supported by Microsoft on Windows
Server 2022 and Windows Server 2025 (x64). Use **Desktop Experience** if you
want to view the WinForms task manager and system tray locally. The background
service supports Server Core, where the GUI is not available; configure tasks
using appropriately secured `C:\ProgramData\FileFlow\config.json` or from a
Desktop Experience installation. Do not copy a password into config.json.

Microsoft support matrix: https://learn.microsoft.com/dotnet/core/install/windows

This source archive does not include a compiled installer. To create a usable
MSI on a Windows build computer, use the build steps below. A full `win-x64`
self-contained publish is packaged so target servers **do not require the
.NET runtime**. Build and test your own binaries before production use.

## Build MSI on Windows Server/Windows 11

Prerequisites: .NET 10 SDK, internet access for NuGet restore, and PowerShell
(on Windows, built in). Visual Studio 2026 with .NET desktop development
workload is recommended. The WiX SDK `WixToolset.Sdk/7.0.1` will be restored
from NuGet automatically; please review and comply with its license.

1. Extract source archive and open `FileFlow.sln` to review project.
2. Open **PowerShell** in the extracted `FileFlow` folder.
3. Execute:

   ```powershell
   .\scripts\Build-MSI.ps1
   ```

4. Output: `dist\FileFlow-Server-x64.msi` and SHA256 printed in the terminal.
   (If you already published fresh binaries, use `-SkipPublish`.)
5. Before distributing widely, sign MSI with your enterprise code-signing
   certificate and test in an isolated Windows Server 2022 and 2025 VM.

Or push the project to a GitHub repository and run the `Windows Server MSI`
GitHub Actions workflow. After build, download the `FileFlow-Server-x64-MSI`
artifact. The workflow also attempts a service install/uninstall smoke test on
hosted Windows Server 2022 and 2025 runners; these runners do not prove
functionality on every Standard Edition configuration.

## MSI install and uninstall

Stop and remove any **previous script-installed** `FileFlowService` before
switching to MSI deployment. An MSI cannot reliably adopt an already installed
service originally created outside Windows Installer.

Run as administrator:

```powershell
msiexec.exe /i "C:\path\to\FileFlow-Server-x64.msi" /l*v "C:\Temp\FileFlow-install.log"
```

For unattended deployment:

```powershell
msiexec.exe /i "C:\path\to\FileFlow-Server-x64.msi" /qn /norestart /l*v "C:\Temp\FileFlow-install.log"
```

The MSI uses Windows Installer service registration (not install-time PowerShell):

- Installs service to `C:\Program Files\FileFlow\Service` and manager to
  `C:\Program Files\FileFlow\Manager`.
- Creates `FileFlowService` using **Automatic** startup and native recovery
  actions (restart after 5/30/60 seconds).
- Creates protected `C:\ProgramData\FileFlow` and `Logs` directories, with
  read access for Users and write access for SYSTEM/Administrators.
- Adds a Start Menu shortcut and machine-wide Manager startup at sign-in.
- Removes the service, installed files and startup entry on uninstall, but
  preserves existing task settings/history/logs (nonempty directories).
- Uses **LocalSystem** as initial service account. You must configure an
  appropriate identity for authenticated remote network shares.

To check installation:

```powershell
Get-Service FileFlowService
sc.exe qfailure FileFlowService
Get-Content "$env:ProgramData\FileFlow\status.json"
```

Before upgrading or uninstalling, use the notification-area icon to exit any running FileFlow Manager instances.

To uninstall: Windows Settings > Installed apps > FileFlow Automated File
Transfer, or an elevated prompt with `msiexec /x "full-path-to-msi"`.

**Never run both the legacy PowerShell installer and MSI install together.**
They target the same Windows service name and machine-wide startup entry.

## Network share accounts

The service starts under LocalSystem by default. For a share on a different
server, its domain machine account might be used over SMB and is often
*not* authorized. Recommended: assign a dedicated least-privilege domain
service account or a managed service account (gMSA) with the required share
and NTFS permissions and "Log on as a service" rights.

1. After MSI install, open `services.msc` as Administrator.
2. Open **FileFlow Automated File Transfer > Properties > Log On** and set
   the service identity using the organization's approved credential policy.
3. Grant that service identity **Modify** permission to
   `C:\ProgramData\FileFlow` (including `Logs`) via the Security tab; keep
   ordinary users read-only.
4. Grant source read and destination create/write on UNC shares. For Move,
   also grant delete on source. Use `\\server\share\directory` not a mapped
   drive letter.
5. Restart the service and run sample Copy and Move tasks in a temporary folder.

If a domain account changes after a repair or upgrade, recheck the Windows
service Log On identity and ProgramData ACL before running Move tasks.

## Basic Server acceptance test

1. Install MSI (Desktop Experience), confirm service status is Running.
2. Launch FileFlow Manager via Start menu; minimize to tray.
3. Add two test rules pointing to temporary files, check Copy and Move paths.
4. Confirm Repeat causes scanning roughly every five seconds when idle and
   daily logs appear under `C:\ProgramData\FileFlow\Logs`.
5. Sign out of Windows; verify transfers continue with no interactive user
   session. A service cannot display a tray icon until someone signs in.
6. Reboot; verify the service automatically starts and the tray manager starts
   when a user signs in.
7. Configure an SNMPv2c test trap to a monitoring receiver, confirm it is
   received. Configure missed-heartbeat alarms; stopped services cannot emit
   traps.
8. Trigger a deliberate permission error in a test folder; confirm the
   error is logged and a trap arrives. Restore permissions, confirm recovery.
9. Uninstall; verify `FileFlowService` is gone while audit logs remain.

## Important limitations

- SNMPv2c traps/heartbeat only, not SNMP GET agent or SNMPv3.
- Server Core has no Windows Forms tray interface.
- MSI has **no built-in domain credential dialog**; use Services MMC / gMSA
  management after installing (no plaintext password stored in the MSI).
- No load testing, Windows Server Standard acceptance testing or Authenticode
  signing has yet been completed in the current non-Windows development environment.
