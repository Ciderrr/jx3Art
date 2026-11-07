[CmdletBinding(DefaultParameterSetName='Build')]
param(
 [Parameter(Mandatory,ParameterSetName='Build')][string]$OutputDirectory,
 [Parameter(Mandatory,ParameterSetName='Verify')][string]$ExistingPackage,
 [switch]$ManagedDiagnostics
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project=Join-Path $repo 'product/JX3Artist/JX3Artist.csproj'
[xml]$projectXml=Get-Content -LiteralPath $project -Raw
$version=[string]$projectXml.Project.PropertyGroup.Version
$runRoot=Join-Path $repo ('int1/output/release-gate/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$report=[ordered]@{version=$version;startedUtc=[DateTime]::UtcNow;passed=$false;step='initial';scope='local package, offline recognizer, isolated native demo only; no game calls';package=$null;engine=@();error=$null}
$report['diagnosticHost']=if($ManagedDiagnostics){'dotnet managed assembly; no elevation; apphost manifest not exercised'}else{'published apphost'}
function Scoped([string]$path){
 if(-not [IO.Path]::IsPathRooted($path)){$path=Join-Path $repo $path}
 $path=[IO.Path]::GetFullPath($path);$prefix=$repo+[IO.Path]::DirectorySeparatorChar
 if(-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must remain inside the project'}
 $ancestor=[IO.DirectoryInfo][IO.Path]::GetDirectoryName($path)
 while($ancestor -and $ancestor.FullName.Length -ge $repo.Length){if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse-point output directory refused'};$ancestor=$ancestor.Parent}
 return $path
}
function Run-Check([string]$exe,[string[]]$arguments,[string]$jsonPath){
 if($ManagedDiagnostics){$arguments=@(('"'+[IO.Path]::ChangeExtension($exe,'.dll')+'"'))+$arguments;$exe='dotnet'}
 $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
 if(-not $process.WaitForExit(45000)){Stop-Process -Id $process.Id;throw 'Own diagnostic process timed out'}
 if($process.ExitCode -ne 0){throw "Diagnostic exit code $($process.ExitCode)"}
 if($jsonPath){
  $result=Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
  if($result.total -le 0 -or $result.passed -ne $result.total){throw "Check report failed: $jsonPath"}
 }
}
try{
 if($PSCmdlet.ParameterSetName -eq 'Build'){
  $destination=Scoped $OutputDirectory
  if(Test-Path -LiteralPath $destination){throw 'Destination exists; never overwrite an existing/running candidate'}
  $package=Join-Path $runRoot 'package';$report.step='publish'
  $generatedUi=Scoped 'product/JX3Artist/web'
  if((Test-Path -LiteralPath $generatedUi) -and ((Get-Item -LiteralPath $generatedUi).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Generated UI must not be a reparse point'}
  & npm --prefix (Join-Path $repo 'product/frontend') run build
  if($LASTEXITCODE -ne 0){throw 'Embedded UI build failed'}
  & dotnet publish $project -c Release -o $package
  if($LASTEXITCODE -ne 0){throw 'dotnet publish failed'}
 }else{$package=Scoped $ExistingPackage}
 $report.package=$package;$report.step='manifest'
 foreach($item in $projectXml.SelectNodes('//Content[@Link]')){
  foreach($include in ([string]$item.Include).Split(';')){
   $source=[IO.Path]::GetFullPath((Join-Path (Split-Path $project) $include))
   $name=[IO.Path]::GetFileNameWithoutExtension($source);$extension=[IO.Path]::GetExtension($source)
   $relative=([string]$item.Link).Replace('%(Filename)',$name).Replace('%(Extension)',$extension)
   $file=Join-Path $package $relative
   if(-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0){throw "Missing or empty package component: $relative"}
   $expected=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
   if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected){throw "Package source mismatch: $relative"}
   $report.engine+=@{file=$relative;sha256=$expected}
  }
 }
 if($report.engine.Count -ne 17){throw 'Engine manifest count changed; review release contract'}
 foreach($name in @('JX3Artist.exe','Preview.cmd','README.txt','Assets/AppIcon.ico','Create-DesktopShortcut.ps1','Create-DesktopShortcut.cmd')){if(-not(Test-Path -LiteralPath (Join-Path $package $name))){throw "Required package entry absent: $name"}}
 $report['appIconSha256']=(Get-FileHash -LiteralPath (Join-Path $package 'Assets/AppIcon.ico') -Algorithm SHA256).Hash
 $report.step='shortcut-test'
 $shortcutRoot=Join-Path $runRoot 'shortcut-test';$shortcutScript=Join-Path $package 'Create-DesktopShortcut.ps1'
 $first=& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $shortcutScript -DesktopDirectory $shortcutRoot
 if($LASTEXITCODE -ne 0){throw 'Isolated desktop shortcut creation failed'}
 $link=$first|ConvertFrom-Json;$before=(Get-FileHash -LiteralPath $link.shortcut -Algorithm SHA256).Hash
 $second=& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $shortcutScript -DesktopDirectory $shortcutRoot
 if($LASTEXITCODE -ne 0){throw 'Isolated desktop shortcut update failed'}
 $updated=$second|ConvertFrom-Json
 if($updated.target -ne (Join-Path $package 'JX3Artist.exe') -or $updated.icon -ne ((Join-Path $package 'Assets/AppIcon.ico')+',0') -or (Get-FileHash -LiteralPath $updated.backup -Algorithm SHA256).Hash -ne $before){throw 'Shortcut target, icon, or original backup mismatch'}
 $updated|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runRoot 'shortcut-results.json') -Encoding utf8
 $exe=Join-Path $package 'JX3Artist.exe'
 $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0]
 if($actual -ne $version){throw "Assembly version $actual does not match source $version"}
 $report.step='product-tests';$productJson=Join-Path $runRoot 'product.json'
 Run-Check $exe @('--self-test','--out',('"'+$productJson+'"')) $productJson
 $report.step='webview-bridge-tests';$nativeRoot=Join-Path $runRoot 'web-ui'
 Run-Check $exe @('--web-ui-test','--out',('"'+$nativeRoot+'"')) (Join-Path $nativeRoot 'web-ui-results.json')
 $report.step='offscreen';$renderRoot=Join-Path $runRoot 'render'
 Run-Check $exe @('--ui-offscreen','--out',('"'+$renderRoot+'"')) ''
 foreach($name in @('minimum.png','tracking.png','waiting.png','skin-幕间.png')){if(-not(Test-Path -LiteralPath (Join-Path $renderRoot $name))){throw "Missing rendered check: $name"}}
 $uiManifest=Join-Path $package 'web/ui-manifest.json'
 if(-not(Test-Path -LiteralPath $uiManifest)){throw 'Embedded UI manifest missing'}
 $ui=Get-Content -LiteralPath $uiManifest -Raw|ConvertFrom-Json
 if($ui.version -ne $version){throw 'Embedded UI version does not match the desktop package'}
 $report['uiAssets']=@()
 foreach($entry in $ui.files){
  if([IO.Path]::IsPathRooted($entry.path) -or $entry.path -match '(^|[/\\])\.\.([/\\]|$)'){throw 'Invalid UI asset path'}
  $file=Join-Path (Join-Path $package 'web') $entry.path
  if(-not(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw 'Embedded UI asset hash mismatch'}
  $report.uiAssets+=@{file=('web/'+$entry.path);sha256=$entry.sha256}
 }
 # Tests may create Python bytecode. Runtime copies must still match source.
 foreach($entry in $report.engine){if((Get-FileHash -LiteralPath (Join-Path $package $entry.file) -Algorithm SHA256).Hash -ne $entry.sha256){throw 'Engine changed during validation'}}
 if($PSCmdlet.ParameterSetName -eq 'Build'){
  $report.step='promote';$package=Scoped $package;$destination=Scoped $destination
  New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
  Move-Item -LiteralPath $package -Destination $destination
  $report.package=$destination
 }
 $report.passed=$true;$report.step='complete'
}catch{$report.error=$_.Exception.Message;Write-Warning $report.error}
finally{
 $report['endedUtc']=[DateTime]::UtcNow
 $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'release-report.json') -Encoding utf8
 Write-Output ("Release report: "+(Join-Path $runRoot 'release-report.json'))
}
if(-not $report.passed){exit 1}
