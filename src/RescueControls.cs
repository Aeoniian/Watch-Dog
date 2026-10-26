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
    private readonly IDialogService? _dialogs;
    private readonly IDispatcherService? _dispatcher;
    private readonly SharedRescueSettings _store = SharedRescueSettings.Instance;
    private readonly Timer _timer;
    private bool _updating, _disposed;
    private int _queued;
    private object? _dialog;
    private object? _settingsDialog, _window;
    private int _seenDelay = -1, _seenDeaths = -1;
    internal RescueControls(Func<IOptionContainer?> options, Func<string> account, IDialogService? dialogs, IDispatcherService? dispatcher)
    {
        _options = options; _account = account; _dialogs = dialogs; _dispatcher = dispatcher;
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
        bool enabled = _store.Enabled, allowed = _store.IsAllowed(_account());
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
                        name == "rescueDeaths" && _seenDeaths == _store.DeathsBeforeRescue) continue;
                    object wanted = name switch {
                        "rescueAlliesShared" => enabled,
                        "allowRescueThisAccount" => allowed,
                        "rescueDelayMinutes" => _store.DelayMinutes.ToString(),
                        "rescueDeaths" => _store.DeathsBeforeRescue.ToString(),
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
            container.Options.Add(Opt.SharedBool("allowRescueThisAccount", "Allow this account to rescue",
                "Saved for this logged-in account. Excluding it only prevents helping; it can still receive help. Idle accounts can rescue too.", true));
            container.SetDefaults();
            foreach (IOption option in container.Options)
                container.OptionValues[option] = (option.Name == "rescueAlliesShared" ? _store.Enabled : _store.IsAllowed(_account())).ToString();
            Type? vmType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types)
                .FirstOrDefault(t => t.Name == "OptionContainerViewModel" && t.GetConstructor([typeof(IOptionContainer)]) is not null);
            if (vmType is null) throw new InvalidOperationException("This client does not expose its native option controls.");
            _dialog = Activator.CreateInstance(vmType, container)!;
            var timing = Activator.CreateInstance(current.GetType(), _dialogs) as IOptionContainer
                ?? throw new InvalidOperationException("This client cannot create rescue settings.");
            timing.Options.Add(Opt.SharedInt("rescueDelayMinutes", "Rescue delay (minutes, 1–120)",
                "Request help after this long without a kill, starting at the first death (at least two deaths required). Either this timer OR the death count triggers rescue. Changes apply immediately on this PC.", 15));
            timing.Options.Add(Opt.SharedInt("rescueDeaths", "Deaths before requesting rescue (1–100)",
                "Consecutive deaths without a kill. A kill resets both thresholds. Defaults: 15 minutes or 10 deaths. Whole numbers only; invalid input is not saved. Existing rescue calls continue until a kill or a normal stop condition.", 10));
            timing.SetDefaults();
            foreach (IOption option in timing.Options)
                timing.OptionValues[option] = (option.Name == "rescueDelayMinutes" ? _store.DelayMinutes : _store.DeathsBeforeRescue).ToString();
            _settingsDialog = Activator.CreateInstance(vmType, timing)!;
            _seenDelay = _store.DelayMinutes; _seenDeaths = _store.DeathsBeforeRescue;
            foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged += OnEdited;
            try { _window = RescueTabbedWindow.Show(_dialog, _settingsDialog, CloseWindow); return; }
            catch (InvalidOperationException) { } // Headless/older hosts retain their native option dialog fallback.
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_dialog, "Rescue Allies - changes apply immediately"]);
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_settingsDialog, "Rescue Settings - changes apply immediately"]);
        }
        catch (Exception ex) { _dialogs.ShowMessageBox(ex.Message, "Rescue Allies"); }
        finally
        {
            if (_window is null) CloseWindow();
        }
    }
    private void CloseWindow()
    {
        foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged -= OnEdited;
        _dialog = null; _settingsDialog = null; _window = null;
    }
    private IEnumerable<object> DialogItems() => new[] { _dialog, _settingsDialog }.Where(m => m is not null)
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
            else if (name == "allowRescueThisAccount" && _account() is { Length: > 0 } account) _store.SetAllowed(account, value);
            else if (int.TryParse(ItemValue(item)?.ToString(), out int number))
            {
                if (name == "rescueDelayMinutes" && number is >= 1 and <= 120) _store.SetDelayMinutes(number);
                else if (name == "rescueDeaths" && number is >= 1 and <= 100) _store.SetDeathsBeforeRescue(number);
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
