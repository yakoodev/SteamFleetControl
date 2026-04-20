using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamFleet.Contracts.Steam;

namespace SteamFleet.Integrations.Steam;

public sealed partial class SteamKitGateway
{
    private static bool TryParseSsrRenderContext(string html, out JsonDocument jsonDocument)
    {
        var direct = SsrRenderContextRegex.Match(html);
        if (direct.Success)
        {
            try
            {
                jsonDocument = JsonDocument.Parse(direct.Groups["json"].Value);
                return true;
            }
            catch (JsonException)
            {
                // continue with alternate parsing.
            }
        }

        var parseCall = SsrRenderContextJsonParseRegex.Match(html);
        if (parseCall.Success)
        {
            var encoded = parseCall.Groups["json"].Value;
            try
            {
                var decoded = Regex.Unescape(encoded);
                decoded = WebUtility.HtmlDecode(decoded);
                jsonDocument = JsonDocument.Parse(decoded);
                return true;
            }
            catch (JsonException)
            {
                // ignored
            }
        }

        jsonDocument = null!;
        return false;
    }

    private static void CollectOwnedGames(JsonElement element, List<SteamOwnedGame> buffer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (TryExtractOwnedGame(element, out var game))
                {
                    buffer.Add(game);
                }

                foreach (var prop in element.EnumerateObject())
                {
                    CollectOwnedGames(prop.Value, buffer);
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectOwnedGames(item, buffer);
                }

                break;
            }
        }
    }

    private static string? CollectOwnedGamesFromQueryData(JsonElement renderContextRoot, List<SteamOwnedGame> buffer)
    {
        if (renderContextRoot.ValueKind != JsonValueKind.Object ||
            !renderContextRoot.TryGetProperty("queryData", out var queryDataElement) ||
            queryDataElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var queryDataRaw = queryDataElement.GetString();
        if (string.IsNullOrWhiteSpace(queryDataRaw))
        {
            return null;
        }

        try
        {
            using var queryData = JsonDocument.Parse(queryDataRaw);
            if (!queryData.RootElement.TryGetProperty("queries", out var queries) ||
                queries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? profileUrl = null;
            foreach (var query in queries.EnumerateArray())
            {
                if (query.ValueKind != JsonValueKind.Object ||
                    !query.TryGetProperty("queryKey", out var queryKey) ||
                    queryKey.ValueKind != JsonValueKind.Array ||
                    queryKey.GetArrayLength() == 0 ||
                    queryKey[0].ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var key = queryKey[0].GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!query.TryGetProperty("state", out var state) ||
                    state.ValueKind != JsonValueKind.Object ||
                    !state.TryGetProperty("data", out var stateData))
                {
                    continue;
                }

                if (key.Equals("OwnedGames", StringComparison.OrdinalIgnoreCase))
                {
                    CollectOwnedGames(stateData, buffer);
                }
                else if (string.IsNullOrWhiteSpace(profileUrl) &&
                         key.Equals("PlayerLinkDetails", StringComparison.OrdinalIgnoreCase) &&
                         stateData.ValueKind == JsonValueKind.Object &&
                         stateData.TryGetProperty("public_data", out var publicData) &&
                         publicData.ValueKind == JsonValueKind.Object &&
                         publicData.TryGetProperty("profile_url", out var profileUrlElement) &&
                         profileUrlElement.ValueKind == JsonValueKind.String)
                {
                    profileUrl = profileUrlElement.GetString();
                }
            }

            return profileUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractOwnedGame(JsonElement element, out SteamOwnedGame game)
    {
        game = new SteamOwnedGame();

        if (!TryReadInt(element, out var appId, "appid", "app_id", "appId"))
        {
            return false;
        }

        if (!TryReadString(element, out var name, "name", "title", "app_name"))
        {
            return false;
        }

        var playtime = 0;
        if (TryReadInt(element, out var minutes, "playtime_forever", "playtime_minutes", "playtime"))
        {
            playtime = minutes;
        }
        else if (TryReadDouble(element, out var hours, "hours_forever"))
        {
            playtime = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        }

        TryReadString(element, out var icon, "img_icon_url", "img_logo_url", "icon");

        game = new SteamOwnedGame
        {
            AppId = appId,
            Name = name,
            PlaytimeMinutes = Math.Max(0, playtime),
            ImgIconUrl = icon
        };

        return true;
    }

    private static void CollectOwnedGamesFromRawHtml(string html, List<SteamOwnedGame> buffer)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        CollectOwnedGamesFromRgGames(html, buffer);

        var patterns = new[]
        {
            "\"appid\"\\s*:\\s*(?<id>\\d+)[^\\}]{0,800}?\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"",
            "\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"[^\\}]{0,800}?\"appid\"\\s*:\\s*(?<id>\\d+)",
            "'appid'\\s*:\\s*(?<id>\\d+)[^\\}]{0,800}?'name'\\s*:\\s*'(?<name>(?:\\\\.|[^'])*)'",
            "'name'\\s*:\\s*'(?<name>(?:\\\\.|[^'])*)'[^\\}]{0,800}?'appid'\\s*:\\s*(?<id>\\d+)"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                if (!match.Success ||
                    !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
                {
                    continue;
                }

                var rawName = match.Groups["name"].Value;
                var name = DecodeJsString(rawName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var playtime = ExtractPlaytimeMinutesFromFragment(match.Value);
                buffer.Add(new SteamOwnedGame
                {
                    AppId = appId,
                    Name = name,
                    PlaytimeMinutes = playtime
                });
            }
        }
    }

    private static void CollectOwnedGamesFromRgGames(string html, List<SteamOwnedGame> buffer)
    {
        foreach (Match match in RgGamesJsonRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            CollectOwnedGamesFromJsonArray(match.Groups["json"].Value, buffer);
        }

        foreach (Match match in RgGamesJsonParseRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var encoded = match.Groups["json"].Value;
            var decoded = WebUtility.HtmlDecode(Regex.Unescape(encoded));
            CollectOwnedGamesFromJsonArray(decoded, buffer);
        }
    }

    private static void CollectOwnedGamesFromJsonArray(string json, List<SteamOwnedGame> buffer)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!TryExtractOwnedGame(item, out var game))
                {
                    continue;
                }

                buffer.Add(game);
            }
        }
        catch (JsonException)
        {
            // ignored
        }
    }

    private static int ExtractPlaytimeMinutesFromFragment(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return 0;
        }

        var playtimeMatch = Regex.Match(
            fragment,
            "(?:playtime_forever|playtime_minutes|playtime)\\s*[:=]\\s*(?<minutes>\\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (playtimeMatch.Success &&
            int.TryParse(playtimeMatch.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return Math.Max(0, minutes);
        }

        var hoursMatch = Regex.Match(
            fragment,
            "(?:hours_forever|hoursplayed)\\s*[:=]\\s*(?<hours>\\d+(?:\\.\\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (hoursMatch.Success &&
            double.TryParse(hoursMatch.Groups["hours"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var hours))
        {
            return (int)Math.Max(0, Math.Round(hours * 60, MidpointRounding.AwayFromZero));
        }

        return 0;
    }

    private static string DecodeJsString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var decoded = Regex.Unescape(value);
            return WebUtility.HtmlDecode(decoded);
        }
        catch
        {
            return value;
        }
    }

    private static string? TryReadProfileUrl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "profile_url", "profileUrl", "ProfileURL" })
            {
                if (root.TryGetProperty(propName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
            }

            foreach (var prop in root.EnumerateObject())
            {
                var nested = TryReadProfileUrl(prop.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var nested = TryReadProfileUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryReadString(JsonElement root, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = element.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt(JsonElement root, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var asInt))
            {
                value = asInt;
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out asInt))
            {
                value = asInt;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement root, out double value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var asDouble))
            {
                value = asDouble;
                return true;
            }

            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out asDouble))
            {
                value = asDouble;
                return true;
            }
        }

        value = 0;
        return false;
    }
}

