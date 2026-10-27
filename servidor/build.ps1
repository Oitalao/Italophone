$ErrorActionPreference='Stop'
# Android SDK e JDK 17 pelas variaveis ANDROID_HOME e JAVA_HOME, ou por um local.ps1 ao lado
# (fora do git) que defina $sdk e $jdk (a pasta bin do JDK)
$sdk = $env:ANDROID_HOME
$jdk = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } else { $null }
$local = Join-Path $PSScriptRoot 'local.ps1'
if (Test-Path $local) { . $local }
if (-not $sdk -or -not $jdk) { throw 'Defina ANDROID_HOME e JAVA_HOME (JDK 17) ou crie servidor\local.ps1' }
Set-Location $PSScriptRoot
Remove-Item -Recurse -Force classes,saida.zip -ErrorAction SilentlyContinue; New-Item -ItemType Directory classes | Out-Null
& "$jdk\javac.exe" -source 11 -target 11 -nowarn -cp "$sdk\platforms\android-36\android.jar" -d classes (Get-ChildItem src -Recurse -Filter *.java).FullName
& "$sdk\build-tools\36.0.0\d8.bat" --min-api 26 --output saida.zip (Get-ChildItem classes -Recurse -Filter *.class).FullName
Move-Item -Force saida.zip ..\..\italophone-servidor.jar; Remove-Item -Recurse -Force classes