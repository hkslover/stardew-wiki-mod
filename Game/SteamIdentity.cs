using System.Reflection;

namespace StardewWikiAgent.Game;

/// <summary>
/// Best-effort read of the local Steam user's SteamID64 by reflecting into the
/// Steamworks.NET assembly the game loads. Returns false on non-Steam builds or
/// when the Steam API is not initialized. The result is cached after the first call.
/// </summary>
internal static class SteamIdentity
{
    private static bool attempted;
    private static ulong cached;

    public static bool TryGetSteamId(out ulong steamId)
    {
        if (attempted)
        {
            steamId = cached;
            return cached != 0;
        }
        attempted = true;

        try
        {
            Type? steamUser = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Steamworks.SteamUser"))
                .FirstOrDefault(type => type is not null);
            MethodInfo? getSteamId = steamUser?.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static);
            object? cSteamId = getSteamId?.Invoke(null, null);
            if (cSteamId?.GetType().GetField("m_SteamID")?.GetValue(cSteamId) is ulong value)
            {
                cached = value;
                steamId = value;
                return value != 0;
            }
        }
        catch
        {
            // Steamworks unavailable (GOG build, Steam not running, API not initialized) — skip silently.
        }

        steamId = 0;
        return false;
    }
}
