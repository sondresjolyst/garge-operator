using garge_operator.Services;
using Xunit;

namespace garge_operator.Tests;

public class MqttServicePreSwitchEchoTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly DateTime Now = new(2026, 5, 5, 1, 43, 42, DateTimeKind.Utc);

    [Fact]
    public void NoPriorCommand_NotEcho()
    {
        var result = MqttService.IsPreSwitchEcho("OFF", lastCommand: null, Now, Grace);

        Assert.False(result);
    }

    [Fact]
    public void ContradictingStateWithinGrace_IsEcho()
    {
        var lastCmd = ("ON", Now.AddSeconds(-1));

        var result = MqttService.IsPreSwitchEcho("OFF", lastCmd, Now, Grace);

        Assert.True(result);
    }

    [Fact]
    public void ContradictingStateAtGraceBoundary_NotEcho()
    {
        var lastCmd = ("ON", Now - Grace);

        var result = MqttService.IsPreSwitchEcho("OFF", lastCmd, Now, Grace);

        Assert.False(result);
    }

    [Fact]
    public void ContradictingStateAfterGrace_NotEcho()
    {
        var lastCmd = ("ON", Now.AddSeconds(-31));

        var result = MqttService.IsPreSwitchEcho("OFF", lastCmd, Now, Grace);

        Assert.False(result);
    }

    [Fact]
    public void MatchingStateWithinGrace_NotEcho()
    {
        var lastCmd = ("ON", Now.AddSeconds(-5));

        var result = MqttService.IsPreSwitchEcho("ON", lastCmd, Now, Grace);

        Assert.False(result);
    }

    [Fact]
    public void MatchingStateAfterGrace_NotEcho()
    {
        var lastCmd = ("ON", Now.AddSeconds(-120));

        var result = MqttService.IsPreSwitchEcho("ON", lastCmd, Now, Grace);

        Assert.False(result);
    }

    [Fact]
    public void GraceConstantIsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), MqttService.CommandEchoGrace);
    }
}
