#Requires -Version 5.1
#Requires -RunAsAdministrator
# Run only on a disposable Windows CI machine: exercises real SCM and EXE updates.
param([Parameter(Mandatory = $true)][string] $Executable)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Executable).Path
$install = Join-Path $env:ProgramFiles 'DeusKVM Companion'
$binary = Join-Path $install 'DeusKVM.Companion.exe'
$data = Join-Path $env:ProgramData 'DeusKVM'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$runName = 'DeusKVMCompanion'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'DeusKVM Companion.lnk'
function Get-TrayStartupCommand {
    # Get-ItemPropertyValue throws a terminating error for an absent value in
    # Windows PowerShell 5.1, even with -ErrorAction SilentlyContinue. Read the
    # key's properties instead; missing startup registration is expected here.
    if (-not (Test-Path -LiteralPath $runKey)) { return $null }
    $properties = Get-ItemProperty -LiteralPath $runKey
    $property = $properties.PSObject.Properties[$runName]
    if ($null -ne $property) { return $property.Value }
    return $null
}
if ((Get-Service DeusKVMCompanion -ErrorAction SilentlyContinue) -or
    (Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $data) -or
    (Test-Path -LiteralPath $shortcut) -or
    ($null -ne (Get-TrayStartupCommand))) {
    throw 'This test requires a clean machine without an existing DeusKVM installation.'
}
function Start-CompanionProcess {
    param([string] $Path, [string[]] $Command = @())
    # Retain the native process handle, including for short-lived commands.
    # Windows PowerShell's Start-Process -PassThru can lose their exit code.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.UseShellExecute = $false
    $start.RedirectStandardError = $true
    # All test arguments are fixed switches or the literal "manual".
    if ($Command | Where-Object { $_ -notmatch '^[a-z-]+$' }) { throw 'Unexpected test argument.' }
    $start.Arguments = $Command -join ' '
    return [Diagnostics.Process]::Start($start)
}
function Stop-CompanionProcess {
    param([Diagnostics.Process] $Process)
    if (-not $Process.HasExited) { $Process.Kill() }
    if (-not $Process.WaitForExit(10000)) { throw "Process $($Process.Id) did not exit." }
}
function Invoke-Companion {
    param([string] $Path, [string[]] $Command)
    Write-Host "Running companion: $Command"
    if ($Command -notcontains '--quiet') { $Command += '--quiet' }
    $process = Start-CompanionProcess $Path $Command
    $errorOutput = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(60000)) {
            Stop-CompanionProcess $process
            throw "Companion timed out: $Command"
        }
        if ($process.ExitCode -ne 0) { throw "Companion failed ($($process.ExitCode)): $Command`n$($errorOutput.GetAwaiter().GetResult())" }
        Write-Host "Completed companion: $Command"
    } finally { $process.Dispose() }
}
function Assert-PackagePayload {
    $manifest = Get-Content -LiteralPath (Join-Path (Split-Path $source) 'package.json') -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.PSObject.Properties) {
        $path = Join-Path $install $entry.Name
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.Value) {
            throw "Missing or incorrect installed package file: $($entry.Name)"
        }
        $acl = Get-Acl -LiteralPath $path
        foreach ($rule in $acl.Access) {
            if ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-11' -and
                ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)) {
                throw "Ordinary users can modify package file: $path"
            }
        }
    }
}
function Assert-Service {
    param([string] $State, [string] $Startup)
    $service = Get-Service DeusKVMCompanion
    try {
        if ($service.DisplayName -ne 'DeusKVM Companion') { throw 'Service display name was not updated.' }
        if ($service.Status.ToString() -ne $State -or $service.StartType.ToString() -ne $Startup) {
            throw "Expected $State/$Startup, got $($service.Status)/$($service.StartType)"
        }
    } finally { $service.Dispose() }
}
function Wait-CompanionWindow {
    param($Process, [bool] $Visible, [int] $TimeoutMilliseconds = 10000)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        # WaitForInputIdle can return for another thread before SettingsForm is
        # visible. Process also caches its window handle until Refresh is called.
        $Process.Refresh()
        if ($Process.HasExited) { throw 'The tray exited while waiting for its settings window.' }
        $handle = $Process.MainWindowHandle
        if (($handle -ne [IntPtr]::Zero) -eq $Visible) { return }
        Start-Sleep -Milliseconds 50
    } while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    throw "Settings window did not reach visible=$Visible (PID $($Process.Id), handle $handle)."
}
function Wait-SingleCompanionTray {
    param([int] $ExpectedId, [int] $TimeoutMilliseconds = 10000)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        # The downloaded launcher exits after starting an installed process.
        # That child still has to initialize, signal the existing tray, and exit.
        # Require the original tray to survive; do not accept a replacement UI.
        $processes = @(Get-Process -Name 'DeusKVM.Companion' -ErrorAction SilentlyContinue)
        $trays = @($processes | Where-Object { $_.Path -eq $binary })
        if ($trays.Count -eq 1 -and $trays[0].Id -eq $ExpectedId) {
            foreach ($process in $processes) { if ($process.Id -ne $ExpectedId) { $process.Dispose() } }
            return $trays[0]
        }
        $ids = @($trays | ForEach-Object { $_.Id }) -join ', '
        foreach ($process in $processes) { $process.Dispose() }
        if ($timer.ElapsedMilliseconds -ge $TimeoutMilliseconds) { break }
        Start-Sleep -Milliseconds 50
    } while ($true)
    throw "Expected only the original installed tray (PID $ExpectedId) after handoff; found PIDs: [$ids]."
}
$failure = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
try {
    Invoke-Companion $source @('--install')
    Assert-Service 'Running' 'Automatic'
    Assert-PackagePayload
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Missing Start menu shortcut.' }
    if ((Get-TrayStartupCommand) -ne ('"' + $binary + '" --tray')) { throw 'Missing tray startup registration.' }
    Invoke-Companion $binary @('--tray-startup', 'off')
    if ($null -ne (Get-TrayStartupCommand)) { throw 'Tray startup was not disabled.' }
    foreach ($directory in @($install, $data)) {
        $acl = Get-Acl -LiteralPath $directory
        if (-not $acl.AreAccessRulesProtected) { throw "Unprotected directory: $directory" }
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') {
            throw "Incorrect directory owner: $directory"
        }
        $users = @($acl.Access | Where-Object {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-11'
        })
        if ($users.Count -ne 1 -or ($users[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)) {
            throw "Ordinary users can modify the installation: $directory"
        }
    }
    Invoke-Companion $binary @('--stop')
    Invoke-Companion $binary @('--startup', 'manual')
    # Preserve machine configuration byte-for-byte, independently of Bluetooth hardware.
    $settings = Join-Path $data 'settings.json'
    [IO.File]::WriteAllText($settings, '{"DeviceId":"test-selected-mac","DeviceName":"Keep this Mac"}')
    $before = (Get-FileHash -LiteralPath $settings).Hash
    Write-Host 'Checking update with the installed tray open.'
    $tray = Start-CompanionProcess $binary
    try {
        Wait-CompanionWindow $tray $true
        Invoke-Companion $source @('--install')
        if (-not $tray.WaitForExit(10000)) { throw 'Update did not close the previous tray.' }
    } finally { $tray.Dispose() }
    Assert-Service 'Stopped' 'Manual'
    Assert-PackagePayload
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Update removed the Start menu shortcut.' }
    if ($null -ne (Get-TrayStartupCommand)) { throw 'Update re-enabled tray startup.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $before) { throw 'Update changed the selected Mac.' }
    if ((Get-FileHash -LiteralPath $binary).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw 'Wrong installed EXE.' }
    Remove-Item -LiteralPath $settings
    Invoke-Companion $binary @('--start')
    Invoke-Companion $source @('--install')
    Assert-Service 'Running' 'Manual'
    Invoke-Companion $binary @('--stop')
    # An identical downloaded EXE should just open the installed window; no install.
    Write-Host 'Checking quiet tray startup before downloaded EXE handoff.'
    $quietTray = Start-CompanionProcess $binary @('--tray')
    $quietTrayId = $quietTray.Id
    try {
        if (-not $quietTray.WaitForInputIdle(10000)) { throw 'Tray did not initialize its message loop.' }
        $quietTray.Refresh()
        if ($quietTray.HasExited -or $quietTray.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Login startup opened a settings window or exited.' }
    } finally { $quietTray.Dispose() }
    Assert-Service 'Stopped' 'Manual'
    Write-Host 'Checking downloaded EXE handoff and settings reopening.'
    $launcher = Start-CompanionProcess $source
    try {
        if (-not $launcher.WaitForExit(15000) -or $launcher.ExitCode -ne 0) { throw 'Downloaded EXE did not hand off to installed UI.' }
    } finally { $launcher.Dispose() }
    Assert-Service 'Stopped' 'Manual'
    $tray = Wait-SingleCompanionTray $quietTrayId
    try {
        Wait-CompanionWindow $tray $true
        if (-not $tray.CloseMainWindow()) { throw 'Could not close settings to test reopening.' }
        Wait-CompanionWindow $tray $false
        $again = Start-CompanionProcess $source
        try {
            if (-not $again.WaitForExit(15000) -or $again.ExitCode -ne 0) { throw 'Could not reopen companion.' }
        } finally { $again.Dispose() }
        Wait-CompanionWindow $tray $true
        $settled = Wait-SingleCompanionTray $quietTrayId
        $settled.Dispose()
    } finally { $tray.Dispose() }
    Write-Host 'Checking complete removal with no saved pairing (no Bluetooth hardware required).'
    Invoke-Companion $binary @('--tray-startup', 'on')
    Invoke-Companion $binary @('--remove', '--quiet')
    $removalTimer = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $data)) {
        if ($removalTimer.Elapsed.TotalSeconds -gt 60) { throw 'Removal did not delete its installed files and data.' }
        Start-Sleep -Milliseconds 100
    }
    if (Get-Service DeusKVMCompanion -ErrorAction SilentlyContinue) { throw 'Removal left the service registered.' }
    if ((Test-Path -LiteralPath $shortcut)) { throw 'Removal left the shortcut.' }
    if ($null -ne (Get-TrayStartupCommand)) { throw 'Removal left tray startup enabled.' }
    if (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\DeusKVMCompanion') { throw 'Removal left its event-source registration.' }
    Write-Host 'EXE installation, update, startup, reopening and removal passed.'
} catch {
    $failure = $_
    Write-Host "Lifecycle test failed before cleanup: $($_ | Out-String)"
    foreach ($name in @('status.json', 'service.log')) {
        $path = Join-Path $data $name
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Tail 30 -ErrorAction Continue | Out-Host }
    }
} finally {
    # Cleanup must finish process termination before deleting mapped EXEs, and
    # must not replace the original assertion with a secondary cleanup error.
    try {
        $service = Get-Service DeusKVMCompanion -ErrorAction SilentlyContinue
        if ($service) {
            try {
                if ($service.Status -ne 'Stopped') { $service.Stop(); $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
                & "$env:SystemRoot\System32\sc.exe" delete DeusKVMCompanion
                if ($LASTEXITCODE -ne 0) { throw "Service deletion failed: $LASTEXITCODE" }
            } finally { $service.Dispose() }
        }
    } catch { $cleanupErrors.Add($_.ToString()) }
    foreach ($process in @(Get-Process -Name 'DeusKVM.Companion' -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -eq $binary -or $process.Path -eq $source) { Stop-CompanionProcess $process }
        } catch { $cleanupErrors.Add($_.ToString()) }
        finally { $process.Dispose() }
    }
    try {
        if ($null -ne (Get-TrayStartupCommand)) { Remove-ItemProperty -LiteralPath $runKey -Name $runName }
        $eventSource = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\DeusKVMCompanion'
        if (Test-Path -LiteralPath $eventSource) { Remove-Item -LiteralPath $eventSource -Recurse -Force }
    } catch { $cleanupErrors.Add($_.ToString()) }
    foreach ($path in @($shortcut, $install, $data)) {
        try {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        } catch { $cleanupErrors.Add($_.ToString()) }
    }
}
foreach ($message in $cleanupErrors) { Write-Warning "Cleanup failed: $message" }
if ($failure) { throw $failure }
if ($cleanupErrors.Count -ne 0) { throw 'Lifecycle test cleanup failed; see warnings above.' }
