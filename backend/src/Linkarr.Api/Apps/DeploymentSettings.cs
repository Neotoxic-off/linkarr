using Linkarr.Api.Data;

namespace Linkarr.Api.Apps;

// Connection settings the deployment sets in the environment (docker compose). They are written into the settings row at
// startup and again on every save, so the environment always wins; keys are stored encrypted like a key typed in Settings.
public static class DeploymentSettings
{
    // Environment variable -> the settings field it sets, as the API names it.
    public static readonly IReadOnlyList<(string Variable, string Field)> Variables =
    [
        ("MODULARR_URL", "modularrUrl"),
        ("MODULARR_API_KEY", "modularrApiKey"),
        ("MODULARR_PUBLIC_URL", "modularrPublicUrl"),
        ("VISULARR_URL", "visularrUrl"),
        ("VISULARR_API_KEY", "visularrApiKey"),
        ("VISULARR_PUBLIC_URL", "visularrPublicUrl"),
        ("LINKARR_PUBLIC_URL", "linkarrPublicUrl"),
    ];

    // The fields set by the deployment, shown read-only in Settings.
    public static IReadOnlyList<string> Fields(IConfiguration configuration) =>
        [.. Variables.Where(v => Value(configuration, v.Variable) is not null).Select(v => v.Field)];

    // An invalid URL fails loudly (at startup) rather than being ignored.
    public static void Apply(AppSettings settings, IConfiguration configuration, Connections connections)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(connections);
        settings.ModularrUrl = Url(configuration, "MODULARR_URL") ?? settings.ModularrUrl;
        settings.ModularrPublicUrl = Url(configuration, "MODULARR_PUBLIC_URL") ?? settings.ModularrPublicUrl;
        settings.VisularrUrl = Url(configuration, "VISULARR_URL") ?? settings.VisularrUrl;
        settings.VisularrPublicUrl = Url(configuration, "VISULARR_PUBLIC_URL") ?? settings.VisularrPublicUrl;
        settings.LinkarrPublicUrl = Url(configuration, "LINKARR_PUBLIC_URL") ?? settings.LinkarrPublicUrl;
        if (Value(configuration, "MODULARR_API_KEY") is string modularrKey && connections.Unprotect(settings.ModularrKey) != modularrKey)
        {
            settings.ModularrKey = connections.Protect(modularrKey);
        }

        if (Value(configuration, "VISULARR_API_KEY") is string visularrKey && connections.Unprotect(settings.VisularrKey) != visularrKey)
        {
            settings.VisularrKey = connections.Protect(visularrKey);
        }
    }

    private static string? Value(IConfiguration configuration, string variable) =>
        configuration[variable]?.Trim() is { Length: > 0 } value ? value : null;

    private static string? Url(IConfiguration configuration, string variable) => Connections.ValidUrl(Value(configuration, variable), variable);
}
