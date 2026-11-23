using Xunit;

namespace DevProxy.Tests;

public class CiAuditDeliberateFailureTests
{
    [Fact]
    public void DeliberateFailure() => Assert.Fail("CI audit: deliberate unit test failure");
}
