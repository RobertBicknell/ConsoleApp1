# Generates a realistic sample deployment twice under sandbox\samples:
#   reference\  a known-good deployment (two stacks: core, reporting)
#   current\    the same deployment after someone has been working on it, with deliberate differences of every kind
# and points the tool at them (DEPLOY_ROOT / REFERENCE_ROOT in your personal layer, globals.user.json).
# Safe to re-run: it wipes and recreates sandbox\samples. Nothing here is a git repo or runs containers; it is for
# the deployment scan, the plugin check and `compare`.
#   toolrunner scan dir=<...>\samples\current format=text
#   toolrunner compare format=text
$root = Split-Path $PSScriptRoot -Parent
$samples = Join-Path $PSScriptRoot 'samples'
if (Test-Path $samples) { Remove-Item $samples -Recurse -Force }

function W([string]$rel, [string]$text, [switch]$Crlf) {      # write with LF endings unless -Crlf
    $path = Join-Path $samples $rel
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    $text = $text -replace "`r`n", "`n"
    if ($Crlf) { $text = $text -replace "`n", "`r`n" }
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}
function Dll([string]$rel, [string]$content) { W $rel "MZ-fake-assembly:$content" }      # stand-ins: only name and bytes matter

# ---------------------------------------------------------------- reference: core stack
$coreCompose = @'
# Core stack: gateway in front of the plugin host and the orders API.
services:
  gateway:
    image: registry.example.com/acme/gateway:${GATEWAY_TAG}
    ports:
      - "${GATEWAY_PORT:-8080}:80"
      - "127.0.0.1:9090:9090"
    env_file:
      - .env
      - product.env
    environment:
      - PLUGINHOST_URL=http://${PLUGINHOST_HOST}:${PLUGINHOST_PORT}
      - ORDERS_URL=http://orders-api:5100
      - LOG_LEVEL=${LOG_LEVEL}
    volumes:
      - ./config/gateway:/app/config:ro
      - ./logs/gateway:/app/logs
    depends_on:
      - pluginhost
      - orders-api

  pluginhost:
    image: registry.example.com/acme/pluginhost:${PLUGINHOST_TAG}
    ports:
      - "5000:5000"
    env_file:
      - .env
      - product.env
    volumes:
      - ./config/pluginhost:/app/config:ro
      - ./plugins:/app/plugins:ro
      - ./logs/pluginhost:/app/logs
    depends_on:
      db:
        condition: service_healthy
      cache:
        condition: service_started

  orders-api:
    image: registry.example.com/acme/orders-api:${ORDERS_TAG}
    ports:
      - "5100:5100"
    env_file: product.env
    environment:
      - ConnectionStrings__Main=Host=db;Database=orders;Username=orders;Password=${DB_PASSWORD}
      - CACHE_HOST=cache
    volumes:
      - ./logs/orders:/app/logs
    depends_on:
      - db
      - cache

  db:
    image: postgres:16.3
    environment:
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - dbdata:/var/lib/postgresql/data

  cache:
    image: redis:7.2

volumes:
  dbdata:
'@
W 'reference\core\docker-compose.yml' $coreCompose
W 'reference\core\.env' @'
# Image tags
GATEWAY_TAG=3.1.0
PLUGINHOST_TAG=3.1.0
ORDERS_TAG=2.4.1

PLUGINHOST_HOST=pluginhost
PLUGINHOST_PORT=5000
LOG_LEVEL=info
LEGACY_MODE=1
DB_PASSWORD=reference-db-secret
'@
W 'reference\core\product.env' @'
PRODUCT_NAME=AcmeCommerce
PRODUCT_REGION=eu
PRODUCT_TENANT=acme-prod
'@
W 'reference\core\config\gateway\appsettings.json' @'
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "Routes": { "Orders": "/api/orders", "Plugins": "/api/plugins" },
  "Timeouts": { "UpstreamSeconds": 30 }
}
'@
W 'reference\core\config\pluginhost\appsettings.json' @'
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Plugins": {
    "Path": "/app/plugins",
    "Assemblies": [ "Acme.Billing", "Acme.Audit", "Acme.Tax" ],
    "Options": { "LoadTimeoutSeconds": 20, "IsolateLoadContexts": true }
  }
}
'@
W 'reference\core\config\gateway\nlog.config' @'
<?xml version="1.0" encoding="utf-8"?>
<nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd" autoReload="true" throwConfigExceptions="false">
  <variable name="logDir" value="/app/logs" />
  <targets async="true">
    <target name="file" type="File" fileName="${logDir}/gateway-${shortdate}.log" archiveEvery="Day" maxArchiveFiles="14">
      <layout type="JsonLayout">
        <attribute name="time" layout="${longdate}" />
        <attribute name="level" layout="${level:upperCase=true}" />
        <attribute name="message" layout="${message}" />
      </layout>
    </target>
    <target name="console" type="Console" layout="${longdate} ${level} ${logger} ${message}" />
  </targets>
  <rules>
    <logger name="Microsoft.*" maxlevel="Info" final="true" />
    <logger name="*" minlevel="Info" writeTo="file,console" />
  </rules>
</nlog>
'@
Dll 'reference\core\plugins\Acme.Billing.dll' 'billing-1.4.0'
Dll 'reference\core\plugins\Acme.Audit.dll' 'audit-2.0.3'
Dll 'reference\core\plugins\Acme.Tax.dll' 'tax-1.1.0'
Dll 'reference\core\plugins\Acme.Plugin.Abstractions.dll' 'abstractions-3.0.0'
foreach ($s in 'gateway', 'pluginhost', 'orders') { W "reference\core\logs\$s\.gitkeep" '' }

# ---------------------------------------------------------------- reference: reporting stack
W 'reference\reporting\docker-compose.yml' @'
services:
  reports:
    image: registry.example.com/acme/reports:${REPORTS_TAG}
    ports:
      - "5200:5200"
    env_file: product.env
    volumes:
      - ./config/reports:/app/config:ro
      - ./plugins:/app/plugins:ro
      - reportcache:/var/cache/reports
  scheduler:
    image: registry.example.com/acme/scheduler:${REPORTS_TAG}
    depends_on: [reports]

volumes:
  reportcache:
'@
W 'reference\reporting\.env' "REPORTS_TAG=1.9.2`nREPORT_RETENTION_DAYS=90`n"
W 'reference\reporting\product.env' "PRODUCT_NAME=AcmeCommerce`nPRODUCT_REGION=eu`n"
W 'reference\reporting\config\reports\appsettings.json' @'
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Plugins": { "Assemblies": [ "Acme.Reports.Pdf" ] },
  "Output": { "Folder": "/var/cache/reports", "Format": "pdf" }
}
'@
W 'reference\reporting\config\reports\appsettings.Production.json' @'
{ "Logging": { "LogLevel": { "Default": "Warning" } } }
'@
Dll 'reference\reporting\plugins\Acme.Reports.Pdf.dll' 'reports-pdf-1.9.2'
W 'reference\shared\nginx.conf' "server {`n  listen 80;`n  location / { proxy_pass http://gateway:80; }`n}`n"
W 'reference\README.txt' "Acme deployment, release 2026.10. Known-good reference copy.`n"

W 'reference\.gitignore' ".toolrunner/`n"

# ---------------------------------------------------------------- current: start as an exact copy, then diverge
Copy-Item (Join-Path $samples 'reference') (Join-Path $samples 'current') -Recurse

# Both folders are git repositories, as real deployment folders almost always are. In `current` the commit holds the
# known-good state and every difference below is an uncommitted change, so `compare ref=git:HEAD` finds the same things.
function Commit([string]$dir, [string]$message) {
    $ErrorActionPreference = 'Continue'      # git reports line-ending notes on stderr
    git -C $dir init -q 2>$null
    git -C $dir add -A 2>$null
    git -C $dir -c user.name=samples -c user.email=samples@local commit -q -m $message 2>$null
}
Commit (Join-Path $samples 'reference') 'Release 2026.10'
Commit (Join-Path $samples 'current') 'Release 2026.10'

# ---------------------------------------------------------------- "running" containers, in the fake docker
# Started now, while `current` is still the committed release: what runs is the known-good state, and the edits below
# are then made to the files without restarting anything. That is the situation the runtime check exists for.
$fwd = ($samples -replace '\\', '/')
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'C:\Program Files\dotnet\dotnet.exe' }
& $dotnet build "$root\src\ToolRunner\ToolRunner.csproj" -nologo -v q | Out-Null
New-Item -ItemType Directory -Force "$PSScriptRoot\work" | Out-Null
Remove-Item "$PSScriptRoot\work\fakedocker-state.json*" -ErrorAction SilentlyContinue
foreach ($stack in 'core', 'reporting') {
    cmd /c "powershell -NoProfile -File `"$PSScriptRoot\run.ps1`" --set APP_ROOT=$fwd/current/$stack run compose verb=up 2>nul" | Out-Null
}

# core/.env: a level raised, a tag bumped to a pre-release, a key added, a key dropped, a secret rotated
W 'current\core\.env' @'
# Image tags
GATEWAY_TAG=3.1.0
PLUGINHOST_TAG=3.1.0
ORDERS_TAG=2.5.0-rc1

PLUGINHOST_HOST=pluginhost
PLUGINHOST_PORT=5000
LOG_LEVEL=debug
FEATURE_NEW_PRICING=on
DB_PASSWORD=rotated-on-site
'@
W 'current\core\product.env' "PRODUCT_NAME=AcmeCommerce`nPRODUCT_REGION=us`nPRODUCT_TENANT=acme-prod`n"

# core compose: gateway moved to 8081, a hotfix folder bind-mounted into orders-api (which does not exist on disk),
# the cache service removed (and its dependants relaxed), a metrics service added, and a comment changed
$cur = $coreCompose
$cur = $cur.Replace('# Core stack: gateway in front of the plugin host and the orders API.', '# Core stack. EDITED ON SITE - see ticket OPS-1432.')
$cur = $cur.Replace('"${GATEWAY_PORT:-8080}:80"', '"${GATEWAY_PORT:-8081}:80"')
$cur = $cur.Replace("    volumes:`n      - ./logs/orders:/app/logs", "    volumes:`n      - ./logs/orders:/app/logs`n      - ./hotfix:/app/hotfix:ro")
$cur = $cur.Replace("      cache:`n        condition: service_started`n", '')
$cur = $cur.Replace("      - CACHE_HOST=cache`n", '')
$cur = $cur.Replace("    depends_on:`n      - db`n      - cache`n", "    depends_on:`n      - db`n")
$cur = $cur.Replace("  cache:`n    image: redis:7.2`n", "  metrics:`n    image: prom/prometheus:v2.53.0`n    ports:`n      - `"9091:9090`"`n")
W 'current\core\docker-compose.yml' ($cur -replace "`r`n", "`n")

# pluginhost settings: Acme.Tax dropped, Acme.Loyalty added (but its .dll was never copied), a timeout changed
W 'current\core\config\pluginhost\appsettings.json' @'
{
  "Logging": { "LogLevel": { "Default": "Debug" } },
  "Plugins": {
    "Path": "/app/plugins",
    "Assemblies": [ "Acme.Billing", "Acme.Audit", "Acme.Loyalty" ],
    "Options": { "LoadTimeoutSeconds": 60, "IsolateLoadContexts": true }
  }
}
'@
Remove-Item (Join-Path $samples 'current\core\plugins\Acme.Tax.dll')
Dll 'current\core\plugins\Acme.Billing.dll' 'billing-1.5.0-hotfix'            # same name, different build
Dll 'current\core\plugins\Acme.Experimental.dll' 'experimental-0.1'          # on disk but listed nowhere

# reporting: compose reformatted only (no real change), the Production settings file missing, a stray note added
W 'current\reporting\docker-compose.yml' @'
# reformatted by an editor - no functional change
services:
  reports:
    image: registry.example.com/acme/reports:${REPORTS_TAG}
    ports: ["5200:5200"]
    env_file: product.env
    volumes:
      - ./config/reports:/app/config:ro
      - ./plugins:/app/plugins:ro
      - reportcache:/var/cache/reports

  scheduler:
    image: registry.example.com/acme/scheduler:${REPORTS_TAG}
    depends_on:
      - reports

volumes:
  reportcache:
'@
Remove-Item (Join-Path $samples 'current\reporting\config\reports\appsettings.Production.json')
W 'current\reporting\extra\notes.txt' "Left here by the on-site engineer. Remember to revert the gateway port.`n"
W 'current\shared\nginx.conf' "server {`n  listen 80;`n  location / { proxy_pass http://gateway:80; }`n}`n" -Crlf      # same text, Windows line endings
W 'current\README.txt' "Acme deployment, release 2026.10. Working copy on the customer site.`nPatched 2026-11-03.`n"

# the gateway's logging config (XML): someone turned on debug logging
$nlog = Join-Path $samples 'current\core\config\gateway\nlog.config'
[IO.File]::WriteAllText($nlog, ([IO.File]::ReadAllText($nlog)).Replace('<logger name="*" minlevel="Info"', '<logger name="*" minlevel="Debug"'))

# ---------------------------------------------------------------- a plugin project to publish (its own repo, outside the deployment)
W 'plugin-src\Acme.Loyalty\Acme.Loyalty.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@
W 'plugin-src\Acme.Loyalty\LoyaltyPlugin.cs' @'
namespace Acme.Loyalty;

/// A stand-in plugin: enough to build an assembly called Acme.Loyalty.dll.
public sealed class LoyaltyPlugin
{
    public string Name => "Loyalty";
    public int PointsFor(decimal orderTotal) => (int)Math.Floor(orderTotal / 10m);
}
'@
W 'plugin-src\Acme.Loyalty\.gitignore' "bin/`nobj/`n"
Commit (Join-Path $samples 'plugin-src\Acme.Loyalty') 'Loyalty plugin'

# ---------------------------------------------------------------- point the tool at them (keeping whatever else is in your layer)
$userFile = "$root\src\ToolRunner\config\globals.user.json"
$user = if (Test-Path $userFile) { Get-Content $userFile -Raw | ConvertFrom-Json } else { New-Object psobject }
$fwd = ($samples -replace '\\', '/')
$user | Add-Member -NotePropertyName APP_ROOT -NotePropertyValue "$fwd/current/core" -Force       # workflows act on the core stack
$user | Add-Member -NotePropertyName DEPLOY_ROOT -NotePropertyValue "$fwd/current" -Force
$user | Add-Member -NotePropertyName REFERENCE_ROOT -NotePropertyValue "$fwd/reference" -Force
[IO.File]::WriteAllText($userFile, ($user | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))

Write-Host "samples ready in $samples"
Write-Host "APP_ROOT, DEPLOY_ROOT and REFERENCE_ROOT set in globals.user.json; both stacks are 'running' in the fake docker as they were at the commit."
Write-Host "Try: sandbox\run.ps1 compare format=text      sandbox\run.ps1 compare ref=git:HEAD format=text      sandbox\run.ps1 runtime format=text"
