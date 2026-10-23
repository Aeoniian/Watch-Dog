using System.Diagnostics;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace WatchDog;

/// <summary>
/// Watch Dog: keeps an account logged in and its script running, for unattended farming in Skua and VibeSkua.
/// </summary>
public class WatchDogPlugin : ISkuaPlugin
{
    public string Name => "Watch Dog";
    public string Author => "Aeoniian";
    public string Description =>
        "Keeps accounts logged in and their scripts running: fixes stuck logins, logs back in after disconnects, " +
        "relogs scripts that stopped doing anything or hit AQW's \"Missing Quest Progress\" bug, starts scripts " +
        "again after every relog, and can send accounts to help one that keeps dying (Rescue Allies).";

    internal const string Category = "Watch Dog";

    public List<IOption>? Options { get; } = new()
    {
        Opt.Bool("enabled", "Enable Watch Dog", "Turns every Watch Dog feature on or off.", true),
        Opt.Bool("stuckLogin", "Fix stuck logins",
            "When the login sits on \"Authenticating...\", \"Connecting...\", \"Loading Character Data...\" and similar for 20 seconds (60 with VibeSkua 2.0.7+'s own Auto Relogin on), reset the login screen and log in again (avoiding that server for a few minutes).", true),
        Opt.Bool("relogOut", "Log back in after 3 minutes logged out",
            "When the account has been on the login screen for 3 minutes with nothing happening (auto relogin gave up, kicked, a failed login).", true),
        Opt.Bool("relogOutOnlyScript", "...only when a script was running",
            "Only log back in by itself when a script was running when the account got logged out, so logging out by hand is left alone.", true),
        Opt.Bool("connectionLost", "Fix \"Communication with server has been lost\"",
            "When that message stays on screen for 1 minute (2 with VibeSkua 2.0.7+'s own Auto Relogin on), log back in.", true),
        Opt.Int("stuckScriptMinutes", "Relog a script that does nothing (minutes, 0 = off)",
            "A running script with no kills, drops, quest turn-ins/accepts, gold changes or movement for this long gets a relog (at most once every 20 minutes).", 15),
        Opt.Bool("questErrors", "Relog on \"Missing Quest Progress\" spam",
            "AQW sometimes refuses every turn-in of a finished quest until you relog. 5 of those messages in 2 minutes (or the same quest refused 10 times) while a script runs: relog.", true),
        Opt.Bool("restartScript", "Start the script again after a relog",
            "Once the account is back in game (and has stayed in game a few seconds), start the script that was running, check it really runs, up to 3 tries.", true),
        Opt.Bool("returnToRoom", "Go back to the same room after a relog",
            "With no script to restart, go back to the room the account was in (followers of a ButlerV5 master can find it again).", true),
        Opt.Bool("rescueAllies", "Rescue Allies",
            "While this account runs a script: when another account on this PC and server keeps dying (10 deaths without a kill, or 15 minutes of only deaths), stop the script, go help, then come back and start it again.", false),
    };

    private Watchdog? _watchdog;
    private IPluginHelper? _helper;
    private const string LogButton = "Watch Dog log";

    public void Load(IServiceProvider provider, IPluginHelper helper)
    {
        _helper = helper;
        if (IsVibeSkuffle())
        {
            Log.Write("?", " running inside VibeSkuffle, which has all of this built in; Watch Dog stays off here.");
            return;
        }

        IScriptInterface? bot = provider.GetService(typeof(IScriptInterface)) as IScriptInterface;
        IScriptManager? manager = provider.GetService(typeof(IScriptManager)) as IScriptManager;
        IPluginManager? plugins = provider.GetService(typeof(IPluginManager)) as IPluginManager;
        IDispatcherService? dispatcher = provider.GetService(typeof(IDispatcherService)) as IDispatcherService;
        if (bot is null || manager is null)
        {
            Log.Write("?", " couldn't reach the bot; Watch Dog is off.");
            return;
        }
        _watchdog = new Watchdog(bot, manager, dispatcher, () => plugins?.GetContainer(this)?.OptionContainer);
        _watchdog.Start();
        try { helper.AddMenuButton(LogButton, OpenLog); } catch { }
    }

    public void Unload()
    {
        try { _helper?.RemoveMenuButton(LogButton); } catch { }
        _watchdog?.Stop();
        _watchdog = null;
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath))
                Log.Write("?", " log opened.");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>VibeSkuffle (VibeSkua on Ruffle) has its own built-in watchdog; running both would double up.</summary>
    private static bool IsVibeSkuffle()
    {
        try
        {
            string name = Process.GetCurrentProcess().ProcessName;
            if (name.StartsWith("VibeSkuffle", StringComparison.OrdinalIgnoreCase))
                return true;
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "Skua.Core.Interfaces" && a.GetType("Skua.Core.Interfaces.IRestartableGamePlayer") is not null);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>An option definition (the same shape as Skua's Option&lt;T&gt;, so it works in every client).</summary>
internal sealed class Opt : IOption
{
    public string Category { get; set; } = WatchDogPlugin.Category;
    public string Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public object? DefaultValue { get; }
    public bool Transient => false;
    public Type Type { get; }

    private Opt(string name, string displayName, string description, object value, Type type)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        DefaultValue = value;
        Type = type;
    }

    public static Opt Bool(string name, string display, string description, bool value) => new(name, display, description, value, typeof(bool));
    public static Opt Int(string name, string display, string description, int value) => new(name, display, description, value, typeof(int));

    public override string ToString() => DisplayName;
}

/// <summary>watchdog.log in the client's plugins folder (time and account name, never passwords).</summary>
internal static class Log
{
    private static readonly object Lock = new();

    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(ClientFileSources.SkuaPluginsDIR, "WatchDog");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "watchdog.log");
        }
    }

    public static void Write(string who, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{who}]{message}{Environment.NewLine}";
        // Several clients share this file: open it shared and retry briefly if another one is writing.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                string path = FilePath;
                lock (Lock)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 2_000_000)
                    {
                        try { File.Copy(path, path + ".old", true); File.WriteAllText(path, ""); } catch { }
                    }
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch
            {
                return;
            }
        }
    }
}
