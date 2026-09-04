using HusayniaSMS.Core.Messaging;

namespace HusayniaSMS.Tests.Core;

[TestClass]
public sealed class MessageValidatorTests
{
    private readonly MessageValidator _validator = new();

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void EmptyOrWhitespaceIsInvalid(string message)
    {
        var result = _validator.Validate(message);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("MessageRequired", result.ErrorCode);
    }

    [TestMethod]
    public void OneAndExactly1600RunesAreValid()
    {
        Assert.IsTrue(_validator.Validate("x").IsValid);
        Assert.IsTrue(_validator.Validate(new string('x', 1600)).IsValid);
    }

    [TestMethod]
    public void MoreThan1600RunesIsInvalid()
    {
        var result = _validator.Validate(new string('x', 1601));
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1601, result.CharacterCount);
    }

    [TestMethod]
    public void SurrogatePairCountsAsOneScalar()
    {
        var result = _validator.Validate("😀");
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.CharacterCount);
    }

    [TestMethod]
    public void ValidationDoesNotNormalizeMessage()
    {
        const string message = "  keep this spacing  ";
        var result = _validator.Validate(message);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(21, result.CharacterCount);
    }
}
