namespace DeusKVM.Companion.Core;

public interface IRemovalSteps
{
    Task Stop();
    Task Unregister();
    Task FinishFiles();
}

public static class RemovalWorkflow
{
    // Removal leaves nothing behind but the downloaded EXE.
    // Never claim success or delete files if an earlier cleanup step failed.
    public static async Task Run(IRemovalSteps steps)
    {
        await steps.Stop();
        await steps.Unregister();
        await steps.FinishFiles();
    }
}
