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

.PARAMETER FlashBin
    免工程烧录：用 esptool 直接写固件 .bin，无需 ESP-IDF 项目目录（无 CMakeLists.txt 也能烧）。

.PARAMETER Bin
    与 -FlashBin 配合，指定单个 .bin 固件文件（配合 -BinAddr 指定起始地址）。

.PARAMETER BinAddr
    与 -FlashBin/-Bin 配合，指定单个固件烧录的起始地址（默认 0x10000）。

.PARAMETER BinDir
    与 -FlashBin 配合，指定构建输出目录（自动识别 bootloader/分区表/app 固件并烧录）。

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
    .\flash-esp32.ps1 -FlashBin -Bin D:\fw\app.bin -BinAddr 0x10000 -Port COM4   # 免工程烧单个固件
    .\flash-esp32.ps1 -FlashBin -BinDir D:\fw\build -Port COM4                   # 免工程烧构建目录
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
    [switch]$FlashBin,
    [string]$Bin = "",
    [string]$BinAddr = "",
    [string]$BinDir = "",
    [switch]$Help
)

$ScriptVersion = '1.1.0'

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

# 检测项目 build 目录的 Python 环境与当前 IDF 环境是否一致（不一致会触发 idf.py 的环境报错）
# 依据 ESP-IDF 官方环境变量 IDF_PYTHON_ENV_PATH，避免被系统 PATH 里其他 Python 干扰
function Test-PythonEnvMismatch { param($Project)
    $cache = Join-Path $Project 'build\CMakeCache.txt'
    if (-not (Test-Path $cache)) { return $false }
    $pyLine = Select-String -Path $cache -Pattern '^PYTHON:UNINITIALIZED=' -ErrorAction SilentlyContinue | Select-Object -First 1
    $idfPyRoot = $env:IDF_PYTHON_ENV_PATH
    if (-not $pyLine -or -not $idfPyRoot) { return $false }
    $projPy = ($pyLine.Line -split '=', 2)[1].Trim()
    $projPyNorm = ($projPy -replace '/', '\').TrimEnd('\')
    $rootNorm = ($idfPyRoot -replace '/', '\').TrimEnd('\')
    return (-not $projPyNorm.StartsWith($rootNorm + '\', [System.StringComparison]::OrdinalIgnoreCase))
}

function Invoke-Build { param($Project)
    if (-not (Test-IdfProject $Project)) { Write-Err "当前项目不是有效的 ESP-IDF 项目（缺 CMakeLists.txt）：$Project"; Write-Warn '请用 -Project 或在菜单按 [7] 指定正确项目目录。'; return $false }

    # 环境自愈：项目由不同 Python 环境构建时，自动 fullclean 重新配置（仅首次，之后增量构建）
    if (Test-PythonEnvMismatch $Project) {
        Write-Warn '检测到项目由不同的 Python 环境构建，自动 fullclean 重新配置...'
        Push-Location $Project
        try { idf.py fullclean 2>&1 | Out-Null } finally { Pop-Location }
    }

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

# ---- 免工程烧录（esptool 直写，无需 ESP-IDF 项目目录）----

# 在目录中查找指定文件名（优先根目录，其次递归子目录）
function Get-BinFile { param($Dir, $Name)
    $root = Join-Path $Dir $Name
    if (Test-Path $root) { return $root }
    $hit = Get-ChildItem $Dir -Filter $Name -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    return $null
}

# 读取构建目录的 flasher_args.json（ESP-IDF 生成的烧录清单），失败返回空
function Get-FlasherArgs { param($Dir)
    $p = Join-Path $Dir 'flasher_args.json'
    if (-not (Test-Path $p)) { return $null }
    try { return (Get-Content $p -Raw | ConvertFrom-Json) } catch { return $null }
}

# 免工程烧录：单个 .bin（-Bin + -BinAddr），或构建目录（-BinDir，按 flasher_args.json 自动识别）
function Invoke-FlashBin { param($Port, $Bin, $BinAddr, $BinDir)
    $pairs = @(); $chipArgs = @(); $writeArgs = @()

    if ($Bin) {
        if (-not (Test-Path $Bin)) { Write-Err "固件文件不存在：$Bin"; return $false }
        $addr = $BinAddr; if (-not $addr) { $addr = '0x10000' }
        if ($addr -notmatch '^0x[0-9a-fA-F]+$') { Write-Err '地址格式错误，应为十六进制，如 0x10000。'; return $false }
        $pairs += ,@($addr, (Resolve-Path $Bin).Path)
    } elseif ($BinDir) {
        if (-not (Test-Path $BinDir)) { Write-Err "目录不存在：$BinDir"; return $false }
        $spec = Get-FlasherArgs $BinDir
        if ($spec -and $spec.flash_files) {
            if ($spec.extra_esptool_args) { $chipArgs = @($spec.extra_esptool_args) }
            if ($spec.write_flash_args) { $writeArgs = @($spec.write_flash_args) }
            foreach ($prop in $spec.flash_files.PSObject.Properties) {
                $full = Join-Path $BinDir ([string]$prop.Value)
                if (Test-Path $full) { $pairs += ,@([string]$prop.Name, (Resolve-Path $full).Path) }
                else { Write-Warn "flasher_args.json 指向的文件不存在，忽略：$([string]$prop.Value)" }
            }
            if (-not $pairs) { Write-Err 'flasher_args.json 中无可用固件文件。'; return $false }
        } else {
            $boot = Get-BinFile $BinDir 'bootloader.bin'
            $part = Get-BinFile $BinDir 'partition-table.bin'
            $app  = Get-ChildItem $BinDir -Filter '*.bin' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notmatch '^bootloader\.bin$|^partition-table\.bin$' } |
                Sort-Object Name | Select-Object -First 1 -ExpandProperty FullName
            if (-not $boot -or -not $part -or -not $app) {
                Write-Err '目录中未能识别 bootloader.bin / partition-table.bin / app.bin，请改用 -Bin 手动指定单个镜像。'; return $false
            }
            $pairs += ,@('0x1000', $boot)
            $pairs += ,@('0x8000', $part)
            $pairs += ,@('0x10000', $app)
        }
    } else {
        Write-Err '请提供 -Bin <固件.bin> 或 -BinDir <构建输出目录>。'; return $false
    }

    Write-Info "免工程烧录：$Port，共 $($pairs.Count) 个镜像"
    $argList = @('--port', $Port) + $chipArgs + @('write_flash') + $writeArgs
    foreach ($p in $pairs) { $argList += $p }
    Write-Host ('  esptool ' + ($argList -join ' ')) -ForegroundColor DarkGray
    & esptool @argList 2>&1 | ForEach-Object { Write-Host $_ }
    $ok = ($LASTEXITCODE -eq 0)
    if ($ok) { Write-Ok '免工程烧录完成（Hash of data verified 即成功）。' } else { Write-Err '免工程烧录失败。' }
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
        Write-Host "  ESP32 烧录工具 v$ScriptVersion" -ForegroundColor Cyan
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
        Write-Host '    [9] 免工程烧录  (esptool 直写 .bin，无需项目)'
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
            '9' { try {
                    $Port = Ensure-Port $Port
                    Write-Info "免工程烧录，端口：$Port"
                    $t = Read-Host '  类型 [1] 单个bin文件  [2] 构建目录(自动识别)'
                    if ($t -eq '1') {
                        $f = Read-Host '  固件 .bin 绝对路径'
                        $a = Read-Host '  起始地址(默认 0x10000)'
                        if (-not $a) { $a = '0x10000' }
                        Invoke-FlashBin -Port $Port -Bin $f -BinAddr $a
                    } elseif ($t -eq '2') {
                        $d = Read-Host '  构建输出目录绝对路径'
                        Invoke-FlashBin -Port $Port -BinDir $d
                    } else { Write-Warn '无效选择，已取消。' }
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
# 免工程烧录（-FlashBin）不依赖 ESP-IDF 项目，无需项目校验
if (-not $FlashBin) {
    if ($Project -and -not (Test-IdfProject $Project)) { Write-Warn "指定项目缺少 CMakeLists.txt：$Project" }
    if (-not $Project) {
        Write-Err '未自动定位到有效的 ESP-IDF 项目目录（未找到 CMakeLists.txt）。'
        Write-Warn '请用 -Project <路径> 指定，或在交互菜单按 [7] 修改项目路径。'
    }
}

# 仅列出端口
if ($ListPorts) { $devs = Get-SerialDevices; Show-Devices $devs; exit 0 }

# 非交互：构建 / 识别 / 烧录 / 监视 / 免工程烧录
if ($Build -or $Identify -or $Flash -or $Monitor -or $FlashBin) {
    if ($Build) { if (Invoke-Build $Project) { exit 0 } else { exit 1 } }
    try {
        $Port = Ensure-Port $Port
        Write-Info "使用端口：$Port"
        if ($Identify) { if (Invoke-ChipId $Port) { exit 0 } else { exit 1 } }
        if ($Monitor)  { Invoke-Monitor $Project $Port; exit 0 }
        if ($Flash)    { if (Invoke-Flash $Project $Port $SkipBuild) { exit 0 } else { exit 1 } }
        if ($FlashBin) { if (Invoke-FlashBin -Port $Port -Bin $Bin -BinAddr $BinAddr -BinDir $BinDir) { exit 0 } else { exit 1 } }
    } catch { Write-Err $_.Exception.Message; exit 1 }
}

# 默认交互菜单
Show-InteractiveMenu $Project $Port
