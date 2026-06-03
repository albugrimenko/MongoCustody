namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class HealthCheckResultTests
{
    [TestMethod]
    public void IsHealthy_ReturnsFalse_WhenNoComponents()
    {
        var result = new HealthCheckResult();
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public void IsHealthy_ReturnsTrue_WhenAllComponentsHealthy()
    {
        var result = new HealthCheckResult
        {
            ComponentResults =
            [
                new ComponentHealthResult { ComponentName = "MongoDB",     IsHealthy = true,  LatencyMs = 5  },
                new ComponentHealthResult { ComponentName = "BlobStorage", IsHealthy = true,  LatencyMs = 12 }
            ]
        };
        Assert.IsTrue(result.IsHealthy);
    }

    [TestMethod]
    public void IsHealthy_ReturnsFalse_WhenAnyComponentUnhealthy()
    {
        var result = new HealthCheckResult
        {
            ComponentResults =
            [
                new ComponentHealthResult { ComponentName = "MongoDB",     IsHealthy = true  },
                new ComponentHealthResult { ComponentName = "BlobStorage", IsHealthy = false }
            ]
        };
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public void Summary_ListsUnhealthyComponents()
    {
        var result = new HealthCheckResult
        {
            ComponentResults =
            [
                new ComponentHealthResult { ComponentName = "MongoDB",     IsHealthy = true  },
                new ComponentHealthResult { ComponentName = "BlobStorage", IsHealthy = false },
                new ComponentHealthResult { ComponentName = "KeyVault",    IsHealthy = false }
            ]
        };
        StringAssert.Contains(result.Summary, "BlobStorage");
        StringAssert.Contains(result.Summary, "KeyVault");
    }

    [TestMethod]
    public void Summary_ReturnsAllHealthy_WhenEveryComponentPasses()
    {
        var result = new HealthCheckResult
        {
            ComponentResults =
            [
                new ComponentHealthResult { ComponentName = "MongoDB", IsHealthy = true }
            ]
        };
        Assert.AreEqual("All components healthy.", result.Summary);
    }
}
