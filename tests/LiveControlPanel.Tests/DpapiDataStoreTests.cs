using LiveControlPanel.Youtube;
using Xunit;

namespace LiveControlPanel.Tests;

public sealed class DpapiDataStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lcp-tests", Guid.NewGuid().ToString("N"));

    private string TokenPath => Path.Combine(_dir, "token.bin");

    /// <summary>
    /// Google's flow rewrites the token file through the store on every hourly access-token
    /// refresh. Deriving the authorization age from the file's write time therefore reset the
    /// FR 8 expiry countdown forever — it must come from the explicit stamp instead.
    /// </summary>
    [Fact]
    public async Task The_authorization_stamp_survives_token_refreshes()
    {
        var store = new DpapiDataStore(TokenPath);
        await store.StoreAsync("panel", new FakeToken("first"));

        var authorizedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        store.MarkAuthorized(authorizedAt);

        await store.StoreAsync("panel", new FakeToken("refreshed"));
        await store.StoreAsync("panel", new FakeToken("refreshed-again"));

        Assert.Equal(authorizedAt, store.AuthorizedAtUtc);
        Assert.Equal("refreshed-again", (await store.GetAsync<FakeToken>("panel")).Value);
    }

    [Fact]
    public async Task Clearing_the_store_clears_the_stamp_too()
    {
        var store = new DpapiDataStore(TokenPath);
        await store.StoreAsync("panel", new FakeToken("first"));
        store.MarkAuthorized(DateTime.UtcNow);

        await store.ClearAsync();

        Assert.Null(store.AuthorizedAtUtc);
    }

    private sealed record FakeToken(string Value);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* stray handle on a temp dir is not worth failing a test over */ }
    }
}
