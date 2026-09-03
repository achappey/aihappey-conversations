using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIhappey.Core.Conversations.Models;

/// <summary>
/// A shallow update for one stored UI message. The message id is intentionally
/// supplied by the route and cannot be changed by a patch.
/// </summary>
public sealed class ConversationMessagePatchDto
{
    private Dictionary<string, object>? metadata;

    [JsonPropertyName("role")]
    public Role? Role { get; init; }

    [JsonPropertyName("parts")]
    public List<UIMessagePart>? Parts { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata
    {
        get => metadata;
        init
        {
            metadata = value;
            MetadataSpecified = true;
        }
    }

    [JsonIgnore]
    public bool MetadataSpecified { get; private init; }
}
