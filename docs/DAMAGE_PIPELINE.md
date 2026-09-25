# Valheim damage pipeline: статическое исследование

> Контекст Milestone 1C: пользователь сообщил об успешной runtime-проверке victim-owner HP delta в Listen Host + Remote Client. Принята модель доверенных участников кооператива: owner reports допустимы, anti-cheat не требуется, dedicated не целевой сценарий. Ниже сохранены исходные статические выводы и исходное строгое требование доверия; актуальный транспорт описан в `DAMAGE_COMMIT_TRANSPORT.md`.

Дата: 2026-09-24. Исследована локальная Windows Steam public-сборка **Valheim 1.0.15**, network version **40**, Steam build **25390630**. Combat Meter, UI, CombatTracker, EncounterManager и сетевой протокол не реализованы. Игра не запускалась; runtime-проверки не выполнялись.

## Главный вывод

Для измерения реально списанного здоровья оптимальная исходная точка — `Character.ApplyDamage(HitData, bool, bool, HitData.DamageModifier)`: owner-only наблюдение HP до/после вызова. Непосредственная запись происходит в `Character.SetHealth(float)`, который проверяет ownership и ограничивает отрицательное HP нулём.

**Owner-authoritative в Valheim не означает server-authoritative.** Обычный `Character.Damage` посылает `RPC_Damage` owner-у жертвы. Это может быть клиент; сервер тогда только маршрутизирует запрос. Даже если жертвой владеет сервер, входной `HitData` содержит рассчитанные отправителем значения, а `RPC_Damage` не пересчитывает оружие, допустимость попадания и не связывает `sender` с `m_attacker`. Поэтому ни server-only patch, ни сбор отчётов от owners сами по себе не удовлетворяют требованию недоверия к клиентским значениям.

Обычные fire/spirit/poison DoT теряют исходного атакующего: тики создают новые `HitData` без `SetAttacker`. Нужен отдельный механизм происхождения эффектов; его нельзя получить из одного конечного hook.

## 1. Материалы, воспроизводимость и границы доказательств

Проект на старте содержал только пустые `work/` и `outputs/`; собственных assemblies, исходников и bootstrap не было. Найдена установленная игра:

`C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed`

Основной объект исследования — **`assembly_valheim.dll`**, а не маленький `Assembly-CSharp.dll`. Версия подтверждена телом `Version.CurrentVersion = new GameVersion(1, 0, 15)`, build — локальным `steamapps/appmanifest_892970.acf`. Официальная [заметка о 1.0.15](https://www.valheimgame.com/news/patch-1-0-15/) используется только для сопоставления релиза; выводы об API получены из DLL.

| Файл | SHA-256 |
|---|---|
| assembly_valheim.dll (2 569 728 байт) | `59F53FB55D99D22A33E8ED094EEC8D21E9F133543BCE92BC3D80DCE44033ADB1` |
| assembly_utils.dll | `201E27467F8B1F899F9F2058B47D94A2D3532BCF9AF07A02EEE0AB16D4B12DD0` |
| UnityEngine.CoreModule.dll | `FBA3821AFB6867FE4D471DC50BF04BA21E6982B33AB6B5336B08A5C55119D990` |

Зависимости резолвились из соседнего Managed-каталога. Наличие следов Doorstop/BepInEx не доказывает работоспособный mod loader: в найденном `BepInEx` есть только `config`, core/Harmony DLL не обнаружены. Bootstrap для чтения IL не понадобился.

Декомпилятор: ILSpyCmd **8.2.0.7535**, .NET SDK **6.0.428**. Пакет загружен с NuGet в `work/ilspy.zip` и распакован в `work/ilspy/`. Полный локальный декомпилированный материал находится в `work/decompiled/`; это исследовательский scratch, не исходники будущего мода. Команда воспроизведения из корня проекта:

```powershell
dotnet work/ilspy/tools/net6.0/any/ilspycmd.dll `
  'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll' `
  -p -o work/decompiled
```

Ссылки `[E…]` ниже обозначают проверенные тела методов в следующем реестре. Номера строк относятся к этому выводу ILSpy, не к исходникам Iron Gate. Для повторной сборки ориентироваться на символы и SHA, поскольку строки меняются между версиями декомпилятора.

| ID | Декомпилированный файл, строки/методы | Что проверено |
|---|---|---|
| E1 | `Character.cs:2233–2402`, `Damage`, `RPC_Damage` | RPC, ранние выходы, модификаторы, split DoT |
| E2 | `Character.cs:2418–2491`, `ApplyDamage`; `3004–3032`, `GetHealth`, `SetHealth` | Финальная потеря HP и owner-запись |
| E3 | `HitData.cs:746+`, `Serialize`, `Deserialize`; `1048–1081`, `HaveAttacker`, `GetAttacker`, `SetAttacker` | Передача и разрешение attacker ZDOID |
| E4 | `HitData.cs:414–444`, `DamageTypes.ApplyArmor`; `915–1008`, `GetTotalDamage`, `ApplyResistance`, `ApplyArmor` | Armor, resistances, world-level добавка |
| E5 | `Humanoid.cs:256`, `CustomFixedUpdate`; `553`, `OnAttackTrigger`; `Attack.cs:607`, `OnAttackTrigger`, `847`, `FireProjectileBurst`, `1240`, `DoMeleeAttack` | Owner атакующего, создание hit |
| E6 | `Projectile.cs:255`, `FixedUpdate`; `408`, `Setup`; `DoAOE`, `IsValidTarget`, `OnHit` (hit build: 638–664) | Owner projectile, ranged hit, AoE |
| E7 | `Character.cs:2502–2566`; `SE_Burning.cs:24–85`; `SE_Poison.cs:24–66`; `StatusEffect.cs:97` | DoT pool, тики, утрата attacker |
| E8 | `Character.cs:810–845`, `CustomFixedUpdate`; `SEMan.cs:113+`, `Update`, `AddStatusEffect`, `OnDamaged` | Owner tick, status effects |
| E9 | `ZNetView.cs:227`, `IsOwner`; `314–334`, `HandleRoutedRPC`, обе `InvokeRPC`; `ZRoutedRpc.cs:120–208` | Маршрутизация и локальная доставка |
| E10 | `ZDO.cs`, `IsOwner`, `SetOwner`, `SetOwnerInternal`; `ZDOMan.cs:740`, `CreateNewZDO`; `881`, `Update`; `938–993`, `ReleaseZDOS`, `ReleaseNearbyZDOS`; `1142–1205`, `RPC_ZDOData` | Ownership, migration, репликация |
| E11 | `Humanoid.cs:1751`, `BlockAttack`; `SE_Shield.cs:51`, `OnDamaged`; `Player.cs:6132`, `IsPVPEnabled`, `SetPVP`; `743`, `SetPlayerID`, `GetPlayerID` | Block, щит, PvP, идентичность |
| E12 | `Character.cs:1106`, `UpdateHeatDamage`; `2766`, `UpdateGroundContact`; `SE_Smoke`, `SE_Wet`, `SE_Stats.UpdateStatusEffect`; `CharacterTimedDestruction.DestroyNow`; `Attack.cs:598` | Environment и прямые вызовы ApplyDamage |
| E13 | `DamageText.cs:146–194`, `ShowText`, `RPC_DamageText`; `ZNetScene.cs`, `FindInstance`, `CreateDestroyObjects` | Presentation и наличие объекта на peer |
| E14 | `Aoe.cs`, `CustomFixedUpdate`, `ShouldHit`, `CauseTriggerDamage`, `OnHit`, `Setup` | Исключения для trigger AoE и источника RPC |

## 2. Фактический путь применения damage

```text
attacker-owner: Humanoid.OnAttackTrigger → Attack.DoMeleeAttack
или projectile-owner: Projectile.FixedUpdate → OnHit / DoAOE
    → IDestructible.Damage → Character.Damage(hit)
    → victim.ZNetView.InvokeRPC("RPC_Damage", hit)
    → ZRoutedRpc (локально либо через server relay)
victim-owner:
    Character.RPC_Damage(sender, hit)
    → проверки / SEMan.OnDamaged / backstab / stagger / block
    → resistance / armor
    → отделение fire, poison, spirit
    → Character.ApplyDamage(оставшаяся immediate-часть)
    → Character.SetHealth
    → AddFireDamage / AddSpiritDamage / AddPoisonDamage
позднее victim-owner:
    Character.CustomFixedUpdate → SEMan.Update → SE_Burning / SE_Poison
    → Character.ApplyDamage(новый HitData без attacker) → SetHealth
```

`Damage` сам HP не изменяет: при валидном `ZNetView` вычисляет `m_weakSpot` по collider и вызывает RPC. `RPC_Damage` регистрируется в `Character.Awake`. Проверка `IsOwner` стоит в обработчике; перед ней есть debug-flight return и локальная статистика попаданий. Это не делает локальную статистику доказательством применённого damage. [E1]

Последовательность `RPC_Damage`:

1. Owner, HP/dead, teleport, cutscene и dodge проверки. Отдельная ветка stagger при `m_staggerMultiplier >= 100` может сработать раньше некоторых отказов.
2. Если attacker ZDOID задан, но объект не разрешился — return. Для player-жертвы проверяется PvP (см. ниже).
3. Для NPC attacker — difficulty и `Game.m_enemyDamageRate`; служебные записи attackers/kill modifiers не являются damage counter.
4. `SEMan.OnDamaged(hit, attacker)` может изменить hit: например, `SE_Shield.OnDamaged` накапливает absorbed damage и обнуляет damage через `ApplyModifier(0)`.
5. Backstab, двойной damage по staggered non-player, `BlockAttack`, pushback, status effect hash.
6. Weakspot/equipment/status resistances через `GetDamageModifiers` и `ApplyResistance`.
7. Armor игрока (`GetBodyArmor`, durability) либо world-level armor non-player.
8. Fire/poison/spirit запоминаются отдельно и обнуляются в hit. Затем `ApplyDamage`, затем добавление DoT и frost/lightning effects. [E1, E4, E11]

Важно для этой версии: `DamageTypes.ApplyArmor` распределяет armor attenuation по blunt/slash/pierce **и fire/frost/lightning/poison/spirit/nonPlayer**. Generic `m_damage`, chop и pickaxe в эту сумму не входят. Нельзя переносить предположения об armor из старых версий. Формула: при `ac < dmg/2` результат `dmg-ac`, иначе `Clamp01(dmg/(4*ac))*dmg`. [E4]

## 3. Где измерять фактическую потерю HP

В `ApplyDamage` ещё применяются финальные множители: для non-player — `GetDifficultyDamageScaleEnemy` и `Game.m_playerDamageRate`, для player — `Game.m_localDamgeTakenRate` (написание символа именно такое). Есть return для debug flying, dead, teleport, cutscene, cinematics и для итогового damage `<= 0.1f`. Далее из HP вычитается `hit.GetTotalDamage()`, god/ghost mode оставляет 1 HP при смертельном ударе, и вызывается `SetHealth`. [E2]

Рекомендуемая метрика: **effective HP loss**, `max(0, hpBefore - hpAfter)`, для валидного owner-объекта. Пример: при 12 HP и рассчитанных 100 damage записываются 12, а не 100. Для обычного принятого hit так учитываются уже выполненные block/armor/resistances, финальные множители и clamp. Не нужно повторять формулы armor в meter.

Prefix должен сохранять в per-invocation `__state` HP, victim ZDOID, owner/session и снимок attribution до изменения hit. Postfix читает HP снова; нулевой delta не записывается. `ApplyDamage` сам не содержит owner guard, поэтому фильтр owner необходим и в наблюдателе. `SetHealth` откажет в записи на non-owner, но это не повод считать прочие side effects методом защиты. [E2]

Граница точности: Prefix/Postfix измеряет суммарное изменение за весь вызов. В конце идут виртуальный `OnDamaged`, delegate `m_onDamaged`, effects и другие hooks. Сторонний мод/подписчик может лечить, рекурсивно повреждать или уничтожать объект. Для точного отдельного commit предпочтительна будущая более узкая инструментализация непосредственно вокруг `SetHealth` внутри `ApplyDamage`, либо scoped observer `SetHealth` со стеком контекстов. Нельзя суммировать одновременно внешний delta и вложенный delta. Исключения и Harmony-порядок требуют отдельной проверки и очистки контекста через Finalizer.

`m_onDamaged(totalDamage2, attacker)` получает рассчитанное значение, не clamped HP loss. `DamageText` получает `totalDamage`, снятое **до** финальных множителей `ApplyDamage`, и также не ограничено оставшимся HP. Его RPC broadcast не подходит для статистики. `HitData.GetTotalDamage` дополнительно может добавлять world-level base damage при NPC attacker; сумма полей damage не всегда равна его результату. [E2, E4, E13]

`SetHealth` глобально не равен damage: используется также healing, `UseHealth`, изменением max health и инициализацией. Расход HP как ресурса следует заранее отделить от боевого урона. [E2]

## 4. Атакующий игрок

`HitData.m_attacker` — **ZDOID персонажа**, не Steam ID, не player profile ID и не обязательно текущий owner peer ID. `SetAttacker(character)` записывает `character.GetZDOID()`; `Serialize/Deserialize` переносят его по сети. `GetAttacker()` ищет локальный instance через `ZNetScene.FindInstance`, затем `GetComponent<Character>()`. [E3]

Обычный путь идентификации: `hit.GetAttacker() is Player player`, затем `player.GetPlayerID()` для игрового profile ID и `GetPlayerName()` только для подписи. Profile ID хранится в ZDO; это не доказанная аутентифицированная account identity. Для доверенной системы нужна серверная привязка соединения к player ZDO/profile, проверенная отдельно. Нельзя группировать игроков по имени. [E11]

Различать три состояния: `m_attacker == None`; непустой ID, разрешённый в NPC/Player; непустой ID без локального instance. Последнее — **unresolved**, а не environment. В `RPC_Damage` такой hit отклоняется; прямой `ApplyDamage` этой проверки не делает. Сохранение ID заранее полезно при смерти/disconnect атакующего, но не восстанавливает уже утраченный DoT provenance. [E1–E3]

## 5. Melee

`Humanoid.OnAttackTrigger` требует valid view и ownership атакующего, затем вызывает `Attack.OnAttackTrigger`. `DoMeleeAttack` на этой стороне делает sphere/ray casts, фильтрует friendly/PvP/dodge, объединяет точки попадания через `AddHitPoint`, создаёт `HitData`, берёт damage оружия, skill factor, multi-hit/chain modifiers, задаёт attacker и вызывает `IDestructible.Damage`. Это запрос на hit; окончательное списание HP выполняется owner-ом жертвы. [E5]

Один swing может поражать несколько целей; дополнительные colliders не следует автоматически считать независимыми попаданиями. У weapon/attack есть параметры multi-hit и специальные случаи. Учёт по финальным HP commit естественно отделяет реальные применения от raycast-кандидатов. Patch `DoMeleeAttack` пригоден для диагностики/контекста, но не для финального damage и не для доказательства честности клиента.

## 6. Projectiles и bows

`Attack.FireProjectileBurst` формирует damage оружия и ammo, skill/draw modifiers, задаёт attacker и передаёт hit в `IProjectile.Setup`. `Projectile.Setup` сохраняет `m_owner` (персонаж стрелка), damage и параметры; это **другой смысл owner**, чем сетевой `m_nview.IsOwner()`. [E5, E6]

`Projectile.FixedUpdate` после обновления rotation выходит на non-owner; движение/collision damage идут у network owner projectile. В `OnHit` создаётся новый hit, `m_ranged=true`, `SetAttacker(m_owner)`, затем `destructible.Damage(hit)`. `DoAOE` тоже создаёт hit с attacker и имеет набор уже затронутых объектов. Дальше действует тот же victim-owner pipeline. Возможны burst, дочерние projectiles, mid-flight AoE и повторные интервальные hits; «одна стрела = одно событие» не универсально. [E6]

На обычном spawn network ownership сначала локально у создавшего ZDO peer (`ZNetView.Awake` → `ZDOMan.CreateNewZDO`). Нельзя из этого заключать, что `m_owner` автоматически восстанавливается после миграции projectile: в исследованном `Setup` это локальная Character-ссылка. Перелёт между зонами, despawn стрелка и смена ownership требуют runtime-теста. [E6, E10]

AoE нельзя безоговорочно свести к owner атакующего: `Aoe.CustomFixedUpdate` проверяет собственный view, а trigger-ветка `OnHit` фильтрует `Character.IsOwner` жертвы; существуют AoE без собственного view. Поэтому RPC `sender` не всегда shooter peer, даже при честной игре. `Aoe.OnHit` задаёт attacker из `m_owner`, а `m_ignorePVP` может включаться для self-hit или prefab-настройкой. [E14]

## 7. Elemental и DoT

| Тип | Применение | Attribution |
|---|---|---|
| Frost / lightning | Компоненты остаются в immediate `ApplyDamage`; затем добавляются соответствующие status effects | В immediate hit сохраняется attacker |
| Fire / spirit | После resistance/armor исключаются из immediate hit, добавляются в `SE_Burning` соответствующего status hash | Новые тики `HitType.Burning` не содержат attacker |
| Poison | Исключается из immediate hit, передаётся `SE_Poison.AddDamage` | Новые тики `HitType.Poisoned` не содержат attacker |
| Smoke | `SE_Smoke.UpdateStatusEffect` прямо вызывает `ApplyDamage` | Без attacker, `HitType.Smoke` |
| SE_Stats / Wet | Отрицательные health ticks / water создают новый hit через `Damage` | Без attacker в исследованных телах |

`SE_Burning` суммирует остаток fire/spirit pool с новым damage, пересчитывает damage per tick и сбрасывает время; при пустом pool слишком малый новый tick (`<0.2`) отвергается. Wet ускоряет истечение эффекта. `SE_Poison` **не суммирует каждое попадание**: заменяет pool только если новый damage `>= m_damageLeft`, рассчитывает TTL и per-hit заново. Default interval в коде 1 секунда, но реальные TTL/параметры могут задаваться assets. [E7]

`StatusEffect.SetAttacker` в базовом классе пустой. Ветка `m_statusEffectHash` в `RPC_Damage` вызывает его, но `SE_Burning` и `SE_Poison` не переопределяют и не сохраняют attacker. Наличие этого вызова не доказывает attribution DoT. Для сравнения `SE_Harpooned` действительно переопределяет SetAttacker; переносить это поведение на все SE нельзя. [E7]

Обычные DoT ticks идут из `Character.CustomFixedUpdate` только owner-веткой через `SEMan.Update`; они **обходят `RPC_Damage`**, повторные block/resistance/armor там не выполняются, но финальные modifiers `ApplyDamage` выполняются. [E7, E8]

Для будущей атрибуции нужно наблюдать принятые добавления pool внутри контекста исходного `RPC_Damage`, сохранять вклад и связывать его с конкретным SE/tick. Last attacker жертвы или `m_lastHit` непригодны: их перезаписывают другие hits, а сам tick записывает `m_lastHit` без attacker. Fire/spirit могут смешивать нескольких игроков и environment, poison может игнорировать более слабое добавление. Пропорциональное распределение pool либо last-contributor policy — продуктовая конвенция, не восстановленный факт vanilla. Нельзя считать upfront DoT amount и затем ticks второй раз. Миграция pool/provenance между owners отдельно не доказана.

## 8. PvP и источники без игрока

`RPC_Damage` отвергает hit, когда жертва Player, её PvP выключен, attacker разрешён в Player и `m_ignorePVP == false`. Это именно проверка PvP **жертвы**. Обычные melee/projectile дополнительно фильтруют friendly hit по PvP атакующего на стороне генерации. `Player.IsPVPEnabled` на owner читает `m_pvp`, на остальных — ZDO `s_pvp`. Финальный HP delta включает block, armor и другие проверки жертвы. [E1, E5, E6, E11]

`m_ignorePVP` сериализуется внутри hit. В `RPC_Damage` не найдено доказательства, что отправителю разрешено его выставлять; серверная доверенная система должна валидировать допустимые исключения. Прямые DoT `ApplyDamage` не повторяют PvP check; последствия переключения PvP во время горения нужно тестировать. Нельзя получить полное доверенное PvP на vanilla client-owned player, просто установив observer на dedicated server.

Environment классифицировать по **контексту + attacker state + `HitType`**, а не по damage type:

- Fall: `Character.UpdateGroundContact` создаёт generic damage после `SEMan.ModifyFallDamage`, `HitType.Fall`, без attacker.
- Lava/ocean heat: `UpdateHeatDamage`, generic damage, `AshlandsLava` / `AshlandsOcean`, без attacker.
- Smoke, water, SE ticks: пути в таблице выше. `Player` также создаёт `Drowning` / `EdgeOfWorld` hits.
- `Attack` self-destruction и `CharacterTimedDestruction.DestroyNow` вызывают `ApplyDamage` напрямую без player attacker; не считать их нанесённым игроком damage.
- Burning/Poisoned без attacker может быть **игроком инициированным DoT**. Null attacker не доказывает environment.
- Игрок, сваливший дерево, построивший ловушку или приручивший существо, не становится автоматически `hit.GetAttacker()`. Attribution косвенных источников требует отдельного, явно заданного правила; все prefab-сценарии здесь не доказаны.

`HitType` — сериализуемая метка, полезная для классификации, но не удостоверение источника и не anti-cheat boundary. Для PvE meter отдельно определить политику self/PvP/tamed/structures; `Character` не покрывает весь `IDestructible` мир. [E3, E12]

## 9. Multiplayer ownership, RPC и доверие

`ZDO.SetOwnerInternal(uid)` выставляет локальный owner-флаг как `uid == ZDOMan.GetSessionID()`. `ZNetView.IsOwner()` использует ZDO. Это session peer identity, а не роль сервера и не immutable creator часть ZDOID. [E9, E10]

`ZNetView.InvokeRPC(string, ...)` выбирает **`m_zdo.GetOwner()`**. В отличие от него, overload `ZRoutedRpc.InvokeRoutedRPC(string, ...)` без target ZDO обращается к server peer. Путать эти два API нельзя. [E9]

| Ситуация | RPC маршрут | Где списывается HP |
|---|---|---|
| Инициатор уже owner жертвы | Локальный `HandleRoutedRPC` | На этом peer, без обязательного сетевого round trip |
| Клиент A бьёт объект owner B | A → server relay → B | На B |
| Клиент A бьёт server-owned объект | A → server | На сервере, если есть instance |
| Host владеет жертвой | Локально или remote → host | На host; это частный случай совмещения ролей |
| Owner ID = 0 | В routed layer `0` означает everybody | Доставка может быть шире, но `RPC_Damage` откажет peer без ownership; опасная граница handoff |

`ZRoutedRpc.RPC_RoutedRPC` десериализует envelope; исполняет локально только при target=self/everybody, а server пересылает remote-target. `HandleRoutedRPC` требует ZDO и локальный `ZNetView` instance. Серверное наличие реплики ZDO не означает наличие Character и вызов `ApplyDamage`. `ZNetScene.CreateDestroyObjects` привязан к reference position/simulation distance. [E9, E13]

`ZDOMan.Update` вызывает server-side `ReleaseZDOS`; `ReleaseNearbyZDOS` рассматривает persistent objects вокруг local reference position и peer positions, снимает/назначает ownership по active area. Поэтому моб может принадлежать клиенту; это не статическое «все NPC на сервере». `ZNetView.ClaimOwnership` и создание объектов тоже влияют на owner. Частота release-проверки в коде — после накопления более 2 секунд. [E10]

**Что именно не защищено найденным pipeline:**

- `Character.RPC_Damage(long sender, HitData hit)` не использует `sender` для аутентификации attacker или проверки damage. Он применяет переданный payload с игровыми модификаторами, не пересобирает его из доверенного inventory/skill/attack state.
- В исследованном `ZRoutedRpc.RPC_RoutedRPC` sender берётся из envelope, нет сопоставления `m_senderPeerID` с аргументом `ZRpc rpc`. Это локальный вывод об этом слое, не заявление о проверенном exploit всего Steam/PlayFab transport.
- `ZDOMan.RPC_ZDOData` проверяет известность peer и revisions, принимает owner/data и десериализует ZDO. Это репликация состояния, а не повторный серверный расчёт HP hit. Даже наблюдение server-side ZDO delta не даёт историю отдельных событий или доверенную attribution: несколько damage/heal могут слиться между репликациями.
- Подпись/sequence пользовательского отчёта от модифицируемого client-owner помогает доставке и dedup, но не доказывает истинность числа.

Практическое различие: «клиент не присылает отдельную произвольную статистику» достижимо серверным наблюдением **доверенной симуляции**; «клиент не может подделать входной игровой damage» дополнительно требует серверной валидации самого боевого протокола. Текущий vanilla pipeline не обеспечивает второе и не гарантирует серверное выполнение первого.

## 10. Двойной учёт и потеря событий

При стабильном ownership обычный hit не применяется на каждом peer: RPC направлен одному owner, handler проверяет owner, HP записывает owner. Репликация HP не вызывает автоматически `Character.ApplyDamage` на наблюдателях. Это аргумент против неизбежного double count, но не доказательство exactly-once при всех handoff/модах. [E1, E2, E9]

Риски двойного учёта:

- Суммирование `Damage`, `RPC_Damage`, `ApplyDamage` и `SetHealth` как разных событий одного hit.
- Одновременная регистрация одного события на host через локальный observer и через отчёт, дубли/повторная доставка собственной телеметрии.
- Подписка на broadcast `RPC_DamageText`: каждый получатель видит presentation того же hit.
- Upfront elemental damage плюс последующие DoT ticks; вложенный damage и внешний HP delta.
- Нестабильное ownership во время синхронизации; сопоставление sender с owner по слишком позднему состоянию.

Также возможны пропуски: target owner изменился между отправкой и доставкой, Character/attacker instance отсутствует, projectile owner потерян, status effect state не перенесён. В `RPC_Damage` нет retry/reroute после отказа `!IsOwner`. У `HitData` нет стабильного damage-event ID. `RoutedRPCData.m_msgID` существует, но в просмотренном receive/dispatch нет dedup по нему; DoT вообще не идёт через routed hit RPC. Нельзя дедуплицировать по damage+timestamp: одинаковые реальные hits допустимы. Для будущего доверенного журнала нужны отдельные event IDs/sequence и ownership epoch с определёнными правилами handoff.

## 11. Harmony patch candidates

Это оценка точек перехвата, а не реализованные patches. Сигнатуры относятся к указанному SHA.

| Candidate | Плюсы | Минусы | Multiplayer implications / решение |
|---|---|---|---|
| `Character.Damage(HitData)` Prefix/Postfix | Исходный hit, attacker, классификация запроса | Только отправка; Postfix не означает завершённое удалённое списание; нет direct DoT | На инициаторе, который не обязан быть server/жертва; только диагностика |
| `Character.RPC_Damage(long, HitData)` Prefix/Postfix | Контекст sender, hit, проверки, elemental split | Prefix до принятия; Postfix delta не покрывает будущие ticks; hit мутирует | На recipient, owner guard в original; полезен для контекста, **не** единственный счётчик |
| `Character.ApplyDamage(HitData, bool, bool, DamageModifier)` Prefix/Postfix | Объединяет immediate и прямые DoT; доступен HP delta | Нет собственного owner guard; вход до final multipliers; callbacks/reentrancy; DoT без attacker | **Основная рекомендуемая точка измерения**, с valid+owner guard; server-only без смены архитектуры неполон |
| Transpiler `ApplyDamage` вокруг `SetHealth(float)` | Изолирует фактический commit до callback side effects | Привязка к IL, конфликт transpilers, проверка ожидаемого call site после обновлений | Точнее для сложной mod-среды; ownership/trust проблем не решает |
| `Character.SetHealth(float)` Prefix/Postfix в damage scope | Видит clamp и реальную ZDO-запись | Нет HitData; глобально смешивает heal/resource/max HP; нужен стек контекста | Запись owner-only; scoped вариант альтернативен transpiler, не второй независимый счётчик |
| `OnDamaged(HitData)` / `m_onDamaged` | После SetHealth, есть hit или attacker | Виртуальные overrides; delegate не Harmony method; значение не clamped loss; пропускает ранние return | Owner-context vanilla; лишь дополнительный сигнал |
| `Character.AddFireDamage(float, short)`, `AddSpiritDamage(float, short)`, `AddPoisonDamage(float, short)` + `SE_Burning.AddFireDamage/AddSpiritDamage`, `SE_Poison.AddDamage` | Видят создание/изменение pool, в RPC scope можно сохранить источник | Нет attacker в аргументах; poison replacement, смешение contributors; применение pool не равно HP loss | Дополнительные attribution hooks на victim-owner; перенос при handoff не решён |
| `SE_Burning.UpdateStatusEffect(float)`, `SE_Poison.UpdateStatusEffect(float)` | Контекст конкретного тика/SE | Нет vanilla attacker; считать HP второй раз нельзя | Owner update; обрамлять вызов ApplyDamage контекстом эффекта |
| `Attack.DoMeleeAttack`, `FireProjectileBurst`; `Projectile.Setup/OnHit/DoAOE`; `Aoe.OnHit` | Источник оружия/projectile и generation context | Предварительные значения, разные ветки, клиентские collision/skill | Диагностика/будущая валидация, не authoritative damage counter |
| `ZRoutedRpc.RPC_RoutedRPC`, `ZNetView.HandleRoutedRPC` | Наблюдение маршрута и envelope | Запрос ещё не applied, локальный/DoT path может обходить сетевой receive | Server relay не равен server simulation; не суммировать payload |
| `ZDOMan.RPC_ZDOData` / health replication | Сервер видит некоторое состояние client-owned objects | Coalescing damage/heal, нет hit attribution, недоверенный publisher | Не эквивалент trusted damage source |
| `DamageText.ShowText/RPC_DamageText` | Удобно визуально сравнить | Presentation, rounding, не final/clamped HP, broadcast | Отвергнуть для статистики |

## Recommended approach

1. **Зафиксировать требование доверия до реализации.** В исследованной версии не найден готовый серверный hook, который одновременно покрывает все PvE/PvP/DoT и гарантирует независимость от клиентского damage payload. Не называть vanilla owner reports server-authoritative.
2. Для следующего этапа диагностики выбрать owner-filtered `Character.ApplyDamage` Prefix/Postfix, со снимком HP и идентичностей. При необходимости точного commit использовать scoped `SetHealth` или проверяемый transpiler вокруг его вызова. Учитывать только positive effective HP loss и иметь один источник записи каждого commit.
3. `RPC_Damage` использовать для контекста принятого hit и связи elemental pool с исходным игроком; `SE_*` — для attribution тиков. До появления достоверного provenance показывать эти события как unknown/unattributed, а не угадывать last attacker. Правило распределения смешанного DoT утвердить отдельно.
4. Для строгого режима рассматривать **серверное владение и активную симуляцию отслеживаемых целей плюс серверную валидацию входных hits**: source identity, разрешённые типы атаки, weapon/ammo/skill, cadence, spatial plausibility, PvP flags, replay. Доверенность inventory/skills/positions также должна быть решена, иначе пересчёт использует недоверенные исходные данные. Простого `SetOwner(server)` недостаточно: необходимы instance lifecycle, AI/physics/SE simulation и запрет неконтролируемого ownership/data overwrite. Это отдельное изменение multiplayer-архитектуры, здесь не реализованное и не доказанное работоспособным.
5. Для PvP особенно учитывать client-owned player simulation. При невозможности перенести/валидировать её строгий режим должен честно ограничить покрытие. Режим доверенных участников с отчётами victim-owner возможен как иной контракт, но не соответствует исходному требованию защиты от модифицируемого клиента.

### Что статически не доказано: обязательные runtime-тесты

Нужны dedicated server + минимум два клиента, затем отдельный listen-host сценарий. Логировать роль peer, session ID, victim ZDOID, owner до/после, RPC sender, attacker ID/resolution, hit type, HP до/после и контекст direct/DoT; сверять журналы по всем peers. Диагностический plugin на этом этапе не создан.

| Проверка | Что установить экспериментально |
|---|---|
| A атакует моба owner A/B/server | Где реально выполняются hooks, где только relay; наличие Character на dedicated server |
| Melee, несколько colliders/целей, combo, burst arrows, projectile AoE | Число HP commits, отсутствие ложного dedup и повторного учёта |
| Armor, все resistance уровни, weakspot, block/parry, stagger/backstab, SE_Shield | Совпадение delta с фактическим HP при разных world/difficulty modifiers |
| Damage <=0.1, immunity/dodge, teleport/cutscene/cinematics, god/ghost, lethal overkill | Нулевые события, clamps и отказы; корректность выбранного HP sampling |
| Fire/spirit/poison одного и двух игроков, повторный слабый/сильный poison, wet, cleanse | Реальные prefab TTL/interval, pool replacement, truncation и политика attribution |
| Смешанный environment+player DoT | Невозможность автоматического attribution без ledger и корректная unknown-категория |
| Смерть/disconnect стрелка до попадания и между DoT ticks | Разрешение attacker, отказы RPC, projectile `m_owner`, сохранение provenance |
| Вход/выход из active area, teleport, disconnect owner во время боя/полёта/DoT | Потери/дубли на ownership handoff, судьба SE pools и локальных ссылок |
| PvP оба on/off, только одна сторона on, переключение после начала DoT | Реальное взаимодействие generation/receiver checks и `m_ignorePVP` |
| Fall, drowning, smoke, lava/ocean, self-cost, timed destruction, traps/tamed/tree | Покрытие классификатора и границы косвенного player attribution |
| Контролируемый модифицированный тестовый клиент | Может ли изменить hit amount/attacker/ignorePVP/envelope/ZDO; точные transport ограничения. Это проверять только в собственной тестовой сессии |
| Server ownership prototype | Активная симуляция удалённых зон, AI/physics/SE, производительность, отсутствие обратного перехвата ownership |
| BepInEx/Harmony bootstrap и сторонние mods | Совместимость текущего loader с Unity/Valheim 1.0.15, порядок patches, healing/reentrant callbacks, exceptions/despawn |
| Сетевые условия и платформы | Steam/PlayFab/crossplay, latency/disconnect, доставка и фактические границы exactly-once |

Декомпиляция доказывает control flow конкретной DLL. Она не доказывает состав prefab assets, runtime patch ordering, фактическое распределение ownership в конкретной сессии, доступность всех объектов на сервере, отсутствие транспортных проверок вне просмотренных методов или безопасность будущей серверной архитектуры.
