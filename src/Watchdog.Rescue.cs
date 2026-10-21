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

    private sealed record RescueCall(string User, string Server, string Room, string Cell, string Pad, Dictionary<string, string> Quests);

    // ---- quest progress --------------------------------------------------------------------------------
    // A boss (or a whole map, cell or monster) can be hidden until a questline is done. The account asking for
    // help sends its quest progress (the strQuests strings) with the call; a helper copies in every quest value
    // that is higher (client-side only, the same way ButlerV5's quest clone works) so it can see the same boss.
    // Its own values are saved first and put back exactly when the rescue ends, so its own script doesn't see
    // quests as done that aren't. (After a relog the server sends the real values anyway.)

    private static readonly string[] QuestFields =
        { "strQuests", "strQuests2", "strQuests3", "strQuests4", "strQuests5", "strQuests6", "strQuests7", "strQuests8", "strQuests9" };

    private Dictionary<string, string>? ReadQuestStrings()
    {
        try
        {
            string? json = _bot.Flash.GetGameObject("world.myAvatar.objData");
            if (string.IsNullOrEmpty(json))
                return null;
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            Dictionary<string, string> result = new();
            foreach (string field in QuestFields)
                if (doc.RootElement.TryGetProperty(field, out System.Text.Json.JsonElement el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                    result[field] = el.GetString() ?? "";
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private void SetQuestStrings(Dictionary<string, string> strings)
    {
        foreach ((string field, string value) in strings)
            try { _bot.Flash.SetGameObject($"world.myAvatar.objData.{field}", value); } catch { }
    }

    private static int QuestChar(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        _ => 0,
    };

    /// <summary>Per slot (one character each) the higher of the two; length kept so slot positions don't move.</summary>
    private static string MergeQuestString(string mine, string theirs)
    {
        System.Text.StringBuilder sb = new(Math.Max(mine.Length, theirs.Length));
        for (int i = 0; i < Math.Max(mine.Length, theirs.Length); i++)
        {
            char a = i < mine.Length ? mine[i] : '0';
            char b = i < theirs.Length ? theirs[i] : '0';
            sb.Append(QuestChar(a) >= QuestChar(b) ? a : b);
        }
        return sb.ToString();
    }

    /// <summary>Copies in the stuck account's higher quest values; returns how many strings changed.</summary>
    private int BorrowQuests(Dictionary<string, string> own, Dictionary<string, string> theirs)
    {
        Dictionary<string, string> merged = new();
        foreach ((string field, string value) in theirs)
        {
            string mine = own.TryGetValue(field, out string? m) ? m : "";
            string result = MergeQuestString(mine, value);
            if (result != mine)
                merged[field] = result;
        }
        SetQuestStrings(merged);
        return merged.Count;
    }

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
            File.WriteAllLines(file + ".tmp", RescueLines(new[] { user, server, _bot.Map.FullName, _bot.Player.Cell, _bot.Player.Pad, DateTime.UtcNow.Ticks.ToString() }));
            File.Move(file + ".tmp", file, true);
        }
        catch { }
    }

    /// <summary>The call's six fixed lines, then this account's quest progress (field=value lines).</summary>
    private IEnumerable<string> RescueLines(string[] fixedLines)
    {
        foreach (string l in fixedLines)
            yield return l;
        if (ReadQuestStrings() is { } quests)
            foreach ((string field, string value) in quests)
                yield return $"{field}={value}";
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
            Dictionary<string, string> quests = new();
            for (int i = 6; i < l.Length; i++)
            {
                int eq = l[i].IndexOf('=');
                if (eq > 0 && l[i].StartsWith("strQuests", StringComparison.Ordinal))
                    quests[l[i][..eq]] = l[i][(eq + 1)..];
            }
            return new RescueCall(l[0], l[1], l[2], l[3], l[4], quests);
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
        Dictionary<string, string>? ownQuests = null;
        bool knockedOut = false;
        try
        {
            myRoom = _bot.Map.FullName;
            myCell = _bot.Player.Cell;
            myPad = _bot.Player.Pad;
            Write($" {call.User} keeps dying in {call.Room} ({call.Cell}); stopping the script to go help (Rescue Allies).");
            await _manager.StopScript();
            await Task.Delay(1500);

            // See the same boss as the stuck account: borrow its quest progress (put back afterwards).
            if (call.Quests.Count > 0 && (ownQuests = ReadQuestStrings()) is not null)
            {
                int changed = BorrowQuests(ownQuests, call.Quests);
                if (changed > 0)
                    Write($" borrowing {call.User}'s quest progress while helping (client-side; this account's own is put back after).");
            }

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
                    knockedOut = true; // logging back in brings this account's real quest progress back
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
                    if (ownQuests is not null && now.Quests.Count > 0)
                        BorrowQuests(ReadQuestStrings() ?? ownQuests, now.Quests);
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
            // Put this account's own quest progress back exactly as it was, before it goes back and its script
            // starts again (its script would otherwise see quests as done that aren't).
            if (ownQuests is not null && !knockedOut && InGame)
            {
                SetQuestStrings(ownQuests);
                Write(" put this account's own quest progress back.");
            }
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
