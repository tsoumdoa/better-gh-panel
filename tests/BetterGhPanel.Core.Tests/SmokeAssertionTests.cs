using System;
using Xunit;

public sealed class SmokeAssertionTests
{
    [Fact]
    public void OnlySuccessfulChecksDuringTheFixtureAreCounted()
    {
        SmokeAssertions.Check(true, "startup check");
        Assert.Equal(0, SmokeAssertions.Count);
        try
        {
            SmokeAssertions.Begin();
            SmokeAssertions.Check(true, "fixture check");
            Assert.Equal(1, SmokeAssertions.Count);
            Assert.Throws<InvalidOperationException>(() => SmokeAssertions.Check(false, "failed fixture check"));
            Assert.Equal(1, SmokeAssertions.Count);
        }
        finally { SmokeAssertions.End(); }
        SmokeAssertions.Check(true, "post-fixture check");
        Assert.Equal(1, SmokeAssertions.Count);
    }
}
