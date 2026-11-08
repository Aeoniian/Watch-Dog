using Skua.Core.Models;

namespace WatchDog.Shared;

/// <summary>Reads the per-character Boss selection saved by Skua CoreBots options.</summary>
internal sealed record BossRescueLoadout(string ClassName, string SkillMode, bool UseEquipment,
    string Weapon, string Armor, string Helm, string Cape, string Pet, string GroundItem)
{
    internal static BossRescueLoadout? Read(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        string fileName = $"CBO_Storage({account}).txt";
        string[] candidates =
        [
            Path.Combine(ClientFileSources.SkuaOptionsDIR, fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua", "options", fileName)
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return null;
        try
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(path))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            values.TryGetValue("BossClassSelect", out string? className);
            values.TryGetValue("BossModeSelect", out string? mode);
            values.TryGetValue("BossEquipCheck", out string? gear);
            values.TryGetValue("Weapon4Select", out string? weapon);
            values.TryGetValue("Armor4Select", out string? armor);
            values.TryGetValue("Helm4Select", out string? helm);
            values.TryGetValue("Cape4Select", out string? cape);
            values.TryGetValue("Pet4Select", out string? pet);
            values.TryGetValue("GroundItem4Select", out string? groundItem);
            bool useEquipment = bool.TryParse(gear, out bool enabled) && enabled;
            if (string.IsNullOrWhiteSpace(className) && !useEquipment) return null;
            return new BossRescueLoadout(className ?? "", mode ?? "", useEquipment,
                weapon ?? "", armor ?? "", helm ?? "", cape ?? "", pet ?? "", groundItem ?? "");
        }
        catch { return null; }
    }
}
