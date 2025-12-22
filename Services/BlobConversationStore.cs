
using AIhappey.Core.Conversations.Models;
using Azure.Storage.Blobs;
using System.Text.Json;

namespace AIhappey.Core.Conversations.Services;

public sealed class BlobConversationStore(BlobContainerClient container) : IConversationStore
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> DeleteAsync(string id, string? tenantId = null, CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(BuildBlobName(id, tenantId));
        var exists = await blob.ExistsAsync(ct);
        if (!exists) return false;
        await blob.DeleteAsync(cancellationToken: ct);
        return true;
    }

    public async Task<ConversationDto?> GetAsync(string id, string? tenantId = null, CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(BuildBlobName(id, tenantId));
        if (!await blob.ExistsAsync(ct)) return null;
        var resp = await blob.DownloadContentAsync(ct);
        return resp.Value.Content.ToObjectFromJson<ConversationDto>(JsonOptions);
    }

    public async Task SaveAsync(ConversationDto conversation, string? tenantId = null, CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(BuildBlobName(conversation.Id, tenantId));
        await blob.UploadAsync(BinaryData.FromObjectAsJson(conversation, JsonOptions), overwrite: true, cancellationToken: ct);
    }

    public async Task UpdateAsync(ConversationDto conversation, string? tenantId = null, CancellationToken ct = default) =>
        await SaveAsync(conversation, tenantId, ct);

    public async Task<IEnumerable<ConversationDto>> GetAllAsync(string? tenantId = null, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrWhiteSpace(tenantId) ? "default/" : $"{tenantId}/";
        var results = new List<ConversationDto>();
        var options = new Azure.Storage.Blobs.Models.GetBlobsOptions
        {
            Prefix = prefix
        };

        await foreach (var item in container.GetBlobsAsync(options, cancellationToken: ct))
        {
            var blobClient = container.GetBlobClient(item.Name);
            var resp = await blobClient.DownloadContentAsync(ct);
            var dto = resp.Value.Content.ToObjectFromJson<ConversationDto>(JsonOptions);
            if (dto is not null) results.Add(dto);
        }
        return results;
    }

    static string BuildBlobName(string id, string? tenantId)
        => $"{(string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId)}/{id}.json";
}
