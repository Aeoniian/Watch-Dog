using System.ComponentModel;
using Skua.Core.Interfaces;

namespace WatchDog.Shared;

// One helper's ownership of its script/combat state. Shared by both rescue engines.
internal sealed class RescueSession : IDisposable
{
    private readonly IScriptPlayer _player;
    private readonly IScriptMap _map;
    private readonly IScriptCombat _combat;
    private readonly IScriptSkill _skills;
    private readonly IScriptOption _options;
    private readonly IFlashUtil _flash;
    private readonly IScriptManager _manager;
    private readonly IScriptSend? _send;
    private readonly bool _returnToOwnHouse;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _userCancelled;
    private readonly string _account, _script, _server;
    private CancellationTokenSource? _scriptToken;
    private readonly System.Reflection.PropertyInfo? _pauseProperty;
    private bool _stoppedOriginal;
    private readonly bool _wasRunning, _wasPaused, _infiniteRange, _stopAttacking;
    private bool _ownChange, _begun, _finished, _finishing, _ownsCombat;
    private volatile bool _interrupted;
    internal string Room { get; }
    internal string Cell { get; }
    internal string Pad { get; }
    internal bool WasRunning => _wasRunning;
    internal bool WasPaused => _wasPaused;
    internal RescueSession(IScriptPlayer player, IScriptMap map, IScriptCombat combat, IScriptSkill skills,
        IScriptOption options, IFlashUtil flash, IScriptManager manager, Func<bool> enabled, Func<bool>? userCancelled = null,
        IScriptSend? send = null)
    {
        _player = player; _map = map; _combat = combat; _skills = skills; _options = options;
        _flash = flash; _manager = manager; _enabled = enabled; _userCancelled = userCancelled ?? (() => false);
        _account = player.Username ?? ""; _server = ServerName(_flash); _script = manager.LoadedScript ?? "";
        _scriptToken = manager.ScriptCts; _wasRunning = manager.ScriptRunning; _wasPaused = IsPaused(manager);
        _pauseProperty = manager.GetType().GetProperty("ScriptPaused");
        _infiniteRange = options.InfiniteRange; _stopAttacking = combat.StopAttacking;
        Room = map.FullName; Cell = player.Cell; Pad = player.Pad;
        _send = send;
        try
        {
            int characterId = flash.GetGameObject<int>("world.myAvatar.objData.CharID");
            _returnToOwnHouse = characterId > 0 && Room.Equals($"house-{characterId}", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        _manager.PropertyChanged += OnManagerChanged;
    }
    internal static bool IsPaused(IScriptManager manager) => manager.GetType().GetProperty("ScriptPaused")?.GetValue(manager) is true;
    private void SetPaused(bool value) => _pauseProperty?.SetValue(_manager, value);
    internal static string ServerName(IFlashUtil flash)
    {
        try { return (flash.GetGameObject<string>("objServerInfo.sName", "") ?? "").Trim().Trim('"'); }
        catch { return ""; }
    }
    internal bool SameAccount => _account.Length > 0 && string.Equals(_account, _player.Username, StringComparison.OrdinalIgnoreCase);
    internal bool InOriginalSession => SameAccount && (_server.Length == 0 || string.Equals(_server, ServerName(_flash), StringComparison.OrdinalIgnoreCase));
    internal bool Interrupted => _interrupted || _userCancelled() || !InOriginalSession ||
        !string.Equals(_script, _manager.LoadedScript, StringComparison.OrdinalIgnoreCase) ||
        !ReferenceEquals(_scriptToken, _manager.ScriptCts) || (_scriptToken?.IsCancellationRequested ?? false);
    internal bool CanContinue => _begun && !_finished && !_interrupted && !Interrupted && _enabled() && _player.Playing && _flash.IsWorldLoaded && (!_ownsCombat || !_combat.StopAttacking);
    private void OnManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ownChange || !_begun || _finished) return;
        if (e.PropertyName is nameof(IScriptManager.ScriptRunning) or nameof(IScriptManager.LoadedScript)
            or "ScriptPaused") _interrupted = true;
    }
    internal async Task<bool> BeginAsync()
    {
        if (!_enabled() || Interrupted || !_player.Playing || !_flash.IsWorldLoaded) return false;
        _begun = true;
        if (_wasRunning && !_wasPaused)
        {
            _ownChange = true;
            try
            {
                if (_pauseProperty?.CanWrite == true) SetPaused(true);
                else
                {
                    _stoppedOriginal = true;
                    await _manager.StopScript();
                    _scriptToken = _manager.ScriptCts;
                }
            }
            finally { _ownChange = false; }
        }
        _combat.CancelAutoAttack();
        await Task.Delay(250);
        if (!CanContinue) return false;
        _options.InfiniteRange = true;
        _combat.StopAttacking = false;
        _ownsCombat = true;
        return true;
    }
    internal void Fight()
    {
        if (!CanContinue) return;
        _combat.Attack("*");
        for (int skill = 1; skill <= 4 && CanContinue; skill++)
            if (_skills.CanUseSkill(skill)) _skills.UseSkill(skill);
    }
    internal async Task CompleteAsync()
    {
        if (_finished || _finishing) return;
        _finishing = true;
        try
        {
            if (!_begun || !InOriginalSession) return;
            bool sameRun = ReferenceEquals(_scriptToken, _manager.ScriptCts) &&
                string.Equals(_script, _manager.LoadedScript, StringComparison.OrdinalIgnoreCase);
            // A new script owns its combat/options; a manually stopped script only needs attack cleanup.
            if ((!sameRun && _manager.ScriptRunning) || (Interrupted && _manager.ScriptRunning && !IsPaused(_manager))) return;
            _combat.CancelAutoAttack();
            if (_options.InfiniteRange) _options.InfiniteRange = _infiniteRange;
            if (!_combat.StopAttacking) _combat.StopAttacking = _stopAttacking;
            try
            {
                if (!Interrupted && _player.Playing && _flash.IsWorldLoaded && !string.IsNullOrWhiteSpace(Room))
                {
                    if (!string.Equals(_map.FullName, Room, StringComparison.OrdinalIgnoreCase))
                    {
                        // AQW can reject an immediate second transfer while its transfer cooldown expires.
                        for (int attempt = 0; attempt < 3 && !Interrupted && _player.Playing; attempt++)
                        {
                            if (_flash.IsWorldLoaded && string.Equals(_map.FullName, Room, StringComparison.OrdinalIgnoreCase)) break;
                            // AQW ignores normal tfer packets to house-<CharID>. Use the house command
                            // only when the captured room is proven to be this account's own house.
                            if (_returnToOwnHouse && _send is not null)
                                _send.Packet($"%xt%zm%house%1%{_account}%");
                            else
                                _map.JoinPacket(Room, string.IsNullOrWhiteSpace(Cell) ? "Enter" : Cell, string.IsNullOrWhiteSpace(Pad) ? "Spawn" : Pad);
                            for (int i = 0; i < 40 && !Interrupted && _player.Playing; i++)
                            {
                                if (_flash.IsWorldLoaded && string.Equals(_map.FullName, Room, StringComparison.OrdinalIgnoreCase)) break;
                                await Task.Delay(100);
                            }
                        }
                    }
                    if (!Interrupted && _flash.IsWorldLoaded && string.Equals(_map.FullName, Room, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(_player.Cell, Cell, StringComparison.OrdinalIgnoreCase))
                        _map.Jump(Cell, string.IsNullOrWhiteSpace(Pad) ? "Left" : Pad);
                }
            }
            finally
            {
                if (_wasRunning && !_wasPaused && !Interrupted && _manager.ScriptRunning && IsPaused(_manager))
                {
                    _ownChange = true;
                    try { SetPaused(false); } finally { _ownChange = false; }
                }
                else if (_stoppedOriginal && _wasRunning && !Interrupted && !_manager.ScriptRunning && _player.Playing && _flash.IsWorldLoaded)
                {
                    var silent = _manager.GetType().GetProperty("SilentConfig");
                    object? previous = silent?.GetValue(_manager);
                    _ownChange = true;
                    try { silent?.SetValue(_manager, true); await _manager.StartScript(); }
                    finally { if (previous is not null) silent?.SetValue(_manager, previous); _ownChange = false; }
                }
            }
        }
        finally
        {
            _finished = true; _finishing = false;
            _manager.PropertyChanged -= OnManagerChanged;
        }
    }
    public void Dispose() => _manager.PropertyChanged -= OnManagerChanged;
}
