<#
.SYNOPSIS
    准备 tau2-bench 运行环境（uv + 固定 commit 的源码 + venv）。

.DESCRIPTION
    AgentWorkbench 的 tau2-bench 接入是"外部基准"：本仓库只提供适配器与验收脚本，
    基准本体（含真实业务数据、工具、评分器）克隆在仓库外，并按 commit 固定版本。

    脚本做的事：
      1. 定位 uv（PATH 找不到时回退到 %USERPROFILE%\.local\bin\uv.exe）；
      2. clone/checkout D:\Code\tau2-bench 到固定 commit（幂等，可重复运行）；
      3. uv sync 建 venv；
      4. 补装 websockets（tau2 核心依赖漏声明，见下方注释）；
      5. 冒烟验证 `import tau2` 可用。

    为什么必须补装 websockets：tau2 的 src/tau2/data_model/simulation.py 无条件
    import LiveConfig，而 LiveConfig 依赖 websockets；websockets 不在核心依赖里，
    只 `uv sync` 会在 import 阶段就 ImportError。这里用 `uv pip install websockets`
    固化该修补，避免每次重新踩坑。

.PARAMETER Tau2Dir
    tau2-bench 源码目录。默认 D:\Code\tau2-bench。

.PARAMETER Commit
    固定 commit（tau2 无 tag，只能锁 commit）。默认 4ce7c0397c1eb65c9bbe59aeacfe1ca44a1cd699。

.PARAMETER Ref
    clone 时使用的远端地址。默认 https://github.com/sierra-research/tau2-bench.git。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\benchmark\tau2\setup_env.ps1
#>
[CmdletBinding()]
param(
    [string]$Tau2Dir = "D:\Code\tau2-bench",
    [string]$Commit = "4ce7c0397c1eb65c9bbe59aeacfe1ca44a1cd699",
    [string]$Ref = "https://github.com/sierra-research/tau2-bench.git"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Get-UvPath {
    $cmd = Get-Command uv -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = Join-Path $env:USERPROFILE ".local\bin\uv.exe"
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw "找不到 uv。请先安装（见 https://docs.astral.sh/uv/），或把 uv.exe 放到 $fallback。"
}

$uv = Get-UvPath
Write-Host "[tau2] uv      = $uv"
Write-Host "[tau2] target  = $Tau2Dir"
Write-Host "[tau2] commit  = $Commit"

# --- 1. 获取源码 -----------------------------------------------------------
if (Test-Path -LiteralPath (Join-Path $Tau2Dir ".git")) {
    Write-Host "[tau2] 仓库已存在，检查是否需要切到固定 commit"
    Push-Location $Tau2Dir
    try {
        git fetch --all --tags --quiet
        $head = (git rev-parse HEAD).Trim()
        if ($head -ne $Commit) {
            git checkout --quiet $Commit
        }
    }
    finally { Pop-Location }
}
else {
    if (Test-Path -LiteralPath $Tau2Dir) {
        throw "目标目录已存在但不是 git 仓库：$Tau2Dir。请先手动清理。"
    }
    Write-Host "[tau2] clone $Ref"
    git clone --quiet $Ref $Tau2Dir
    Push-Location $Tau2Dir
    try { git checkout --quiet $Commit }
    finally { Pop-Location }
}

Push-Location $Tau2Dir
try {
    $head = (git rev-parse HEAD).Trim()
    if ($head -ne $Commit) { throw "HEAD=$head，期望 $Commit。" }
    Write-Host "[tau2] HEAD ok = $head"

    # --- 2. 建 venv -------------------------------------------------------
    Write-Host "[tau2] uv sync"
    & $uv sync
    if ($LASTEXITCODE -ne 0) { throw "uv sync 失败（exit=$LASTEXITCODE）。" }

    # --- 3. 修补缺失依赖 --------------------------------------------------
    Write-Host "[tau2] uv pip install websockets（补 tau2 漏声明的核心依赖）"
    & $uv pip install websockets
    if ($LASTEXITCODE -ne 0) { throw "uv pip install websockets 失败（exit=$LASTEXITCODE）。" }

    # --- 4. 冒烟 ----------------------------------------------------------
    Write-Host "[tau2] 冒烟：import tau2"
    & $uv run python -c "import tau2, websockets; from tau2.registry import registry; print('tau2 ok', registry.get_domains())"
    if ($LASTEXITCODE -ne 0) { throw "import tau2 失败（exit=$LASTEXITCODE）。" }
}
finally { Pop-Location }

Write-Host "[tau2] 环境就绪。"
