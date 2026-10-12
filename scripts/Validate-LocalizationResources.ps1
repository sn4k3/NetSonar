param(
    [string] $LocalizationDirectory = (Join-Path $PSScriptRoot '..\src\NetSonar\Localization')
)

$ErrorActionPreference = 'Stop'
$errors = [System.Collections.Generic.List[string]]::new()
$basePath = Join-Path $LocalizationDirectory 'Strings.resx'
$placeholderPattern = '\{\d+(?:[^{}]*)\}'
# Brand names, technical abbreviations, and placeholder-only formats can be culture invariant.
$technicalSameValueKeys = @(
    'NetworkScanner.Engine.Nmap',
    'NetworkScanner.LastScan',
    'NetworkScanner.Progress',
    'NetworkScanner.ProgressEta',
    'SpeedTest.Grid.Mac',
    'SpeedTest.Grid.Vpn',
    'Status.HttpFallback',
    'Unit.MillisecondsShort',
    'Unit.MinutesShort',
    'Unit.SecondsShort',
    'Ui.AvaloniaUI',
    'Ui.IP',
    'Ui.IQM',
    'Ui.TimeToLive'
)
# These localized terms legitimately share their English spelling.
$intentionalSameValues = @(
    'de|Ui.ScannerHost',
    'de|Ui.ScannerVersion',
    'es|Common.No',
    'es|Status.ErrorFallback',
    'es|Ui.ScannerHost',
    'fr|Ui.ServicesCountPlain',
    'fr|Ui.ScannerService',
    'fr|Ui.ScannerVersion',
    'fr|Ui.Type',
    'it|Common.No',
    'it|Ui.ScannerHost',
    'nl-NL|Ui.ScannerHost',
    'nl-NL|Ui.ScannerProtocol',
    'nl-NL|Ui.ScannerService',
    'pl|Ui.ScannerHost',
    'pt-BR|Ui.ScannerHost',
    'zh-Hans|Ui.Ping'
)

function Read-ResourceCatalog([string] $Path) {
    try {
        [xml] $document = Get-Content -Raw -LiteralPath $Path
    }
    catch {
        $errors.Add("$Path is not valid XML: $($_.Exception.Message)")
        return @{}
    }

    $duplicates = @($document.root.data | Group-Object -Property name | Where-Object Count -gt 1)
    foreach ($duplicate in $duplicates) {
        $errors.Add("$Path contains duplicate key '$($duplicate.Name)'.")
    }

    $catalog = @{}
    foreach ($entry in $document.root.data) {
        $catalog[[string] $entry.name] = [string] $entry.value
    }

    return $catalog
}

function Get-Placeholders([string] $Value) {
    return @([regex]::Matches($Value, $placeholderPattern) | ForEach-Object Value | Sort-Object)
}

$baseCatalog = Read-ResourceCatalog $basePath
if ($baseCatalog.Count -eq 0) {
    throw "The neutral resource catalog is empty or invalid: $basePath"
}

foreach ($key in $baseCatalog.Keys) {
    if ([string]::IsNullOrWhiteSpace($baseCatalog[$key])) {
        $errors.Add("Strings.resx has a blank value for '$key'.")
    }
}

$satelliteFiles = Get-ChildItem -LiteralPath $LocalizationDirectory -Filter 'Strings.*.resx' | Sort-Object Name
foreach ($file in $satelliteFiles) {
    $culture = $file.BaseName.Substring('Strings.'.Length)
    $catalog = Read-ResourceCatalog $file.FullName

    foreach ($key in $baseCatalog.Keys) {
        if (-not $catalog.ContainsKey($key)) {
            $errors.Add("$($file.Name) is missing '$key'.")
            continue
        }

        $value = $catalog[$key]
        if ([string]::IsNullOrWhiteSpace($value)) {
            $errors.Add("$($file.Name) has a blank value for '$key'.")
            continue
        }

        $expectedPlaceholders = Get-Placeholders $baseCatalog[$key]
        $actualPlaceholders = Get-Placeholders $value
        if (($expectedPlaceholders -join '|') -cne ($actualPlaceholders -join '|')) {
            $errors.Add("$($file.Name) has different placeholders for '$key'.")
        }

        $sameValueAllowed = $technicalSameValueKeys -contains $key -or
            $intentionalSameValues -contains "$culture|$key"
        if (-not $sameValueAllowed -and $value -ceq $baseCatalog[$key]) {
            $errors.Add("$($file.Name) keeps the English value for '$key'.")
        }
    }

    foreach ($key in $catalog.Keys) {
        if (-not $baseCatalog.ContainsKey($key)) {
            $errors.Add("$($file.Name) has extra key '$key'.")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { [Console]::Error.WriteLine($_) }
    exit 1
}

Write-Host "Validated $($satelliteFiles.Count) translations with $($baseCatalog.Count) resources each."
