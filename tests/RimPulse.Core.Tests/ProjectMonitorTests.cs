using RimPulse.Core.Monitoring;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ProjectMonitorTests
{
    [Fact]
    public async Task Monitor_ReportsRealFileChangesAndStopsAfterDisposal()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("Defs");
        var found = new TaskCompletionSource<ProjectChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new ProjectMonitor(temp.Root, TimeSpan.FromMilliseconds(200));
        monitor.ChangesDetected += (_, changes) =>
        {
            var match = changes.FirstOrDefault(c => c.RelativePath == Path.Combine("Defs", "Test.xml"));
            if (match is not null) found.TrySetResult(match);
        };
        monitor.Start();
        temp.CreateFile("Defs/Test.xml", "<Defs />");
        var result = await found.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ProjectChangeCategory.Xml, result.Category);
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.Start());
    }

    [Fact]
    public void Monitor_RejectsMissingRootsAndInvalidDebounce()
    {
        using var temp = new TempDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectMonitor(temp.Root, TimeSpan.Zero));
        Assert.ThrowsAny<Exception>(() => new ProjectMonitor(temp.Combine("missing")));
    }
}
