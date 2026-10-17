using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests that <c>SynchronousProgress</c> forwards reports in order on the calling thread.</summary>
public class SynchronousProgressTests
{
    [Fact]
    public void ReportInvokesCallbackSynchronouslyAndInOrder()
    {
        var values = new List<int>();
        var progress = new SynchronousProgress<int>(values.Add);

        progress.Report(1);
        progress.Report(2);
        progress.Report(3);

        Assert.Equal([1, 2, 3], values);
    }

    [Fact]
    public void ReportInvokesCallbackBeforeReturning()
    {
        var called = false;
        var progress = new SynchronousProgress<string>(_ => called = true);

        progress.Report("value");

        Assert.True(called);
    }
}