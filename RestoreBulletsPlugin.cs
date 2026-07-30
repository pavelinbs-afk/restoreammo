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
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "pRfect";

    public RestoreBulletsConfig Config { get; set; } = new();

    private bool _roundActive = true;
    private float _nextCheckAt;

    /// <summary>
    /// Удерживаем Clip1=0 после выдачи, пока игрок сам не нажмёт R
    /// (иначе движок может сразу затянуть запас в обойму).
    /// </summary>
    private readonly HashSet<nuint> _holdEmptyClip = [];

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
            _holdEmptyClip.Clear();
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
        _holdEmptyClip.Clear();
        LogInfo("Round started.");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundActive = false;
        _holdEmptyClip.Clear();
        LogInfo("Round ended, restore paused until next round.");
        return HookResult.Continue;
    }

    private void OnPostEntityThink()
    {
        if (!Config.Enabled || !_roundActive)
        {
            _holdEmptyClip.Clear();
            return;
        }

        EnforceHeldEmptyClips();

        var now = Server.CurrentTime;
        if (now < _nextCheckAt)
            return;

        _nextCheckAt = now + Config.CheckIntervalSeconds;

        foreach (var player in Utilities.GetPlayers())
            RestorePlayerWeapons(player);
    }

    private void EnforceHeldEmptyClips()
    {
        if (_holdEmptyClip.Count == 0)
            return;

        List<nuint>? toRemove = null;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, PawnIsAlive: true })
                continue;

            var weaponServices = player.PlayerPawn?.Value?.WeaponServices;
            if (weaponServices == null)
                continue;

            foreach (var weaponHandle in weaponServices.MyWeapons)
            {
                var weapon = weaponHandle.Value;
                if (weapon is not { IsValid: true })
                    continue;

                var key = (nuint)weapon.Handle;
                if (!_holdEmptyClip.Contains(key))
                    continue;

                // Игрок перезарядился — отпускаем.
                if (weapon.Clip1 > 0)
                {
                    toRemove ??= [];
                    toRemove.Add(key);
                    continue;
                }

                var vdata = weapon.VData;
                if (vdata == null)
                {
                    toRemove ??= [];
                    toRemove.Add(key);
                    continue;
                }

                // Запас уже израсходован — отпускаем.
                if (GetReserve0(weapon) <= 0)
                {
                    toRemove ??= [];
                    toRemove.Add(key);
                    continue;
                }

                if (weapon.Clip1 != 0)
                {
                    weapon.Clip1 = 0;
                    Utilities.SetStateChanged(weapon.As<CCSWeaponBase>(), "CBasePlayerWeapon", "m_iClip1");
                }
            }
        }

        if (toRemove == null)
            return;

        foreach (var key in toRemove)
            _holdEmptyClip.Remove(key);
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

            TryRestoreWeapon(player, weapon, weaponServices);
        }
    }

    private void TryRestoreWeapon(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices weaponServices)
    {
        var weaponName = weapon.GetWeaponName() ?? weapon.DesignerName;
        if (string.IsNullOrEmpty(weaponName))
            return;

        if (ExcludedWeapons.Contains(weaponName) || weaponName.StartsWith("weapon_knife", StringComparison.Ordinal))
            return;

        var vdata = weapon.VData;
        if (vdata == null || vdata.MaxClip1 <= 1)
            return;

        // В обойме ещё есть патроны — не трогаем.
        if (weapon.Clip1 > 0)
            return;

        var ammoType = (int)vdata.PrimaryAmmoType;
        if (ammoType < 0 || ammoType > 31)
            return;

        var reserveAsClips = vdata.ReserveAmmoAsClips;
        if (!NeedsRestore(weapon, weaponServices, ammoType, reserveAsClips))
        {
            LogDebugPlayer(player,
                "skip {Weapon}: usable reserve (clip={Clip}, reserve0={Reserve}, wsAmmo={WsAmmo}, asClips={AsClips})",
                weaponName,
                weapon.Clip1,
                GetReserve0(weapon),
                ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0,
                reserveAsClips);
            return;
        }

        var restoreAmount = GetRestoreAmount(weapon);
        if (restoreAmount <= 0)
            return;

        ApplyReserve(weapon, weaponServices, ammoType, restoreAmount, reserveAsClips, holdEmptyClip: true);

        // Повтор на следующем кадре — винтовки любят перезаписывать ammo.
        var weaponHandle = weapon.Handle;
        var playerSlot = player.Slot;
        var amount = restoreAmount;
        var asClips = reserveAsClips;
        Server.NextFrame(() =>
        {
            var p = Utilities.GetPlayerFromSlot(playerSlot);
            if (p is not { IsValid: true, PawnIsAlive: true })
                return;

            var ws = p.PlayerPawn?.Value?.WeaponServices;
            if (ws == null)
                return;

            foreach (var handle in ws.MyWeapons)
            {
                var w = handle.Value;
                if (w is not { IsValid: true } || w.Handle != weaponHandle)
                    continue;

                if (w.Clip1 > 0)
                    return;

                if (!NeedsRestore(w, ws, ammoType, asClips))
                    return;

                ApplyReserve(w, ws, ammoType, amount, asClips, holdEmptyClip: true);
                return;
            }
        });

        LogInfo(
            "Restored {Player} weapon={Weapon} amount={Amount} asClips={AsClips} clipAfter={Clip} reserveAfter={ReserveAfter} wsAmmoAfter={WsAmmo}",
            player.PlayerName,
            weaponName,
            restoreAmount,
            reserveAsClips,
            weapon.Clip1,
            GetReserve0(weapon),
            ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0);
    }

    /// <summary>
    /// Выдаём только когда обойма пуста и запас реально закончился (reserve0 &lt;= 0).
    /// Не трогаем оружие, если обоймы ещё есть — иначе затираем 2–3 оставшиеся до 1.
    /// </summary>
    private static bool NeedsRestore(
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices weaponServices,
        int ammoType,
        bool reserveAsClips)
    {
        var reserve0 = GetReserve0(weapon);

        if (reserveAsClips)
            return reserve0 <= 0;

        var wsAmmo = ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0;
        return reserve0 <= 0 && wsAmmo <= 0;
    }

    private static int GetRestoreAmount(CBasePlayerWeapon weapon)
    {
        var vdata = weapon.VData;
        if (vdata == null)
            return 0;

        if (vdata.ReserveAmmoAsClips)
            return 1;

        return vdata.MaxClip1;
    }

    private static int GetReserve0(CBasePlayerWeapon weapon)
    {
        var reserve = weapon.ReserveAmmo;
        return reserve.Length > 0 ? reserve[0] : 0;
    }

    private void ApplyReserve(
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices weaponServices,
        int ammoType,
        int amount,
        bool reserveAsClips,
        bool holdEmptyClip)
    {
        var reserveSpan = weapon.ReserveAmmo;
        var currentReserve = reserveSpan.Length > 0 ? reserveSpan[0] : 0;

        // Никогда не уменьшаем уже имеющийся запас (2–3 обоймы не затираем до 1).
        if (reserveSpan.Length > 0 && currentReserve < amount)
            reserveSpan[0] = amount;

        // m_iAmmo синхронизируем только при реальной выдаче с нуля.
        if (currentReserve <= 0 && ammoType < weaponServices.Ammo.Length)
            weaponServices.Ammo[ammoType] = (ushort)Math.Clamp(amount, 0, ushort.MaxValue);

        var weaponBase = weapon.As<CCSWeaponBase>();
        Utilities.SetStateChanged(weaponBase, "CBasePlayerWeapon", "m_pReserveAmmo");

        if (weapon.Clip1 != 0)
        {
            weapon.Clip1 = 0;
            Utilities.SetStateChanged(weaponBase, "CBasePlayerWeapon", "m_iClip1");
        }

        if (holdEmptyClip)
            _holdEmptyClip.Add((nuint)weapon.Handle);

        _ = reserveAsClips;
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
        ApplyReserve(weapon, weaponServices, ammoType, amount, reserveAsClips, holdEmptyClip: true);

        var reserve0 = GetReserve0(weapon);
        var wsAmmo = ammoType >= 0 && ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0;

        command.ReplyToCommand(
            $"[RestoreBullets] Forced {weaponName}: set={amount}, asClips={reserveAsClips}, reserve0={reserve0}, wsAmmo={wsAmmo}, clip={weapon.Clip1}");
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
            var reserve0 = GetReserve0(weapon);
            var wsAmmo = ammoType >= 0 && ammoType < weaponServices.Ammo.Length ? weaponServices.Ammo[ammoType] : (ushort)0;
            var needs = weapon.Clip1 <= 0
                        && NeedsRestore(weapon, weaponServices, ammoType, reserveAsClips)
                        && vdata.MaxClip1 > 1
                        && !ExcludedWeapons.Contains(weaponName)
                        && !weaponName.StartsWith("weapon_knife", StringComparison.Ordinal);

            reply(
                $"  {weaponName}: clip={weapon.Clip1} maxClip={vdata.MaxClip1} reserve0={reserve0} wsAmmo={wsAmmo} asClips={reserveAsClips} restoreAmount={GetRestoreAmount(weapon)} needsRestore={needs}");
        }
    }

    private void LogInfo(string message, params object?[] args) =>
        Logger.LogInformation("[RestoreBullets] " + message, args);

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
