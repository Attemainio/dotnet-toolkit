using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Correlates Codex's documented subagent lifecycle payloads with tool hooks, whose payloads do not
/// repeat the active agent identity.
/// </summary>
internal static class SubagentTracker
{
    private sealed record ActiveAgent(string? AgentId, string? AgentType);

    /// <summary>Records the active agent for a session turn. Failures deliberately allow the hook.</summary>
    public static HookOutcome Start(HookPayload payload)
    {
        if (PathFor(payload) is not { } path || payload.AgentType is null)
        {
            return HookOutcome.Allow;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ActiveAgent(payload.AgentId, payload.AgentType)));
        }
        catch (Exception)
        {
            // Attribution is observational and must never interrupt an agent run.
        }

        return HookOutcome.Allow;
    }

    /// <summary>Removes a completed agent correlation. Failures deliberately allow the hook.</summary>
    public static HookOutcome Stop(HookPayload payload)
    {
        try
        {
            if (PathFor(payload) is { } path)
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Attribution is observational and must never interrupt an agent run.
        }

        // Codex requires valid JSON on stdout for every successful SubagentStop hook.
        return HookOutcome.AllowWith("{\"continue\":true}");
    }

    /// <summary>Uses direct agent fields when present, otherwise resolves the active Codex agent.</summary>
    public static (string? AgentId, string? AgentType) Resolve(HookPayload payload)
    {
        if (payload.AgentId is not null || payload.AgentType is not null || PathFor(payload) is not { } path)
        {
            return (payload.AgentId, payload.AgentType);
        }

        try
        {
            var agent = JsonSerializer.Deserialize<ActiveAgent>(File.ReadAllText(path));
            return (agent?.AgentId, agent?.AgentType);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? PathFor(HookPayload payload)
    {
        if (string.IsNullOrEmpty(payload.SessionId) || string.IsNullOrEmpty(payload.TurnId))
        {
            return null;
        }

        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(payload.SessionId + "\n" + payload.TurnId)));
        return Path.Combine(Path.GetTempPath(), "dotnet-toolkit-hooks", key + ".agent.json");
    }
}
