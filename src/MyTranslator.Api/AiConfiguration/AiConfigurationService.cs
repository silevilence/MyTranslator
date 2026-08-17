using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.AiConfiguration;

public sealed class AiConfigurationService(AppDbContext database)
{
    public async Task<IReadOnlyList<AiProviderWithModelsResponse>> ListProvidersAsync(
        CancellationToken cancellationToken)
    {
        var providers = await database.Providers
            .AsNoTracking()
            .Include(provider => provider.Models)
            .ToListAsync(cancellationToken);
        return providers
            .OrderBy(provider => provider.CreatedAt)
            .ThenBy(provider => provider.Id)
            .Select(ToResponseWithModels)
            .ToArray();
    }

    public async Task<AiProviderResponse> CreateProviderAsync(
        ProviderWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (request.IsDefault)
        {
            await ClearDefaultProvidersAsync(cancellationToken);
        }

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Kind = request.Kind,
            BaseUrl = request.BaseUrl,
            ApiKey = NormalizeNewKey(request.ApiKey),
            Enabled = request.Enabled,
            IsDefault = request.IsDefault,
            BatchSize = request.BatchSize,
            RequestTimeout = request.RequestTimeout,
            MaxAttempts = request.MaxAttempts,
            CreatedAt = DateTimeOffset.UtcNow
        };
        database.Providers.Add(provider);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(provider);
    }

    public async Task<AiProviderResponse> UpdateProviderAsync(
        Guid id,
        ProviderWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var provider = await database.Providers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (provider is null)
        {
            throw ProviderNotFound();
        }

        if (request.IsDefault)
        {
            await ClearDefaultProvidersAsync(cancellationToken);
        }

        provider.Name = request.Name;
        provider.Kind = request.Kind;
        provider.BaseUrl = request.BaseUrl;
        if (!ShouldKeepKey(request.ApiKey))
        {
            provider.ApiKey = request.ApiKey;
        }

        provider.Enabled = request.Enabled;
        provider.IsDefault = request.IsDefault;
        provider.BatchSize = request.BatchSize;
        provider.RequestTimeout = request.RequestTimeout;
        provider.MaxAttempts = request.MaxAttempts;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(provider);
    }

    public async Task DeleteProviderAsync(Guid id, CancellationToken cancellationToken)
    {
        var provider = await database.Providers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (provider is null)
        {
            throw ProviderNotFound();
        }

        database.Providers.Remove(provider);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiModelResponse>> ListModelsAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        if (!await database.Providers.AnyAsync(item => item.Id == providerId, cancellationToken))
        {
            throw ProviderNotFound();
        }

        var models = await database.Models
            .AsNoTracking()
            .Where(model => model.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        return models
            .OrderBy(model => model.CreatedAt)
            .ThenBy(model => model.Id)
            .Select(ToResponse)
            .ToArray();
    }

    public async Task<AiModelResponse> CreateModelAsync(
        Guid providerId,
        ModelWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await database.Providers.AnyAsync(item => item.Id == providerId, cancellationToken))
        {
            throw ProviderNotFound();
        }

        if (await database.Models.AnyAsync(
                model => model.ProviderId == providerId && model.ModelId == request.ModelId,
                cancellationToken))
        {
            throw ModelConflict(providerId, request.ModelId);
        }

        if (request.IsDefault)
        {
            await ClearDefaultModelsAsync(providerId, cancellationToken);
        }

        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            ModelId = request.ModelId,
            DisplayName = request.DisplayName,
            SupportsThinking = request.SupportsThinking,
            SupportsToolUse = request.SupportsToolUse,
            SupportsStreaming = request.SupportsStreaming,
            IsDefault = request.IsDefault,
            CreatedAt = DateTimeOffset.UtcNow
        };
        database.Models.Add(model);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            throw ModelConflict(providerId, request.ModelId);
        }

        await transaction.CommitAsync(cancellationToken);
        return ToResponse(model);
    }

    public async Task<AiModelResponse> UpdateModelAsync(
        Guid providerId,
        Guid id,
        ModelWriteRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await database.Providers.AnyAsync(item => item.Id == providerId, cancellationToken))
        {
            throw ProviderNotFound();
        }

        var model = await database.Models.SingleOrDefaultAsync(
            item => item.Id == id && item.ProviderId == providerId,
            cancellationToken);
        if (model is null)
        {
            throw ModelNotFound();
        }

        if (await database.Models.AnyAsync(
                item => item.ProviderId == providerId && item.Id != id && item.ModelId == request.ModelId,
                cancellationToken))
        {
            throw ModelConflict(providerId, request.ModelId);
        }

        if (request.IsDefault)
        {
            await ClearDefaultModelsAsync(providerId, cancellationToken);
        }

        model.ModelId = request.ModelId;
        model.DisplayName = request.DisplayName;
        model.SupportsThinking = request.SupportsThinking;
        model.SupportsToolUse = request.SupportsToolUse;
        model.SupportsStreaming = request.SupportsStreaming;
        model.IsDefault = request.IsDefault;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            throw ModelConflict(providerId, request.ModelId);
        }

        await transaction.CommitAsync(cancellationToken);
        return ToResponse(model);
    }

    public async Task DeleteModelAsync(Guid providerId, Guid id, CancellationToken cancellationToken)
    {
        if (!await database.Providers.AnyAsync(item => item.Id == providerId, cancellationToken))
        {
            throw ProviderNotFound();
        }

        var model = await database.Models.SingleOrDefaultAsync(
            item => item.Id == id && item.ProviderId == providerId,
            cancellationToken);
        if (model is null)
        {
            throw ModelNotFound();
        }

        database.Models.Remove(model);
        await database.SaveChangesAsync(cancellationToken);
    }

    public static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return key.Length <= 8 ? "***" : $"{key[..3]}***{key[^4..]}";
    }

    public static bool ShouldKeepKey(string? key) =>
        key is null || IsMaskedKey(key);

    private static bool IsMaskedKey(string key) =>
        key == "***" ||
        (key.Length == 10 && key.AsSpan(3, 3).SequenceEqual("***"));

    private static string? NormalizeNewKey(string? key) => ShouldKeepKey(key) ? null : key;

    private async Task ClearDefaultProvidersAsync(CancellationToken cancellationToken)
    {
        await database.Providers
            .Where(provider => provider.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(provider => provider.IsDefault, false), cancellationToken);
        foreach (var provider in database.ChangeTracker.Entries<AiProvider>())
        {
            provider.Entity.IsDefault = false;
        }
    }

    private async Task ClearDefaultModelsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await database.Models
            .Where(model => model.ProviderId == providerId && model.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(model => model.IsDefault, false), cancellationToken);
        foreach (var model in database.ChangeTracker.Entries<AiModel>()
                     .Where(entry => entry.Entity.ProviderId == providerId))
        {
            model.Entity.IsDefault = false;
        }
    }

    private static AiProviderWithModelsResponse ToResponseWithModels(AiProvider provider) => new(
        provider.Id,
        provider.Name,
        provider.Kind,
        provider.BaseUrl,
        MaskKey(provider.ApiKey),
        provider.Enabled,
        provider.IsDefault,
        provider.BatchSize,
        provider.RequestTimeout,
        provider.MaxAttempts,
        provider.CreatedAt,
        provider.Models.OrderBy(model => model.CreatedAt).ThenBy(model => model.Id).Select(ToResponse).ToArray());

    private static AiProviderResponse ToResponse(AiProvider provider) => new(
        provider.Id,
        provider.Name,
        provider.Kind,
        provider.BaseUrl,
        MaskKey(provider.ApiKey),
        provider.Enabled,
        provider.IsDefault,
        provider.BatchSize,
        provider.RequestTimeout,
        provider.MaxAttempts,
        provider.CreatedAt);

    private static AiModelResponse ToResponse(AiModel model) => new(
        model.Id,
        model.ProviderId,
        model.ModelId,
        model.DisplayName,
        model.SupportsThinking,
        model.SupportsToolUse,
        model.SupportsStreaming,
        model.IsDefault,
        model.CreatedAt);

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private static AiConfigurationRequestException ProviderNotFound() => new(
        "provider_not_found",
        "Provider not found.",
        StatusCodes.Status404NotFound);

    private static AiConfigurationRequestException ModelNotFound() => new(
        "model_not_found",
        "Model not found.",
        StatusCodes.Status404NotFound);

    private static AiConfigurationRequestException ModelConflict(Guid providerId, string modelId) => new(
        "model_id_conflict",
        "The provider already has a model with this model ID.",
        StatusCodes.Status409Conflict,
        new Dictionary<string, object?>
        {
            ["providerId"] = providerId,
            ["modelId"] = modelId
        });
}
