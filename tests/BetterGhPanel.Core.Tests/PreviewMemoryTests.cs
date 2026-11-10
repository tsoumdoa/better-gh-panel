using BetterGhPanel.Core;
using Xunit;

public class PreviewMemoryTests
{
    [Fact]
    public void ActualResourcesShareCapacityWithSourcesAndStagedCopies()
    {
        long sources = 30;
        var memory = new PanelPreviewMemory(100, () => sources);
        using var mesh = memory.TryReserve(40);
        using var edges = memory.TryReserve(20);
        Assert.NotNull(mesh); Assert.NotNull(edges);
        Assert.Equal(60, memory.UsedBytes);
        sources += 10; // A staged source copy consumes the remaining capacity.
        Assert.Null(memory.TryReserve(1));
        sources -= 10;
        using var replacement = memory.TryReserve(10);
        Assert.NotNull(replacement);
        Assert.Equal(70, memory.UsedBytes);
    }

    [Fact]
    public void RetiredMeshesStayChargedUntilDisposedAndCapacityCanRetry()
    {
        long sources = 20;
        var memory = new PanelPreviewMemory(100, () => sources);
        var retired = memory.TryReserve(60)!;
        // Detaching an old cache or clearing its generation does not release it.
        sources = 10;
        Assert.Null(memory.TryReserve(40));
        Assert.Equal(60, memory.UsedBytes);
        retired.Dispose();
        var replacement = memory.TryReserve(40);
        Assert.NotNull(replacement);
        retired.Dispose(); // A repeated cleanup cannot release the new cache's charge.
        Assert.Equal(40, memory.UsedBytes);
        replacement!.Dispose();
        Assert.Equal(0, memory.UsedBytes);
    }

    [Fact]
    public void RetirementCoversOnlyTheShortfallWithoutMakingItReservable()
    {
        long sources = 40;
        var memory = new PanelPreviewMemory(100, () => sources);
        var display = memory.TryReserve(60)!;
        Assert.Equal(15, memory.RequiredRelease(15, 0));
        // One removed entry contributes both its source copy and display lease,
        // stopping further eviction while both remain alive until the idle flush.
        Assert.Equal(0, memory.RequiredRelease(15, retiringBytes: 5 + display.Bytes));
        Assert.False(memory.CanReserve(15));
        Assert.Equal(5, memory.RequiredRelease(15, retiringBytes: 10));
        display.Dispose(); sources -= 5;
        Assert.Equal(0, memory.RequiredRelease(15, 0));
        Assert.True(memory.CanReserve(15));
    }

    [Fact]
    public void FailedReservationsAndReentrantSourceChangesDoNotUnbalanceCharges()
    {
        long sources = 10;
        var memory = new PanelPreviewMemory(100, () => sources);
        var staged = memory.TryReserve(50)!;
        sources = 40; // Another generation is admitted while native work is being prepared.
        Assert.False(memory.CanReserve(11));
        Assert.Null(memory.TryReserve(-1));
        Assert.Null(memory.TryReserve(long.MaxValue));
        staged.Dispose(); // Preparation was canceled; the owned resource was disposed.
        Assert.Equal(0, memory.UsedBytes);
        using var retry = memory.TryReserve(60);
        Assert.NotNull(retry);
    }
}
