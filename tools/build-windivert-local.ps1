# Build only: no driver loading, installation or security setting changes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$kit = (Join-Path $root 'dist/research/wdk-nuget/c').Replace('\', '/') + '/'
$sdk = 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/'
$log = Join-Path $root 'dist/research/windivert-driver-build.log'
Push-Location (Join-Path $root 'dist/research/WinDivert-source')
try {
    # Explicit paths avoid divergent KitsRoot10 registrations. Release libraries
    # also prevent upstream debug runtime checks from conflicting with optimization.
    & 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' `
        'sys/windivert.vcxproj' '/p:Configuration=Release' '/p:Platform=x64' `
        '/p:TargetVersion=Windows10' '/p:UseDebugLibraries=false' '/p:SignMode=Off' `
        '/p:WindowsTargetPlatformVersion=10.0.26100.0' "/p:WDKContentRoot=$kit" `
        "/p:MessageCompileToolPath=$sdk" "/p:RCToolPath=$sdk" `
        '/p:OutDir=../install/LOCAL/amd64/' '/p:AssemblyName=WinDivert64' `
        '/verbosity:minimal' *> $log
    if ($LASTEXITCODE -ne 0) { throw "Driver build failed. See $log" }
    Get-Item 'install/LOCAL/amd64/WinDivert64.sys' | Select-Object FullName, Length
} finally { Pop-Location }
