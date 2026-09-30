Set-StrictMode -Version Latest

function Get-MigrationProperty {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-MigrationExactProperties {
    param($Object, [string[]]$Expected, [string]$Label)

    if ($null -eq $Object) {
        throw "$Label is missing."
    }
    if (@(Compare-Object ($Expected | Sort-Object) (@($Object.PSObject.Properties.Name) | Sort-Object)).Count -ne 0) {
        throw "$Label fields do not exactly match the reviewed contract."
    }
}

function Test-MigrationSha256 {
    param([AllowNull()][string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[a-f0-9]{64}$'
}

function Get-MigrationSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-MigrationLocalTestSeams {
    param(
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider,
        [scriptblock]$LocalSqlExecutor,
        [object]$LocalPlatformFacts
    )

    $localStageLeaseStatePath =
        [Environment]::GetEnvironmentVariable('T21_MIGRATION_STAGE_LEASE_STATE_PATH')
    $hasSeam = $null -ne $LocalAccessTokenProvider -or
        $null -ne $LocalSqlExecutor -or
        $null -ne $LocalPlatformFacts -or
        -not [string]::IsNullOrWhiteSpace($localStageLeaseStatePath)
    if (($EnableLocalTestSeams -or $hasSeam) -and
        [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -ceq 'true') {
        throw 'T21_SQL_RUNTIME_INVALID'
    }
    if ($hasSeam -and -not $EnableLocalTestSeams) {
        throw 'T21_SQL_RUNTIME_INVALID'
    }
}

function Get-MigrationSqlRuntimeDescriptor {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-f0-9]{64}$')]
        [string]$ExpectedApplicationSha256,
        [switch]$RequireReadOnly
    )

    try {
        $resolvedRoot = (Resolve-Path -LiteralPath $BundleRoot).Path
        $manifestPath = Join-Path $resolvedRoot 'bundle-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw 'invalid'
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw |
            ConvertFrom-Json -DateKind String
        if ([string]$manifest.schemaVersion -cne '1.1.0' -or
            $null -eq $manifest.PSObject.Properties['sqlRuntime']) {
            throw 'invalid'
        }
        $runtime = $manifest.sqlRuntime
        Assert-MigrationExactProperties -Object $runtime -Label 'SQL runtime' -Expected @(
            'schemaVersion',
            'sourceApplicationPath',
            'sourceApplicationSha256',
            'dependencyManifestSourcePath',
            'packageId',
            'packageVersion',
            'target',
            'runtime',
            'entryAssembly',
            'assemblies'
        )
        if ([string]$runtime.schemaVersion -cne '1.0.0' -or
            [string]$runtime.sourceApplicationPath -cne 'app/Husaynia.Web.zip' -or
            [string]$runtime.sourceApplicationSha256 -cne $ExpectedApplicationSha256 -or
            [string]$runtime.dependencyManifestSourcePath -cne 'Husaynia.Web.deps.json' -or
            [string]$runtime.packageId -cne 'Microsoft.Data.SqlClient' -or
            [string]$runtime.packageVersion -cne '6.1.1' -or
            [string]$runtime.target -cne '.NETCoreApp,Version=v10.0' -or
            [string]$runtime.runtime -cne 'unix' -or
            [string]$runtime.entryAssembly -cne
                'runtime/sqlclient/Microsoft.Data.SqlClient.dll') {
            throw 'invalid'
        }
        $expectedNames = @(
            'Azure.Core',
            'Azure.Identity',
            'Microsoft.Bcl.AsyncInterfaces',
            'Microsoft.Bcl.Cryptography',
            'Microsoft.Data.SqlClient',
            'Microsoft.Identity.Client',
            'Microsoft.Identity.Client.Extensions.Msal',
            'Microsoft.IdentityModel.Abstractions',
            'Microsoft.IdentityModel.JsonWebTokens',
            'Microsoft.IdentityModel.Logging',
            'Microsoft.IdentityModel.Protocols',
            'Microsoft.IdentityModel.Protocols.OpenIdConnect',
            'Microsoft.IdentityModel.Tokens',
            'Microsoft.SqlServer.Server',
            'System.ClientModel',
            'System.Configuration.ConfigurationManager',
            'System.IdentityModel.Tokens.Jwt',
            'System.Memory.Data',
            'System.Security.Cryptography.ProtectedData'
        )
        $pathsByName = [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::Ordinal)
        $identitiesByName = [Collections.Generic.Dictionary[string, string]]::new(
            [StringComparer]::Ordinal)
        $orderedPaths = [Collections.Generic.List[string]]::new()
        foreach ($assembly in @($runtime.assemblies)) {
            Assert-MigrationExactProperties -Object $assembly -Label 'SQL runtime assembly' -Expected @(
                'assemblyVersion',
                'dependencyAssetPath',
                'fileVersion',
                'managedIdentity',
                'packageKey',
                'runtimeIdentifier',
                'sourcePath',
                'bundlePath',
                'simpleName',
                'sha256',
                'length'
            )
            $simpleName = [string]$assembly.simpleName
            $sourcePath = [string]$assembly.sourcePath
            $dependencyAssetPath = [string]$assembly.dependencyAssetPath
            $bundlePath = [string]$assembly.bundlePath
            $packageKey = [string]$assembly.packageKey
            $runtimeIdentifier = [string]$assembly.runtimeIdentifier
            $sourceSegments = @($sourcePath.Split('/'))
            $dependencySegments = @($dependencyAssetPath.Split('/'))
            $sourceFileName = [IO.Path]::GetFileName($sourcePath)
            $dependencyFileName = [IO.Path]::GetFileName($dependencyAssetPath)
            $expectedSqlClientAsset =
                'runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll'
            if ($simpleName -notmatch '^[A-Za-z0-9.]+$' -or
                [string]::IsNullOrWhiteSpace($sourcePath) -or
                [string]::IsNullOrWhiteSpace($dependencyAssetPath) -or
                $sourcePath.Contains('\') -or
                $dependencyAssetPath.Contains('\') -or
                $sourcePath.Contains(':') -or
                $dependencyAssetPath.Contains(':') -or
                $sourcePath.StartsWith('/') -or
                $dependencyAssetPath.StartsWith('/') -or
                @($sourceSegments + $dependencySegments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or
                    $_ -in @('.', '..') -or
                    $_.EndsWith('.') -or
                    $_.EndsWith(' ')
                }).Count -ne 0 -or
                $sourceFileName -cne "$simpleName.dll" -or
                $dependencyFileName -cne $sourceFileName -or
                $packageKey -notmatch "^$([regex]::Escape($simpleName))/[0-9]+(?:\.[0-9]+){2}(?:[-+][0-9A-Za-z.-]+)?$" -or
                [string]$assembly.assemblyVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
                [string]$assembly.fileVersion -notmatch '^[0-9]+(?:\.[0-9]+){3}$' -or
                [string]::IsNullOrWhiteSpace([string]$assembly.managedIdentity) -or
                ($simpleName -ceq 'Microsoft.Data.SqlClient' -and (
                    $packageKey -cne 'Microsoft.Data.SqlClient/6.1.1' -or
                    $dependencyAssetPath -cne $expectedSqlClientAsset -or
                    $sourcePath -cne $expectedSqlClientAsset -or
                    $runtimeIdentifier -cne 'unix'
                )) -or
                ($simpleName -cne 'Microsoft.Data.SqlClient' -and (
                    -not [string]::IsNullOrEmpty($runtimeIdentifier) -or
                    $sourcePath -cne $sourceFileName -or
                    $dependencyAssetPath -notmatch "^lib/[A-Za-z0-9.]+/$([regex]::Escape($sourceFileName))$"
                )) -or
                $bundlePath -cne "runtime/sqlclient/$simpleName.dll" -or
                [string]$assembly.sha256 -notmatch '^[a-f0-9]{64}$' -or
                -not $pathsByName.TryAdd($simpleName, (Join-Path $resolvedRoot $bundlePath)) -or
                -not $identitiesByName.TryAdd(
                    $simpleName,
                    [string]$assembly.managedIdentity)) {
                throw 'invalid'
            }
            $path = $pathsByName[$simpleName]
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                -not (Test-MigrationPathWithinDirectory -BasePath $resolvedRoot -Path $path) -or
                (Get-Item -LiteralPath $path).Length -ne [long]$assembly.length -or
                (Get-MigrationSha256 -Path $path) -cne [string]$assembly.sha256 -or
                ($RequireReadOnly -and -not (Get-Item -LiteralPath $path -Force).IsReadOnly)) {
                throw 'invalid'
            }
            $identity = [Reflection.AssemblyName]::GetAssemblyName($path)
            $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
            if ([string]$identity.Name -cne $simpleName -or
                [string]$identity.Version -cne [string]$assembly.assemblyVersion -or
                [string]$fileVersion -cne [string]$assembly.fileVersion -or
                [string]$identity.FullName -cne [string]$assembly.managedIdentity) {
                throw 'invalid'
            }
            $orderedPaths.Add($bundlePath)
        }
        if (@(Compare-Object $expectedNames @($pathsByName.Keys | Sort-Object)).Count -ne 0 -or
            (@($orderedPaths) -join "`n") -cne (@($orderedPaths | Sort-Object) -join "`n") -or
            -not $pathsByName.ContainsKey('Microsoft.Data.SqlClient')) {
            throw 'invalid'
        }
        return [pscustomobject]@{
            BundleRoot = $resolvedRoot
            Manifest = $manifest
            Runtime = $runtime
            AssemblyPaths = $pathsByName
            ManagedIdentities = $identitiesByName
            EntryAssemblyPath = $pathsByName['Microsoft.Data.SqlClient']
        }
    }
    catch {
        throw 'T21_SQL_RUNTIME_INVALID'
    }
}

function Initialize-MigrationSqlRuntimeLoadContextType {
    if ($null -ne ('Husaynia.T21.RestrictedAssemblyLoadContext' -as [type])) {
        return
    }
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Husaynia.T21
{
    public sealed class RestrictedAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, string> paths;
        private readonly Dictionary<string, string> managedIdentities;
        private readonly HashSet<string> platformAssemblies;

        public RestrictedAssemblyLoadContext(
            string name,
            IDictionary<string, string> paths,
            IDictionary<string, string> managedIdentities,
            IEnumerable<string> platformAssemblies)
            : base(name, true)
        {
            this.paths = new Dictionary<string, string>(paths, StringComparer.Ordinal);
            this.managedIdentities = new Dictionary<string, string>(
                managedIdentities,
                StringComparer.Ordinal);
            this.platformAssemblies = new HashSet<string>(
                platformAssemblies,
                StringComparer.Ordinal);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            string path;
            if (assemblyName.Name != null && paths.TryGetValue(assemblyName.Name, out path))
            {
                string expectedIdentity;
                if (!managedIdentities.TryGetValue(assemblyName.Name, out expectedIdentity) ||
                    !String.Equals(assemblyName.FullName, expectedIdentity, StringComparison.Ordinal))
                {
                    throw new FileLoadException("T21_SQL_RUNTIME_INVALID");
                }
                Assembly loaded = LoadFromAssemblyPath(path);
                if (!String.Equals(
                    loaded.GetName().FullName,
                    expectedIdentity,
                    StringComparison.Ordinal))
                {
                    throw new FileLoadException("T21_SQL_RUNTIME_INVALID");
                }
                return loaded;
            }
            if (assemblyName.Name != null && platformAssemblies.Contains(assemblyName.Name))
            {
                return null;
            }
            throw new FileLoadException("T21_SQL_RUNTIME_INVALID");
        }
    }
}
'@
}

function New-MigrationSqlRuntimeLoadContext {
    param([Parameter(Mandatory = $true)]$Descriptor)

    $context = $null
    try {
        Initialize-MigrationSqlRuntimeLoadContextType
        $platformAssemblyNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        $trustedPlatformAssemblies = [string][AppContext]::GetData(
            'TRUSTED_PLATFORM_ASSEMBLIES')
        foreach ($path in @($trustedPlatformAssemblies.Split(
                    [IO.Path]::PathSeparator,
                    [StringSplitOptions]::RemoveEmptyEntries))) {
            [void]$platformAssemblyNames.Add([IO.Path]::GetFileNameWithoutExtension($path))
        }
        $context = [Husaynia.T21.RestrictedAssemblyLoadContext]::new(
            "husaynia-t21-sql-$([guid]::NewGuid().ToString('N'))",
            $Descriptor.AssemblyPaths,
            $Descriptor.ManagedIdentities,
            $platformAssemblyNames)
        $entryAssembly = $context.LoadFromAssemblyPath([string]$Descriptor.EntryAssemblyPath)
        if ([string]$entryAssembly.GetName().Name -cne 'Microsoft.Data.SqlClient' -or
            $null -eq $entryAssembly.GetType(
                'Microsoft.Data.SqlClient.SqlConnection',
                $false,
                $false)) {
            throw 'invalid'
        }
        return [pscustomobject]@{
            Context = $context
            EntryAssembly = $entryAssembly
        }
    }
    catch {
        if ($null -ne $context) {
            try { $context.Unload() } catch {}
        }
        throw 'T21_SQL_RUNTIME_INVALID'
    }
}

function Assert-MigrationSqlRuntimeCompatibility {
    param(
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-f0-9]{64}$')]
        [string]$ExpectedApplicationSha256,
        [switch]$EnableLocalTestSeams,
        [object]$LocalPlatformFacts
    )

    Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalPlatformFacts $LocalPlatformFacts
    $facts = if ($null -ne $LocalPlatformFacts) {
        $LocalPlatformFacts
    }
    else {
        [pscustomobject]@{
            isLinux = [bool]$IsLinux
            architecture = [string][Runtime.InteropServices.RuntimeInformation]::OSArchitecture
            powerShellVersion = [string]$PSVersionTable.PSVersion
            dotNetVersion = [string][Environment]::Version
        }
    }
    try {
        Assert-MigrationExactProperties -Object $facts -Label 'SQL runtime platform facts' -Expected @(
            'isLinux',
            'architecture',
            'powerShellVersion',
            'dotNetVersion'
        )
        $powerShellVersion = [version][string]$facts.powerShellVersion
        $dotNetVersion = [version][string]$facts.dotNetVersion
        if ([bool]$facts.isLinux -ne $true -or
            [string]$facts.architecture -cne 'X64' -or
            $powerShellVersion -lt [version]'7.6.0' -or
            $dotNetVersion.Major -ne 10) {
            throw 'invalid'
        }
        $descriptor = Get-MigrationSqlRuntimeDescriptor `
            -BundleRoot $BundleRoot `
            -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
            -RequireReadOnly
        $loaded = New-MigrationSqlRuntimeLoadContext -Descriptor $descriptor
        try {
            return $descriptor
        }
        finally {
            $loaded.EntryAssembly = $null
            $loaded.Context.Unload()
            $loaded = $null
        }
    }
    catch {
        throw 'T21_SQL_RUNTIME_INVALID'
    }
}

function Get-MigrationAzureSqlAccessToken {
    param(
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider
    )

    Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider
    $arguments = @(
        'account',
        'get-access-token',
        '--resource',
        'https://database.windows.net/',
        '--query',
        'accessToken',
        '--output',
        'tsv',
        '--only-show-errors'
    )
    $tokenText = $null
    $rawTokenText = $null
    $stdout = $null
    $stderr = $null
    $providerOutput = $null
    $executionResult = $null
    try {
        if ($null -ne $LocalAccessTokenProvider) {
            $providerOutput = @(& $LocalAccessTokenProvider ([pscustomobject]@{
                filePath = 'az'
                arguments = @($arguments)
                timeoutSeconds = 30
            }))
            if ($providerOutput.Count -ne 1 -or $null -eq $providerOutput[0]) {
                throw 'invalid'
            }
            $executionResult = $providerOutput[0]
            Assert-MigrationExactProperties -Object $executionResult `
                -Label 'Azure CLI token process result' `
                -Expected @('exitCode', 'timedOut', 'stdout', 'stderr')
        }
        else {
            $az = Get-Command 'az' -CommandType Application -ErrorAction Stop
            $executionResult = Invoke-MigrationBoundedProcess `
                -FilePath $az.Source `
                -Arguments $arguments `
                -TimeoutMilliseconds 30000
        }
        if ($executionResult.timedOut -isnot [bool] -or
            $executionResult.exitCode -is [bool] -or
            $executionResult.exitCode -isnot [sbyte] -and
            $executionResult.exitCode -isnot [byte] -and
            $executionResult.exitCode -isnot [int16] -and
            $executionResult.exitCode -isnot [uint16] -and
            $executionResult.exitCode -isnot [int32] -and
            $executionResult.exitCode -isnot [uint32] -and
            $executionResult.exitCode -isnot [int64] -or
            $null -eq $executionResult.stdout -or
            $null -eq $executionResult.stderr -or
            [bool]$executionResult.timedOut -or
            [int64]$executionResult.exitCode -ne 0) {
            throw 'invalid'
        }
        $tokenText = [string]$executionResult.stdout
        $rawTokenText = [string]$tokenText
        if ($rawTokenText -notmatch '^[^\r\n]+(?:\r?\n)?$') {
            throw 'invalid'
        }
        $token = $rawTokenText.TrimEnd("`r", "`n")
        if ($token.Length -lt 16 -or $token.Length -gt 32768 -or
            $token -notmatch '^[^\s\r\n]+$') {
            throw 'invalid'
        }
        return $token
    }
    catch {
        throw 'T21_AZURE_SQL_TOKEN_UNAVAILABLE'
    }
    finally {
        $tokenText = $null
        $rawTokenText = $null
        $stdout = $null
        $stderr = $null
        $providerOutput = $null
        $executionResult = $null
    }
}

function Invoke-MigrationBoundedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 300000)]
        [int]$TimeoutMilliseconds
    )

    $process = $null
    $stdoutTask = $null
    $stderrTask = $null
    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $FilePath
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'invalid'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
            }
            catch {
                throw 'invalid'
            }
            if (-not $process.WaitForExit(5000)) {
                throw 'invalid'
            }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            exitCode = [int]$process.ExitCode
            timedOut = [bool]$timedOut
            stdout = [string]$stdout
            stderr = [string]$stderr
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
        $process = $null
        $stdoutTask = $null
        $stderrTask = $null
    }
}

function ConvertTo-MigrationSqlResultRows {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][int]$ExpectedColumnCount
    )

    $resultSetCount = 1
    $rows = $Value
    if ($null -ne $Value -and
        $null -ne $Value.PSObject.Properties['resultSetCount']) {
        Assert-MigrationExactProperties -Object $Value -Label 'SQL execution result' -Expected @(
            'resultSetCount',
            'rows'
        )
        $resultSetCount = [int]$Value.resultSetCount
        $rows = $Value.rows
    }
    $rowList = @($rows)
    if ($resultSetCount -ne 1 -or $rowList.Count -ne 1) {
        throw 'T21_SQL_RESULT_INVALID'
    }
    $row = @($rowList[0])
    if ($row.Count -ne $ExpectedColumnCount) {
        throw 'T21_SQL_RESULT_INVALID'
    }
    $matrix = [object[][]]::new(1)
    $matrix[0] = [object[]]$row
    return ,$matrix
}

function Assert-MigrationPreflightSqlRow {
    param(
        [Parameter(Mandatory = $true)]$Row,
        [Parameter(Mandatory = $true)]$TargetMetadata
    )

    $values = @($Row | ForEach-Object {
        if ($null -eq $_) { '' } else { ([string]$_).Trim() }
    })
    if ($values.Count -ne 11) {
        throw 'T21_SQL_RESULT_INVALID'
    }
    $observedDatabaseName = $values[0]
    $observedServerName = $values[1]
    $observedProductVersion = $values[2]
    $observedDatabaseStatus = $values[3]
    $selectPermission = $values[4]
    $mutationPermissions = @($values[5..10])
    if ($observedDatabaseName -cne [string]$TargetMetadata.sqlDatabaseName -or
        $observedServerName -notin @(
            [string]$TargetMetadata.sqlServerName,
            [string]$TargetMetadata.sqlServerFqdn
        ) -or
        [string]::IsNullOrWhiteSpace($observedProductVersion) -or
        $observedDatabaseStatus -cne 'ONLINE' -or
        $selectPermission -cne '1' -or
        @($mutationPermissions | Where-Object { $_ -cne '0' }).Count -ne 0) {
        throw 'T21_SQL_RESULT_INVALID'
    }
    return [pscustomobject]@{
        DatabaseName = $observedDatabaseName
        ServerName = $observedServerName
        ProductVersion = $observedProductVersion
        DatabaseStatus = $observedDatabaseStatus
    }
}

function ConvertFrom-MigrationStageLeaseSqlRow {
    param(
        [Parameter(Mandatory = $true)]$Row,
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Resource,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint,
        [Parameter(Mandatory = $true)][string]$AuthorizationEvidenceSha256,
        [Parameter(Mandatory = $true)]$Holder
    )

    try {
        $values = @($Row | ForEach-Object {
            if ($null -eq $_) { '' } else { ([string]$_).Trim() }
        })
        $fenceToken = [int64]0
        $acquiredAt = [DateTimeOffset]::MinValue
        $expiresAt = [DateTimeOffset]::MinValue
        $releasedAt = [DateTimeOffset]::MinValue
        $activeMutationId = [guid]::Empty
        $activeMutationStartedAt = [DateTimeOffset]::MinValue
        if ($values.Count -ne 7 -or
            -not [int64]::TryParse(
                $values[0],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$fenceToken) -or
            $fenceToken -lt 1 -or
            -not [DateTimeOffset]::TryParse(
                $values[1],
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$acquiredAt) -or
            -not [DateTimeOffset]::TryParse(
                $values[2],
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$expiresAt) -or
            $values[4] -notin @('0', '1') -or
            ($values[4] -ceq '0' -and -not [string]::IsNullOrWhiteSpace($values[3])) -or
            ($values[4] -ceq '1' -and
                -not [DateTimeOffset]::TryParse(
                    $values[3],
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind,
                    [ref]$releasedAt)) -or
            ([string]::IsNullOrWhiteSpace($values[5]) -xor
                [string]::IsNullOrWhiteSpace($values[6])) -or
            (-not [string]::IsNullOrWhiteSpace($values[5]) -and
                (-not [guid]::TryParse($values[5], [ref]$activeMutationId) -or
                 -not [DateTimeOffset]::TryParse(
                    $values[6],
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind,
                    [ref]$activeMutationStartedAt)))) {
            throw 'invalid'
        }
        return [ordered]@{
            schemaVersion = '1.0.0'
            provider = [string]$LeasePolicy.provider
            resource = $Resource
            stage = $Stage
            databaseTargetFingerprint = $TargetFingerprint
            authorizationEvidenceSha256 = $AuthorizationEvidenceSha256
            holderRunId = [string]$Holder.holderRunId
            holderRunAttempt = [int]$Holder.holderRunAttempt
            fenceToken = $fenceToken
            acquiredAtUtc = $values[1]
            expiresAtUtc = $values[2]
            releasedAtUtc = $values[3]
            released = $values[4] -ceq '1'
            activeMutationId = $values[5]
            activeMutationStartedAtUtc = $values[6]
        }
    }
    catch {
        throw 'T21_SQL_RESULT_INVALID'
    }
}

function Invoke-MigrationSqlQuery {
    param(
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)][string]$Query,
        [Parameter(Mandatory = $true)][ValidateRange(1, 64)][int]$ExpectedColumnCount,
        [ValidateRange(1, 600)][int]$CommandTimeoutSeconds = 60,
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-f0-9]{64}$')]
        [string]$ExpectedApplicationSha256,
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider,
        [scriptblock]$LocalSqlExecutor
    )

    Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    if ($ServerName -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.database\.windows\.net$' -or
        $DatabaseName -notmatch '^husaynia-(dev|stg|prd)$' -or
        [string]::IsNullOrWhiteSpace($Query) -or
        $Query.Length -gt 1MB) {
        throw 'T21_SQL_EXECUTION_FAILED'
    }
    if (($null -eq $LocalAccessTokenProvider) -xor ($null -eq $LocalSqlExecutor)) {
        throw 'T21_SQL_RUNTIME_INVALID'
    }

    $descriptor = if ($null -ne $LocalSqlExecutor) {
        Get-MigrationSqlRuntimeDescriptor `
            -BundleRoot $BundleRoot `
            -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
            -RequireReadOnly
    }
    else {
        Assert-MigrationSqlRuntimeCompatibility `
            -BundleRoot $BundleRoot `
            -ExpectedApplicationSha256 $ExpectedApplicationSha256
    }
    $token = $null
    $loaded = $null
    $connection = $null
    $command = $null
    $reader = $null
    try {
        $token = Get-MigrationAzureSqlAccessToken `
            -EnableLocalTestSeams:$EnableLocalTestSeams `
            -LocalAccessTokenProvider $LocalAccessTokenProvider
        if ($null -ne $LocalSqlExecutor) {
            $queryHash = ([Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData(
                    [Text.UTF8Encoding]::new($false).GetBytes($Query)
                )
            )).ToLowerInvariant()
            try {
                $localResult = & $LocalSqlExecutor ([pscustomobject]@{
                    serverName = $ServerName
                    databaseName = $DatabaseName
                    dataSource = "tcp:$ServerName,1433"
                    encrypt = $true
                    trustServerCertificate = $false
                    pooling = $false
                    connectionTimeoutSeconds = 15
                    commandTimeoutSeconds = $CommandTimeoutSeconds
                    applicationName = 'Husaynia-T21-Migration'
                    expectedColumnCount = $ExpectedColumnCount
                    querySha256 = $queryHash
                })
            }
            catch {
                throw 'T21_SQL_EXECUTION_FAILED'
            }
            return ConvertTo-MigrationSqlResultRows `
                -Value $localResult `
                -ExpectedColumnCount $ExpectedColumnCount
        }

        $loaded = New-MigrationSqlRuntimeLoadContext -Descriptor $descriptor
        $connectionType = $loaded.EntryAssembly.GetType(
            'Microsoft.Data.SqlClient.SqlConnection',
            $true,
            $false)
        $connectionString = (
            "Server=tcp:$ServerName,1433;Initial Catalog=$DatabaseName;" +
            'Encrypt=True;TrustServerCertificate=False;Pooling=False;' +
            'Connect Timeout=15;Application Name=Husaynia-T21-Migration')
        $connection = [Activator]::CreateInstance($connectionType, @($connectionString))
        $connection.AccessToken = $token
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = $CommandTimeoutSeconds
        $reader = $command.ExecuteReader()
        if ($reader.FieldCount -ne $ExpectedColumnCount -or -not $reader.Read()) {
            throw 'T21_SQL_RESULT_INVALID'
        }
        $row = [object[]]::new($ExpectedColumnCount)
        for ($index = 0; $index -lt $ExpectedColumnCount; $index++) {
            $row[$index] = if ($reader.IsDBNull($index)) {
                $null
            }
            else {
                $reader.GetValue($index)
            }
        }
        if ($reader.Read() -or $reader.NextResult()) {
            throw 'T21_SQL_RESULT_INVALID'
        }
        return ConvertTo-MigrationSqlResultRows `
            -Value (,([object[]]$row)) `
            -ExpectedColumnCount $ExpectedColumnCount
    }
    catch {
        if ($_.Exception.Message -ceq 'T21_SQL_RESULT_INVALID') {
            throw
        }
        if ($_.Exception.Message -ceq 'T21_AZURE_SQL_TOKEN_UNAVAILABLE' -or
            $_.Exception.Message -ceq 'T21_SQL_RUNTIME_INVALID') {
            throw
        }
        throw 'T21_SQL_EXECUTION_FAILED'
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $command) { $command.Dispose() }
        if ($null -ne $connection) {
            try { $connection.AccessToken = '' } catch {}
            $connection.Dispose()
        }
        $token = $null
        $connectionString = $null
        $reader = $null
        $command = $null
        $connection = $null
        if ($null -ne $loaded) {
            $loaded.EntryAssembly = $null
            $loaded.Context.Unload()
            $loaded = $null
        }
    }
}

function Assert-MigrationVerifiedProvenanceProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "Migration verified provenance $Label does not exactly match the v2 contract."
    }
}

function Test-MigrationPathWithinDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $base = (Resolve-Path -LiteralPath $BasePath).Path.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidate = (Resolve-Path -LiteralPath $Path).Path
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    return $candidate.StartsWith(
        $base + [IO.Path]::DirectorySeparatorChar,
        $comparison)
}

function Assert-MigrationVerifiedArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$VerifiedProvenancePath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('release-c6', 'trusted-preflight', 'migration-authorization')]
        [string]$ExpectedRole,
        [Parameter(Mandatory = $true)][string]$Stage,
        [string]$ApplicationSha256,
        [string]$BundleSha256,
        [Parameter(Mandatory = $true)]$Policy,
        [ValidatePattern('^[a-f0-9]{64}$')]
        [string]$PreflightEvidenceSha256
    )

    if ([string]::IsNullOrWhiteSpace($VerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $VerifiedProvenancePath -PathType Leaf)) {
        throw "Migration verified provenance is missing: $ExpectedRole."
    }
    $provenancePath = (Resolve-Path -LiteralPath $VerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw "Migration requires resolver-created verified-provenance.json: $ExpectedRole."
    }
    $resolverRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $extractedRoot = Join-Path $resolverRoot 'artifact'
    if (-not (Test-Path -LiteralPath $extractedRoot -PathType Container)) {
        throw "Migration resolver-owned artifact root is missing: $ExpectedRole."
    }
    $extractedRoot = (Resolve-Path -LiteralPath $extractedRoot).Path
    if (-not (Test-MigrationPathWithinDirectory -BasePath $resolverRoot -Path $extractedRoot)) {
        throw "Migration resolver-owned artifact root escaped its verified provenance parent: $ExpectedRole."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationVerifiedProvenanceProperties -Value $provenance -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label 'document'
    Assert-MigrationVerifiedProvenanceProperties -Value $provenance.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label 'run'
    Assert-MigrationVerifiedProvenanceProperties -Value $provenance.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label 'artifact'

    $contract = $Policy.t21ProvenanceContract
    Assert-MigrationVerifiedProvenanceProperties -Value $contract -Expected @(
        'schemaVersion', 'repository', 'protectedRef', 'callerMatrix', 'producerRoles', 'attestation'
    ) -Label 'policy'
    $role = @($contract.producerRoles | Where-Object { [string]$_.role -ceq $ExpectedRole })
    $isReusableProducer = $ExpectedRole -eq 'trusted-preflight'
    $matchingCaller = if ($isReusableProducer) {
        @($contract.callerMatrix | Where-Object {
            [string]$_.stage -ceq $Stage -and
            [string]$_.workflowRef -ceq [string]$provenance.run.workflowRef
        })
    }
    else {
        @()
    }
    if ($role.Count -ne 1 -or [bool]$role[0].forbidden -or
        [string]$contract.schemaVersion -cne '2.1.0' -or
        [string]$contract.repository -cne 'syedmh/Dreamer' -or
        [string]$contract.protectedRef -cne 'refs/heads/main' -or
        [string]$provenance.schemaVersion -cne '2.0.0' -or
        [string]$provenance.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$provenance.expectedRole -cne $ExpectedRole -or
        [string]$provenance.run.repository -cne [string]$contract.repository -or
        [string]$provenance.run.workflowRef -cne
            "$([string]$contract.repository)/$([string]$provenance.run.workflowPath)@$([string]$contract.protectedRef)" -or
        ((-not $isReusableProducer) -and
            [string]$provenance.run.workflowPath -cne [string]$role[0].workflowPath) -or
        ($isReusableProducer -and $matchingCaller.Count -ne 1) -or
        [string]$provenance.run.runId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value $provenance.run.runAttempt -Minimum 1) -or
        [string]$provenance.run.ref -cne [string]$contract.protectedRef -or
        [string]$provenance.run.commitSha -notmatch '^[a-f0-9]{40}$' -or
        [string]$provenance.run.status -cne 'completed' -or
        [string]$provenance.run.conclusion -cne 'success' -or
        [string]$provenance.artifact.id -notmatch '^[1-9][0-9]*$' -or
        [string]$provenance.artifact.archiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        -not (Test-MigrationJsonInteger -Value $provenance.artifact.sizeBytes -Minimum 1) -or
        [string]$provenance.contentManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Migration verified provenance has invalid role, API run, or artifact bindings: $ExpectedRole."
    }

    if ($ExpectedRole -eq 'release-c6') {
        $releaseRoots = @(
            Get-ChildItem -LiteralPath $extractedRoot -Directory -Force |
                Where-Object { $_.Name -ceq [string]$provenance.artifact.name }
        )
        $releaseManifestPath = if ($releaseRoots.Count -eq 1) {
            Join-Path $releaseRoots[0].FullName 'release\release-manifest.json'
        }
        else {
            ''
        }
        if ($null -ne $provenance.attestation -or
            [string]$provenance.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
            $releaseRoots.Count -ne 1 -or
            @(Get-ChildItem -LiteralPath $extractedRoot -Force).Count -ne 1 -or
            -not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf) -or
            (Get-MigrationSha256 -Path $releaseManifestPath) -cne [string]$provenance.contentManifestSha256) {
            throw 'Migration release C6 verified provenance is not uniquely bound to its resolver-owned artifact root.'
        }
        return [pscustomobject]@{
            provenance = $provenance
            artifactRoot = $releaseRoots[0].FullName
        }
    }

    if (-not (Test-MigrationSha256 $BundleSha256)) {
        throw "Migration verified provenance requires the protected bundle SHA-256: $ExpectedRole."
    }
    if ($ApplicationSha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Migration verified provenance requires the release-bound application SHA-256: $ExpectedRole."
    }
    Assert-MigrationVerifiedProvenanceProperties -Value $provenance.attestation -Expected @(
        'predicateType', 'signerWorkflow', 'signerDigest', 'sourceRef', 'sourceCommitSha', 'status'
    ) -Label 'attestation'
    $expectedArtifactName = if ($ExpectedRole -eq 'trusted-preflight') {
        "raw-$($Stage.ToLowerInvariant())-preflight-$ApplicationSha256"
    }
    else {
        if ($PreflightEvidenceSha256 -notmatch '^[a-f0-9]{64}$') {
            throw 'Migration authorization verified provenance requires the exact preflight evidence SHA-256.'
        }
        "migration-apply-authorization-$Stage-$ApplicationSha256-$PreflightEvidenceSha256"
    }
    $manifestPath = Join-Path $extractedRoot 't21-producer-manifest.json'
    if ([string]$provenance.artifact.name -cne $expectedArtifactName -or
        -not [bool]$role[0].requiresAttestation -or
        [string]$provenance.attestation.predicateType -cne [string]$contract.attestation.predicateType -or
        [string]$provenance.attestation.signerWorkflow -cne
            "$([string]$contract.repository)/$([string]$role[0].workflowPath)" -or
        [string]$provenance.attestation.signerDigest -notmatch '^[a-f0-9]{40}$' -or
        [string]$provenance.attestation.signerDigest -ceq ('0' * 40) -or
        [string]$provenance.attestation.sourceRef -cne [string]$contract.protectedRef -or
        [string]$provenance.attestation.sourceCommitSha -cne [string]$provenance.run.commitSha -or
        [string]$provenance.attestation.status -cne 'verified' -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-MigrationSha256 -Path $manifestPath) -cne [string]$provenance.contentManifestSha256) {
        throw "Migration verified provenance is not bound to the canonical attested producer artifact: $ExpectedRole."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationVerifiedProvenanceProperties -Value $manifest -Expected @(
        'schemaVersion', 'kind', 'producerRole', 'stage', 'applicationSha256', 'bundleSha256', 'release',
        'producer', 'trustedExecution', 'files', 'createdAtUtc'
    ) -Label 'producer manifest'
    Assert-MigrationVerifiedProvenanceProperties -Value $manifest.producer -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'commitSha', 'ref'
    ) -Label 'producer manifest run'
    Assert-MigrationVerifiedProvenanceProperties -Value $manifest.trustedExecution -Expected @(
        'topLevelCallerWorkflowRef', 'producerWorkflowRef', 'bundlePath', 'bundleSha256'
    ) -Label 'producer manifest trusted execution'
    $producerWorkflowRef = "$([string]$provenance.attestation.signerWorkflow)@$([string]$provenance.attestation.signerDigest)"
    if ([string]$manifest.schemaVersion -cne '2.0.0' -or
        [string]$manifest.kind -cne 't21-producer-manifest' -or
        [string]$manifest.producerRole -cne $ExpectedRole -or
        [string]$manifest.stage -cne $Stage -or
        [string]$manifest.applicationSha256 -cne $ApplicationSha256 -or
        [string]$manifest.bundleSha256 -cne $BundleSha256 -or
        [string]$manifest.producer.repository -cne [string]$provenance.run.repository -or
        [string]$manifest.producer.workflowPath -cne [string]$provenance.run.workflowPath -or
        [string]$manifest.producer.workflowRef -cne [string]$provenance.run.workflowRef -or
        [string]$manifest.producer.runId -cne [string]$provenance.run.runId -or
        [int]$manifest.producer.runAttempt -ne [int]$provenance.run.runAttempt -or
        [string]$manifest.producer.commitSha -cne [string]$provenance.run.commitSha -or
        [string]$manifest.producer.ref -cne [string]$provenance.run.ref -or
        [string]$manifest.trustedExecution.topLevelCallerWorkflowRef -cne
            [string]$provenance.run.workflowRef -or
        [string]$manifest.trustedExecution.producerWorkflowRef -cne $producerWorkflowRef -or
        [string]$manifest.trustedExecution.bundlePath -cne 'operations/protected-execution-bundle.zip' -or
        [string]$manifest.trustedExecution.bundleSha256 -cne $BundleSha256) {
        throw "Migration verified producer manifest is not exactly bound to verified provenance: $ExpectedRole."
    }
    return [pscustomobject]@{
        provenance = $provenance
        artifactRoot = $extractedRoot
        producerRunBinding = [ordered]@{
            schemaVersion = '2.0.0'
            topLevelCallerWorkflowRef = [string]$manifest.trustedExecution.topLevelCallerWorkflowRef
            producerWorkflowRef = [string]$manifest.trustedExecution.producerWorkflowRef
            runId = [string]$manifest.producer.runId
            runAttempt = [int]$manifest.producer.runAttempt
            commitSha = [string]$manifest.producer.commitSha
        }
    }
}

function Assert-MigrationProducerRunBinding {
    param(
        [Parameter(Mandatory = $true)]$Binding,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)]$Policy,
        [string]$ExpectedTopLevelCallerWorkflowRef,
        [string]$ExpectedProducerWorkflowRef
    )

    Assert-MigrationVerifiedProvenanceProperties -Value $Binding -Expected @(
        'schemaVersion', 'topLevelCallerWorkflowRef', 'producerWorkflowRef', 'runId', 'runAttempt', 'commitSha'
    ) -Label 'producer run binding'
    $contract = $Policy.t21ProvenanceContract
    $trustedPreflightRole = @($contract.producerRoles | Where-Object {
        [string]$_.role -ceq 'trusted-preflight'
    })
    $callers = @($contract.callerMatrix | Where-Object {
        [string]$_.stage -ceq $Stage -and
        [string]$_.workflowRef -ceq [string]$Binding.topLevelCallerWorkflowRef
    })
    if ($trustedPreflightRole.Count -ne 1 -or
        $callers.Count -ne 1 -or
        [string]$Binding.schemaVersion -cne '2.0.0' -or
        [string]$Binding.topLevelCallerWorkflowRef -notmatch
            '^syedmh/Dreamer/\.github/workflows/[A-Za-z0-9_.-]+\.yml@refs/heads/main$' -or
        [string]$Binding.producerWorkflowRef -notmatch
            '^syedmh/Dreamer/\.github/workflows/trusted-protected-operations\.yml@[a-f0-9]{40}$' -or
        [string]$Binding.producerWorkflowRef -ceq
            "syedmh/Dreamer/$([string]$trustedPreflightRole[0].workflowPath)@$('0' * 40)" -or
        [string]$Binding.runId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value $Binding.runAttempt -Minimum 1) -or
        [string]$Binding.commitSha -notmatch '^[a-f0-9]{40}$' -or
        (-not [string]::IsNullOrWhiteSpace($ExpectedTopLevelCallerWorkflowRef) -and
            [string]$Binding.topLevelCallerWorkflowRef -cne $ExpectedTopLevelCallerWorkflowRef) -or
        (-not [string]::IsNullOrWhiteSpace($ExpectedProducerWorkflowRef) -and
            [string]$Binding.producerWorkflowRef -cne $ExpectedProducerWorkflowRef)) {
        throw 'Migration producer run binding is malformed or does not match the exact signed caller and producer identity.'
    }
    return [pscustomobject][ordered]@{
        schemaVersion = '2.0.0'
        topLevelCallerWorkflowRef = [string]$Binding.topLevelCallerWorkflowRef
        producerWorkflowRef = [string]$Binding.producerWorkflowRef
        runId = [string]$Binding.runId
        runAttempt = [int]$Binding.runAttempt
        commitSha = [string]$Binding.commitSha
    }
}

function Assert-MigrationRecordRunProvenanceBinding {
    param(
        [Parameter(Mandatory = $true)]$Binding,
        [Parameter(Mandatory = $true)]$VerifiedProvenance,
        [Parameter(Mandatory = $true)][string]$Label
    )

    foreach ($name in @(
        'repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    )) {
        if ([string](Get-MigrationProperty $Binding $name) -cne
            [string](Get-MigrationProperty $VerifiedProvenance.run $name)) {
            throw "Migration $Label does not match its resolver-verified producer run: $name."
        }
    }
}

function Get-MigrationCanonicalTextSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($bytes).Replace("`r`n", "`n").Replace("`r", "`n")
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($utf8.GetBytes($text))
    )).ToLowerInvariant()
}

function Write-MigrationJson {
    param($Value, [string]$Path)

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 40) + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function ConvertTo-MigrationActorIdSet {
    param([Parameter(Mandatory = $true)][string]$Value)

    $ids = @($Value -split '[,\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -notmatch '^[1-9][0-9]*$' }).Count -gt 0) {
        throw 'The protected migration authorizer actor ID allowlist is empty or malformed.'
    }
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $ids) {
        if (-not $set.Add([string]$id)) {
            throw 'The protected migration authorizer actor ID allowlist contains a duplicate.'
        }
    }
    return ,$set
}

function Assert-MigrationRunMetadata {
    param(
        $Metadata,
        $Provenance,
        [string[]]$AllowedWorkflowPaths,
        [string]$ExpectedCommitSha,
        [switch]$RequireSuccess
    )

    if ([string](Get-MigrationProperty $Metadata 'schemaVersion') -ne '1.0.0' -or
        [string](Get-MigrationProperty $Metadata 'repository') -ne [string]$Provenance.repository -or
        [string](Get-MigrationProperty $Metadata 'workflowPath') -notin $AllowedWorkflowPaths -or
        [string](Get-MigrationProperty $Metadata 'ref') -ne [string]$Provenance.protectedRef -or
        [string](Get-MigrationProperty $Metadata 'runId') -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value (Get-MigrationProperty $Metadata 'runAttempt') -Minimum 1) -or
        [string](Get-MigrationProperty $Metadata 'actor') -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
        [string](Get-MigrationProperty $Metadata 'actorId') -notmatch '^[1-9][0-9]*$' -or
        [string](Get-MigrationProperty $Metadata 'commitSha') -ne $ExpectedCommitSha.ToLowerInvariant()) {
        throw 'Migration run metadata is not bound to the approved repository, workflow, ref, run, and release commit.'
    }
    if ($RequireSuccess -and
        ([string](Get-MigrationProperty $Metadata 'status') -ne 'completed' -or
         [string](Get-MigrationProperty $Metadata 'conclusion') -ne 'success')) {
        throw 'Migration run metadata does not prove a completed successful protected workflow.'
    }
}

function Get-MigrationRunIssuerWorkflowPaths {
    param(
        [Parameter(Mandatory = $true)]$Provenance,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Development', 'Staging', 'Production')]
        [string]$Stage
    )

    $issuers = @(Get-MigrationProperty $Provenance 'migrationRunIssuers')
    $issuer = @($issuers | Where-Object { [string](Get-MigrationProperty $_ 'stage') -ceq $Stage })
    if ($issuer.Count -ne 1) {
        throw "Migration run provenance must define exactly one trusted issuer set for $Stage."
    }
    Assert-MigrationExactProperties -Object $issuer[0] -Label "$Stage migration run issuer" -Expected @(
        'stage',
        'repository',
        'workflowPaths',
        'ref'
    )
    $workflowPaths = @(Get-MigrationProperty $issuer[0] 'workflowPaths')
    $uniqueWorkflowPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ([string]$issuer[0].repository -cne [string]$Provenance.repository -or
        [string]$issuer[0].ref -cne [string]$Provenance.protectedRef -or
        $workflowPaths.Count -eq 0) {
        throw "$Stage migration run issuer is not bound to the protected repository, workflow set, and ref."
    }
    foreach ($workflowPath in $workflowPaths) {
        if ([string]$workflowPath -notmatch '^\.github/workflows/[a-z0-9-]+\.yml$' -or
            -not $uniqueWorkflowPaths.Add([string]$workflowPath)) {
            throw "$Stage migration run issuer workflow set is malformed or duplicated."
        }
    }
    return $workflowPaths
}

function Assert-MigrationRunBinding {
    param($Binding, $Metadata)

    foreach ($name in @('repository', 'workflowPath', 'runId', 'runAttempt', 'ref', 'commitSha', 'conclusion')) {
        if ([string](Get-MigrationProperty $Binding $name) -ne [string](Get-MigrationProperty $Metadata $name)) {
            throw "Migration evidence run binding does not match trusted metadata: $name."
        }
    }
}

function Get-MigrationAuthorizationIssuer {
    param(
        [Parameter(Mandatory = $true)]$Provenance,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    $issuers = @(Get-MigrationProperty $Provenance 'migrationAuthorizationIssuers')
    $issuer = @($issuers | Where-Object { [string](Get-MigrationProperty $_ 'stage') -ceq $Stage })
    if ($issuer.Count -ne 1) {
        throw "Migration authorization provenance must define exactly one trusted issuer for $Stage."
    }
    Assert-MigrationExactProperties -Object $issuer[0] -Label "$Stage migration authorization issuer" -Expected @(
        'stage',
        'repository',
        'workflowPath',
        'ref'
    )
    if ([string]$issuer[0].repository -cne [string]$Provenance.repository -or
        [string]$issuer[0].ref -cne [string]$Provenance.protectedRef -or
        [string]$issuer[0].workflowPath -notmatch '^\.github/workflows/[a-z0-9-]+\.yml$') {
        throw "$Stage migration authorization issuer is not bound to the protected repository, workflow, and ref."
    }
    return $issuer[0]
}

function Assert-MigrationAuthorizationIssuerRun {
    param(
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)]$Provenance,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$ExpectedCommitSha
    )

    $issuer = Get-MigrationAuthorizationIssuer -Provenance $Provenance -Stage $Stage
    Assert-MigrationRunMetadata -Metadata $Metadata `
        -Provenance $Provenance `
        -AllowedWorkflowPaths @([string]$issuer.workflowPath) `
        -ExpectedCommitSha $ExpectedCommitSha `
        -RequireSuccess
    if ([string](Get-MigrationProperty $Metadata 'repository') -cne [string]$issuer.repository -or
        [string](Get-MigrationProperty $Metadata 'ref') -cne [string]$issuer.ref) {
        throw 'Migration authorization run does not match its stage-specific trusted issuer.'
    }
    return $issuer
}

function Get-MigrationApplyOperationId {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)]$OperationRun
    )

    $runId = [string](Get-MigrationProperty $OperationRun 'runId')
    $runAttempt = Get-MigrationProperty $OperationRun 'runAttempt'
    if ($Stage -notin @('Development', 'Staging', 'Production') -or
        $runId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value $runAttempt -Minimum 1)) {
        throw 'Migration Apply operation ID requires a trusted stage, run ID, and run attempt.'
    }
    return "migration-apply/$Stage/$runId/$runAttempt"
}

function Read-MigrationTargetMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)]$StagePolicy
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Protected stage target metadata is missing.'
    }
    $metadata = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-MigrationExactProperties -Object $metadata -Label 'Protected stage target metadata' -Expected @(
        'schemaVersion',
        'source',
        'stage',
        'stageCode',
        'githubEnvironment',
        'dataIsolationKey',
        'serviceConnectionSecretName',
        'immutableResourceGroupName',
        'sqlServerName',
        'sqlServerFqdn',
        'sqlDatabaseName',
        'sqlServerResourceId',
        'sqlDatabaseResourceId',
        'webAppName',
        'webAppResourceId',
        'keyVaultName',
        'secretReferences',
        'providerModes',
        'operationHooks'
    )
    if ([string]$metadata.schemaVersion -ne '1.0.0' -or
        [string]$metadata.source -ne 'T20_T21_PROTECTED_STAGE_CONFIGURATION' -or
        [string]$metadata.stage -ne $Stage -or
        [string]$metadata.stageCode -ne [string]$StagePolicy.stageCode -or
        [string]$metadata.githubEnvironment -ne [string]$StagePolicy.githubEnvironment -or
        [string]$metadata.dataIsolationKey -ne [string]$StagePolicy.dataIsolationKey -or
        [string]$metadata.serviceConnectionSecretName -ne [string]$StagePolicy.serviceConnectionSecretName -or
        [string]$metadata.immutableResourceGroupName -ne [string]$StagePolicy.immutableResourceGroupName) {
        throw 'Protected stage target metadata does not match the immutable stage policy.'
    }

    $stageCode = [regex]::Escape([string]$StagePolicy.stageCode)
    if ([string]$metadata.sqlServerName -notmatch "^husaynia-sql-$stageCode-([a-z0-9]{13})$") {
        throw 'Protected SQL server name does not match the immutable T20 naming contract.'
    }
    $stableToken = $Matches[1]
    if ([string]$metadata.sqlServerFqdn -ne "$($metadata.sqlServerName).database.windows.net" -or
        [string]$metadata.sqlDatabaseName -ne "husaynia-$($StagePolicy.stageCode)" -or
        [string]$metadata.webAppName -ne "husaynia-web-$($StagePolicy.stageCode)-$stableToken" -or
        [string]$metadata.keyVaultName -ne "hsy-kv-$($StagePolicy.stageCode)-$stableToken") {
        throw 'Protected target names are cross-stage or do not match the immutable T20 handoff.'
    }

    if ([string]$metadata.sqlServerResourceId -notmatch '^/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/([^/]+)/providers/Microsoft\.Sql/servers/([^/]+)$') {
        throw 'Protected SQL server resource ID is invalid.'
    }
    $subscriptionId = $Matches[1].ToLowerInvariant()
    $resourceGroup = $Matches[2]
    $resourceServerName = $Matches[3]
    $parsedSubscriptionId = [guid]::Empty
    if (-not [guid]::TryParse($subscriptionId, [ref]$parsedSubscriptionId) -or
        $resourceGroup -ne [string]$StagePolicy.immutableResourceGroupName -or
        $resourceServerName -ne [string]$metadata.sqlServerName -or
        [string]$metadata.sqlDatabaseResourceId -cne "$($metadata.sqlServerResourceId)/databases/$($metadata.sqlDatabaseName)" -or
        [string]$metadata.webAppResourceId -cne "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$($metadata.webAppName)") {
        throw 'Protected target resource IDs do not bind the immutable stage resources.'
    }

    if ([string]$metadata.sqlServerFqdn -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.database\.windows\.net$' -or
        [string]$metadata.sqlDatabaseName -notmatch '^husaynia-(dev|stg|prd)$') {
        throw 'Protected SQL target contains prohibited characters or SQLCMD syntax.'
    }
    return $metadata
}

function Get-MigrationTargetFingerprint {
    param($TargetMetadata)

    $normalized = @(
        ([string]$TargetMetadata.stage).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlServerFqdn).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlDatabaseName).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlServerResourceId).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlDatabaseResourceId).ToLowerInvariant()
    ) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant()
}

function Get-ApplicationMigrationBundlePolicy {
    param(
        [Parameter(Mandatory = $true)]$Policy,
        [switch]$RequireMaterialized
    )

    $migration = Get-MigrationProperty $Policy 'migration'
    $bundle = Get-MigrationProperty $migration 'applicationBundle'
    Assert-MigrationExactProperties -Object $bundle -Label 'Application migration bundle policy' -Expected @(
        'relativePath',
        'format',
        'sha256'
    )
    if ([string]$bundle.relativePath -cne 'migrations/bundle/Husaynia.Database.Migrations.dll' -or
        [string]$bundle.format -cne 'dotnet-managed-migration-bundle-v1') {
        throw 'Application migration bundle policy path or format is not the dedicated reviewed T18 contract.'
    }
    $bundleSha256 = [string]$bundle.sha256
    if (-not [string]::IsNullOrWhiteSpace($bundleSha256) -and
        -not (Test-MigrationSha256 $bundleSha256)) {
        throw 'The dedicated T18 application migration bundle checksum is invalid.'
    }
    if ($RequireMaterialized -and -not (Test-MigrationSha256 $bundleSha256)) {
        throw 'The dedicated T18 application migration bundle is not materialized and hash-bound in policy.'
    }
    return $bundle
}

function Assert-MigrationOrchestrationFiles {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactRoot,
        [Parameter(Mandatory = $true)]$Policy,
        [Parameter(Mandatory = $true)]$Manifest
    )

    $expectedPaths = @(
        'migrations/bundle/Invoke-MigrationBundle.ps1',
        'migrations/bundle/Migration.Common.ps1'
    )
    $orchestrationFiles = @($Policy.migration.orchestrationFiles)
    if ([string]$Policy.scriptIntegrity.algorithm -cne 'sha256-utf8-lf-v1' -or
        [string]$Policy.scriptIntegrity.encoding -cne 'utf-8' -or
        [string]$Policy.scriptIntegrity.lineEndings -cne 'lf' -or
        $orchestrationFiles.Count -ne $expectedPaths.Count) {
        throw 'Migration orchestration policy does not contain the exact reviewed file set.'
    }
    foreach ($expectedPath in $expectedPaths) {
        $policyEntry = @($orchestrationFiles | Where-Object { $_.relativePath -ceq $expectedPath })
        $manifestEntry = @($Manifest.files | Where-Object { $_.path -ceq $expectedPath })
        $artifactPath = Join-Path $ArtifactRoot $expectedPath
        if ($policyEntry.Count -ne 1 -or
            $manifestEntry.Count -ne 1 -or
            -not (Test-MigrationSha256 ([string]$policyEntry[0].sha256)) -or
            -not (Test-Path -LiteralPath $artifactPath -PathType Leaf) -or
            (Get-MigrationSha256 -Path $artifactPath) -cne [string]$manifestEntry[0].sha256 -or
            (Get-MigrationCanonicalTextSha256 -Path $artifactPath) -cne [string]$policyEntry[0].sha256) {
            throw "Migration orchestration file is missing or not policy-pinned: $expectedPath"
        }
    }
}

function Test-MigrationJsonInteger {
    param($Value, [int64]$Minimum, [int64]$Maximum = [int64]::MaxValue)

    if ($null -eq $Value -or $Value -is [bool]) {
        return $false
    }
    if ($Value -isnot [sbyte] -and
        $Value -isnot [byte] -and
        $Value -isnot [int16] -and
        $Value -isnot [uint16] -and
        $Value -isnot [int32] -and
        $Value -isnot [uint32] -and
        $Value -isnot [int64]) {
        return $false
    }
    $number = [int64]$Value
    return $number -ge $Minimum -and $number -le $Maximum
}

function Assert-MigrationCanonicalConnection {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][string]$ExpectedServerFqdn,
        [Parameter(Mandatory = $true)][string]$ExpectedDatabaseName
    )

    $expected = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $expected.Add('Server', "tcp:$ExpectedServerFqdn,1433")
    $expected.Add('Initial Catalog', $ExpectedDatabaseName)
    $expected.Add('Authentication', 'Active Directory Default')
    $expected.Add('Encrypt', 'True')
    $expected.Add('TrustServerCertificate', 'False')
    $expectedConnectionString =
        "Server=$($expected['Server']);" +
        "Initial Catalog=$($expected['Initial Catalog']);" +
        "Authentication=$($expected['Authentication']);" +
        "Encrypt=$($expected['Encrypt']);" +
        "TrustServerCertificate=$($expected['TrustServerCertificate']);"
    $segments = @($ConnectionString -split ';')
    if ($segments.Count -eq 0) {
        throw 'Migration connection string is malformed.'
    }

    $values = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $separator = $segment.IndexOf('=')
        if ($separator -le 0) {
            throw 'Migration connection string is malformed.'
        }
        $key = $segment.Substring(0, $separator).Trim()
        $value = $segment.Substring($separator + 1).Trim()
        if ([string]::IsNullOrWhiteSpace($key) -or
            [string]::IsNullOrWhiteSpace($value) -or
            -not $expected.ContainsKey($key)) {
            throw 'Migration connection string contains an unapproved key, alias, or empty value.'
        }
        if ($values.ContainsKey($key)) {
            throw 'Migration connection string contains a duplicate key.'
        }
        $values.Add($key, $value)
    }

    if ($values.Count -ne $expected.Count) {
        throw 'Migration connection string does not contain the exact approved key set.'
    }
    foreach ($key in $expected.Keys) {
        if (-not $values.ContainsKey($key) -or
            $values[$key] -cne $expected[$key]) {
            throw 'Migration connection string does not exactly match the protected passwordless target contract.'
        }
    }
    if ($ConnectionString -cne $expectedConnectionString) {
        throw 'Migration connection string is not in the exact canonical workflow form.'
    }
}

function Get-MigrationApplyLeasePolicy {
    param([Parameter(Mandatory = $true)]$Policy)

    $migration = Get-MigrationProperty $Policy 'migration'
    $lease = Get-MigrationProperty $migration 'applyLease'
    Assert-MigrationExactProperties -Object $lease -Label 'Migration apply lease policy' -Expected @(
        'provider',
        'resourcePrefix',
        'tableName',
        'leaseDurationSeconds',
        'renewAfterSeconds',
        'staleAfterSeconds'
    )
    if ([string]$lease.provider -cne 'sqlserver-stage-lease-v1' -or
        [string]$lease.resourcePrefix -cne 'husaynia-stage-lease' -or
        [string]$lease.tableName -cne '__HusayniaStageLease' -or
        -not (Test-MigrationJsonInteger -Value $lease.leaseDurationSeconds -Minimum 1) -or
        -not (Test-MigrationJsonInteger -Value $lease.renewAfterSeconds -Minimum 1) -or
        -not (Test-MigrationJsonInteger -Value $lease.staleAfterSeconds -Minimum 1) -or
        [int]$lease.renewAfterSeconds -gt [int]$lease.leaseDurationSeconds -or
        [int]$lease.renewAfterSeconds -gt [int]$lease.staleAfterSeconds -or
        [int]$lease.leaseDurationSeconds -gt [int]$lease.staleAfterSeconds -or
        [string]$lease.tableName -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,127}$') {
        throw 'Migration apply lease policy is not the reviewed durable SQL-backed fencing contract.'
    }
    return $lease
}

function Get-MigrationStageLeaseResource {
    param(
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint
    )

    if ($Stage -notin @('Development', 'Staging', 'Production') -or
        -not (Test-MigrationSha256 $TargetFingerprint)) {
        throw 'Migration stage lease resource requires a reviewed stage and target fingerprint.'
    }
    return "$([string]$LeasePolicy.resourcePrefix)-$($Stage.ToLowerInvariant())-$TargetFingerprint"
}

function Get-MigrationStageLeaseHolder {
    param([Parameter(Mandatory = $true)]$ProducerRunBinding)

    $holderRunId = [string](Get-MigrationProperty $ProducerRunBinding 'runId')
    $holderRunAttempt = Get-MigrationProperty $ProducerRunBinding 'runAttempt'
    if ($holderRunId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value $holderRunAttempt -Minimum 1)) {
        throw 'Migration stage lease holder run identity is invalid.'
    }
    return [ordered]@{
        holderRunId = $holderRunId
        holderRunAttempt = [int]$holderRunAttempt
    }
}

function ConvertTo-MigrationSqlStringLiteral {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return 'NULL'
    }
    return "N'$($Value.Replace('''', ''''''))'"
}

function Get-MigrationStageLeaseContextExactProperties {
    return @(
        'schemaVersion',
        'provider',
        'resource',
        'stage',
        'databaseTargetFingerprint',
        'authorizationEvidenceSha256',
        'holderRunId',
        'holderRunAttempt',
        'fenceToken',
        'acquiredAtUtc',
        'expiresAtUtc',
        'releasedAtUtc',
        'released'
    )
}

function Write-MigrationStageLeaseContext {
    param(
        [Parameter(Mandatory = $true)]$LeaseContext,
        [Parameter(Mandatory = $true)][string]$Path
    )

    Write-MigrationJson -Value $LeaseContext -Path $Path
}

function Read-MigrationStageLeaseContext {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$ExpectedStage,
        [string]$ExpectedTargetFingerprint,
        [string]$ExpectedAuthorizationEvidenceSha256,
        [string]$ExpectedResource
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Migration stage lease context is missing.'
    }
    $context = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String
    Assert-MigrationExactProperties -Object $context -Label 'Migration stage lease context' `
        -Expected (Get-MigrationStageLeaseContextExactProperties)
    $acquiredAt = [DateTimeOffset]::MinValue
    $expiresAt = [DateTimeOffset]::MinValue
    $releasedAt = [DateTimeOffset]::MinValue
    if ([string]$context.schemaVersion -cne '1.0.0' -or
        [string]$context.provider -cne 'sqlserver-stage-lease-v1' -or
        [string]$context.resource -notmatch '^[a-z0-9-]{1,256}$' -or
        [string]$context.stage -notin @('Development', 'Staging', 'Production') -or
        -not (Test-MigrationSha256 ([string]$context.databaseTargetFingerprint)) -or
        -not (Test-MigrationSha256 ([string]$context.authorizationEvidenceSha256)) -or
        $context.released -isnot [bool] -or
        [string]$context.holderRunId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-MigrationJsonInteger -Value $context.holderRunAttempt -Minimum 1) -or
        -not (Test-MigrationJsonInteger -Value $context.fenceToken -Minimum 1) -or
        -not [DateTimeOffset]::TryParse([string]$context.acquiredAtUtc, [ref]$acquiredAt) -or
        -not [DateTimeOffset]::TryParse([string]$context.expiresAtUtc, [ref]$expiresAt) -or
        $acquiredAt.Offset -ne [TimeSpan]::Zero -or
        $expiresAt.Offset -ne [TimeSpan]::Zero -or
        $expiresAt -le $acquiredAt -or
        ($context.released -and
            (-not [DateTimeOffset]::TryParse([string]$context.releasedAtUtc, [ref]$releasedAt) -or
             $releasedAt.Offset -ne [TimeSpan]::Zero -or
             $releasedAt -lt $acquiredAt)) -or
        (-not $context.released -and
            -not [string]::IsNullOrWhiteSpace([string]$context.releasedAtUtc))) {
        throw 'Migration stage lease context does not match the reviewed durable fencing contract.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedStage) -and
        [string]$context.stage -cne $ExpectedStage) {
        throw 'Migration stage lease context stage does not match the requested stage.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetFingerprint) -and
        [string]$context.databaseTargetFingerprint -cne $ExpectedTargetFingerprint) {
        throw 'Migration stage lease context target fingerprint does not match the requested stage target.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedAuthorizationEvidenceSha256) -and
        [string]$context.authorizationEvidenceSha256 -cne $ExpectedAuthorizationEvidenceSha256) {
        throw 'Migration stage lease context authorization hash does not match the requested authorization evidence.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedResource) -and
        [string]$context.resource -cne $ExpectedResource) {
        throw 'Migration stage lease context resource does not match the requested lease resource.'
    }
    return $context
}

function Get-MigrationStageLeaseEvidence {
    param([Parameter(Mandatory = $true)]$LeaseContext)

    return [ordered]@{
        provider = [string]$LeaseContext.provider
        resource = [string]$LeaseContext.resource
        authorizationEvidenceSha256 = [string]$LeaseContext.authorizationEvidenceSha256
        holderRunId = [string]$LeaseContext.holderRunId
        holderRunAttempt = [int]$LeaseContext.holderRunAttempt
        fenceToken = [int64]$LeaseContext.fenceToken
        acquiredAtUtc = [string]$LeaseContext.acquiredAtUtc
        expiresAtUtc = [string]$LeaseContext.expiresAtUtc
        releasedAtUtc = [string]$LeaseContext.releasedAtUtc
        released = [bool]$LeaseContext.released
    }
}

function Update-MigrationApplyEvidenceStageLease {
    param(
        [Parameter(Mandatory = $true)][string]$EvidencePath,
        [Parameter(Mandatory = $true)]$LeaseContext
    )

    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw 'Migration apply evidence is missing.'
    }
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -DateKind String
    if ([string](Get-MigrationProperty $evidence 'schemaVersion') -cne '1.0.0' -or
        [string](Get-MigrationProperty $evidence 'evidenceType') -cne 'migration-apply' -or
        [string](Get-MigrationProperty $evidence 'status') -cne 'PASS' -or
        $null -eq (Get-MigrationProperty $evidence 'operation')) {
        throw 'Migration apply evidence is invalid for stage-lease update.'
    }
    $evidence.operation.stageLease = Get-MigrationStageLeaseEvidence -LeaseContext $LeaseContext
    Write-MigrationJson -Value $evidence -Path $EvidencePath
}

function Get-MigrationStageLeaseStatePath {
    param()
    return [Environment]::GetEnvironmentVariable('T21_MIGRATION_STAGE_LEASE_STATE_PATH')
}

function Add-MigrationStageLeaseMutationProperties {
    param([Parameter(Mandatory = $true)]$Record)

    if ($null -eq $Record.PSObject.Properties['activeMutationId']) {
        $Record | Add-Member -NotePropertyName activeMutationId -NotePropertyValue ''
    }
    if ($null -eq $Record.PSObject.Properties['activeMutationStartedAtUtc']) {
        $Record | Add-Member -NotePropertyName activeMutationStartedAtUtc -NotePropertyValue ''
    }
    return $Record
}

function ConvertTo-MigrationStageLeaseOperationLease {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [switch]$IncludeActiveMutation
    )

    $lease = [ordered]@{
        schemaVersion = [string]$Record.schemaVersion
        provider = [string]$Record.provider
        resource = [string]$Record.resource
        stage = [string]$Record.stage
        databaseTargetFingerprint = [string]$Record.databaseTargetFingerprint
        authorizationEvidenceSha256 = [string]$Record.authorizationEvidenceSha256
        holderRunId = [string]$Record.holderRunId
        holderRunAttempt = [int]$Record.holderRunAttempt
        fenceToken = [int64]$Record.fenceToken
        acquiredAtUtc = [string]$Record.acquiredAtUtc
        expiresAtUtc = [string]$Record.expiresAtUtc
        releasedAtUtc = [string]$Record.releasedAtUtc
        released = [bool]$Record.released
    }
    if ($IncludeActiveMutation) {
        $lease.activeMutationId = [string](Get-MigrationProperty $Record 'activeMutationId')
        $lease.activeMutationStartedAtUtc =
            [string](Get-MigrationProperty $Record 'activeMutationStartedAtUtc')
    }
    return $lease
}

function Read-MigrationStageLeaseState {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{
            schemaVersion = '1.0.0'
            leases = @()
        }
    }
    $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String
    if ([string](Get-MigrationProperty $state 'schemaVersion') -cne '1.0.0' -or
        $null -eq (Get-MigrationProperty $state 'leases')) {
        throw 'Local migration stage lease test state is malformed.'
    }
    foreach ($lease in @($state.leases)) {
        Add-MigrationStageLeaseMutationProperties -Record $lease | Out-Null
    }
    return $state
}

function Write-MigrationStageLeaseState {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Path
    )

    Write-MigrationJson -Value $State -Path $Path
}

function Invoke-LocalMigrationStageLease {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('AcquireLease', 'BeginMutation', 'CompleteMutation', 'RenewLease', 'ReleaseLease')]
        [string]$Mode,
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Resource,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint,
        [Parameter(Mandatory = $true)][string]$AuthorizationEvidenceSha256,
        [Parameter(Mandatory = $true)]$Holder,
        [int64]$ExpectedFenceToken,
        [guid]$MutationId = [guid]::Empty
    )

    $statePath = Get-MigrationStageLeaseStatePath
    if ([string]::IsNullOrWhiteSpace($statePath)) {
        throw 'Local migration stage lease test state path is not configured.'
    }

    $now = [DateTimeOffset]::UtcNow
    $state = Read-MigrationStageLeaseState -Path $statePath
    $leases = [Collections.Generic.List[object]]::new()
    foreach ($entry in @($state.leases)) {
        $leases.Add($entry)
    }
    $index = -1
    for ($current = 0; $current -lt $leases.Count; $current++) {
        if ([string]$leases[$current].resource -cne $Resource) {
            continue
        }
        $index = $current
        break
    }
    $record = if ($index -ge 0) { $leases[$index] } else { $null }

    switch ($Mode) {
        'AcquireLease' {
            if ($null -ne $record) {
                Add-MigrationStageLeaseMutationProperties -Record $record | Out-Null
            }
            if ($null -ne $record -and
                (-not [string]::IsNullOrWhiteSpace([string]$record.activeMutationId) -or
                 -not [string]::IsNullOrWhiteSpace([string]$record.activeMutationStartedAtUtc))) {
                throw 'Migration stage lease operation rejected while an active mutation claim exists.'
            }
            if ($null -ne $record -and
                [string]$record.authorizationEvidenceSha256 -ceq $AuthorizationEvidenceSha256) {
                throw 'Migration stage lease rejected authorization-hash replay.'
            }
            $canAcquire = $false
            $nextFenceToken = 1
            if ($null -eq $record) {
                $canAcquire = $true
            }
            else {
                $expiresAt = [DateTimeOffset]::Parse([string]$record.expiresAtUtc)
                if ([bool]$record.released -or $expiresAt -le $now) {
                    $canAcquire = $true
                    $nextFenceToken = [int64]$record.fenceToken + 1
                }
            }
            if (-not $canAcquire) {
                throw 'Migration stage lease is already held and not stale.'
            }
            $record = [ordered]@{
                schemaVersion = '1.0.0'
                provider = [string]$LeasePolicy.provider
                resource = $Resource
                stage = $Stage
                databaseTargetFingerprint = $TargetFingerprint
                authorizationEvidenceSha256 = $AuthorizationEvidenceSha256
                holderRunId = [string]$Holder.holderRunId
                holderRunAttempt = [int]$Holder.holderRunAttempt
                fenceToken = $nextFenceToken
                acquiredAtUtc = $now.ToString('O')
                expiresAtUtc = $now.AddSeconds([int]$LeasePolicy.leaseDurationSeconds).ToString('O')
                releasedAtUtc = ''
                released = $false
                activeMutationId = ''
                activeMutationStartedAtUtc = ''
            }
            if ($index -ge 0) {
                $leases[$index] = $record
            }
            else {
                $leases.Add($record)
            }
        }
        'BeginMutation' {
            if ($MutationId -eq [guid]::Empty) {
                throw 'Migration mutation claim ID is missing.'
            }
            if ($null -ne $record) {
                Add-MigrationStageLeaseMutationProperties -Record $record | Out-Null
            }
            $expiresAt = [DateTimeOffset]::MinValue
            if ($null -eq $record -or
                [string]$record.resource -cne $Resource -or
                [string]$record.stage -cne $Stage -or
                [string]$record.databaseTargetFingerprint -cne $TargetFingerprint -or
                [string]$record.authorizationEvidenceSha256 -cne $AuthorizationEvidenceSha256 -or
                [string]$record.holderRunId -cne [string]$Holder.holderRunId -or
                [int]$record.holderRunAttempt -ne [int]$Holder.holderRunAttempt -or
                [int64]$record.fenceToken -ne $ExpectedFenceToken -or
                [bool]$record.released -or
                -not [DateTimeOffset]::TryParse([string]$record.expiresAtUtc, [ref]$expiresAt) -or
                $expiresAt -le $now -or
                -not [string]::IsNullOrWhiteSpace([string]$record.activeMutationId) -or
                -not [string]::IsNullOrWhiteSpace([string]$record.activeMutationStartedAtUtc)) {
                throw 'Migration mutation claim rejected stale lease holder, fence token, authorization hash, expiry, or active claim.'
            }
            $record.activeMutationId = $MutationId.ToString('D')
            $record.activeMutationStartedAtUtc = $now.ToString('O')
            $leases[$index] = $record
        }
        'CompleteMutation' {
            if ($MutationId -eq [guid]::Empty) {
                throw 'Migration mutation completion claim ID is missing.'
            }
            if ($null -ne $record) {
                Add-MigrationStageLeaseMutationProperties -Record $record | Out-Null
            }
            if ($null -eq $record -or
                [string]$record.resource -cne $Resource -or
                [string]$record.stage -cne $Stage -or
                [string]$record.databaseTargetFingerprint -cne $TargetFingerprint -or
                [string]$record.authorizationEvidenceSha256 -cne $AuthorizationEvidenceSha256 -or
                [string]$record.holderRunId -cne [string]$Holder.holderRunId -or
                [int]$record.holderRunAttempt -ne [int]$Holder.holderRunAttempt -or
                [int64]$record.fenceToken -ne $ExpectedFenceToken -or
                [bool]$record.released -or
                [string]$record.activeMutationId -cne $MutationId.ToString('D') -or
                [string]::IsNullOrWhiteSpace([string]$record.activeMutationStartedAtUtc)) {
                throw 'Migration mutation completion rejected a mismatched claim or lease tuple.'
            }
            $record.activeMutationId = ''
            $record.activeMutationStartedAtUtc = ''
            $leases[$index] = $record
        }
        'RenewLease' {
            if ($null -eq $record) {
                throw 'Migration stage lease row is missing.'
            }
            Add-MigrationStageLeaseMutationProperties -Record $record | Out-Null
            if (-not [string]::IsNullOrWhiteSpace([string]$record.activeMutationId) -or
                -not [string]::IsNullOrWhiteSpace([string]$record.activeMutationStartedAtUtc)) {
                throw 'Migration stage lease operation rejected while an active mutation claim exists.'
            }
            $expiresAt = [DateTimeOffset]::Parse([string]$record.expiresAtUtc)
            if ([string]$record.authorizationEvidenceSha256 -cne $AuthorizationEvidenceSha256 -or
                [string]$record.holderRunId -cne [string]$Holder.holderRunId -or
                [int]$record.holderRunAttempt -ne [int]$Holder.holderRunAttempt -or
                [int64]$record.fenceToken -ne $ExpectedFenceToken -or
                [bool]$record.released) {
                throw 'Migration stage lease fence token, holder, or authorization changed.'
            }
            if ($expiresAt -le $now) {
                throw 'Migration stage lease expired before renewal.'
            }
            if ($expiresAt -le $now.AddSeconds([int]$LeasePolicy.renewAfterSeconds)) {
                $record.expiresAtUtc = $now.AddSeconds([int]$LeasePolicy.leaseDurationSeconds).ToString('O')
            }
            $leases[$index] = $record
        }
        'ReleaseLease' {
            if ($null -eq $record) {
                throw 'Migration stage lease row is missing.'
            }
            Add-MigrationStageLeaseMutationProperties -Record $record | Out-Null
            if (-not [string]::IsNullOrWhiteSpace([string]$record.activeMutationId) -or
                -not [string]::IsNullOrWhiteSpace([string]$record.activeMutationStartedAtUtc)) {
                throw 'Migration stage lease operation rejected while an active mutation claim exists.'
            }
            $expiresAt = [DateTimeOffset]::Parse([string]$record.expiresAtUtc)
            if ([string]$record.authorizationEvidenceSha256 -cne $AuthorizationEvidenceSha256 -or
                [string]$record.holderRunId -cne [string]$Holder.holderRunId -or
                [int]$record.holderRunAttempt -ne [int]$Holder.holderRunAttempt -or
                [int64]$record.fenceToken -ne $ExpectedFenceToken -or
                [bool]$record.released) {
                throw 'Migration stage lease cleanup could not release the current fence token.'
            }
            if ($expiresAt -le $now) {
                throw 'Migration stage lease expired before cleanup completed.'
            }
            $record.released = $true
            $record.releasedAtUtc = $now.ToString('O')
            $leases[$index] = $record
        }
    }

    $state.leases = @($leases)
    Write-MigrationStageLeaseState -State $state -Path $statePath
    return $record
}

function Invoke-SqlMigrationStageLease {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('AcquireLease', 'BeginMutation', 'CompleteMutation', 'RenewLease', 'ReleaseLease')]
        [string]$Mode,
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Resource,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint,
        [Parameter(Mandatory = $true)][string]$AuthorizationEvidenceSha256,
        [Parameter(Mandatory = $true)]$Holder,
        [int64]$ExpectedFenceToken,
        [guid]$MutationId = [guid]::Empty,
        [Parameter(Mandatory = $true)][string]$BundleRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-f0-9]{64}$')]
        [string]$ExpectedApplicationSha256,
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider,
        [scriptblock]$LocalSqlExecutor
    )

    if ($Mode -in @('BeginMutation', 'CompleteMutation') -and
        $MutationId -eq [guid]::Empty) {
        throw "Migration stage lease $Mode requires a non-empty mutation claim ID."
    }

    $tableName = [string]$LeasePolicy.tableName
    $leaseDurationSeconds = [int]$LeasePolicy.leaseDurationSeconds
    $renewAfterSeconds = [int]$LeasePolicy.renewAfterSeconds
    $resourceSql = ConvertTo-MigrationSqlStringLiteral -Value $Resource
    $stageSql = ConvertTo-MigrationSqlStringLiteral -Value $Stage
    $targetFingerprintSql = ConvertTo-MigrationSqlStringLiteral -Value $TargetFingerprint
    $authorizationSql = ConvertTo-MigrationSqlStringLiteral -Value $AuthorizationEvidenceSha256
        $mutationIdSql = if ($MutationId -eq [guid]::Empty) {
            'NULL'
        }
        else {
            ConvertTo-MigrationSqlStringLiteral -Value $MutationId.ToString('D')
        }
        $schemaUpgradeSql = @"
    -- Migration safety:
    -- What: add nullable durable mutation-claim columns to the stage-lease table.
    -- Backward compatible: yes; old readers and writers can ignore both nullable columns.
    -- Rollback: leave the columns unused and NULL; dropping them is destructive and requires separate CTO authorization.
    -- Large table: nullable columns without defaults are metadata-only on SQL Server, with only a brief schema lock and no row backfill.
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        IF OBJECT_ID(N'dbo.$tableName', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[$tableName] (
                [LeaseResource] nvarchar(256) NOT NULL PRIMARY KEY,
                [Stage] nvarchar(32) NOT NULL,
                [DatabaseTargetFingerprint] char(64) NOT NULL,
                [AuthorizationEvidenceSha256] char(64) NOT NULL,
                [HolderRunId] bigint NOT NULL,
                [HolderRunAttempt] int NOT NULL,
                [FenceToken] bigint NOT NULL,
                [AcquiredAtUtc] datetimeoffset(7) NOT NULL,
                [ExpiresAtUtc] datetimeoffset(7) NOT NULL,
                [Released] bit NOT NULL,
                [ReleasedAtUtc] datetimeoffset(7) NULL,
                [ActiveMutationId] uniqueidentifier NULL,
                [ActiveMutationStartedAtUtc] datetimeoffset(7) NULL
            );
        END
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 2714
            THROW;
    END CATCH;
    IF COL_LENGTH(N'dbo.$tableName', N'ActiveMutationId') IS NULL
    BEGIN
        BEGIN TRY
            ALTER TABLE [dbo].[$tableName]
                ADD [ActiveMutationId] uniqueidentifier NULL;
        END TRY
        BEGIN CATCH
            IF COL_LENGTH(N'dbo.$tableName', N'ActiveMutationId') IS NULL
                THROW;
        END CATCH;
    END;
    IF COL_LENGTH(N'dbo.$tableName', N'ActiveMutationStartedAtUtc') IS NULL
    BEGIN
        BEGIN TRY
            ALTER TABLE [dbo].[$tableName]
                ADD [ActiveMutationStartedAtUtc] datetimeoffset(7) NULL;
        END TRY
        BEGIN CATCH
            IF COL_LENGTH(N'dbo.$tableName', N'ActiveMutationStartedAtUtc') IS NULL
                THROW;
        END CATCH;
    END;
"@

        $operationQuery = switch ($Mode) {
            'AcquireLease' {
    @"
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @Now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
    DECLARE @FenceToken bigint = 0;
DECLARE @AcquiredAtUtc datetimeoffset(7) = @Now;
DECLARE @ExpiresAtUtc datetimeoffset(7) = DATEADD(SECOND, $leaseDurationSeconds, @Now);
DECLARE @ExistingAuthorizationEvidenceSha256 char(64);
DECLARE @ExistingFenceToken bigint;
DECLARE @ExistingExpiresAtUtc datetimeoffset(7);
DECLARE @ExistingReleased bit;
DECLARE @ExistingActiveMutationId uniqueidentifier;
DECLARE @ExistingActiveMutationStartedAtUtc datetimeoffset(7);
SELECT
    @ExistingAuthorizationEvidenceSha256 = [AuthorizationEvidenceSha256],
    @ExistingFenceToken = [FenceToken],
    @ExistingExpiresAtUtc = [ExpiresAtUtc],
    @ExistingReleased = [Released],
    @ExistingActiveMutationId = [ActiveMutationId],
    @ExistingActiveMutationStartedAtUtc = [ActiveMutationStartedAtUtc]
FROM [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
WHERE [LeaseResource] = $resourceSql;
IF @ExistingActiveMutationId IS NOT NULL OR
   @ExistingActiveMutationStartedAtUtc IS NOT NULL
BEGIN
    ;THROW 51021, 'Migration stage lease operation rejected while an active mutation claim exists.', 1;
END;
IF @ExistingAuthorizationEvidenceSha256 IS NOT NULL AND
   @ExistingAuthorizationEvidenceSha256 = $authorizationSql
BEGIN
    ;THROW 51022, 'Migration stage lease rejected authorization-hash replay.', 1;
END;
IF @ExistingAuthorizationEvidenceSha256 IS NULL
BEGIN
    SET @FenceToken = 1;
    INSERT INTO [dbo].[$tableName] (
        [LeaseResource],
        [Stage],
        [DatabaseTargetFingerprint],
        [AuthorizationEvidenceSha256],
        [HolderRunId],
        [HolderRunAttempt],
        [FenceToken],
        [AcquiredAtUtc],
        [ExpiresAtUtc],
        [Released],
        [ReleasedAtUtc],
        [ActiveMutationId],
        [ActiveMutationStartedAtUtc]
    )
    VALUES (
        $resourceSql,
        $stageSql,
        $targetFingerprintSql,
        $authorizationSql,
        $([string]$Holder.holderRunId),
        $([int]$Holder.holderRunAttempt),
        @FenceToken,
        @AcquiredAtUtc,
        @ExpiresAtUtc,
        0,
        NULL,
        NULL,
        NULL
    );
END
ELSE IF @ExistingReleased = 1 OR @ExistingExpiresAtUtc <= @Now
BEGIN
    SET @FenceToken = ISNULL(@ExistingFenceToken, 0) + 1;
    UPDATE [dbo].[$tableName]
    SET
        [Stage] = $stageSql,
        [DatabaseTargetFingerprint] = $targetFingerprintSql,
        [AuthorizationEvidenceSha256] = $authorizationSql,
        [HolderRunId] = $([string]$Holder.holderRunId),
        [HolderRunAttempt] = $([int]$Holder.holderRunAttempt),
        [FenceToken] = @FenceToken,
        [AcquiredAtUtc] = @AcquiredAtUtc,
        [ExpiresAtUtc] = @ExpiresAtUtc,
        [Released] = 0,
        [ReleasedAtUtc] = NULL,
        [ActiveMutationId] = NULL,
        [ActiveMutationStartedAtUtc] = NULL
    WHERE [LeaseResource] = $resourceSql;
END
ELSE
BEGIN
    ;THROW 51023, 'Migration stage lease is already held and not stale.', 1;
END;
COMMIT TRANSACTION;
SELECT
    CAST(@FenceToken AS nvarchar(40)),
    CONVERT(nvarchar(40), @AcquiredAtUtc, 127),
    CONVERT(nvarchar(40), @ExpiresAtUtc, 127),
    CAST('' AS nvarchar(40)),
    CAST(0 AS nvarchar(1)),
    CAST('' AS nvarchar(36)),
    CAST('' AS nvarchar(40));
"@
        }
        'BeginMutation' {
@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @Now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
DECLARE @MutationId uniqueidentifier = $mutationIdSql;
DECLARE @AcquiredAtUtc datetimeoffset(7);
DECLARE @ExpiresAtUtc datetimeoffset(7);
DECLARE @ReleasedAtUtc datetimeoffset(7);
DECLARE @Released bit;
DECLARE @ObservedFenceToken bigint;
DECLARE @ActiveMutationId uniqueidentifier;
DECLARE @ActiveMutationStartedAtUtc datetimeoffset(7);
UPDATE [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
SET
    [ActiveMutationId] = @MutationId,
    [ActiveMutationStartedAtUtc] = @Now
WHERE [LeaseResource] = $resourceSql
  AND [Stage] = $stageSql
  AND [DatabaseTargetFingerprint] = $targetFingerprintSql
  AND [AuthorizationEvidenceSha256] = $authorizationSql
  AND [HolderRunId] = $([string]$Holder.holderRunId)
  AND [HolderRunAttempt] = $([int]$Holder.holderRunAttempt)
  AND [FenceToken] = $ExpectedFenceToken
  AND [Released] = 0
  AND [ExpiresAtUtc] > @Now
  AND [ActiveMutationId] IS NULL
  AND [ActiveMutationStartedAtUtc] IS NULL;
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51024, 'Migration mutation claim rejected stale lease holder, fence token, authorization hash, expiry, or active claim.', 1;
END;
SELECT
    @AcquiredAtUtc = [AcquiredAtUtc],
    @ExpiresAtUtc = [ExpiresAtUtc],
    @ReleasedAtUtc = [ReleasedAtUtc],
    @Released = [Released],
    @ObservedFenceToken = [FenceToken],
    @ActiveMutationId = [ActiveMutationId],
    @ActiveMutationStartedAtUtc = [ActiveMutationStartedAtUtc]
FROM [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
WHERE [LeaseResource] = $resourceSql
  AND [ActiveMutationId] = @MutationId;
COMMIT TRANSACTION;
SELECT
    CAST(@ObservedFenceToken AS nvarchar(40)),
    CONVERT(nvarchar(40), @AcquiredAtUtc, 127),
    CONVERT(nvarchar(40), @ExpiresAtUtc, 127),
    ISNULL(CONVERT(nvarchar(40), @ReleasedAtUtc, 127), N''),
    CAST(@Released AS nvarchar(1)),
    CONVERT(nvarchar(36), @ActiveMutationId),
    CONVERT(nvarchar(40), @ActiveMutationStartedAtUtc, 127);
"@
        }
        'CompleteMutation' {
@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @MutationId uniqueidentifier = $mutationIdSql;
DECLARE @AcquiredAtUtc datetimeoffset(7);
DECLARE @ExpiresAtUtc datetimeoffset(7);
DECLARE @ReleasedAtUtc datetimeoffset(7);
DECLARE @Released bit;
DECLARE @ObservedFenceToken bigint;
UPDATE [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
SET
    [ActiveMutationId] = NULL,
    [ActiveMutationStartedAtUtc] = NULL
WHERE [LeaseResource] = $resourceSql
  AND [Stage] = $stageSql
  AND [DatabaseTargetFingerprint] = $targetFingerprintSql
  AND [AuthorizationEvidenceSha256] = $authorizationSql
  AND [HolderRunId] = $([string]$Holder.holderRunId)
  AND [HolderRunAttempt] = $([int]$Holder.holderRunAttempt)
  AND [FenceToken] = $ExpectedFenceToken
  AND [Released] = 0
  AND [ActiveMutationId] = @MutationId
  AND [ActiveMutationStartedAtUtc] IS NOT NULL;
IF @@ROWCOUNT <> 1
BEGIN
    ;THROW 51025, 'Migration mutation completion rejected a mismatched claim or lease tuple.', 1;
END;
SELECT
    @AcquiredAtUtc = [AcquiredAtUtc],
    @ExpiresAtUtc = [ExpiresAtUtc],
    @ReleasedAtUtc = [ReleasedAtUtc],
    @Released = [Released],
    @ObservedFenceToken = [FenceToken]
FROM [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
WHERE [LeaseResource] = $resourceSql
  AND [Stage] = $stageSql
  AND [DatabaseTargetFingerprint] = $targetFingerprintSql
  AND [AuthorizationEvidenceSha256] = $authorizationSql
  AND [HolderRunId] = $([string]$Holder.holderRunId)
  AND [HolderRunAttempt] = $([int]$Holder.holderRunAttempt)
  AND [FenceToken] = $ExpectedFenceToken
  AND [Released] = 0
  AND [ActiveMutationId] IS NULL
  AND [ActiveMutationStartedAtUtc] IS NULL;
COMMIT TRANSACTION;
SELECT
    CAST(@ObservedFenceToken AS nvarchar(40)),
    CONVERT(nvarchar(40), @AcquiredAtUtc, 127),
    CONVERT(nvarchar(40), @ExpiresAtUtc, 127),
    ISNULL(CONVERT(nvarchar(40), @ReleasedAtUtc, 127), N''),
    CAST(@Released AS nvarchar(1)),
    CAST('' AS nvarchar(36)),
    CAST('' AS nvarchar(40));
"@
        }
        'RenewLease' {
@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @Now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
DECLARE @AcquiredAtUtc datetimeoffset(7);
DECLARE @ExpiresAtUtc datetimeoffset(7);
DECLARE @ReleasedAtUtc datetimeoffset(7);
DECLARE @Released bit;
DECLARE @ObservedFenceToken bigint;
DECLARE @ObservedAuthorizationEvidenceSha256 char(64);
DECLARE @ObservedHolderRunId bigint;
DECLARE @ObservedHolderRunAttempt int;
DECLARE @ObservedActiveMutationId uniqueidentifier;
DECLARE @ObservedActiveMutationStartedAtUtc datetimeoffset(7);
SELECT
    @AcquiredAtUtc = [AcquiredAtUtc],
    @ExpiresAtUtc = [ExpiresAtUtc],
    @ReleasedAtUtc = [ReleasedAtUtc],
    @Released = [Released],
    @ObservedFenceToken = [FenceToken],
    @ObservedAuthorizationEvidenceSha256 = [AuthorizationEvidenceSha256],
    @ObservedHolderRunId = [HolderRunId],
    @ObservedHolderRunAttempt = [HolderRunAttempt],
    @ObservedActiveMutationId = [ActiveMutationId],
    @ObservedActiveMutationStartedAtUtc = [ActiveMutationStartedAtUtc]
FROM [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
WHERE [LeaseResource] = $resourceSql;
IF @ObservedFenceToken IS NULL
BEGIN
    ;THROW 51026, 'Migration stage lease row is missing.', 1;
END;
IF @ObservedActiveMutationId IS NOT NULL OR
   @ObservedActiveMutationStartedAtUtc IS NOT NULL
BEGIN
    ;THROW 51027, 'Migration stage lease operation rejected while an active mutation claim exists.', 1;
END;
IF @ObservedAuthorizationEvidenceSha256 <> $authorizationSql OR
   @ObservedHolderRunId <> $([string]$Holder.holderRunId) OR
   @ObservedHolderRunAttempt <> $([int]$Holder.holderRunAttempt) OR
   @ObservedFenceToken <> $ExpectedFenceToken OR
   @Released = 1
BEGIN
    ;THROW 51028, 'Migration stage lease fence token, holder, or authorization changed.', 1;
END;
IF @ExpiresAtUtc <= @Now
BEGIN
    ;THROW 51029, 'Migration stage lease expired before renewal.', 1;
END;
IF @ExpiresAtUtc <= DATEADD(SECOND, $renewAfterSeconds, @Now)
BEGIN
    SET @ExpiresAtUtc = DATEADD(SECOND, $leaseDurationSeconds, @Now);
    UPDATE [dbo].[$tableName]
    SET [ExpiresAtUtc] = @ExpiresAtUtc
    WHERE [LeaseResource] = $resourceSql;
END;
COMMIT TRANSACTION;
SELECT
    CAST(@ObservedFenceToken AS nvarchar(40)),
    CONVERT(nvarchar(40), @AcquiredAtUtc, 127),
    CONVERT(nvarchar(40), @ExpiresAtUtc, 127),
    ISNULL(CONVERT(nvarchar(40), @ReleasedAtUtc, 127), N''),
    CAST(@Released AS nvarchar(1)),
    CAST('' AS nvarchar(36)),
    CAST('' AS nvarchar(40));
"@
        }
        'ReleaseLease' {
@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @Now datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
DECLARE @AcquiredAtUtc datetimeoffset(7);
DECLARE @ExpiresAtUtc datetimeoffset(7);
DECLARE @ReleasedAtUtc datetimeoffset(7);
DECLARE @Released bit;
DECLARE @ObservedFenceToken bigint;
DECLARE @ObservedAuthorizationEvidenceSha256 char(64);
DECLARE @ObservedHolderRunId bigint;
DECLARE @ObservedHolderRunAttempt int;
DECLARE @ObservedActiveMutationId uniqueidentifier;
DECLARE @ObservedActiveMutationStartedAtUtc datetimeoffset(7);
SELECT
    @AcquiredAtUtc = [AcquiredAtUtc],
    @ExpiresAtUtc = [ExpiresAtUtc],
    @ReleasedAtUtc = [ReleasedAtUtc],
    @Released = [Released],
    @ObservedFenceToken = [FenceToken],
    @ObservedAuthorizationEvidenceSha256 = [AuthorizationEvidenceSha256],
    @ObservedHolderRunId = [HolderRunId],
    @ObservedHolderRunAttempt = [HolderRunAttempt],
    @ObservedActiveMutationId = [ActiveMutationId],
    @ObservedActiveMutationStartedAtUtc = [ActiveMutationStartedAtUtc]
FROM [dbo].[$tableName] WITH (UPDLOCK, HOLDLOCK)
WHERE [LeaseResource] = $resourceSql;
IF @ObservedFenceToken IS NULL
BEGIN
    ;THROW 51030, 'Migration stage lease row is missing.', 1;
END;
IF @ObservedActiveMutationId IS NOT NULL OR
   @ObservedActiveMutationStartedAtUtc IS NOT NULL
BEGIN
    ;THROW 51031, 'Migration stage lease operation rejected while an active mutation claim exists.', 1;
END;
IF @ObservedAuthorizationEvidenceSha256 <> $authorizationSql OR
   @ObservedHolderRunId <> $([string]$Holder.holderRunId) OR
   @ObservedHolderRunAttempt <> $([int]$Holder.holderRunAttempt) OR
   @ObservedFenceToken <> $ExpectedFenceToken OR
   @Released = 1
BEGIN
    ;THROW 51032, 'Migration stage lease cleanup could not release the current fence token.', 1;
END;
IF @ExpiresAtUtc <= @Now
BEGIN
    ;THROW 51033, 'Migration stage lease expired before cleanup completed.', 1;
END;
SET @ReleasedAtUtc = @Now;
SET @Released = 1;
UPDATE [dbo].[$tableName]
SET
    [Released] = 1,
    [ReleasedAtUtc] = @ReleasedAtUtc
WHERE [LeaseResource] = $resourceSql;
COMMIT TRANSACTION;
SELECT
    CAST(@ObservedFenceToken AS nvarchar(40)),
    CONVERT(nvarchar(40), @AcquiredAtUtc, 127),
    CONVERT(nvarchar(40), @ExpiresAtUtc, 127),
    CONVERT(nvarchar(40), @ReleasedAtUtc, 127),
    CAST(1 AS nvarchar(1)),
    CAST('' AS nvarchar(36)),
    CAST('' AS nvarchar(40));
"@
        }
    }
    $query = "$schemaUpgradeSql`n$operationQuery"

    $rows = Invoke-MigrationSqlQuery `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -Query $query `
        -ExpectedColumnCount 7 `
        -CommandTimeoutSeconds 60 `
        -BundleRoot $BundleRoot `
        -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    return ConvertFrom-MigrationStageLeaseSqlRow `
        -Row $rows[0] `
        -LeasePolicy $LeasePolicy `
        -Resource $Resource `
        -Stage $Stage `
        -TargetFingerprint $TargetFingerprint `
        -AuthorizationEvidenceSha256 $AuthorizationEvidenceSha256 `
        -Holder $Holder
}

function Invoke-MigrationStageLeaseOperation {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('AcquireLease', 'BeginMutation', 'CompleteMutation', 'RenewLease', 'ReleaseLease')]
        [string]$Mode,
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Resource,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint,
        [Parameter(Mandatory = $true)][string]$AuthorizationEvidenceSha256,
        [Parameter(Mandatory = $true)]$Holder,
        [int64]$ExpectedFenceToken,
        [guid]$MutationId = [guid]::Empty,
        [string]$BundleRoot,
        [string]$ExpectedApplicationSha256,
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider,
        [scriptblock]$LocalSqlExecutor
    )

    Assert-MigrationLocalTestSeams -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor
    $localStatePath = Get-MigrationStageLeaseStatePath
    $record = $null
    if (-not [string]::IsNullOrWhiteSpace($localStatePath)) {
        $record = Invoke-LocalMigrationStageLease `
            -Mode $Mode `
            -LeasePolicy $LeasePolicy `
            -Resource $Resource `
            -Stage $Stage `
            -TargetFingerprint $TargetFingerprint `
            -AuthorizationEvidenceSha256 $AuthorizationEvidenceSha256 `
            -Holder $Holder `
            -ExpectedFenceToken $ExpectedFenceToken `
            -MutationId $MutationId
    }
    else {
        $record = Invoke-SqlMigrationStageLease `
            -Mode $Mode `
            -ServerName $ServerName `
            -DatabaseName $DatabaseName `
            -LeasePolicy $LeasePolicy `
            -Resource $Resource `
            -Stage $Stage `
            -TargetFingerprint $TargetFingerprint `
            -AuthorizationEvidenceSha256 $AuthorizationEvidenceSha256 `
            -Holder $Holder `
            -ExpectedFenceToken $ExpectedFenceToken `
            -MutationId $MutationId `
            -BundleRoot $BundleRoot `
            -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
            -EnableLocalTestSeams:$EnableLocalTestSeams `
            -LocalAccessTokenProvider $LocalAccessTokenProvider `
            -LocalSqlExecutor $LocalSqlExecutor
    }
    return ConvertTo-MigrationStageLeaseOperationLease `
        -Record $record `
        -IncludeActiveMutation:($Mode -eq 'BeginMutation')
}

function Invoke-MigrationFencedMutation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ServerName,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)]$LeasePolicy,
        [Parameter(Mandatory = $true)][string]$Resource,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$TargetFingerprint,
        [Parameter(Mandatory = $true)][string]$AuthorizationEvidenceSha256,
        [Parameter(Mandatory = $true)]$Holder,
        [Parameter(Mandatory = $true)][int64]$ExpectedFenceToken,
        [Parameter(Mandatory = $true)][scriptblock]$MutationExecutor,
        [string]$BundleRoot,
        [string]$ExpectedApplicationSha256,
        [switch]$EnableLocalTestSeams,
        [scriptblock]$LocalAccessTokenProvider,
        [scriptblock]$LocalSqlExecutor
    )

    $mutationId = [guid]::NewGuid()
    $validatedLease = Invoke-MigrationStageLeaseOperation `
        -Mode BeginMutation `
        -ServerName $ServerName `
        -DatabaseName $DatabaseName `
        -LeasePolicy $LeasePolicy `
        -Resource $Resource `
        -Stage $Stage `
        -TargetFingerprint $TargetFingerprint `
        -AuthorizationEvidenceSha256 $AuthorizationEvidenceSha256 `
        -Holder $Holder `
        -ExpectedFenceToken $ExpectedFenceToken `
        -MutationId $mutationId `
        -BundleRoot $BundleRoot `
        -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
        -EnableLocalTestSeams:$EnableLocalTestSeams `
        -LocalAccessTokenProvider $LocalAccessTokenProvider `
        -LocalSqlExecutor $LocalSqlExecutor

    $mutationResult = $null
    $mutationError = $null
    $completionError = $null
    $previousMutationId = [Environment]::GetEnvironmentVariable('T21_STAGE_LEASE_MUTATION_ID')
    try {
        try {
            if ([int64]$validatedLease.fenceToken -ne $ExpectedFenceToken -or
                [string]$validatedLease.activeMutationId -cne $mutationId.ToString('D')) {
                throw 'Migration mutation claim changed before bundle execution.'
            }
            [Environment]::SetEnvironmentVariable(
                'T21_STAGE_LEASE_MUTATION_ID',
                $mutationId.ToString('D'))
            $mutationResult = & $MutationExecutor $validatedLease
        }
        catch {
            $mutationError = $_
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'T21_STAGE_LEASE_MUTATION_ID',
            $previousMutationId)
        try {
            Invoke-MigrationStageLeaseOperation `
                -Mode CompleteMutation `
                -ServerName $ServerName `
                -DatabaseName $DatabaseName `
                -LeasePolicy $LeasePolicy `
                -Resource $Resource `
                -Stage $Stage `
                -TargetFingerprint $TargetFingerprint `
                -AuthorizationEvidenceSha256 $AuthorizationEvidenceSha256 `
                -Holder $Holder `
                -ExpectedFenceToken $ExpectedFenceToken `
                -MutationId $mutationId `
                -BundleRoot $BundleRoot `
                -ExpectedApplicationSha256 $ExpectedApplicationSha256 `
                -EnableLocalTestSeams:$EnableLocalTestSeams `
                -LocalAccessTokenProvider $LocalAccessTokenProvider `
                -LocalSqlExecutor $LocalSqlExecutor | Out-Null
        }
        catch {
            $completionError = $_
        }
    }
    if ($null -ne $mutationError) {
        if ($null -ne $completionError) {
            $mutationError.Exception.Data['MutationCompletionFailure'] =
                $completionError.Exception.Message
        }
        throw $mutationError
    }
    if ($null -ne $completionError) {
        throw $completionError
    }

    return [pscustomobject]@{
        Lease = $validatedLease
        Result = $mutationResult
    }
}
