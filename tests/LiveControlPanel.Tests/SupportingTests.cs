using System.Net;
using System.Net.WebSockets;
using Google;
using Google.Apis.Requests;
using LiveControlPanel.Core;
using LiveControlPanel.Net;
using LiveControlPanel.Obs;
using LiveControlPanel.Slides;
using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

/// <summary>obs-websocket v5 SHA256 challenge (FR 2.1) and the subscription mask FR 4.4 depends on.</summary>
public class ObsProtocolTests
{
    /// <summary>
    /// Pins the exact composition the v5 handshake requires:
    /// base64(sha256(base64(sha256(password + salt)) + challenge)).
    /// Spelled out step by step so a reordering or a dropped base64 in the implementation fails here.
    /// </summary>
    [Fact]
    public void Authentication_is_the_salted_then_challenged_double_hash()
    {
        const string password = "supersecretpassword";
        const string salt = "PZVbYpvAnZut2SS6JNJytDm9";
        const string challenge = "ztTBnnuqrqaKDzRM3xcVdbYm";

        var secret = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password + salt)));
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret + challenge)));

        Assert.Equal(expected, ObsAuth.BuildAuthentication(password, salt, challenge));
    }

    [Fact]
    public void Authentication_produces_a_base64_encoded_sha256_digest()
    {
        var actual = ObsAuth.BuildAuthentication("pw", "salt", "challenge");

        var bytes = Convert.FromBase64String(actual);
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public void Authentication_would_not_pass_if_salt_and_challenge_were_swapped()
    {
        // Guards the single most likely implementation slip.
        Assert.NotEqual(
            ObsAuth.BuildAuthentication("pw", "salt", "challenge"),
            ObsAuth.BuildAuthentication("pw", "challenge", "salt"));
    }

    [Fact]
    public void Authentication_changes_when_any_input_changes()
    {
        var baseline = ObsAuth.BuildAuthentication("pw", "salt", "chal");

        Assert.NotEqual(baseline, ObsAuth.BuildAuthentication("pw2", "salt", "chal"));
        Assert.NotEqual(baseline, ObsAuth.BuildAuthentication("pw", "salt2", "chal"));
        Assert.NotEqual(baseline, ObsAuth.BuildAuthentication("pw", "salt", "chal2"));
    }

    [Fact]
    public void Authentication_is_deterministic_and_password_sensitive()
    {
        var a = ObsAuth.BuildAuthentication("pw", "salt", "chal");
        var b = ObsAuth.BuildAuthentication("pw", "salt", "chal");
        var c = ObsAuth.BuildAuthentication("pw2", "salt", "chal");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Volume_meters_are_not_part_of_the_all_mask_and_must_be_requested_explicitly()
    {
        // This is the whole reason for the hand-rolled client: a library that sends only All never
        // receives volume meters, and FR 4.4's audio check needs them.
        Assert.Equal(0, (int)ObsEventSubscription.All & (int)ObsEventSubscription.InputVolumeMeters);
        Assert.Equal(1 << 16, (int)ObsEventSubscription.InputVolumeMeters);

        var mask = ObsEventSubscription.All | ObsEventSubscription.InputVolumeMeters;
        Assert.True(mask.HasFlag(ObsEventSubscription.InputVolumeMeters));
        Assert.True(mask.HasFlag(ObsEventSubscription.Outputs));
    }
}

/// <summary>
/// Classifying "nothing is listening" correctly is what lets the pre-flight name the real fix (a
/// running OBS with its WebSocket server switched off). The signal is buried several levels down the
/// exception chain, and checking only one level misclassified the most common cause in the field.
/// </summary>
public class ObsConnectionDiagnosisTests
{
    [Fact]
    public void Connection_refused_is_found_however_deeply_it_is_wrapped()
    {
        var refused = new System.Net.Sockets.SocketException(10061);   // WSAECONNREFUSED

        // The shape ClientWebSocket actually produces.
        var wrapped = new WebSocketException(
            "Unable to connect to the remote server",
            new HttpRequestException("actively refused", refused));

        Assert.True(ObsClient.IsConnectionRefused(refused));
        Assert.True(ObsClient.IsConnectionRefused(new HttpRequestException("x", refused)));
        Assert.True(ObsClient.IsConnectionRefused(wrapped));
    }

    [Fact]
    public void Other_failures_are_not_reported_as_nothing_listening()
    {
        Assert.False(ObsClient.IsConnectionRefused(null));
        Assert.False(ObsClient.IsConnectionRefused(new Exception("boom")));

        // A timeout or a reset is a different problem and must not claim the server is switched off.
        Assert.False(ObsClient.IsConnectionRefused(
            new WebSocketException("timeout", new System.Net.Sockets.SocketException(10060))));
        Assert.False(ObsClient.IsConnectionRefused(
            new HttpRequestException("reset", new System.Net.Sockets.SocketException(10054))));
    }
}

/// <summary>FR 6.4: no loopback and no virtual adapters in the address list (T-17).</summary>
public class AccessInfoTests
{
    [Theory]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Network")]
    [InlineData("VMware Network Adapter VMnet1")]
    [InlineData("Software Loopback Interface 1")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("Tailscale Tunnel")]
    [InlineData("ZeroTier One")]
    [InlineData("WSL (Hyper-V firewall)")]
    [InlineData("Microsoft Teredo Tunneling Adapter")]
    public void Virtual_adapters_are_filtered_out(string name)
    {
        Assert.True(AccessInfoProvider.IsVirtual(name));
    }

    [Theory]
    [InlineData("Wi-Fi")]
    [InlineData("Ethernet")]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz")]
    [InlineData("Realtek PCIe GbE Family Controller")]
    [InlineData("以太网")]
    public void Real_adapters_are_kept(string name)
    {
        Assert.False(AccessInfoProvider.IsVirtual(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_adapter_names_are_not_treated_as_virtual(string? name)
    {
        Assert.False(AccessInfoProvider.IsVirtual(name));
    }

    [Fact]
    public void Discovered_addresses_are_routable_ipv4_only()
    {
        foreach (var (address, adapter) in AccessInfoProvider.UsableAddresses())
        {
            var parsed = IPAddress.Parse(address);
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));
            Assert.DoesNotContain("169.254.", address);
            Assert.False(AccessInfoProvider.IsVirtual(adapter));
        }
    }

    [Fact]
    public void Access_info_always_produces_a_scannable_qr_code_and_carries_the_access_code()
    {
        using var host = new TestHost();

        var info = new AccessInfoProvider(host.Config).Get();

        Assert.False(string.IsNullOrWhiteSpace(info.QrPngBase64));
        var png = Convert.FromBase64String(info.QrPngBase64);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());

        Assert.Contains($"?k={host.Config.Settings.AccessCode}", info.LocalUrl);
        Assert.All(info.Addresses, a => Assert.Contains($"?k={host.Config.Settings.AccessCode}", a.Url));
        Assert.All(info.Addresses, a => Assert.StartsWith("http://", a.Url));
    }

    [Fact]
    public void Access_info_is_recomputed_on_each_call_so_an_ip_change_is_picked_up()
    {
        // T-20: nothing is cached, so a new IP shows up without restarting the service.
        using var host = new TestHost();
        var provider = new AccessInfoProvider(host.Config);

        var first = provider.Get();
        host.Config.UpdateSettings(s => s.Port = 6001);
        var second = provider.Get();

        Assert.Contains(":6001", second.LocalUrl);
        Assert.NotEqual(first.LocalUrl, second.LocalUrl);
    }
}

/// <summary>FR 8: nothing an operator reads may contain a status code or a stack trace.</summary>
public class FriendlyErrorTests
{
    [Fact]
    public void Quota_exhaustion_is_explained_in_plain_language()
    {
        var ex = new GoogleApiException("youtube", "quota")
        {
            Error = new RequestError
            {
                Errors = new List<SingleError> { new() { Reason = "quotaExceeded" } },
            },
            HttpStatusCode = HttpStatusCode.Forbidden,
        };

        var message = FriendlyError.Describe(ex).Zh;

        Assert.Equal("今天创建直播的次数已达上限，请联系管理员。", message);
        Assert.DoesNotContain("403", message);
        Assert.DoesNotContain("quota", message);
    }

    [Fact]
    public void An_inactive_stream_tells_the_operator_to_check_obs()
    {
        var ex = new GoogleApiException("youtube", "inactive")
        {
            Error = new RequestError
            {
                Errors = new List<SingleError> { new() { Reason = "errorStreamInactive" } },
            },
        };

        Assert.Contains("OBS", FriendlyError.Describe(ex).Zh);
    }

    [Fact]
    public void Missing_authorization_points_at_the_settings_page()
    {
        Assert.Contains("重新授权", FriendlyError.Describe(new NotAuthorizedException()).Zh);
    }

    [Fact]
    public void A_disconnected_obs_says_to_open_obs()
    {
        Assert.Contains("OBS", FriendlyError.Describe(new ObsUnavailableException()).Zh);
    }

    [Fact]
    public void Network_failures_mention_the_network_not_the_exception()
    {
        var message = FriendlyError.Describe(new HttpRequestException("No such host is known.")).Zh;

        Assert.Contains("网络", message);
        Assert.DoesNotContain("host", message);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(NotAuthorizedException))]
    [InlineData(typeof(ObsUnavailableException))]
    public void No_message_ever_leaks_technical_vocabulary(Type exceptionType)
    {
        var ex = exceptionType == typeof(InvalidOperationException)
            ? new InvalidOperationException("请让管理员在设置页创建推流密钥。")
            : (Exception)Activator.CreateInstance(exceptionType)!;

        var message = FriendlyError.Describe(ex).Zh;

        Assert.False(string.IsNullOrWhiteSpace(message));
        foreach (var forbidden in new[] { "Exception", "System.", "stack", "null reference", "HTTP " })
            Assert.DoesNotContain(forbidden, message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Which YouTube failures are worth retrying (FR 8) — and which would only waste time.</summary>
public class RetryTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void Transient_http_statuses_are_retried(HttpStatusCode status)
    {
        var ex = new GoogleApiException("youtube", "boom") { HttpStatusCode = status };
        Assert.True(Retry.IsTransient(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void Permanent_http_statuses_are_not_retried(HttpStatusCode status)
    {
        var ex = new GoogleApiException("youtube", "boom") { HttpStatusCode = status };
        Assert.False(Retry.IsTransient(ex));
    }

    [Fact]
    public void Network_and_timeout_failures_are_retried()
    {
        Assert.True(Retry.IsTransient(new HttpRequestException()));
        Assert.True(Retry.IsTransient(new TaskCanceledException()));
        Assert.True(Retry.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void Programming_errors_are_not_retried()
    {
        Assert.False(Retry.IsTransient(new InvalidOperationException()));
        Assert.False(Retry.IsTransient(new NotAuthorizedException()));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        var attempts = 0;

        var result = await Retry.TransientAsync(_ =>
        {
            attempts++;
            if (attempts < 3) throw new HttpRequestException("flaky");
            return Task.FromResult("ok");
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.TransientAsync<string>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("nope");
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None));

        Assert.Equal(1, attempts);
    }
}

/// <summary>
/// FR 5.3: the slide-show window is matched by configured class/title, never guessed — a wrong guess
/// would fire arrow keys into an arbitrary application.
/// </summary>
public class SlideWindowMatchingTests
{
    private static readonly WindowInfo[] Windows =
    {
        new(1, "screenClass", "幻灯片放映 - [sermon.pptx]", true),
        new(2, "Chrome_WidgetWin_1", "直播控制面板 - Chrome", true),
        new(3, "screenClass", "hidden show", false),
        new(4, "Qt5152QWindowIcon", "WPS Presentation", true),
    };

    [Fact]
    public void No_configuration_matches_nothing()
    {
        Assert.Null(SlideController.MatchWindow(Windows, "", ""));
        Assert.Null(SlideController.MatchWindow(Windows, null, null));
    }

    [Fact]
    public void Class_name_alone_matches()
    {
        var match = SlideController.MatchWindow(Windows, "screenClass", "");
        Assert.Equal(1, match!.Handle);
    }

    [Fact]
    public void Class_name_matching_is_case_insensitive()
    {
        Assert.NotNull(SlideController.MatchWindow(Windows, "SCREENCLASS", ""));
    }

    [Fact]
    public void Title_regex_alone_matches()
    {
        var match = SlideController.MatchWindow(Windows, "", "WPS Presentation");
        Assert.Equal(4, match!.Handle);
    }

    [Fact]
    public void Class_and_title_must_both_match_when_both_are_configured()
    {
        Assert.NotNull(SlideController.MatchWindow(Windows, "screenClass", "sermon"));
        Assert.Null(SlideController.MatchWindow(Windows, "screenClass", "WPS Presentation"));
    }

    [Fact]
    public void Invisible_windows_are_never_matched()
    {
        // Handle 3 shares the class but is hidden, so handle 1 must win.
        var match = SlideController.MatchWindow(Windows, "screenClass", "hidden show");
        Assert.Null(match);
    }

    [Fact]
    public void An_invalid_title_regex_matches_nothing_instead_of_throwing()
    {
        Assert.Null(SlideController.MatchWindow(Windows, "", "[unclosed"));
    }
}
