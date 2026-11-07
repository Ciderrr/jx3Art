$ErrorActionPreference='Stop'
try {
    $assembly=Join-Path $PSScriptRoot 'JX3Artist.dll'
    $previewRoot=Join-Path $env:LOCALAPPDATA 'JX3ArtistAutomation\ui-preview'
    Start-Process -FilePath 'dotnet' -ArgumentList @(('"'+$assembly+'"'),'--demo','--out',('"'+$previewRoot+'"')) -WindowStyle Hidden
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('预览未能启动，请确认已安装.NET 10 Windows Desktop Runtime，并保留完整程序目录。','艺人助手') | Out-Null
}
