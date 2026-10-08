<#
.SYNOPSIS
Creates missing Unity metadata files for the queued event bus package.

.DESCRIPTION
Derives stable asset GUIDs from package-relative paths and writes importer
settings appropriate to each asset. Existing metadata is preserved. DLL assets
require explicit importer metadata because analyzer configuration cannot be
inferred from the filename alone.

.PARAMETER PackagePath
The package directory whose files and folders require Unity metadata. Defaults
to Packages/com.eventbus.queued relative to the repository root.
#>
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '../Packages/com.eventbus.queued')
)

$ErrorActionPreference = 'Stop'

function Get-StableAssetGuid
{
    <#
    .SYNOPSIS
    Derives a deterministic Unity GUID from an asset's package-relative path.

    .PARAMETER RelativePath
    The asset path beginning with a forward slash relative to the package root.

    .PARAMETER HashAlgorithm
    The reusable SHA-256 instance used for this metadata update.

    .PARAMETER TextEncoding
    The UTF-8 encoder used to convert the stable asset identity into bytes.

    .OUTPUTS
    System.String. The first 32 lowercase hexadecimal characters of the hash.
    #>
    param(
        [string]$RelativePath,
        [System.Security.Cryptography.SHA256]$HashAlgorithm,
        [System.Text.Encoding]$TextEncoding
    )

    # Package identity plus normalized path keeps GUIDs stable across checkout locations.
    $digest = $HashAlgorithm.ComputeHash($TextEncoding.GetBytes('com.eventbus.queued' + $RelativePath))
    $hexadecimal = [System.BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant()

    return $hexadecimal.Substring(0, 32)
}

function Get-ImporterMetadata
{
    <#
    .SYNOPSIS
    Selects Unity importer metadata for one file-system entry.

    .PARAMETER Entry
    The file or directory returned by Get-ChildItem, including its PSIsContainer
    provider property.

    .OUTPUTS
    System.String. The importer YAML body with newline-terminated fields.
    #>
    param(
        [object]$Entry
    )

    if ($Entry.PSIsContainer)
    {
        return "folderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
    }

    if ($Entry.Extension -eq '.cs')
    {
        return "MonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
    }

    if ($Entry.Extension -eq '.asmdef')
    {
        return "AssemblyDefinitionImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
    }

    if ($Entry.Name -eq 'package.json')
    {
        return "PackageManifestImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
    }

    if ($Entry.Extension -eq '.dll')
    {
        throw "DLL importer settings must be supplied explicitly: $($Entry.FullName)"
    }

    return "DefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
}

$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sha = [System.Security.Cryptography.SHA256]::Create()

try
{
    foreach ($entry in Get-ChildItem -LiteralPath $packageRoot -Recurse -Force)
    {
        if ($entry.Name.EndsWith('.meta'))
        {
            continue
        }

        $metadataPath = $entry.FullName + '.meta'

        if (Test-Path -LiteralPath $metadataPath)
        {
            continue
        }

        $relative = $entry.FullName.Substring($packageRoot.Length).Replace('\', '/')
        $guid = Get-StableAssetGuid $relative $sha $utf8
        $header = "fileFormatVersion: 2`nguid: $guid`n"
        $body = Get-ImporterMetadata $entry

        [System.IO.File]::WriteAllText($metadataPath, $header + $body, $utf8)
    }
}
finally
{
    $sha.Dispose()
}
