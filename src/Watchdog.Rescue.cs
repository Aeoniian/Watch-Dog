using Skua.Core.Interfaces;
using WatchDog.Shared;

namespace WatchDog;

/// <summary>
/// Rescue: when an account running a script keeps dying without killing anything (10 deaths in a row, or 15
/// minutes of only deaths), it asks for help. Every eligible account on the same server (including idle accounts)
/// and has the shared Rescue Allies switch turned on pauses its script, goes to that room and cell, fights there, and once the
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
    private const int RescueStaleMs = 60000;
    private const int RescueGiveUpMs = 20 * 60000;
    private const int RescueCooldownMs = 10 * 60000;

    private static string RescueDir => Environment.GetEnvironmentVariable("WATCHDOG_RESCUE_CALL_ROOT") ?? Path.Combine(Path.GetTempPath(), "VibeSkuaTabs", "rescue");

    // The account that needs help.
    private int _rsLastDeaths = -1, _rsLastKills = -1, _rsDeathStreak, _rsStreakSince;
    private bool _rsCalling;
    private string _rsOwner = "";
    private int _rsCallWritten;

    // An account helping.
    private volatile string? _rsHelping;
    private int _rsScanAt;
    private readonly Dictionary<string, int> _rsLastHelped = new(StringComparer.OrdinalIgnoreCase);

    private bool RescueActive => _rsHelping is not null;
    private RescueSession? _rsSession;
    private bool RescueEnabled => SharedRescueSettings.Instance.Enabled;
    private bool RescueAllowed => RescueEnabled && SharedRescueSettings.Instance.IsAllowed(OwnName) && Opt("enabled", true);


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
            string owner = OwnName;
            if (!string.Equals(owner, _rsOwner, StringComparison.OrdinalIgnoreCase))
            {
                if (_rsCalling && _rsOwner.Length > 0)
                    try { File.Delete(RescueFile(_rsOwner)); } catch { }
                _rsOwner = owner; _rsCalling = false; _rsDeathStreak = 0; _rsStreakSince = 0; _rsLastDeaths = -1;
                lock (_rsLastHelped) _rsLastHelped.Clear();
            }
            if (!RescueEnabled)
            {
                if (_rsCalling) { _rsCalling = false; ClearRescueCall(); }
                _rsDeathStreak = 0; _rsStreakSince = 0; _rsLastDeaths = -1;
                return;
            }
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

            if (!_manager.ScriptRunning || RescueSession.IsPaused(_manager) || RescueActive)
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
                bool stuck = SharedRescueSettings.Instance.ShouldRequest(_rsDeathStreak,
                    _rsStreakSince == 0 ? 0 : unchecked(now - _rsStreakSince));
                if (stuck && !_rsCalling)
                {
                    _rsCalling = true;
                    _rsCallWritten = 0;
                    Write($" died {_rsDeathStreak} times without a kill in {Math.Max(1, (now - _rsStreakSince) / 60000)} minute(s) in {_bot.Map.FullName} ({_bot.Player.Cell}); asking the other accounts for help (Rescue Allies).");
                }
                if (_rsCalling && (_rsCallWritten == 0 || now - _rsCallWritten > 15000))
                {
                    _rsCallWritten = now;
                    WriteRescueCall();
                }
            }

            if (_rsScanAt == 0 || now - _rsScanAt > 5000)
            {
                _rsScanAt = now;
                ScanForRescueCalls();
            }
        }
        catch { }
    }

    private string OwnName => _bot.Player.Username ?? "";

    private static string RescueFile(string user) =>
        Path.Combine(RescueDir, new string(user.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).ToLowerInvariant() + ".txt");

    private void WriteRescueCall()
    {
        try
        {
            string user = OwnName;
            string server = RescueSession.ServerName(_bot.Flash);
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
        if (!RescueAllowed || RescueActive || _rsCalling || _bot.Auto.IsRunning || _busy || _restarting != 0 || _userLoggedOut)
            return;
        if (!Directory.Exists(RescueDir))
            return;
        string me = OwnName;
        string server = RescueSession.ServerName(_bot.Flash);
        if (server.Length == 0) return;
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
        RescueSession? session = null;
        Dictionary<string, string>? ownQuests = null;
        try
        {
            IScriptManager manager = _manager;
            session = new RescueSession(_bot.Player, _bot.Map, _bot.Combat, _bot.Skills, _bot.Options, _bot.Flash, manager,
                () => RescueAllowed && !_stop, () => _userLoggedOut, _bot.Send);
            _rsSession = session;
            if (!await session.BeginAsync()) return;
            Write($" helping {call.User} in {call.Room}; the original script/idle state will be restored afterward.");
            if (call.Quests.Count > 0 && (ownQuests = ReadQuestStrings()) is not null)
                BorrowQuests(ownQuests, call.Quests);
            if (!await JoinForRescue(call.Room, call.Cell, call.Pad, session)) return;
            int start = Environment.TickCount;
            while (session.CanContinue)
            {
                RescueCall? current = ReadRescueCall(file);
                if (current is null) break;
                if (!current.User.Equals(call.User, StringComparison.OrdinalIgnoreCase) ||
                    !current.Server.Equals(call.Server, StringComparison.OrdinalIgnoreCase)) break;
                if (!string.Equals(_bot.Map.FullName, current.Room, StringComparison.OrdinalIgnoreCase))
                {
                    _bot.Combat.CancelAutoAttack();
                    if (ownQuests is not null && current.Quests.Count > 0)
                        BorrowQuests(ReadQuestStrings() ?? ownQuests, current.Quests);
                    if (!await JoinForRescue(current.Room, current.Cell, current.Pad, session)) break;
                }
                else if (!string.Equals(_bot.Player.Cell, current.Cell, StringComparison.OrdinalIgnoreCase))
                    _bot.Map.Jump(current.Cell, string.IsNullOrWhiteSpace(current.Pad) ? "Left" : current.Pad);
                session.Fight();
                if (Environment.TickCount - start > RescueGiveUpMs) break;
                await Task.Delay(200);
            }
        }
        catch (Exception ex) { Write($" rescue failed: {ex.Message}"); }
        finally
        {
            try
            {
                if (session is not null)
                {
                    if (ownQuests is not null && session.InOriginalSession && _bot.Player.Playing && _bot.Flash.IsWorldLoaded)
                        SetQuestStrings(ownQuests);
                    Write($" rescue ending; interrupted={session.Interrupted}, in game={_bot.Player.Playing && _bot.Flash.IsWorldLoaded}; returning to {session.Room}.");
                    await session.CompleteAsync();
                }
            }
            catch (Exception ex) { Write($" rescue cleanup failed: {ex.Message}"); }
            finally
            {
                session?.Dispose(); _rsSession = null;
                if (RescueAllowed)
                    lock (_rsLastHelped) _rsLastHelped[call.User] = Environment.TickCount;
                _rsHelping = null;
            }
        }
    }

    private async Task<bool> JoinForRescue(string room, string cell, string pad, RescueSession session)
    {
        for (int i = 0; i < 3 && session.CanContinue; i++)
        {
            try { _bot.Map.JoinPacket(room, string.IsNullOrWhiteSpace(cell) ? "Enter" : cell, string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad); }
            catch { }
            for (int w = 0; w < 20 && session.CanContinue; w++)
            {
                if (string.Equals(_bot.Map.FullName, room, StringComparison.OrdinalIgnoreCase) && _bot.Player.Playing && _bot.Flash.IsWorldLoaded)
                {
                    if (!string.IsNullOrWhiteSpace(cell) && !string.Equals(_bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
                        _bot.Map.Jump(cell, string.IsNullOrWhiteSpace(pad) ? "Left" : pad);
                    return true;
                }
                await Task.Delay(200);
            }
        }
        return false;
    }
}
