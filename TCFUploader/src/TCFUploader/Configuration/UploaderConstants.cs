namespace TCFUploader.Configuration;

internal static class UploaderConstants
{
    internal const string EventId = "-P1Y143qUTagjT1hDguA";
    internal const string TokenEnvironmentVariable = "LUMABOOTH_FOTOSHARE_TOKEN";
    internal const string BrowserPathEnvironmentVariable = "TCFUPLOADER_BROWSER_PATH";
    internal static readonly Uri DashboardUploadUri =
        new("https://dash.lumabooth.com/event/-P1Y143qUTagjT1hDguA/upload");
    internal static readonly Uri PutBaseUri = new("https://w.fotoshare.co/upload_file/");
    internal static readonly Uri PostUri = new("https://fotoshare.co/api/event/-P1Y143qUTagjT1hDguA/upload");
    internal static readonly Uri MediaBaseUri = new("https://fotoshare.s3.us-east-005.backblazeb2.com/");
}
