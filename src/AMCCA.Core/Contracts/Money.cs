using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AMCCA.Core.Contracts;

public static class Money
{
    private static readonly Regex MoneyPattern = new(
        @"^(0|-?[1-9]\d*)\.\d{6}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Format(decimal amount)
    {
        return amount.ToString("F6", CultureInfo.InvariantCulture);
    }

    public static bool IsValidFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return MoneyPattern.IsMatch(value);
    }

    public static decimal Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Monetary value cannot be null or empty.");
        }

        // D-023: Reject scientific notation, NaN, Infinity
        if (text.Contains('e', StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NaN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Scientific notation, NaN, and Infinity are strictly prohibited for monetary values (D-023).");
        }

        // D-023: Must match exact 6 fractional digits
        if (!IsValidFormat(text))
        {
            throw new FormatException($"Monetary value '{text}' must be a decimal string with exactly six fractional digits (D-023).");
        }

        return decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string text, out decimal result)
    {
        result = 0m;
        if (!IsValidFormat(text)) return false;

        return decimal.TryParse(
            text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out result);
    }
}
