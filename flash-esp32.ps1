#requires -Version 5.1
<#
.SYNOPSIS
    ESP32 固件一键识别 / 编译 / 烧录工具（Windows）

.DESCRIPTION
    自动识别可用串口与设备（芯片型号 + MAC），提供交互式菜单，
    也支持命令行参数做非交互自动化。适配 ESP-IDF v5.x。

.PARAMETER Project
    ESP-IDF 项目目录（默认：上次使用目录 / 当前目录）。

.PARAMETER Port
    串口，如 COM4（默认：自动识别唯一 ESP32 串口）。

.PARAMETER ListPorts
    仅列出可用串口。

.PARAMETER Identify
    识别端口上设备的芯片与 MAC（写前验身）。

.PARAMETER Build
    仅构建。

.PARAMETER Flash
    构建并烧录。

.PARAMETER SkipBuild
    与 -Flash 配合，跳过构建直接烧录。

.PARAMETER Monitor
    串口监视。

.PARAMETER Help
    显示帮助。

.EXAMPLE
    .\flash-esp32.ps1                       # 交互菜单
    .\flash-esp32.ps1 -ListPorts            # 列出串口
    .\flash-esp32.ps1 -Identify             # 自动端口识别芯片/MAC
    .\flash-esp32.ps1 -Port COM4 -Identify
    .\flash-esp32.ps1 -Project D:\app -Flash
#>
param(
    [string]$Project = "",
    [string]$Port = "",
    [switch]$ListPorts,
    [switch]$Identify,
    [switch]$Build,
    [switch]$Flash,
    [switch]$SkipBuild,
    [switch]$Monitor,
    [switch]$Help
)

$StatePath = Join-Path $PSScriptRoot 'flash-esp32.state.json'
if ($env:EASYINPUT_TOOL_STATE_DIR) { $StatePath = Join-Path $env:EASYINPUT_TOOL_STATE_DIR 'flash-esp32.state.json' }

# ====================================================================
# 工具函数
# ====================================================================
function Write-Info { param($m) Write-Host "[信息] $m" -ForegroundColor Cyan }
function Write-Ok   { param($m) Write-Host "[完成] $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "[提醒] $m" -ForegroundColor Yellow }
function Write-Err  { param($m) Write-Host "[错误] $m" -ForegroundColor Red }

# 判断目录是否为有效的 ESP-IDF 项目（根目录含 CMakeLists.txt）
function Test-IdfProject { param($p)
    if (-not $p) { return $false }
    return (Test-Path (Join-Path $p 'CMakeLists.txt'))
}

# ====================================================================
# 状态记忆（上次使用的项目 / 端口）
# ====================================================================
function Get-SavedState {
    if (Test-Path $StatePath) {
        try { return (Get-Content $StatePath -Raw | ConvertFrom-Json) } catch { return $null }
    }
    return $null
}
function Save-State { param($project, $port)
    try {
        [pscustomobject]@{ LastProject = $project; LastPort = $port } |
            ConvertTo-Json | Set-Content $StatePath
    } catch {}
}
function Clear-State { param($project, $port)
    # 若两个都为空，删除状态文件，避免残留
    if ($project -or $port) { Save-State $project $port }
    elseif (Test-Path $StatePath) { Remove-Item $StatePath -Force -ErrorAction SilentlyContinue }
}

# ====================================================================
# 串口识别
# ====================================================================
function Get-SerialDevices {
    $ports = [System.IO.Ports.SerialPort]::GetPortNames()
    $out = @()
    $pnp = @()
    try { $pnp = Get-PnpDevice -PresentOnly -Class Ports -ErrorAction SilentlyContinue } catch {}
    foreach ($p in $pnp) {
        if ($null -eq $p.FriendlyName -or $p.FriendlyName -notmatch '\(COM(\d+)\)') { continue }
        $com = 'COM' + $Matches[1]
        if ($com -notin $ports) { continue }
        $usb = ''
        if ($p.InstanceId -and $p.InstanceId -match '^USB\\') {
            $seg = ($p.InstanceId -split '\\')[1]
            $usb = $seg -replace '&MI_.*$', ''
        }
        $isEsp = ($usb -match 'VID_303A|VID_10C4|VID_1A86|ESP')
        $out += [pscustomobject]@{ Com = $com; Name = $p.FriendlyName; UsbId = $usb; IsEsp32 = $isEsp }
    }
    foreach ($c in $ports) {
        if ($c -notin $out.Com) {
            $out += [pscustomobject]@{ Com = $c; Name = '未知串口'; UsbId = ''; IsEsp32 = $false }
        }
    }
    return ($out | Sort-Object Com)
}

function Show-Devices { param($Devs)
    if (-not $Devs) { Write-Warn '未检测到可用串口，请检查 USB 连接与驱动。'; return }
    Write-Host ""
    Write-Host ("  {0,-4} {1,-7} {2,-6} {3}" -f '#', '端口', '类型', '设备') -ForegroundColor DarkGray
    Write-Host ("  " + ('-' * 70)) -ForegroundColor DarkGray
    $i = 0
    foreach ($d in $Devs) {
        $i++
        $type = if ($d.IsEsp32) { 'ESP32' } else { '  -  ' }
        $usb  = if ($d.UsbId) { $d.UsbId } else { '(非USB串口)' }
        Write-Host ("  {0,-4} {1,-7} {2,-6} {3}" -f $i, $d.Com, $type, $d.Name)
        Write-Host ("        `t usb={0}" -f $usb) -ForegroundColor DarkGray
    }
    Write-Host ""
}

function Resolve-AutoPort {
    $devs = Get-SerialDevices
    $esp = @($devs | Where-Object { $_.IsEsp32 })
    if ($esp.Count -eq 1) { return $esp[0].Com }
    return $null   # 0 个或多个 ESP32 串口，交给用户选择
}

function Ensure-Port { param($Port)
    if ($Port) { return $Port }
    $auto = Resolve-AutoPort
    if ($auto) { return $auto }
    throw '未指定端口，且无法自动识别唯一 ESP32 设备。请用 -Port COMx 或进入菜单选择。'
}

function Select-PortFromList { param($Devs, $Current)
    if ($Devs.Count -eq 1) { Write-Warn "仅一个端口 $($Devs[0].Com)，自动选中。"; return $Devs[0].Com }
    while ($true) {
        $in = Read-Host '  输入端口序号(如 1) 或直接输 COMx(回车保持当前)'
        if ([string]::IsNullOrWhiteSpace($in)) { return $Current }
        if ($in -match '^COM\d+$' -and $in -in $Devs.Com) { return $in }
        if ($in -match '^\d+$') {
            $n = [int]$in
            if ($n -ge 1 -and $n -le $Devs.Count) { return $Devs[$n - 1].Com }
        }
        Write-Warn '无效选择，请重试。'
    }
}

# ====================================================================
# 设备身份识别（写前门禁）
# ====================================================================
function Invoke-ChipId { param($Port)
    Write-Info "识别设备：$Port"
    $raw = & esptool --port $Port chip_id 2>&1 | Out-String
    if (-not $raw.Trim()) { Write-Err '未拿到 esptool 输出，请检查串口/驱动。'; return $null }
    Write-Host $raw.Trim()
    Write-Host ""
    $chip = ''; $mac = ''; $flash = ''
    if ($raw -match 'Chip is (.+)')     { $chip  = $Matches[1].Trim() }
    if ($raw -match 'MAC: ([0-9a-fA-F:]+)') { $mac = $Matches[1].Trim() }
    if ($raw -match '(?i)flash size: ([0-9A-Za-z]+)') { $flash = $Matches[1].Trim() }
    $info = [pscustomobject]@{ Port = $Port; Chip = $chip; Mac = $mac; Flash = $flash }
    Write-Host ('  ' + ('=' * 60)) -ForegroundColor DarkGray
    Write-Ok ("  芯片: {0}" -f $chip)
    Write-Ok ("  MAC : {0}" -f $mac)
    if ($flash) { Write-Ok ("  Flash: {0}" -f $flash) }
    return $info
}

# ====================================================================
# 构建 / 烧录 / 监视
# ====================================================================
function Invoke-Build { param($Project)
    if (-not (Test-IdfProject $Project)) { Write-Err "当前项目不是有效的 ESP-IDF 项目（缺 CMakeLists.txt）：$Project"; Write-Warn '请用 -Project 或在菜单按 [7] 指定正确项目目录。'; return $false }
    Write-Info "构建：$Project"
    Push-Location $Project
    try { idf.py build; $ok = ($LASTEXITCODE -eq 0) } finally { Pop-Location }
    if ($ok) { Write-Ok '构建成功。' } else { Write-Err '构建失败。' }
    return $ok
}

function Invoke-Flash { param($Project, $Port, $Skip)
    if (-not (Test-IdfProject $Project)) { Write-Err "当前项目不是有效的 ESP-IDF 项目（缺 CMakeLists.txt）：$Project"; Write-Warn '请用 -Project 或在菜单按 [7] 指定正确项目目录。'; return $false }
    if (-not $Skip) {
        $b = Invoke-Build $Project
        if (-not $b) { return $false }
    }
    Write-Info "烧录：$Project  ->  $Port"
    Push-Location $Project
    try { idf.py -p $Port flash; $ok = ($LASTEXITCODE -eq 0) } finally { Pop-Location }
    if ($ok) { Write-Ok "烧录完成：$Port（若显示 Hash of data verified 即成功）。" } else { Write-Err '烧录失败。' }
    return $ok
}

function Invoke-Monitor { param($Project, $Port)
    Write-Info "串口监视：$Port（Ctrl+] 退出）"
    Push-Location $Project
    try { idf.py -p $Port monitor } finally { Pop-Location }
}

# ====================================================================
# 交互菜单
# ====================================================================
function Show-InteractiveMenu { param($Project, $Port)
    while ($true) {
        Write-Host ""
        Write-Host ("=" * 56) -ForegroundColor Cyan
        Write-Host '  ESP32 烧录工具' -ForegroundColor Cyan
        Write-Host ("=" * 56) -ForegroundColor Cyan
        Write-Host "  项目 : $Project"
        $cur = if ($Port) { $Port } else { '(未选择)' }
        Write-Host "  端口 : $cur"

        $devs = Get-SerialDevices
        Show-Devices $devs

        Write-Host '  操作（直接按对应键触发，免回车）：' -ForegroundColor Cyan
        Write-Host '    [1] 选择/重新识别端口'
        Write-Host '    [2] 识别设备芯片/MAC  (烧录前验身)'
        Write-Host '    [3] 构建  idf.py build'
        Write-Host '    [4] 构建 + 烧录'
        Write-Host '    [5] 仅烧录 (跳过构建)'
        Write-Host '    [6] 串口监视'
        Write-Host '    [7] 修改项目路径'
        Write-Host '    [8] 一键烧录  (自动识别端口→验身→构建→烧录)'
        Write-Host '    [Q] 退出'
        Write-Host '  ➤ 请按键：' -ForegroundColor Green -NoNewline
        $key = [Console]::ReadKey($true)
        $c = $key.KeyChar
        switch ($c.ToString().ToUpper()) {
            '1' { $Port = Select-PortFromList $devs $Port }
            '2' { if ($Port) { Invoke-ChipId $Port } else { Write-Warn '请先选择端口。' } }
            '3' { Invoke-Build $Project }
            '4' { try { $Port = Ensure-Port $Port; Invoke-Flash $Project $Port $false } catch { Write-Err $_.Exception.Message } }
            '5' { try { $Port = Ensure-Port $Port; Invoke-Flash $Project $Port $true } catch { Write-Err $_.Exception.Message } }
            '6' { try { $Port = Ensure-Port $Port; Invoke-Monitor $Project $Port } catch { Write-Err $_.Exception.Message } }
            '7' { $np = Read-Host '  新项目绝对路径'; if ($np -and (Test-Path $np)) { $Project = (Resolve-Path $np).Path } elseif ($np) { Write-Err '路径不存在。' } }
            '8' { try {
                    $Port = Resolve-OneKeyPort $Port $devs
                    Write-Info "一键烧录：$Project  ->  $Port（写前验身）"
                    Invoke-ChipId $Port | Out-Null
                    Invoke-Flash $Project $Port $false
                } catch { Write-Err $_.Exception.Message } }
            'Q' { Write-Info '退出。'; return }
            default { Write-Warn '无效选项，请重试。' }
        }
        Save-State $Project $Port
    }
}

# 一键烧录使用的端口解析：优先已选端口；否则自动识别唯一 ESP32；否则单个端口自动选中；否则报错
function Resolve-OneKeyPort { param($Port, $Devs)
    if ($Port -and $Port -in $Devs.Com) { return $Port }
    $auto = Resolve-AutoPort
    if ($auto) { return $auto }
    if ($Devs.Count -eq 1) { Write-Warn "仅一个端口 $($Devs[0].Com)，自动选中。"; return $Devs[0].Com }
    throw '无法自动识别唯一 ESP32 设备（多设备/无设备）。请先按 [1] 选择端口。'
}

# ====================================================================
# ESP-IDF 环境定位
# ====================================================================
function Get-IdfProfilePath {
    $cands = @()
    $cands += Get-ChildItem 'C:\Espressif\tools' -Filter 'Microsoft.*PowerShell_profile.ps1' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -ExpandProperty FullName
    $cands += 'C:\Espressif\tools\Microsoft.v5.5.5.PowerShell_profile.ps1'
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

# ====================================================================
# 主流程
# ====================================================================
if ($Help) { Get-Help $MyInvocation.MyCommand.Path -Full; exit 0 }

# 一次性加载 ESP-IDF 环境（顶层 dot-source，确保 idf.py / esptool 在本脚本可见）
if (-not (Get-Command idf.py -ErrorAction SilentlyContinue)) {
    $profile = Get-IdfProfilePath
    if (-not $profile) { Write-Err '未找到 ESP-IDF PowerShell 激活脚本，请先安装 ESP-IDF v5.x。'; exit 1 }
    Write-Info "正在加载 ESP-IDF 环境：$profile"
    try { . $profile 6>$null } catch { Write-Err "加载激活脚本失败：$($_.Exception.Message)"; exit 1 }
    if (-not (Get-Command idf.py -ErrorAction SilentlyContinue)) { Write-Err '加载后仍找不到 idf.py，激活脚本不完整。'; exit 1 }
}
Write-Ok "ESP-IDF 环境已就绪，版本 v$env:ESP_IDF_VERSION"

# 确定项目路径（默认优先取上次记录，其次当前目录，都须为有效 ESP-IDF 项目）
$state = Get-SavedState
if (-not $Project) {
    if ($state -and $state.LastProject -and (Test-IdfProject $state.LastProject)) { $Project = $state.LastProject }
    elseif (Test-IdfProject (Get-Location).Path) { $Project = (Get-Location).Path }
    else { $Project = "" }
}
if ($Project -and -not (Test-IdfProject $Project)) { Write-Warn "指定项目缺少 CMakeLists.txt：$Project" }
if (-not $Project) {
    Write-Err '未自动定位到有效的 ESP-IDF 项目目录（未找到 CMakeLists.txt）。'
    Write-Warn '请用 -Project <路径> 指定，或在交互菜单按 [7] 修改项目路径。'
}

# 仅列出端口
if ($ListPorts) { $devs = Get-SerialDevices; Show-Devices $devs; exit 0 }

# 非交互：构建 / 识别 / 烧录 / 监视
if ($Build -or $Identify -or $Flash -or $Monitor) {
    if ($Build) { Invoke-Build $Project; exit 0 }
    try {
        $Port = Ensure-Port $Port
        Write-Info "使用端口：$Port"
        if ($Identify) { Invoke-ChipId $Port; exit 0 }
        if ($Monitor)  { Invoke-Monitor $Project $Port; exit 0 }
        if ($Flash)    { Invoke-Flash $Project $Port $SkipBuild; exit 0 }
    } catch { Write-Err $_.Exception.Message; exit 1 }
}

# 默认交互菜单
Show-InteractiveMenu $Project $Port
