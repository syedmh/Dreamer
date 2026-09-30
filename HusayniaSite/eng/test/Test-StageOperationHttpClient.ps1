[CmdletBinding()]
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
. (Join-Path $RepositoryRoot 'eng\promotion\StageOperationInput.Common.ps1')

$passed = 0
$failed = 0
function Assert-HttpProbe {
    param([string]$Name, [bool]$Condition, [string]$Failure)

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

if ($null -eq ('Husaynia.Testing.HttpProbeTestConnector' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Husaynia.Testing
{
    public static class HttpProbeDnsResolver
    {
        public static int Calls { get; private set; }
        public static bool Completed { get; private set; }

        public static void Reset()
        {
            Calls = 0;
            Completed = false;
        }

        public static async Task<IPAddress[]> ResolveAfterDelayAsync(
            int delayMilliseconds,
            CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            Completed = true;
            return new[] { IPAddress.Parse("93.184.216.34") };
        }
    }

    public static class HttpProbeTestConnector
    {
        public static IPAddress LastAddress { get; private set; }
        public static int LastPort { get; private set; }

        public static ValueTask<Stream> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAddress = address;
            LastPort = port;
            return new ValueTask<Stream>(new MemoryStream());
        }
    }

    public sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream inner;

        public NonSeekableReadStream(byte[] value)
        {
            inner = new MemoryStream(value, writable: false);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class DelayedReadStream : Stream
    {
        private readonly int delayMilliseconds;
        private bool completed;

        public DelayedReadStream(int delayMilliseconds)
        {
            this.delayMilliseconds = delayMilliseconds;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (completed)
            {
                return 0;
            }
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            completed = true;
            buffer[offset] = (byte)'x';
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
'@
}

$httpPolicy = [pscustomobject]@{
    connectTimeoutMilliseconds = 100
    requestTimeoutMilliseconds = 50
    maxResponseBytes = 64
    maxJsonDepth = 3
}

$dnsState = [pscustomobject]@{ Calls = 0 }
$dnsResolver = {
    param([string]$HostName)

    $dnsState.Calls++
    if ($dnsState.Calls -eq 1) {
        return @([Net.IPAddress]::Parse('93.184.216.34'))
    }
    return @([Net.IPAddress]::Parse('169.254.169.254'))
}.GetNewClosure()
$requestUri = [Uri]'https://probe.example.test/resource'
$pinnedAddress = Resolve-StageOperationPinnedAddress `
    -Uri $requestUri `
    -ApprovedPrivateEndpoints @() `
    -DnsResolver $dnsResolver
$connectorType = [Func[
    Net.IPAddress,
    int,
    Threading.CancellationToken,
    Threading.Tasks.ValueTask[IO.Stream]
]]
$connector = [Delegate]::CreateDelegate(
    $connectorType,
    [Husaynia.Testing.HttpProbeTestConnector].GetMethod('ConnectAsync'))
$callback = New-StageOperationConnectCallback `
    -PinnedAddress $pinnedAddress `
    -ExpectedHost $requestUri.DnsSafeHost `
    -ExpectedPort $requestUri.Port `
    -ConnectTimeoutMilliseconds $httpPolicy.connectTimeoutMilliseconds `
    -Connector $connector
$contextConstructor = [Net.Http.SocketsHttpConnectionContext].GetConstructors(
    [Reflection.BindingFlags]'Instance,NonPublic'
)[0]
$connectionContext = $contextConstructor.Invoke(@(
    [Net.DnsEndPoint]::new($requestUri.DnsSafeHost, $requestUri.Port),
    [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $requestUri)
))
$stream = $callback.Invoke(
    $connectionContext,
    [Threading.CancellationToken]::None
).AsTask().GetAwaiter().GetResult()
$stream.Dispose()
$laterDnsRejected = $false
try {
    $null = Resolve-StageOperationPinnedAddress `
        -Uri $requestUri `
        -ApprovedPrivateEndpoints @() `
        -DnsResolver $dnsResolver
}
catch {
    $laterDnsRejected = $_.Exception.Message -match 'loopback, private, link-local, metadata, or reserved'
}
Assert-HttpProbe 'dns-rebinding-connects-only-validated-address' (
    $pinnedAddress.ToString() -eq '93.184.216.34' -and
    [Husaynia.Testing.HttpProbeTestConnector]::LastAddress.ToString() -eq '93.184.216.34' -and
    [Husaynia.Testing.HttpProbeTestConnector]::LastPort -eq 443 -and
    $dnsState.Calls -eq 2 -and
    $laterDnsRejected
) 'A later private DNS answer changed the pinned connection target or was not rejected on the next request.'

[Husaynia.Testing.HttpProbeDnsResolver]::Reset()
$stalledDnsResolver = {
    param(
        [string]$HostName,
        [Threading.CancellationToken]$CancellationToken
    )

    return [Husaynia.Testing.HttpProbeDnsResolver]::ResolveAfterDelayAsync(
        $httpPolicy.requestTimeoutMilliseconds * 20,
        $CancellationToken)
}.GetNewClosure()
$dnsStopwatch = [Diagnostics.Stopwatch]::StartNew()
$stalledDnsRejected = $false
try {
    $null = Invoke-StageOperationPinnedHttpGet `
        -Uri $requestUri `
        -ApprovedPrivateEndpoints @() `
        -HttpPolicy $httpPolicy `
        -DnsResolver $stalledDnsResolver
}
catch {
    $stalledDnsRejected = $_.Exception.Message -match 'overall request timeout'
}
finally {
    $dnsStopwatch.Stop()
}
Assert-HttpProbe 'http-overall-timeout-includes-stalled-dns' (
    $stalledDnsRejected -and
    [Husaynia.Testing.HttpProbeDnsResolver]::Calls -eq 1 -and
    -not [Husaynia.Testing.HttpProbeDnsResolver]::Completed -and
    $dnsStopwatch.ElapsedMilliseconds -lt 1000
) 'A stalled DNS resolver was not cancelled by the overall request deadline before any network connection.'

$oversizedLengthResponse = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
$oversizedLengthResponse.Content = [Net.Http.ByteArrayContent]::new([byte[]](1, 2, 3))
$oversizedLengthResponse.Content.Headers.ContentLength = $httpPolicy.maxResponseBytes + 1
$oversizedLengthRejected = $false
try {
    $null = Read-StageOperationHttpResponseBody `
        -Response $oversizedLengthResponse `
        -MaxResponseBytes $httpPolicy.maxResponseBytes `
        -CancellationToken ([Threading.CancellationToken]::None)
}
catch {
    $oversizedLengthRejected = $_.Exception.Message -match 'Content-Length exceeds'
}
finally {
    $oversizedLengthResponse.Dispose()
}
Assert-HttpProbe 'http-oversized-content-length-rejected' $oversizedLengthRejected `
    'A response advertising a body over the policy limit was read.'

$chunkedResponse = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
$chunkedResponse.Content = [Net.Http.StreamContent]::new(
    [Husaynia.Testing.NonSeekableReadStream]::new([byte[]]::new($httpPolicy.maxResponseBytes + 1)))
$chunkedRejected = $false
try {
    $null = Read-StageOperationHttpResponseBody `
        -Response $chunkedResponse `
        -MaxResponseBytes $httpPolicy.maxResponseBytes `
        -CancellationToken ([Threading.CancellationToken]::None)
}
catch {
    $chunkedRejected = $_.Exception.Message -match 'body exceeds'
}
finally {
    $chunkedResponse.Dispose()
}
Assert-HttpProbe 'http-chunked-body-over-limit-rejected' $chunkedRejected `
    'A response without Content-Length streamed beyond the byte cap.'

$encodedResponse = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
$encodedResponse.Content = [Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes('{}'))
$encodedResponse.Content.Headers.ContentEncoding.Add('gzip')
$encodingRejected = $false
try {
    $null = Read-StageOperationHttpResponseBody `
        -Response $encodedResponse `
        -MaxResponseBytes $httpPolicy.maxResponseBytes `
        -CancellationToken ([Threading.CancellationToken]::None)
}
catch {
    $encodingRejected = $_.Exception.Message -match 'content encoding'
}
finally {
    $encodedResponse.Dispose()
}
Assert-HttpProbe 'http-unsupported-content-encoding-rejected' $encodingRejected `
    'A compressed or unsupported response encoding reached body processing.'

$slowResponse = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
$slowResponse.Content = [Net.Http.StreamContent]::new(
    [Husaynia.Testing.DelayedReadStream]::new($httpPolicy.requestTimeoutMilliseconds * 4))
$timeoutSource = [Threading.CancellationTokenSource]::new()
$timeoutSource.CancelAfter($httpPolicy.requestTimeoutMilliseconds)
$timeoutRejected = $false
try {
    $null = Read-StageOperationHttpResponseBody `
        -Response $slowResponse `
        -MaxResponseBytes $httpPolicy.maxResponseBytes `
        -CancellationToken $timeoutSource.Token
}
catch [OperationCanceledException] {
    $timeoutRejected = $true
}
finally {
    $timeoutSource.Dispose()
    $slowResponse.Dispose()
}
Assert-HttpProbe 'http-overall-timeout-cancels-slow-body' $timeoutRejected `
    'A slow response body ignored the policy-configured cancellation deadline.'

$deepJsonRejected = $false
try {
    $null = ConvertFrom-StageOperationBoundedJson `
        -Value '{"a":{"b":{"c":{"d":1}}}}' `
        -MaxJsonDepth $httpPolicy.maxJsonDepth
}
catch {
    $deepJsonRejected = $_.Exception.Message -match 'depth|valid JSON'
}
Assert-HttpProbe 'http-json-depth-limit-rejected' $deepJsonRejected `
    'JSON deeper than the policy limit was accepted.'

Write-Output "SUMMARY stage-operation-http-client passed=$passed failed=$failed"
if ($failed -gt 0) {
    exit 1
}
