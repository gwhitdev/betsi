namespace Betsi.Tests.Application;

using Betsi.Application.Validation;

/// <summary>
/// NHS numbers carry a modulus-11 check digit. Validating it at entry catches transposed
/// digits where they are cheapest to fix — a mis-keyed number attaches a record to the wrong
/// person.
/// </summary>
public class NhsNumberTests
{
    [Theory]
    [InlineData("9434765919")]
    [InlineData("9434765870")]
    [InlineData("9990548609")]
    public void A_well_formed_number_is_accepted(string nhsNumber)
    {
        NhsNumber.IsValid(nhsNumber).ShouldBeTrue();
    }

    [Theory]
    [InlineData("943 476 5919")]
    [InlineData("943-476-5919")]
    public void Spacing_and_hyphens_are_tolerated(string nhsNumber)
    {
        NhsNumber.IsValid(nhsNumber).ShouldBeTrue();
    }

    [Fact]
    public void A_transposed_pair_of_digits_is_caught_by_the_check_digit()
    {
        NhsNumber.IsValid("9434765919").ShouldBeTrue();

        // 4-3 transposed in the middle.
        NhsNumber.IsValid("9433765919").ShouldBeFalse();
    }

    [Theory]
    [InlineData("9434765918")]
    [InlineData("1234567890")]
    public void A_wrong_check_digit_is_rejected(string nhsNumber)
    {
        NhsNumber.IsValid(nhsNumber).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("943476591")]
    [InlineData("94347659199")]
    [InlineData("943476591A")]
    public void Anything_that_is_not_ten_digits_is_rejected(string? nhsNumber)
    {
        NhsNumber.IsValid(nhsNumber).ShouldBeFalse();
    }
}
