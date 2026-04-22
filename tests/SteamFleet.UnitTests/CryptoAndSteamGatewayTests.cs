using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SteamFleet.Contracts.Steam;
using SteamFleet.Integrations.Steam;
using SteamFleet.Integrations.Steam.Options;
using SteamFleet.Persistence.Security;
using System.Reflection;
using System.Text.Json;

namespace SteamFleet.UnitTests;

public sealed class CryptoAndSteamGatewayTests
{
    [Fact]
    public void AesGcmCrypto_Roundtrip_Works()
    {
        var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        var crypto = new AesGcmSecretCryptoService(key);
        var secret = "super-secret-value";

        var encrypted = crypto.Encrypt(secret);
        var decrypted = crypto.Decrypt(encrypted);

        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, decrypted);
        Assert.Equal("aes-gcm-v1", crypto.Version);
    }

    [Fact]
    public async Task SteamGateway_AuthValidateRefresh_Works_WhenLiveCredsProvided()
    {
        var login = Environment.GetEnvironmentVariable("STEAM_TEST_LOGIN");
        var password = Environment.GetEnvironmentVariable("STEAM_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var gateway = new SteamKitGateway(
            NullLogger<SteamKitGateway>.Instance,
            Options.Create(new SteamGatewayOptions()));

        var auth = await gateway.AuthenticateAsync(new SteamCredentials
        {
            LoginName = login,
            Password = password,
            SharedSecret = Environment.GetEnvironmentVariable("STEAM_TEST_SHARED_SECRET"),
            GuardCode = Environment.GetEnvironmentVariable("STEAM_TEST_GUARD_CODE"),
            AllowDeviceConfirmation = true
        });

        Assert.True(auth.Success);
        Assert.False(string.IsNullOrWhiteSpace(auth.SteamId64));
        Assert.False(string.IsNullOrWhiteSpace(auth.Session.CookiePayload));

        var validation = await gateway.ValidateSessionAsync(auth.Session.CookiePayload!);
        Assert.True(validation.IsValid);

        var refreshed = await gateway.RefreshSessionAsync(auth.Session.CookiePayload!);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.CookiePayload));
        Assert.NotEqual(auth.Session.CookiePayload, refreshed.CookiePayload);
    }

    [Fact]
    public void SteamGateway_Parses_SsrRenderContext_FromHtml()
    {
        const string html = """
            <html>
            <script>
            window.SSR.renderContext = {"profile_url":"https://steamcommunity.com/profiles/76561198000000000","OwnedGames":[{"appid":570,"name":"Dota 2","playtime_forever":1234}]};
            </script>
            </html>
            """;

        var parseMethod = typeof(SteamKitGateway).GetMethod(
            "TryParseSsrRenderContext",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(parseMethod);

        var args = new object?[] { html, null };
        var parsed = (bool)parseMethod!.Invoke(null, args)!;

        Assert.True(parsed);
        var json = Assert.IsType<JsonDocument>(args[1]);
        Assert.True(json.RootElement.TryGetProperty("OwnedGames", out var games));
        Assert.Equal(JsonValueKind.Array, games.ValueKind);
        Assert.Equal(1, games.GetArrayLength());
        Assert.Equal(570, games[0].GetProperty("appid").GetInt32());
        json.Dispose();
    }

    [Fact]
    public void SteamGateway_Parses_HiddenInputs_And_SessionId()
    {
        const string html = """
            <html>
            <script>var g_sessionID = "abc-session-id";</script>
            <form>
              <input type="hidden" name="sessionid" value="abc-session-id" />
              <input type="hidden" name="action" value="deauthorize" />
            </form>
            </html>
            """;

        var extractSessionMethod = typeof(SteamKitGateway).GetMethod(
            "ExtractSessionId",
            BindingFlags.Static | BindingFlags.NonPublic);
        var extractHiddenMethod = typeof(SteamKitGateway).GetMethod(
            "ExtractHiddenInputs",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(extractSessionMethod);
        Assert.NotNull(extractHiddenMethod);

        var sessionId = (string?)extractSessionMethod!.Invoke(null, [html]);
        Assert.Equal("abc-session-id", sessionId);

        var hiddenInputs = Assert.IsType<Dictionary<string, string>>(extractHiddenMethod!.Invoke(null, [html]));
        Assert.Equal("abc-session-id", hiddenInputs["sessionid"]);
        Assert.Equal("deauthorize", hiddenInputs["action"]);
    }

    [Fact]
    public void SteamGateway_Detects_CommunityLoginPage_By_Markers()
    {
        const string html = """
            <html>
              <body>
                <div feature-target="login"></div>
                <form><input type="password" name="password" /></form>
              </body>
            </html>
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "LooksLikeCommunityLoginPage",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = (bool)method!.Invoke(null, [html])!;
        Assert.True(result);
    }

    [Theory]
    [InlineData("https://store.steampowered.com/account/changepassword", false)]
    [InlineData("https://store.steampowered.com/account/changepassword/", false)]
    [InlineData("https://store.steampowered.com/account/changepassword/submit", true)]
    [InlineData("https://store.steampowered.com/account/changepasswordajax", true)]
    public void SteamGateway_Filters_StorePasswordSubmit_Endpoints(string url, bool expected)
    {
        var method = typeof(SteamKitGateway).GetMethod(
            "IsAcceptedStorePasswordSubmitEndpoint",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = (bool)method!.Invoke(null, [url])!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SteamGateway_Reads_ProfileUrl_From_Html_Fallback()
    {
        const string html = """
            <html>
              <meta property="og:url" content="https://steamcommunity.com/profiles/76561198000000000">
            </html>
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryReadProfileUrlFromHtml",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var url = (string?)method!.Invoke(null, [html]);
        Assert.Equal("https://steamcommunity.com/profiles/76561198000000000", url);
    }

    [Fact]
    public void SteamGateway_Extracts_QuickInvite_From_UserInfo_DataAttribute()
    {
        const string html = """
            <div id="application_config"
                 data-userinfo="{&quot;steamid&quot;:&quot;76561198715630543&quot;,&quot;short_url&quot;:&quot;https:\/\/s.team\/p\/dtbh-wfrw&quot;}">
            </div>
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryExtractQuickInviteLinkFromHtml",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var args = new object?[] { html, null };
        var parsed = (bool)method!.Invoke(null, args)!;

        Assert.True(parsed);
        var inviteUrl = Assert.IsType<string>(args[1]);
        Assert.Equal("https://s.team/p/dtbh-wfrw", inviteUrl);
    }

    [Fact]
    public void SteamGateway_Parses_AddFriendAjax_FailedInvite_Code()
    {
        const string body = """
            {"failed_invites":["76561198000000000"],"failed_invites_result":[24],"success":1}
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryParseAddFriendAjaxResponse",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var args = new object?[] { body, "76561198000000000", null };
        var parsed = (bool)method!.Invoke(null, args)!;

        Assert.True(parsed);
        var error = Assert.IsType<string>(args[2]);
        Assert.Contains("Steam отклонил приглашение", error);
    }

    [Fact]
    public void SteamGateway_Parses_AddFriendAjax_Success()
    {
        const string body = """
            {"success":1}
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryParseAddFriendAjaxResponse",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var args = new object?[] { body, "76561198000000000", null };
        var parsed = (bool)method!.Invoke(null, args)!;

        Assert.True(parsed);
        Assert.Null(args[2]);
    }

    [Fact]
    public void SteamGateway_Extracts_InviteToken_From_AjaxGetAll_Response()
    {
        const string body = """
            {"success":1,"tokens":[{"invite_token":"oldtoken","valid":false},{"invite_token":"wnmcnjnm","valid":true}]}
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryExtractInviteTokenFromAjaxGetAll",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var args = new object?[] { body, null };
        var parsed = (bool)method!.Invoke(null, args)!;

        Assert.True(parsed);
        Assert.Equal("wnmcnjnm", Assert.IsType<string>(args[1]));
    }

    [Fact]
    public void SteamGateway_Extracts_InviteToken_From_AjaxCreate_Response()
    {
        const string body = """
            {"success":1,"invite":{"invite_token":"abcd1234"}}
            """;

        var method = typeof(SteamKitGateway).GetMethod(
            "TryExtractInviteTokenFromAjaxCreate",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var args = new object?[] { body, null };
        var parsed = (bool)method!.Invoke(null, args)!;

        Assert.True(parsed);
        Assert.Equal("abcd1234", Assert.IsType<string>(args[1]));
    }

    [Fact]
    public void SteamGateway_Generates_SteamGuard_Code_SdaParity_Vector()
    {
        var nested = typeof(SteamKitGateway).GetNestedType("SteamGuardCodeGenerator", BindingFlags.NonPublic);
        Assert.NotNull(nested);

        var method = nested!.GetMethod(
            "GenerateForUnixTime",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var code = (string)method!.Invoke(null, ["MDEyMzQ1Njc4OUFCQ0RFRg==", 1700000000L])!;
        Assert.Equal("5XR77", code);
    }

    [Theory]
    [InlineData("conf", "m3ndpWz6aqlyQ/fA5spbV2NHwdQ=")]
    [InlineData("accept", "Qqfcxnx9LLJ94I4UfdfVJGcsxM4=")]
    [InlineData("reject", "JHdVSnqGCNjc8S0Bq82U37KsFPQ=")]
    public void SteamGateway_Generates_ConfirmationHash_SdaParity_Vector(string tag, string expected)
    {
        var method = typeof(SteamKitGateway).GetMethod(
            "GenerateConfirmationHashForTime",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hash = (string)method!.Invoke(null, ["MDEyMzQ1Njc4OUFCQ0RFRg==", 1700000000L, tag])!;
        Assert.Equal(expected, hash);
    }
}
