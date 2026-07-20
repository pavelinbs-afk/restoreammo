using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using Microsoft.Extensions.Logging;

namespace RestoreBullets;

[MinimumApiVersion(80)]
public sealed class RestoreBulletsPlugin : BasePlugin, IPluginConfig<RestoreBulletsConfig>
{
    public override string ModuleName => "RestoreBullets";
    public override string ModuleVersion => "1.0.7";
    public override string ModuleAuthor => "pRfect";

    public RestoreBulletsConfig Config { get; set; } = new();

    private bool _roundActive = true;
    private float _nextCheckAt;

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

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _roundActive = true;
            LogInfo("Map started, round tracking enabled.");
        });

        RegisterListener<Listeners.OnServerPostEntityThink>(OnPostEntityThink);

        AddCommand("css_restorebullets_debug", "Print ammo restore debug info", OnDebugCommand);
        AddCommand("css_restorebullets_test", "Force restore active weapon reserve", OnTestCommand);

        LogInfo("Loaded (hotReload={HotReload}, enabled={Enabled}, debug={Debug}, interval={Interval}s)",
            hotReload, Config.Enabled, Config.Debug, Config.CheckIntervalSeconds);
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

    private void OnPostEntityThink()
    {
        if (!Config.Enabled || !_roundActive)
            return;

        var now = Server.CurrentTime;
        if (now < _nextCheckAt)
            return;

        _nextCheckAt = now + Config.CheckIntervalSeconds;

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

        SetReserveAmmo(weapon, weaponServices, ammoType, restoreAmount, reserveAsClips);

        var afterReserve = GetTotalReserveAmmo(weapon, weaponServices, ammoType, reserveAsClips);
        LogInfo(
            "Restored {Player} weapon={Weapon} amount={Amount} clipAfter={Clip} reserveAfter={ReserveAfter} wsAmmoAfter={WsAmmo}",
            player.PlayerName,
            weaponName,
            restoreAmount,
            weapon.Clip1,
            afterReserve,
            ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0);
    }

    /// <summary>
    /// Для обойм (ReserveAmmoAsClips) — 1 запасная обойма в reserve, не патроны в clip.
    /// Для дробовиков и старого пула — MaxClip1 патронов в reserve.
    /// </summary>
    private static int GetRestoreAmount(CBasePlayerWeapon weapon)
    {
        var vdata = weapon.VData;
        if (vdata == null)
            return 0;

        if (vdata.ReserveAmmoAsClips)
            return 1;

        return vdata.MaxClip1;
    }

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
        CPlayer_WeaponServices weaponServices,
        int ammoType,
        int amount,
        bool reserveAsClips)
    {
        var clipBefore = weapon.Clip1;

        var reserveSpan = weapon.ReserveAmmo;
        if (reserveSpan.Length > 0)
            reserveSpan[0] = amount;

        if (ammoType < reserveSpan.Length)
            reserveSpan[ammoType] = amount;

        // m_iAmmo для clip-оружия заполняет обойму — трогаем только m_pReserveAmmo.
        if (!reserveAsClips && ammoType < weaponServices.Ammo.Length)
            weaponServices.Ammo[ammoType] = (ushort)Math.Clamp(amount, 0, ushort.MaxValue);

        var weaponBase = weapon.As<CCSWeaponBase>();
        Utilities.SetStateChanged(weaponBase, "CBasePlayerWeapon", "m_pReserveAmmo");

        if (reserveAsClips && weapon.Clip1 != clipBefore)
        {
            weapon.Clip1 = clipBefore;
            Utilities.SetStateChanged(weaponBase, "CBasePlayerWeapon", "m_iClip1");
        }
    }

    private void OnTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        var target = player;
        if (target is not { IsValid: true })
        {
            command.ReplyToCommand("[RestoreBullets] Run from in-game as alive player.");
            return;
        }

        var pawn = target.PlayerPawn?.Value;
        var weaponServices = pawn?.WeaponServices;
        var weapon = weaponServices?.ActiveWeapon.Value;
        if (pawn is not { IsValid: true } || weaponServices == null || weapon is not { IsValid: true })
        {
            command.ReplyToCommand("[RestoreBullets] No active weapon.");
            return;
        }

        var weaponName = weapon.GetWeaponName() ?? weapon.DesignerName ?? "?";
        var vdata = weapon.VData;
        if (vdata == null)
        {
            command.ReplyToCommand("[RestoreBullets] VData is null.");
            return;
        }

        var ammoType = (int)vdata.PrimaryAmmoType;
        var reserveAsClips = vdata.ReserveAmmoAsClips;
        var amount = GetRestoreAmount(weapon);
        SetReserveAmmo(weapon, weaponServices, ammoType, amount, reserveAsClips);

        var reserve = weapon.ReserveAmmo;
        var reserve0 = reserve.Length > 0 ? reserve[0] : -1;
        var wsAmmo = ammoType >= 0 && ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0;

        command.ReplyToCommand(
            $"[RestoreBullets] Forced {weaponName}: set={amount}, reserve0={reserve0}, wsAmmo={wsAmmo}, clip={weapon.Clip1}");
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
                $"  {weaponName}: clip={weapon.Clip1} maxClip={vdata.MaxClip1} reserve0={reserve0} reserve[{ammoType}]={reserveAt} wsAmmo={wsAmmo} totalReserve={totalReserve} asClips={reserveAsClips} restoreAmount={GetRestoreAmount(weapon)} wouldRestore={wouldRestore}");
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
