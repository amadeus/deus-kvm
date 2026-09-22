namespace DeusKVM.Companion.Core;

public interface IRemovalSteps
{
    Task Stop();
    Task Unregister();
    Task FinishFiles();
}

public static class RemovalWorkflow
{
    // Bluetooth pairings belong to Windows and survive automatic-mode removal.
    // Never claim success or delete files if an earlier cleanup step failed.
    public static async Task Run(IRemovalSteps steps)
    {
        await steps.Stop();
        await steps.Unregister();
        await steps.FinishFiles();
    }
}
