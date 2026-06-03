namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class ValidationResultTests
{
    [TestMethod]
    public void Passed_ReturnsFalse_WhenNoCollections()
    {
        var result = new ValidationResult();
        Assert.IsFalse(result.Passed);
    }

    [TestMethod]
    public void Passed_ReturnsTrue_WhenAllCollectionsPassed()
    {
        var result = new ValidationResult
        {
            CollectionResults =
            [
                new CollectionValidationResult { CollectionName = "patients",  Passed = true },
                new CollectionValidationResult { CollectionName = "encounters", Passed = true }
            ]
        };
        Assert.IsTrue(result.Passed);
    }

    [TestMethod]
    public void Passed_ReturnsFalse_WhenAnyCollectionFailed()
    {
        var result = new ValidationResult
        {
            CollectionResults =
            [
                new CollectionValidationResult { CollectionName = "patients",  Passed = true  },
                new CollectionValidationResult { CollectionName = "encounters", Passed = false }
            ]
        };
        Assert.IsFalse(result.Passed);
    }
}
