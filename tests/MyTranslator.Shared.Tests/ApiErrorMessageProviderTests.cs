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

    [Theory]
    [InlineData("task_not_exportable", "任务暂不可导出：译文不完整或占位符不合法")]
    [InlineData("translation_loss_confirmation_required", "重新提取将丢失已有译文，需确认后继续")]
    [InlineData("unable_to_infer_file_type", "无法推断文件类型，请显式指定")]
    [InlineData("selector_no_match", "选择器在所有目标文档中均无匹配")]
    [InlineData("extraction_preview_expired", "提取预览已过期，请重新创建")]
    [InlineData("content_too_large", "内容超过大小上限")]
    public void ForError_已知错误码优先映射文案(string code, string expected)
    {
        Assert.Equal(expected, Create().ForError(409, code));
    }

    [Fact]
    public void ForError_未知错误码_降级为状态码映射()
    {
        var provider = Create();
        Assert.Equal(provider.For(404), provider.ForError(404, "future_unknown_code"));
        Assert.Equal(provider.For(418), provider.ForError(418, null));
        Assert.Equal(provider.For(null), provider.ForError(null, "future_unknown_code"));
    }

    [Theory]
    [InlineData("segment_translation_failed", "部分分段翻译失败，详情见失败列表")]
    [InlineData("llm_provider_unavailable", "AI 服务暂时不可用，请稍后重试")]
    [InlineData("llm_authentication_failed", "AI 服务拒绝了服务端密钥，请检查服务端配置")]
    [InlineData("placeholder_integrity_violation", "AI 译文破坏了占位符标记，未保存")]
    [InlineData("translation_interrupted", "翻译运行被中断，已成功译文已保留")]
    public void ForFailureCode_已知失败码映射文案(string code, string expected)
    {
        Assert.Equal(expected, Create().ForFailureCode(code));
    }

    [Fact]
    public void ForFailureCode_未知失败码_降级为通用失败文案()
    {
        var provider = Create();
        Assert.Equal("请求失败，请稍后重试", provider.ForFailureCode("future_failure_code"));
        Assert.Equal("请求失败，请稍后重试", provider.ForFailureCode(null));
    }

    [Fact]
    public void ForError_ApiErrorException_按code映射()
    {
        var exception = new ApiErrorException(409, "task_busy", null, null, "test");
        Assert.Equal("任务正在执行其他操作，请稍后重试", Create().ForError(exception));
    }

    [Fact]
    public void ForException_按异常类型映射()
    {
        var provider = Create();
        Assert.Equal("任务正在执行其他操作，请稍后重试",
            provider.ForException(new ApiErrorException(409, "task_busy", null, null, "test")));
        Assert.Equal("无法连接后端，请确认后端服务已启动",
            provider.ForException(new HttpRequestException("network down")));
        Assert.Equal(provider.For(null), provider.ForException(new InvalidOperationException("unknown")));
    }
}
