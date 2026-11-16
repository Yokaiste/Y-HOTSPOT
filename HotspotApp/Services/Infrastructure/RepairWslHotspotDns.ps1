param([Parameter(Mandatory=$true)][string]$WorkDirectory, [switch]$InspectOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$journalPath = Join-Path $WorkDirectory 'dns-recovery-state.json'
$resultPath = Join-Path $WorkDirectory 'dns-recovery-result.json'
$logPath = Join-Path $WorkDirectory 'dns-recovery.log'
$mutex=New-Object Threading.Mutex($false,'Local\Y-HOTSPOT-WslDnsRepair')
$locked=$false
function Log([string]$message) { Add-Content -LiteralPath $logPath -Encoding UTF8 -Value ((Get-Date -Format o) + ' ' + $message) }
function Select-WslNetwork($networks) {
    $candidates=@($networks | Where-Object {$_.Type -eq 'ICS' -and $_.Name -in @('WSL','WSL (Hyper-V firewall)')})
    if($candidates.Count -ne 1){throw 'Expected exactly one supported WSL ICS network'}
    return $candidates[0]
}
function Get-ProxyTarget([string]$saved,[string]$current,[string]$oldIP,[string]$newIP) {
    $target=$saved -split '/'
    if($target.Count -ne 2 -or $target[0] -ne $oldIP -or $oldIP -eq $newIP){return $null}
    $desired="$newIP/$($target[1])"
    if($current -eq $desired){return $null} # Idempotent restoration after a partial failure.
    if($current -ne $saved){throw 'Portproxy changed concurrently; automatic overwrite refused'}
    return $desired
}
function Save-State { $state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath ($journalPath+'.tmp') -Encoding UTF8; Move-Item -LiteralPath ($journalPath+'.tmp') -Destination $journalPath -Force }
function Native([string]$file, [string[]]$arguments, [int]$seconds=30) {
    # Arguments are kept separate from PowerShell code; reject embedded quotes/newlines.
    foreach($arg in $arguments) { if($arg -match '["\r\n]'){throw 'Unsupported argument'} }
    $info=New-Object System.Diagnostics.ProcessStartInfo
    # WSL treats a quoted leading option as a Linux command; quote only values
    # that actually need it (distribution names with spaces, for example).
    $info.FileName=$file; $info.Arguments=($arguments | ForEach-Object {if($_ -match '\s'){'"'+$_+'"'}else{$_}}) -join ' '
    Log ("Command: $file $($info.Arguments)")
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    if($file -like '*wsl.exe'){$info.StandardOutputEncoding=[Text.Encoding]::Unicode}
    $p=New-Object System.Diagnostics.Process; $p.StartInfo=$info
    try {
        $null=$p.Start(); $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
        if(-not $p.WaitForExit($seconds*1000)) { $p.Kill(); throw "Timeout: $file" }
        if($p.ExitCode -ne 0){throw "Command failed: $file (exit $($p.ExitCode))"}
        return $stdout.GetAwaiter().GetResult()
    } finally {$p.Dispose()}
}
function Service-State([string]$name,[bool]$running) {
    $svc=Get-Service -Name $name
    if($running){$svc.Start();$expected=[ServiceProcess.ServiceControllerStatus]::Running}
    else {$svc.Stop();$expected=[ServiceProcess.ServiceControllerStatus]::Stopped}
    $svc.WaitForStatus($expected,[TimeSpan]::FromSeconds(30))
}
function Hotspot([string]$action) {
    $profile=@([Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Where-Object {$_.NetworkAdapter.NetworkAdapterId.ToString() -eq $state.Upstream})
    if($profile.Count -ne 1){throw 'Upstream connection unavailable'}
    $manager=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile[0])
    if($action -eq 'Stop' -and $manager.TetheringOperationalState.ToString() -eq 'Off'){return}
    if($action -eq 'Start' -and $manager.TetheringOperationalState.ToString() -eq 'On'){return}
    $operation=if($action -eq 'Stop'){$manager.StopTetheringAsync()}else{$manager.StartTetheringAsync()}
    $task=$asTask.MakeGenericMethod($operationType).Invoke($null,@($operation))
    if(-not $task.Wait(20000)){throw "Hotspot timeout: $action"}
    if($task.Result.Status.ToString() -ne 'Success'){throw "Hotspot failed: $action ($($task.Result.Status))"}
}
$success=$false; $failure=$null; $restoreFailure=$null; $state=$null
try {
    $locked=$mutex.WaitOne(0)
    if(-not $locked){throw 'Another WSL DNS recovery is running'}
    Import-Module HostNetworkingService
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null=[Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
    $null=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
    $operationType=[Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
    $asTask=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetGenericArguments().Count -eq 1} | Select-Object -First 1
    # A pending journal is resumed as restoration only, never another deletion.
    if(Test-Path -LiteralPath $journalPath){$state=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json}
    if($state -and -not $state.Restored){Log 'Resuming pending restoration';$success=$true}
    else {
        $service=Get-Service -Name 'WslService' -ErrorAction SilentlyContinue
        if(-not $service){throw 'Modern WslService not found; this recovery does not support this WSL installation'}
        if($service.Status -ne 'Running'){throw 'Start WSL before using this recovery'}
        $background=Get-Service -Name 'Y-HOTSPOT' -ErrorAction SilentlyContinue
        if($background -and $background.Status -ne 'Stopped'){throw 'Stop the Y-HOTSPOT background service before recovery'}
        $network=Select-WslNetwork @(Get-HnsNetwork)
        $endpoints=@(Get-HnsEndpoint | Where-Object {$_.VirtualNetwork -eq $network.Id})
        if($endpoints.Count -ne 1){throw 'Expected exactly one WSL endpoint; refusing an ambiguous recovery'}
        $distros=@((Native "$env:SystemRoot\System32\wsl.exe" @('--list','--running','--quiet')) -split '\r?\n' | ForEach-Object {$_.Trim([char]0).Trim()} | Where-Object {$_})
        if($distros.Count -eq 0){throw 'No running WSL distribution to restore'}
        $profile=[Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
        if(-not $profile -or -not $profile.NetworkAdapter){throw 'No internet upstream'}
        $proxy=@();$key=Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Services\PortProxy\v4tov4\tcp' -ErrorAction SilentlyContinue
        if($key){foreach($name in $key.GetValueNames()){$proxy+=@{Listen=$name;Target=[string]$key.GetValue($name)}}}
        $state=[pscustomobject]@{Restored=$true;MutationStarted=$false;Deleted=$false;Network=$network;OldIP=[string]$endpoints[0].IPAddress;Distros=$distros;Proxy=$proxy;Upstream=$profile.NetworkAdapter.NetworkAdapterId.ToString();WasHotspotOn=([Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile).TetheringOperationalState.ToString() -eq 'On')}
        if($InspectOnly){Log 'Read-only preflight passed';$success=$true;return}
        # Persist everything needed for a retry BEFORE interrupting any service.
        $state.Restored=$false; $state.MutationStarted=$true; Save-State
        Log 'Stopping hotspot and WSL service'
        Hotspot 'Stop'
        Service-State 'WslService' $false
        $target=@(Get-HnsNetwork | Where-Object {$_.Id -eq $state.Network.Id -and $_.Name -eq $state.Network.Name -and $_.Type -eq 'ICS'})
        if($target.Count -ne 1){throw 'WSL network changed; deletion refused'}
        Log 'Removing the identified WSL network only'
        $target[0] | Remove-HnsNetwork | Out-Null
        $state.Deleted=$true; Save-State
        if(@(Get-HnsNetwork | Where-Object {$_.Id -eq $state.Network.Id}).Count){throw 'WSL network removal was not confirmed'}
        # Initialize the hotspot DNS instance before WSL recreates its ICS network.
        Log 'Starting hotspot before WSL'
        Hotspot 'Start'
        $success=$true
    }
} catch {$failure=$_.Exception.Message;Log ('Failure: '+$failure)}
finally {
    if(-not $InspectOnly -and $state -and $state.MutationStarted -and -not $state.Restored) {
        try {
            Log 'Restoring WSL service and previously running distributions'
            if((Get-Service WslService).Status -ne 'Running'){Service-State 'WslService' $true}
            foreach($distro in $state.Distros){$null=Native "$env:SystemRoot\System32\wsl.exe" @('--distribution',$distro,'--exec','/bin/true') 45}
            $networks=@(Get-HnsNetwork | Where-Object {$_.Name -eq $state.Network.Name -and $_.Type -eq 'ICS'})
            if($networks.Count -ne 1){throw 'WSL network was not recreated'}
            $endpoints=@(Get-HnsEndpoint | Where-Object {$_.VirtualNetwork -eq $networks[0].Id})
            if($endpoints.Count -ne 1){throw 'Cannot identify restored WSL address'}
            $newIP=[string]$endpoints[0].IPAddress
            if(-not $newIP){throw 'Restored WSL has no IPv4 address'}
            $oldGateway=[string]$state.Network.Subnets[0].GatewayAddress
            $newGateway=[string]$networks[0].Subnets[0].GatewayAddress
            if($oldGateway -ne $newGateway -and @($state.Proxy | Where-Object {$_.Listen -like "$oldGateway/*"}).Count) {
                throw 'WSL gateway changed; port forwards listening on the previous gateway require manual review'
            }
            # Change only rules that still match the saved value and point at the old WSL IP.
            foreach($rule in $state.Proxy) {
                $listen=$rule.Listen -split '/';$target=$rule.Target -split '/'
                if($target.Count -ne 2 -or $listen.Count -ne 2 -or $target[0] -ne $state.OldIP -or $newIP -eq $state.OldIP){continue}
                $key=Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Services\PortProxy\v4tov4\tcp'
                $desired=Get-ProxyTarget $rule.Target ([string]$key.GetValue($rule.Listen)) $state.OldIP $newIP
                if(-not $desired){continue}
                $null=Native "$env:SystemRoot\System32\netsh.exe" @('interface','portproxy','set','v4tov4',"listenaddress=$($listen[0])","listenport=$($listen[1])","connectaddress=$newIP","connectport=$($target[1])")
                $key=Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Services\PortProxy\v4tov4\tcp'
                if($key.GetValue($rule.Listen) -ne "$newIP/$($target[1])"){throw 'Portproxy update not confirmed'}
            }
            if($state.WasHotspotOn -or $state.Deleted){Hotspot 'Start'}
            $state.Restored=$true;Save-State;Log 'Restoration completed; client DHCP/browsing validation is still required'
        } catch {$restoreFailure=$_.Exception.Message;Log ('Restoration incomplete: '+$restoreFailure)}
    }
    [pscustomobject]@{Success=($success -and -not $restoreFailure);Error=$failure;RestoreError=$restoreFailure;NeedsClientTest=($success -and -not $InspectOnly)} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if($locked){$mutex.ReleaseMutex()}
    $mutex.Dispose()
}
if(-not $success -or $restoreFailure){exit 1}
