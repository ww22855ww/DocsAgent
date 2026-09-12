<#
.SYNOPSIS
    Put the demo back to its starting state.

.DESCRIPTION
    Clears the working directories, empties the task and result tables, restores
    the mock documents, and sets the supplier scenario back to the resolvable
    case. Run it before every rehearsal and before the meeting.

    Master data is left alone unless -Full is given: mock_suppliers and
    mock_parts hold real Oracle EBS vendors and reseeding them is only needed
    after editing db/init.sql.

.PARAMETER Full
    Also reseed the supplier and part master tables from db/init.sql.

.PARAMETER Ambiguous
    Leave the scenario in the ambiguous state (both 勝宏科技 entities enabled)
    instead of resetting it to unique.

.EXAMPLE
    ./scripts/reset-demo.ps1
    ./scripts/reset-demo.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Ambiguous
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

# client_min_messages keeps TRUNCATE ... CASCADE from printing a NOTICE for
# every dependent table, which drowns out the progress lines.
$db = @('exec', '-i', 'adp-postgres', 'psql', '-U', 'agent', '-d', 'agentdemo',
        '-q', '-v', 'ON_ERROR_STOP=1', '-c', 'SET client_min_messages = warning;')

function Step($text) { Write-Host "  $text" -ForegroundColor DarkGray }

try {
    Write-Host "`nResetting the demo" -ForegroundColor Cyan
    Write-Host "  $repo`n" -ForegroundColor DarkGray

    # --- containers must be up, or nothing below can work -------------------
    $running = (docker compose ps --status running --format '{{.Service}}') -join ' '
    foreach ($svc in 'postgres', 'agent-api', 'mcp-worker', 'mock-portal', 'frontend') {
        if ($running -notmatch [regex]::Escape($svc)) {
            Write-Host "  $svc is not running. Start the stack first:" -ForegroundColor Red
            Write-Host "    docker compose up -d`n" -ForegroundColor Red
            exit 1
        }
    }
    Step 'all five containers are running'

    # --- working directories ------------------------------------------------
    $cleared = 0
    foreach ($dir in 'downloads', 'staging', 'archive', 'output') {
        $path = Join-Path $repo "data/$dir"
        if (Test-Path $path) {
            $files = Get-ChildItem $path -File | Where-Object { $_.Name -ne '.gitkeep' }
            $cleared += $files.Count
            $files | Remove-Item -Force
        }
    }
    Step "cleared $cleared file(s) from data/"

    # --- task and result tables ---------------------------------------------
    # Master data is intentionally not touched here.
    'TRUNCATE tasks, archives, manual_reviews RESTART IDENTITY CASCADE;' |
        docker @db -f - | Out-Null
    Step 'emptied tasks, archives and manual_reviews'

    if ($Full) {
        'TRUNCATE mock_suppliers, mock_parts CASCADE;' | docker @db -f - | Out-Null
        Get-Content (Join-Path $repo 'db/init.sql') -Raw | docker @db -f - | Out-Null
        Step 'reseeded supplier and part master from db/init.sql'
    }

    # --- mock documents -----------------------------------------------------
    # Regenerated in the worker image, which already has openpyxl.
    $mockData = (Join-Path $repo 'mock-portal/MockData') -replace '\\', '/'
    $scripts  = (Join-Path $repo 'scripts') -replace '\\', '/'
    $env:MSYS_NO_PATHCONV = '1'
    docker run --rm -v "${scripts}:/scripts:ro" -v "${mockData}:/out" `
        agentic-document-demo-mcp-worker python /scripts/gen_mock_data.py /out | Out-Null
    $count = (Get-ChildItem (Join-Path $repo 'mock-portal/MockData') -File).Count
    Step "regenerated $count mock document(s)"

    # --- scenario -----------------------------------------------------------
    $target = if ($Ambiguous) { 'ambiguous' } else { 'unique' }
    python (Join-Path $repo 'scripts/scenario.py') $target | Out-Null
    Step "supplier scenario set to $target"

    # --- confirm ------------------------------------------------------------
    Write-Host "`nReady." -ForegroundColor Green
    try {
        $health = Invoke-RestMethod -Uri 'http://localhost:5000/api/health/services' -TimeoutSec 30
        $bad = @($health.services | Where-Object { -not $_.ok -and $_.required })
        if ($health.ready) {
            Write-Host "  every required service responded" -ForegroundColor DarkGray
        } else {
            Write-Host "  not ready: $($bad.name -join ', ')" -ForegroundColor Yellow
        }
        Write-Host "  llm=$($health.mode.llmModel)  master=$($health.mode.dbquery)  mail=$(if ($health.mode.mailEnabled) { 'on' } else { 'off' })" -ForegroundColor DarkGray
    } catch {
        Write-Host "  could not reach the health endpoint yet; give agent-api a moment" -ForegroundColor Yellow
    }

    Write-Host "`n  Console   http://localhost:5173"
    Write-Host "  Portal    http://localhost:5100  (admin / 123456)`n"
}
finally {
    Pop-Location
}
