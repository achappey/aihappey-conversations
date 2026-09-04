using System.Buffers;
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
    const int DecoderCharacterBufferSize = 32 * 1024;
    const int HashBufferSize = 64 * 1024;
    const int UploadTransferSize = 4 * 1024 * 1024;

    // Strings are immutable, so sharing attachment data-URI strings is safe. Only
    // containers that ExternalizeAsync mutates need to be copied. The previous
    // JSON round trip briefly created another complete UTF-8/UTF-16 representation
    // of every attachment and was especially expensive on the large object heap.
    public static ConversationDto CloneConversation(ConversationDto conversation) => new()
    {
        Id = conversation.Id,
        Messages = conversation.Messages.Select(CloneMessage).ToList(),
        Metadata = conversation.Metadata
    };

    public static UIMessage CloneMessage(UIMessage message) => new()
    {
        Id = message.Id,
        Role = message.Role,
        Parts = [.. message.Parts],
        Metadata = message.Metadata
    };

    public static List<UIMessagePart> CloneParts(IReadOnlyList<UIMessagePart> parts) =>
        [.. parts];

    public static void ValidateIncomingParts(IEnumerable<UIMessagePart> parts)
    {
        foreach (var file in parts.OfType<FileUIPart>())
        {
            var url = file.Url ?? string.Empty;
            if (url.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidConversationAttachmentException("Internal attachment references are not accepted from clients.");

            if (IsDataUri(url)
                && (!TryGetBase64PayloadOffset(url, out var payloadOffset)
                    || !IsValidBase64(url.AsSpan(payloadOffset))))
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

                if (!TryGetBase64PayloadOffset(url, out var payloadOffset))
                {
                    if (rejectMalformedDataUris)
                        throw new InvalidConversationAttachmentException("A file part contains an invalid or non-base64 data URI.");
                    continue;
                }

                string hash;
                try
                {
                    hash = await ComputeHashAsync(url, payloadOffset, ct);
                }
                catch (FormatException)
                {
                    if (rejectMalformedDataUris)
                        throw new InvalidConversationAttachmentException("A file part contains an invalid or non-base64 data URI.");
                    continue;
                }

                await UploadIfMissingAsync(attachmentPrefix, hash, url, payloadOffset, file.MediaType, ct);
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

        // Hydrate one attachment at a time. The public contract requires the
        // resulting base64 strings to remain resident in the returned DTO, but
        // there is no reason to retain byte arrays for every attachment too.
        foreach (var hash in hashes)
        {
            var blob = container.GetBlobClient($"{attachmentPrefix}{hash}");
            var response = await blob.DownloadContentAsync(ct);
            var bytes = response.Value.Content.ToArray();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
                throw new InvalidDataException($"Attachment '{hash}' failed its integrity check.");

            string? dataUri = null;
            foreach (var message in conversation.Messages)
            {
                for (var index = 0; index < message.Parts.Count; index++)
                {
                    if (message.Parts[index] is not FileUIPart file
                        || !TryGetHash(file.Url, out var partHash)
                        || !string.Equals(partHash, hash, StringComparison.Ordinal)) continue;

                    dataUri ??= $"data:{SafeMediaType(file.MediaType)};base64,{Convert.ToBase64String(bytes)}";
                    message.Parts[index] = CopyWithUrl(file, dataUri);
                }
            }
        }
    }

    async Task UploadIfMissingAsync(
        string attachmentPrefix,
        string hash,
        string dataUri,
        int payloadOffset,
        string? mediaType,
        CancellationToken ct)
    {
        var blob = container.GetBlobClient($"{attachmentPrefix}{hash}");
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = SafeMediaType(mediaType) },
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                InitialTransferSize = UploadTransferSize,
                MaximumTransferSize = UploadTransferSize,
                MaximumConcurrency = 1
            }
        };

        try
        {
            await using var decoded = new Base64DecodingStream(dataUri, payloadOffset);
            await blob.UploadAsync(decoded, options, ct);
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

    static bool TryGetBase64PayloadOffset(string value, out int payloadOffset)
    {
        payloadOffset = 0;
        var comma = value.IndexOf(',');
        if (comma <= 5) return false;

        var header = value[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;

        payloadOffset = comma + 1;
        return true;
    }

    static bool IsValidBase64(ReadOnlySpan<char> value)
    {
        var symbolCount = 0;
        var paddingCount = 0;
        var sawPadding = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character)) continue;
            symbolCount++;

            if (character == '=')
            {
                sawPadding = true;
                if (++paddingCount > 2) return false;
                continue;
            }

            if (sawPadding || !IsBase64Character(character)) return false;
        }

        return symbolCount % 4 == 0;
    }

    static bool IsBase64Character(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '+' or '/';

    static async Task<string> ComputeHashAsync(string dataUri, int payloadOffset, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var decoded = new Base64DecodingStream(dataUri, payloadOffset);
        var buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            int read;
            while ((read = await decoded.ReadAsync(buffer.AsMemory(0, HashBufferSize), ct)) != 0)
                hash.AppendData(buffer, 0, read);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Exposes a base64 payload already held in a JSON string as decoded bytes
    /// without allocating a byte array proportional to the attachment size.
    /// </summary>
    sealed class Base64DecodingStream : Stream
    {
        readonly string source;
        readonly char[] encodedBuffer;
        readonly byte[] decodedBuffer;
        int sourceOffset;
        int decodedOffset;
        int decodedCount;
        bool finished;
        bool disposed;

        public Base64DecodingStream(string source, int payloadOffset)
        {
            this.source = source;
            sourceOffset = payloadOffset;
            encodedBuffer = ArrayPool<char>.Shared.Rent(DecoderCharacterBufferSize);
            decodedBuffer = ArrayPool<byte>.Shared.Rent(DecoderCharacterBufferSize / 4 * 3);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (destination.Length == 0) return 0;

            var written = 0;
            while (written < destination.Length)
            {
                if (decodedOffset == decodedCount && !FillDecodedBuffer()) break;

                var count = Math.Min(destination.Length - written, decodedCount - decodedOffset);
                decodedBuffer.AsSpan(decodedOffset, count).CopyTo(destination[written..]);
                decodedOffset += count;
                written += count;
            }

            return written;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        bool FillDecodedBuffer()
        {
            if (finished) return false;

            var encodedCount = 0;
            var sawPadding = false;
            while (sourceOffset < source.Length && encodedCount < DecoderCharacterBufferSize)
            {
                var character = source[sourceOffset++];
                if (char.IsWhiteSpace(character)) continue;
                encodedBuffer[encodedCount++] = character;
                sawPadding |= character == '=';
            }

            if (encodedCount == 0)
            {
                finished = true;
                return false;
            }

            if (encodedCount % 4 != 0
                || !Convert.TryFromBase64Chars(
                    encodedBuffer.AsSpan(0, encodedCount),
                    decodedBuffer,
                    out decodedCount))
                throw new FormatException("The attachment contains invalid base64 data.");

            if (sawPadding)
            {
                while (sourceOffset < source.Length)
                    if (!char.IsWhiteSpace(source[sourceOffset++]))
                        throw new FormatException("The attachment contains data after base64 padding.");
                finished = true;
            }
            else if (sourceOffset == source.Length)
            {
                finished = true;
            }

            decodedOffset = 0;
            return decodedCount != 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                ArrayPool<char>.Shared.Return(encodedBuffer);
                ArrayPool<byte>.Shared.Return(decodedBuffer);
            }
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
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
