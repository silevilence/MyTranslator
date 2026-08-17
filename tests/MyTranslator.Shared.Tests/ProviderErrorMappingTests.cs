using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTranslator.Shared.Resources;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 配置类错误码映射回归（AI 配置管理接口约定 §7 / AI 翻译接口约定 §10.1）：
/// 前端错误文案必须来自错误码映射，禁止页面自行解释。
/// </summary>
public class ProviderErrorMappingTests
{
    private static ApiErrorMessageProvider Create()
    {
        var options = Options.Create(new LocalizationOptions());
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new ApiErrorMessageProvider(new StringLocalizer<Errors>(factory));
    }

    [Theory]
    [InlineData(404, "provider_not_found", "AI 提供商不存在或已被删除，请前往设置检查配置")]
    [InlineData(422, "model_not_found", "AI 模型不存在或不属于所选提供商，请前往设置检查配置")]
    [InlineData(422, "provider_disabled", "AI 提供商已停用，请前往设置启用后重试")]
    [InlineData(400, "invalid_provider_name", "提供商名称不能为空")]
    [InlineData(400, "invalid_provider_kind", "连接器类型无效（仅支持 openai / ollama）")]
    [InlineData(400, "invalid_base_url", "BaseUrl 须为绝对 http(s) 地址")]
    [InlineData(400, "invalid_runtime_settings", "运行参数超出合法范围（批大小 1..200，超时须大于 0，尝试次数 1..10）")]
    [InlineData(400, "invalid_provider_key_field", "密钥字段提交无效，请重新输入")]
    [InlineData(409, "model_id_conflict", "该厂商模型 ID 在同一提供商下已存在")]
    public void ForError_配置类错误码映射文案(int status, string code, string expected)
    {
        Assert.Equal(expected, Create().ForError(status, code));
    }

    [Fact]
    public void ForError_llmNotConfigured_提示配置入口()
    {
        Assert.Equal("尚未配置可用的 AI 提供商/模型，请前往设置完成配置后重试", Create().ForError(503, "llm_not_configured"));
    }
}
