using System.Security.Cryptography;
using Linkarr.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Apps;

// The settings row and the two app clients built from it. API keys are encrypted at rest with ASP.NET Data Protection
// (the key ring lives in <config>/keys, mode 0700): a copy of the database alone does not reveal them.
public sealed class Connections(LinkarrDbContext db, IDataProtectionProvider protection, IHttpClientFactory httpFactory)
{
    private readonly IDataProtector protector = protection.CreateProtector("linkarr.app-keys");

    public async Task<AppSettings> SettingsAsync(CancellationToken ct) =>
        await db.Settings.FirstAsync(s => s.Id == 1, ct);

    public string Protect(string apiKey) => protector.Protect(apiKey);

    // Null when the key cannot be read any more (the key ring was lost): the admin enters it again.
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task<ModularrClient?> ModularrAsync(CancellationToken ct)
    {
        AppSettings settings = await SettingsAsync(ct);
        return Modularr(settings.ModularrUrl, Unprotect(settings.ModularrKey));
    }

    public async Task<VisularrClient?> VisularrAsync(CancellationToken ct)
    {
        AppSettings settings = await SettingsAsync(ct);
        return Visularr(settings.VisularrUrl, Unprotect(settings.VisularrKey));
    }

    public ModularrClient? Modularr(string? url, string? apiKey) =>
        BaseUrl(url) is Uri baseUrl && !string.IsNullOrEmpty(apiKey) ? new ModularrClient(httpFactory.CreateClient(AppHttp.ClientName), baseUrl, apiKey) : null;

    public VisularrClient? Visularr(string? url, string? apiKey) =>
        BaseUrl(url) is Uri baseUrl && !string.IsNullOrEmpty(apiKey) ? new VisularrClient(httpFactory.CreateClient(AppHttp.ClientName), baseUrl, apiKey) : null;

    // With a trailing slash, so relative paths keep a base path (http://host/modularr/).
    public static Uri? BaseUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/")
            : null;

    // Absolute http(s) or nothing; anything else is refused at save time.
    public static string? ValidUrl(string? url, string field)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return BaseUrl(url) is Uri uri ? uri.ToString().TrimEnd('/') : throw new ArgumentException($"{field} must be an absolute http or https address.", field);
    }
}
