namespace TenonEdge.Host.Features.Configuration;

internal static class ConfigurationChangeDescription
{
    public static string? Normalize(string? description)
    {
        if (description is null)
        { return null; }
        if (description.Length > 1000 || description.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t')))
        { throw new ArgumentException("变更说明最多 1000 字符，且不得包含换行或制表以外的控制字符。", nameof(description)); }
        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }
}
