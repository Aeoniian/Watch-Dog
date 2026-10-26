using Skua.Core.Interfaces;

namespace WatchDog;

/// <summary>
/// The watchdog loop: looks at the game twice a second and steps in when something is stuck.
/// </summary>
/// <remarks>
/// Everything goes through the normal bot API (no client changes), so it works in Skua and VibeSkua alike.
/// It waits generously before acting, so the client's own auto relogin always gets the first go.
/// </remarks>
internal sealed partial class Watchdog
{
    private readonly IScriptInterface _bot;
    private readonly IScriptManager _manager;
    private readonly IDispatcherService? _dispatcher;
    private readonly Func<IOptionContainer?> _options;
    private Thread? _thread;
    private volatile bool _stop;

    public Watchdog(IScriptInterface bot, IScriptManager manager, IDispatcherService? dispatcher, Func<IOptionContainer?> options)
    {
        _bot = bot;
        _manager = manager;
        _dispatcher = dispatcher;
        _options = options;
        try
        {
            Type servers = bot.Servers.GetType();
            _intentionalProp = servers.GetProperty("IsIntentionalLogout");
            _disconnectForRelogin = servers.GetMethod("DisconnectForRelogin", Type.EmptyTypes);
        }
        catch { }
    }

    public void Start()
    {
        // Script starts, as they happen (a short script can start and finish between two ticks).
        _manager.PropertyChanged += (_, e) =>
        {
            try
            {
                if (e.PropertyName == nameof(IScriptManager.ScriptRunning) && _manager.ScriptRunning && !_ourStart)
                    _otherStartAt = Environment.TickCount | 1;
            }
            catch { }
        };
        HookLogoutSignals();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Watch Dog" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        UnhookEvents();
        UnhookLogoutSignals();
        try { _relogCts?.Cancel(); } catch { }
    }

    private void Loop()
    {
        // Give the client time to start up.
        Thread.Sleep(10000);
        while (!_stop)
        {
            try
            {
                if (Opt<bool>("enabled", true))
                    Tick();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Watch Dog: {ex}");
            }
            Thread.Sleep(500);
        }
    }

    // ---- options / log -----------------------------------------------------------------------------------

    private T Opt<T>(string name, T fallback) where T : IConvertible
    {
        try
        {
            IOptionContainer? c = _options();
            if (c is null)
                return fallback;
            // An option that was never saved has no value yet: use the default.
            IOption? o = c.Options.FirstOrDefault(x => x.Name == name);
            if (o is null || !c.OptionValues.ContainsKey(o))
                return fallback;
            return c.Get<T>(o) is T v ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private string _who = "?";

    private void Write(string message)
    {
        Log.Write(_who, message);
        try { _bot.Log("[Watch Dog]" + message); } catch { }
    }

    // ---- what the game looks like right now -------------------------------------------------------------

    private bool InGame
    {
        get
        {
            try { return _bot.Player.Playing && _bot.Flash.IsWorldLoaded; }
            catch { return false; }
        }
    }

    private string ConnText()
    {
        try
        {
            if (_bot.Flash.IsNull("mcConnDetail.stage"))
                return string.Empty;
            string? t = _bot.Flash.GetGameObject<string>("mcConnDetail.txtDetail.text", string.Empty);
            return (t ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- state -----------------------------------------------------------------------------------------

    private bool _wasInGame;
    private int _lastInGame;
    private string? _user, _pass;           // kept in memory only, to fill in the login again
    private string? _server, _room, _cell, _pad;
    private int _roomCheck;

    private volatile bool _busy;             // a recovery of ours is running
    private int _stalls;                     // recoveries in a row without getting back in game
    private int _lastAction;
    private readonly Dictionary<string, int> _avoidServers = new(StringComparer.OrdinalIgnoreCase);

    private string _text = string.Empty;
    private int _textSince = Environment.TickCount;
    private int _outSince, _lostSince;
    private bool _scriptWhenLeft;

    private const int StallMs = 20000;
    private const int OutOfGameMs = 180000;
    private const int LostMs = 60000;

    // ---- VibeSkua 2.0.7+ (its own "universal auto-relogin watchdog") ------------------------------------
    // Found by reflection, so the plugin still loads in Skua 1.4.5 and older VibeSkua, which don't have them.

    private readonly System.Reflection.PropertyInfo? _intentionalProp;
    private readonly System.Reflection.MethodInfo? _disconnectForRelogin;
    private bool _ownLogout; // our own relog logged out (VibeSkua counts any logout as intentional)

    /// <summary>VibeSkua 2.0.7+: tells the user's own Log Out / Change Server apart from a drop.</summary>
    private bool KnowsIntentionalLogout => _intentionalProp is not null && _disconnectForRelogin is not null;

    /// <summary>The player logged out (or went to the server list) on purpose; leave the account alone.</summary>
    private bool LoggedOutOnPurpose => _userLoggedOut && !_ownLogout;

    /// <summary>The client re-logs by itself within seconds (VibeSkua 2.0.7+ with Auto Relogin on): give it time first.</summary>
    private bool ClientRelogs
    {
        get
        {
            try { return KnowsIntentionalLogout && _bot.Options.AutoRelogin; }
            catch { return false; }
        }
    }

    private int StallLimit => ClientRelogs ? 60000 : StallMs;
    private int LostLimit => ClientRelogs ? 120000 : LostMs;

    /// <summary>Back to the login screen for a relog, without it counting as the player's own logout where possible.</summary>
    private void LogoutForRelog()
    {
        _ownLogout = true;
        if (_disconnectForRelogin is not null)
        {
            try { _disconnectForRelogin.Invoke(_bot.Servers, null); return; } catch { }
        }
        try { _bot.Servers.Logout(); } catch { }
    }

    private void Tick()
    {
        int now = Environment.TickCount;
        TrackScriptStops(now);
        if (_busy)
            return;

        if (InGame)
        {
            OnInGameTick(now);
            return;
        }

        // Just left the game.
        if (_wasInGame)
        {
            _wasInGame = false;
            _leftGameAt = now | 1;
            _scriptWhenLeft = ScriptRanRecently;
            if (_scriptWhenLeft)
                _restartScript = true;
            ResetText(string.Empty);
        }

        // The player clicked Log Out / Change Server (VibeSkua 2.0.7 stops the script itself). Don't log
        // back in or start the script again until they're back in game.
        if (LoggedOutOnPurpose)
        {
            _outSince = 0;
            _lostSince = 0;
            _restartScript = false;
            _scriptWhenLeft = false;
            ResetText(string.Empty);
            return;
        }

        string text = ConnText();

        // "Communication with server has been lost" left on screen.
        if (text.Contains("has been lost", StringComparison.OrdinalIgnoreCase))
        {
            if (_lostSince == 0)
                _lostSince = now | 1;
            else if (now - _lostSince >= LostLimit && Opt("connectionLost", true) && BackoffAllows())
            {
                _lostSince = 0;
                StartRecovery($" stuck on \"{text}\" for {LostLimit / 1000}s; logging back in", 1, false);
                return;
            }
        }
        else
        {
            _lostSince = 0;
        }

        // On the login screen with nothing happening.
        if (text.Length == 0)
        {
            bool wanted = Opt("relogOut", true) && (_scriptWhenLeft || _restartScript || !Opt("relogOutOnlyScript", true));
            if (!wanted || _user is null)
                _outSince = 0;
            else if (_outSince == 0)
                _outSince = now | 1;
            else if (now - _outSince >= OutOfGameMs && BackoffAllows())
            {
                _outSince = 0;
                StartRecovery($" logged out with nothing happening for {OutOfGameMs / 60000} minutes; logging back in", 1, false);
                return;
            }
            ResetText(text);
            return;
        }
        _outSince = 0;

        if (text.Contains("has been lost", StringComparison.OrdinalIgnoreCase) || text.Contains("loading map", StringComparison.OrdinalIgnoreCase))
        {
            ResetText(text);
            return;
        }

        // The same login message for 20 seconds: stuck.
        if (text != _text)
        {
            ResetText(text);
            return;
        }
        if (now - _textSince < StallLimit || !BackoffAllows() || !Opt("stuckLogin", true))
            return;
        AvoidCurrentServer();
        StartRecovery($" stuck on \"{text}\" for {StallLimit / 1000}s", _stalls + 1, true);
    }

    private void ResetText(string text)
    {
        _text = text;
        _textSince = Environment.TickCount;
    }

    private void OnInGameTick(int now)
    {
        if (_stalls > 0)
            Write($" in game again after {_stalls} recover{(_stalls == 1 ? "y" : "ies")}.");
        _stalls = 0;
        _ownLogout = false;
        ClearUserLogout(!_wasInGame, now);
        _wasInGame = true;
        _lastInGame = now;
        _outSince = 0;
        _lostSince = 0;
        _scriptWhenLeft = false;

        if (now - _roomCheck > 10000)
        {
            _roomCheck = now;
            try
            {
                string u = _bot.Player.Username, p = _bot.Player.Password;
                if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrEmpty(p))
                {
                    if (_user is null)
                        Log.Write(u, " watching this account.");
                    _user = u;
                    _pass = p;
                    _who = u;
                }
            }
            catch { }
            try
            {
                string room = _bot.Map.FullName;
                if (!string.IsNullOrWhiteSpace(room) && room.Contains('-'))
                {
                    _room = room;
                    _cell = _bot.Player.Cell;
                    _pad = _bot.Player.Pad;
                }
                string? server = _bot.Flash.GetGameObject<string>("objServerInfo.sName", null);
                if (!string.IsNullOrWhiteSpace(server))
                    _server = server.Trim();
            }
            catch { }
        }

        HookEventsIfNeeded();
        CheckStuckScript(now);
        RescueTick(now);

        if (_restartScript && _restarting == 0 && !RescueActive)
        {
            if (Opt("restartScript", true))
                _ = Task.Run(RestartScriptAfterLogin);
            else
                _restartScript = false;
        }
        else if (_returnToRoom && !_restartScript && _restarting == 0 && !RescueActive)
        {
            _returnToRoom = false;
            if (Opt("returnToRoom", true) && !_manager.ScriptRunning && _room is { } room)
                _ = Task.Run(() => ReturnToRoom(room, _cell, _pad));
        }
    }

    /// <summary>After 6 recoveries in a row wait a minute between tries, after 12 ten minutes. Never gives up.</summary>
    private bool BackoffAllows()
    {
        int since = Environment.TickCount - _lastAction;
        if (_stalls >= 12)
            return since >= 600000;
        if (_stalls >= 6)
            return since >= 60000;
        return true;
    }

    private void AvoidCurrentServer()
    {
        try
        {
            string? s = _bot.Servers.LastName;
            if (!string.IsNullOrWhiteSpace(s))
                lock (_avoidServers)
                    _avoidServers[s] = Environment.TickCount;
        }
        catch { }
    }

    // ---- script running? --------------------------------------------------------------------------------

    private int _scriptSeenAt, _stoppedInGameAt;

    /// <summary>
    /// A script often stops a moment before the game is seen as gone (calls start failing), so this looks back a
    /// little. A script that stopped while the game carried on for 5 seconds was stopped on purpose or finished.
    /// </summary>
    private bool ScriptRanRecently => _manager.ScriptRunning || (_scriptSeenAt != 0 && Environment.TickCount - _scriptSeenAt < 45000);

    private bool _lastRunning;
    private int _leftGameAt, _otherStartAt;
    private volatile bool _ourStart; // RestartScriptAfterLogin is starting it

    private void TrackScriptStops(int now)
    {
        // A rescue on older hosts temporarily stops the original script. Keep recovery aware of it.
        if (RescueActive && _rsSession is { WasRunning: true, WasPaused: false } session && !session.Interrupted)
        {
            _scriptSeenAt = now | 1; _stoppedInGameAt = 0;
            return;
        }
        bool running = _manager.ScriptRunning;
        // A start we didn't make (VibeSkua 2.0.7's relog starts the script again itself, or the player did):
        // then there's nothing left for us to restart after this relog.
        if (running && !_lastRunning && !_ourStart)
            _otherStartAt = now | 1; // (also caught by the PropertyChanged handler in Start)
        _lastRunning = running;
        if (running)
        {
            _scriptSeenAt = now | 1;
            _stoppedInGameAt = 0;
            return;
        }
        if (_scriptSeenAt == 0)
            return;
        if (_busy || !InGame)
        {
            _stoppedInGameAt = 0;
            return;
        }
        if (_stoppedInGameAt == 0)
            _stoppedInGameAt = now | 1;
        else if (now - _stoppedInGameAt > 5000)
            _scriptSeenAt = _stoppedInGameAt = 0;
    }
}
