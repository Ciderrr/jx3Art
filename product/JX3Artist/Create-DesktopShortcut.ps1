[CmdletBinding()]
param(
 [string]$DesktopDirectory=[Environment]::GetFolderPath('DesktopDirectory'),
 [string]$ShortcutName='剑网3 艺人助手',
 [string]$BackupDirectory
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($BackupDirectory)){$BackupDirectory=Join-Path $PSScriptRoot 'shortcut-backups'}
if([string]::IsNullOrWhiteSpace($ShortcutName) -or $ShortcutName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0){throw 'Invalid shortcut name'}
$program=Join-Path $PSScriptRoot 'JX3Artist.exe'
$icon=Join-Path $PSScriptRoot 'Assets/AppIcon.ico'
if(-not(Test-Path -LiteralPath $program) -or -not(Test-Path -LiteralPath $icon)){throw 'Keep the complete application folder before creating a shortcut.'}
if([IO.Path]::GetFullPath($DesktopDirectory) -eq [Environment]::GetFolderPath('DesktopDirectory')){
 try{$iconDataRoot=Join-Path $env:LOCALAPPDATA 'JX3ArtistAutomation';$saved=Get-Content -LiteralPath (Join-Path $iconDataRoot 'settings.json') -Raw|ConvertFrom-Json;$chosen=[string]$saved.appIcon;$candidate=$null;if($chosen -in @('01-yunxiu','02-jiangshan','03-qingshang','05-fuxian','06-huadeng','retained')){$candidate=Join-Path $PSScriptRoot ('Assets/Icons/'+$chosen+'.ico')}elseif($chosen -match '^custom-[0-9a-f]{32}$'){$candidate=Join-Path $iconDataRoot ('icons/'+$chosen+'.ico')};if($candidate -and (Test-Path -LiteralPath $candidate)){$icon=$candidate}}catch{}
}
$desktopPath=[IO.Path]::GetFullPath($DesktopDirectory)
if(-not(Test-Path -LiteralPath $desktopPath -PathType Container)){New-Item -ItemType Directory -Path $desktopPath | Out-Null}
$destination=[IO.Path]::GetFullPath((Join-Path $desktopPath ($ShortcutName+'.lnk')))
if(-not $destination.StartsWith($desktopPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Shortcut escaped target directory'}
$backup=$null
if(Test-Path -LiteralPath $destination){
 New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
 $backup=Join-Path $BackupDirectory ($ShortcutName+'-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')+'-'+[Guid]::NewGuid().ToString('N')+'.lnk')
 Copy-Item -LiteralPath $destination -Destination $backup
}
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut($destination)
$shortcut.TargetPath=$program
$shortcut.WorkingDirectory=$PSScriptRoot
$shortcut.IconLocation=$icon+',0'
$shortcut.Description='剑网3 艺人助手'
$shortcut.Save()
[pscustomobject]@{shortcut=$destination;target=$shortcut.TargetPath;icon=$shortcut.IconLocation;backup=$backup}|ConvertTo-Json -Compress
