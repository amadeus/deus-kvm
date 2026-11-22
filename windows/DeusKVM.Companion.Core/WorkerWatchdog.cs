namespace DeusKVM.Companion.Core;

// heartbeat freshness and connection-attempt progress are separate: the STA
// may keep pumping heartbeats while a WinRT discovery call never completes.
public sealed class WorkerWatchdog
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private long lastHeartbeat;
    private long operationStarted;
    private string state = "Starting";

    public WorkerWatchdog(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        lastHeartbeat = operationStarted = this.clock.GetTimestamp();
    }

    public void Observe(string nextState)
    {
        lock (gate)
        {
            lastHeartbeat = clock.GetTimestamp();
            if (nextState != state) { state = nextState; operationStarted = lastHeartbeat; }
        }
    }

    public bool IsExpired()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            return clock.GetElapsedTime(lastHeartbeat, now) > TimeSpan.FromSeconds(60) ||
                (state is "Starting" or "Opening" or "Discovering" &&
                    clock.GetElapsedTime(operationStarted, now) > TimeSpan.FromSeconds(60));
        }
    }
}
