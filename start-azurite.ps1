param(
    [switch]$WithCompose
)

if ($WithCompose) {
    Write-Host "Levantando Azurite con docker-compose..." -ForegroundColor Cyan
    docker compose -f docker-compose.azurite.yml up -d
    exit $LASTEXITCODE
}

Write-Host "Levantando Azurite con docker run directo..." -ForegroundColor Cyan

docker run `
  --name azurite `
  -p 10000:10000 `
  -p 10001:10001 `
  -p 10002:10002 `
  -v "$PSScriptRoot/azurite-data:/data" `
  -d mcr.microsoft.com/azure-storage/azurite `
  azurite --location /data --debug /data/debug.log --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0
