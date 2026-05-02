namespace garge_operator.Tests;

public class WorkerConditionTests
{
    [Theory]
    [InlineData(19, "<", 20, true)]
    [InlineData(20, "<", 20, false)]
    [InlineData(21, "<", 20, false)]
    [InlineData(21, ">", 20, true)]
    [InlineData(20, ">", 20, false)]
    [InlineData(19, ">", 20, false)]
    [InlineData(20, "<=", 20, true)]
    [InlineData(19, "<=", 20, true)]
    [InlineData(21, "<=", 20, false)]
    [InlineData(20, ">=", 20, true)]
    [InlineData(21, ">=", 20, true)]
    [InlineData(19, ">=", 20, false)]
    [InlineData(20, "==", 20, true)]
    [InlineData(21, "==", 20, false)]
    [InlineData(21, "!=", 20, true)]
    [InlineData(20, "!=", 20, false)]
    [InlineData(20, "??", 20, false)]
    public void EvaluateCondition_ReturnsExpected(double value, string op, double threshold, bool expected)
    {
        var result = Worker.EvaluateCondition(value, op, threshold);
        Assert.Equal(expected, result);
    }
}
