namespace EPApi.Utils;

public static class LocalizationUtils
{
    private static readonly HashSet<string> EsCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "CR", "MX", "CO", "AR", "PE", "CL", "ES", "UY", "PY",
        "BO", "EC", "GT", "SV", "HN", "NI", "PA", "VE",
        "PR", "DO", "CU"
    };

    public static string NormalizeLanguage(string? lang, string? countryIso2)
    {
        if (!string.IsNullOrWhiteSpace(lang))
        {
            var l = lang.Trim().ToLowerInvariant();
            if (l == "es" || l == "en") return l;
        }

        if (!string.IsNullOrWhiteSpace(countryIso2))
        {
            var c = countryIso2.Trim().ToUpperInvariant();
            if (EsCountries.Contains(c)) return "es";
        }

        return "es";
    }
}
