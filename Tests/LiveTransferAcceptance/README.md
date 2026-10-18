# Real OneDrive transfer acceptance

Opt-in Windows checks against a signed-in OneDrive account. The production WinUI
application performs the actual uploads, downloads, persistence and restoration.
This helper generates a disposable 256 MiB + 123 byte file, prepares a saved task,
reads copies of encrypted checkpoints, and verifies the downloaded SHA-256.

Use an explicitly designated test account or obtain authorization to use the
current account. Allow at least 512 MiB free in OneDrive. Create a fresh root
folder named `CF-Acceptance-<timestamp>` with the application. Do not reuse a
folder containing other files. Close the application before seeding a task.

Build the application (Debug/x64) and this project, then run from the repository
root. Choose a new ignored output directory for each run:

```powershell
dotnet build Tests/LiveTransferAcceptance/LiveTransferAcceptance.csproj
$testRoot = 'bin/LiveAcceptance-<timestamp>'
$appData = 'C:/Users/<user>/AppData/Local/OneDriveSimpleManagementTool'
$appExe = 'bin/x64/Debug/net8.0-windows10.0.22621.0/OneDrive Simple Management Tool.exe'
$helper = 'Tests/LiveTransferAcceptance/bin/Debug/net8.0-windows/LiveTransferAcceptance.dll'
$cloudFolder = 'CF-Acceptance-<timestamp>'
dotnet $helper prepare $testRoot
dotnet $helper seed-upload $testRoot $appData appsettings.json $cloudFolder
```

Use the actual user's application data path: a sandbox can redirect
`LOCALAPPDATA`. Seeding requires exactly one configured drive and silent
authentication. It copies the protected MSAL cache into the ignored test
directory; it never logs tokens, account IDs or upload/download URLs. The seed
uses a 2048 KiB/s task limit to leave time for fault injection. Admission is
through a saved task, so drag-and-drop and file-picker behavior are outside this
test's coverage.

Run the proxy in a separate terminal, then launch the app:

```powershell
dotnet $helper proxy $testRoot
# In another terminal:
& Tests/LiveTransferAcceptance/Start-TestApp.ps1 -TestDirectory $testRoot -Application $appExe
```

The loopback proxy forwards HTTPS CONNECT tunnels without decrypting TLS. Only
the child app receives its proxy environment variables; Windows proxy settings,
network adapters and other applications are unchanged. An existing HTTP proxy
is used upstream when configured. This tests termination and rejection of real
network connections, not a physical adapter outage or every timeout scenario.

1. In Upload tasks, continue the restored test task. Wait for a nonzero saved
   checkpoint. `inspect` reports server-confirmed upload bytes and durably saved
   download bytes from a copy of the store, leaving live files untouched:
   `dotnet $helper inspect $testRoot "$appData/Transfers"`.
2. Create `$testRoot/offline` to close active tunnels and reject new connections.
   Wait for failure/retry behavior and record the state and checkpoint. Remove
   only that marker to restore connectivity, then retry the failed task.
3. Once the checkpoint advances, run the launcher with `-Crash`. It verifies
   the recorded process ID, executable path and creation time before forcefully
   terminating only the test process.
4. Launch again. Expect an interrupted task with a nonzero checkpoint; continue
   it and wait for completion. Capture snapshots and UI screenshots.
5. Close the app, run `seed-download` with the same arguments as `seed-upload`,
   launch again and repeat steps 1–4 for the Download tab. Downloads go into
   `$testRoot/download/CF-Acceptance-256MiB.bin`.
6. Verify the complete round trip:
   `dotnet $helper verify $testRoot "$testRoot/download/CF-Acceptance-256MiB.bin"`.
   Both byte length and SHA-256 must match `source.json`.
7. Close the test app and create `$testRoot/stop-proxy`. Confirm the proxy exits
   and remove its copied authentication cache. Preserve a sanitized report;
   keep encrypted snapshots and screenshots out of commits. Remove only the
   explicitly identified disposable cloud/local fixtures when no longer needed.

Do not rebuild the helper while its proxy is running. Do not seed records while
the app is open, modify existing task IDs, log capability URLs, or inspect the
authentication cache as plaintext.
