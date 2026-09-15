namespace Betsi.Application.Validation;

/// <summary>
/// Validation for NHS numbers.
/// </summary>
/// <remarks>
/// An NHS number is ten digits. The tenth is a modulus-11 check digit over the first nine,
/// weighted 10 down to 2. A remainder of 10 has no valid check digit, so such numbers are
/// never issued.
/// </remarks>
public static class NhsNumber
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var digits = value.Replace(" ", string.Empty).Replace("-", string.Empty);

        if (digits.Length != 10 || !digits.All(char.IsAsciiDigit))
            return false;

        var weightedSum = 0;
        for (var i = 0; i < 9; i++)
            weightedSum += (digits[i] - '0') * (10 - i);

        var remainder = weightedSum % 11;
        var checkDigit = 11 - remainder;

        if (checkDigit == 11)
            checkDigit = 0;

        if (checkDigit == 10)
            return false;

        return checkDigit == digits[9] - '0';
    }
}
