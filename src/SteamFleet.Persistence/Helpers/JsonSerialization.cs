using System.Text.Json;

namespace SteamFleet.Persistence.Helpers;

public static class JsonSerialization
{
    public static readonly JsonSerializerOptions Defaults = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string SerializeDictionary(Dictionary<string, string>? value)
    {
        value ??= [];
        return JsonSerializer.Serialize(value, Defaults);
    }

    public static Dictionary<string, string> DeserializeDictionary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(value, Defaults)
               ?? new(StringComparer.OrdinalIgnoreCase);
    }
}
