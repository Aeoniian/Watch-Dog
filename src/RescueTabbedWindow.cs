using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using WatchDog.Shared;

namespace WatchDog;

// Uses the host UI framework and its own option controls; no WPF/Avalonia dependencies are shipped.
internal static class RescueTabbedWindow
{
    internal static object Show(object allies, object settings, object helperSettings,
        Func<IReadOnlyList<RescueAccountHub.AccountSnapshot>> accounts,
        Func<string> currentAccount, Func<string, string> requestHelpFor, Func<string> readLog,
        Action startLog, Action stopLog, Action clearLog, Action closed)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        Type? Find(string name) => assemblies.Select(a => a.GetType(name)).FirstOrDefault(t => t is not null);
        bool avalonia = Find("Avalonia.Application")?.GetProperty("Current")?.GetValue(null) is not null;
        string prefix = avalonia ? "Avalonia.Controls." : "System.Windows.Controls.";
        Type windowType = Find(avalonia ? "Avalonia.Controls.Window" : "System.Windows.Window")
            ?? throw new InvalidOperationException("The host window framework is unavailable.");
        Type viewType = Find(avalonia ? "Skua.Shared.Avalonia.Controls.Options.OptionContainerUserControl" : "Skua.WPF.UserControls.OptionContainerUserControl")
            ?? throw new InvalidOperationException("The host option view is unavailable.");
        object New(string name) => Activator.CreateInstance(Find(prefix + name) ?? throw new InvalidOperationException("Missing host control: " + name))!;
        static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        static void SetMargin(object target, double left, double top, double right, double bottom)
        {
            PropertyInfo? property = target.GetType().GetProperty("Margin");
            if (property is null) return;
            object? margin = Activator.CreateInstance(property.PropertyType, [left, top, right, bottom]);
            if (margin is not null) property.SetValue(target, margin);
        }
        object window = Activator.CreateInstance(windowType)!;
        Set(window, "Title", "Watch Dog — Rescue Allies");
        Set(window, "Width", 760d); Set(window, "Height", 560d);
        Set(window, "MinWidth", 500d); Set(window, "MinHeight", 350d);
        object tabs = New("TabControl");
        string selectedAccount = "";
        void AddTab(string title, object content)
        {
            object tab = New("TabItem");
            Set(tab, "Header", title);
            object items = tabs.GetType().GetProperty("Items")!.GetValue(tabs)!;
            if (items is IList list) list.Add(tab);
            else items.GetType().GetMethod("Add", [typeof(object)])!.Invoke(items, [tab]);
            Set(tab, "Content", content);
        }
        void AddChild(object panel, object child)
        {
            object children = panel.GetType().GetProperty("Children")!.GetValue(panel)!;
            if (children is IList list) list.Add(child);
            else children.GetType().GetMethod("Add", [typeof(object)])!.Invoke(children, [child]);
        }
        void SetEnum(object target, string propertyName, string value)
        {
            var property = target.GetType().GetProperty(propertyName);
            if (property is not null)
            {
                property.SetValue(target, Enum.Parse(property.PropertyType, value));
                return;
            }

            // Avalonia exposes TextBox scrollbar visibility as ScrollViewer attached properties,
            // while WPF exposes them directly on TextBox.
            if (avalonia && propertyName is "VerticalScrollBarVisibility" or "HorizontalScrollBarVisibility")
            {
                Type scrollViewer = Find("Avalonia.Controls.ScrollViewer")
                    ?? throw new MissingMemberException("Avalonia ScrollViewer type is unavailable.");
                string setterName = "Set" + propertyName;
                MethodInfo setter = scrollViewer.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == setterName && m.GetParameters().Length == 2)
                    ?? throw new MissingMemberException("Avalonia ScrollViewer." + setterName + " is unavailable.");
                setter.Invoke(null, [target, Enum.Parse(setter.GetParameters()[1].ParameterType, value)]);
                return;
            }

            throw new MissingMemberException(target.GetType().FullName, propertyName);
        }

        object Text(string value, double top = 0, double bottom = 0, bool heading = false)
        {
            object label = New("TextBlock");
            Set(label, "Text", value);
            SetEnum(label, "TextWrapping", "Wrap");
            SetMargin(label, 0, top, 0, bottom);
            if (heading) Set(label, "FontSize", 17d);
            return label;
        }

        object Page(string heading, string description)
        {
            object panel = New("StackPanel");
            SetMargin(panel, 16, 14, 16, 14);
            AddChild(panel, Text(heading, bottom: 4, heading: true));
            AddChild(panel, Text(description, bottom: 14));
            return panel;
        }

        void AddScrollableTab(string title, object content)
        {
            object scroll = New("ScrollViewer");
            SetEnum(scroll, "VerticalScrollBarVisibility", "Auto");
            SetEnum(scroll, "HorizontalScrollBarVisibility", "Disabled");
            Set(scroll, "Content", content);
            AddTab(title, scroll);
        }

        bool HasFocus(object control) => control.GetType()
            .GetProperty(avalonia ? "IsFocused" : "IsKeyboardFocused")?.GetValue(control) is true;

        void ShowControl(object control, bool visible)
        {
            if (avalonia) Set(control, "IsVisible", visible);
            else SetEnum(control, "Visibility", visible ? "Visible" : "Collapsed");
        }

        void SetTextIfChanged(object control, string value)
        {
            if (!Equals(control.GetType().GetProperty("Text")?.GetValue(control), value))
                Set(control, "Text", value);
        }
        var setDock = Find(prefix + "DockPanel")!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "SetDock" && m.GetParameters().Length == 2);
        void Dock(object control, string side) =>
            setDock.Invoke(null, [control, Enum.Parse(setDock.GetParameters()[1].ParameterType, side)]);

        static void BindEvent(object control, string eventName, Action action)
        {
            var controlEvent = control.GetType().GetEvent(eventName)!;
            var invoke = controlEvent.EventHandlerType!.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var body = Expression.Invoke(Expression.Constant(action));
            controlEvent.AddEventHandler(control, Expression.Lambda(controlEvent.EventHandlerType, body, parameters).Compile());
        }
        static void BindClick(object button, Action action) => BindEvent(button, "Click", action);

        static void BindTextChanged(object control, Action<string> action)
        {
            var changed = control.GetType().GetEvent("TextChanged")!;
            var invoke = changed.EventHandlerType!.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var text = Expression.Property(Expression.Constant(control), "Text");
            var body = Expression.Invoke(Expression.Constant(action), text);
            changed.AddEventHandler(control, Expression.Lambda(changed.EventHandlerType, body, parameters).Compile());
        }

        void BindSelection(object control, Action<string?> action)
        {
            var changed = control.GetType().GetEvent("SelectionChanged")!;
            var invoke = changed.EventHandlerType!.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var selected = Expression.Property(Expression.Constant(control), "SelectedItem");
            var body = Expression.Invoke(Expression.Constant(action), Expression.Convert(selected, typeof(string)));
            changed.AddEventHandler(control, Expression.Lambda(changed.EventHandlerType, body, parameters).Compile());
        }

        object accountsPanel = Page("Active accounts", "Choose an account to see its rescue status, request help for it, or let it help others.");
        object accountRow = New("StackPanel"); SetEnum(accountRow, "Orientation", "Horizontal");
        object accountPicker = New("ComboBox"); Set(accountPicker, "MinWidth", 200d);
        Set(accountPicker, "MaxWidth", 260d);
        object accountRequest = New("Button"); Set(accountRequest, "Content", "Request Help");
        SetMargin(accountPicker, 0, 0, 10, 0);
        AddChild(accountRow, accountPicker); AddChild(accountRow, accountRequest);
        object accountAllowed = New("CheckBox"); Set(accountAllowed, "Content", "Allow this account to help others");
        SetMargin(accountAllowed, 0, 10, 0, 0);
        object accountFeedback = Text("", top: 6);
        ShowControl(accountFeedback, false);
        object accountLocation = Text("", bottom: 6);
        object accountDetails = Text("");
        AddChild(accountsPanel, accountRow);
        AddChild(accountsPanel, accountAllowed);
        AddChild(accountsPanel, accountFeedback);
        AddChild(accountsPanel, Text("Live rescue status", top: 18, bottom: 6, heading: true));
        AddChild(accountsPanel, accountLocation);
        AddChild(accountsPanel, accountDetails);

        IReadOnlyList<RescueAccountHub.AccountSnapshot> currentAccounts = [];
        bool refreshingPicker = false;
        void ShowAccountFeedback(string message)
        {
            Set(accountFeedback, "Text", message);
            ShowControl(accountFeedback, message.Length > 0);
        }

        BindSelection(accountPicker, account =>
        {
            if (refreshingPicker) return;
            selectedAccount = account ?? "";
            ShowAccountFeedback("");
            RefreshAccountDetails();
        });
        BindClick(accountRequest, () => ShowAccountFeedback(requestHelpFor(selectedAccount)));
        BindClick(accountAllowed, () =>
        {
            if (selectedAccount.Length == 0) return;
            bool allowed = accountAllowed.GetType().GetProperty("IsChecked")?.GetValue(accountAllowed) is true;
            SharedRescueSettings.Instance.SetAllowed(selectedAccount, allowed);
            ShowAccountFeedback(allowed
                ? $"{selectedAccount} is now allowed to rescue other accounts."
                : $"{selectedAccount} is now excluded from rescuing other accounts.");
        });
        AddScrollableTab("Accounts", accountsPanel);

        void RefreshAccountDetails()
        {
            RescueAccountHub.AccountSnapshot? selected = currentAccounts.FirstOrDefault(a => a.Account.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase));
            ShowControl(accountLocation, selected is not null);
            if (selected is not null)
                SetTextIfChanged(accountLocation, $"Server: {selected.Server}   Map: {selected.Map}   Cell: {selected.Cell}");
            string details = selected is not null ? RescueStatus.Describe(selected.Status, DateTime.UtcNow)
                : currentAccounts.Count == 0 ? "No active accounts found. Sign in with Watch Dog loaded to see an account here."
                : "Select an account to see its status.";
            SetTextIfChanged(accountDetails, details);
            Set(accountRequest, "IsEnabled", selected is not null);
            Set(accountAllowed, "IsEnabled", selected is not null);
            Set(accountAllowed, "IsChecked", selected is not null && SharedRescueSettings.Instance.IsAllowed(selected.Account));
        }

        void RefreshAccountList()
        {
            var list = accountPicker.GetType().GetProperty("Items")?.GetValue(accountPicker);
            if (list is null) return;
            currentAccounts = accounts();
            string[] names = currentAccounts.Select(a => a.Account).ToArray();
            IEnumerable<object> existing = list is IEnumerable enumerable ? enumerable.Cast<object>() : [];
            string[] shown = existing.Select(x => x.ToString() ?? "").ToArray();
            if (!names.SequenceEqual(shown, StringComparer.OrdinalIgnoreCase))
            {
                string nextAccount = names.FirstOrDefault(name => name.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault() ?? "";
                if (!nextAccount.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase)) ShowAccountFeedback("");
                refreshingPicker = true;
                try
                {
                    if (list is IList items) { items.Clear(); foreach (string name in names) items.Add(name); }
                    else
                    {
                        list.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(list, null);
                        var add = list.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                        foreach (string name in names) add?.Invoke(list, [name]);
                    }
                    selectedAccount = nextAccount;
                    Set(accountPicker, "SelectedItem", selectedAccount.Length == 0 ? null! : selectedAccount);
                }
                finally { refreshingPicker = false; }
            }
            RefreshAccountDetails();
        }

        selectedAccount = currentAccount();
        RefreshAccountList();

        object alliesView = Activator.CreateInstance(viewType)!;
        Set(alliesView, "DataContext", allies);

        object settingsView = Activator.CreateInstance(viewType)!;
        Set(settingsView, "DataContext", settings);
        object helperSettingsView = Activator.CreateInstance(viewType)!;
        Set(helperSettingsView, "DataContext", helperSettings);
        object settingsPanel = Page("Rescue settings", "Valid changes apply immediately.");
        AddChild(settingsPanel, Text("Rescue for all accounts", bottom: 4, heading: true));
        AddChild(settingsPanel, alliesView);
        AddChild(settingsPanel, Text("When to request help", top: 14, bottom: 4, heading: true));
        AddChild(settingsPanel, Text("Help starts when either threshold is reached.", bottom: 8));
        object settingsLayout = New("DockPanel");
        object thresholds = New("StackPanel");
        AddChild(thresholds, settingsView);
        AddChild(settingsLayout, thresholds);
        AddChild(settingsPanel, settingsLayout);

        AddChild(settingsPanel, Text("Helper behavior", top: 14, bottom: 4, heading: true));
        AddChild(settingsPanel, Text("Set the helper's death limit and repeat-help wait.", bottom: 8));
        object helperLayout = New("DockPanel");
        object helperDeathsPanel = New("StackPanel");
        AddChild(helperDeathsPanel, helperSettingsView);
        AddChild(helperLayout, helperDeathsPanel);
        object cooldownPanel = New("StackPanel");
        object cooldownToggle = New("CheckBox"); Set(cooldownToggle, "Content", "Enable helper cooldown");
        AddChild(cooldownPanel, cooldownToggle);
        object cooldownRow = New("StackPanel"); SetEnum(cooldownRow, "Orientation", "Horizontal");
        SetMargin(cooldownRow, 0, 8, 0, 0);
        object cooldownText = New("TextBox"); Set(cooldownText, "Width", 68d); Set(cooldownText, "MinHeight", 28d);
        SetEnum(cooldownText, "VerticalContentAlignment", "Center");
        SetEnum(cooldownText, "TextAlignment", "Center");
        SetMargin(cooldownText, 8, 0, 8, 0);
        object cooldownLabel = Text("Wait");
        SetEnum(cooldownLabel, "VerticalAlignment", "Center");
        object cooldownUnits = Text("minutes (1–30)");
        SetEnum(cooldownUnits, "VerticalAlignment", "Center");
        AddChild(cooldownRow, cooldownLabel);
        AddChild(cooldownRow, cooldownText);
        AddChild(cooldownRow, cooldownUnits);
        AddChild(cooldownPanel, cooldownRow);
        AddChild(helperLayout, cooldownPanel);
        AddChild(settingsPanel, helperLayout);

        void ArrangeSettings()
        {
            object? size = helperLayout.GetType().GetProperty(avalonia ? "Bounds" : "RenderSize")?.GetValue(helperLayout);
            double width = size?.GetType().GetProperty("Width")?.GetValue(size) is double measured ? measured : 0;
            const double cooldownWidth = 240;
            const double gap = 18;
            bool sideBySide = width >= 310 + cooldownWidth + gap;
            Dock(helperDeathsPanel, sideBySide ? "Left" : "Top");
            Set(helperDeathsPanel, "Width", sideBySide ? width - cooldownWidth - gap : double.NaN);
            SetMargin(cooldownPanel, sideBySide ? gap : 0, sideBySide ? 0 : 16, 0, 0);
        }

        ArrangeSettings();
        BindEvent(helperLayout, "SizeChanged", ArrangeSettings);
        BindClick(cooldownToggle, () =>
        {
            bool enabled = cooldownToggle.GetType().GetProperty("IsChecked")?.GetValue(cooldownToggle) is true;
            SharedRescueSettings.Instance.SetCooldownEnabled(enabled);
            RefreshCooldownRow();
        });
        BindTextChanged(cooldownText, text =>
        {
            if (int.TryParse(text, out int minutes) && minutes is >= 1 and <= 30 &&
                minutes != SharedRescueSettings.Instance.CooldownMinutes)
                SharedRescueSettings.Instance.SetCooldownMinutes(minutes);
        });
        void RefreshCooldownRow()
        {
            bool enabled = SharedRescueSettings.Instance.CooldownEnabled;
            Set(cooldownToggle, "IsChecked", enabled);
            ShowControl(cooldownRow, enabled);
            if (!HasFocus(cooldownText))
                SetTextIfChanged(cooldownText, SharedRescueSettings.Instance.CooldownMinutes.ToString());
        }
        RefreshCooldownRow();
        AddScrollableTab("Rescue Settings", settingsPanel);

        object logPanel = New("DockPanel");
        SetMargin(logPanel, 16, 14, 16, 14);
        object logHeader = New("StackPanel");
        AddChild(logHeader, Text("Recent activity", bottom: 4, heading: true));
        object logStatus = Text("", bottom: 8);
        AddChild(logHeader, logStatus);
        object toolbar = New("StackPanel");
        SetEnum(toolbar, "Orientation", "Horizontal");
        object startButton = New("Button"), stopButton = New("Button"), clearButton = New("Button");
        Set(startButton, "Content", "Start");
        Set(stopButton, "Content", "Stop");
        Set(clearButton, "Content", "Clear Log");
        SetMargin(startButton, 0, 0, 8, 0);
        SetMargin(stopButton, 0, 0, 8, 0);
        AddChild(toolbar, startButton); AddChild(toolbar, stopButton); AddChild(toolbar, clearButton);
        AddChild(logHeader, toolbar);
        AddChild(logHeader, Text("Select text and press Ctrl+C to copy. Live updates pause while the log has focus.", top: 8, bottom: 8));
        Dock(logHeader, "Top");
        AddChild(logPanel, logHeader);
        object logText = New("TextBox");
        Set(logText, "IsReadOnly", true);
        Set(logText, "AcceptsReturn", true);
        Set(logText, "Text", readLog());
        SetEnum(logText, "TextWrapping", "NoWrap");
        SetEnum(logText, "VerticalScrollBarVisibility", "Auto");
        SetEnum(logText, "HorizontalScrollBarVisibility", "Auto");
        AddChild(logPanel, logText);
        AddTab("Watch Dog Log", logPanel);
        void UpdateLogControls()
        {
            bool running = Log.IsRunning;
            Set(startButton, "IsEnabled", !running); Set(stopButton, "IsEnabled", running);
            SetTextIfChanged(logStatus, running ? "Logging is on" : "Logging is stopped");
        }
        BindClick(startButton, () => { startLog(); UpdateLogControls(); });
        BindClick(stopButton, () => { stopLog(); UpdateLogControls(); });
        BindClick(clearButton, () => { clearLog(); Set(logText, "Text", readLog()); });
        UpdateLogControls();
        Set(window, "Content", tabs);
        if (avalonia)
        {
            object? classes = windowType.GetProperty("Classes")?.GetValue(window);
            classes?.GetType().GetMethod("Add", [typeof(string)])?.Invoke(classes, ["skua"]);
        }
        Type timerType = Find(avalonia ? "Avalonia.Threading.DispatcherTimer" : "System.Windows.Threading.DispatcherTimer")
            ?? throw new InvalidOperationException("Host dispatcher timer unavailable.");
        object timer = Activator.CreateInstance(timerType)!;
        Set(timer, "Interval", TimeSpan.FromSeconds(1));
        EventHandler tick = (_, _) =>
        {
            RefreshAccountList();
            RefreshCooldownRow();
            if (!HasFocus(logText)) SetTextIfChanged(logText, readLog());
            UpdateLogControls();
        };
        timerType.GetEvent("Tick")!.AddEventHandler(timer, tick);
        EventHandler handler = (_, _) => { timerType.GetMethod("Stop")!.Invoke(timer, null); timerType.GetEvent("Tick")!.RemoveEventHandler(timer, tick); closed(); };
        windowType.GetEvent("Closed")!.AddEventHandler(window, handler);
        windowType.GetMethod("Show", Type.EmptyTypes)!.Invoke(window, null);
        timerType.GetMethod("Start")!.Invoke(timer, null);
        return window;
    }
}
