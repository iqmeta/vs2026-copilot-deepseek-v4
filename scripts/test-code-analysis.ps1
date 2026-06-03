#!/usr/bin/env pwsh
# test-code-analysis.ps1
# Sends the actual project code to every active model through the proxy
# and asks for a code review / analysis. Measures latency and captures responses.
# Output: docs/testing/code-analysis-results.json
#
# Usage: .\scripts\test-code-analysis.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$ProxyBase   = "http://localhost:11434"
$OutputFile  = Join-Path $PSScriptRoot "..\docs\testing\code-analysis-results.json"
$RepoRoot    = Join-Path $PSScriptRoot ".."

# ── Build the code context from the actual project files ──────────────────────
function Get-ProjectContext {
    $files = @(
        "Program.cs",
        "config/model-selection/nvidia.json",
        "config/model-selection/deepseek.json",
        "config/model-selection/openai.json"
    )

    $parts = @()
    foreach ($rel in $files) {
        $full = Join-Path $RepoRoot $rel
        if (Test-Path $full) {
            $content = Get-Content $full -Raw -Encoding UTF8
            $parts += "### FILE: $rel`n``````n$content`n``````"
        }
    }
    return $parts -join "`n`n"
}

$codeContext = Get-ProjectContext

$systemPrompt = @"
You are a senior .NET 10 software architect performing a code review.
Analyse the code provided and return a structured JSON response with:
{
  "summary": "1-2 sentence overall assessment",
  "bugs": [ { "location": "file:line", "description": "...", "severity": "critical|high|medium|low" } ],
  "security_issues": [ { "location": "...", "description": "...", "severity": "..." } ],
  "performance_issues": [ { "location": "...", "description": "...", "severity": "..." } ],
  "improvements": [ { "location": "...", "description": "..." } ],
  "score": 0-10
}
Return ONLY the JSON, no markdown fences.
"@

$userPrompt = "Review the following .NET 10 multi-provider AI proxy project:`n`n$codeContext"

# ── Discover active models ────────────────────────────────────────────────────
Write-Host "`n[*] Discovering models from $ProxyBase/api/tags ..." -ForegroundColor Cyan
try {
    $tagsResp = Invoke-RestMethod -Uri "$ProxyBase/api/tags" -Method GET -TimeoutSec 15
    $models = $tagsResp.models | ForEach-Object { $_.model }
} catch {
    Write-Host "[-] Could not reach proxy: $_" -ForegroundColor Red
    exit 1
}

if (-not $models -or $models.Count -eq 0) {
    Write-Host "[-] No models returned by proxy." -ForegroundColor Red
    exit 1
}

Write-Host "[+] Found $($models.Count) model(s): $($models -join ', ')" -ForegroundColor Green

# ── Test each model ───────────────────────────────────────────────────────────
$results = @()

foreach ($model in $models) {
    Write-Host "`n──────────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host "[>] Testing: $model" -ForegroundColor Yellow

    $body = @{
        model    = $model
        stream   = $false
        messages = @(
            @{ role = "system"; content = $systemPrompt }
            @{ role = "user";   content = $userPrompt   }
        )
    } | ConvertTo-Json -Depth 5

    $result = [ordered]@{
        model      = $model
        success    = $false
        latency_ms = $null
        response   = $null
        parsed     = $null
        error      = $null
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod `
            -Uri "$ProxyBase/v1/chat/completions" `
            -Method POST `
            -Body $body `
            -ContentType "application/json; charset=utf-8" `
            -TimeoutSec 180

        $sw.Stop()
        $result.latency_ms = $sw.ElapsedMilliseconds

        # content may be a string or already a parsed object depending on the model
        $rawContent = $resp.choices[0].message.content
        $rawStr = if ($rawContent -is [string]) { $rawContent } else { $rawContent | ConvertTo-Json -Depth 20 }
        $result.response = $rawStr

        # Try to parse the JSON the model returned
        $parseOk = $false
        $parsed  = $null
        if ($rawContent -is [string]) {
            try   { $parsed = $rawContent | ConvertFrom-Json -ErrorAction Stop; $parseOk = $true } catch {}
        } else {
            # Already a PSCustomObject — model returned valid JSON that Invoke-RestMethod auto-parsed
            $parsed  = $rawContent
            $parseOk = $true
        }

        if ($parseOk -and $parsed) {
            $result.parsed  = $parsed
            $result.success = $true

            $score    = if ($parsed.PSObject.Properties['score'])              { $parsed.score }              else { '?' }
            $bugCount = if ($parsed.PSObject.Properties['bugs'])               { @($parsed.bugs).Count }       else { 0 }
            $secCount = if ($parsed.PSObject.Properties['security_issues'])    { @($parsed.security_issues).Count } else { 0 }
            $perfCount= if ($parsed.PSObject.Properties['performance_issues']) { @($parsed.performance_issues).Count } else { 0 }
            $impCount = if ($parsed.PSObject.Properties['improvements'])       { @($parsed.improvements).Count } else { 0 }

            Write-Host "[+] ✅ OK | $($sw.ElapsedMilliseconds)ms | score=$score | bugs=$bugCount | sec=$secCount | perf=$perfCount | improvements=$impCount" -ForegroundColor Green
            if ($parsed.PSObject.Properties['summary']) {
                Write-Host "    📝 $($parsed.summary)" -ForegroundColor White
            }
        } else {
            # Model returned text but not valid JSON — still mark as success
            $result.success = $true
            $preview = $rawStr.Substring(0, [Math]::Min(200, $rawStr.Length))
            Write-Host "[~] ⚠ OK but non-JSON response | $($sw.ElapsedMilliseconds)ms" -ForegroundColor Yellow
            Write-Host "    $preview..." -ForegroundColor Gray
        }
    } catch {
        $sw.Stop()
        $result.latency_ms = $sw.ElapsedMilliseconds
        $errMsg = $_.Exception.Message
        $result.error = $errMsg
        Write-Host "[-] ❌ FAILED | $($sw.ElapsedMilliseconds)ms | $errMsg" -ForegroundColor Red
    }

    $results += $result
}

# ── Summary table ─────────────────────────────────────────────────────────────
Write-Host "`n`n══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
$ok   = ($results | Where-Object { $_.success }).Count
$fail = ($results | Where-Object { -not $_.success }).Count
Write-Host "  Passed : $ok / $($results.Count)" -ForegroundColor $(if ($ok -eq $results.Count) { 'Green' } else { 'Yellow' })
Write-Host "  Failed : $fail / $($results.Count)" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

foreach ($r in $results) {
    $icon   = if ($r.success) { '✅' } else { '❌' }
    $latStr = if ($r.latency_ms) { "$($r.latency_ms)ms" } else { '?' }
    $score  = ''
    if ($r.parsed -and $r.parsed.PSObject.Properties['score']) { $score = " | score=$($r.parsed.score)/10" }
    Write-Host "  $icon $($r.model) | $latStr$score"
}

# ── Persist results ───────────────────────────────────────────────────────────
$outputDir = Split-Path $OutputFile
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

$output = [ordered]@{
    timestamp = (Get-Date -Format "o")
    proxy     = $ProxyBase
    models_tested = $results.Count
    passed    = $ok
    failed    = $fail
    results   = $results
}

$output | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputFile -Encoding UTF8
Write-Host "`n[+] Results saved to: $OutputFile" -ForegroundColor Green
