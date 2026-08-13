namespace ZomboidManager;

/// <summary>
/// Builds the RCON command used for Server Broadcaster messages.
/// Vanilla Project Zomboid (Build 42) only exposes <c>servermsg</c>, which shows as the
/// large red admin/server announcement overlay — not a normal player-style global chat line.
/// There is no documented vanilla console/RCON command for true global chat; that requires a
/// server-side mod. Swap the body of <see cref="FormatOutgoingMessage"/> when such a command exists.
/// </summary>
public static class BroadcastRconCommand
{
    public const string Mechanism = "servermsg";

    public static string FormatOutgoingMessage(string text)
    {
        string escaped = (text ?? string.Empty).Replace("\"", "\\\"");
        // Easy swap point: e.g. return $"say \"{escaped}\";" or a mod command.
        return $"{Mechanism} \"{escaped}\"";
    }
}
