using System.Text.Json;

namespace Granite.Toolkit.Core.Custodian;

public enum RepositoryHealth
{
    /// <summary>Custodian reached the process repository.</summary>
    Connected,
    /// <summary>GitHub refused Custodian's token (expired, revoked or wrong).</summary>
    TokenRejected,
    /// <summary>Custodian answered but couldn't reach the repository for another reason.</summary>
    Failed,
    /// <summary>Couldn't tell: Custodian didn't answer, wanted a sign-in, or answered in an unexpected shape.</summary>
    Unknown
}

public sealed record RepositoryCheck(RepositoryHealth Health, string Message);

/// <summary>
/// Whether Custodian can reach its approved process repository on GitHub.
/// Custodian reports this itself: its /config reply carries a
/// StoreConnection message ("Connection error ... (Bad credentials) ..."
/// when GitHub refuses the token). With a dead token the process catalogue
/// is simply empty, every request still returns 200, and Custodian logs
/// nothing, so this message is the only place the cause shows.
/// </summary>
/// <remarks>
/// Only StoreConnection is read. The rest of the reply (connection string
/// without its password, masked settings) is never logged or kept.
/// </remarks>
public static class CustodianRepository
{
    public static string ConfigUrl(string custodianBaseUrl) => custodianBaseUrl.TrimEnd('/') + "/config";

    public static RepositoryCheck FromResponse(int statusCode, string body)
    {
        if (statusCode is 401 or 403)
            return new(RepositoryHealth.Unknown, $"Custodian wants a sign-in for /config (HTTP {statusCode}); open the process catalogue in Web Desktop to check.");
        if (statusCode < 200 || statusCode > 299)
            return new(RepositoryHealth.Unknown, $"Custodian answered HTTP {statusCode}.");
        return FromConfig(body);
    }

    public static RepositoryCheck FromConfig(string json)
    {
        string? message;
        try
        {
            using var doc = JsonDocument.Parse(json);
            message = doc.RootElement.ValueKind == JsonValueKind.Object
                      && doc.RootElement.TryGetProperty("StoreConnection", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString()
                : null;
        }
        catch (JsonException)
        {
            return new(RepositoryHealth.Unknown, "Custodian's /config reply isn't JSON.");
        }
        if (message is null) return new(RepositoryHealth.Unknown, "Custodian's /config reply has no StoreConnection.");

        string text = message.Trim();
        if (text.Length > 300) text = text[..300];
        if (text.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("expired", StringComparison.OrdinalIgnoreCase) && text.Contains("error", StringComparison.OrdinalIgnoreCase))
            return new(RepositoryHealth.TokenRejected, text);
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new(RepositoryHealth.Failed, text);
        return new(RepositoryHealth.Connected, text.Length == 0 ? "Connected." : text);
    }

    /// <summary>The advice that goes with a rejected token.</summary>
    public const string TokenAdvice =
        "GitHub refused Custodian's token, so the process catalogue stays empty. Get a current Custodian.md (token and Repo_id) from Granite, " +
        "run it against the Granite database, then recycle the Custodian app pool.";

    /// <summary>Asks Custodian. Never throws for network trouble: that comes back as Unknown.</summary>
    public static async Task<RepositoryCheck> CheckAsync(HttpClient http, string custodianBaseUrl, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ConfigUrl(custodianBaseUrl));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, token);
            string body = await response.Content.ReadAsStringAsync(token);
            return FromResponse((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return new(RepositoryHealth.Unknown, $"Custodian didn't answer at {custodianBaseUrl}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
