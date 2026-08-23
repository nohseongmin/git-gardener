<#
.SYNOPSIS
    git gardener를 빌드해 사용자 폴더에 설치하고 시작프로그램에 등록한다.

.DESCRIPTION
    빌드 폴더에서 바로 실행하면 dotnet clean 한 번에 시작프로그램 등록이 깨진다.
    그래서 %LOCALAPPDATA%\GitGardener\bin 으로 복사한 뒤 그 경로를 등록한다.

    네이티브 명령에는 stderr 리다이렉트를 쓰지 않는다. Windows PowerShell 5.1에서
    2>&1 은 각 줄을 ErrorRecord로 감싸는데, ErrorActionPreference=Stop 과 겹치면
    정상 종료한 명령에서도 스크립트가 멈춘다. 종료 코드로만 판단한다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -NoStartup
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$NoStartup,
    [switch]$Uninstall,

    # 미리 받아둔 실행 파일을 쓰면 빌드를 건너뛴다. .NET SDK가 필요 없다.
    [string]$SourceExe,

    # 설치 위치. 비워두면 %LOCALAPPDATA%\GitGardener\bin 을 쓴다.
    [string]$InstallDir,

    # 바탕화면에도 바로가기를 만든다.
    [switch]$DesktopShortcut
)

$ErrorActionPreference = 'Stop'

$AppName    = 'GitGardener'
$LegacyRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$StartupLink  = Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk"
$DesktopLink  = Join-Path ([Environment]::GetFolderPath('Desktop')) "git gardener.lnk"
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "$AppName\bin" }
$ExePath    = Join-Path $InstallDir "$AppName.exe"
$Root       = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say  { param($m) Write-Host "  $m" }
function Step { param($m) Write-Host "`n$m" -ForegroundColor Cyan }
function Die  { param($m) Write-Host "`n$m" -ForegroundColor Red; exit 1 }

# 로그온 시 자동 실행. HKCU Run 키는 로그온 때 실행되지 않는 경우가 있었고,
# 예약 작업은 로그온 트리거라 관리자 권한을 요구한다.
# 시작 폴더는 권한 없이 되고 탐색기가 로그온마다 처리한다.
function New-Shortcut {
    param([string]$Path, [string]$Exe, [string]$Arguments)
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($Path)
    $s.TargetPath = $Exe
    $s.Arguments = $Arguments
    $s.WorkingDirectory = Split-Path $Exe
    $s.Description = 'git gardener'
    $s.Save()
}

function Set-Startup {
    param([string]$Exe)
    New-Shortcut -Path $StartupLink -Exe $Exe -Arguments '--tray'
}

# 예전 등록이 남아 있으면 지운다. 둘 다 살아 있으면 로그온 때 두 번 뜼다.
function Remove-LegacyRunEntry {
    Remove-ItemProperty -Path $LegacyRunKey -Name $AppName -ErrorAction SilentlyContinue
}

function Stop-App {
    foreach ($p in @(Get-Process $AppName -ErrorAction SilentlyContinue)) {
        $p.Kill(); [void]$p.WaitForExit(5000)
    }
}

if ($Uninstall) {
    Step '제거'
    Stop-App
    Remove-LegacyRunEntry
    if (Test-Path $StartupLink) { Remove-Item $StartupLink -Force }
    if (Test-Path $DesktopLink) { Remove-Item $DesktopLink -Force }
    Say '시작프로그램 등록 해제'
    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force
        Say "실행 파일 삭제: $InstallDir"
    }
    Say "설정과 로그는 남겨둡니다: $(Join-Path $env:APPDATA $AppName)"
    Write-Host "`n제거 완료." -ForegroundColor Green
    exit 0
}

if ($SourceExe) {
    if (-not (Test-Path $SourceExe)) { Die "지정한 실행 파일이 없습니다: $SourceExe" }
    $SourceExe = (Resolve-Path $SourceExe).Path

    # 인터넷에서 받은 파일에는 Windows가 Mark of the Web를 붙이고,
    # SmartScreen이 그걸 보고 "안전하지 않음"을 띄운다. 내가 고른 파일이므로 표식만 뚜다.
    Unblock-File -Path $SourceExe -ErrorAction SilentlyContinue
}

Step '필요한 도구 확인'

if (-not $SourceExe) {
    $sdk = @(& dotnet --list-sdks | Where-Object { $_ -match '^9\.' })
    if ($sdk.Count -eq 0) { Die '.NET 9 SDK가 없습니다.  winget install Microsoft.DotNet.SDK.9' }
    Say ".NET SDK $(($sdk[0] -split ' ')[0])"
} else {
    Say "받아둔 실행 파일을 씁니다: $SourceExe"
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Die 'git이 없습니다.  winget install Git.Git' }
Say (& git --version)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Die 'gh가 없습니다.  winget install --id GitHub.cli -e' }
& gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'gh 인증이 안 되어 있습니다.  gh auth login --scopes "repo,workflow,read:org"' }
Say 'gh 인증 확인'

# 자격증명 헬퍼가 없으면 트레이에서 도는 git push가 GUI 인증창을 띄우고 타임아웃까지 멈춘다.
$helper = @(& git config --global --get-regexp '^credential\..*github\.com.*helper$')
if ($helper.Count -eq 0) {
    Say 'git 자격증명 헬퍼가 없어 등록합니다'
    & gh auth setup-git --hostname github.com
    if ($LASTEXITCODE -ne 0) { Die 'gh auth setup-git 에 실패했습니다.' }
}
Say 'git 자격증명 헬퍼 확인'

foreach ($k in @('user.name', 'user.email')) {
    $v = & git config --global $k
    if (-not $v) { Die "git $k 이(가) 비어 있습니다. 커밋을 만들 수 없습니다." }
}
Say "커밋 작성자 $(& git config --global user.name)"

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Die 'claude CLI가 없습니다.  npm i -g @anthropic-ai/claude-code'
}
Say "claude $(& claude --version)"

if ($SourceExe) {
    $built = $SourceExe
} else {
Step '빌드'
& dotnet publish (Join-Path $Root 'src\GitGardener') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -v q --nologo
if ($LASTEXITCODE -ne 0) { Die '빌드에 실패했습니다.' }

    $built = Join-Path $Root 'src\GitGardener\bin\Release\net9.0-windows\win-x64\publish\GitGardener.exe'
}
if (-not (Test-Path $built)) { Die "빌드 결과물을 찾지 못했습니다: $built" }

Step '설치'
Stop-App
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $built $ExePath -Force
Unblock-File -Path $ExePath -ErrorAction SilentlyContinue
Say ('{0}  ({1:N0} MB)' -f $ExePath, ((Get-Item $ExePath).Length / 1MB))

Remove-LegacyRunEntry
if ($NoStartup) {
    Say '시작프로그램 등록은 건너뜁니다 (-NoStartup)'
} else {
    Set-Startup -Exe $ExePath
    if (-not (Test-Path $StartupLink)) { Die '시작프로그램 등록에 실패했습니다.' }
    Say '시작프로그램 등록 완료'
}

if ($DesktopShortcut) {
    New-Shortcut -Path $DesktopLink -Exe $ExePath -Arguments ''
    Say "바탕화면 바로가기 생성"
}

Step '실행'
Start-Process $ExePath -ArgumentList '--tray' | Out-Null
Say '트레이에서 실행 중입니다'

Write-Host "`n설치 완료." -ForegroundColor Green
Write-Host @"

claude CLI 로그인 여부는 아래로 확인하세요. is_error 가 false 여야 합니다.

  claude -p "reply with OK only" --output-format json

그다음 순서입니다.

  1. 트레이 아이콘을 더블클릭해 창을 엽니다
  2. 대상 레포를 고릅니다
  3. Dry-run 을 먼저 돌립니다  (이슈도 PR도 만들지 않습니다)
  4. 납득되면 그대로 두면 됩니다. 매일 예약 시각에 알아서 돕니다

제거:  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
"@
