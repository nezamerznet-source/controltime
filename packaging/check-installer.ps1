param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$process = Start-Process -FilePath (Resolve-Path $Installer) -PassThru
try {
    $window = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw 'Installer exited before its welcome page.' }
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $text = ($all | ForEach-Object { $_.Current.Name }) -join "`n"
            if ($text.Contains('Семейный учёт времени')) { break }
        }
    }
    if (-not $text.Contains('Семейный учёт времени')) { throw "Russian welcome text is missing or incorrectly decoded: $text" }
    $directory = Join-Path (Split-Path (Resolve-Path $Installer)) 'ui-checks'
    New-Item -ItemType Directory -Force $directory | Out-Null
    $bounds = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap ([int]$bounds.Width), ([int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $directory 'installer-welcome.png'))
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Output 'PASS: installed welcome-page text is readable Russian UTF-8.'
} finally {
    # Close only the installer process launched by this test; no installation is performed.
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
