# Benchmark duplicate models to find fastest provider for each model
# Tests each duplicated model across all providers and measures latency

$ErrorActionPreference = "Stop"
$ProxyUrl = "http://localhost:11434"

# Models to test - each model name as exposed by the proxy
$models = @(
    # DeepSeek V4 Pro - 3 providers
    @{Name="deepseek-v4-pro"; Provider="deepseek"; Qualified="deepseek-ai/deepseek-v4-pro"},
    @{Name="deepseek-v4-pro"; Provider="nvidia"; Qualified="deepseek-ai/deepseek-v4-pro@nvidia"},
    @{Name="deepseek-v4-pro"; Provider="ollama"; Qualified="deepseek-v4-pro@ollama"},

    # Qwen3 Coder 480B - 2 providers
    @{Name="qwen/qwen3-coder-480b-a35b-instruct"; Provider="nvidia"; Qualified="qwen/qwen3-coder-480b-a35b-instruct@nvidia"},
    @{Name="qwen3-coder:480b"; Provider="ollama"; Qualified="qwen3-coder:480b@ollama"},

    # Qwen3.5 397B - 2 providers
    @{Name="qwen/qwen3.5-397b-a17b"; Provider="nvidia"; Qualified="qwen/qwen3.5-397b-a17b@nvidia"},
    @{Name="qwen3.5:397b"; Provider="ollama"; Qualified="qwen3.5:397b@ollama"},

    # GPT-OSS 120B - 2 providers
    @{Name="gpt-oss:120b"; Provider="ollama"; Qualified="gpt-oss:120b@ollama"},
    @{Name="openai/gpt-oss-120b"; Provider="nvidia"; Qualified="openai/gpt-oss-120b@nvidia"},

    # Nemotron 3 Super - 2 providers
    @{Name="nvidia/nemotron-3-super-120b-a12b"; Provider="nvidia"; Qualified="nvidia/nemotron-3-super-120b-a12b@nvidia"},
    @{Name="nemotron-3-super"; Provider="ollama"; Qualified="nemotron-3-super@ollama"},

    # Mistral Large 3 - 1 provider (only ollama, but test both names)
    @{Name="mistral-large-3:675b"; Provider="ollama"; Qualified="mistral-large-3:675b@ollama"},

    # DeepSeek V4 Flash - 2 providers
    @{Name="deepseek-v4-flash"; Provider="deepseek"; Qualified="deepseek-v4-flash@deepseek"},
    @{Name="deepseek-v4-flash"; Provider="ollama"; Qualified="deepseek-v4-flash@ollama"},

    # DeepSeek V3.2 - 2 providers
    @{Name="deepseek-v3.2"; Provider="ollama"; Qualified="deepseek-v3.2@ollama"},
    @{Name="deepseek-v3.1:671b"; Provider="ollama"; Qualified="deepseek-v3.1:671b@ollama"},
    
    # New Ollama models for discovery
    @{Name="glm-5.1"; Provider="ollama"; Qualified="glm-5.1"},
    @{Name="glm-5"; Provider="ollama"; Qualified="glm-5"},
    @{Name="glm-4.7"; Provider="ollama"; Qualified="glm-4.7"},
    @{Name="glm-4.6"; Provider="ollama"; Qualified="glm-4.6"},
    @{Name="minimax-m3"; Provider="ollama"; Qualified="minimax-m3"},
    @{Name="minimax-m2.7"; Provider="ollama"; Qualified="minimax-m2.7"},
    @{Name="minimax-m2.5"; Provider="ollama"; Qualified="minimax-m2.5"},
    @{Name="minimax-m2.1"; Provider="ollama"; Qualified="minimax-m2.1"},
    @{Name="minimax-m2"; Provider="ollama"; Qualified="minimax-m2"},
    @{Name="cogito-2.1:671b"; Provider="ollama"; Qualified="cogito-2.1:671b"},
    @{Name="gemma4:31b"; Provider="ollama"; Qualified="gemma4:31b"},
    @{Name="qwen3-vl:235b-instruct"; Provider="ollama"; Qualified="qwen3-vl:235b-instruct"},
    @{Name="qwen3-vl:235b"; Provider="ollama"; Qualified="qwen3-vl:235b"},
    @{Name="kimi-k2:1t"; Provider="ollama"; Qualified="kimi-k2:1t"},
    @{Name="kimi-k2.6"; Provider="ollama"; Qualified="kimi-k2.6"},
    @{Name="kimi-k2.5"; Provider="ollama"; Qualified="kimi-k2.5"},
    @{Name="kimi-k2-thinking"; Provider="ollama"; Qualified="kimi-k2-thinking"}
)

$results = @()

foreach ($m in $models) {
    $qualifiedName = $m.Qualified
    $modelDisplay = $m.Name
    $provider = $m.Provider
    
    Write-Host "Testing $qualifiedName ($provider)..." -NoNewline
    
    $body = @{
        model = $qualifiedName
        messages = @(
            @{
                role = "user"
                content = "Respond with just the word 'pong'"
            }
        )
        stream = $false
        options = @{
            num_predict = 1
        }
    } | ConvertTo-Json -Compress
    
    $timings = @()
    $successCount = 0
    $errorMsg = $null
    
    # Try 3 times to get average
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $response = Invoke-RestMethod -Uri "$ProxyUrl/api/chat" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60
            $sw.Stop()
            $timings += $sw.ElapsedMilliseconds
            $successCount++
        } catch {
            $errorMsg = $_.Exception.Message
            Write-Host "`n  Error (attempt $attempt): $errorMsg"
        }
    }
    
    if ($timings.Count -gt 0) {
        $avgMs = [math]::Round(($timings | Measure-Object -Average).Average, 0)
        $minMs = ($timings | Measure-Object -Minimum).Minimum
        $maxMs = ($timings | Measure-Object -Maximum).Maximum
        Write-Host " OK [avg: ${avgMs}ms, min: ${minMs}ms, max: ${maxMs}ms]" -ForegroundColor Green
    } else {
        $avgMs = -1
        $minMs = -1
        $maxMs = -1
        Write-Host " FAIL ($errorMsg)" -ForegroundColor Red
    }
    
    $results += [PSCustomObject]@{
        Model = $modelDisplay
        Provider = $provider
        QualifiedName = $qualifiedName
        SuccessCount = $successCount
        AvgLatencyMs = $avgMs
        MinLatencyMs = $minMs
        MaxLatencyMs = $maxMs
        Error = if ($successCount -eq 0) { $errorMsg } else { "" }
    }
}

Write-Host "`n`n========== BENCHMARK RESULTS ==========" -ForegroundColor Cyan
Write-Host "Model`.Display`tProvider`tAvg(ms)`tMin(ms)`tMax(ms)`tSuccess`tError" -ForegroundColor Yellow
$results | Sort-Object Model, AvgLatencyMs | Format-Table Model, Provider, AvgLatencyMs, MinLatencyMs, MaxLatencyMs, SuccessCount, Error -AutoSize

# Group by model name and show fastest provider
Write-Host "`n========== DUPLICATE ANALYSIS (Bare Model Names) ==========" -ForegroundColor Cyan
$duplicates = $results | Group-Object Model | Where-Object { $_.Count -gt 1 }
foreach ($group in $duplicates) {
    Write-Host "`nModel: $($group.Name)" -ForegroundColor Yellow
    $sorted = $group.Group | Sort-Object AvgLatencyMs
    $fastest = $sorted | Select-Object -First 1
    Write-Host "  FASTEST: $($fastest.QualifiedName) via $($fastest.Provider) - $($fastest.AvgLatencyMs)ms" -ForegroundColor Green
    foreach ($r in $sorted | Select-Object -Skip 1) {
        Write-Host "  SLOWER:  $($r.QualifiedName) via $($r.Provider) - $($r.AvgLatencyMs)ms" -ForegroundColor DarkYellow
    }
}

# Save report
$reportFile = "docs/testing/logs/duplicate-benchmark-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
$results | Sort-Object Model, AvgLatencyMs | ConvertTo-Json -Depth 3 | Out-File -FilePath $reportFile -Encoding utf8
Write-Host "`nReport saved to: $reportFile" -ForegroundColor Cyan