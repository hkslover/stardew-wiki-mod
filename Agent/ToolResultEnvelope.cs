using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewWikiAgent.Agent;

/// <summary>Creates and inspects the structured result format used by built-in agent tools.</summary>
internal static class ToolResultEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Success(object data)
    {
        return JsonSerializer.Serialize(new { ok = true, data }, JsonOptions);
    }

    public static string Failure(string code, string message, string hint, object? data = null)
    {
        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = new { code, message, hint },
            data
        }, JsonOptions);
    }

    public static ToolResultInspection Inspect(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ToolResultInspection(false, true, null, null);

            bool isEnvelope = root.TryGetProperty("ok", out JsonElement okElement)
                && okElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
            bool isSuccess = isEnvelope
                ? okElement.GetBoolean()
                : !root.TryGetProperty("error", out _);
            string? errorCode = null;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                errorCode = error.ValueKind == JsonValueKind.Object
                    ? GetString(error, "code")
                    : "legacy_error";
            }

            string? status = GetString(root, "status");
            if (status is null
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object)
                status = GetString(data, "status");

            return new ToolResultInspection(isEnvelope, isSuccess, errorCode, status);
        }
        catch (JsonException)
        {
            return new ToolResultInspection(false, true, "invalid_json", null);
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

internal readonly record struct ToolResultInspection(
    bool IsEnvelope,
    bool IsSuccess,
    string? ErrorCode,
    string? Status
)
{
    public string LogStatus => this.Status
        ?? (this.IsSuccess ? "ok" : this.ErrorCode ?? "error");
}
