# Scaffold model và DbContext từ database hiện có (chỉ đọc metadata, không sửa database).
# Cần ConnectionStrings:DeCakesDb trong user-secrets hoặc biến môi trường ConnectionStrings__DeCakesDb.
# Script không in chuỗi kết nối. Không ghi đè model đã có trừ khi truyền -Force.
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$api = 'src/DeCakes.Api'
$id = [regex]::Match((Get-Content "$api/DeCakes.Api.csproj" -Raw), '<UserSecretsId>([^<]+)').Groups[1].Value
$secretFile = Join-Path $env:APPDATA "Microsoft\UserSecrets\$id\secrets.json"
$hasSecret = (Test-Path $secretFile) -and (Select-String $secretFile -Pattern 'ConnectionStrings:DeCakesDb' -Quiet)
$hasEnv = [bool]$env:ConnectionStrings__DeCakesDb
if (-not ($hasSecret -or $hasEnv)) {
    Write-Error 'Thiếu ConnectionStrings:DeCakesDb (user-secrets hoặc biến môi trường). Xem README, mục Cấu hình bí mật.'
}

$out = 'src/DeCakes.Infrastructure/Persistence/Models'
if ((Test-Path $out) -and (Get-ChildItem $out -Filter *.cs -ErrorAction SilentlyContinue) -and -not $Force) {
    Write-Error "Đã có model trong $out. Rà soát rồi chạy lại với -Force nếu muốn ghi đè."
}

dotnet tool restore | Out-Null
dotnet dotnet-ef dbcontext scaffold 'Name=ConnectionStrings:DeCakesDb' MySql.EntityFrameworkCore `
    --project src/DeCakes.Infrastructure --startup-project $api `
    --context DeCakesDbContext --context-dir Persistence --output-dir Persistence/Models `
    --namespace DeCakes.Infrastructure.Persistence.Models --context-namespace DeCakes.Infrastructure.Persistence `
    --no-onconfiguring --no-pluralize --force:$Force
