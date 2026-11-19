# Downloads the RIFE checkpoints (MIT, hzwer/Practical-RIFE) from the vs-rife GitHub release into models\weights.
# The files are data only: export_rife_flow.py loads them with torch.load(weights_only=True).
param([string[]]$Versions = @('4.6', '4.25'))

$ErrorActionPreference = 'Stop'
$dest = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) 'models\weights'
New-Item -ItemType Directory -Force $dest | Out-Null

# SHA-256 of the files as first downloaded on 2026-11-19; a mismatch aborts.
$known = @{
    '4.6'  = '008646E761F0E67CB77F0C6C44CFE3C3E5A05D9D9465311B9681CA650CE030DB'
    '4.25' = '6615790EFD627772917205DB291F51CD392528A157ECBB2ECAEEC3BFF8EB6DE2'
}

foreach ($v in $Versions) {
    $file = Join-Path $dest "flownet_v$v.pkl"
    if (-not (Test-Path $file)) {
        $url = "https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v$v.pkl"
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($known[$v] -and $known[$v] -ne $hash) {
        throw "SHA-256 mismatch for $file`n expected $($known[$v])`n actual   $hash"
    }
    '{0}  {1}  {2:N0} bytes' -f $hash, (Split-Path $file -Leaf), (Get-Item $file).Length
}
