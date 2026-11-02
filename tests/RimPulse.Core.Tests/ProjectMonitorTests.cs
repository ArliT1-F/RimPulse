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
    public async Task Monitor_IsolatesFailingSubscribersAndNotifiesOtherSubscribers()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("Defs");
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new ProjectMonitor(temp.Root, TimeSpan.FromMilliseconds(100));
        monitor.ChangesDetected += (_, _) => throw new InvalidOperationException("consumer failure");
        monitor.ChangesDetected += (_, _) => notified.TrySetResult();
        monitor.MonitoringError += (_, message) => reported.TrySetResult(message);
        monitor.Start();

        temp.CreateFile("Defs/Test.xml", "<Defs />");

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("subscriber failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consumer failure", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_RejectsMissingRootsAndInvalidDebounce()
    {
        using var temp = new TempDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectMonitor(temp.Root, TimeSpan.Zero));
        Assert.ThrowsAny<Exception>(() => new ProjectMonitor(temp.Combine("missing")));
    }
}
