using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PocketCake.Services.Agent;

/// <summary>MCP prompts: the agent guide, plus task starters built on it.</summary>
public sealed class AgentPrompts
{
    [McpServerPrompt(Name = "pocketcake_guide")]
    [Description("How to drive PocketCake: the device model, the workflow and the safety rules.")]
    public static string Guide() => AgentServer.Guide;

    [McpServerPrompt(Name = "design_tone")]
    [Description("Build a tone on the pedal from a description (a song, an artist, or words like 'warm edge-of-breakup blues').")]
    public static string DesignTone([Description("The sound to build")] string description) => $"""
        {AgentServer.Guide}

        ---

        Build this tone on the pedal with the PocketCake tools: {description}

        Start from the closest factory or user preset if one fits, otherwise from the current preset. Pick
        effects and set parameters module by module, then summarize the chain and the key settings. Leave
        it as live edits and ask me before calling save_preset.
        """;
}
