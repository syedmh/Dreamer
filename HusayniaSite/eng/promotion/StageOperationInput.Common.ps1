Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
. (Join-Path $PSScriptRoot 'StageTarget.Common.ps1')

function Get-StageOperationInputProperty {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-StageOperationInputInteger {
    param($Value, [int64]$Minimum, [int64]$Maximum = [int64]::MaxValue)

    if ($null -eq $Value -or $Value -is [bool] -or
        ($Value -isnot [sbyte] -and
         $Value -isnot [byte] -and
         $Value -isnot [int16] -and
         $Value -isnot [uint16] -and
         $Value -isnot [int32] -and
         $Value -isnot [uint32] -and
         $Value -isnot [int64])) {
        return $false
    }
    $number = [int64]$Value
    return $number -ge $Minimum -and $number -le $Maximum
}

function ConvertFrom-StageOperationInputUtc {
    param([string]$Value, [string]$Label, [int]$MaximumAgeHours = 24)

    $parsed = [DateTimeOffset]::MinValue
    $now = [DateTimeOffset]::UtcNow
    if ([string]::IsNullOrWhiteSpace($Value) -or
        -not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero -or
        $parsed -gt $now.AddMinutes(5) -or
        $parsed -lt $now.AddHours(-$MaximumAgeHours)) {
        throw "$Label is missing, stale, in the future, or not UTC."
    }
    return $parsed
}

function Assert-StageOperationInputUri {
    param(
        [string]$Value,
        [string]$ExpectedHost,
        [int]$ExpectedPort = 443,
        [string]$RequiredPath = '',
        [switch]$AllowPath
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        $uri.Host -cne $ExpectedHost -or
        $uri.Port -ne $ExpectedPort -or
        $uri.Host -match '[^\x00-\x7f]' -or
        $uri.IdnHost -cne $uri.Host -or
        $uri.Host -match '(?i)(?:^|\.)xn--' -or
        $ExpectedHost -match '[^\x00-\x7f]' -or
        $ExpectedHost -match '(?i)(?:^|\.)xn--' -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        (-not $AllowPath -and [string]::IsNullOrWhiteSpace($RequiredPath) -and $uri.AbsolutePath -cne '/') -or
        (-not [string]::IsNullOrWhiteSpace($RequiredPath) -and $uri.AbsolutePath -cne $RequiredPath)) {
        throw 'Read-only probe URI is not the exact protected HTTPS target.'
    }
    return $uri
}

function Test-StageOperationDisallowedAddress {
    param([Net.IPAddress]$Address)

    if ($Address.IsIPv4MappedToIPv6) {
        $Address = $Address.MapToIPv4()
    }
    if ([Net.IPAddress]::IsLoopback($Address) -or
        $Address.Equals([Net.IPAddress]::Any) -or
        $Address.Equals([Net.IPAddress]::IPv6Any) -or
        $Address.IsIPv6LinkLocal -or
        $Address.IsIPv6SiteLocal -or
        $Address.IsIPv6Multicast) {
        return $true
    }

    $bytes = $Address.GetAddressBytes()
    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
        return (
            $bytes[0] -eq 0 -or
            $bytes[0] -eq 10 -or
            $bytes[0] -eq 127 -or
            ($bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127) -or
            ($bytes[0] -eq 169 -and $bytes[1] -eq 254) -or
            ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
            ($bytes[0] -eq 192 -and $bytes[1] -eq 168) -or
            ($bytes[0] -eq 198 -and $bytes[1] -in 18, 19) -or
            $bytes[0] -ge 224)
    }
    return (($bytes[0] -band 0xfe) -eq 0xfc)
}

function Test-StageOperationPrivateEndpointApproved {
    param(
        [Uri]$Uri,
        [object[]]$ApprovedPrivateEndpoints
    )

    foreach ($endpoint in @($ApprovedPrivateEndpoints)) {
        if ([string](Get-StageOperationInputProperty $endpoint 'host') -ceq $Uri.Host -and
            (Test-StageOperationInputInteger `
                -Value (Get-StageOperationInputProperty $endpoint 'port') `
                -Minimum 1 `
                -Maximum 65535) -and
            [int](Get-StageOperationInputProperty $endpoint 'port') -eq $Uri.Port) {
            return $true
        }
    }
    return $false
}

function Get-StageOperationHttpPolicy {
    param($Policy)

    $httpPolicy = Get-StageOperationInputProperty $Policy 'httpProbePolicy'
    Assert-StageTargetExactProperties -Object $httpPolicy -Label 'HTTP probe policy' -Expected @(
        'connectTimeoutMilliseconds',
        'requestTimeoutMilliseconds',
        'maxResponseBytes',
        'maxJsonDepth'
    )
    if (-not (Test-StageOperationInputInteger `
            -Value $httpPolicy.connectTimeoutMilliseconds -Minimum 1 -Maximum 30000) -or
        -not (Test-StageOperationInputInteger `
            -Value $httpPolicy.requestTimeoutMilliseconds -Minimum 1 -Maximum 120000) -or
        -not (Test-StageOperationInputInteger `
            -Value $httpPolicy.maxResponseBytes -Minimum 1 -Maximum 16777216) -or
        -not (Test-StageOperationInputInteger `
            -Value $httpPolicy.maxJsonDepth -Minimum 1 -Maximum 128) -or
        [int64]$httpPolicy.requestTimeoutMilliseconds -lt
            [int64]$httpPolicy.connectTimeoutMilliseconds) {
        throw 'HTTP probe policy contains invalid or unsafe resource bounds.'
    }
    return $httpPolicy
}

function Resolve-StageOperationPinnedAddress {
    param(
        [Uri]$Uri,
        [object[]]$ApprovedPrivateEndpoints,
        [scriptblock]$DnsResolver,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )

    $addresses = [Collections.Generic.List[Net.IPAddress]]::new()
    $literalAddress = [Net.IPAddress]::None
    $CancellationToken.ThrowIfCancellationRequested()
    if ([Net.IPAddress]::TryParse($Uri.Host, [ref]$literalAddress)) {
        $addresses.Add($literalAddress)
    }
    else {
        try {
            $resolution = if ($null -eq $DnsResolver) {
                [Net.Dns]::GetHostAddressesAsync($Uri.DnsSafeHost, $CancellationToken)
            }
            else {
                & $DnsResolver $Uri.DnsSafeHost $CancellationToken
            }
            $resolved = if ($resolution -is [Threading.Tasks.Task]) {
                @($resolution.GetAwaiter().GetResult())
            }
            else {
                @($resolution)
            }
            $CancellationToken.ThrowIfCancellationRequested()
            foreach ($address in $resolved) {
                if ($address -isnot [Net.IPAddress]) {
                    throw 'Trusted service probe DNS resolver returned a non-IP address.'
                }
                $addresses.Add($address)
            }
        }
        catch [OperationCanceledException] {
            throw
        }
        catch {
            throw 'Trusted service probe host DNS resolution failed.'
        }
        if ($addresses.Count -eq 0) {
            throw 'Trusted service probe host did not resolve to an address.'
        }
    }

    if (@($addresses | Where-Object { Test-StageOperationDisallowedAddress -Address $_ }).Count -gt 0 -and
        -not (Test-StageOperationPrivateEndpointApproved `
            -Uri $Uri `
            -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints)) {
        throw 'Trusted service probe resolved to a loopback, private, link-local, metadata, or reserved destination.'
    }
    return $addresses[0]
}

function Assert-StageOperationServiceDestination {
    param(
        [Uri]$Uri,
        [object[]]$ApprovedPrivateEndpoints,
        [switch]$ResolveDns
    )

    $literalAddress = [Net.IPAddress]::None
    if ($ResolveDns -or [Net.IPAddress]::TryParse($Uri.Host, [ref]$literalAddress)) {
        $null = Resolve-StageOperationPinnedAddress `
            -Uri $Uri `
            -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints
    }
}

function Initialize-StageOperationHttpConnectionType {
    if ($null -ne ('Husaynia.Promotion.StageOperationHttpConnection' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Husaynia.Promotion
{
    public static class StageOperationHttpConnection
    {
        public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(
            IPAddress pinnedAddress,
            string expectedHost,
            int expectedPort,
            int connectTimeoutMilliseconds,
            Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connector)
        {
            if (pinnedAddress == null) throw new ArgumentNullException(nameof(pinnedAddress));
            if (string.IsNullOrWhiteSpace(expectedHost)) throw new ArgumentException("Host is required.", nameof(expectedHost));
            if (expectedPort < 1 || expectedPort > 65535) throw new ArgumentOutOfRangeException(nameof(expectedPort));
            if (connectTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
            connector ??= ConnectSocketAsync;

            return async (context, cancellationToken) =>
            {
                if (!string.Equals(
                        context.DnsEndPoint.Host,
                        expectedHost,
                        StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != expectedPort)
                {
                    throw new HttpRequestException(
                        "HTTP connection callback received an unexpected host or port.");
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(connectTimeoutMilliseconds);
                try
                {
                    return await connector(
                        pinnedAddress,
                        expectedPort,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("HTTP probe connection exceeded the configured timeout.");
                }
            };
        }

        private static async ValueTask<Stream> ConnectSocketAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
'@
}

function New-StageOperationConnectCallback {
    param(
        [Parameter(Mandatory = $true)]
        [Net.IPAddress]$PinnedAddress,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedHost,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedPort,
        [Parameter(Mandatory = $true)]
        [int]$ConnectTimeoutMilliseconds,
        [Func[
            Net.IPAddress,
            int,
            Threading.CancellationToken,
            Threading.Tasks.ValueTask[IO.Stream]
        ]]$Connector
    )

    Initialize-StageOperationHttpConnectionType
    return [Husaynia.Promotion.StageOperationHttpConnection]::Create(
        $PinnedAddress,
        $ExpectedHost,
        $ExpectedPort,
        $ConnectTimeoutMilliseconds,
        $Connector)
}

function Read-StageOperationHttpResponseBody {
    param(
        [Parameter(Mandatory = $true)]
        [Net.Http.HttpResponseMessage]$Response,
        [Parameter(Mandatory = $true)]
        [int]$MaxResponseBytes,
        [Parameter(Mandatory = $true)]
        [Threading.CancellationToken]$CancellationToken
    )

    if (@($Response.Content.Headers.ContentEncoding).Count -ne 0) {
        throw 'HTTP probe response uses an unsupported content encoding.'
    }
    $contentLength = $Response.Content.Headers.ContentLength
    if ($null -ne $contentLength -and [int64]$contentLength -gt $MaxResponseBytes) {
        throw 'HTTP probe response Content-Length exceeds the configured byte limit.'
    }

    $stream = $Response.Content.ReadAsStreamAsync($CancellationToken).GetAwaiter().GetResult()
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new([Math]::Min(8192, $MaxResponseBytes + 1))
        $total = 0
        while ($true) {
            $read = $stream.ReadAsync(
                $buffer,
                0,
                $buffer.Length,
                $CancellationToken
            ).GetAwaiter().GetResult()
            if ($read -eq 0) {
                break
            }
            if ($total + $read -gt $MaxResponseBytes) {
                throw 'HTTP probe response body exceeds the configured byte limit.'
            }
            $memory.Write($buffer, 0, $read)
            $total += $read
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
    }
    finally {
        $stream.Dispose()
        $memory.Dispose()
    }
}

function ConvertFrom-StageOperationBoundedJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [int]$MaxJsonDepth,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )

    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = $MaxJsonDepth
    try {
        $CancellationToken.ThrowIfCancellationRequested()
        $document = [Text.Json.JsonDocument]::Parse($Value, $options)
        $document.Dispose()
        $CancellationToken.ThrowIfCancellationRequested()
        $result = $Value | ConvertFrom-Json -DateKind String -Depth $MaxJsonDepth
        $CancellationToken.ThrowIfCancellationRequested()
        return $result
    }
    catch [OperationCanceledException] {
        throw
    }
    catch {
        throw 'Trusted service probe response body is not valid JSON or exceeds the configured depth limit.'
    }
}

function Invoke-StageOperationPinnedHttpGet {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$Uri,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$ApprovedPrivateEndpoints,
        [Parameter(Mandatory = $true)]
        $HttpPolicy,
        [string]$Accept = '*/*',
        [string]$BearerToken,
        [switch]$ParseJson,
        [scriptblock]$DnsResolver
    )

    $timeout = [Threading.CancellationTokenSource]::new()
    $timeout.CancelAfter([int]$HttpPolicy.requestTimeoutMilliseconds)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $handler = $null
    $client = $null
    $request = $null
    try {
        $pinnedAddress = Resolve-StageOperationPinnedAddress `
            -Uri $Uri `
            -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints `
            -DnsResolver $DnsResolver `
            -CancellationToken $timeout.Token
        $handler = [Net.Http.SocketsHttpHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
        $handler.ConnectTimeout = [TimeSpan]::FromMilliseconds(
            [int]$HttpPolicy.connectTimeoutMilliseconds)
        $handler.MaxConnectionsPerServer = 1
        $handler.MaxResponseHeadersLength = 32
        $handler.PooledConnectionIdleTimeout = [TimeSpan]::Zero
        $handler.UseCookies = $false
        $handler.ConnectCallback = New-StageOperationConnectCallback `
            -PinnedAddress $pinnedAddress `
            -ExpectedHost $Uri.DnsSafeHost `
            -ExpectedPort $Uri.Port `
            -ConnectTimeoutMilliseconds ([int]$HttpPolicy.connectTimeoutMilliseconds)
        $client = [Net.Http.HttpClient]::new($handler, $true)
        $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
        $request.Headers.Host = $Uri.Host
        $request.Headers.Accept.ParseAdd($Accept)
        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            if ($BearerToken.IndexOfAny([char[]]"`r`n") -ge 0) {
                throw 'HTTP probe bearer token is malformed.'
            }
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new(
                'Bearer',
                $BearerToken)
        }
        $response = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $timeout.Token
        ).GetAwaiter().GetResult()
        try {
            $body = Read-StageOperationHttpResponseBody `
                -Response $response `
                -MaxResponseBytes ([int]$HttpPolicy.maxResponseBytes) `
                -CancellationToken $timeout.Token
            $jsonBody = $null
            if ($ParseJson) {
                Assert-StageOperationJsonContentType -Value ([string]$response.Content.Headers.ContentType)
                $jsonBody = ConvertFrom-StageOperationBoundedJson `
                    -Value $body `
                    -MaxJsonDepth ([int]$HttpPolicy.maxJsonDepth) `
                    -CancellationToken $timeout.Token
            }
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Body = $body
                JsonBody = $jsonBody
                FinalUri = $request.RequestUri.AbsoluteUri
                Location = [string]$response.Headers.Location
                ContentType = [string]$response.Content.Headers.ContentType
                DurationMilliseconds = [int64]$stopwatch.ElapsedMilliseconds
                PinnedAddress = $pinnedAddress.ToString()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    catch [OperationCanceledException] {
        if ($timeout.IsCancellationRequested) {
            throw 'HTTP probe exceeded the configured overall request timeout.'
        }
        throw
    }
    finally {
        $stopwatch.Stop()
        $timeout.Dispose()
        if ($null -ne $request) {
            $request.Dispose()
        }
        if ($null -ne $client) {
            $client.Dispose()
        }
        elseif ($null -ne $handler) {
            $handler.Dispose()
        }
    }
}

function Assert-StageOperationJsonContentType {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -notmatch '(?i)^application/(?:json|[a-z0-9!#$&^_.+-]+\+json)(?:\s*;|$)') {
        throw 'Trusted service probe response must use a JSON content type.'
    }
}

function Assert-StageOperationServiceJson {
    param($Value)

    if ($null -eq $Value -or
        $Value -is [string] -or
        $Value -is [ValueType] -or
        [string](Get-StageOperationInputProperty $Value 'schemaVersion') -cne '1.0.0') {
        throw 'Trusted service probe JSON does not match the required schema.'
    }
    return $Value
}

function Assert-StageOperationRoutePath {
    param([string]$Value, [string]$Label)

    if ([string]::IsNullOrEmpty($Value) -or
        $Value -notmatch '^/(?!/)(?:[^?#]*)$' -or
        -not $Value.IsNormalized([Text.NormalizationForm]::FormC)) {
        throw "$Label must be a host-free, query-free NFC-normalized Unicode route path."
    }
    return $Value
}

function New-StageOperationRouteUri {
    param(
        [Uri]$Origin,
        [string]$Path,
        [string]$Label
    )

    $null = Assert-StageOperationRoutePath -Value $Path -Label $Label
    $builder = [UriBuilder]::new($Origin)
    $builder.Path = $Path
    $builder.Query = ''
    $builder.Fragment = ''
    $uri = $builder.Uri
    if ($uri.Scheme -cne $Origin.Scheme -or
        $uri.Host -cne $Origin.Host -or
        $uri.Port -ne $Origin.Port -or
        -not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw "$Label escaped the approved stage origin."
    }
    return $uri
}

function Resolve-StageOperationRedirectLocation {
    param(
        [Uri]$RequestUri,
        [string]$Location,
        [Uri]$ApprovedOrigin,
        [string]$ExpectedPath
    )

    if ([string]::IsNullOrWhiteSpace($Location) -or
        $Location.IndexOfAny([char[]]"`r`n") -ge 0) {
        throw 'Redirect Location is missing or malformed.'
    }
    $resolved = $null
    if (-not [Uri]::TryCreate($RequestUri, $Location, [ref]$resolved) -or
        -not $resolved.IsAbsoluteUri -or
        $resolved.Scheme -cne $ApprovedOrigin.Scheme -or
        $resolved.Host -cne $ApprovedOrigin.Host -or
        $resolved.Port -ne $ApprovedOrigin.Port -or
        -not [string]::IsNullOrEmpty($resolved.UserInfo) -or
        -not [string]::IsNullOrEmpty($resolved.Query) -or
        -not [string]::IsNullOrEmpty($resolved.Fragment)) {
        throw 'Redirect Location changed the approved stage scheme, host, or port.'
    }

    $expected = New-StageOperationRouteUri -Origin $ApprovedOrigin -Path $ExpectedPath -Label 'Redirect target'
    if ($resolved.AbsoluteUri -cne $expected.AbsoluteUri) {
        throw 'Redirect Location does not exactly match the manifest redirect target.'
    }
    return $resolved
}

function Read-StageOperationFixture {
    param([string]$FixtureRoot, [string]$FileName)

    if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
        throw 'An explicit local fixture root is required for fixture reads.'
    }
    $root = (Resolve-Path -LiteralPath $FixtureRoot).Path
    $candidate = Join-Path $root $FileName
    $path = (Resolve-Path -LiteralPath $candidate).Path
    $relative = [IO.Path]::GetRelativePath($root, $path)
    if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..') -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Local stage-operation fixture escaped its explicit fixture root.'
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
}

function Invoke-StageOperationServiceGet {
    param(
        [string]$Uri,
        [string]$ExpectedHost,
        [string]$TokenEnvironmentVariable,
        [string]$FixtureRoot,
        [string]$FixtureFile,
        [object[]]$ApprovedPrivateEndpoints,
        $HttpPolicy
    )

    $requestedUri = Assert-StageOperationInputUri -Value $Uri -ExpectedHost $ExpectedHost -AllowPath
    if (-not [string]::IsNullOrWhiteSpace($FixtureRoot)) {
        Assert-StageOperationServiceDestination `
            -Uri $requestedUri `
            -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints
        $fixture = Read-StageOperationFixture -FixtureRoot $FixtureRoot -FileName $FixtureFile
        $probeResponse = Get-StageOperationInputProperty $fixture 'probeResponse'
        if ($null -eq $probeResponse) {
            return Assert-StageOperationServiceJson -Value $fixture
        }
        $statusCode = Get-StageOperationInputProperty $probeResponse 'statusCode'
        if (-not (Test-StageOperationInputInteger -Value $statusCode -Minimum 100 -Maximum 599) -or
            ([int]$statusCode -ge 300 -and [int]$statusCode -le 399)) {
            throw 'Trusted service probe rejected a redirect or malformed HTTP status.'
        }
        if ([int]$statusCode -lt 200 -or [int]$statusCode -gt 299) {
            throw 'Trusted service probe did not return a successful HTTP status.'
        }
        Assert-StageOperationJsonContentType `
            -Value ([string](Get-StageOperationInputProperty $probeResponse 'contentType'))
        return Assert-StageOperationServiceJson `
            -Value (Get-StageOperationInputProperty $probeResponse 'body')
    }

    $token = [Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Read-only stage probe token is missing: $TokenEnvironmentVariable"
    }
    $response = Invoke-StageOperationPinnedHttpGet `
        -Uri $requestedUri `
        -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints `
        -HttpPolicy $HttpPolicy `
        -Accept 'application/json' `
        -BearerToken $token `
        -ParseJson
    $statusCode = [int]$response.StatusCode
    if ($statusCode -ge 300 -and $statusCode -le 399) {
        throw 'Trusted service probe rejected an HTTP redirect.'
    }
    if ($statusCode -lt 200 -or $statusCode -gt 299) {
        throw "Trusted service probe failed with HTTP status $statusCode."
    }
    if ([string]$response.FinalUri -cne $requestedUri.AbsoluteUri) {
        throw 'Trusted service probe response URI changed from the approved protected endpoint.'
    }
    return Assert-StageOperationServiceJson -Value $response.JsonBody
}

function Invoke-StageOperationWebGet {
    param(
        [string]$Uri,
        [string]$ExpectedHost,
        [int]$ExpectedPort = 443,
        [string]$FixtureRoot,
        [object[]]$ApprovedPrivateEndpoints,
        $HttpPolicy
    )

    $requestedUri = Assert-StageOperationInputUri -Value $Uri -ExpectedHost $ExpectedHost `
        -ExpectedPort $ExpectedPort -AllowPath
    if (-not [string]::IsNullOrWhiteSpace($FixtureRoot)) {
        Assert-StageOperationServiceDestination `
            -Uri $requestedUri `
            -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints
        $fixtures = @(Read-StageOperationFixture -FixtureRoot $FixtureRoot -FileName 'http-probes.json')
        $matches = @($fixtures | Where-Object { [string]$_.url -ceq $Uri })
        if ($matches.Count -ne 1 -or
            -not (Test-StageOperationInputInteger -Value $matches[0].statusCode -Minimum 100 -Maximum 599)) {
            throw "Local HTTP probe fixture is missing or duplicated: $Uri"
        }
        $finalUriValue = [string](Get-StageOperationInputProperty $matches[0] 'finalUrl')
        if ([string]::IsNullOrWhiteSpace($finalUriValue)) {
            $finalUriValue = $Uri
        }
        $finalUri = Assert-StageOperationInputUri -Value $finalUriValue -ExpectedHost $ExpectedHost `
            -ExpectedPort $ExpectedPort -AllowPath
        if ($finalUri.AbsoluteUri -cne $requestedUri.AbsoluteUri) {
            throw 'HTTP probe followed or reported a redirect instead of preserving the requested stage URI.'
        }
        $durationValue = Get-StageOperationInputProperty $matches[0] 'durationMilliseconds'
        $durationMilliseconds = if ($null -eq $durationValue) {
            1
        }
        elseif (Test-StageOperationInputInteger -Value $durationValue -Minimum 0) {
            [int64]$durationValue
        }
        else {
            throw "Local HTTP probe fixture has an invalid duration: $Uri"
        }
        return [pscustomobject]@{
            StatusCode = [int]$matches[0].statusCode
            Body = [string]$matches[0].body
            FinalUri = $finalUri.AbsoluteUri
            Location = [string](Get-StageOperationInputProperty $matches[0] 'location')
            DurationMilliseconds = $durationMilliseconds
        }
    }

    $response = Invoke-StageOperationPinnedHttpGet `
        -Uri $requestedUri `
        -ApprovedPrivateEndpoints $ApprovedPrivateEndpoints `
        -HttpPolicy $HttpPolicy
    $null = Assert-StageOperationInputUri -Value ([string]$response.FinalUri) -ExpectedHost $ExpectedHost `
        -ExpectedPort $ExpectedPort -AllowPath
    if ([string]$response.FinalUri -cne $requestedUri.AbsoluteUri) {
        throw 'HTTP client followed a redirect instead of preserving the requested stage URI.'
    }
    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Body = [string]$response.Body
        FinalUri = [string]$response.FinalUri
        Location = [string]$response.Location
        DurationMilliseconds = [int64]$response.DurationMilliseconds
    }
}

function Assert-StageOperationVerifiedProvenanceProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "Stage-operation release verified provenance $Label does not exactly match the v2 contract."
    }
}

function Get-StageOperationVerifiedRelease {
    param(
        [string]$ReleaseVerifiedProvenancePath,
        $Policy
    )

    if ([string]::IsNullOrWhiteSpace($ReleaseVerifiedProvenancePath) -or
        -not (Test-Path -LiteralPath $ReleaseVerifiedProvenancePath -PathType Leaf)) {
        throw 'Stage-operation inputs require release-c6 verified provenance.'
    }
    $provenancePath = (Resolve-Path -LiteralPath $ReleaseVerifiedProvenancePath).Path
    if ([IO.Path]::GetFileName($provenancePath) -cne 'verified-provenance.json') {
        throw 'Stage-operation inputs require consumer-created release-c6 verified-provenance.json.'
    }
    $resolverOutputRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $provenancePath)).Path
    $extractedRoot = Join-Path $resolverOutputRoot 'artifact'
    if (-not (Test-Path -LiteralPath $extractedRoot -PathType Container)) {
        throw 'Stage-operation release-c6 resolver artifact root is missing.'
    }
    $extractedRoot = (Resolve-Path -LiteralPath $extractedRoot).Path
    if (-not (Test-PathWithinDirectory -BasePath $resolverOutputRoot -Path $extractedRoot)) {
        throw 'Stage-operation release-c6 artifact root escaped the resolver output.'
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -DateKind String
    Assert-StageOperationVerifiedProvenanceProperties -Value $provenance -Expected @(
        'schemaVersion', 'authority', 'expectedRole', 'run', 'artifact', 'contentManifestSha256', 'attestation'
    ) -Label 'document'
    Assert-StageOperationVerifiedProvenanceProperties -Value $provenance.run -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label 'run'
    Assert-StageOperationVerifiedProvenanceProperties -Value $provenance.artifact -Expected @(
        'id', 'name', 'archiveSha256', 'sizeBytes', 'createdAtUtc', 'expiresAtUtc'
    ) -Label 'artifact'

    $releaseRole = @($Policy.t21ProvenanceContract.producerRoles | Where-Object {
        [string]$_.role -ceq 'release-c6'
    })
    $createdAt = [DateTimeOffset]::MinValue
    $expiresAt = [DateTimeOffset]::MinValue
    $hasExpiry = -not [string]::IsNullOrWhiteSpace([string]$provenance.artifact.expiresAtUtc)
    if ([string]$Policy.t21ProvenanceContract.schemaVersion -cne '2.1.0' -or
        [string]$Policy.t21ProvenanceContract.repository -cne [string]$Policy.provenance.repository -or
        [string]$Policy.t21ProvenanceContract.protectedRef -cne [string]$Policy.provenance.protectedRef -or
        $releaseRole.Count -ne 1 -or
        [string]$releaseRole[0].workflowPath -cne [string]$Policy.provenance.releaseWorkflowPath -or
        [bool]$releaseRole[0].requiresAttestation -or
        -not [bool]$releaseRole[0].requiresCompletedSuccess -or
        [bool]$releaseRole[0].forbidden -or
        [string]$provenance.schemaVersion -cne '2.0.0' -or
        [string]$provenance.authority -cne 'github-actions-api-and-sigstore-v1' -or
        [string]$provenance.expectedRole -cne 'release-c6' -or
        [string]$provenance.run.repository -cne [string]$Policy.provenance.repository -or
        [string]$provenance.run.workflowPath -cne [string]$Policy.provenance.releaseWorkflowPath -or
        [string]$provenance.run.workflowRef -cne
            "$([string]$Policy.provenance.repository)/$([string]$Policy.provenance.releaseWorkflowPath)@$([string]$Policy.provenance.protectedRef)" -or
        [string]$provenance.run.runId -notmatch '^[1-9][0-9]*$' -or
        -not (Test-StageOperationInputInteger -Value $provenance.run.runAttempt -Minimum 1) -or
        [string]$provenance.run.ref -cne [string]$Policy.provenance.protectedRef -or
        [string]$provenance.run.commitSha -notmatch '^[a-f0-9]{40}$' -or
        [string]$provenance.run.status -cne 'completed' -or
        [string]$provenance.run.conclusion -cne 'success' -or
        [string]$provenance.artifact.id -notmatch '^[1-9][0-9]*$' -or
        [string]$provenance.artifact.name -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$' -or
        [string]$provenance.artifact.archiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        -not (Test-StageOperationInputInteger -Value $provenance.artifact.sizeBytes -Minimum 1) -or
        -not [DateTimeOffset]::TryParse([string]$provenance.artifact.createdAtUtc, [ref]$createdAt) -or
        $createdAt.Offset -ne [TimeSpan]::Zero -or
        $createdAt -gt [DateTimeOffset]::UtcNow.AddMinutes([int]$Policy.provenance.maxClockSkewMinutes) -or
        ($hasExpiry -and (
            -not [DateTimeOffset]::TryParse([string]$provenance.artifact.expiresAtUtc, [ref]$expiresAt) -or
            $expiresAt.Offset -ne [TimeSpan]::Zero -or
            $expiresAt -le $createdAt -or
            $expiresAt -le [DateTimeOffset]::UtcNow
        )) -or
        [string]$provenance.contentManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        $null -ne $provenance.attestation) {
        throw 'Stage-operation release-c6 verified provenance has invalid API, artifact, or role bindings.'
    }

    $releaseRoots = @(
        Get-ChildItem -LiteralPath $extractedRoot -Directory -Force |
            Where-Object {
                $_.Name -ceq [string]$provenance.artifact.name -and
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0
            }
    )
    if ($releaseRoots.Count -ne 1 -or
        @(Get-ChildItem -LiteralPath $extractedRoot -Force).Count -ne 1 -or
        -not (Test-PathWithinDirectory -BasePath $extractedRoot -Path $releaseRoots[0].FullName)) {
        throw 'Stage-operation release C6 root is not uniquely resolver-owned and API-bound.'
    }
    $releaseManifestPath = Join-Path $releaseRoots[0].FullName 'release\release-manifest.json'
    if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf) -or
        (Get-Sha256Lower -Path $releaseManifestPath) -cne [string]$provenance.contentManifestSha256) {
        throw 'Stage-operation release-c6 verified provenance content manifest does not bind the resolved C6 root.'
    }
    return [pscustomobject]@{
        Provenance = $provenance
        ArtifactRoot = $releaseRoots[0].FullName
    }
}

function Get-StageOperationSourceRelease {
    param($VerifiedProvenance)

    return [ordered]@{
        schemaVersion = [string]$VerifiedProvenance.schemaVersion
        authority = [string]$VerifiedProvenance.authority
        expectedRole = [string]$VerifiedProvenance.expectedRole
        run = [ordered]@{
            repository = [string]$VerifiedProvenance.run.repository
            workflowPath = [string]$VerifiedProvenance.run.workflowPath
            workflowRef = [string]$VerifiedProvenance.run.workflowRef
            runId = [string]$VerifiedProvenance.run.runId
            runAttempt = [int]$VerifiedProvenance.run.runAttempt
            ref = [string]$VerifiedProvenance.run.ref
            commitSha = [string]$VerifiedProvenance.run.commitSha
            status = [string]$VerifiedProvenance.run.status
            conclusion = [string]$VerifiedProvenance.run.conclusion
        }
        artifact = [ordered]@{
            id = [string]$VerifiedProvenance.artifact.id
            name = [string]$VerifiedProvenance.artifact.name
            archiveSha256 = [string]$VerifiedProvenance.artifact.archiveSha256
            sizeBytes = [int64]$VerifiedProvenance.artifact.sizeBytes
            createdAtUtc = [string]$VerifiedProvenance.artifact.createdAtUtc
            expiresAtUtc = [string]$VerifiedProvenance.artifact.expiresAtUtc
        }
        contentManifestSha256 = [string]$VerifiedProvenance.contentManifestSha256
    }
}

function Assert-StageOperationAuthorizationContextProperties {
    param($Value, [string[]]$Expected, [string]$Label)

    if ($null -eq $Value -or @(Compare-Object ($Expected | Sort-Object) @(
            $Value.PSObject.Properties.Name | Sort-Object)).Count -ne 0) {
        throw "$Label does not exactly match the CTO authorization context v2 contract."
    }
}

function Test-StageOperationAuthorizationRunBinding {
    param($Actual, $Expected)

    Assert-StageOperationAuthorizationContextProperties -Value $Actual -Expected @(
        'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
    ) -Label 'Production authorization context release run'
    return [string]$Actual.repository -ceq [string]$Expected.repository -and
        [string]$Actual.workflowPath -ceq [string]$Expected.workflowPath -and
        [string]$Actual.workflowRef -ceq [string]$Expected.workflowRef -and
        [string]$Actual.runId -ceq [string]$Expected.runId -and
        [int]$Actual.runAttempt -eq [int]$Expected.runAttempt -and
        [string]$Actual.ref -ceq [string]$Expected.ref -and
        [string]$Actual.commitSha -ceq [string]$Expected.commitSha -and
        [string]$Actual.status -ceq [string]$Expected.status -and
        [string]$Actual.conclusion -ceq [string]$Expected.conclusion
}

function Get-StageOperationInputContext {
    param(
        [string]$Stage,
        [string]$ReleaseVerifiedProvenancePath,
        [string]$ExpectedAppSha256,
        [string]$StageTargetMetadataPath,
        [string]$ProductionAuthorizationContextPath,
        [string]$PolicyPath
    )

    $repositoryRoot = Resolve-HusayniaRepositoryRoot
    if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
        $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
    }
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
    $stagePolicy = @($policy.stages | Where-Object { $_.name -eq $Stage })
    if ($stagePolicy.Count -ne 1) {
        throw 'Stage-operation input production requires one reviewed stage.'
    }
    $stagePolicy = Assert-StageDeploymentEnabled -Policy $policy -Stage $Stage `
        -Operation 'Stage-operation report production'
    $target = Read-StageTargetMetadata -Path $StageTargetMetadataPath -Stage $Stage -StagePolicy $stagePolicy
    $releaseVerified = Get-StageOperationVerifiedRelease `
        -ReleaseVerifiedProvenancePath $ReleaseVerifiedProvenancePath -Policy $policy
    $artifact = (Resolve-Path -LiteralPath $releaseVerified.ArtifactRoot).Path
    $manifestPath = Join-Path $artifact 'release\release-manifest.json'
    $appPath = Join-Path $artifact 'app\Husaynia.Web.zip'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw 'Immutable release manifest or application archive is missing.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $actualAppSha256 = Get-Sha256Lower -Path $appPath
    $appEntry = @($manifest.files | Where-Object { $_.path -ceq 'app/Husaynia.Web.zip' })
    if ($actualAppSha256 -cne $ExpectedAppSha256.ToLowerInvariant() -or
        $appEntry.Count -ne 1 -or
        [string]$appEntry[0].sha256 -cne $actualAppSha256 -or
        -not (Test-SafeVersion -Version ([string]$manifest.version)) -or
        (Split-Path -Leaf $artifact) -cne "husaynia-site-$($manifest.version)" -or
        -not (Test-CommitSha -Value ([string]$manifest.commitSha))) {
        throw 'Stage-operation report cannot bind the immutable release identity.'
    }
    $authorizationContext = $null
    if ($Stage -eq 'Production' -and -not [string]::IsNullOrWhiteSpace($ProductionAuthorizationContextPath)) {
        if ([Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -ne 'true' -or
            -not (Test-Path -LiteralPath $ProductionAuthorizationContextPath -PathType Leaf)) {
            throw 'Production authorization context path is not a protected workflow materialization.'
        }
        $authorizationContext = Get-Content -LiteralPath $ProductionAuthorizationContextPath -Raw |
            ConvertFrom-Json -DateKind String
        Assert-StageOperationAuthorizationContextProperties -Value $authorizationContext -Expected @(
            'schemaVersion',
            'status',
            'stage',
            'environment',
            'applicationSha256',
            'releaseManifestSha256',
            'releaseCommitSha',
            'sourceRelease',
            'bundlePath',
            'bundleSha256',
            'targetFingerprint',
            'preflightEvidenceSha256',
            'preflightProducerRun',
            'preparedInputProducerRun',
            'producerBindings',
            'authorizedExecution',
            'approvalReference',
            'authorizedByActor',
            'authorizedByActorId',
            'expiresAtUtc',
            'ctoAuthorizationSha256',
            'ctoAuthorizationProvenanceSha256',
            'sourceChangeRecordSha256',
            'materializedAtUtc'
        ) -Label 'Production stage-operation authorization context'
        Assert-StageOperationAuthorizationContextProperties -Value $authorizationContext.authorizedExecution `
            -Expected @('topLevelCallerWorkflowRef', 'producerWorkflowRef') `
            -Label 'Production authorization context execution'
        $preflightRun = $authorizationContext.preflightProducerRun
        Assert-StageOperationAuthorizationContextProperties -Value $preflightRun -Expected @(
            'repository', 'workflowPath', 'workflowRef', 'runId', 'runAttempt', 'ref', 'commitSha', 'status', 'conclusion'
        ) -Label 'Production authorization context preflight run'
        $contract = $policy.t21ProvenanceContract
        $trustedExecution = $policy.trustedExecutionContract
        $trustedPreflightRole = @($contract.producerRoles | Where-Object {
            [string]$_.role -ceq 'trusted-preflight'
        })
        $productionCaller = @($contract.callerMatrix | Where-Object {
            [string]$_.stage -ceq 'Production'
        })
        $bundlePath = 'operations/protected-execution-bundle.zip'
        $bundleEntry = @($manifest.files | Where-Object { [string]$_.path -ceq $bundlePath })
        $bundleFile = Join-Path $artifact $bundlePath
        $contextExpiry = [DateTimeOffset]::MinValue
        $materializedAt = [DateTimeOffset]::MinValue
        $now = [DateTimeOffset]::UtcNow
        if (-not [bool]$stagePolicy.deploymentEnabled -or
            [string]$contract.schemaVersion -cne '2.1.0' -or
            [string]$contract.repository -cne [string]$policy.provenance.repository -or
            [string]$contract.protectedRef -cne [string]$policy.provenance.protectedRef -or
            $trustedPreflightRole.Count -ne 1 -or
            $productionCaller.Count -ne 1 -or
            [string]$trustedPreflightRole[0].workflowPath -cne
                [string]$policy.provenance.trustedProtectedOperationsWorkflowPath -or
            [string]$trustedExecution.schemaVersion -cne '2.0.0' -or
            [string]$trustedExecution.bundlePath -cne $bundlePath -or
            $bundleEntry.Count -ne 1 -or
            -not (Test-Path -LiteralPath $bundleFile -PathType Leaf) -or
            [string]$bundleEntry[0].sha256 -cne (Get-Sha256Lower -Path $bundleFile) -or
            [string]$authorizationContext.schemaVersion -cne '2.1.0' -or
            [string]$authorizationContext.status -cne 'PASS' -or
            [string]$authorizationContext.stage -cne 'Production' -or
            [string]$authorizationContext.environment -cne [string]$stagePolicy.githubEnvironment -or
            [string]$authorizationContext.applicationSha256 -cne $actualAppSha256 -or
            [string]$authorizationContext.releaseManifestSha256 -cne (Get-Sha256Lower -Path $manifestPath) -or
            [string]$authorizationContext.releaseCommitSha -cne ([string]$manifest.commitSha).ToLowerInvariant() -or
            -not (Test-StageOperationAuthorizationRunBinding `
                -Actual $authorizationContext.sourceRelease -Expected $releaseVerified.Provenance.run) -or
            [string]$authorizationContext.bundlePath -cne $bundlePath -or
            [string]$authorizationContext.bundleSha256 -cne (Get-Sha256Lower -Path $bundleFile) -or
            [string]$authorizationContext.targetFingerprint -ne (Get-StageTargetFingerprint -TargetMetadata $target) -or
            -not (Test-Sha256 ([string]$authorizationContext.preflightEvidenceSha256)) -or
            [string]$authorizationContext.preparedInputProducerRun.runId -notmatch '^[1-9][0-9]*$' -or
            [string]$authorizationContext.producerBindings.preflight.runId -cne
                [string]$preflightRun.runId -or
            [string]$authorizationContext.producerBindings.preparedInputs.runId -cne
                [string]$authorizationContext.preparedInputProducerRun.runId -or
            [string]$preflightRun.repository -cne [string]$policy.provenance.repository -or
            [string]$preflightRun.workflowPath -cne [string]$policy.provenance.productionOperationEvidenceWorkflowPath -or
            [string]$preflightRun.workflowRef -cne
                "$([string]$policy.provenance.repository)/$([string]$policy.provenance.productionOperationEvidenceWorkflowPath)@$([string]$policy.provenance.protectedRef)" -or
            [string]$preflightRun.runId -notmatch '^[1-9][0-9]*$' -or
            -not (Test-StageOperationInputInteger -Value $preflightRun.runAttempt -Minimum 1) -or
            [string]$preflightRun.ref -cne [string]$policy.provenance.protectedRef -or
            [string]$preflightRun.commitSha -notmatch '^[a-f0-9]{40}$' -or
            [string]$preflightRun.status -cne 'completed' -or
            [string]$preflightRun.conclusion -cne 'success' -or
            [string]$authorizationContext.authorizedExecution.topLevelCallerWorkflowRef -cne
                [string]$productionCaller[0].workflowRef -or
            [string]$authorizationContext.authorizedExecution.producerWorkflowRef -notmatch (
                '^' + [regex]::Escape(
                    "$([string]$policy.provenance.repository)/$([string]$trustedPreflightRole[0].workflowPath)@"
                ) + '[a-f0-9]{40}$'
            ) -or
            [string]$authorizationContext.authorizedExecution.producerWorkflowRef -match '@0{40}$' -or
            [string]$authorizationContext.approvalReference -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}$' -or
            [string]$authorizationContext.authorizedByActor -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$' -or
            [string]$authorizationContext.authorizedByActorId -notmatch '^[1-9][0-9]*$' -or
            -not (Test-Sha256 ([string]$authorizationContext.ctoAuthorizationSha256)) -or
            -not (Test-Sha256 ([string]$authorizationContext.ctoAuthorizationProvenanceSha256)) -or
            -not (Test-Sha256 ([string]$authorizationContext.sourceChangeRecordSha256)) -or
            -not [DateTimeOffset]::TryParse([string]$authorizationContext.expiresAtUtc, [ref]$contextExpiry) -or
            $contextExpiry.Offset -ne [TimeSpan]::Zero -or
            $contextExpiry -le $now -or
            -not [DateTimeOffset]::TryParse([string]$authorizationContext.materializedAtUtc, [ref]$materializedAt) -or
            $materializedAt.Offset -ne [TimeSpan]::Zero -or
            $materializedAt -gt $now.AddMinutes([int]$policy.provenance.maxClockSkewMinutes)) {
            throw 'Production authorization context is invalid or no longer bound to the enabled policy, target, and release.'
        }
    }

    return [pscustomobject]@{
        RepositoryRoot = $repositoryRoot
        Policy = $policy
        StagePolicy = $stagePolicy
        Target = $target
        TargetFingerprint = Get-StageTargetFingerprint -TargetMetadata $target
        ArtifactRoot = $artifact
        Manifest = $manifest
        ManifestPath = $manifestPath
        ManifestSha256 = Get-Sha256Lower -Path $manifestPath
        AppSha256 = $actualAppSha256
        ReleaseVerifiedProvenance = $releaseVerified.Provenance
        SourceRelease = Get-StageOperationSourceRelease -VerifiedProvenance $releaseVerified.Provenance
        ProductionAuthorizationContext = $authorizationContext
    }
}
