using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.ScanWorker;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = ScanWorkerArguments.Parse(args);
            var validator = new WindowsVolumeOnlyLiveScanTargetValidator();
            var executor = new LiveScanExecutor(validator, new LiveVolumeSourceFactory(), new NtfsMetadataScanner());
            return await new NamedPipeScanWorkerHost(executor).RunAsync(arguments, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return 24;
        }
    }
}
