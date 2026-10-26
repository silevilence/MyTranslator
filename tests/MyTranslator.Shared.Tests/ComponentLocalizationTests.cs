using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using MyTranslator.Shared.Components;

namespace MyTranslator.Shared.Tests;

public sealed class ComponentLocalizationTests
{
    [Theory]
    [InlineData(typeof(ProviderEditDialog), "NameLabel")]
    [InlineData(typeof(ProviderEditDialog), "KindOpenai")]
    [InlineData(typeof(ProviderEditDialog), "Cancel")]
    [InlineData(typeof(ModelEditDialog), "ModelIdLabel")]
    [InlineData(typeof(ModelEditDialog), "Save")]
    public void DialogLabelsResolveThroughActualLocalizer(Type component, string key)
    {
        var factory = new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = factory.Create(component);
        var value = localizer[key];
        Assert.False(value.ResourceNotFound, $"{component.Name}.{key} displays a resource key.");
        Assert.NotEqual(key, value.Value);
    }

    [Fact]
    public void EveryComponentResourceHasTheExpectedManifestName()
    {
        var assembly = typeof(ProviderEditDialog).Assembly;
        var names = assembly.GetManifestResourceNames();
        foreach (var name in names.Where(name => name.StartsWith("MyTranslator.Shared.Resources.") && name.EndsWith(".resources")))
        {
            var componentName = name.Replace(".Resources.", ".Components.").Replace(".resources", "");
            Assert.Null(assembly.GetType(componentName));
        }
    }
}
