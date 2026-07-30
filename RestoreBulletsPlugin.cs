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
    public override string ModuleVersion => "1.1.1";
    public override string ModuleAuthor => "pRfect";

    public RestoreBulletsConfig Config { get; set; } = new();

    private bool _roundActive = true;
    private float _nextCheckAt;

    /// <summary>
    /// Оружия, которым принудительно держим запас (после test/restore),
    /// пока игрок не перезарядится (Clip1 &gt; 0).
    /// </summary>
    private readonly HashSet<nuint> _pendingReserve = [];

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
            _pendingReserve.Clear();
            LogInfo("Map started.");
        });

        // Как в InfiniteReserveAmmo: пишем ПОСЛЕ игровой логики, часто.
        RegisterListener<Listeners.OnServerPostEntityThink>(OnPostEntityThink);

        AddCommand("css_restorebullets_debug", "Print ammo restore debug info", OnDebugCommand);
        AddCommand("css_restorebullets_test", "Force restore active weapon reserve", OnTestCommand);

        LogInfo("Loaded v{Version} (hotReload={HotReload})", ModuleVersion, hotReload);
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundActive = true;
        _pendingReserve.Clear();
        LogInfo("Round started.");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundActive = false;
        _pendingReserve.Clear();
        LogInfo("Round ended.");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true })
            return HookResult.Continue;

        // Сразу после выстрела движок обновляет ammo — догоняем.
        AddTimer(0.01f, () => RestorePlayerWeapons(player), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true })
            return HookResult.Continue;

        AddTimer(0.05f, () =>
        {
            // После reload снимаем pending — обойма должна наполниться.
            ClearPendingForPlayer(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void OnPostEntityThink()
    {
        if (!Config.Enabled || !_roundActive)
        {
            _pendingReserve.Clear();
            return;
        }

        // Пока оружие в pending и обойма пуста — держим запас каждый тик
        // (иначе движок затирает запись обратно в 0).
        EnforcePendingReserves();

        var now = Server.CurrentTime;
        if (now < _nextCheckAt)
            return;

        // Чаще, чем раньше: 0.1с как InfiniteReserveAmmo.
        _nextCheckAt = now + Math.Min(Config.CheckIntervalSeconds, 0.10f);

        foreach (var player in Utilities.GetPlayers())
            RestorePlayerWeapons(player);
    }

    private void EnforcePendingReserves()
    {
        if (_pendingReserve.Count == 0)
            return;

        List<nuint>? remove = null;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, PawnIsAlive: true })
                continue;

            var ws = player.PlayerPawn?.Value?.WeaponServices;
            if (ws == null)
                continue;

            foreach (var handle in ws.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true })
                    continue;

                var key = (nuint)weapon.Handle;
                if (!_pendingReserve.Contains(key))
                    continue;

                // Игрок перезарядился — больше не держим.
                if (weapon.Clip1 > 0)
                {
                    remove ??= [];
                    remove.Add(key);
                    continue;
                }

                WriteReserve(weapon, ws, force: true);
            }
        }

        if (remove == null)
            return;

        foreach (var key in remove)
            _pendingReserve.Remove(key);
    }

    private void ClearPendingForPlayer(CCSPlayerController player)
    {
        var ws = player.PlayerPawn?.Value?.WeaponServices;
        if (ws == null)
            return;

        foreach (var handle in ws.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon is { IsValid: true })
                _pendingReserve.Remove((nuint)weapon.Handle);
        }
    }

    private void RestorePlayerWeapons(CCSPlayerController player)
    {
        if (player is not { IsValid: true, PawnIsAlive: true })
            return;

        var pawn = player.PlayerPawn?.Value;
        var ws = pawn?.WeaponServices;
        if (pawn is not { IsValid: true } || ws == null)
            return;

        foreach (var handle in ws.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon is not { IsValid: true })
                continue;

            TryRestoreWeapon(player, weapon, ws);
        }
    }

    private void TryRestoreWeapon(
        CCSPlayerController player,
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices ws)
    {
        if (!IsSupportedFirearm(weapon, out var weaponName, out var vdata))
            return;

        // Есть патроны в обойме — не трогаем (и не затираем оставшиеся 2–3 обоймы).
        if (weapon.Clip1 > 0)
            return;

        var reserve0 = GetReserve0(weapon);

        // Запас ещё есть — не трогаем.
        if (reserve0 > 0)
            return;

        var amount = GetRestoreAmount(vdata);
        if (amount <= 0)
            return;

        var before = GetReserve0(weapon);
        WriteReserve(weapon, ws, force: true);
        _pendingReserve.Add((nuint)weapon.Handle);

        var after = GetReserve0(weapon);
        var ammoType = (int)vdata.PrimaryAmmoType;
        var wsAmmo = ammoType >= 0 && ammoType < ws.Ammo.Length ? ws.Ammo[ammoType] : (ushort)0;

        LogInfo(
            "Restored {Player} {Weapon}: reserve {Before}->{After}, wsAmmo={WsAmmo}, amount={Amount}, asClips={AsClips}",
            player.PlayerName,
            weaponName,
            before,
            after,
            wsAmmo,
            amount,
            vdata.ReserveAmmoAsClips);

        // Если запись сразу стёрлась — fallback: кладём патроны прямо в обойму.
        if (after <= 0 && weapon.Clip1 <= 0)
        {
            weapon.Clip1 = vdata.MaxClip1;
            Utilities.SetStateChanged(weapon.As<CCSWeaponBase>(), "CBasePlayerWeapon", "m_iClip1");
            _pendingReserve.Remove((nuint)weapon.Handle);
            LogInfo(
                "Fallback clip fill {Player} {Weapon}: clip={Clip}",
                player.PlayerName,
                weaponName,
                weapon.Clip1);
        }
    }

    private static bool IsSupportedFirearm(
        CBasePlayerWeapon weapon,
        out string weaponName,
        out CBasePlayerWeaponVData vdata)
    {
        weaponName = weapon.GetWeaponName() ?? weapon.DesignerName ?? string.Empty;
        vdata = null!;

        if (string.IsNullOrEmpty(weaponName))
            return false;

        if (ExcludedWeapons.Contains(weaponName) || weaponName.StartsWith("weapon_knife", StringComparison.Ordinal))
            return false;

        var vd = weapon.VData;
        if (vd == null || vd.MaxClip1 <= 1)
            return false;

        vdata = vd;
        return true;
    }

    private static int GetRestoreAmount(CBasePlayerWeaponVData vdata)
    {
        // asClips: 1 обойма. Иначе — патроны на магазин.
        return vdata.ReserveAmmoAsClips ? 1 : vdata.MaxClip1;
    }

    private static int GetReserve0(CBasePlayerWeapon weapon)
    {
        var reserve = weapon.ReserveAmmo;
        return reserve.Length > 0 ? Math.Max(0, reserve[0]) : 0;
    }

    /// <summary>
    /// Как InfiniteReserveAmmo: пишем и m_pReserveAmmo, и m_iAmmo одним значением.
    /// </summary>
    private static void WriteReserve(
        CBasePlayerWeapon weapon,
        CPlayer_WeaponServices ws,
        bool force)
    {
        var vdata = weapon.VData;
        if (vdata == null)
            return;

        var amount = GetRestoreAmount(vdata);
        if (amount <= 0)
            return;

        var current = GetReserve0(weapon);
        if (!force && current >= amount)
            return;

        // Не уменьшаем чужой запас (2–3 обоймы).
        if (current > amount)
            return;

        var ammoType = (int)vdata.PrimaryAmmoType;

        var csWeapon = weapon.As<CCSWeaponBase>();
        var csVData = csWeapon.VData;
        if (csVData != null && csVData.PrimaryReserveAmmoMax < amount)
            csVData.PrimaryReserveAmmoMax = amount;

        var reserve = weapon.ReserveAmmo;
        if (reserve.Length > 0)
            reserve[0] = amount;

        if (ammoType >= 0 && ammoType < reserve.Length)
            reserve[ammoType] = amount;

        if (ammoType >= 0 && ammoType < ws.Ammo.Length)
            ws.Ammo[ammoType] = (ushort)Math.Clamp(amount, 0, ushort.MaxValue);

        Utilities.SetStateChanged(csWeapon, "CBasePlayerWeapon", "m_pReserveAmmo");
    }

    private void OnTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true })
        {
            command.ReplyToCommand("[RestoreBullets] Run in-game as alive player.");
            return;
        }

        var pawn = player.PlayerPawn?.Value;
        var ws = pawn?.WeaponServices;
        var weapon = ws?.ActiveWeapon.Value;
        if (pawn is not { IsValid: true } || ws == null || weapon is not { IsValid: true })
        {
            command.ReplyToCommand("[RestoreBullets] No active weapon.");
            return;
        }

        if (!IsSupportedFirearm(weapon, out var weaponName, out var vdata))
        {
            command.ReplyToCommand($"[RestoreBullets] Unsupported: {weaponName}");
            return;
        }

        var before = GetReserve0(weapon);
        WriteReserve(weapon, ws, force: true);
        _pendingReserve.Add((nuint)weapon.Handle);

        var after = GetReserve0(weapon);
        var ammoType = (int)vdata.PrimaryAmmoType;
        var wsAmmo = ammoType >= 0 && ammoType < ws.Ammo.Length ? ws.Ammo[ammoType] : (ushort)0;

        command.ReplyToCommand(
            $"[RestoreBullets] TEST {weaponName}: reserve {before}->{after}, wsAmmo={wsAmmo}, clip={weapon.Clip1}, amount={GetRestoreAmount(vdata)}, asClips={vdata.ReserveAmmoAsClips}, pending=1");

        // Проверка через кадр — не затёр ли движок.
        var handle = weapon.Handle;
        var slot = player.Slot;
        Server.NextFrame(() =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            var w = p?.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon.Value;
            if (w is not { IsValid: true } || w.Handle != handle)
                return;

            p?.PrintToChat($"[RestoreBullets] after-frame reserve={GetReserve0(w)} clip={w.Clip1}");
        });
    }

    private void OnDebugCommand(CCSPlayerController? player, CommandInfo command)
    {
        var target = player;
        if (target is not { IsValid: true })
        {
            target = Utilities.GetPlayers().FirstOrDefault(p => p is { IsValid: true, PawnIsAlive: true });
            if (target == null)
            {
                command.ReplyToCommand("[RestoreBullets] No alive players.");
                return;
            }

            command.ReplyToCommand($"[RestoreBullets] Using {target.PlayerName}");
        }

        DumpPlayerState(target, command.ReplyToCommand);
    }

    private void DumpPlayerState(CCSPlayerController player, Action<string> reply)
    {
        reply($"[RestoreBullets] enabled={Config.Enabled} roundActive={_roundActive} pending={_pendingReserve.Count}");

        var ws = player.PlayerPawn?.Value?.WeaponServices;
        if (ws == null)
        {
            reply("[RestoreBullets] no WeaponServices");
            return;
        }

        var active = ws.ActiveWeapon.Value;
        reply($"[RestoreBullets] active={active?.GetWeaponName() ?? active?.DesignerName ?? "none"}");

        foreach (var handle in ws.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon is not { IsValid: true })
                continue;

            var name = weapon.GetWeaponName() ?? weapon.DesignerName ?? "?";
            var vdata = weapon.VData;
            if (vdata == null)
            {
                reply($"  {name}: VData=null");
                continue;
            }

            var ammoType = (int)vdata.PrimaryAmmoType;
            var wsAmmo = ammoType >= 0 && ammoType < ws.Ammo.Length ? ws.Ammo[ammoType] : (ushort)0;
            var pending = _pendingReserve.Contains((nuint)weapon.Handle);
            var needs = weapon.Clip1 <= 0 && GetReserve0(weapon) <= 0 && vdata.MaxClip1 > 1;

            reply(
                $"  {name}: clip={weapon.Clip1} reserve0={GetReserve0(weapon)} wsAmmo={wsAmmo} asClips={vdata.ReserveAmmoAsClips} maxClip={vdata.MaxClip1} amount={GetRestoreAmount(vdata)} needs={needs} pending={pending}");
        }
    }

    private void LogInfo(string message, params object?[] args) =>
        Logger.LogInformation("[RestoreBullets] " + message, args);
}
