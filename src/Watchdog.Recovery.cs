using Skua.Core.Interfaces;
using Skua.Core.Models.Servers;

namespace WatchDog;

internal sealed partial class Watchdog
{
    private CancellationTokenSource? _relogCts;
    private volatile bool _restartScript;
    private volatile bool _returnToRoom;
    private int _restarting;
    /// <param name="level">Recoveries in a row (spreads the retry out a little more after the first).</param>
    /// <param name="loginStuck">The login itself is stuck (reset the login screen), rather than a relog from in game.</param>
    private void StartRecovery(string why, int level, bool loginStuck)
    {
        _busy = true;
        _stalls++;
        _lastAction = Environment.TickCount;
        if (_stalls == 12)
            Write(" 12 recoveries in a row (AQW may be down); trying every 10 minutes from now on.");
        _ = Task.Run(() => RecoverAsync(why, level, loginStuck));
    }

    private async Task RecoverAsync(string why, int level, bool loginStuck)
    {
        try
        {
            Write($"{why} (recovery {_stalls}).");
            _returnToRoom = true;
            if (ScriptRanRecently)
                _restartScript = true;
            if (_manager.ScriptRunning)
                await _manager.StopScript();

            if (_user is null || _pass is null)
            {
                Write(" no login seen in this client yet (log in once by hand); can't log back in.");
                return;
            }

            if (loginStuck || !InGame)
            {
                try { _bot.Flash.CallGameFunction("gotoAndPlay", "Login"); } catch { }
                await Task.Delay(2000);
            }
            else
            {
                // A relog from in game.
                try { _bot.Servers.Logout(); } catch { }
                await Task.Delay(3000);
            }

            // When AQW hiccups every client recovers at once: spread them out.
            await Task.Delay((level <= 1 ? 1000 : 3000) + Random.Shared.Next(0, 5000));

            // Fill in the login fields, then log in on the same server unless it's the one that got stuck.
            try { _bot.Servers.SetLoginInfo(_user, _pass); } catch { }
            try { _bot.Servers.Login(_user, _pass); } catch { }
            string server = PickServer();
            _relogCts?.Dispose();
            _relogCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            bool ok;
            try
            {
                ok = await _bot.Servers.EnsureRelogin(server, _relogCts.Token);
            }
            catch (OperationCanceledException)
            {
                ok = false;
            }
            if (!ok)
                Write($" logging in on {server} didn't work this time; trying again shortly.");
        }
        catch (Exception ex)
        {
            Write($" recovery failed: {ex.Message}");
        }
        finally
        {
            ResetText(string.Empty);
            _busy = false;
        }
    }

    private string PickServer()
    {
        int now = Environment.TickCount;
        lock (_avoidServers)
        {
            foreach (string k in _avoidServers.Where(kv => now - kv.Value > 180000).Select(kv => kv.Key).ToList())
                _avoidServers.Remove(k);
        }
        string? preferred = _server;
        bool avoided;
        lock (_avoidServers)
            avoided = preferred is not null && _avoidServers.ContainsKey(preferred);
        if (!string.IsNullOrWhiteSpace(preferred) && !avoided)
            return preferred;
        try
        {
            List<Server> list = _bot.Servers.ServerList ?? new();
            Server? pick;
            lock (_avoidServers)
                pick = list.Where(s => s.Online && !s.Upgrade && s.PlayerCount < s.MaxPlayers - 20 && !_avoidServers.ContainsKey(s.Name))
                    .OrderBy(s => s.PlayerCount).FirstOrDefault();
            if (pick is not null)
                return pick.Name;
        }
        catch { }
        return preferred ?? "Twilly";
    }

    /// <summary>
    /// Starts the script again once the account is properly back in game: logged in, map loaded and steady for
    /// a few seconds. Checks that it really is running afterwards; up to 3 tries.
    /// </summary>
    private async Task RestartScriptAfterLogin()
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1)
            return;
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!await WaitInGameSteady(6000, 180000))
                    return; // not back yet; the next time in game tries again
                if (!_restartScript)
                    return;
                if (_manager.ScriptRunning)
                {
                    _restartScript = false;
                    return;
                }
                if (string.IsNullOrEmpty(_manager.LoadedScript))
                {
                    _restartScript = false;
                    Write(" no script loaded to start again.");
                    return;
                }
                Write(attempt == 1 ? " back in game; starting the script again." : $" starting the script again (try {attempt} of 3).");
                Exception? error = await _manager.StartScript();
                if (error is not null)
                {
                    Write($" couldn't start the script: {error.Message}");
                }
                else
                {
                    await Task.Delay(15000);
                    if (!_manager.ScriptRunning && !InGame)
                        return; // knocked out again; leaving the game sets it up for another go
                    // Running, or it finished / was stopped by itself while the game is fine: done either way
                    // (starting a script that ends by itself over and over would be wrong).
                    _restartScript = false;
                    return;
                }
                await Task.Delay(10000);
            }
            _restartScript = false;
            Write(" couldn't get the script running again after 3 tries; leaving it stopped.");
        }
        catch (Exception ex)
        {
            Write($" couldn't start the script: {ex.Message}");
        }
        finally
        {
            _restarting = 0;
        }
    }

    private async Task<bool> WaitInGameSteady(int steadyMs, int timeoutMs)
    {
        int start = Environment.TickCount, since = 0;
        while (!_stop && Environment.TickCount - start < timeoutMs)
        {
            if (_busy || !InGame)
                since = 0;
            else if (since == 0)
                since = Environment.TickCount | 1;
            else if (Environment.TickCount - since >= steadyMs)
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    private async Task ReturnToRoom(string room, string? cell, string? pad)
    {
        try
        {
            if (!await WaitInGameSteady(3000, 60000) || _manager.ScriptRunning)
                return;
            if (string.Equals(_bot.Map.FullName, room, StringComparison.OrdinalIgnoreCase))
                return;
            Write($" back in game; returning to {room}.");
            _bot.Map.Join(room, string.IsNullOrWhiteSpace(cell) ? "Enter" : cell, string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad);
        }
        catch (Exception ex)
        {
            Write($" couldn't return to {room}: {ex.Message}");
        }
    }

    // ---- a running script that stopped doing anything -------------------------------------------------

    private int _stuckCheckAt, _stuckSince, _lastStuckRelog;
    private string _stuckSignature = "";

    private void CheckStuckScript(int now)
    {
        if (now - _stuckCheckAt < 30000)
            return;
        _stuckCheckAt = now;
        int minutes = Opt("stuckScriptMinutes", 15);
        if (minutes <= 0 || !_manager.ScriptRunning)
        {
            _stuckSince = 0;
            return;
        }
        string signature;
        try
        {
            IScriptBotStats s = _bot.Stats;
            signature = $"{s.Kills}|{s.Drops}|{s.QuestsCompleted}|{s.QuestsAccepted}|{_bot.Player.Gold}|{_bot.Map.FullName}|{_bot.Player.Cell}";
        }
        catch
        {
            return;
        }
        if (signature != _stuckSignature || _stuckSince == 0)
        {
            _stuckSignature = signature;
            _stuckSince = now | 1;
            return;
        }
        if (now - _stuckSince < minutes * 60000)
            return;
        if (_lastStuckRelog != 0 && now - _lastStuckRelog < 1200000)
            return;
        _lastStuckRelog = now | 1;
        _stuckSince = 0;
        StartRecovery($" the script has done nothing for {minutes} minutes (no kills, drops, quests or moving); logging out and back in", 1, false);
    }

    // ---- "Quest Complete Failed: Missing Quest Progress" ------------------------------------------------
    // The client only passes packets to listeners while a script runs, and drops all listeners when a script
    // stops, so the listener is added again each time a script starts.

    private bool _hooked;
    private readonly Queue<int> _questErrors = new();
    private readonly Dictionary<int, Queue<int>> _questFailures = new();
    private int _lastQuestRelog;

    private void HookEventsIfNeeded()
    {
        bool running = _manager.ScriptRunning;
        if (running && !_hooked)
        {
            try
            {
                _bot.Events.ExtensionPacketReceived -= OnPacket;
                _bot.Events.ExtensionPacketReceived += OnPacket;
                _hooked = true;
            }
            catch { }
        }
        else if (!running)
        {
            _hooked = false;
        }
    }

    private void UnhookEvents()
    {
        try { _bot.Events.ExtensionPacketReceived -= OnPacket; } catch { }
        _hooked = false;
    }

    private void OnPacket(dynamic packet)
    {
        try
        {
            string raw = Convert.ToString(packet) ?? "";
            if (raw.Contains("Missing Quest Progress", StringComparison.OrdinalIgnoreCase))
                CountQuestError(_questErrors, 5, "\"Quest Complete Failed: Missing Quest Progress\"");
            dynamic? data = packet["params"]?.dataObj;
            if (data is not null && (string?)data.cmd == "ccqr" && (int?)data.bSuccess != 1)
            {
                int id = (int?)data.QuestID ?? 0;
                Queue<int> q;
                lock (_questFailures)
                {
                    if (_questFailures.Count > 50)
                        _questFailures.Clear();
                    if (!_questFailures.TryGetValue(id, out q!))
                        _questFailures[id] = q = new Queue<int>();
                }
                CountQuestError(q, 10, $"quest {id} turn-in refused");
            }
        }
        catch { }
    }

    private void CountQuestError(Queue<int> times, int needed, string what)
    {
        int now = Environment.TickCount;
        lock (times)
        {
            times.Enqueue(now);
            while (times.Count > 0 && now - times.Peek() > 120000)
                times.Dequeue();
            if (times.Count < needed)
                return;
            times.Clear();
        }
        if (!Opt("questErrors", true) || _busy || !_manager.ScriptRunning || !InGame)
            return;
        if (_lastQuestRelog != 0 && now - _lastQuestRelog < 600000)
            return;
        _lastQuestRelog = now | 1;
        StartRecovery($" {what} {needed} times in 2 minutes while farming (an AQW bug a relog fixes); logging out and back in", 1, false);
    }
}
