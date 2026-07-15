using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class EmailAddressValidatorTests
{
    [Theory]
    [InlineData(" User@Example.COM ", "user@example.com")]
    [InlineData("name+tag@example.co.uk", "name+tag@example.co.uk")]
    public void AcceptsAndNormalizesValidAddresses(string input, string expected)
    {
        Assert.True(EmailAddressValidator.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("user@example")]
    [InlineData("user..name@example.com")]
    [InlineData("user@-example.com")]
    public void RejectsMalformedAddresses(string input) => Assert.False(EmailAddressValidator.TryNormalize(input, out _));

    [Theory]
    [InlineData("Secure123", true)]
    [InlineData("short1", false)]
    [InlineData("onlyletters", false)]
    [InlineData("12345678", false)]
    public void EnforcesPasswordPolicy(string password, bool expected) => Assert.Equal(expected, PasswordPolicy.IsValid(password));
}
