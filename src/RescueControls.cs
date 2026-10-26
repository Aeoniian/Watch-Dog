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
                    bool wanted = ItemOption(item)?.Name == "rescueAlliesShared" ? enabled : allowed;
                    if (ItemValue(item) is bool current && current == wanted) continue;
                    // Avalonia uses BoolValue; older hosts bind directly to Value.
                    PropertyInfo? property = item.GetType().GetProperty("BoolValue") ?? item.GetType().GetProperty("Value");
                    if (property?.CanWrite == true) property.SetValue(item, wanted);
                }
            }
            finally { _updating = false; }
        }
    }
    internal void Show()
    {
        if (_dialogs is null || _disposed || _dialog is not null) return;
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
            foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged += OnEdited;
            typeof(IDialogService).GetMethods().Single(m => m.Name == "ShowDialog" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string))
                .MakeGenericMethod(vmType).Invoke(_dialogs, [_dialog, "Rescue Allies - changes apply immediately"]);
        }
        catch (Exception ex) { _dialogs.ShowMessageBox(ex.Message, "Rescue Allies"); }
        finally
        {
            if (_dialog is not null)
                foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged -= OnEdited;
            _dialog = null;
        }
    }
    private IEnumerable<object> DialogItems() => _dialog?.GetType().GetProperty("Options")?.GetValue(_dialog) is IEnumerable items ? items.Cast<object>() : [];
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
        bool value = ItemValue(item) is bool b && b;
        if (ItemOption(item)?.Name == "rescueAlliesShared") _store.SetEnabled(value);
        else if (_account() is { Length: > 0 } account) _store.SetAllowed(account, value);

    }
    public void Dispose()
    {
        _disposed = true; _timer.Dispose(); _store.Changed -= QueueRefresh;
        if (_dialog is not null) foreach (var item in DialogItems()) ((INotifyPropertyChanged)item).PropertyChanged -= OnEdited;
    }
}
