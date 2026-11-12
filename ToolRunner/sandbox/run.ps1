# Runs toolrunner with the fake docker first on PATH. Usage: sandbox\run.ps1 [--dry-run] debug pluginhost
# Fake docker state and call log: sandbox\work\fakedocker-state.json(.calls.log)
$root = Split-Path $PSScriptRoot -Parent
$env:PATH = "$root\src\FakeDocker\bin\Debug\net10.0;$env:PATH"
$env:FAKEDOCKER_STATE = "$PSScriptRoot\work\fakedocker-state.json"
Push-Location "$root\src\ToolRunner"
try { & "$root\src\ToolRunner\bin\Debug\net10.0\toolrunner.exe" @args; exit $LASTEXITCODE } finally { Pop-Location }
