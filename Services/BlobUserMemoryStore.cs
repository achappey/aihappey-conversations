using AIhappey.Core.Conversations.Models;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text.Json;

namespace AIhappey.Core.Conversations.Services;

public sealed class BlobUserMemoryStore(BlobContainerClient container) : IUserMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<UserMemoryDto>> ListAsync(
        string userId,
        CancellationToken ct = default)
    {
        var prefix = BuildPrefix(userId);
        var results = new List<UserMemoryDto>();

        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: ct))
        {
            if (!TryGetMemoryId(item.Name, prefix, out var memoryId)) continue;

            var blob = container.GetBlobClient(item.Name);
            BlobDownloadResult download;
            try
            {
                download = (await blob.DownloadContentAsync(ct)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }

            var content = Deserialize(download.Content, memoryId);
            var createdAt = item.Properties.CreatedOn
                ?? download.Details.LastModified;
            var updatedAt = item.Properties.LastModified
                ?? download.Details.LastModified;

            results.Add(ToDto(memoryId, content, createdAt, updatedAt, truncateMemory: true));
        }

        return results
            .OrderByDescending(memory => memory.UpdatedAt)
            .ThenByDescending(memory => memory.CreatedAt)
            .ThenBy(memory => memory.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<UserMemoryDto?> GetAsync(
        string userId,
        string memoryId,
        CancellationToken ct = default)
    {
        var blob = GetBlob(userId, memoryId);
        try
        {
            var download = (await blob.DownloadContentAsync(ct)).Value;
            var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            var content = Deserialize(download.Content, memoryId);
            return ToDto(memoryId, content, properties.CreatedOn, properties.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<UserMemoryDto> CreateAsync(
        string userId,
        UserMemoryContentDto content,
        CancellationToken ct = default)
    {
        ValidateContent(content);

        while (true)
        {
            var memoryId = Guid.NewGuid().ToString("N");
            var blob = GetBlob(userId, memoryId);
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            };

            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(content, JsonOptions), options, ct);
                var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
                return ToDto(memoryId, content, properties.CreatedOn, properties.LastModified);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                // An exceptionally unlikely generated-id collision: generate another id.
            }
        }
    }

    public async Task<UserMemoryDto?> UpdateAsync(
        string userId,
        string memoryId,
        UserMemoryContentDto content,
        CancellationToken ct = default)
    {
        ValidateContent(content);
        var blob = GetBlob(userId, memoryId);

        BlobProperties current;
        try
        {
            current = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Conditions = new BlobRequestConditions { IfMatch = current.ETag }
        };

        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(content, JsonOptions), options, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var updated = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        return ToDto(memoryId, content, updated.CreatedOn, updated.LastModified);
    }

    public async Task<bool> DeleteAsync(
        string userId,
        string memoryId,
        CancellationToken ct = default)
    {
        var blob = GetBlob(userId, memoryId);
        return (await blob.DeleteIfExistsAsync(cancellationToken: ct)).Value;
    }

    private BlobClient GetBlob(string userId, string memoryId)
        => container.GetBlobClient($"{BuildPrefix(userId)}{ValidateMemoryId(memoryId)}.json");

    private static string BuildPrefix(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || userId.Contains('/')
            || userId.Contains('\\'))
            throw new ArgumentException("A valid user id is required.", nameof(userId));

        return $"{userId}/memories/";
    }

    private static string ValidateMemoryId(string memoryId)
    {
        if (!Guid.TryParseExact(memoryId, "N", out _))
            throw new ArgumentException("A valid memory id is required.", nameof(memoryId));
        return memoryId;
    }

    private static bool TryGetMemoryId(string blobName, string prefix, out string memoryId)
    {
        memoryId = string.Empty;
        if (!blobName.StartsWith(prefix, StringComparison.Ordinal)
            || !blobName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = blobName[prefix.Length..^5];
        if (candidate.Contains('/') || !Guid.TryParseExact(candidate, "N", out _))
            return false;

        memoryId = candidate;
        return true;
    }

    private static void ValidateContent(UserMemoryContentDto content)
    {
        if (string.IsNullOrWhiteSpace(content.Subject))
            throw new ArgumentException("A non-empty subject is required.", nameof(content));
        if (string.IsNullOrWhiteSpace(content.Memory))
            throw new ArgumentException("A non-empty memory is required.", nameof(content));
    }

    private static UserMemoryContentDto Deserialize(BinaryData data, string memoryId)
        => data.ToObjectFromJson<UserMemoryContentDto>(JsonOptions)
            ?? throw new InvalidDataException($"Memory '{memoryId}' could not be deserialized.");

    private static UserMemoryDto ToDto(
        string memoryId,
        UserMemoryContentDto content,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool truncateMemory = false) => new()
    {
        Id = memoryId,
        Subject = content.Subject,
        Memory = truncateMemory && content.Memory.Length > 100
            ? content.Memory[..100]
            : content.Memory,
        CreatedAt = createdAt,
        UpdatedAt = updatedAt
    };
}
