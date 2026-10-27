# Monta dist\Italophone e gera o instalador (dist\Italophone-<versao>-instalador.exe).
# Os programas prontos vêm da pasta acima de "fontes" (onde build.cmd e servidor\build.ps1 gravam):
#   Italophone.exe, italophone-servidor.jar, Italophone.apk, icone\ e scrcpy\ (scrcpy oficial,
#   https://github.com/Genymobile/scrcpy, com o adb).
param([string]$Versao = "1.0.0")
$ErrorActionPreference = 'Stop'
$fontes = $PSScriptRoot
$programa = Split-Path $fontes -Parent
$dist = Join-Path $fontes 'dist'
$alvo = Join-Path $dist 'Italophone'

if (Test-Path $alvo) { Remove-Item -Recurse -Force $alvo }
New-Item -ItemType Directory -Force $alvo | Out-Null
foreach ($f in 'Italophone.exe', 'italophone-servidor.jar', 'Italophone.apk') {
    Copy-Item (Join-Path $programa $f) $alvo
}
Copy-Item -Recurse (Join-Path $programa 'icone') $alvo
Copy-Item -Recurse (Join-Path $programa 'scrcpy') $alvo
Copy-Item (Join-Path $fontes 'LICENSE') (Join-Path $alvo 'LICENSE.txt')

$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
& $iscc "/DVersao=$Versao" (Join-Path $fontes 'instalador\Italophone.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou" }
Copy-Item (Join-Path $programa 'Italophone.apk') (Join-Path $dist 'Italophone.apk')
Get-ChildItem $dist -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
