using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraniteNiFiDeploy.Core;

/// <summary>What NiFi said when it checked the database connection pool.</summary>
public sealed record VerificationResult(string Step, string Outcome, string Explanation)
{
    public bool Failed => Outcome.Equals("FAILED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A component that isn't valid or isn't running after start.</summary>
public sealed record ComponentProblem(string Group, string Name, string State, string Detail);

/// <summary>
/// The NiFi 2.x REST calls NiFi Deploy needs: sign in, upload the flow,
/// set its parameters, check and enable its services, start it. Plain
/// HttpClient and System.Text.Json, so the same code runs in the harness
/// against a real NiFi.
/// </summary>
/// <remarks>
/// NiFi answers on HTTPS with a certificate it generated itself on first
/// start. Rather than turning certificate checks off, the module reads that
/// certificate from NiFi's own keystore and accepts exactly it
/// (<see cref="PinnedHandler"/>).
/// </remarks>
public sealed class NiFiApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Action<LogEntry> _log;
    private string? _token;

    public NiFiApiClient(Uri baseUri, HttpMessageHandler handler, Action<LogEntry> log)
    {
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(120) };
        _log = log;
    }

    /// <summary>
    /// An HttpClientHandler that only accepts the server certificate whose
    /// SHA-256 thumbprint is <paramref name="sha256Thumbprint"/>, never uses
    /// a proxy, and keeps no cookies: the module authenticates with the
    /// bearer token only. (NiFi also sets the token as a cookie, and a
    /// request carrying that cookie must pass NiFi's CSRF check, which
    /// refuses the flow upload with 403.)
    /// </summary>
    public static HttpClientHandler PinnedHandler(string sha256Thumbprint) => new()
    {
        UseProxy = false,
        UseCookies = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            cert is not null && ThumbprintMatches(cert, sha256Thumbprint) &&
            // Name and chain errors are expected for a self-signed cert; anything else is not.
            (errors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) == SslPolicyErrors.None
    };

    public static bool ThumbprintMatches(X509Certificate2 cert, string sha256Thumbprint) =>
        string.Equals(cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), sha256Thumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SHA-256 thumbprint of the certificate in NiFi's generated keystore
    /// (conf\keystore.p12, password in nifi.properties).
    /// </summary>
    public static string KeystoreThumbprint(string nifiHome)
    {
        string propsPath = Path.Combine(nifiHome, "conf", "nifi.properties");
        string props = File.ReadAllText(propsPath);
        string keystore = NiFiConfigFiles.GetProperty(props, "nifi.security.keystore")
            ?? throw new InvalidDataException("nifi.security.keystore is not set in nifi.properties.");
        string password = NiFiConfigFiles.GetProperty(props, "nifi.security.keystorePasswd") ?? "";
        string path = Path.IsPathRooted(keystore) ? keystore : Path.GetFullPath(Path.Combine(nifiHome, keystore));
        using var cert = new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
        return cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
    }

    // ------------------------------------------------------------------ sign in

    /// <summary>
    /// Signs in, retrying while NiFi finishes starting (it answers HTTP
    /// before the flow controller is ready).
    /// </summary>
    public async Task LoginAsync(string user, string password, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        string last = "no answer";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = password });
                using var response = await _http.PostAsync("nifi-api/access/token", form, token);
                string body = await response.Content.ReadAsStringAsync(token);
                if (response.IsSuccessStatusCode)
                {
                    _token = body.Trim();
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                    return;
                }
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !body.Contains("not yet", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"NiFi refused the sign-in for {user} ({(int)response.StatusCode}). {Trim(body)}");
                last = $"{(int)response.StatusCode} {Trim(body)}";
            }
            catch (HttpRequestException ex)
            {
                last = ex.InnerException?.Message ?? ex.Message;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                last = "timed out";
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"NiFi wasn't ready to sign in within {timeout.TotalMinutes:0} minutes. Last answer: {last}");
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    // ------------------------------------------------------------------ flow

    public async Task<string> RootGroupIdAsync(CancellationToken token)
    {
        var root = await GetAsync("nifi-api/flow/process-groups/root", token);
        return Str(root["processGroupFlow"]?["id"]) ?? throw new InvalidDataException("NiFi didn't return the root process group.");
    }

    /// <summary>Id of the child process group with this name, or null.</summary>
    public async Task<string?> FindChildGroupAsync(string parentId, string name, CancellationToken token)
    {
        var flow = await GetAsync($"nifi-api/flow/process-groups/{parentId}", token);
        foreach (var g in flow["processGroupFlow"]?["flow"]?["processGroups"]?.AsArray() ?? new JsonArray())
            if (Str(g?["component"]?["name"]) == name) return Str(g?["id"]);
        return null;
    }

    /// <summary>
    /// Ids of the parameter contexts NiFi already has. NiFi reuses a context
    /// with the same name when a flow is uploaded, so callers check this
    /// first to avoid overwriting another flow's settings.
    /// </summary>
    public async Task<IReadOnlySet<string>> ParameterContextIdsAsync(CancellationToken token)
    {
        var list = await GetAsync("nifi-api/flow/parameter-contexts", token);
        return (list["parameterContexts"]?.AsArray() ?? new JsonArray()).Select(c => Str(c?["id"])).OfType<string>().ToHashSet();
    }

    /// <summary>Uploads a flow definition as a new process group; returns its id and its parameter context id.</summary>
    public async Task<(string GroupId, string? ParameterContextId)> UploadFlowAsync(string parentId, string groupName, byte[] flowJson, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(groupName), "groupName");
        form.Add(new StringContent("0"), "positionX");
        form.Add(new StringContent("0"), "positionY");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "clientId");
        var file = new ByteArrayContent(flowJson);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "GraniteCsvImport.json");

        using var response = await _http.PostAsync($"nifi-api/process-groups/{parentId}/process-groups/upload", form, token);
        var group = await ReadAsync(response, "upload the flow", token);
        string id = Str(group["id"]) ?? throw new InvalidDataException("NiFi didn't return the new group's id.");
        return (id, Str(group["component"]?["parameterContext"]?["id"]));
    }

    /// <summary>
    /// Writes parameter values and waits for NiFi to apply them (it stops
    /// and restarts whatever uses them). Sensitive values are sent once,
    /// over the pinned HTTPS connection, and never logged.
    /// </summary>
    public async Task UpdateParametersAsync(string contextId, IReadOnlyList<NiFiParameter> parameters, CancellationToken token)
    {
        var context = await GetAsync($"nifi-api/parameter-contexts/{contextId}", token);
        var known = (context["component"]?["parameters"]?.AsArray() ?? new JsonArray())
            .Select(p => Str(p?["parameter"]?["name"])).OfType<string>().ToHashSet();
        var unknown = parameters.Where(p => !known.Contains(p.Name)).Select(p => p.Name).ToList();
        if (unknown.Count > 0) throw new InvalidDataException("The flow's parameter context has no " + string.Join(", ", unknown) + ". Is this the Granite CSV Import flow?");

        var body = new JsonObject
        {
            ["revision"] = context["revision"]?.DeepClone(),
            ["id"] = contextId,
            ["component"] = new JsonObject
            {
                ["id"] = contextId,
                ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode)new JsonObject
                {
                    ["parameter"] = new JsonObject { ["name"] = p.Name, ["value"] = p.Value, ["sensitive"] = p.Sensitive }
                }).ToArray())
            }
        };
        var request = await PostAsync($"nifi-api/parameter-contexts/{contextId}/update-requests", body, "update the parameters", token);
        string requestId = Str(request["request"]?["requestId"]) ?? throw new InvalidDataException("NiFi didn't return a parameter update request id.");

        JsonNode state = request;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (state["request"]?["complete"]?.GetValue<bool>() != true)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("NiFi didn't finish applying the parameters within 3 minutes.");
            await Task.Delay(1000, token);
            state = await GetAsync($"nifi-api/parameter-contexts/{contextId}/update-requests/{requestId}", token);
        }
        string? failure = Str(state["request"]?["failureReason"]);
        await DeleteAsync($"nifi-api/parameter-contexts/{contextId}/update-requests/{requestId}", token);
        if (!string.IsNullOrEmpty(failure)) throw new InvalidOperationException("NiFi couldn't apply the parameters: " + failure);
    }

    // ------------------------------------------------------------------ services

    /// <summary>Controller services in a group: id, name, type and state.</summary>
    public async Task<IReadOnlyList<(string Id, string Name, string Type, string State, IReadOnlyList<string> Errors)>> ControllerServicesAsync(string groupId, CancellationToken token)
    {
        var list = await GetAsync($"nifi-api/flow/process-groups/{groupId}/controller-services?includeAncestorGroups=false&includeDescendantGroups=true", token);
        return (list["controllerServices"]?.AsArray() ?? new JsonArray()).Select(s => (
            Str(s?["id"]) ?? "",
            Str(s?["component"]?["name"]) ?? "",
            Str(s?["component"]?["type"]) ?? "",
            Str(s?["component"]?["state"]) ?? "",
            (IReadOnlyList<string>)(s?["component"]?["validationErrors"]?.AsArray() ?? new JsonArray()).Select(e => Str(e) ?? "").ToList()
        )).ToList();
    }

    /// <summary>The value NiFi's API shows for every set sensitive property, whatever it really holds.</summary>
    public const string SensitiveMask = "********";

    /// <summary>
    /// The property map to send with a verification request. NiFi returns
    /// every set sensitive property as "********" (even a parameter reference
    /// like #{granite.db.password}), and the verification uses the map it's
    /// sent as is, so sending the GET back unchanged makes NiFi try to log in
    /// with the password "********" (first Windows run, 2026-10-04: "Login
    /// failed for user 'svc_granite_nifi'" while the running pool was fine).
    /// Masked sensitive values are replaced with the value from the uploaded
    /// flow definition, which for the password is the parameter reference.
    /// </summary>
    public static JsonObject PropertiesForVerification(JsonObject component, IReadOnlyDictionary<string, string> flowProperties)
    {
        var descriptors = component["descriptors"]?.AsObject();
        var properties = new JsonObject();
        foreach (var kv in component["properties"]?.AsObject() ?? new JsonObject())
        {
            bool sensitive = descriptors?[kv.Key]?["sensitive"]?.GetValue<bool>() == true;
            string? value = kv.Value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            if (sensitive && value == SensitiveMask && flowProperties.TryGetValue(kv.Key, out string? fromFlow))
                properties[kv.Key] = fromFlow;
            else
                properties[kv.Key] = kv.Value?.DeepClone();
        }
        return properties;
    }

    /// <summary>
    /// Asks NiFi to check a (disabled) controller service's configuration,
    /// which for the SQL Server pool means actually connecting with the
    /// parameters just set. Returns NiFi's step-by-step result.
    /// </summary>
    public async Task<IReadOnlyList<VerificationResult>> VerifyControllerServiceAsync(string serviceId, IReadOnlyDictionary<string, string> flowProperties, CancellationToken token)
    {
        var service = await GetAsync($"nifi-api/controller-services/{serviceId}", token);
        var properties = PropertiesForVerification(service["component"]?.AsObject() ?? new JsonObject(), flowProperties);

        var body = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["componentId"] = serviceId,
                ["properties"] = properties,
                ["attributes"] = new JsonObject()
            }
        };
        var request = await PostAsync($"nifi-api/controller-services/{serviceId}/config/verification-requests", body, "start the connection check", token);
        string requestId = Str(request["request"]?["requestId"]) ?? throw new InvalidDataException("NiFi didn't return a verification request id.");

        JsonNode state = request;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (state["request"]?["complete"]?.GetValue<bool>() != true)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("NiFi didn't finish checking the connection within 2 minutes.");
            await Task.Delay(1000, token);
            state = await GetAsync($"nifi-api/controller-services/{serviceId}/config/verification-requests/{requestId}", token);
        }
        await DeleteAsync($"nifi-api/controller-services/{serviceId}/config/verification-requests/{requestId}", token);

        return (state["request"]?["results"]?.AsArray() ?? new JsonArray()).Select(r => new VerificationResult(
            Str(r?["verificationStepName"]) ?? "",
            Str(r?["outcome"]) ?? "",
            Str(r?["explanation"]) ?? "")).ToList();
    }

    /// <summary>Enables every controller service in the group and waits until they are all ENABLED.</summary>
    public async Task EnableControllerServicesAsync(string groupId, CancellationToken token)
    {
        await PutAsync($"nifi-api/flow/process-groups/{groupId}/controller-services", new JsonObject { ["id"] = groupId, ["state"] = "ENABLED" }, "enable the controller services", token);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var services = await ControllerServicesAsync(groupId, token);
            var notYet = services.Where(s => s.State != "ENABLED").ToList();
            if (notYet.Count == 0) return;
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException("These controller services didn't enable: " +
                    string.Join("; ", notYet.Select(s => $"{s.Name} ({s.State}{(s.Errors.Count > 0 ? ": " + string.Join(" ", s.Errors) : "")})")));
            await Task.Delay(1000, token);
        }
    }

    public Task StartGroupAsync(string groupId, CancellationToken token) =>
        PutAsync($"nifi-api/flow/process-groups/{groupId}", new JsonObject { ["id"] = groupId, ["state"] = "RUNNING" }, "start the flow", token);

    public Task StopGroupAsync(string groupId, CancellationToken token) =>
        PutAsync($"nifi-api/flow/process-groups/{groupId}", new JsonObject { ["id"] = groupId, ["state"] = "STOPPED" }, "stop the flow", token);

    /// <summary>Disables every controller service in the group and waits until they are all DISABLED.</summary>
    public async Task DisableControllerServicesAsync(string groupId, CancellationToken token)
    {
        await PutAsync($"nifi-api/flow/process-groups/{groupId}/controller-services", new JsonObject { ["id"] = groupId, ["state"] = "DISABLED" }, "disable the controller services", token);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while ((await ControllerServicesAsync(groupId, token)).Any(s => s.State != "DISABLED"))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The controller services didn't disable within 2 minutes.");
            await Task.Delay(1000, token);
        }
    }

    /// <summary>
    /// Deletes a stopped process group (its queues must be empty). Used by
    /// the harness's live test to clean up; the wizard never deletes flows.
    /// </summary>
    public async Task DeleteGroupAsync(string groupId, CancellationToken token)
    {
        var group = await GetAsync($"nifi-api/process-groups/{groupId}", token);
        string version = group["revision"]?["version"]?.ToString() ?? "0";
        using var response = await _http.DeleteAsync($"nifi-api/process-groups/{groupId}?version={version}&clientId={Guid.NewGuid()}", token);
        await ReadAsync(response, "delete the process group", token);
    }

    /// <summary>Processors anywhere under the group that aren't running, with their validation errors.</summary>
    public async Task<IReadOnlyList<ComponentProblem>> ProblemsAsync(string groupId, CancellationToken token)
    {
        var problems = new List<ComponentProblem>();
        await CollectAsync(groupId, problems, token);
        return problems;
    }

    private async Task CollectAsync(string groupId, List<ComponentProblem> problems, CancellationToken token)
    {
        var flow = await GetAsync($"nifi-api/flow/process-groups/{groupId}", token);
        var pgf = flow["processGroupFlow"];
        string groupName = Str(pgf?["breadcrumb"]?["breadcrumb"]?["name"]) ?? groupId;
        foreach (var p in pgf?["flow"]?["processors"]?.AsArray() ?? new JsonArray())
        {
            string state = Str(p?["component"]?["state"]) ?? "";
            var errors = (p?["component"]?["validationErrors"]?.AsArray() ?? new JsonArray()).Select(e => Str(e) ?? "").ToList();
            if (state != "RUNNING" || errors.Count > 0)
                problems.Add(new ComponentProblem(groupName, Str(p?["component"]?["name"]) ?? "", state, string.Join(" ", errors)));
        }
        foreach (var g in pgf?["flow"]?["processGroups"]?.AsArray() ?? new JsonArray())
            if (Str(g?["id"]) is string child) await CollectAsync(child, problems, token);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<JsonNode> GetAsync(string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(path, token);
        return await ReadAsync(response, "read " + path, token);
    }

    private async Task<JsonNode> PostAsync(string path, JsonNode body, string what, CancellationToken token)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, token);
        return await ReadAsync(response, what, token);
    }

    private async Task<JsonNode> PutAsync(string path, JsonNode body, string what, CancellationToken token)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(path, content, token);
        return await ReadAsync(response, what, token);
    }

    private async Task DeleteAsync(string path, CancellationToken token)
    {
        try { using var _ = await _http.DeleteAsync(path, token); }
        catch (HttpRequestException ex) { _log(new LogEntry(LogLevel.Detail, $"Couldn't tidy up NiFi request {path}: {ex.Message}")); }
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, string what, CancellationToken token)
    {
        string text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"NiFi couldn't {what} ({(int)response.StatusCode}): {Trim(text)}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Trim(string text) => text.Length > 400 ? text[..400] + "..." : text.Trim();

    public void Dispose() => _http.Dispose();
}
