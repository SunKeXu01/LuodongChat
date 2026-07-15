using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace ChatGPTConnector.Core;

public static class EmailAddressValidator
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 254
            || !Regex.IsMatch(candidate, "^[a-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[a-z0-9.-]{3,189}$", RegexOptions.CultureInvariant)
            || !MailAddress.TryCreate(candidate, out var address)) return false;
        if (!string.Equals(address.Address, candidate, StringComparison.OrdinalIgnoreCase)) return false;
        var separator = candidate.LastIndexOf('@');
        if (separator is <= 0 or >= 254) return false;
        var local = candidate[..separator];
        var domain = candidate[(separator + 1)..];
        if (local.Length > 64 || local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal) || !domain.Contains('.')) return false;
        try { domain = new IdnMapping().GetAscii(domain); }
        catch (ArgumentException) { return false; }
        var labels = domain.Split('.');
        if (labels.Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))) return false;
        normalized = $"{local}@{domain}";
        return true;
    }
}

public static class PasswordPolicy
{
    public const string Requirement = "密码应为 8 至 128 个字符，并同时包含字母和数字。";
    public static bool IsValid(string? value) => value is { Length: >= 8 and <= 128 }
        && value.Any(char.IsLetter)
        && value.Any(char.IsDigit);
}
