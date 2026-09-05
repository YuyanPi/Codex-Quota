param(
    [string]$InstallDirectory = 'D:\Codex-Quota\App',
    [switch]$StartWithWindows
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedSource = [System.IO.Path]::GetFullPath($sourceDirectory)
$resolvedTarget = [System.IO.Path]::GetFullPath($InstallDirectory)

if ($resolvedSource.TrimEnd('\') -eq $resolvedTarget.TrimEnd('\')) {
    throw '请先把压缩包解压到 D:\Codex-Quota\Downloads，再运行 Install.ps1。'
}

New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
Get-ChildItem -LiteralPath $resolvedSource -File |
    Where-Object Name -NotIn @('Install.ps1') |
    Copy-Item -Destination $resolvedTarget -Force

$shell = New-Object -ComObject WScript.Shell
$desktopShortcut = $shell.CreateShortcut("$env:USERPROFILE\Desktop\Codex Quota Bar.lnk")
$desktopShortcut.TargetPath = Join-Path $resolvedTarget 'CodexQuotaBar.exe'
$desktopShortcut.WorkingDirectory = $resolvedTarget
$desktopShortcut.IconLocation = "$(Join-Path $resolvedTarget 'CodexQuotaBar.exe'),0"
$desktopShortcut.Description = 'Codex 额度桌面看板'
$desktopShortcut.Save()

if ($StartWithWindows) {
    $startup = [Environment]::GetFolderPath('Startup')
    Copy-Item "$env:USERPROFILE\Desktop\Codex Quota Bar.lnk" (Join-Path $startup 'Codex Quota Bar.lnk') -Force
}

Write-Host "安装完成：$resolvedTarget"
Write-Host '已创建桌面快捷方式。'
