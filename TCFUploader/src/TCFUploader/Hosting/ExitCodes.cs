namespace TCFUploader.Hosting;

internal static class ExitCodes
{
    internal const int Success = 0;
    internal const int Fatal = 1;
    internal const int Configuration = 2;
    internal const int State = 3;
    internal const int Authentication = 4;
    internal const int ForcedShutdown = 5;
}
