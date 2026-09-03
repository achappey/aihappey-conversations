using System.Security.Cryptography;
using System.Text.Json;
using AIhappey.Core.Conversations.Models;
using AIHappey.Vercel.Models;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AIhappey.Core.Conversations.Services;

public sealed class InvalidConversationAttachmentException(string message) : Exception(message);

/// <summary>
/// Projects inline file data to private blobs without changing the public
/// conversation contract. References produced here are storage-only and must
/// never be returned by the REST API.
/// </summary>
internal sealed class ConversationAttachmentStorage(BlobContainerClient container)
{
    const string ReferencePrefix = "aihappey-attachment:sha256:";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ConversationDto CloneConversation(ConversationDto conversation) =>
        JsonSerializer.Deserialize<ConversationDto>(
            JsonSerializer.Serialize(conversation, JsonOptions),
            JsonOptions)
        ?? throw new InvalidDataException("The conversation could not be cloned.");

    public static UIMessage CloneMessage(UIMessage message) =>
        JsonSerializer.Deserialize<UIMessage>(
            JsonSerializer.Serialize(message, JsonOptions),
            JsonOptions)
        ?? throw new InvalidDataException("The message could not be cloned.");

    public static List<UIMessagePart> CloneParts(IReadOnlyList<UIMessagePart> parts) =>
        parts.Select(part =>
                JsonSerializer.Deserialize<UIMessagePart>(
                    JsonSerializer.Serialize(part, JsonOptions),
                    JsonOptions)
                ?? throw new InvalidDataException("A message part could not be cloned."))
            .ToList();

    public static void ValidateIncomingParts(IEnumerable<UIMessagePart> parts)
    {
        foreach (var file in parts.OfType<FileUIPart>())
        {
            var url = file.Url ?? string.Empty;
            if (url.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidConversationAttachmentException("Internal attachment references are not accepted from clients.");

            if (IsDataUri(url) && !TryDecodeDataUri(url, out _))
                throw new InvalidConversationAttachmentException("A file part contains an invalid or non-base64 data URI.");
        }
    }

    public async Task<bool> ExternalizeAsync(
        ConversationDto conversation,
        string attachmentPrefix,
        bool rejectMalformedDataUris,
        CancellationToken ct)
    {
        var changed = false;

        foreach (var message in conversation.Messages)
        {
            for (var index = 0; index < message.Parts.Count; index++)
            {
                if (message.Parts[index] is not FileUIPart file) continue;

                var url = file.Url ?? string.Empty;
                if (TryGetHash(url, out _)) continue;
                if (!IsDataUri(url)) continue;

                if (!TryDecodeDataUri(url, out var bytes))
                {
                    if (rejectMalformedDataUris)
                        throw new InvalidConversationAttachmentException("A file part contains an invalid or non-base64 data URI.");
                    continue;
                }

                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                await UploadIfMissingAsync(attachmentPrefix, hash, bytes, file.MediaType, ct);
                message.Parts[index] = CopyWithUrl(file, $"{ReferencePrefix}{hash}");
                changed = true;
            }
        }

        return changed;
    }

    public async Task HydrateAsync(
        ConversationDto conversation,
        string attachmentPrefix,
        CancellationToken ct)
    {
        var hashes = conversation.Messages
            .SelectMany(message => message.Parts)
            .OfType<FileUIPart>()
            .Select(file => TryGetHash(file.Url, out var hash) ? hash : null)
            .Where(hash => hash is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        if (hashes.Length == 0) return;

        var downloads = await Task.WhenAll(hashes.Select(async hash =>
        {
            var blob = container.GetBlobClient($"{attachmentPrefix}{hash}");
            var response = await blob.DownloadContentAsync(ct);
            var bytes = response.Value.Content.ToArray();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
                throw new InvalidDataException($"Attachment '{hash}' failed its integrity check.");
            return (Hash: hash, Bytes: bytes);
        }));

        var content = downloads.ToDictionary(item => item.Hash, item => item.Bytes, StringComparer.Ordinal);
        foreach (var message in conversation.Messages)
        {
            for (var index = 0; index < message.Parts.Count; index++)
            {
                if (message.Parts[index] is not FileUIPart file
                    || !TryGetHash(file.Url, out var hash)) continue;

                var mediaType = SafeMediaType(file.MediaType);
                var dataUri = $"data:{mediaType};base64,{Convert.ToBase64String(content[hash])}";
                message.Parts[index] = CopyWithUrl(file, dataUri);
            }
        }
    }

    async Task UploadIfMissingAsync(
        string attachmentPrefix,
        string hash,
        byte[] bytes,
        string? mediaType,
        CancellationToken ct)
    {
        var blob = container.GetBlobClient($"{attachmentPrefix}{hash}");
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = SafeMediaType(mediaType) }
        };

        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(bytes), options, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // The content-derived name makes a concurrent existing upload the
            // desired result. Hydration verifies the bytes against this hash.
        }
    }

    static FileUIPart CopyWithUrl(FileUIPart source, string url) => new()
    {
        MediaType = source.MediaType,
        Url = url,
        Filename = source.Filename,
        ProviderMetadata = source.ProviderMetadata
    };

    static bool IsDataUri(string value) =>
        value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    static bool TryDecodeDataUri(string value, out byte[] bytes)
    {
        bytes = [];
        var comma = value.IndexOf(',');
        if (comma <= 5) return false;

        var header = value[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            bytes = Convert.FromBase64String(value[(comma + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static bool TryGetHash(string? value, out string hash)
    {
        hash = string.Empty;
        if (value is null || !value.StartsWith(ReferencePrefix, StringComparison.Ordinal)) return false;

        var candidate = value[ReferencePrefix.Length..];
        if (candidate.Length != 64 || candidate.Any(character => !Uri.IsHexDigit(character))) return false;

        hash = candidate.ToLowerInvariant();
        return true;
    }

    static string SafeMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Contains('\r')
            || value.Contains('\n'))
            return "application/octet-stream";
        return value;
    }
}
