# Release-only helper. Nothing in this directory is shipped in the app.
$script:GhRetryDelays = if ($env:GH_RETRY_DELAYS) {
    @($env:GH_RETRY_DELAYS -split ',' | ForEach-Object { [int]$_ })
} else { @(5, 15, 30, 60, 120) }
$script:GhLastFailure = ''

function Test-GhTransientFailure {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match '(?i)(HTTP|status code:?)\s*(429|500|502|503|504)\b|^\s*(429|500|502|503|504)\b|service unavailable|bad gateway|gateway time-?out|internal server error|no server is currently available|couldn.t respond to your request in time|secondary rate limit|rate limit exceeded|abuse detection|connection (reset|refused|closed)|unexpected EOF|i/o timeout|tls handshake timeout|temporary failure in name resolution'
}

function Test-GhNotFoundFailure {
    param([string]$Text)
    return $Text -match '(?i)\bHTTP\s+404\b|\brelease not found\b'
}

function Get-GhFailureText { return $script:GhLastFailure }

function Invoke-Gh {
    $arguments = $args
    $stderrFile = [IO.Path]::GetTempFileName()
    $callerPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        for ($attempt = 0; ; $attempt++) {
            $output = & gh @arguments 2> $stderrFile
            $code = $LASTEXITCODE
            $problem = [string](Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue)
            $script:GhLastFailure = $problem
            if ($problem) { Write-Host $problem.TrimEnd() }
            if ($code -eq 0 -or $attempt -ge $script:GhRetryDelays.Count -or
                -not (Test-GhTransientFailure $problem)) {
                $global:LASTEXITCODE = $code
                return $output
            }
            $delay = $script:GhRetryDelays[$attempt]
            Write-Host "GitHub request failed temporarily; retrying in $delay seconds."
            Start-Sleep -Seconds $delay
        }
    }
    finally {
        $ErrorActionPreference = $callerPreference
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
}
