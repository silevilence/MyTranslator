using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class TokenStoreTests
{
    [Fact]
    public async Task SaveAsync_GetAsync_往返一致()
    {
        var js = new FakeJSRuntime();
        var store = new TokenStore(js);

        Assert.Null(await store.GetAsync());

        await store.SaveAsync("sk-dev-test");

        Assert.Equal("sk-dev-test", await store.GetAsync());
    }

    [Fact]
    public async Task ClearAsync_清空已保存Token()
    {
        var js = new FakeJSRuntime();
        var store = new TokenStore(js);
        await store.SaveAsync("sk-dev-test");

        await store.ClearAsync();

        Assert.Null(await store.GetAsync());
        Assert.True(js.IsRemoved("mt.token"));
    }
}
