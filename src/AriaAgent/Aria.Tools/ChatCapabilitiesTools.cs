using Microsoft.Extensions.AI;

namespace Aria.Tools;

/// <summary>
/// The chat-capabilities index tool. Always-on when the host supplies the text (Web only —
/// Console has no "/" palette or "#" reference system). Lets the agent answer "how do I do X"
/// / "what can this interface do" questions from the same catalog that drives the chat UI's
/// command palette and reference picker, instead of guessing or staying silent.
/// </summary>
public static class ChatCapabilitiesTools
{
    public static AITool Create(string capabilitiesText) =>
        AIFunctionFactory.Create(
            () => capabilitiesText,
            name: "list_chat_capabilities",
            description:
                "Lists the chat UI's \"/\" commands and \"#\" context references available to the " +
                "user right now. Call this when the user asks how to do something the interface " +
                "supports (attach/reference a file, run a command, inject git state, etc.) or asks " +
                "what you or the interface can do.");
}
