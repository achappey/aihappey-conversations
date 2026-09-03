
using AIhappey.Core.Conversations.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;
using System.Text.Json;
using System.Text;

namespace AIhappey.Core.Conversations.Services;

public sealed class BlobConversationStore(BlobContainerClient container) : IConversationStore
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    const int MutationAttempts = 5;
    const string NameMetadataKey = "aihname";
    const string MessageCountMetadataKey = "aihmessagecount";
    const string ActivityAtMetadataKey = "aihactivityat";
    const string DefaultName = "New chat";

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
        BlobProperties? properties = null;
        try
        {
            properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // A new conversation has no metadata to preserve.
        }

        var metadata = properties is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(properties.Metadata, StringComparer.OrdinalIgnoreCase);

        var previousCount = TryReadMessageCount(metadata, out var storedCount) ? storedCount : (int?)null;
        var latestActivity = GetLatestActivity(conversation);
        var activityAt = previousCount == conversation.Messages.Count
            && TryReadTimestamp(metadata, ActivityAtMetadataKey, out var storedActivity)
                ? storedActivity
                : latestActivity ?? properties?.LastModified ?? DateTimeOffset.UtcNow;

        WriteSummaryMetadata(metadata, GetName(conversation), conversation.Messages.Count, activityAt);

        var options = new BlobUploadOptions { Metadata = metadata };
        if (properties is not null)
            options.Conditions = new BlobRequestConditions { IfMatch = properties.ETag };
        else
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };

        await blob.UploadAsync(BinaryData.FromObjectAsJson(conversation, JsonOptions), options, ct);
    }

    public async Task UpdateAsync(ConversationDto conversation, string? tenantId = null, CancellationToken ct = default) =>
        await SaveAsync(conversation, tenantId, ct);

    public Task<ConversationMutationResult> AddMessageAsync(
        string conversationId,
        AIHappey.Vercel.Models.UIMessage message,
        string? tenantId = null,
        CancellationToken ct = default) =>
        MutateAsync(
            conversationId,
            conversation =>
            {
                // A repeated POST can happen when a client retries after losing the
                // response. Treat the stable message id as the idempotency key.
                if (conversation.Messages.Any(item => item.Id == message.Id))
                    return ConversationMutationResult.NoChange;

                conversation.Messages.Add(message);
                return ConversationMutationResult.Success;
            },
            tenantId,
            ct);

    public Task<ConversationMutationResult> UpdateMessageAsync(
        string conversationId,
        string messageId,
        ConversationMessagePatchDto patch,
        string? tenantId = null,
        CancellationToken ct = default) =>
        MutateAsync(
            conversationId,
            conversation =>
            {
                var index = conversation.Messages.FindIndex(item => item.Id == messageId);
                if (index < 0) return ConversationMutationResult.MessageNotFound;

                var current = conversation.Messages[index];
                conversation.Messages[index] = new AIHappey.Vercel.Models.UIMessage
                {
                    Id = current.Id,
                    Role = patch.Role ?? current.Role,
                    Parts = patch.Parts ?? current.Parts,
                    Metadata = patch.MetadataSpecified ? patch.Metadata : current.Metadata
                };
                return ConversationMutationResult.Success;
            },
            tenantId,
            ct);

    public Task<ConversationMutationResult> DeleteMessageAsync(
        string conversationId,
        string messageId,
        string? tenantId = null,
        CancellationToken ct = default) =>
        MutateAsync(
            conversationId,
            conversation =>
            {
                var index = conversation.Messages.FindIndex(item => item.Id == messageId);
                if (index < 0) return ConversationMutationResult.MessageNotFound;

                conversation.Messages.RemoveAt(index);
                return ConversationMutationResult.Success;
            },
            tenantId,
            ct);

    async Task<ConversationMutationResult> MutateAsync(
        string conversationId,
        Func<ConversationDto, ConversationMutationResult> mutate,
        string? tenantId,
        CancellationToken ct)
    {
        var blob = container.GetBlobClient(BuildBlobName(conversationId, tenantId));

        for (var attempt = 0; attempt < MutationAttempts; attempt++)
        {
            Azure.Response<BlobDownloadResult> download;
            try
            {
                download = await blob.DownloadContentAsync(ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return ConversationMutationResult.ConversationNotFound;
            }

            var conversation = download.Value.Content.ToObjectFromJson<ConversationDto>(JsonOptions);
            if (conversation is null)
                throw new InvalidDataException($"Conversation '{conversationId}' could not be deserialized.");

            var result = mutate(conversation);
            if (result != ConversationMutationResult.Success) return result;

            var metadata = new Dictionary<string, string>(
                download.Value.Details.Metadata,
                StringComparer.OrdinalIgnoreCase);
            var activityAt = GetLatestActivity(conversation)
                ?? download.Value.Details.LastModified;
            WriteSummaryMetadata(metadata, GetName(conversation), conversation.Messages.Count, activityAt);

            var options = new BlobUploadOptions
            {
                Metadata = metadata,
                Conditions = new BlobRequestConditions { IfMatch = download.Value.Details.ETag }
            };

            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(conversation, JsonOptions), options, ct);
                return ConversationMutationResult.Success;
            }
            catch (RequestFailedException ex) when ((ex.Status is 409 or 412) && attempt + 1 < MutationAttempts)
            {
                // Re-read and re-apply the mutation rather than overwriting a
                // concurrent writer with the stale conversation body.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
            }
        }

        throw new InvalidOperationException(
            $"Conversation '{conversationId}' changed too frequently to apply the message mutation safely.");
    }

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

    public async Task<IReadOnlyList<ConversationSummaryDto>> GetSummariesAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var prefix = BuildPrefix(tenantId);
        var results = new List<ConversationSummaryDto>();

        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.Metadata,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: ct))
        {
            if (!TryGetId(item.Name, prefix, out var id)) continue;

            var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase);
            var updatedAt = item.Properties.LastModified ?? DateTimeOffset.MinValue;

            if (TryCreateSummary(id, metadata, updatedAt, out var summary))
            {
                results.Add(summary);
                continue;
            }

            var backfilled = await BackfillSummaryAsync(id, item, metadata, updatedAt, tenantId, ct);
            if (backfilled is not null) results.Add(backfilled);
        }

        return results
            .OrderByDescending(summary => summary.ActivityAt)
            .ThenByDescending(summary => summary.UpdatedAt)
            .ToArray();
    }

    public async Task<ConversationSearchResultDto> SearchAsync(
        string query,
        int limit = 20,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0) throw new ArgumentException("A non-empty query is required.", nameof(query));

        var cappedLimit = Math.Clamp(limit, 1, 50);
        var terms = normalizedQuery
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var results = new List<ConversationSearchHitDto>(cappedLimit);
        var prefix = BuildPrefix(tenantId);

        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: ct))
        {
            if (results.Count >= cappedLimit) break;
            if (!TryGetId(item.Name, prefix, out var id)) continue;

            var conversation = await GetAsync(id, tenantId, ct);
            if (conversation is null) continue;

            for (var messageIndex = 0; messageIndex < conversation.Messages.Count && results.Count < cappedLimit; messageIndex++)
            {
                var message = conversation.Messages[messageIndex];
                for (var partIndex = 0; partIndex < message.Parts.Count && results.Count < cappedLimit; partIndex++)
                {
                    if (!TryGetText(message.Parts[partIndex], out var text)) continue;
                    if (!TryMatch(text, terms, out var matchIndex)) continue;

                    results.Add(new ConversationSearchHitDto
                    {
                        ConversationId = conversation.Id,
                        MessageId = message.Id,
                        MessageIndex = messageIndex,
                        Role = message.Role.ToString(),
                        PartIndex = partIndex,
                        MatchIndex = matchIndex,
                        Snippet = CreateSnippet(text, matchIndex)
                    });
                }
            }
        }

        return new ConversationSearchResultDto
        {
            Query = normalizedQuery,
            Total = results.Count,
            Limit = cappedLimit,
            Results = results
        };
    }

    async Task<ConversationSummaryDto?> BackfillSummaryAsync(
        string id,
        BlobItem item,
        Dictionary<string, string> metadata,
        DateTimeOffset listedUpdatedAt,
        string? tenantId,
        CancellationToken ct)
    {
        var blob = container.GetBlobClient(item.Name);
        var download = await blob.DownloadContentAsync(ct);
        var conversation = download.Value.Content.ToObjectFromJson<ConversationDto>(JsonOptions);
        if (conversation is null) return null;

        var activityAt = GetLatestActivity(conversation) ?? listedUpdatedAt;
        WriteSummaryMetadata(metadata, GetName(conversation), conversation.Messages.Count, activityAt);

        try
        {
            var response = await blob.SetMetadataAsync(
                metadata,
                new BlobRequestConditions { IfMatch = download.Value.Details.ETag },
                ct);
            return CreateSummary(id, metadata, response.Value.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // A concurrent writer won. Never overwrite it with metadata derived
            // from the stale body; use its metadata if it is already complete.
            var current = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            var currentMetadata = new Dictionary<string, string>(current.Metadata, StringComparer.OrdinalIgnoreCase);
            return TryCreateSummary(id, currentMetadata, current.LastModified, out var summary)
                ? summary
                : null;
        }
    }

    static bool TryCreateSummary(
        string id,
        IReadOnlyDictionary<string, string> metadata,
        DateTimeOffset updatedAt,
        out ConversationSummaryDto summary)
    {
        if (metadata.TryGetValue(NameMetadataKey, out var encodedName)
            && TryDecodeName(encodedName, out var name)
            && TryReadMessageCount(metadata, out var messageCount)
            && TryReadTimestamp(metadata, ActivityAtMetadataKey, out var activityAt))
        {
            summary = new ConversationSummaryDto
            {
                Id = id,
                Name = name,
                MessageCount = messageCount,
                ActivityAt = activityAt,
                UpdatedAt = updatedAt
            };
            return true;
        }

        summary = default!;
        return false;
    }

    static ConversationSummaryDto CreateSummary(
        string id,
        IReadOnlyDictionary<string, string> metadata,
        DateTimeOffset updatedAt)
    {
        if (!TryCreateSummary(id, metadata, updatedAt, out var summary))
            throw new InvalidOperationException("Conversation summary metadata is incomplete.");
        return summary;
    }

    static void WriteSummaryMetadata(
        IDictionary<string, string> metadata,
        string name,
        int messageCount,
        DateTimeOffset activityAt)
    {
        metadata[NameMetadataKey] = EncodeName(name);
        metadata[MessageCountMetadataKey] = messageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata[ActivityAtMetadataKey] = activityAt.ToUniversalTime().ToString("O");
    }

    static string GetName(ConversationDto conversation)
    {
        if (conversation.Metadata?.TryGetValue("name", out var value) == true)
        {
            var name = value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                _ => value?.ToString()
            };
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return DefaultName;
    }

    static DateTimeOffset? GetLatestActivity(ConversationDto conversation)
    {
        DateTimeOffset? latest = null;
        foreach (var message in conversation.Messages)
        {
            if (message.Metadata?.TryGetValue("timestamp", out var value) != true) continue;
            if (!TryParseTimestamp(value, out var timestamp)) continue;
            if (latest is null || timestamp > latest) latest = timestamp;
        }
        return latest;
    }

    static bool TryParseTimestamp(object? value, out DateTimeOffset timestamp)
    {
        var text = value switch
        {
            string source => source,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            DateTimeOffset dto => dto.ToString("O"),
            DateTime dateTime => dateTime.ToString("O"),
            _ => null
        };
        return DateTimeOffset.TryParse(text, out timestamp);
    }

    static bool TryReadTimestamp(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return metadata.TryGetValue(key, out var value) && DateTimeOffset.TryParse(value, out timestamp);
    }

    static bool TryReadMessageCount(IReadOnlyDictionary<string, string> metadata, out int count)
    {
        count = default;
        return metadata.TryGetValue(MessageCountMetadataKey, out var value)
            && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)
            && count >= 0;
    }

    static string EncodeName(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    static bool TryDecodeName(string value, out string name)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            name = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            name = string.Empty;
            return false;
        }
    }

    static bool TryGetText(AIHappey.Vercel.Models.UIMessagePart part, out string text)
    {
        if (part is AIHappey.Vercel.Models.TextUIPart textPart && !string.IsNullOrWhiteSpace(textPart.Text))
        {
            text = textPart.Text;
            return true;
        }
        text = string.Empty;
        return false;
    }

    static bool TryMatch(string text, IReadOnlyList<string> terms, out int firstMatch)
    {
        firstMatch = int.MaxValue;
        foreach (var term in terms)
        {
            var index = text.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) return false;
            firstMatch = Math.Min(firstMatch, index);
        }
        if (firstMatch == int.MaxValue) firstMatch = 0;
        return true;
    }

    static string CreateSnippet(string text, int matchIndex)
    {
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 320) return compact;
        var start = Math.Max(0, Math.Min(matchIndex, compact.Length) - 90);
        var length = Math.Min(260, compact.Length - start);
        return $"{(start > 0 ? "…" : string.Empty)}{compact.Substring(start, length)}{(start + length < compact.Length ? "…" : string.Empty)}";
    }

    static string BuildPrefix(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "default/" : $"{tenantId}/";

    static bool TryGetId(string blobName, string prefix, out string id)
    {
        if (blobName.StartsWith(prefix, StringComparison.Ordinal)
            && blobName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            id = blobName[prefix.Length..^5];
            return id.Length > 0 && !id.Contains('/');
        }
        id = string.Empty;
        return false;
    }

    static string BuildBlobName(string id, string? tenantId)
        => $"{BuildPrefix(tenantId)}{id}.json";
}
