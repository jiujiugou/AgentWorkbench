# 从国家市场监督管理总局「合同示范文本库」下载公开合同示范文本（PDF）。
# 用法：pwsh -File fetch-corpus.ps1
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = "https://htsfwb.samr.gov.cn/api/File/DownTemplate"

$items = @(
  @{ n = "samr-01-yanxue";       id = "ea53524e-0970-4806-adeb-d98332f7d397"; t = "研学旅游合同（文化和旅游部、市场监管总局2026版）" },
  @{ n = "samr-02-yiriyou";      id = "72b8ae0e-139d-4e59-ac4b-c761c2e2156a"; t = "境内旅游一日游合同（文化和旅游部、市场监管总局2026版）" },
  @{ n = "samr-03-chujing";      id = "b47bb5dc-565c-4955-910b-f6fd312920ad"; t = "团队出境旅游合同（文化和旅游部、市场监管总局2026版）" },
  @{ n = "samr-04-fangwumaimai"; id = "cbb13efd-97cf-4941-a76a-1627faf942cc"; t = "山东省新建商品房买卖合同（预售）（山东省2025版）" },
  @{ n = "samr-05-jiating";      id = "c1bfbbc8-9699-4bf3-984f-43379b71d7f5"; t = "黑龙江省家庭服务合同（中介制）（黑龙江省2025版）" }
)

foreach ($item in $items) {
    $out = Join-Path $dir ($item.n + ".pdf")
    & curl.exe -s -L -o $out "$base?id=$($item.id)&type=2"
    $length = (Get-Item -LiteralPath $out).Length
    Write-Host ("downloaded {0} ({1} bytes) - {2}" -f $item.n, $length, $item.t)
}
