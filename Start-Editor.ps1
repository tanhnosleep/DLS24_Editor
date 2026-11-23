[CmdletBinding()]
param(
    [string] $GamePackage,
    [switch] $CompileOnly
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion -lt [version]'7.4') {
    throw 'DLS24 Player Editor cần PowerShell 7.4 trở lên trên Windows.'
}
if (-not $CompileOnly -and [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Hãy mở bằng Start-DLS24-Editor.cmd hoặc chạy pwsh -STA.'
}

Add-Type -AssemblyName System.Windows.Forms
$dlls = @(
    'System.Windows.Forms.dll', 'System.Windows.Forms.Primitives.dll', 'System.Drawing.Common.dll',
    'System.Private.Windows.Core.dll', 'System.Private.Windows.GdiPlus.dll', 'Accessibility.dll'
)
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -File -Filter '*.dll' |
    Select-Object -ExpandProperty FullName)
foreach ($dll in $dlls) {
    $local = Join-Path $PSHOME $dll
    if (Test-Path -LiteralPath $local) { $references += $local }
}
$sources = @('GameData.cs', 'OvrAdjustment.cs', 'ApkBuilder.cs', 'EditorWindow.cs', 'SigningDialog.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
Add-Type -Path $sources -ReferencedAssemblies $references
if ($CompileOnly) { return }

$project = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GamePackage)) {
    $candidate = Join-Path $project 'DLS24_100M_ARM64_ARMV7/DLS24_100M_ARM64_ARMV7.zip'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $GamePackage = $candidate }
} else {
    $GamePackage = [IO.Path]::GetFullPath($GamePackage)
}
[Dls24Editor.EditorWindow]::Run($project, $PSScriptRoot, $GamePackage)
