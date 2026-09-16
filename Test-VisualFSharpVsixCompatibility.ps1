# Decide whether a Visual F# Tools VSIX can actually load in a given Visual Studio hive, and say why not.
#
# The decisive check is Roslyn. The VSIX carries no Microsoft.CodeAnalysis of its own, so FSharp.Editor
# binds to whatever Roslyn the hive supplies, and Roslyn's assembly version is Major.Minor.0.0 with no
# unification across minors: an extension built against 5.10 cannot bind to a 5.9 installation. The
# exception is a hive with a locally built Roslyn deployed into it (RoslynDev), which stamps
# 42.42.42.42 and redirects every reference to itself, so the built-against version stops mattering.
#
#   .\Test-VisualFSharpVsixCompatibility.ps1 VisualFSharpDebug.vsix                  # against the Exp hive
#   .\Test-VisualFSharpVsixCompatibility.ps1 VisualFSharpDebug.vsix -RootSuffix ''   # against the installation
#
# Returns an object with Compatible/Failures; exit code is non-zero when incompatible.
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$VsixPath,
    [AllowEmptyString()][string]$RootSuffix = 'Exp',
    [string]$DevEnv
)
Set-StrictMode -Version Latest; $ErrorActionPreference = 'Stop'

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check($name, $status, $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; Status = $status; Detail = $detail })
}

# .NET Framework has no System.Reflection.Metadata and .NET has no ReflectionOnlyLoadFrom, so read the
# assembly reference tables by whichever one this PowerShell edition actually has. Windows PowerShell
# reads them in a child process: the reflection-only context keeps every file it loads mapped until its
# AppDomain dies, so the staging directory could not be removed, and it refuses a second load of any
# identity - which a rerun in the same window always is, since every local build stamps 42.42.42.42.
function Get-AssemblyReferences([string[]]$assemblyPaths) {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        $references = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -Command {
            $ErrorActionPreference = 'Stop'
            foreach ($path in $args) {
                foreach ($r in [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($path).GetReferencedAssemblies()) {
                    [pscustomobject]@{ Assembly = [IO.Path]::GetFileName($path); Name = $r.Name; Version = "$($r.Version)" }
                }
            }
        } -args $assemblyPaths
        if ($LASTEXITCODE) { throw "reading assembly references failed with exit code $LASTEXITCODE" }
        return $references | ForEach-Object { [pscustomobject]@{ Assembly = $_.Assembly; Name = $_.Name; Version = [version]$_.Version } }
    }
    Add-Type -AssemblyName System.Reflection.Metadata
    foreach ($path in $assemblyPaths) {
        $stream = [IO.File]::OpenRead($path)
        $pe = New-Object System.Reflection.PortableExecutable.PEReader $stream
        try {
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($handle in $md.AssemblyReferences) {
                $r = $md.GetAssemblyReference($handle)
                [pscustomobject]@{ Assembly = [IO.Path]::GetFileName($path); Name = $md.GetString($r.Name); Version = $r.Version }
            }
        }
        finally { $pe.Dispose(); $stream.Dispose() }
    }
}

if (-not $DevEnv) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $DevEnv = if ($env:DevEnvDir) { Join-Path $env:DevEnvDir 'devenv.exe' }
              elseif (Test-Path $vswhere) { & $vswhere -latest -prerelease -property productPath 2>$null }
}
if (-not ($DevEnv -and (Test-Path $DevEnv))) { throw 'devenv.exe not found; run from a VS Developer prompt or pass -DevEnv.' }
$DevEnv = (Resolve-Path -LiteralPath $DevEnv).ProviderPath
$ideDir = Split-Path $DevEnv

$ini = Get-Content -LiteralPath (Join-Path $ideDir 'devenv.isolation.ini') -Raw
$null = $ini -match '(?m)^InstallationID=(?<id>\S+)'; $installationId = $Matches.id
$null = $ini -match '(?m)^InstallationVersion=(?<v>\S+)'; $installedVersion = [version]$Matches.v
$hive = Join-Path $env:LOCALAPPDATA ('Microsoft\VisualStudio\{0}.0_{1}{2}' -f $installedVersion.Major, $installationId, $RootSuffix)

Write-Host "Visual Studio  : $([System.Diagnostics.FileVersionInfo]::GetVersionInfo($DevEnv).FileDescription) $installedVersion"
Write-Host "                 $DevEnv"
Write-Host "Hive           : $(if ($RootSuffix) { "/rootSuffix $RootSuffix" } else { 'the installation itself' })"
Write-Host "                 $hive"

$vsix = (Resolve-Path -LiteralPath $VsixPath).ProviderPath
$staging = Join-Path ([IO.Path]::GetTempPath()) ("vsixcheck-" + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $manifestEntry = $archive.GetEntry('extension.vsixmanifest')
    if (-not $manifestEntry) { throw "$vsix is not a VSIX: it has no extension.vsixmanifest." }
    $reader = New-Object System.IO.StreamReader $manifestEntry.Open()
    try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }

    # Every managed DLL the VSIX carries, not just FSharp.Editor.dll - FSharp.ProjectSystem.FSharp.dll
    # and FSharp.LanguageService.Base.dll bind to the same shared assemblies and have failed to load
    # from the exact same version-ceiling mismatch in practice. Resource satellites carry no code.
    $dllEntries = @($archive.Entries | Where-Object { $_.FullName -eq $_.Name -and $_.Name -like '*.dll' -and $_.Name -notlike '*.resources.dll' })
    if ($dllEntries) {
        New-Item -ItemType Directory -Force $staging | Out-Null
        foreach ($e in $dllEntries) {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $staging $e.Name), $true)
        }
    }
}
finally { $archive.Dispose() }

$metadata = $manifest.PackageManifest.Metadata
$target = $manifest.PackageManifest.Installation.InstallationTarget
Write-Host "VSIX           : $($metadata.Identity.Id) $($metadata.Identity.Version) - $($metadata.DisplayName)"
Write-Host "                 $vsix"
Write-Host ''

# 1. Does this VSIX target this Visual Studio at all?
$range = @($target)[0].Version
if ($range -match '^\[(?<min>[\d.]+),(?<max>[\d.]*)[\]\)]$') {
    $min = [version]$Matches.min
    $okMin = $installedVersion -ge $min
    $okMax = $true
    if ($Matches.max) { $okMax = $installedVersion -lt [version]$Matches.max }
    Add-Check 'VS version range' $(if ($okMin -and $okMax) { 'PASS' } else { 'FAIL' }) "VSIX targets $range, installed $installedVersion"
}
else { Add-Check 'VS version range' 'INFO' "unparsed InstallationTarget version '$range'" }

$arch = @($target)[0].ProductArchitecture
if ($arch) {
    $machine = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
    Add-Check 'Architecture' $(if ($arch -eq $machine) { 'PASS' } else { 'FAIL' }) "VSIX is $arch, machine is $machine"
}

# 2. Which Roslyn will this hive actually give the extension - a locally deployed one wins over in-box.
$hiveRoslyn = $null
if (Test-Path (Join-Path $hive 'Extensions')) {
    $hiveRoslyn = Get-ChildItem (Join-Path $hive 'Extensions') -Recurse -Filter 'Microsoft.CodeAnalysis.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*Roslyn Language Services*' } | Select-Object -First 1
}
$inboxRoslyn = Join-Path $ideDir 'CommonExtensions\Microsoft\VBCSharp\LanguageServices\Microsoft.CodeAnalysis.dll'

if ($hiveRoslyn) {
    $effective = [System.Reflection.AssemblyName]::GetAssemblyName($hiveRoslyn.FullName).Version
    $effectiveProduct = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($hiveRoslyn.FullName).ProductVersion
    $effectiveSource = "deployed into the hive ($effectiveProduct)"
}
elseif (Test-Path $inboxRoslyn) {
    $effective = [System.Reflection.AssemblyName]::GetAssemblyName($inboxRoslyn).Version
    $effectiveProduct = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($inboxRoslyn).ProductVersion
    $effectiveSource = "shipped with Visual Studio ($effectiveProduct)"
}
else { $effective = $null; $effectiveSource = 'not found' }

$stagedDlls = @(if (Test-Path $staging) { Get-ChildItem $staging -Filter '*.dll' })
$references = @(if ($stagedDlls) { Get-AssemblyReferences @($stagedDlls | ForEach-Object FullName) })
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

if (-not $stagedDlls) {
    Add-Check 'Roslyn binding' 'INFO' 'VSIX contains no managed DLLs to inspect'
}
elseif (-not $effective) {
    Add-Check 'Roslyn binding' 'FAIL' 'no Microsoft.CodeAnalysis.dll found for this hive'
}
else {
    # A dev-built Roslyn stamps 42.42.42.42 and ships redirects that catch every reference version.
    $isDevRoslyn = $effective.Major -eq 42
    $roslynReferences = @($references | Where-Object Name -eq 'Microsoft.CodeAnalysis')
    $mismatches = @(foreach ($required in $roslynReferences) {
        if (-not $isDevRoslyn -and
            ($required.Version.Major -ne $effective.Major -or $required.Version.Minor -ne $effective.Minor)) {
            "$($required.Assembly) needs $($required.Version)"
        }
    })
    if (-not $roslynReferences) { Add-Check 'Roslyn binding' 'INFO' 'no shipped DLL references Microsoft.CodeAnalysis' }
    else {
        $note = "hive provides $effective $effectiveSource"
        if ($isDevRoslyn) { $note += ' - dev build, binding redirects apply' }
        elseif ($mismatches) { $note = "$($mismatches -join '; '); $note" }
        Add-Check 'Roslyn binding' $(if ($mismatches -and -not $isDevRoslyn) { 'FAIL' } else { 'PASS' }) $note
    }
}
# 3. Assemblies the extension does not carry, which Visual Studio must therefore supply itself - for
# every DLL the VSIX ships, not just FSharp.Editor.dll: FSharp.ProjectSystem.FSharp.dll and
# FSharp.LanguageService.Base.dll bind to the same shared assemblies and have failed to load from
# exactly this kind of version-ceiling mismatch in practice. devenv.exe.config unifies these with a
# binding redirect, and the oldVersion range is a ceiling: a reference above it is not redirected, so
# the CLR demands that exact version, fails to find it, and the DLL never loads - which shows up as
# every export it carries going missing rather than as a load error naming the DLL.
if ($stagedDlls) {
    $config = [xml](Get-Content -LiteralPath (Join-Path $ideDir 'devenv.exe.config') -Raw)
    $redirects = @{}
    foreach ($dependent in $config.GetElementsByTagName('dependentAssembly')) {
        $identity = $dependent.Item('assemblyIdentity')
        $redirect = $dependent.Item('bindingRedirect')
        if (-not $identity -or -not $redirect) { continue }
        $ceiling = ($redirect.GetAttribute('oldVersion') -split '-')[-1]
        if ($ceiling) { $redirects[$identity.GetAttribute('name')] = [version]$ceiling }
    }

    $shipped = @{}
    $archive = [System.IO.Compression.ZipFile]::OpenRead($vsix)
    try { foreach ($e in $archive.Entries) { if ($e.FullName -eq $e.Name) { $shipped[$e.Name] = $true } } }
    finally { $archive.Dispose() }

    # References devenv.exe.config never mentions at all - no bindingRedirect, no codeBase pin - are not
    # a .NET Framework binding question, so a ceiling check cannot say anything about them. Most are the
    # standard Framework GAC set (System.*, Microsoft.Build*), unified for free and never worth flagging.
    # What is left is the genuine unknown: an assembly a VS *extension* supplies (Copilot is the one
    # FSharp.Editor.dll itself pulls in) and resolves through its own extension loader, at whatever
    # version happens to be installed - impossible to verify without starting devenv and watching it try.
    $gacStable = '^(System(\.|$)|mscorlib$|netstandard$|Microsoft\.VisualBasic$|Microsoft\.Build(\.|$))'

    $unsatisfied = [System.Collections.Generic.List[string]]::new()
    $unverifiable = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    foreach ($ref in $references) {
        if ($shipped["$($ref.Name).dll"] -or $seen["$($ref.Assembly)|$($ref.Name)|$($ref.Version)"]) { continue }
        $seen["$($ref.Assembly)|$($ref.Name)|$($ref.Version)"] = $true
        $supplied = $redirects[$ref.Name]
        if ($supplied) {
            if ($ref.Version -gt $supplied) { $unsatisfied.Add("$($ref.Assembly): $($ref.Name) $($ref.Version) > $supplied") }
        }
        elseif ($ref.Name -notmatch $gacStable) {
            $unverifiable.Add("$($ref.Assembly): $($ref.Name) $($ref.Version)")
        }
    }
    Add-Check 'Shared assemblies' $(if ($unsatisfied) { 'FAIL' } else { 'PASS' }) `
        $(if ($unsatisfied) { "above this VS's binding redirect ceiling: $($unsatisfied -join '; ')" }
          else { 'every reference covered by a binding redirect stays within its ceiling' })
    if ($unverifiable) {
        Add-Check 'Unverifiable references' 'INFO' `
            "not in devenv.exe.config - resolved by VS's own extension loader, not checked here: $($unverifiable -join '; ')"
    }
}

# 4. State of a previous install, so a "it still does not work" has somewhere to start.
$deployed = @()
if (Test-Path (Join-Path $hive 'Extensions')) {
    $deployed = @(Get-ChildItem (Join-Path $hive 'Extensions') -Recurse -Depth 3 -Filter 'extension.vsixmanifest' -ErrorAction SilentlyContinue |
        Where-Object { ([xml](Get-Content -LiteralPath $_.FullName -Raw)).PackageManifest.Metadata.Identity.Id -eq $metadata.Identity.Id })
}
Add-Check 'Deployed files' 'INFO' $(if ($deployed) { "$($deployed.Count) copy in $($deployed[0].DirectoryName)" } else { 'not deployed in this hive yet' })

$vsRegEdit = Join-Path $ideDir 'VsRegEdit.exe'
if ($RootSuffix -and (Test-Path $vsRegEdit)) {
    $entry = & $vsRegEdit read local $RootSuffix hkcu 'ExtensionManager\EnabledExtensions' "$($metadata.Identity.Id),$($metadata.Identity.Version)" string 2>&1
    $registered = "$entry" -notmatch 'Failed to read'
    Add-Check 'Registration' $(if ($deployed -and -not $registered) { 'FAIL' } else { 'INFO' }) `
        $(if ($registered) { 'listed in ExtensionManager\EnabledExtensions' } else { 'absent from ExtensionManager\EnabledExtensions - deployed files alone are ignored' })
}

# Composition failures describe the hive's last run, not this VSIX, so they never block an install -
# reinstalling is usually the very thing that fixes them. They are the most useful diagnostic here
# though, so report the causes rather than a count: one failed assembly load rejects every part that
# imports from it, and a hundred rejections routinely come from a single missing contract.
$mefDetail = [System.Collections.Generic.List[string]]::new()
$err = Join-Path $hive 'ComponentModelCache\Microsoft.VisualStudio.Default.err'
if (-not (Test-Path $err)) {
    Add-Check 'MEF composition' 'INFO' 'no cache yet - it is built on the first start of this hive'
}
else {
    $text = Get-Content -LiteralPath $err -Raw

    $catalogSection = ($text -split '(?m)^-----.*CompositionError.*-----\s*$')[0]
    $loadFailures = @($catalogSection -split '(?m)^Error #\d+\s*$' |
        Where-Object { $_ -match 'FSharp' } |
        ForEach-Object { ($_ -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' ' })

    $rejections = @($text -split "`r?`n`r?`n" | Where-Object { $_ -match 'expected exactly 1 export' -and $_ -match 'FSharp' })
    $missingContracts = $rejections |
        ForEach-Object { if ($_ -match '(?m)^\s+Contract name:\s*(?<c>.+?)\s*$') { $Matches.c } } |
        Group-Object | Sort-Object Count -Descending

    if (-not $loadFailures -and -not $rejections) {
        Add-Check 'MEF composition' 'INFO' 'no F#-related problems cached'
    }
    else {
        Add-Check 'MEF composition' 'WARN' "$($rejections.Count) F# rejection(s), $($loadFailures.Count) F# assembly load failure(s) - details below"
        foreach ($failure in $loadFailures | Select-Object -First 5) {
            $mefDetail.Add("  assembly load: $($failure -replace '\s+', ' ')")
        }
        foreach ($contract in $missingContracts | Select-Object -First 10) {
            $mefDetail.Add("  missing export x$($contract.Count): $($contract.Name)")
        }
    }
}

$checks | Format-Table -AutoSize -Wrap | Out-String | Write-Host
if ($mefDetail) {
    Write-Host "Last composition in this hive ($err):"
    $mefDetail | ForEach-Object { Write-Host $_ }
    Write-Host ''
}
$failures = @($checks | Where-Object Status -eq 'FAIL')
if ($failures) { Write-Host "INCOMPATIBLE: $($failures.Count) blocking problem(s)." -ForegroundColor Red }
else { Write-Host 'Compatible.' -ForegroundColor Green }

[pscustomobject]@{
    Compatible = -not $failures
    Failures   = $failures
    Checks     = $checks
    Hive       = $hive
}
# VsRegEdit leaves its own failure code behind after reading an absent value; do not report it as ours.
$global:LASTEXITCODE = if ($failures) { 1 } else { 0 }
