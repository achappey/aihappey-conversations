using AIHappey.Common.MCP;

namespace AIhappey.Core.Conversations.MCP;

public static class ConversationMcpDefinitions
{
    public static IEnumerable<McpServerDefinition> GetDefinitions()
    {
        yield return new McpServerDefinition(
            Name: "AI-Conversations",
            Title: "AI Conversations",
            Description: "List, read, and search the current user's conversations.",
            ToolTypes: [typeof(ConversationTools)]);

        yield return new McpServerDefinition(
            Name: "AI-UserMemories",
            Title: "AI User Memories",
            Description: "Create, list, read, update, and delete the current user's memories.",
            ToolTypes: [typeof(UserMemoryTools)]);
    }
}
