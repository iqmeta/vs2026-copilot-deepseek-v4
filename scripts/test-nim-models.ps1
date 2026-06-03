$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$outDir = Join-Path $PSScriptRoot '..\docs\testing'
$outDir = [System.IO.Path]::GetFullPath($outDir)
if (!(Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

$outFile = Join-Path $outDir 'nvidia-nim-models-config.json'
$tags = Invoke-RestMethod -Uri 'http://localhost:11434/api/tags' -Method Get
$models = $tags.models
$results = @()
$idx = 0

foreach ($m in $models) {
    $idx++
    $model = $m.model
    Write-Output ("[{0}/{1}] Testing {2}" -f $idx, $models.Count, $model)
    $ctx = [int]$m.context_length
    $maxOut = [int]$m.max_output_tokens
    $supportsTools = [bool]$m.supports_tools
    $supportsVision = [bool]$m.supports_vision

    $optMax = if ($maxOut -gt 0) { [Math]::Min($maxOut, 4096) } else { 1024 }
    $temperature = if ($model -match 'coder|code|granite|starcoder|codestral') {
        0.15
    }
    elseif ($model -match 'reason|nemotron-3-super|deepseek-v4-pro') {
        0.2
    }
    else {
        0.3
    }

    $topP = 0.9
    $testMax = [Math]::Min($optMax, 24)

    $body = @{
        model = $model
        messages = @(
            @{
                role = 'user'
                content = 'Reply exactly OK'
            }
        )
        stream = $false
        temperature = $temperature
        top_p = $topP
        max_tokens = $testMax
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $false
    $status = 'ok'
    $respText = ''
    $err = ''

    try {
        $resp = Invoke-RestMethod -Uri 'http://localhost:11434/v1/chat/completions' -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -TimeoutSec 15
        $respText = $resp.choices[0].message.content
        if ([string]::IsNullOrWhiteSpace($respText)) {
            $status = 'empty_response'
        }
        else {
            $ok = $true
        }
    }
    catch {
        $status = 'error'
        $err = $_.Exception.Message
    }

    $sw.Stop()

    $results += [pscustomobject]@{
        model = $model
        provider = 'nvidia_nim'
        tested_at_utc = (Get-Date).ToUniversalTime().ToString('o')
        test = @{
            success = $ok
            status = $status
            latency_ms = [int]$sw.Elapsed.TotalMilliseconds
            sample_response = ($respText -replace '\s+', ' ')
            error = $err
        }
        capabilities = @{
            supports_tools = $supportsTools
            supports_vision = $supportsVision
            context_length = $ctx
            max_output_tokens = $maxOut
        }
        optimal_parameters = @{
            temperature = $temperature
            top_p = $topP
            max_tokens = $optMax
            stream = $true
            tool_choice = if ($supportsTools) { 'auto' } else { 'none' }
            response_format = 'text'
        }
    }
}

$summary = [pscustomobject]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    total_models = $results.Count
    successful = ($results | Where-Object { $_.test.success }).Count
    failed = ($results | Where-Object { -not $_.test.success }).Count
    source = 'http://localhost:11434/api/tags'
    notes = @(
        'Configuración óptima heurística por familia de modelo',
        'Capacidades y límites base tomados de /api/tags del proxy'
    )
}

$final = [pscustomobject]@{
    summary = $summary
    models = $results
}

$final | ConvertTo-Json -Depth 12 | Set-Content -Path $outFile -Encoding UTF8

Write-Output "JSON generado: $outFile"
$summary | ConvertTo-Json -Depth 5
Write-Output 'Fallidos (primeros 15):'
$results |
    Where-Object { -not $_.test.success } |
    Select-Object -First 15 |
    ForEach-Object {
        Write-Output (" - " + $_.model + " => " + $_.test.status + " | " + $_.test.error)
    }
