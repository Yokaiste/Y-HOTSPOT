# Reproduz a DLL local com a correção dos fallbacks de memória do MSVC.
# Não carrega driver nem altera configurações do Windows.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'dist/research/WinDivert-source'
$patch=Join-Path $PSScriptRoot 'windivert-msvc-memory-loops.patch'
Push-Location $source
try {
    git apply --reverse --check $patch 2>$null
    if($LASTEXITCODE -ne 0){
        git apply --check $patch
        if($LASTEXITCODE -ne 0){throw 'A correção não corresponde à fonte local'}
        git apply $patch
        if($LASTEXITCODE -ne 0){throw 'Falha ao aplicar correção'}
    }
    & 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/amd64/MSBuild.exe' dll/windivert.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /p:UseDebugLibraries=false /p:WindowsTargetPlatformVersion=10.0.26100.0 /p:OutDir=../install/LOCAL/amd64/ /verbosity:minimal *> (Join-Path $root 'dist/research/windivert-dll-build.log')
    if($LASTEXITCODE -ne 0){throw 'Falha na compilação da DLL; consulte windivert-dll-build.log'}
} finally {Pop-Location}
