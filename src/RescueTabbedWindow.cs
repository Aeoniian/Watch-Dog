using System.Collections;
using System.Reflection;
using WatchDog.Shared;

namespace WatchDog;

// Uses the host UI framework and its own option controls; no WPF/Avalonia dependencies are shipped.
internal static class RescueTabbedWindow
{
    internal static object Show(object allies, object settings, Action closed)
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
        object window = Activator.CreateInstance(windowType)!;
        Set(window, "Title", "Watch Dog — Rescue Allies");
        Set(window, "Width", 680d); Set(window, "Height", 490d);
        Set(window, "MinWidth", 500d); Set(window, "MinHeight", 350d);
        object tabs = New("TabControl");
        object? statusText = null;
        foreach (var (title, model) in new[] { ("Rescue Allies", allies), ("Rescue Settings", settings) })
        {
            object view = Activator.CreateInstance(viewType)!;
            Set(view, "DataContext", model);
            object tab = New("TabItem");
            Set(tab, "Header", title);
            if (title == "Rescue Allies")
            {
                object panel = New("StackPanel"); statusText = New("TextBlock");
                Set(statusText, "Text", RescueStatus.Describe(RescueStatus.Latest, DateTime.UtcNow));
                var wrap = statusText.GetType().GetProperty("TextWrapping")!;
                wrap.SetValue(statusText, Enum.Parse(wrap.PropertyType, "Wrap"));
                object children = panel.GetType().GetProperty("Children")!.GetValue(panel)!;
                void Add(object child) { if (children is IList list) list.Add(child); else children.GetType().GetMethod("Add", [typeof(object)])!.Invoke(children, [child]); }
                Add(statusText); Add(view); Set(tab, "Content", panel);
            }
            else Set(tab, "Content", view);
            object items = tabs.GetType().GetProperty("Items")!.GetValue(tabs)!;
            if (items is IList list) list.Add(tab);
            else items.GetType().GetMethod("Add", [typeof(object)])!.Invoke(items, [tab]);
        }
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
        EventHandler tick = (_, _) => { if (statusText is not null) Set(statusText, "Text", RescueStatus.Describe(RescueStatus.Latest, DateTime.UtcNow)); };
        timerType.GetEvent("Tick")!.AddEventHandler(timer, tick);
        EventHandler handler = (_, _) => { timerType.GetMethod("Stop")!.Invoke(timer, null); timerType.GetEvent("Tick")!.RemoveEventHandler(timer, tick); closed(); };
        windowType.GetEvent("Closed")!.AddEventHandler(window, handler);
        windowType.GetMethod("Show", Type.EmptyTypes)!.Invoke(window, null);
        timerType.GetMethod("Start")!.Invoke(timer, null);
        return window;
    }
}
