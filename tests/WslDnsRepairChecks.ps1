$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot '..\HotspotApp\Services\Infrastructure\RepairWslHotspotDns.ps1'
$tokens=$null;$errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors | Out-String)}
# Load only pure planning functions. Never execute the recovery or touch networking.
foreach($name in @('Select-WslNetwork','Get-ProxyTarget')) {
    $definition=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Check($condition,[string]$message){if(-not $condition){throw $message}}
function Reject([scriptblock]$action){try{& $action}catch{return};throw 'Expected refusal'}
$wsl=[pscustomobject]@{Name='WSL (Hyper-V firewall)';Type='ICS';Id='wsl'}
$other=[pscustomobject]@{Name='Docker';Type='ICS';Id='docker'}
Check ((Select-WslNetwork @($other,$wsl)).Id -eq 'wsl') 'Unrelated HNS network selected'
Reject {Select-WslNetwork @($other)}
Reject {Select-WslNetwork @($wsl,$wsl)}
Reject {Select-WslNetwork @([pscustomobject]@{Name='WSL';Type='Mirrored'})}
Check ((Get-ProxyTarget '10.0.0.2/80' '10.0.0.2/80' '10.0.0.2' '10.0.0.3') -eq '10.0.0.3/80') 'Target migration failed'
Check ($null -eq (Get-ProxyTarget '127.0.0.1/80' '127.0.0.1/80' '10.0.0.2' '10.0.0.3')) 'Unrelated forward changed'
Check ($null -eq (Get-ProxyTarget '10.0.0.2/80' '10.0.0.3/80' '10.0.0.2' '10.0.0.3')) 'Retry is not idempotent'
Reject {Get-ProxyTarget '10.0.0.2/80' '10.0.0.9/80' '10.0.0.2' '10.0.0.3'}
Reject {Get-ProxyTarget '10.0.0.2/80' '' '10.0.0.2' '10.0.0.3'}
Check ($null -eq (Get-ProxyTarget '10.0.0.2/443' '10.0.0.2/443' '10.0.0.2' '10.0.0.2')) 'Unchanged IP rewritten'
Write-Output '10 recovery planning checks passed; no network changes performed.'
