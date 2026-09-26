using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

namespace L2Launcher.Core;

/// <summary>
/// Hook for a future server-side login gate. If LauncherConfig.PreLaunchUrl is set, the launcher POSTs
/// JSON right before starting l2.exe and only starts the game on HTTP 2xx with {"allow":true}.
///
/// Request:  {"launcherVersion":"1.0.0","manifestVersion":"20260926.1","language":"ru","clientVerified":true,"machineId":"<sha256>"}
/// Response: {"allow":true,"message":"optional text shown to the player","gameArguments":"optional extra l2.exe args"}
///
/// Intended server use (not implemented yet): record (source IP, time) and let the Mobius LoginServer accept
/// logins only from IPs that passed the gate in the last N minutes. This forces updates to be applied, but it
/// does NOT prove client integrity: anyone can send this request.
/// </summary>
public sealed class PreLaunchGate
{
    public sealed record Request(string launcherVersion, string manifestVersion, string language, bool clientVerified, string machineId);
    public sealed class Response
    {
        [JsonProperty("allow")] public bool Allow { get; set; }
        [JsonProperty("message")] public string? Message { get; set; }
        [JsonProperty("gameArguments")] public string? GameArguments { get; set; }
    }

    private readonly HttpClient _http;
    public PreLaunchGate(HttpClient http) { _http = http; }

    public async Task<Response> CheckAsync(string url, Request req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var content = new StringContent(JsonConvert.SerializeObject(req), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage resp;
        try { resp = await _http.PostAsync(url, content, cts.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new LauncherException("gate_unreachable", "Login gate unreachable: " + ex.Message, ex); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            Response? r = null;
            try { r = JsonConvert.DeserializeObject<Response>(body); } catch { }
            if (!resp.IsSuccessStatusCode)
                return new Response { Allow = false, Message = r?.Message ?? $"HTTP {(int)resp.StatusCode}" };
            return r ?? new Response { Allow = false, Message = "Invalid gate response" };
        }
    }

    /// <summary>Anonymous, stable per Windows install/user; not a real HWID.</summary>
    public static string MachineId()
    {
        var s = Environment.MachineName + "|" + Environment.UserName + "|" + Environment.OSVersion.VersionString;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}
