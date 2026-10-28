using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>The remote debugging port survives the game and WebUI Doctor setting the browser's launch arguments.</summary>
public class DebugPortTests
{
    [Fact]
    public void The_port_is_added_after_the_arguments_already_set()
    {
        Assert.Equal("--disable-web-security --no-sandbox --remote-debugging-port=9222", DebugPort.With("--disable-web-security --no-sandbox", 9222));
    }

    [Fact]
    public void Empty_arguments_get_only_the_port()
    {
        Assert.Equal("--remote-debugging-port=9333", DebugPort.With(null, 9333));
        Assert.Equal("--remote-debugging-port=9333", DebugPort.With("  ", 9333));
    }

    [Fact]
    public void A_port_already_there_is_replaced_not_doubled()
    {
        Assert.Equal("--a --remote-debugging-port=9222 --b", DebugPort.With("--a --remote-debugging-port=1234 --b", 9222));
    }
}
