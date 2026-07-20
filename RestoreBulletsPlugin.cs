using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace RestoreBullets;

[MinimumApiVersion(80)]
public sealed class RestoreBulletsPlugin : BasePlugin, IPluginConfig<RestoreBulletsConfig>
{
    public override string ModuleName => "RestoreBullets";
    public override string ModuleVersion => "1.0.4";
    public override string ModuleAuthor => "pRfect";

    public RestoreBulletsConfig Config { get; set; } = new();

    private bool _roundActive = true;

    /// <summary>
    /// Сколько запасных обойм выдавать для оружия с системой обойм (CS2).
    /// </summary>
    private const int RestoreMagazineCount = 1;

    private static readonly HashSet<string> ExcludedWeapons =
    [
        "weapon_hegrenade",
        "weapon_flashbang",
        "weapon_smokegrenade",
        "weapon_molotov",
        "weapon_incgrenade",
        "weapon_decoy",
        "weapon_taser",
        "weapon_healthshot",
        "weapon_c4",
        "weapon_knife",
        "weapon_knife_t",
        "weapon_bayonet",
        "weapon_fists",
    ];

    public void OnConfigParsed(RestoreBulletsConfig config)
    {
        if (config.CheckIntervalSeconds < 0.05f)
            config.CheckIntervalSeconds = 0.05f;

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _roundActive = true;

        var interval = Math.Max(Config.CheckIntervalSeconds, 0.05f);
        AddTimer(interval, CheckAllPlayers, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _roundActive = true;
            LogInfo("Map started, round tracking enabled.");
        });

        AddCommand("css_restorebullets_debug", "Print ammo restore debug info", OnDebugCommand);

        LogInfo("Loaded (hotReload={HotReload}, enabled={Enabled}, debug={Debug}, interval={Interval}s)",
            hotReload, Config.Enabled, Config.Debug, interval);
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundActive = true;
        LogInfo("Round started.");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundActive = false;
        LogInfo("Round ended, restore paused until next round.");
        return HookResult.Continue;
    }

    private void CheckAllPlayers()
    {
        if (!Config.Enabled)
            return;

        if (!_roundActive)
        {
            LogDebug("Tick skipped: round is not active.");
            return;
        }

        foreach (var player in Utilities.GetPlayers())
            RestorePlayerWeapons(player);
    }

    private void RestorePlayerWeapons(CCSPlayerController player)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var pawn = player.PlayerPawn?.Value;
        var weaponServices = pawn?.WeaponServices;
        if (pawn is not { IsValid: true } || weaponServices == null)
            return;

        foreach (var weaponHandle in weaponServices.MyWeapons)
        {
            var weapon = weaponHandle.Value;
            if (weapon is not { IsValid: true })
                continue;

            TryRestoreWeapon(player, pawn, weapon, weaponServices);
        }
    }

    private void TryRestoreWeapon(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices weaponServices)
    {
        var weaponName = weapon.GetWeaponName() ?? weapon.DesignerName;
        if (string.IsNullOrEmpty(weaponName))
        {
            LogDebugPlayer(player, "skip: empty weapon name");
            return;
        }

        if (ExcludedWeapons.Contains(weaponName) || weaponName.StartsWith("weapon_knife", StringComparison.Ordinal))
            return;

        var vdata = weapon.VData;
        if (vdata == null)
        {
            LogDebugPlayer(player, "skip {Weapon}: VData is null", weaponName);
            return;
        }

        if (vdata.MaxClip1 <= 1)
        {
            LogDebugPlayer(player, "skip {Weapon}: MaxClip1={MaxClip1}", weaponName, vdata.MaxClip1);
            return;
        }

        if (weapon.Clip1 > 0)
            return;

        var ammoType = (int)vdata.PrimaryAmmoType;
        if (ammoType < 0)
        {
            LogDebugPlayer(player, "skip {Weapon}: invalid ammo type", weaponName);
            return;
        }

        var reserveAsClips = vdata.ReserveAmmoAsClips;
        var reserveAmmo = GetTotalReserveAmmo(weapon, weaponServices, ammoType, reserveAsClips);
        if (reserveAmmo > 0)
        {
            LogDebugPlayer(player,
                "skip {Weapon}: reserve still present (clip={Clip}, reserve={Reserve}, reserveAsClips={AsClips}, ammoType={AmmoType}, wsAmmo={WsAmmo})",
                weaponName,
                weapon.Clip1,
                reserveAmmo,
                reserveAsClips,
                ammoType,
                ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0);
            return;
        }

        var restoreAmount = GetRestoreAmount(weapon);
        if (restoreAmount <= 0)
        {
            LogDebugPlayer(player, "skip {Weapon}: restore amount is 0", weaponName);
            return;
        }

        SetReserveAmmo(weapon, pawn, weaponServices, ammoType, restoreAmount);

        LogInfo("Restored {Player} weapon={Weapon} amount={Amount} reserveAsClips={AsClips} clipBefore={Clip}",
            player.PlayerName,
            weaponName,
            restoreAmount,
            reserveAsClips,
            weapon.Clip1);
    }

    private static int GetRestoreAmount(CBasePlayerWeapon weapon)
    {
        var vdata = weapon.VData;
        if (vdata == null)
            return 0;

        if (vdata is CCSWeaponBaseVData { ReloadsSingleShells: true })
            return vdata.MaxClip1;

        if (vdata.ReserveAmmoAsClips)
            return RestoreMagazineCount;

        return vdata.MaxClip1;
    }

    /// <summary>
    /// Для обойм (ReserveAmmoAsClips) смотрим только m_pReserveAmmo оружия.
    /// m_iAmmo на pawn может хранить устаревший пул патронов и блокировать выдачу.
    /// </summary>
    private static int GetTotalReserveAmmo(
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices weaponServices,
        int ammoType,
        bool reserveAsClips)
    {
        var reserve = weapon.ReserveAmmo;
        var fromWeapon = 0;

        if (reserve.Length > 0)
            fromWeapon = Math.Max(fromWeapon, reserve[0]);

        if (ammoType < reserve.Length)
            fromWeapon = Math.Max(fromWeapon, reserve[ammoType]);

        if (reserveAsClips)
            return fromWeapon;

        var total = fromWeapon;
        if (ammoType < weaponServices.Ammo.Length)
            total = Math.Max(total, weaponServices.Ammo[ammoType]);

        return total;
    }

    private static void SetReserveAmmo(
        CBasePlayerWeapon weapon,
        CCSPlayerPawn pawn,
        CPlayer_WeaponServices weaponServices,
        int ammoType,
        int amount)
    {
        var reserveAmount = (ushort)Math.Clamp(amount, 0, ushort.MaxValue);

        if (ammoType < weaponServices.Ammo.Length)
            weaponServices.Ammo[ammoType] = reserveAmount;

        var reserveSpan = weapon.ReserveAmmo;
        if (reserveSpan.Length > 0)
            reserveSpan[0] = amount;

        if (ammoType < reserveSpan.Length)
            reserveSpan[ammoType] = amount;

        Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
    }

    private void OnDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        var target = player;
        if (target is not { IsValid: true })
        {
            var firstAlive = Utilities.GetPlayers().FirstOrDefault(p => p is { IsValid: true, PawnIsAlive: true });
            if (firstAlive == null)
            {
                command.ReplyToCommand("[RestoreBullets] No alive players found.");
                return;
            }

            target = firstAlive;
            command.ReplyToCommand($"[RestoreBullets] Using player: {target.PlayerName}");
        }

        DumpPlayerState(target, command.ReplyToCommand);
    }

    private void DumpPlayerState(CCSPlayerController player, Action<string> reply)
    {
        reply($"[RestoreBullets] enabled={Config.Enabled} roundActive={_roundActive} debug={Config.Debug}");

        if (player is not { IsValid: true, PawnIsAlive: true })
        {
            reply("[RestoreBullets] Player is not alive.");
            return;
        }

        var pawn = player.PlayerPawn?.Value;
        var weaponServices = pawn?.WeaponServices;
        if (pawn is not { IsValid: true } || weaponServices == null)
        {
            reply("[RestoreBullets] Pawn or WeaponServices is missing.");
            return;
        }

        var active = weaponServices.ActiveWeapon.Value;
        var activeName = active?.GetWeaponName() ?? active?.DesignerName ?? "none";

        reply($"[RestoreBullets] Player={player.PlayerName} active={activeName}");

        foreach (var weaponHandle in weaponServices.MyWeapons)
        {
            var weapon = weaponHandle.Value;
            if (weapon is not { IsValid: true })
                continue;

            var weaponName = weapon.GetWeaponName() ?? weapon.DesignerName ?? "?";
            var vdata = weapon.VData;
            if (vdata == null)
            {
                reply($"  {weaponName}: VData=null");
                continue;
            }

            var ammoType = (int)vdata.PrimaryAmmoType;
            var reserveAsClips = vdata.ReserveAmmoAsClips;
            var reserve = weapon.ReserveAmmo;
            var reserve0 = reserve.Length > 0 ? reserve[0] : -1;
            var reserveAt = ammoType >= 0 && ammoType < reserve.Length ? reserve[ammoType] : -1;
            var wsAmmo = ammoType >= 0 && ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0;
            var totalReserve = GetTotalReserveAmmo(weapon, weaponServices, ammoType, reserveAsClips);
            var wouldRestore = Config.Enabled
                               && _roundActive
                               && weapon.Clip1 <= 0
                               && totalReserve <= 0
                               && vdata.MaxClip1 > 1
                               && !ExcludedWeapons.Contains(weaponName)
                               && !weaponName.StartsWith("weapon_knife", StringComparison.Ordinal);

            reply(
                $"  {weaponName}: clip={weapon.Clip1} maxClip={vdata.MaxClip1} reserve0={reserve0} reserve[{ammoType}]={reserveAt} wsAmmo={wsAmmo} totalReserve={totalReserve} asClips={reserveAsClips} singleShells={(vdata is CCSWeaponBaseVData cs && cs.ReloadsSingleShells)} restoreAmount={GetRestoreAmount(weapon)} wouldRestore={wouldRestore}");
        }
    }

    private void LogInfo(string message, params object?[] args) =>
        Logger.LogInformation("[RestoreBullets] " + message, args);

    private void LogDebug(string message, params object?[] args)
    {
        if (!Config.Debug)
            return;

        Logger.LogInformation("[RestoreBullets:Debug] " + message, args);
    }

    private void LogDebugPlayer(CCSPlayerController player, string message, params object?[] args)
    {
        if (!Config.Debug)
            return;

        Logger.LogInformation("[RestoreBullets:Debug] {Player}: " + message,
            PrependPlayer(player, args));
    }

    private static object?[] PrependPlayer(CCSPlayerController player, object?[] args)
    {
        var result = new object?[args.Length + 1];
        result[0] = player.PlayerName;
        Array.Copy(args, 0, result, 1, args.Length);
        return result;
    }
}
