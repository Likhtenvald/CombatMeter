# Milestone 1C — ручной runtime-тест DamageCommit

Конфигурация: **PC A — Listen Host; PC B — Client A**, оба Valheim 1.0.15 и plugin 0.2.0 через свои Thunderstore profiles. Dedicated server не нужен. Предыдущий probe, по сообщению пользователя, подтвердил измерение HP на victim owner; новые RPC ещё не проверены в игре.

## Подготовка

1. Закрыть Valheim. В Thunderstore на каждом PC выбрать используемый Valheim profile → Settings → Browse profile folder.
2. В `<profile>\BepInEx\plugins\DiagnosticDamageProbe\` заменить старую DLL на `outputs/build/Debug/netstandard2.1/CombatMeter.dll`. Оставить одну копию plugin, старую 0.1.0 рядом не хранить. BepInEx не переустанавливать, файлы Steam game root не менять.
3. В `<profile>\BepInEx\config\local.valheim.diagnosticdamageprobe.cfg` включить оба флага:

   ```ini
   [Diagnostics]
   EnableDiagnosticLogging = true
   EnableTransportDiagnosticLogging = true
   ```

4. Запускать вручную через Start modded. В обоих `<profile>\BepInEx\LogOutput.log` проверить `DamageProbeReady version=0.2.0`.
5. Host открывает multiplayer world, Client подключается. Проверить `DamageCommitSessionReady`: на Host IsHost=true, на Client false, записать PeerSessionId/SourceEpoch обоих.
6. Синхронизировать часы для удобства, но сопоставлять события **по EventId**, не времени/damage. Первые опыты — одиночные удары с паузами 3–5 секунд, без других combat mods и god/debug fly.

## Как определить owner

Не считать атакующего owner по умолчанию. В `DamageProbeEvent` смотреть VictimZDOID, VictimOwner и PeerSessionId; в Created SourcePeerId — наблюдавший owner. Для client-owned mob сначала Client входит в зону/spawn, Host подходит позже; для host-owned — наоборот. Это попытка, не гарантия: зафиксировать фактический owner по логам. Сценарий с неподходящим owner не засчитывать как проверку нужной ветки.

## A–H: основные сценарии

| Сценарий | Шаги | Ожидаемые Created → Accepted |
|---|---|---|
| A. Host → host-owned mob | Host один раз бьёт моба с подтверждённым host owner | Host Created Route=Local → Host Accepted Origin=Local с тем же EventId; remote Created/ACK для этого события нет |
| B. Client → host-owned mob | Client один раз бьёт того же host-owned моба | Host Created Local → Host Accepted Local; attacker PlayerID/name принадлежат Client |
| C. Host → client-owned mob | Подтвердить owner Client, Host наносит одиночный удар | **Client Created RemoteToHost → Host Accepted Remote → Client Acknowledged**; attacker — Host |
| D. Client → client-owned mob | Client наносит одиночный удар при owner Client | Client Created RemoteToHost → ровно один Host Accepted Remote |
| E. Mob → Host player | Дать мобу один раз ударить Host | Host Created Local → Host Accepted Local; victim player, attacker NPC |
| F. Mob → Client player | Дать мобу один раз ударить Client | Client Created RemoteToHost → Host Accepted Remote; victim Client player, attacker NPC |
| G. Lethal overkill | Ослабить моба до малого X HP, затем нанести сильный завершающий hit | HPAfter=0, Created и Accepted loss=X, а не raw damage; сверить EventId |
| H. Fire / poison | Один elemental hit по живучей цели, затем прекратить атаки и дождаться ticks; повторить отдельно для обоих эффектов | Каждый positive tick создаёт отдельный commit и Host Accepted; Burning/Poisoned без attacker остаются None/Unresolved, Player не угадывается |

Для A–D выполнить отдельно melee и обычный bow. При projectile запись соответствует применению урона, не выпуску стрелы. Промах не даёт positive event. Для H желательно проверить и host-owned, и client-owned цель: наиболее важен remote tick → Host.

Для damage taken сверить VictimIsPlayer=true и VictimPlayerID. Отсутствие attacker в fall/environment/DoT не должно мешать транспортировке: дополнительно выполнить одно падение с реальным HP loss на Client и проверить Host Accepted с None.

## Идентичные события и повторная доставка

1. Нанести два одинаковых по величине удара одной цели (если получается) или дождаться двух равных DoT ticks. У них должны быть **разные EventId**, оба приняты.
2. Created появляется один раз на исходное событие, не на каждую retry. При естественной потере ACK возможны Host Rejected Reason=Duplicate и Client Acknowledged Reason=Duplicate. **Accepted для этого EventId остаётся один.**
3. Не обязательно добиваться packet loss вручную: duplicate/lost request/lost ACK проверены unit simulations. Принудительный disconnect не эквивалент безопасному packet-loss тесту — он завершает session.

## Lifecycle и handoff

1. Дождаться `DamageCommitAcknowledged` для всех remote Created; затем Client выходит в меню и подключается снова. В новом SessionReady другой SourceEpoch, первый commit Sequence=1; Host принимает его, не путая с предыдущим epoch.
2. Host закрывает мир после доставки и открывает следующий. Оба peers получают новую session; первый positive event работает, duplicate registration exceptions отсутствуют.
3. Попробовать сменить owner живого моба: прежний owner уходит из active area, второй остаётся, выждать несколько секунд, нанести hit. Сверить смену SourcePeerId и один Accepted на commit. Если owner не сменился — отметить handoff как непроверенный.
4. Отдельно выйти Client при pending commit. Ожидаемый диагностический результат — `DamageCommitAbandoned`, если ACK не получен; это **не успешная проверка exactly-once доставки**. Проверить, был ли EventId принят Host до выхода. В новую session pending не переносится.
5. Выключить только `EnableDiagnosticLogging`, перезапустить: подробные DamageProbeEvent исчезают, Created/Accepted продолжаются. Затем проверить `EnableTransportDiagnosticLogging=false`: подробные transport logs исчезают, транспорт остаётся активным, но его invariant по отключённым логам доказать нельзя. Для финальной сверки вернуть true.

## Главный критерий: сопоставить EventId

После короткой серии дождаться ACK, сохранить копии обоих `BepInEx/LogOutput.log` **до нового запуска**. На каждый positive owner-side DamageProbeEvent должен приходиться один Created; оба записываются на том же observer (Created перед подробным ProbeEvent). На каждый Created из обоих логов — **ровно один Accepted в Host log**, с тем же EventId, loss, victim и attribution. Client не должен писать Accepted.

Можно проверить сохранённые логи read-only PowerShell-кодом; заменить два пути на реальные копии:

```powershell
$hostLog = 'C:\ProbeLogs\Host.log'
$clientLog = 'C:\ProbeLogs\Client.log'
function Read-ProbeEvents($path, $kind) {
    Get-Content -LiteralPath $path | ForEach-Object {
        $marker = $kind + ' '
        $offset = $_.IndexOf($marker, [StringComparison]::Ordinal)
        if ($offset -ge 0) { $_.Substring($offset + $marker.Length) | ConvertFrom-Json }
    }
}
$created = @(Read-ProbeEvents $hostLog 'DamageCommitCreated') + @(Read-ProbeEvents $clientLog 'DamageCommitCreated')
$accepted = @(Read-ProbeEvents $hostLog 'DamageCommitAccepted')
$ids = @($created.EventId) + @($accepted.EventId) | Sort-Object -Unique
$checks = @($ids | ForEach-Object {
    $eventId = $_
    [pscustomobject]@{
        EventId = $eventId
        Created = @($created | Where-Object EventId -EQ $eventId).Count
        Accepted = @($accepted | Where-Object EventId -EQ $eventId).Count
    }
})
$checks | Where-Object { $_.Created -ne 1 -or $_.Accepted -ne 1 } | Format-Table -AutoSize
"Created=$($created.Count) Accepted=$($accepted.Count)"
```

Пустой список mismatches и ненулевое равное число Created/Accepted — необходимое условие, **не единственная проверка**: сравнить также loss/IDs и отсутствие observation failures. При reconnect не приклеивать одну и ту же копию файла дважды. Не склеивать разные events по одинаковому damage/timestamp.

| Сценарий | Реальный owner | EventId / число событий | Created count | Host Accepted count | loss и attribution совпали | Итог |
|---|---|---|---|---|---|---|
| A–H, заполнить вручную | | | | | | |

Следующие markers требуют разбора и не позволяют объявить acceptance criterion выполненным: `DamageCommitAbandoned`, `DamageCommitDeliveryFailed`, `DamageCommitObservationRejected`, `DamageCommitTransportFailure`, `DamageProbeObservationLost`, `DamageProbeCaptureFailed`. Duplicate без второго Accepted нормален при retry. SourceCapacity/TooOld у легитимного теста, PendingCapacity или незавершённые ACK — failures.

Доказательство ограничено активной сессией и in-memory delivery. Crash/закрытие мира с in-flight commit не покрыты durable storage. Разработчик Valheim не запускал; результаты A–H должен предоставить пользователь. До их подтверждения следующий milestone не начинается.

