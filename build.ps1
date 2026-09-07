# Usage:
#   .\build.ps1
#   .\build.ps1 --bump=x
#   .\build.ps1 --bump=y --commit=auto
#   .\build.ps1 --bump=z --commit="Fix gear-bank deployment"

$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot

try {
    $commit = $null
    $cakeArgs = @()

    # Pull --commit out of the arguments.
    # Everything else is passed through to Cake.
    foreach ($arg in $args) {
        if ($arg.StartsWith("--commit=", [StringComparison]::OrdinalIgnoreCase)) {
            $commit = $arg.Substring("--commit=".Length)

            if ([string]::IsNullOrWhiteSpace($commit)) {
                throw "--commit requires either 'auto' or a commit message."
            }
        }
        else {
            $cakeArgs += $arg
        }
    }

    # Run the Cake build.
    dotnet run --project CakeBuild/CakeBuild.csproj -- @cakeArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }

    # No --commit means we're finished.
    if ($null -eq $commit) {
        exit 0
    }

    # If using AI, make sure Codex is available before staging anything.
    if ($commit -ieq "auto") {
        if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
            throw "Codex CLI was not found."
        }
    }

    # Stage every Git-visible change: additions, modifications, and deletions.
    git add -A

    if ($LASTEXITCODE -ne 0) {
        throw "git add failed."
    }

    # Determine whether there is actually anything staged.
    git diff --cached --quiet
    $diffExitCode = $LASTEXITCODE

    if ($diffExitCode -eq 0) {
        Write-Host "Nothing to commit."
        exit 0
    }

    if ($diffExitCode -ne 1) {
        throw "Unable to inspect staged changes."
    }

    # Generate the commit message with Codex if requested.
    if ($commit -ieq "auto") {
        $tempFile = [System.IO.Path]::GetTempFileName()

        try {
            $prompt = @"
Examine the staged Git changes in this repository using git diff --cached.

Write a concise, informative one-line Git commit message describing the changes.

Output exactly the commit message and nothing else.
Do not modify any files.
"@

            & codex -a never exec `
                --ephemeral `
                --sandbox read-only `
                --output-last-message $tempFile `
                $prompt

            if ($LASTEXITCODE -ne 0) {
                throw "Codex failed to generate a commit message."
            }

            $commit = (Get-Content $tempFile -Raw).Trim()

            if ([string]::IsNullOrWhiteSpace($commit)) {
                throw "Codex returned an empty commit message."
            }

            # Require a one-line message.
            if ($commit -match "[`r`n]") {
                throw "Codex returned more than one line for the commit message."
            }

            Write-Host "AI commit message: $commit"
        }
        finally {
            Remove-Item $tempFile -ErrorAction SilentlyContinue
        }
    }

    # Commit everything staged above.
    git commit -m $commit

    if ($LASTEXITCODE -ne 0) {
        throw "git commit failed."
    }
}
finally {
    Pop-Location
}

