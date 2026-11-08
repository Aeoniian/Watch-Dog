using Skua.Core.Interfaces;
using WatchDog.Shared;

namespace WatchDog;

/// <summary>
/// Rescue: when an account running a script keeps dying without killing anything (10 deaths in a row, or 15
/// minutes of only deaths), it asks for help. Eligible accounts help in its map and cell until it leaves the map
/// or the helper dies 10 times without a kill, then restore their original state.
/// </summary>
/// <remarks>
/// Same protocol as VibeSkuffle's built-in Rescue Allies, so Skua, VibeSkua and VibeSkuffle accounts on one PC
/// can help each other. The accounts talk through small files in %TEMP%\VibeSkuaTabs\rescue (one per account asking for help,
/// refreshed every 15 seconds; a file older than a minute counts as finished). A helper can help the same account
/// again after the optional shared cooldown (10 minutes by default).
/// </remarks>
internal sealed partial class Watchdog
{
    private const int RescueStaleMs = 60000;

    private static string RescueDir => Environment.GetEnvironmentVariable("WATCHDOG_RESCUE_CALL_ROOT") ?? Path.Combine(Path.GetTempPath(), "VibeSkuaTabs", "rescue");

    // The account that needs help.
    private int _rsLastDeaths = -1, _rsLastKills = -1, _rsDeathStreak, _rsStreakSince;
    private volatile bool _rsCalling;
    private volatile bool _rsHoldingHelpers;
    private int _rsHoldActive;
    private string _rsCallRoom = "";
    private int _rsManualRequest;
    private string _rsOwner = "";
    private int _rsCallWritten;
    private int _rsDeadSince;
    private int _rsDeadStatusWritten;

    // An account helping.
    private volatile string? _rsHelping;
    private int _rsScanAt;
    private readonly Dictionary<string, int> _rsLastHelped = new(StringComparer.OrdinalIgnoreCase);

    private bool RescueActive => _rsHelping is not null;
    private RescueSession? _rsSession;
    private bool RescueEnabled => SharedRescueSettings.Instance.Enabled;
    private bool RescueAllowed => RescueEnabled && SharedRescueSettings.Instance.IsAllowed(OwnName) && Opt("enabled", true);

    private volatile string _rsPhase = "", _rsOutcome = "", _rsScanStatus = "Waiting for the next scan.";

    internal string RequestManualRescue()
    {
        if (!Opt("enabled", true)) return "Enable Watch Dog before requesting help.";
        if (!RescueEnabled) return "Turn on Rescue Allies for all accounts first.";
        if (!_wasInGame) return "Log in and enter a map before requesting help.";
        if (RescueActive) return "This account is already helping someone else.";
        if (_busy || _restarting != 0) return "Wait for account recovery to finish before requesting help.";
        if (_rsCalling || _rsHoldingHelpers) return "Help is already requested for this map.";
        if (Interlocked.CompareExchange(ref _rsManualRequest, 1, 0) != 0)
            return "Your help request is already queued.";
        return "Manual help request queued.";
    }

    private void PublishRescueStatus()
    {
        try
        {
            int now = Environment.TickCount;
            var store = SharedRescueSettings.Instance;
            int cooldownMs = store.CooldownMinutes * 60000;
            int[] cooldowns;
            lock (_rsLastHelped) cooldowns = store.CooldownEnabled
                ? _rsLastHelped.Values.Select(v => Math.Max(0, (cooldownMs - unchecked(now-v) + 999)/1000)).Where(v => v > 0).OrderBy(v => v).ToArray()
                : [];
            bool requesting = _manager.ScriptRunning && !RescueSession.IsPaused(_manager) && !RescueActive;
            string helper = RescueActive ? "Helping an account." : _rsCalling ? "Requesting help." : _rsHoldingHelpers ? "Helpers are staying in this map." : _bot.Auto.IsRunning ? "Manual auto attack/hunt is active." : (_busy || _restarting != 0) ? "Recovery is in progress." : _rsScanStatus;
            RescueStatusSnapshot status = new(DateTime.UtcNow, RescueEnabled, RescueAllowed,
                _rsDeathStreak, _rsStreakSince == 0 ? 0 : unchecked(now-_rsStreakSince), store.DelayMinutes,
                store.DeathsBeforeRescue, store.CooldownEnabled, requesting, _rsCalling || _rsHoldingHelpers, helper, _rsPhase, _rsOutcome, cooldowns);
            RescueStatus.Publish(status);
            RescueAccountHub.Publish(OwnName, RescueSession.ServerName(_bot.Flash), _bot.Map.FullName, _bot.Player.Cell, status);
        }
        catch { } // Diagnostics must never interfere with rescue.
    }

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
            if (_rsDeadSince != 0)
            {
                _rsDeadSince = 0;
                _rsCallWritten = 0; // Publish the respawned caller's current cell immediately.
                _rsDeadStatusWritten = 0;
            }
            string owner = OwnName;
            if (!string.Equals(owner, _rsOwner, StringComparison.OrdinalIgnoreCase))
            {
                if ((_rsCalling || _rsHoldingHelpers) && _rsOwner.Length > 0)
                    try { File.Delete(RescueFile(_rsOwner)); } catch { }
                Interlocked.Exchange(ref _rsManualRequest, 0);
                _rsOwner = owner; _rsCalling = false; _rsHoldingHelpers = false; _rsCallRoom = ""; _rsDeathStreak = 0; _rsStreakSince = 0; _rsLastDeaths = -1;
                lock (_rsLastHelped) _rsLastHelped.Clear();
                _rsOutcome = ""; _rsScanStatus = "Waiting for the next scan.";
            }
            bool remoteHelpRequested = RescueAccountHub.ConsumeManualHelpRequest(owner);
            if (!RescueEnabled)
            {
                Interlocked.Exchange(ref _rsManualRequest, 0);
                if (_rsCalling || _rsHoldingHelpers) { _rsCalling = false; _rsHoldingHelpers = false; ClearRescueCall(); }
                _rsDeathStreak = 0; _rsStreakSince = 0; _rsLastDeaths = -1;
                return;
            }

            if (remoteHelpRequested)
            {
                string result = RequestManualRescue();
                Write($" received a manual help request from the Watch Dog account selector: {result}");
            }

            if (Interlocked.Exchange(ref _rsManualRequest, 0) != 0)
            {
                _rsCalling = true;
                _rsHoldingHelpers = false;
                _rsCallRoom = _bot.Map.FullName;
                _rsCallWritten = 0;
                _rsDeathStreak = 0;
                _rsStreakSince = 0;
                Write($" manually requested Rescue Allies help in {_bot.Map.FullName} ({_bot.Player.Cell}).");
            }

            int deaths = _bot.Stats.Deaths, kills = _bot.Stats.Kills;
            if (_rsLastDeaths < 0)
            {
                _rsLastDeaths = deaths;
                _rsLastKills = kills;
            }
            bool rsActivity = kills != _rsLastKills || deaths > _rsLastDeaths;
            if (kills != _rsLastKills)
            {
                _rsDeathStreak = 0;
                _rsStreakSince = 0;
                if (_rsCalling)
                {
                    _rsCalling = false;
                    _rsHoldingHelpers = true;
                    _rsCallRoom = _bot.Map.FullName;
                    Write(" got a kill; helpers will stay until this account leaves the map.");
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
                    _rsHoldingHelpers = true;
                    _rsCallRoom = _bot.Map.FullName;
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
                    _rsHoldingHelpers = false;
                    _rsCallRoom = _bot.Map.FullName;
                    _rsCallWritten = 0;
                    Write($" died {_rsDeathStreak} times without a kill in {Math.Max(1, (now - _rsStreakSince) / 60000)} minute(s) in {_bot.Map.FullName} ({_bot.Player.Cell}); asking the other accounts for help (Rescue Allies).");
                }
            }

            if (_rsHoldingHelpers)
            {
                if (_rsHoldActive == 0 || rsActivity) _rsHoldActive = now | 1;
                int holdLimit = SharedRescueSettings.Instance.HoldLimitMinutes;
                if (holdLimit > 0 && unchecked(now - _rsHoldActive) >= holdLimit * 60000)
                {
                    _rsHoldingHelpers = false;
                    _rsHoldActive = 0;
                    _rsCallWritten = 0;
                    ClearRescueCall();
                    Write($" no kill or death for {holdLimit} minute(s); helpers are released (hold limit).");
                }
            }
            else _rsHoldActive = 0;

            if (_rsHoldingHelpers && !string.Equals(_bot.Map.FullName, _rsCallRoom, StringComparison.OrdinalIgnoreCase))
            {
                _rsHoldingHelpers = false;
                _rsCallWritten = 0;
                ClearRescueCall();
                Write(" left the rescue map; helpers can return to their original state.");
            }
            if ((_rsCalling || _rsHoldingHelpers) && (_rsCallWritten == 0 || now - _rsCallWritten > 15000))
            {
                _rsCallWritten = now;
                WriteRescueCall();
            }

            if (_rsScanAt == 0 || now - _rsScanAt > 5000)
            {
                _rsScanAt = now;
                ScanForRescueCalls();
            }
        }
        catch { }
        finally { PublishRescueStatus(); }
    }

    /// <summary>
    /// Keeps a caller's rescue lease alive while AQW temporarily reports the dead avatar as not Playing.
    /// A real logout ends it immediately; an unloaded dead world gets a bounded grace period.
    /// </summary>
    private bool MaintainRescueCallDuringDeath(int now)
    {
        if (!_rsCalling && !_rsHoldingHelpers)
        {
            _rsDeadSince = 0;
            return false;
        }

        try
        {
            if (!RescueEnabled)
            {
                _rsCalling = false; _rsHoldingHelpers = false; _rsCallWritten = 0; _rsDeadSince = 0;
                ClearRescueCall();
                return false;
            }
            if (!_bot.Player.LoggedIn || _bot.Player.Alive)
            {
                _rsDeadSince = 0;
                return false;
            }

            if (_rsDeadSince == 0) _rsDeadSince = now | 1;
            bool worldLoaded = _bot.Flash.IsWorldLoaded;
            if (worldLoaded)
            {
                string currentRoom = _bot.Map.FullName;
                if (!string.IsNullOrWhiteSpace(currentRoom) && !string.IsNullOrWhiteSpace(_rsCallRoom) &&
                    !string.Equals(currentRoom, _rsCallRoom, StringComparison.OrdinalIgnoreCase))
                {
                    _rsCalling = false; _rsHoldingHelpers = false; _rsCallWritten = 0; _rsDeadSince = 0;
                    ClearRescueCall();
                    Write(" left the rescue map while dead; helpers can return to their original state.");
                    return false;
                }
            }
            else if (unchecked(now - _rsDeadSince) >= RescueStaleMs)
            {
                _rsCalling = false; _rsHoldingHelpers = false; _rsCallWritten = 0; _rsDeadSince = 0;
                ClearRescueCall();
                Write(" stayed out of the loaded world after dying; ending the rescue call after the respawn grace period.");
                return false;
            }

            if (_rsCallWritten == 0 || unchecked(now - _rsCallWritten) >= 15000)
            {
                if (RefreshRescueCallTimestamp(OwnName))
                    _rsCallWritten = now;
                else if (worldLoaded && !string.IsNullOrWhiteSpace(_bot.Map.FullName))
                {
                    WriteRescueCall();
                    _rsCallWritten = now;
                }
            }
            if (_rsDeadStatusWritten == 0 || unchecked(now - _rsDeadStatusWritten) >= 5000)
            {
                if (RescueStatus.Latest is { } latest)
                {
                    RescueStatusSnapshot refreshed = latest with { UpdatedUtc = DateTime.UtcNow, Calling = true };
                    RescueStatus.Publish(refreshed);
                    RescueCall? call = ReadRescueCall(RescueFile(OwnName));
                    RescueAccountHub.Publish(OwnName, RescueSession.ServerName(_bot.Flash),
                        call?.Room ?? _rsCallRoom, call?.Cell ?? _bot.Player.Cell, refreshed);
                }
                _rsDeadStatusWritten = now;
            }
            return true;
        }
        catch { return false; }
    }

    private bool RefreshRescueCallTimestamp(string user)
    {
        try
        {
            string file = RescueFile(user);
            if (!File.Exists(file)) return false;
            string[] lines = File.ReadAllLines(file);
            if (lines.Length < 6) return false;
            lines[5] = DateTime.UtcNow.Ticks.ToString();
            File.WriteAllLines(file + ".tmp", lines);
            File.Move(file + ".tmp", file, true);
            return true;
        }
        catch { return false; }
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
        { _rsScanStatus = "No active rescue calls seen."; return; }
        string me = OwnName;
        string server = RescueSession.ServerName(_bot.Flash);
        if (server.Length == 0) return;
        int now = Environment.TickCount;
        _rsScanStatus = "No eligible rescue call seen on the last scan.";
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
            { _rsScanStatus = "An active call is on another server."; continue; } // a different server: can't reach it without logging out
            SharedRescueSettings store = SharedRescueSettings.Instance;
            if (store.CooldownEnabled)
            {
                int cooldownMs = store.CooldownMinutes * 60000;
                lock (_rsLastHelped)
                    if (_rsLastHelped.TryGetValue(call.User, out int last) && now - last < cooldownMs)
                        continue;
            }
            _rsHelping = call.User; _rsPhase = "Joining rescue"; _rsOutcome = "";
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
            session = new RescueSession(_bot.Player, _bot.Map, _bot.Combat, _bot.Skills, _bot.Inventory, _bot.Wait,
                _bot.Monsters, (SharedRescueSettings.Instance.UseBossLoadout ? BossRescueLoadout.Read(OwnName) : null), _bot.Options, _bot.Flash, manager,
                () => RescueAllowed && !_stop, () => _userLoggedOut, _bot.Send);
            _rsSession = session;
            if (!await session.BeginAsync()) return;
            Write($" helping {call.User} in {call.Room}; the original script/idle state will be restored afterward.");
            if (call.Quests.Count > 0 && (ownQuests = ReadQuestStrings()) is not null)
                BorrowQuests(ownQuests, call.Quests);
            int observedDeaths = _bot.Stats.Deaths, observedKills = _bot.Stats.Kills, deathStreak = 0;
            while (session.CanContinue)
            {
                RescueCall? current = ReadRescueCall(file);
                if (current is null) break;
                if (!current.User.Equals(call.User, StringComparison.OrdinalIgnoreCase) ||
                    !current.Server.Equals(call.Server, StringComparison.OrdinalIgnoreCase)) break;
                if (!string.Equals(current.Room, call.Room, StringComparison.OrdinalIgnoreCase))
                {
                    _rsOutcome = $"{call.User} left the rescue map.";
                    Write($" {call.User} left {call.Room}; ending the rescue.");
                    break;
                }

                int deaths = _bot.Stats.Deaths, kills = _bot.Stats.Kills;
                if (kills != observedKills) deathStreak = 0;
                if (deaths > observedDeaths)
                {
                    deathStreak += deaths - observedDeaths;
                    observedDeaths = deaths; observedKills = kills;
                    _bot.Combat.CancelAutoAttack();
                    _rsPhase = "Waiting to respawn";
                    int helperDeathLimit = SharedRescueSettings.Instance.HelperDeathsWithoutKill;
                    if (deathStreak >= helperDeathLimit)
                    {
                        _rsOutcome = $"Stopped after {deathStreak} deaths without a kill (limit {helperDeathLimit}).";
                        Write($" died {deathStreak} times without a kill while helping {call.User} (helper limit {helperDeathLimit}); ending this rescue.");
                        break;
                    }
                    await Task.Delay(500);
                    continue;
                }
                observedDeaths = deaths; observedKills = kills;

                if (!session.CanAct || !HelperAlive())
                {
                    _rsPhase = "Waiting to respawn";
                    if (!HelperAlive())
                        try { _bot.Flash.CallGameFunction("world.resPlayer"); } catch { }
                    await Task.Delay(1000);
                    continue;
                }

                if (!string.Equals(_bot.Map.FullName, call.Room, StringComparison.OrdinalIgnoreCase))
                {
                    _bot.Combat.CancelAutoAttack();
                    if (ownQuests is not null && current.Quests.Count > 0)
                        BorrowQuests(ReadQuestStrings() ?? ownQuests, current.Quests);
                    _rsPhase = "Rejoining rescue cell";
                    if (!await JoinForRescue(call.Room, current.Cell, current.Pad, session))
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    _rsPhase = "Fighting";
                }
                else if (!string.Equals(_bot.Player.Cell, current.Cell, StringComparison.OrdinalIgnoreCase))
                    _bot.Map.Jump(current.Cell, string.IsNullOrWhiteSpace(current.Pad) ? "Left" : current.Pad);

                _rsPhase = "Fighting";
                session.Fight();
                await Task.Delay(200);
            }
        }
        catch (Exception ex) { _rsOutcome = "Rescue failed; see the local log."; Write($" rescue failed: {ex.Message}"); }
        finally
        {
            try
            {
                if (session is not null)
                {
                    if (ownQuests is not null && session.InOriginalSession && _bot.Player.Playing && _bot.Flash.IsWorldLoaded)
                        SetQuestStrings(ownQuests);
                    Write($" rescue ending; interrupted={session.Interrupted}, in game={_bot.Player.Playing && _bot.Flash.IsWorldLoaded}; returning to {session.Room}.");
                    _rsPhase = "Returning / restoring original state";
                    if (_rsOutcome.Length == 0) _rsOutcome = session.Interrupted ? "Interrupted by another action." : "Rescue ended; restoring original state.";
                    await session.CompleteAsync();
                }
            }
            catch (Exception ex) { _rsOutcome = "Cleanup could not finish; see the local log."; Write($" rescue cleanup failed: {ex.Message}"); }
            finally
            {
                session?.Dispose(); _rsSession = null;
                if (RescueAllowed)
                    lock (_rsLastHelped) _rsLastHelped[call.User] = Environment.TickCount;
                _rsHelping = null; _rsPhase = ""; PublishRescueStatus();
            }
        }
    }

    private async Task<bool> JoinForRescue(string room, string cell, string pad, RescueSession session)
    {
        for (int i = 0; i < 3 && session.CanContinue; i++)
        {
            if (!session.CanAct) { await Task.Delay(200); continue; }
            try { _bot.Map.JoinPacket(room, string.IsNullOrWhiteSpace(cell) ? "Enter" : cell, string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad); }
            catch { }
            for (int w = 0; w < 20 && session.CanContinue; w++)
            {
                if (string.Equals(_bot.Map.FullName, room, StringComparison.OrdinalIgnoreCase) && session.CanAct)
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

    private bool HelperAlive()
    {
        try
        {
            const string hp = "world.myAvatar.objData.intHP";
            return _bot.Flash.IsNull(hp) || _bot.Flash.GetGameObject<int>(hp) > 0;
        }
        catch { return true; }
    }
}
