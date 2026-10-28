using System.Globalization;
using Newtonsoft.Json;

namespace L2Launcher.Core.Local;

/// <summary>Settings of the local server, stored in &lt;server&gt;\launcher-local.json (the source of truth);
/// <see cref="LocalConfigWriter"/> turns them into settings.env, modules.env and overrides\game\config\*.ini. Applied on restart.</summary>
public sealed class LocalSettings
{
    // rates
    public double RateXp { get; set; } = 10;
    public double RateSp { get; set; } = 10;
    /// <summary>On: module rates-floating decides drop/spoil/adena/raid (Gleb's x10 curve); the four values below are ignored.</summary>
    public bool FloatingDrop { get; set; } = true;
    public double RateAdena { get; set; } = 5;
    public double RateDrop { get; set; } = 3;
    public double RateSpoil { get; set; } = 3;
    public double RateRaid { get; set; } = 2;
    public double RateQuest { get; set; } = 10;
    public double RateManor { get; set; } = 3;
    // character
    public int AutoLearnMaxLevel { get; set; } = 19;
    public bool ClassChange1 { get; set; } = true;
    public bool ClassChange2 { get; set; } = true;
    public bool ClassChange3 { get; set; } = true;
    public bool SubclassWithoutQuest { get; set; } = false;
    public bool OpenCatacombs { get; set; } = true;
    public int StartLevel { get; set; } = 1;
    public long StartAdena { get; set; } = 0;
    // bots
    public bool BotsEnabled { get; set; } = true;
    public int BotsMax { get; set; } = 150;
    /// <summary>Bots in towns, % of the default presence (100 = as now).</summary>
    public int TownDensity { get; set; } = 100;
    /// <summary>Max bots per hunting spot.</summary>
    public int SpotMax { get; set; } = 3;
    public bool PkBots { get; set; } = true;
    public bool RbParties { get; set; } = true;
    public bool Traders { get; set; } = true;
    // gameplay
    public bool BotBuffers { get; set; } = true;
    public bool CommunityBuffer { get; set; } = true;
    public bool NpcBuffer { get; set; } = true;
    /// <summary>Duration of buffs from the stock buffers, minutes (0 = stock).</summary>
    public int BuffDurationMinutes { get; set; } = 60;
    public bool GmShop { get; set; } = true;
    public int EnchantMax { get; set; } = 16;
    public int EnchantChance { get; set; } = 66;
    public bool ReturnSkill { get; set; } = true;

    public const string FileName = "launcher-local.json";

    public static LocalSettings Load(string folder)
    {
        var p = Path.Combine(folder, FileName);
        try { if (File.Exists(p)) return JsonConvert.DeserializeObject<LocalSettings>(File.ReadAllText(p)) ?? new(); }
        catch { }
        return new LocalSettings();
    }

    public void Save(string folder) => IniKeys.AtomicWrite(Path.Combine(folder, FileName), JsonConvert.SerializeObject(this, Formatting.Indented));

    /// <summary>Clamp to sane ranges (UI text boxes can hold anything).</summary>
    public LocalSettings Normalized()
    {
        var s = (LocalSettings)MemberwiseClone();
        static double R(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1000) : 1;
        s.RateXp = R(RateXp); s.RateSp = R(RateSp); s.RateAdena = R(RateAdena); s.RateDrop = R(RateDrop);
        s.RateSpoil = R(RateSpoil); s.RateRaid = R(RateRaid); s.RateQuest = R(RateQuest); s.RateManor = R(RateManor);
        s.AutoLearnMaxLevel = Math.Clamp(AutoLearnMaxLevel, 0, 80);
        s.StartLevel = Math.Clamp(StartLevel, 1, 80);
        s.StartAdena = Math.Clamp(StartAdena, 0, 2_000_000_000);
        s.BotsMax = Math.Clamp(BotsMax, 0, 1000);
        s.TownDensity = Math.Clamp(TownDensity, 0, 300);
        s.SpotMax = Math.Clamp(SpotMax, 0, 20);
        s.BuffDurationMinutes = Math.Clamp(BuffDurationMinutes, 0, 600);
        s.EnchantMax = Math.Clamp(EnchantMax, 4, 65000);
        s.EnchantChance = Math.Clamp(EnchantChance, 0, 100);
        return s;
    }
}

public static class LocalConfigWriter
{
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string B(bool v) => v ? "True" : "False";
    static string OnOff(bool v) => v ? "on" : "off";

    /// <summary>Stock (solo profile) town presence: key -> default; scaled by TownDensity.</summary>
    internal static readonly (string key, int def)[] TownPresence =
    {
        ("PresenceTownStarterMin", 3), ("PresenceTownStarterMax", 5), ("PresenceTownSmallMin", 5), ("PresenceTownSmallMax", 8),
        ("PresenceTownMediumMin", 6), ("PresenceTownMediumMax", 10), ("PresenceTownLargeMin", 10), ("PresenceTownLargeMax", 15),
    };

    public static Dictionary<string, string> SettingsEnv(LocalSettings s)
    {
        var cm = string.Join(",", new[] { (s.ClassChange1, "1"), (s.ClassChange2, "2"), (s.ClassChange3, "3") }.Where(x => x.Item1).Select(x => x.Item2));
        return new()
        {
            ["L2S_CLASS_MASTER"] = cm.Length == 0 ? "off" : cm,
            ["L2S_STOCK_NPC_BUFFER"] = OnOff(s.NpcBuffer),
            ["L2S_BUFF_DURATION"] = (s.BuffDurationMinutes * 60).ToString(),
            ["L2S_ENCHANT_MAX"] = s.EnchantMax.ToString(),
            ["L2S_ENCHANT_CHANCE"] = s.EnchantChance.ToString(),
        };
    }

    public static Dictionary<string, string> ModulesEnv(LocalSettings s)
    {
        var d = new Dictionary<string, string>
        {
            ["L2_MODULE_RATES_FLOATING"] = OnOff(s.FloatingDrop),
            ["L2_MODULE_RETURN_SKILL"] = OnOff(s.ReturnSkill || s.AutoLearnMaxLevel > 0), // auto-learn lives in return-skill
            ["L2_MOD_RETURN_SKILL__AUTOLEARNMAXLEVEL"] = s.AutoLearnMaxLevel.ToString(),
            ["L2_MODULE_BOTS_CORE"] = OnOff(s.BotsEnabled),
            ["L2_MODULE_BOTS_BRAIN_BASIC"] = OnOff(s.BotsEnabled),
            ["L2_MODULE_TOWN_BUFFER"] = OnOff(s.BotsEnabled && s.BotBuffers),
            ["L2_MODULE_ECONOMY_MARKET"] = OnOff(s.BotsEnabled && s.Traders),
            ["L2_MOD_BOTS_CORE__DIRECTORMAXINWORLD"] = s.BotsMax.ToString(),
            ["L2_MOD_BOTS_CORE__PRESENCEFIELDMAX"] = s.SpotMax.ToString(),
            ["L2_MOD_BOTS_CORE__PRESENCEFIELDMIN"] = Math.Min(1, s.SpotMax).ToString(),
            ["L2_MOD_BOTS_CORE__PKBOTSHARE"] = s.PkBots ? "0.02" : "0",
            ["L2_MOD_BOTS_CORE__RBPARTY"] = B(s.RbParties),
        };
        foreach (var (key, def) in TownPresence)
            d["L2_MOD_BOTS_CORE__" + key.ToUpperInvariant()] = ((int)Math.Round(def * s.TownDensity / 100.0)).ToString();
        return d;
    }

    /// <summary>Relative path under overrides\game\config -> keys.</summary>
    public static Dictionary<string, Dictionary<string, string>> IniOverrides(LocalSettings s)
    {
        var rates = new Dictionary<string, string>
        {
            ["RateXp"] = N(s.RateXp), ["RateSp"] = N(s.RateSp), ["PetXpRate"] = N(s.RateXp), ["SinEaterXpRate"] = N(s.RateXp),
            ["RateQuestRewardXP"] = N(s.RateQuest), ["RateQuestRewardSP"] = N(s.RateQuest), ["RateQuestRewardAdena"] = N(s.RateQuest),
            ["RateDropManor"] = N(s.RateManor),
        };
        // floating on: stock x1 here (the module adds its own rolls on top of stock)
        var f = !s.FloatingDrop;
        string V(double v) => f ? N(v) : "1";
        rates["DeathDropAmountMultiplier"] = V(s.RateDrop); rates["DeathDropChanceMultiplier"] = V(s.RateDrop);
        rates["SpoilDropAmountMultiplier"] = V(s.RateSpoil); rates["SpoilDropChanceMultiplier"] = V(s.RateSpoil);
        rates["RaidDropAmountMultiplier"] = V(s.RateRaid); rates["RaidDropChanceMultiplier"] = V(s.RateRaid);
        rates["DropAmountMultiplierByItemId"] = $"57,{V(s.RateAdena)};6656,1;6657,1;6658,1;6659,1;6660,1;6661,1;6662,1;8191,1";
        return new()
        {
            ["Rates.ini"] = rates,
            ["Player.ini"] = new()
            {
                ["AltSubClassWithoutQuests"] = B(s.SubclassWithoutQuest),
                ["StartingLevel"] = s.StartLevel.ToString(),
                ["StartingAdena"] = s.StartAdena.ToString(),
            },
            ["Feature.ini"] = new()
            {
                ["AltOpenCatacumbs"] = B(s.OpenCatacombs), ["AltOpenNecropolis"] = B(s.OpenCatacombs), ["StrictSevenSigns"] = "False",
            },
            [Path.Combine("Custom", "CommunityBoard.ini")] = new()
            {
                ["CustomCommunityBoard"] = B(s.CommunityBuffer || s.GmShop),
                ["CommunityEnableBuffs"] = B(s.CommunityBuffer),
                ["CommunityEnableMultisells"] = B(s.GmShop),
            },
        };
    }

    /// <summary>Writes every generated file into the server folder and saves launcher-local.json.</summary>
    public static void Apply(string folder, LocalSettings settings)
    {
        var s = settings.Normalized();
        IniKeys.SetFile(Path.Combine(folder, "settings.env"), SettingsEnv(s), "=");
        IniKeys.SetFile(Path.Combine(folder, "modules.env"), ModulesEnv(s), "=");
        var cfg = Path.Combine(folder, "overrides", "game", "config");
        foreach (var (rel, keys) in IniOverrides(s)) IniKeys.SetFile(Path.Combine(cfg, rel), keys);
        s.Save(folder);
    }
}
