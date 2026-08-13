using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTranslator.Shared.Resources;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ApiErrorMessageProviderTests
{
    private static ApiErrorMessageProvider Create()
    {
        var options = Options.Create(new LocalizationOptions());
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new ApiErrorMessageProvider(new StringLocalizer<Errors>(factory));
    }

    [Fact]
    public void For_网络不可达返回连通性文案()
    {
        var message = Create().For(null);
        Assert.Equal("无法连接后端，请确认后端服务已启动", message);
    }

    [Theory]
    [InlineData(400, "请求参数错误，请检查输入后重试")]
    [InlineData(401, "Token 无效或已撤销，请重新登录")]
    [InlineData(403, "无权执行该操作")]
    [InlineData(404, "请求的资源不存在")]
    [InlineData(500, "后端服务异常，请稍后重试")]
    [InlineData(503, "后端服务异常，请稍后重试")]
    [InlineData(418, "请求失败，请稍后重试")]
    public void For_按HTTP状态码映射文案(int status, string expected)
    {
        Assert.Equal(expected, Create().For(status));
    }
}
