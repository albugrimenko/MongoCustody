namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class ValidationLevelFlagsTests
{
    [TestMethod]
    public void ValidationLevel_CanCombineMultipleLevels()
    {
        var levels = ValidationLevel.StructuralIntegrity | ValidationLevel.CountConsistency;

        Assert.IsTrue(levels.HasFlag(ValidationLevel.StructuralIntegrity));
        Assert.IsTrue(levels.HasFlag(ValidationLevel.CountConsistency));
        Assert.IsFalse(levels.HasFlag(ValidationLevel.IncrementalCompleteness));
    }

    [TestMethod]
    public void ValidationLevel_None_HasNoFlags()
    {
        Assert.IsFalse(ValidationLevel.None.HasFlag(ValidationLevel.StructuralIntegrity));
        Assert.IsFalse(ValidationLevel.None.HasFlag(ValidationLevel.CountConsistency));
        Assert.IsFalse(ValidationLevel.None.HasFlag(ValidationLevel.IncrementalCompleteness));
    }

    [TestMethod]
    public void ValidationLevel_AllFlagsAreDistinct()
    {
        var all = new[]
        {
            ValidationLevel.StructuralIntegrity,
            ValidationLevel.CountConsistency,
            ValidationLevel.IncrementalCompleteness
        };

        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
                Assert.AreNotEqual(all[i], all[j],
                    $"{all[i]} and {all[j]} must have distinct bit values.");
    }
}
