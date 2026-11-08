using System.ComponentModel;
using System.Reflection;
using Skua.Core.Interfaces;
using System.Collections;
using WatchDog.Shared;

namespace WatchDog;

// Bridges stock plugin options and the shared store without changing a host client's binaries.
internal sealed class RescueControls : IDisposable
{
    private readonly Func<IOptionContainer?> _options;
    private readonly Func<string> _account;
    private readonly Func<string> _requestHelp;
    private readonly IDialogService? _dialogs;
    private readonly IDispatcherService? _dispatcher;
    private readonly SharedRescueSettings _store = SharedRescueSettings.Instance;
    private readonly Timer _timer;
    private bool _updating, _disposed;
    private int _queued;
    private object? _dialog;
    private object? _settingsDialog, _helperSettingsDialog, _window;
    private int _seenDelay = -1, _seenDeaths = -1, _seenHelperDeaths = -1, _seenHold = -1;
    private bool _seenBoss = true;
    internal RescueControls(Func<IOptionContainer?> options, Func<string> account, Func<string> requestHelp,
        IDialogService? dialogs, IDispatcherService? dispatcher)
    {
        _options = options; _account = account; _requestHelp = requestHelp; _dialogs = dialogs; _dispatcher = dispatcher;
        _store.Changed += QueueRefresh;
        _timer = new Timer(_ => QueueRefresh(), null, 200, 200);
    }
    private void QueueRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) != 0) return;
        void Run()
        {
            try { if (!_disposed) Refresh(); } catch { }
            finally { Volatile.Write(ref _queued, 0); }
        }
        try { if (_dispatcher is not null) _dispatcher.Invoke(Run); else Run(); }
        catch { Volatile.Write(ref _queued, 0); }
    }
    private void Refresh()
    {
        bool enabled = _store.Enabled;
        if (_dialog is not null)
        {
            _updating = true;
            try
            {
                foreach (var item in DialogItems())
                {
                    string? name = ItemOption(item)?.Name;
                    // Do not overwrite a partially typed number on the periodic account refresh.
                    if (name == "rescueDelayMinutes" && _seenDelay == _store.DelayMinutes ||
                        name == "rescueDeaths" && _seenDeaths == _store.DeathsBeforeRescue ||
                        name == "helperDeathsWithoutKill" && _seenHelperDeaths == _store.HelperDeathsWithoutKill ||
                        name == "holdLimitMinutes" && _seenHold == _store.HoldLimitMinutes ||
                        name == "useBossLoadout" && _seenBoss == _store.UseBossLoadout) continue;
                    object wanted = name switch {
                        "rescueAlliesShared" => enabled,
                        "rescueDelayMinutes" => _store.DelayMinutes.ToString(),
                        "rescueDeaths" => _store.DeathsBeforeRescue.ToString(),
                        "helperDeathsWithoutKill" => _store.HelperDeathsWithoutKill.ToString(),
                        "holdLimitMinutes" => _store.HoldLimitMinutes.ToString(),
                        "useBossLoadout" => _store.UseBossLoadout,
                        _ => ItemValue(item) ?? ""
                    };
                    if (Equals(ItemValue(item), wanted) || ItemValue(item)?.ToString() == wanted.ToString()) continue;
                    // Avalonia uses BoolValue; older hosts bind directly to Value.
                    PropertyInfo? property = item.GetType().GetProperty(wanted is bool ? "BoolValue" : "TextValue") ?? item.GetType().GetProperty("Value");
                    if (property?.CanWrite == true) property.SetValue(item, wanted);
                }
            }
            finally { _updating = false; }
            _seenDelay = _store.DelayMinutes; _seenDeaths = _store.DeathsBeforeRescue;
            _seenHelperDeaths = _store.HelperDeathsWithoutKill; _seenHold = _store.HoldLimitMinutes; _seenBoss = _store.UseBossLoadout;
        }
    }
    internal void Show()
    {
        if (_dialogs is null || _disposed) return;
        if (_window is not null) { _window.GetType().GetMethod("Activate", Type.EmptyTypes)?.Invoke(_window, null); return; }
        if (_dialog is not null) return;
        try
        {
            Refresh();
            var current = _options();
            if (current is null) return;
            var container = Activator.CreateInstance(current.GetType(), _dialogs) as IOptionContainer;
            if (container is null) throw new InvalidOperationException("This client cannot create rescue controls.");
            container.Options.Add(Opt.SharedBool("rescueAlliesShared", "Rescue Allies - all accounts",
                "Changes apply immediately across updated Watch Dog and VibeSkuffle clients on this PC.", false));
            container.SetDefaults();
            foreach (IOption option in container.Options)
                container.OptionValues[option] = _store.Enabled.ToString();
            Type? vmType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types)
                .FirstOrDefault(t => t.Name == "OptionContainerViewModel" && t.GetConstructor([typeof(IOptionContainer)]) is not null);
            if (vmType is null) throw new InvalidOperationException("This client does not expose its native option controls.");
            _dialog = Activator.CreateInstance(vmType, container)!;
            IOptionContainer CreateOptions() => Activator.CreateInstance(current.GetType(), _dialogs) as IOptionContainer
                ?? throw new InvalidOperationException("This client cannot create rescue settings.");
            var timing = CreateOptions();
            timing.Options.Add(Opt.SharedInt("rescueDelayMinutes", "Rescue delay (minutes, 1–120)",
                "Request help after this long without a kill, starting at the first death (at least two deaths required). Either this timer OR the death count triggers rescue. Changes apply immediately on this PC.", 15));
            timing.Options.Add(Opt.SharedInt("rescueDeaths", "Deaths before requesting rescue (1–100)",
                "Consecutive deaths without a kill. A kill resets the requester's streak. Defaults: 15 minutes or 10 deaths. Whole numbers only; invalid input is not saved.", 10));
            timing.SetDefaults();
            foreach (IOption option in timing.Options)
            {
                object optionValue = option.Name switch
                {
                    "rescueDelayMinutes" => (object)_store.DelayMinutes,
                    "rescueDeaths" => _store.DeathsBeforeRescue,
                    _ => _store.Enabled
                };
                timing.OptionValues[option] = optionValue.ToString() ?? "";
            }
            _settingsDialog = Activator.CreateInstance(vmType, timing)!;
            var helperSettings = CreateOptions();
            helperSettings.Options.Add(Opt.SharedInt("helperDeathsWithoutKill", "Helper deaths without a kill (1–100)",
                "Stop helping after this many consecutive deaths without a kill. Default: 5. Changes apply to active rescues.", 5));
            helperSettings.Options.Add(Opt.SharedInt("holdLimitMinutes", "Release helpers after (minutes, 0 = no limit)",
                "Helpers normally stay until the requester leaves the map. This releases them after this many minutes without a kill or death. Default: 30.", 30));
            helperSettings.Options.Add(Opt.SharedBool("useBossLoadout", "Helpers use their CoreBots Boss class and gear",
                "On (default): helpers switch to the Boss class, skill mode and gear of their CoreBots options and restore their own setup afterwards. Off: they help with whatever they have equipped.", true));
            helperSettings.SetDefaults();
            foreach (IOption option in helperSettings.Options)
                helperSettings.OptionValues[option] = option.Name switch
                {
                    "holdLimitMinutes" => _store.HoldLimitMinutes.ToString(),
                    "useBossLoadout" => _store.UseBossLoadout.ToString(),
                    _ => _store.HelperDeathsWithoutKill.ToString()
                };
            _helperSettingsDialog = Activator.CreateInstance(vmType, helperSettings)!;
            _seenDelay = _store.DelayMinutes; _seenDeaths = _store.DeathsBeforeRescue;
            _seenHelperDeaths = _store.HelperDeathsWithoutKill; _seenHold = _store.HoldLimitMinutes; _seenBoss = _store.UseBossLoadout;
            foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged += OnEdited;
            try { _window = RescueTabbedWindow.Show(_dialog, _settingsDialog, _helperSettingsDialog,
                () => RescueAccountHub.ReadAccounts(),
                _account,
                account => account.Equals(_account(), StringComparison.OrdinalIgnoreCase)
                    ? _requestHelp() : RescueAccountHub.RequestManualHelp(account),
                () => Log.ReadRecent(), Log.Start, Log.Stop, Log.Clear, CloseWindow); return; }
            catch (InvalidOperationException) { } // Headless/older hosts retain their native option dialog fallback.
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_dialog, "Rescue Allies — " + RescueStatus.Describe(RescueStatus.Latest, DateTime.UtcNow)]);
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_settingsDialog, "Rescue Settings - changes apply immediately"]);
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_helperSettingsDialog, "Helper Rules - changes apply immediately"]);
        }
        catch (Exception ex)
        {
            // Preserve the reflection failure details; a message-only null reference is not actionable.
            Log.Write("?", " Rescue Allies window failed: " + ex);
            _dialogs.ShowMessageBox(ex.ToString(), "Rescue Allies");
        }
        finally
        {
            if (_window is null) CloseWindow();
        }
    }
    private void CloseWindow()
    {
        foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged -= OnEdited;
        _dialog = null; _settingsDialog = null; _helperSettingsDialog = null; _window = null;
    }
    private IEnumerable<object> DialogItems() => new[] { _dialog, _settingsDialog, _helperSettingsDialog }.Where(m => m is not null)
        .SelectMany(m => m!.GetType().GetProperty("Options")?.GetValue(m) is IEnumerable items ? items.Cast<object>() : []);
    private static IOption? ItemOption(object item) => item.GetType().GetProperty("Option")?.GetValue(item) as IOption;
    private static object? ItemValue(object item) => item.GetType().GetProperty("Value")?.GetValue(item);
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        catch { return []; }
    }
    private void OnEdited(object? sender, PropertyChangedEventArgs args)
    {
        if (_updating || _disposed || args.PropertyName is not ("Value" or "BoolValue") || sender is not INotifyPropertyChanged item) return;
        try
        {
            string? name = ItemOption(item)?.Name;
            bool value = ItemValue(item) is bool b && b;
            if (name == "rescueAlliesShared") _store.SetEnabled(value);
            else if (name == "useBossLoadout") _store.SetUseBossLoadout(value);
            else if (int.TryParse(ItemValue(item)?.ToString(), out int number))
            {
                if (name == "rescueDelayMinutes" && number is >= 1 and <= 120) _store.SetDelayMinutes(number);
                else if (name == "rescueDeaths" && number is >= 1 and <= 100) _store.SetDeathsBeforeRescue(number);
                else if (name == "helperDeathsWithoutKill" && number is >= 1 and <= 100) _store.SetHelperDeathsWithoutKill(number);
                else if (name == "holdLimitMinutes" && number is >= 0 and <= 240) _store.SetHoldLimitMinutes(number);
            }
        }
        catch (Exception ex) { _dialogs?.ShowMessageBox(ex.Message, "Rescue Settings"); }
    }
    public void Dispose()
    {
        _disposed = true; _timer.Dispose(); _store.Changed -= QueueRefresh;
        _window?.GetType().GetMethod("Close", Type.EmptyTypes)?.Invoke(_window, null);
        if (_dialog is not null) foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged -= OnEdited;
    }
}
