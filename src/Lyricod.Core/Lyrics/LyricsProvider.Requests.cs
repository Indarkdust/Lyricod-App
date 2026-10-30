using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lyricod.Core;

public sealed partial class LyricsProvider
{
    async Task<JsonNode?> GetJson(HttpRequestMessage req, CancellationToken ct, bool require200 = false)
    {
        var text = await GetText(req, ct, require200).ConfigureAwait(false);
        try { return text == null ? null : JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    async Task<string?> GetText(HttpRequestMessage req, CancellationToken ct, bool require200 = false)
    {
        try
        {
            using var request = req;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_http.Timeout);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode || require200 && res.StatusCode != HttpStatusCode.OK) return null;
            return await LyricData.ReadText(res.Content, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    static string Query(params (string Key, string Value)[] items) =>
        string.Join('&', items.Select(i => Uri.EscapeDataString(i.Key) + "=" + Uri.EscapeDataString(i.Value)));

    static JsonNode? Field(JsonNode? node, params string[] path)
    {
        foreach (var key in path) node = (node as JsonObject)?[key];
        return node;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    static long? Long(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : null) : null;
    static IEnumerable<JsonNode> Arr(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : [];
}
