param(
    [Parameter(Mandatory = $true)][string]$TestDirectory,
    [Parameter(Mandatory = $true)][string]$Application,
    [switch]$Crash
)
$ErrorActionPreference = 'Stop'
$testRoot = (Resolve-Path -LiteralPath $TestDirectory).Path
$applicationPath = (Resolve-Path -LiteralPath $Application).Path
$processFile = Join-Path $testRoot 'app-process.json'
if ($Crash) {
    $saved = Get-Content -LiteralPath $processFile -Raw | ConvertFrom-Json
    $testProcess = Get-Process -Id $saved.Id
    if ($testProcess.Path -ne $applicationPath -or $testProcess.StartTime.ToUniversalTime().Ticks.ToString() -ne $saved.StartTicks) {
        throw 'The recorded test process no longer matches; refusing to terminate it.'
    }
    Stop-Process -Id $testProcess.Id -Force
    Write-Output "Forced exit of test process $($testProcess.Id)."
    exit
}
if (Get-Process | Where-Object { $_.Path -eq $applicationPath }) { throw 'Application is still running.' }
$proxyPort = [int](Get-Content -LiteralPath (Join-Path $testRoot 'proxy-port.txt') -Raw)
$proxy = "http://127.0.0.1:$proxyPort"
$savedHttps = $env:HTTPS_PROXY
$savedHttp = $env:HTTP_PROXY
$savedBypass = $env:NO_PROXY
try {
    $env:HTTPS_PROXY = $proxy
    $env:HTTP_PROXY = $proxy
    $env:NO_PROXY = 'localhost,127.0.0.1'
    $testProcess = Start-Process -FilePath $applicationPath -PassThru -WindowStyle Hidden
    @{Id = $testProcess.Id; StartTicks = $testProcess.StartTime.ToUniversalTime().Ticks.ToString()} |
        ConvertTo-Json | Set-Content -LiteralPath $processFile
    Write-Output "Started test process $($testProcess.Id) through loopback port $proxyPort."
}
finally {
    $env:HTTPS_PROXY = $savedHttps
    $env:HTTP_PROXY = $savedHttp
    $env:NO_PROXY = $savedBypass
}
