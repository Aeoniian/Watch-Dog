using Skua.Core.Interfaces;

namespace WatchDog;

/// <summary>
/// Rescue: when an account running a script keeps dying without killing anything (10 deaths in a row, or 15
/// minutes of only deaths), it asks for help. Every other account on the same server that is running a script
/// and has "Rescue Allies" turned on stops its script, goes to that room and cell, fights there, and once the
/// stuck account gets a kill (it got past) goes back to where it was and starts its own script again.
/// </summary>
/// <remarks>
/// Same protocol as VibeSkuffle's built-in Rescue Allies, so Skua, VibeSkua and VibeSkuffle accounts on one PC
/// can help each other. The accounts talk through small files in %TEMP%\VibeSkuaTabs\rescue (one per account asking for help,
/// refreshed every 15 seconds; a file older than a minute counts as finished). Helpers give up after 20
/// minutes, and help the same account again at the earliest 10 minutes after the last rescue ended.
/// </remarks>
internal sealed partial class Watchdog
{
    private const int RescueDeaths = 10;
    private const int RescueOnlyDeathsMs = 15 * 60000;
    private const int RescueStaleMs = 60000;
    private const int RescueGiveUpMs = 20 * 60000;
    private const int RescueCooldownMs = 10 * 60000;

    private static string RescueDir => Path.Combine(Path.GetTempPath(), "VibeSkuaTabs", "rescue");

    // The account that needs help.
    private int _rsLastDeaths = -1, _rsLastKills = -1, _rsDeathStreak, _rsStreakSince;
    private bool _rsCalling;
    private int _rsCallWritten;

    // An account helping.
    private volatile string? _rsHelping;
    private int _rsScanAt;
    private readonly Dictionary<string, int> _rsLastHelped = new(StringComparer.OrdinalIgnoreCase);

    private bool RescueActive => _rsHelping is not null;

    private sealed record RescueCall(string User, string Server, string Room, string Cell, string Pad);

    /// <summary>Called from the watchdog loop while in game.</summary>
    private void RescueTick(int now)
    {
        try
        {
            int deaths = _bot.Stats.Deaths, kills = _bot.Stats.Kills;
            if (_rsLastDeaths < 0)
            {
                _rsLastDeaths = deaths;
                _rsLastKills = kills;
            }
            if (kills != _rsLastKills)
            {
                _rsDeathStreak = 0;
                _rsStreakSince = 0;
                if (_rsCalling)
                {
                    _rsCalling = false;
                    ClearRescueCall();
                    Write(" got a kill; the accounts helping can go back to what they were doing.");
                }
            }
            else if (deaths > _rsLastDeaths)
            {
                _rsDeathStreak += deaths - _rsLastDeaths;
                if (_rsStreakSince == 0)
                    _rsStreakSince = now | 1;
            }
            _rsLastDeaths = deaths;
            _rsLastKills = kills;

            if (!_manager.ScriptRunning || RescueActive)
            {
                if (_rsCalling)
                {
                    _rsCalling = false;
                    ClearRescueCall();
                }
                _rsDeathStreak = 0;
                _rsStreakSince = 0;
            }
            else
            {
                bool stuck = _rsDeathStreak >= RescueDeaths
                    || (_rsDeathStreak >= 2 && _rsStreakSince != 0 && now - _rsStreakSince >= RescueOnlyDeathsMs);
                if (stuck && !_rsCalling)
                {
                    _rsCalling = true;
                    _rsCallWritten = 0;
                    Write($" died {_rsDeathStreak} times without a kill in {Math.Max(1, (now - _rsStreakSince) / 60000)} minute(s) in {_bot.Map.FullName} ({_bot.Player.Cell}); asking the other accounts for help (Rescue Allies).");
                }
                if (_rsCalling && now - _rsCallWritten > 15000)
                {
                    _rsCallWritten = now;
                    WriteRescueCall();
                }
            }

            if (now - _rsScanAt > 5000)
            {
                _rsScanAt = now;
                ScanForRescueCalls();
            }
        }
        catch { }
    }

    private string OwnName => _user ?? "";

    private static string RescueFile(string user) =>
        Path.Combine(RescueDir, new string(user.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).ToLowerInvariant() + ".txt");

    private void WriteRescueCall()
    {
        try
        {
            string user = OwnName;
            string server = _server ?? "";
            if (user.Length == 0 || server.Length == 0)
                return;
            Directory.CreateDirectory(RescueDir);
            string file = RescueFile(user);
            File.WriteAllLines(file + ".tmp", new[] { user, server, _bot.Map.FullName, _bot.Player.Cell, _bot.Player.Pad, DateTime.UtcNow.Ticks.ToString() });
            File.Move(file + ".tmp", file, true);
        }
        catch { }
    }

    private void ClearRescueCall()
    {
        try { File.Delete(RescueFile(OwnName)); } catch { }
    }

    private static RescueCall? ReadRescueCall(string file)
    {
        try
        {
            string[] l = File.ReadAllLines(file);
            if (l.Length < 6 || !long.TryParse(l[5], out long ticks))
                return null;
            if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromMilliseconds(RescueStaleMs))
                return null;
            return new RescueCall(l[0], l[1], l[2], l[3], l[4]);
        }
        catch
        {
            return null;
        }
    }

    private void ScanForRescueCalls()
    {
        if (!Opt("rescueAllies", false) || RescueActive || _rsCalling || !_manager.ScriptRunning || _busy || _restarting != 0)
            return;
        if (!Directory.Exists(RescueDir))
            return;
        string me = OwnName;
        string server = _server ?? "";
        int now = Environment.TickCount;
        foreach (string file in Directory.GetFiles(RescueDir, "*.txt").OrderBy(File.GetCreationTimeUtc))
        {
            RescueCall? call = ReadRescueCall(file);
            if (call is null)
            {
                // Tidy up calls nobody refreshes any more.
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                        File.Delete(file);
                }
                catch { }
                continue;
            }
            if (call.User.Equals(me, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!call.Server.Equals(server, StringComparison.OrdinalIgnoreCase))
                continue; // a different server: can't reach it without logging out
            lock (_rsLastHelped)
                if (_rsLastHelped.TryGetValue(call.User, out int last) && now - last < RescueCooldownMs)
                    continue;
            _rsHelping = call.User;
            _ = Task.Run(() => RescueAsync(call, file));
            return;
        }
    }

    private async Task RescueAsync(RescueCall call, string file)
    {
        string? myRoom = null, myCell = null, myPad = null;
        try
        {
            myRoom = _bot.Map.FullName;
            myCell = _bot.Player.Cell;
            myPad = _bot.Player.Pad;
            Write($" {call.User} keeps dying in {call.Room} ({call.Cell}); stopping the script to go help (Rescue Allies).");
            await _manager.StopScript();
            await Task.Delay(1500);

            if (!await JoinForRescue(call.Room, call.Cell, call.Pad))
            {
                Write($" couldn't get into {call.Room} to help {call.User}; going back.");
                return;
            }
            _bot.Auto.StartAutoAttack();
            int start = Environment.TickCount;
            while (!_stop)
            {
                await Task.Delay(2000);
                if (!InGame)
                {
                    Write(" knocked out of the game while helping; stopping the rescue.");
                    return;
                }
                RescueCall? now = ReadRescueCall(file);
                if (now is null)
                {
                    Write($" {call.User} got past it; going back.");
                    return;
                }
                if (!string.Equals(_bot.Map.FullName, now.Room, StringComparison.OrdinalIgnoreCase))
                {
                    await _bot.Auto.StopAsync();
                    if (!await JoinForRescue(now.Room, now.Cell, now.Pad))
                    {
                        Write($" couldn't follow {call.User} into {now.Room}; going back.");
                        return;
                    }
                    _bot.Auto.StartAutoAttack();
                }
                else if (!string.Equals(_bot.Player.Cell, now.Cell, StringComparison.OrdinalIgnoreCase))
                {
                    _bot.Map.Jump(now.Cell, string.IsNullOrWhiteSpace(now.Pad) ? "Left" : now.Pad);
                }
                if (Environment.TickCount - start > RescueGiveUpMs)
                {
                    Write($" helped {call.User} for {RescueGiveUpMs / 60000} minutes and it's still stuck; going back.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Write($" rescue failed: {ex.Message}");
        }
        finally
        {
            try { await _bot.Auto.StopAsync(); } catch { }
            lock (_rsLastHelped)
                _rsLastHelped[call.User] = Environment.TickCount;
            try
            {
                if (myRoom is not null && InGame
                    && !string.Equals(_bot.Map.FullName, myRoom, StringComparison.OrdinalIgnoreCase))
                {
                    _bot.Map.Join(myRoom, string.IsNullOrWhiteSpace(myCell) ? "Enter" : myCell, string.IsNullOrWhiteSpace(myPad) ? "Spawn" : myPad);
                }
            }
            catch { }
            // The watchdog loop starts this account's own script again once it has settled.
            _restartScript = true;
            _rsHelping = null;
        }
    }

    private async Task<bool> JoinForRescue(string room, string cell, string pad)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                _bot.Map.Join(room, string.IsNullOrWhiteSpace(cell) ? "Enter" : cell, string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad);
            }
            catch { }
            for (int w = 0; w < 20; w++)
            {
                await Task.Delay(500);
                if (string.Equals(_bot.Map.FullName, room, StringComparison.OrdinalIgnoreCase) && InGame)
                {
                    if (!string.IsNullOrWhiteSpace(cell) && !string.Equals(_bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
                        _bot.Map.Jump(cell, string.IsNullOrWhiteSpace(pad) ? "Left" : pad);
                    return true;
                }
            }
        }
        return false;
    }
}
