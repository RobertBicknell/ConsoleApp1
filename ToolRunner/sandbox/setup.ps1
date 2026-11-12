# Creates a disposable test world under sandbox\work: two git repos (myapp, pluginhost), a globals.user.json
# pointing toolrunner at them, and a built fake `docker`. Safe to re-run: it wipes and recreates sandbox\work.
$ErrorActionPreference = 'Continue'   # git writes CRLF warnings to stderr; 'Stop' would abort on them
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $PSScriptRoot 'work'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory "$work\myapp", "$work\myapp\data", "$work\pluginhost", "$work\other" | Out-Null

@'
services:
  pluginhost:
    image: myorg/pluginhost:dev
    ports:
      - "5000:5000"
    volumes:
      - ./data:/var/lib/plugins
      - pluginstate:/state
  gateway:
    image: myorg/gateway:${GATEWAY_TAG:-dev}
    ports:
      - "${GATEWAY_PORT:-8080}:80"
      - 127.0.0.1:9090:90/tcp
      - target: 443
        published: 8443
        protocol: tcp
    expose:
      - "9000"
    volumes:
      - ./missing:/config:ro
      - ${LOGS_DIR:-./logs}:/logs
      - /var/run/docker.sock:/var/run/docker.sock
      - type: bind
        source: ./data
        target: /shared
    tmpfs:
      - /run
    env_file:
      - .env
      - gateway.env
    environment:
      - LOG_LEVEL=${LOG_LEVEL}
      - DB_PASSWORD=${DB_PASSWORD}
      - PLUGINHOST_URL=http://${PLUGINHOST_HOST}:${PLUGINHOST_PORT}
    depends_on:
      - pluginhost
volumes:
  pluginstate:
  sharedcache:
    external: true
'@ | Set-Content "$work\myapp\docker-compose.yml"
# a second, unrelated stack: publishes the same host port 5000 and shares a bind source with myapp
@'
services:
  legacy:
    image: myorg/legacy:1
    ports:
      - "5000:80"
    volumes:
      - ../myapp/data:/data
      - cache:/cache
'@ | Set-Content "$work\other\docker-compose.yml"
@'
PLUGINHOST_HOST=pluginhost
PLUGINHOST_PORT=5000
LOG_LEVEL=info
'@ | Set-Content "$work\myapp\.env"
"FEATURE_X=on`nAPI_TOKEN=abc123`nLEGACY_FLAG=1" | Set-Content "$work\myapp\gateway.env"
# plugin set-up: pluginhost bind-mounts .\data; its appsettings lists two assemblies, only one of which is on disk
New-Item -ItemType Directory "$work\myapp\data\plugins" | Out-Null
@'
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Plugins": {
    "Path": "/var/lib/plugins/plugins",
    "Assemblies": [ "Acme.Billing", "Acme.Audit" ]
  }
}
'@ | Set-Content "$work\myapp\data\appsettings.json"
"fake" | Set-Content "$work\myapp\data\plugins\Acme.Audit.dll"
"fake" | Set-Content "$work\myapp\data\plugins\Acme.Legacy.dll"
".env`n.toolrunner/`ngateway.env`ndata/" | Set-Content "$work\myapp\.gitignore"
"print('plugin host')" | Set-Content "$work\pluginhost\main.py"

foreach ($r in 'myapp', 'pluginhost') {
    git -C "$work\$r" init -q
    git -C "$work\$r" add -A
    git -C "$work\$r" -c user.name=sandbox -c user.email=sandbox@local commit -q -m init
}

$app = ($work -replace '\\', '/') + '/myapp'
"{ `"APP_ROOT`": `"$app`" }" | Set-Content "$root\src\ToolRunner\config\globals.user.json"

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'C:\Program Files\dotnet\dotnet.exe' }
& $dotnet build "$root\ToolRunner.sln" -nologo -v q
Write-Host "sandbox ready. try: sandbox\run.ps1 doctor"

