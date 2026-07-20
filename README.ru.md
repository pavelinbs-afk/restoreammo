# RestoreBullets

[English](README.md)

Плагин для **Counter-Strike 2** на [CounterStrikeSharp](https://docs.cssharp.dev/), который выдаёт **запасные патроны** (не обойму), когда у оружия полностью закончились боеприпасы. Работает до конца раунда.

## Возможности

- Выдаёт **1 запасную обойму** для оружия с системой обойм (Deagle, Elite, AK, AWP и т.д.)
- Обойма остаётся пустой — нужно нажать **R**, чтобы перезарядиться
- После использования запаса выдача повторяется, пока идёт раунд
- Для дробовиков (Nova, XM1014) выдаётся полный магазин патронов в запас (7–8 шт.)
- Не затрагивает нож, гранаты, C4, Zeus, healthshot

## Требования

- CS2 dedicated server
- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (API ≥ 80)
- .NET 8 SDK (для сборки)

## Установка

1. Соберите проект или скачайте `RestoreBullets.dll` из релиза
2. Скопируйте на сервер:

```
csgo/addons/counterstrikesharp/plugins/RestoreBullets/RestoreBullets.dll
```

3. Перезапустите карту или сервер

## Сборка

```bash
dotnet build RestoreBullets.csproj -c Release
```

Готовый файл:

```
bin/Release/net8.0/RestoreBullets.dll
```

## Конфигурация

Файл создаётся автоматически:

```
csgo/addons/counterstrikesharp/configs/plugins/RestoreBullets/RestoreBullets.json
```

```json
{
  "Enabled": true,
  "CheckIntervalSeconds": 0.25,
  "Debug": false,
  "ConfigVersion": 1
}
```

| Параметр | Описание |
|---|---|
| `Enabled` | Включить / выключить плагин |
| `CheckIntervalSeconds` | Как часто проверять игроков (сек., мин. 0.05) |
| `Debug` | Подробные логи в консоль сервера |

## Команды

| Команда | Описание |
|---|---|
| `css_restorebullets_debug` | Показать состояние патронов у игрока |
| `css_restorebullets_test` | Принудительно выдать запас активному оружию (из игры) |

## Как это работает

1. Игрок полностью расстреливает оружие: **обойма = 0**, **запас = 0**
2. Плагин добавляет **1 обойму в запас** (на HUD: `0 | 1`)
3. Игрок перезаряжается — патроны попадают в обойму
4. Цикл повторяется до `round_end`

Количество патронов берётся из **VData** оружия (`MaxClip1`), отдельный список для каждого ствола не нужен.

## Логи

При успешной выдаче в консоли сервера:

```
[RestoreBullets] Restored PlayerName weapon=weapon_deagle amount=1 clipAfter=0 reserveAfter=1 ...
```

## Лицензия

[MIT](LICENSE)

## Автор

pRfect
