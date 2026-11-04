---
title: FreshWay Gateway Service
subtitle: Installation, Troubleshooting and Gateway Switching Guide — Version 1.2
author: Mettler-Toledo FreshWay · Project Documentation
date: November 4, 2026
lang: en
geometry: margin=22mm
fontsize: 10pt
colorlinks: true
---

# Scope and Architecture

This revision updates guide 1.1: it adds `UrlSegmentMaxLength`, corrects the Java service name to **POSPrinterWS**, and documents how to switch between the Java and C# gateways for recovery and comparative testing.

The new service replaces only `gateway.jar` (Spring Boot). The Java backends remain necessary to communicate with the hardware through JavaPOS, PAL-Service and USB.

```text
Cache COS (%zprd11) -> HTTP :8082 -> ONE active gateway:
  Java: POSPrinterWS (gateway.jar)
  C#:   FreshWayGateway (FreshWayGateway.exe)
    -> :8083 MettlerWSV1.1_G.jar (Label / Green)
    -> :8084 MettlerWSV1.1_B.jar (Receipt / Linerless / Blue)
    -> JavaPOS -> PAL-Service -> USB -> FreshWay printer
```

Both gateways use the same port and routes. They cannot run simultaneously on that port. Cache requires no code changes to switch between them. The ports shown are typical values: verify the actual ports on each installation.

The previous service and its WinSW wrappers are usually located at:

```text
D:\sdppc\RestService\Mettler Driver gateway\
```

**Correspondence with the supplied source.** `FreshWayGateway.cs` retains its internal v1.0 identification and already uses `ctx.Request.RawUrl`. It does not contain the `JposException` filter described in the previous guide: do not assume that feature is present or interpret an empty response as success. This revision changes documentation, not service code. The latency improvements cited in the original guide have not been measured again.

# Preparation and Initial Checks

Run commands in **CMD as Administrator**. When using PowerShell, type `sc.exe` to avoid the `sc` alias. Schedule a printing pause and the Windows restart required by the HTTP.sys configuration.

Before changing the installation, record service startup types and executable paths:

```bat
sc.exe qc POSPrinterWS
sc.exe query POSPrinterWS
sc.exe qc FreshWayGateway
sc.exe query FreshWayGateway
sc.exe query Mettler_Driver_G
sc.exe query Mettler_Driver_B
wmic process where "name='java.exe'" get ProcessId,CommandLine
netstat -ano -p tcp | findstr ":8082"
```

If FreshWayGateway is not installed yet, error 1060 when querying it is expected. If WMIC is unavailable, inspect the processes using PowerShell:

```powershell
Get-CimInstance Win32_Process -Filter "name='java.exe'" |
  Select-Object ProcessId, CommandLine
```

Keep the EXE, INI, JAR files, WinSW XML files and previous logs. Do not uninstall **POSPrinterWS**: it is needed to return to the previous service. Confirm that its `BINARY_PATH_NAME` and configuration correspond to `gateway.jar`.

## Identify Services and Backends

```bat
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.NET4.xml" | findstr "<id>"
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.G.xml" | findstr "<id>"
type "D:\sdppc\RestService\Mettler Driver gateway\WinSW.B.xml" | findstr "<id>"
```

The Java gateway service name on this installation is **POSPrinterWS**. The usual backend services are **Mettler_Driver_G** and **Mettler_Driver_B**; verify them on the machine. If there is only one printer, only its corresponding backend may be installed or enabled. Do not enable modules that are not present.

## .NET Framework

```bat
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release
```

.NET Framework 4.7 or later is required (`Release >= 460798`). Compilation uses the Framework compiler; its folder is still named `v4.0.30319`.

## HTTP.sys URL Reservation

First check the existing reservation:

```bat
netsh http show urlacl url=http://+:8082/
```

If it is missing, create it for the account that will run the service. For the default LocalSystem service configuration:

```bat
netsh http add urlacl url=http://+:8082/ user="NT AUTHORITY\SYSTEM"
```

If a different account is used, substitute that account. The original guide granted access to EVERYONE; broader permissions are unnecessary if the service account already has access. For a console test, the account running it must also have permission; use an elevated CMD window. Do not delete existing reservations without checking which applications use them.

# Required HTTP.sys Configuration

`HttpListener` receives requests through HTTP.sys. Cache includes encoded ESC commands (`%1B`), carriage returns (`%0D`), LF (`%0A`) and SOH (`%01`) in print paths. In addition, the entire ingredients text is sent in **a single segment** after `/pos/p_printnormal/2/`.

These settings apply globally to HTTP.sys on the machine, not just to FreshWayGateway. Record the original values, including whether they were absent, before changing them:

```bat
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v AllowRestrictedChars
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v UrlSegmentMaxLength
```

## Allow Encoded Restricted Characters

```bat
reg add "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" ^
  /v AllowRestrictedChars /t REG_DWORD /d 1 /f
```

This setting allows the encoded restricted characters required by the existing protocol. It does not override length limits.

## Increase the Maximum URL Segment Length

The default `UrlSegmentMaxLength` value is 260 characters. Long ingredients text can exceed it even when lines are separated by `%0D`: carriage returns do not create new URL segments.

For this installation, set a limit of 4096:

```bat
reg add "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" ^
  /v UrlSegmentMaxLength /t REG_DWORD /d 4096 /f
```

If a higher value already exists because another application requires it, retain and document it rather than reducing it. This parameter does not increase other request length limits. If the longest label still fails, investigate the specific rejection before changing other limits.

## Restart and Verify

Apply both settings in the same maintenance window. Before restarting, ensure that only the selected gateway has automatic startup enabled; the gateway switching section explains how. Perform a controlled Windows restart:

```bat
shutdown /r /t 60
```

Save your work before running the command. After the restart:

```bat
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v AllowRestrictedChars
reg query "HKLM\SYSTEM\CurrentControlSet\Services\HTTP\Parameters" /v UrlSegmentMaxLength
```

Expected: `AllowRestrictedChars = 0x1` and `UrlSegmentMaxLength = 0x1000` (4096 decimal), or the higher limit retained earlier. Restarting only the C# service does not apply these kernel settings.

# Compile and Install the C# Gateway

## Prepare and Compile

Copy the source and INI file to the working folder:

```bat
mkdir C:\FreshwayPrinterService
cd /d C:\FreshwayPrinterService
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" ^
  /target:exe /platform:x64 ^
  /r:System.Web.dll /r:System.ServiceProcess.dll ^
  /out:FreshWayGateway.exe FreshWayGateway.cs
```

Compilation must finish without errors. If an existing C# service is running, stop it and wait for `STOPPED` before replacing its executable. Editing the `.cs` file does not change the deployed binary: recompile, copy the EXE and restart the service after any code change.

## Configure gateway.ini

The file must be next to the EXE. Place comments on separate lines: the supplied parser does not interpret comments after numeric values.

```ini
; Port used by Cache
port=8082
; Label / Green backend
port_green=8083
; Receipt / Linerless / Blue backend
port_blue=8084
backend_timeout=30000
```

Verify the ports against the JAR command lines and the Cache printer configuration. Do not change the Cache port between comparative tests if both gateways listen on 8082.

## Register the C# Service Without Starting It Yet

If the service does not exist:

```bat
sc.exe create FreshWayGateway ^
  binPath= "C:\FreshwayPrinterService\FreshWayGateway.exe" ^
  DisplayName= "FreshWay Gateway Service" ^
  start= demand
sc.exe description FreshWayGateway "C# HTTP gateway for FreshWay printers"
sc.exe failure FreshWayGateway reset= 60 actions= restart/5000/restart/10000/restart/30000
```

If it already exists, check `sc.exe qc FreshWayGateway` and its path instead of creating it again. Keep it stopped until port 8082 is free. Apply the HTTP.sys settings and perform the scheduled restart before validating ingredients printing.

# Switch Between the New and Previous Services

Use this section both to restore printing and to compare results. Keep the required backends running in both modes. Do not stop PAL-Service or the G/B services when changing only the gateway.

## Rules Before Every Switch

1. Pause new Cache jobs and allow the current print job to finish. Keep the test label and logs. Do not switch gateways in the middle of a transaction.
2. Record which service is active and its startup type using `sc.exe query` and `sc.exe qc`.
3. Stop the outgoing gateway and actually wait for `STATE : 1 STOPPED`. `sc.exe stop` is asynchronous; repeat the query while it reports `STOP_PENDING`. Do not proceed if it does not stop.
4. If FreshWayGateway is running with `--console`, also stop that instance by pressing Enter in its console. Stopping the service does not stop a separate console instance.
5. Check the port before starting the other gateway:

```bat
netstat -ano -p tcp | findstr ":8082"
```

There must be no listener on local port 8082. `TIME_WAIT` connections are not listeners. Identify an unexpected PID with `tasklist /fi "PID eq NUMBER"`; HTTP.sys may appear as PID 4. In that case, use `netsh http show servicestate` to identify the registered application: do not attempt to terminate the System process.

## Return to the Previous Service: C# -> Java (POSPrinterWS)

Run these steps in stages, checking each state:

```bat
REM Prevent C# from starting again when Windows restarts
sc.exe config FreshWayGateway start= disabled
sc.exe stop FreshWayGateway
sc.exe query FreshWayGateway
```

Wait for `STOPPED`, stop any C# console instance, and verify that 8082 is free using the procedure above. Then:

```bat
sc.exe config POSPrinterWS start= auto
sc.exe start POSPrinterWS
sc.exe query POSPrinterWS
sc.exe qc POSPrinterWS
```

Wait for `RUNNING`; the Spring Boot gateway may need several additional seconds before it starts listening. Confirm a listener on 8082, the required backends in `RUNNING`, and an active `gateway.jar` process. The stable configuration for this mode is:

- **POSPrinterWS:** automatic and running.
- **FreshWayGateway:** disabled and stopped, with no console instance.
- **Mettler_Driver_G / Mettler_Driver_B:** unchanged, according to installed hardware.

Resume with a complete print job from Cache: a short label and the same long ingredients label. Verify content, line breaks, barcode and paper feed/cut. A service reporting `RUNNING` is not enough. If the failed attempt left residual state, close and reinitialize the printer session through the normal Cache workflow before comparing results.

There is no need to remove the new EXE, uninstall the C# service, remove the URL reservation or revert HTTP.sys to return to Java. Keeping these settings makes it easier to switch back again.

## Return to the New Service: Java -> C# (FreshWayGateway)

With printing paused:

```bat
sc.exe config POSPrinterWS start= disabled
sc.exe stop POSPrinterWS
sc.exe query POSPrinterWS
```

Wait for `STOPPED` and for port 8082 to be free. Confirm that HTTP.sys has already been configured and Windows restarted. Then:

```bat
sc.exe config FreshWayGateway start= auto
sc.exe start FreshWayGateway
sc.exe query FreshWayGateway
sc.exe qc FreshWayGateway
```

Wait for `RUNNING`, check the listener and backends, and repeat the same complete print jobs from Cache. The stable configuration for this mode is:

- **FreshWayGateway:** automatic and running.
- **POSPrinterWS:** disabled and stopped.
- **G/B backends:** unchanged.

If C# fails to start or print, keep the error details and follow the return-to-POSPrinterWS procedure. Do not start both services in an attempt to recover the port.

## Temporary C# Console Test

Stop and disable POSPrinterWS and stop the FreshWayGateway service; wait until both are stopped and 8082 is free. Keep the C# service set to manual startup during the test:

```bat
sc.exe config FreshWayGateway start= demand
cd /d C:\FreshwayPrinterService
FreshWayGateway.exe --console
```

Watch requests while Cache prints. When finished, press Enter, verify that the port has been released, and choose one of the two procedures above to leave only one service set to automatic startup. Do not leave a console test as the permanent configuration.

## Record Comparative Tests

Use the same product, complete text, layout, printer, backends and Cache configuration. Run jobs sequentially, never simultaneously.

For each gateway, record: date/time, deployed binary, ports, HTTP.sys values, short and long label results, line breaks, HTTPERR errors, Cache responses and observed timings. Keep logs before repeating a test. If HTTP.sys was changed between tests, record it: the conditions are no longer identical.

## Optional HTTP.sys Restoration

This is not part of a normal gateway switch. If the C# gateway is permanently removed and HTTP.sys restoration is required, first review the impact on other applications and use exactly the values recorded before installation. If a value did not exist, delete only that value; if it existed, restore its original data and type. Do not indiscriminately import the entire registry branch. Schedule another Windows restart to apply the restoration.

# Validate After Installation or a Gateway Switch

```bat
sc.exe query POSPrinterWS
sc.exe query FreshWayGateway
sc.exe query Mettler_Driver_G
sc.exe query Mettler_Driver_B
netstat -ano -p tcp | findstr ":8082"
```

Only the selected gateway must be active. A backend being absent because its hardware is not installed is not, by itself, an error.

Run a complete open, claim, enable, print and close sequence from Cache. Physically inspect a short label and the long label that previously produced `400 - URL -`. In C# mode, the long `p_printnormal` request must reach the gateway log without generating a new HTTPERR rejection. Confirm internal CR characters and accented characters; do not replace `$c(13)` with `$c(10)` to hide an HTTP rejection.

An isolated `p_open` call only validates opening: it can change the gateway's state or active route and does not demonstrate that a complete print job works. Avoid mixing manual tests with production jobs.

# Configuration by Printer Type

| p_open device | Default port | Backend |
|---|---:|---|
| LabelPrinter-1 | 8083 | Green |
| LabelPrinter-1-Sim | 8083 | Green, simulator |
| ReceiptPrinter-1 | 8084 | Blue |
| LinerlessPrinter-1 | 8084 | Blue |

In Cache: MTA#1 corresponds to Receipt, MTA#2 to Label and MTA#3 to Linerless. Configure only devices that are present and verify their `jpos.xml` entries. Do not redirect a nonexistent printer to another backend as a substitute for installing it.

To add Label or Linerless, verify the physical module, power supply and corresponding UPOS packages. For Label, check the FLP package and its registration:

```bat
reg query "HKLM\SOFTWARE\OLEforRetail\ServiceOPOS\POSPrinter" /s
```

Confirm `LabelPrinter-1` in the appropriate configuration; for Linerless, confirm `LinerlessPrinter-1` in the FP backend. Enable Green for Label or Blue for Receipt/Linerless according to the table. Keep the actual ports in `gateway.ini`.

If the USB driver was reinstalled or removed, follow the Mettler installer procedure and use the INF files specific to the hardware. The original guide mentions `LabelPrinterFLP2.inf` for VID_0EB8/PID_300C and `LabelPrinterFLP01.inf` for PID_3014; verify the model and package before installing them. Do not reinstall drivers to diagnose `400 - URL -`, which occurs before hardware access.

# Troubleshooting

## HTTPERR: 400 - URL - With No C# Gateway Log Entry

```text
C:\Windows\System32\LogFiles\HTTPERR\httperr*.log
```

HTTP.sys rejected the request before `Handle`. Check `AllowRestrictedChars`, `UrlSegmentMaxLength` and the subsequent restart. The observed ingredients request contains `%1B`, several `%0D` sequences and a segment exceeding the default limit. The generic `URL` reason alone does not distinguish between all validations: confirm using the same label after applying the settings.

## The Request Appears in the Gateway Log but Does Not Print

Check ports, Java processes, complete responses and backend logs. The supplied C# source uses RawUrl, but .NET URI processing also takes place when creating the outgoing request. Verify what Java receives before automatically attributing every error to an outdated version.

The supplied code does not propagate the backend HTTP status to the client and truncates console messages. A Java error may reach Cache as HTTP 200 with an error body or an empty body. Do not treat HTTP 200 or absent text as proof of successful printing; keep the backend logs. Improving error handling requires a separate code revision.

## Port Busy or Service Fails to Start

Check that the other gateway and any `--console` instance are stopped. Review `sc.exe qc`, the EXE path, URL reservation, service account and Windows events. Wait for `STOPPED` before starting the replacement. If POSPrinterWS fails to start when switching back, check its WinSW wrapper logs, Java runtime and original configuration.

## Backend Connection Refused

Confirm that the device's backend is installed, in `RUNNING`, and listening on the configured port. Keep Green/Blue active when switching gateways; do not change USB drivers because of an HTTP connection error.

## All Calls Look Correct but the Label Is Blank

Check the complete sequence, page mode, transaction state, JavaPOS/UPOS logs and PAL-Service. The original guide cites `D:\sdp_mta_prn_SCA01.txt`; confirm the actual path on the machine. Compare with POSPrinterWS using exactly the same layout and product.

In Cache, inspect `zw ^posfwlog` and keep its contents before clearing or repeating tests. Do not delete logs as an installation step.

# Revision History and Files

- **1.2 — November 4, 2026:** UrlSegmentMaxLength=4096, Java service POSPrinterWS, complete return-to-Java procedure, C# reactivation, comparative testing and ingredients validation. Clarifies differences between the source and guide 1.1 and corrects the INI example to avoid inline comments. This edition is in English, matching the original guide.
- **1.1 — original document:** RawUrl and the AllowRestrictedChars requirement. The exception filter mentioned there is not implemented in the supplied source.

The four originally supplied files are preserved unchanged in the repository. The original PDF is historical and may contain earlier names; use revision 1.2 for new installations. The Markdown for this revision is the editable source of the new PDF.
