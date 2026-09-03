using AIHappey.Common.MCP;

namespace AIhappey.Core.Conversations.MCP;

public static class ConversationMcpDefinitions
{
    public static IEnumerable<McpServerDefinition> GetDefinitions()
    {
        yield return new McpServerDefinition(
            Name: "AI-Conversations",
            Title: "AI Conversations",
            Description: "List, read, and search the authenticated user's conversations.",
            ToolTypes: [typeof(ConversationTools)]);
    }
}
