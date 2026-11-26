using DeusKVM.Companion.Core;
using Xunit;
namespace DeusKVM.Companion.Tests;

public sealed class OwnershipWorkTests
{
    [Fact]
    public void LateReadyCannotReplayOldKeysOrCursorPlacement()
    {
        var state = new OwnershipWork(); var result = new List<string>();
        state.Begin(1, 10); state.Submit(1, 10, () => result.Add("old placement")); state.Submit(1, 10, () => result.Add("old paste"));
        state.Begin(2, 11); state.Submit(2, 11, () => result.Add("new input"));
        Assert.False(state.Complete(1, 10)); Assert.Empty(result);
        Assert.True(state.Complete(2, 11)); Assert.Equal(new[] { "new input" }, result);
    }
    [Fact]
    public void ReadyDrainsInOrderAndNewInputUsesSameExecutor()
    {
        var state = new OwnershipWork(); var result = new List<int>();
        state.Begin(1, 1); state.Submit(1, 1, () => result.Add(1)); state.Submit(1, 1, () => result.Add(2));
        Assert.Empty(result); state.Complete(1, 1); state.Submit(1, 1, () => result.Add(3));
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }
    [Fact]
    public void CallbackReplacingGenerationStopsOldDrain()
    {
        var state = new OwnershipWork(); var result = new List<int>();
        state.Begin(1, 1); state.Submit(1, 1, () => { result.Add(1); state.Begin(2, 2); }); state.Submit(1, 1, () => result.Add(2));
        Assert.False(state.Complete(1, 1)); Assert.Equal(new[] { 1 }, result);
    }
    [Fact]
    public void CleanupDiscardsQueuedPasteWithoutChangingClipboardGeneration()
    {
        var state = new OwnershipWork(); var called = false;
        state.Begin(1, 10); state.Submit(1, 10, () => called = true);
        state.DiscardPending();
        Assert.True(state.Current(1, 10));
        Assert.True(state.Complete(1, 10));
        Assert.False(called);
    }
    [Fact]
    public void CapacityFailureIsExplicitAndCancelledGenerationCannotResume()
    {
        var state = new OwnershipWork(1); state.Begin(1, 1);
        Assert.True(state.Submit(1, 1, () => { })); Assert.False(state.Submit(1, 1, () => { }));
        state.Cancel(); Assert.False(state.Complete(1, 1)); Assert.False(state.Ready);
    }
}
