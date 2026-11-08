using System.ComponentModel;
using System.Reflection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;

namespace WatchDog.Shared;

// One helper's ownership of its script/combat state. Shared by both rescue engines.
internal sealed class RescueSession : IDisposable
{
    private readonly IScriptPlayer _player;
    private readonly IScriptMap _map;
    private readonly IScriptCombat _combat;
    private readonly IScriptSkill _skills;
    private readonly IScriptInventory _inventory;
    private readonly IScriptWait _wait;
    private readonly IScriptMonster _monsters;
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
    private readonly BossRescueLoadout? _bossLoadout;
    private int[] _equippedBeforeRescue = [];
    private int? _classBeforeRescue;
    private ISkillProvider? _oldSkillProvider;
    private int _oldSkillTimeout;
    private Skua.Core.Models.Skills.SkillUseMode _oldSkillUseMode;
    private bool _oldResetComboOnTargetChange;
    private PropertyInfo? _resetComboProperty;
    private bool _bossSetupApplied;
    private bool _ownChange, _begun, _finished, _finishing, _ownsCombat;
    private volatile bool _interrupted;
    internal string Room { get; }
    internal string Cell { get; }
    internal string Pad { get; }
    internal bool WasRunning => _wasRunning;
    internal bool WasPaused => _wasPaused;
    internal RescueSession(IScriptPlayer player, IScriptMap map, IScriptCombat combat, IScriptSkill skills,
        IScriptInventory inventory, IScriptWait wait, IScriptMonster monsters, BossRescueLoadout? bossLoadout,
        IScriptOption options, IFlashUtil flash, IScriptManager manager, Func<bool> enabled, Func<bool>? userCancelled = null,
        IScriptSend? send = null)
    {
        _player = player; _map = map; _combat = combat; _skills = skills; _inventory = inventory; _wait = wait; _monsters = monsters; _bossLoadout = bossLoadout; _options = options;
        _flash = flash; _manager = manager; _enabled = enabled; _userCancelled = userCancelled ?? (() => false);
        _account = player.Username ?? ""; _server = ServerName(_flash); _script = manager.LoadedScript ?? "";
        _scriptToken = manager.ScriptCts; _wasRunning = manager.ScriptRunning; _wasPaused = IsPaused(manager);
        _pauseProperty = manager.GetType().GetProperty("ScriptPaused");
        _resetComboProperty = skills.GetType().GetProperty("ResetComboOnTargetChange");
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
    internal bool CanContinue => _begun && !_finished && !_interrupted && !Interrupted && _enabled() && (!_ownsCombat || !_combat.StopAttacking);
    internal bool CanAct => CanContinue && _player.Playing && _flash.IsWorldLoaded;
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
        bool combatClearedForEquipment = await DisengageAsync("before applying the rescue class and gear");
        if (!CanContinue) return false;
        await Task.Delay(250);
        if (!CanContinue) return false;
        _options.InfiniteRange = true;
        _combat.StopAttacking = false;
        if (combatClearedForEquipment)
            await ApplyBossSetupAsync();
        else
            Log.Write(_account, " continuing rescue with the current class and gear because combat could not be cleared safely.");
        _ownsCombat = true;
        return true;
    }

    private string CombatSnapshot()
    {
        string target = _player.Target?.Name ?? "none";
        return $"combat={_player.InCombat}, state={_player.State}, hasTarget={_player.HasTarget}, target={target}, map={_map.FullName}, cell={_player.Cell}";
    }

    private string? FindQuietCell(string currentCell)
    {
        HashSet<string> cellsWithLivingMonsters = _monsters.MapMonsters
            .Where(monster => monster.Alive && !string.IsNullOrWhiteSpace(monster.Cell))
            .Select(monster => monster.Cell)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _map.Cells
            .Where(cell => !string.IsNullOrWhiteSpace(cell) && !cell.Equals(currentCell, StringComparison.OrdinalIgnoreCase))
            .Where(cell => !cellsWithLivingMonsters.Contains(cell))
            .OrderBy(cell => cell.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? 0 :
                cell.Equals("Spawn", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault();
    }

    private async Task<bool> DisengageAsync(string purpose)
    {
        Log.Write(_account, $" disengage {purpose}: {CombatSnapshot()}.");
        try
        {
            _combat.CancelTarget();
            if (_player.InCombat)
            {
                Log.Write(_account, $" requesting Skua combat exit {purpose}.");
                _combat.Exit();
                for (int attempt = 0; attempt < 50 && _player.InCombat && !Interrupted; attempt++)
                    await Task.Delay(200);
                Log.Write(_account, $" after Skua combat exit: {CombatSnapshot()}.");
            }

            if (_player.InCombat && !Interrupted)
            {
                string? quietCell = FindQuietCell(_player.Cell);
                if (quietCell is null)
                {
                    Log.Write(_account, $" no other quiet cell is available {purpose}: {CombatSnapshot()}.");
                    return false;
                }

                Log.Write(_account, $" combat remained active; moving to quiet cell {quietCell} to drop monster aggro.");
                _map.Jump(quietCell, "Spawn");
                for (int attempt = 0; attempt < 50 && _player.InCombat && !Interrupted; attempt++)
                    await Task.Delay(200);
                Log.Write(_account, $" after quiet-cell move: {CombatSnapshot()}.");
            }

            if (Interrupted) return false;
            if (_player.InCombat)
            {
                Log.Write(_account, $" combat did not clear {purpose}: {CombatSnapshot()}.");
                return false;
            }

            Log.Write(_account, $" combat cleared {purpose}: {CombatSnapshot()}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(_account, $" disengage failed {purpose}: {ex.Message}; {CombatSnapshot()}.");
            return false;
        }
    }

    private async Task ApplyBossSetupAsync()
    {
        if (_bossLoadout is not { } setup)
        {
            Log.Write(_account, " no Boss class or dedicated equipment loadout was found; keeping the current setup for rescue.");
            return;
        }
        string className = setup.ClassName.Equals("[Current]", StringComparison.OrdinalIgnoreCase)
            ? _player.CurrentClass?.Name ?? "" : setup.ClassName;
        (string Slot, string Item)[] configuredSlots =
        [
            ("weapon", setup.Weapon), ("armor", setup.Armor), ("helm", setup.Helm),
            ("cape", setup.Cape), ("pet", setup.Pet), ("ground item", setup.GroundItem)
        ];
        string slotSummary = string.Join(", ", configuredSlots.Select(s => $"{s.Slot}={DisplaySelection(s.Item)}"));
        bool hasClass = className.Length > 0 && _inventory.Items.Any(i => i.Category == ItemCategory.Class && i.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        Log.Write(_account, $" Boss rescue selection: class={DisplaySelection(className)}; skill mode={DisplaySelection(setup.SkillMode)}; dedicated gear={(setup.UseEquipment ? "on" : "off")} ({slotSummary}).");
        if (!hasClass && !setup.UseEquipment)
        {
            Log.Write(_account, " Boss class was not available in inventory and dedicated equipment is off; keeping the current setup.");
            return;
        }

        _classBeforeRescue = _player.CurrentClass?.ID;
        _equippedBeforeRescue = _inventory.Items.Where(i => i.Equipped || i.Wearing).Select(i => i.ID)
            .Concat(_classBeforeRescue is int classId ? [classId] : [])
            .Distinct().ToArray();
        _oldSkillProvider = _skills.OverrideProvider;
        _oldSkillTimeout = _skills.SkillTimeout;
        _oldSkillUseMode = _skills.SkillUseMode;
        _oldResetComboOnTargetChange = _resetComboProperty?.GetValue(_skills) is true;

        if (hasClass)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(setup.SkillMode))
                {
                    _skills.LoadAdvanced(className, setup.SkillMode, !setup.ClassName.Equals("[Current]", StringComparison.OrdinalIgnoreCase));
                    _wait.ForItemEquip(className, 20);
                    await Task.Delay(1000);
                }
                else if (!setup.ClassName.Equals("[Current]", StringComparison.OrdinalIgnoreCase))
                {
                    InventoryItem? classItem = _inventory.Items.FirstOrDefault(i => i.Category == ItemCategory.Class &&
                        i.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
                    if (classItem is not null && !await EquipItemSlowlyAsync(classItem, "Boss class"))
                        Log.Write(_account, $" couldn't confirm equipping Boss class '{className}'.");
                }
            }
            catch (Exception ex) { Log.Write(_account, $" couldn't apply Boss class/skill mode: {ex.Message}"); }
        }

        if (setup.UseEquipment)
        {
            foreach ((string slot, string itemName) in configuredSlots)
                await EquipConfiguredAsync(slot, itemName);
        }
        _bossSetupApplied = true;
    }

    private static string DisplaySelection(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value;

    private async Task EquipConfiguredAsync(string slot, string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return;
        InventoryItem? item = _inventory.Items.FirstOrDefault(i => i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            Log.Write(_account, $" skipped Boss {slot} '{itemName}' because it is not in inventory.");
            return;
        }
        if (item.Equipped || item.Wearing) return;
        if (!await EquipItemSlowlyAsync(item, $"Boss {slot}"))
            Log.Write(_account, $" couldn't confirm equipping Boss {slot} '{itemName}'.");
    }

    private async Task<bool> EquipItemSlowlyAsync(InventoryItem item, string purpose)
    {
        for (int attempt = 0; attempt < 3 && _player.Playing && _flash.IsWorldLoaded; attempt++)
        {
            InventoryItem? current = _inventory.Items.FirstOrDefault(i => i.ID == item.ID);
            if (current is { Equipped: true } or { Wearing: true }) return true;

            // Skua's EquipItem waits for the server action lock but sends even if that wait times out.
            // Gate the request here and leave extra room between successive equipment changes.
            if (!_wait.ForActionCooldown(GameActions.EquipItem, 100))
            {
                Log.Write(_account, $" skipped {purpose} '{item.Name}' because the equip action cooldown did not clear.");
                return false;
            }
            await Task.Delay(1000);
            if (!_player.Playing || !_flash.IsWorldLoaded) return false;
            try
            {
                _inventory.EquipItem(item.ID);
                if (_wait.ForItemEquip(item.ID, 30))
                {
                    await Task.Delay(1000);
                    return true;
                }
            }
            catch (Exception ex) { Log.Write(_account, $" equip attempt for {purpose} '{item.Name}' failed: {ex.Message}"); }

            if (attempt < 2) await Task.Delay(1500);
        }
        return false;
    }

    private async Task RestoreBossSetupAsync()
    {
        if (!_bossSetupApplied) return;
        int[] restoreOrder = _classBeforeRescue is int classId
            ? [classId, .. _equippedBeforeRescue.Where(id => id != classId)]
            : _equippedBeforeRescue;
        List<string> confirmed = [];
        List<string> failed = [];
        foreach (int id in restoreOrder)
        {
            InventoryItem? item = _inventory.Items.FirstOrDefault(i => i.ID == id);
            string label = id == _classBeforeRescue ? $"class: {item?.Name ?? id.ToString()}" : item?.Name ?? $"item {id}";
            if (item is null) { failed.Add(label); continue; }
            if (item.Equipped || item.Wearing) { confirmed.Add(label); continue; }
            if (await EquipItemSlowlyAsync(item, "original gear")) confirmed.Add(label);
            else failed.Add(label);
        }
        _skills.OverrideProvider = _oldSkillProvider;
        _skills.SkillTimeout = _oldSkillTimeout;
        _skills.SkillUseMode = _oldSkillUseMode;
        try { _resetComboProperty?.SetValue(_skills, _oldResetComboOnTargetChange); } catch { }
        Log.Write(_account, confirmed.Count == 0
            ? " no original class or gear items were confirmed restored after rescue."
            : $" original setup confirmed: {string.Join(", ", confirmed)}.");
        if (failed.Count > 0)
            Log.Write(_account, $" could not confirm restoring: {string.Join(", ", failed)}; equip these manually if needed.");
        _bossSetupApplied = false;
    }
    internal void Fight()
    {
        if (!CanAct) return;
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

            bool readyToRestore = _player.Playing && _flash.IsWorldLoaded;
            if (!readyToRestore)
            {
                Log.Write(_account, _bossSetupApplied
                    ? " rescue ended while dead; waiting up to 2 minutes for respawn before restoring the saved class and gear."
                    : " rescue ended while dead; waiting up to 2 minutes for respawn before finishing cleanup.");
                long deadline = Environment.TickCount64 + 120_000;
                while (!readyToRestore && Environment.TickCount64 < deadline)
                {
                    bool currentRun = ReferenceEquals(_scriptToken, _manager.ScriptCts) &&
                        string.Equals(_script, _manager.LoadedScript, StringComparison.OrdinalIgnoreCase);
                    bool otherScriptOwnsState = (!currentRun && _manager.ScriptRunning) ||
                        (Interrupted && _manager.ScriptRunning && !IsPaused(_manager));
                    if (!_player.LoggedIn || !InOriginalSession || otherScriptOwnsState) break;
                    await Task.Delay(500);
                    readyToRestore = _player.Playing && _flash.IsWorldLoaded;
                }
                bool currentRunAfterWait = ReferenceEquals(_scriptToken, _manager.ScriptCts) &&
                    string.Equals(_script, _manager.LoadedScript, StringComparison.OrdinalIgnoreCase);
                bool otherScriptOwnsStateAfterWait = (!currentRunAfterWait && _manager.ScriptRunning) ||
                    (Interrupted && _manager.ScriptRunning && !IsPaused(_manager));
                if (!_player.LoggedIn || !InOriginalSession || otherScriptOwnsStateAfterWait)
                {
                    Log.Write(_account, " skipped rescue restoration because the account logged out, changed session, or another script took control.");
                    return;
                }
                if (!readyToRestore && _bossSetupApplied)
                    Log.Write(_account, " could not restore the saved class and gear because the account did not respawn in time or its session changed; equip them manually if needed.");
                else if (!readyToRestore)
                    Log.Write(_account, " could not finish rescue cleanup because the account did not respawn in time or its session changed.");
                else
                    Log.Write(_account, " respawned; clearing combat before restoring the saved class and gear.");
            }

            _combat.CancelAutoAttack();
            if (readyToRestore)
            {
                bool combatClearedForRestore = await DisengageAsync("before restoring the saved class and gear");
                if (combatClearedForRestore)
                    await RestoreBossSetupAsync();
                else if (_bossSetupApplied)
                    Log.Write(_account, Interrupted
                        ? " skipped restoring the saved class and gear because rescue was interrupted; equip the saved setup manually if needed."
                        : " could not safely restore the saved class and gear because combat remained active; equip the saved setup manually if needed.");
            }
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
                if (_wasRunning && !_wasPaused && !Interrupted && _manager.ScriptRunning && IsPaused(_manager)
                    && _player.Playing && _flash.IsWorldLoaded)
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
