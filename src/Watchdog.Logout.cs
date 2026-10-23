using Skua.Core.Interfaces;

namespace WatchDog;

/// <summary>
/// Telling the player's own Log Out / Change Server apart from a drop, so the plugin leaves the account alone.
/// </summary>
/// <remarks>
/// VibeSkua 2.0.7 has an <c>IsIntentionalLogout</c> flag for this, but its own watchdog clears the flag while the
/// game still looks logged in, so it is usually false again by the time the account is out; it is also set by
/// logouts that aren't the player's (a stuck "loading map" with Auto Relogin off). So the plugin remembers the
/// player's logout itself, until the account is back in game, from:
/// <list type="bullet">
/// <item>the game's Log Out / Change Server buttons (VibeSkua 2.0.7 reports clicks on them),</item>
/// <item>the game's own logout packets (eLO / eSL), where the client passes them on.</item>
/// </list>
/// A script's <c>Bot.Servers.Logout()</c> can't be told apart from the client's own logouts, so it still counts
/// as a drop when the script was running. Our own relog's logout doesn't count.
/// </remarks>
internal sealed partial class Watchdog
{
    private volatile bool _userLoggedOut;
    private int _userLoggedOutAt;
    private bool _logoutHooked;
    private FlashCallHandler? _flashHandler;

    private void MarkUserLogout(string how)
    {
        if (_ownLogout || _busy || _userLoggedOut)
            return;
        _userLoggedOut = true;
        _userLoggedOutAt = Environment.TickCount | 1;
        Write($" logged out by the player ({how}); leaving it logged out.");
    }

    /// <summary>Back in game (after having been out): forget it.</summary>
    private void ClearUserLogout(bool cameBackIn, int now)
    {
        if (!_userLoggedOut)
            return;
        // Still in game a while after the click: the logout didn't happen.
        if (cameBackIn || now - _userLoggedOutAt > 30000)
            _userLoggedOut = false;
    }

    private void HookLogoutSignals()
    {
        if (_logoutHooked)
            return;
        _logoutHooked = true;
        try
        {
            _flashHandler = OnFlashCall;
            _bot.Flash.FlashCall += _flashHandler;
        }
        catch { }
    }

    private void UnhookLogoutSignals()
    {
        try { if (_flashHandler is not null) _bot.Flash.FlashCall -= _flashHandler; } catch { }
    }

    private void OnFlashCall(string function, params object[] args)
    {
        try
        {
            if (function == "userIntentionalAction")
            {
                if (args is { Length: > 0 } && args[0] is string action && action is "logout" or "changeServer")
                    MarkUserLogout(action == "logout" ? "Log Out button" : "Change Server button");
            }
            else if (function == "packet" && args is { Length: > 0 } && args[0] is string packet && packet.Contains("%e"))
            {
                string[] parts = packet.Split('%', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 2 && parts[2] is "eLO" or "eLO2" or "eSL" or "eSL2")
                    MarkUserLogout(parts[2].StartsWith("eLO") ? "Log Out" : "Change Server");
            }
        }
        catch { }
    }
}
