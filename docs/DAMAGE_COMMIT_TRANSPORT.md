# Milestone 1C — DamageCommit Transport

## Статус и контракт

Цель — **Listen Host + Remote Clients** в Valheim 1.0.15 с доверенными участниками. Это не anti-cheat. По сообщению пользователя предыдущий Diagnostic Damage Probe прошёл runtime-проверку HP delta и victim ownership в обычной MP-сессии. Новый транспорт проверяется managed tests и сборкой; его реальный сетевой runtime ещё не проверен. Dedicated server не входит в milestone.

Нет серверного пересчёта weapon/armor/resistance/skills/позиции/cadence/PvP/inventory и фильтрации для будущей статистики. NPC/None/Unresolved, damage taken и DoT передаются наравне с direct Player damage. Attacker attribution сохраняется из Prefix; last-attacker эвристик нет.

## Измерение и единый путь

```text
существующий Character.ApplyDamage Prefix / Postfix
    valid Character + ZNetView + victim owner
    HPBefore / HPAfter → max(0, before - after) > 0
    Plugin.Publish → CommitSession.Observe → DamageCommitCreated
        Host:   ProcessDamageCommit(commit, ownPeer, Local)
        Client: bounded outbox → directed routed RPC → Host
                    ProcessDamageCommit(commit, sender, Remote)
                              ↓
                         CommitAcceptor.Process
                         Accepted / rejection
```

`ApplyDamagePatch` не создаёт второго observer. Из уже снятого Prefix snapshot дополнительно сохраняется `DamageFacts` только со значениями. Postfix после прежних valid/session/owner проверок вызывает `Publish` один раз при positive finite delta. Lethal overkill 6 HP → 0 даёт commit loss=6 независимо от raw hit. `HitTotalDamageBefore` остаётся только в прежнем диагностическом логе, по сети не передаётся.

Harmony `__state` изолирован для каждого invocation. Finalizer и shared stack отсутствуют. Внешний delta при reentrant ApplyDamage может включать внутренний — как в probe; разные вызовы не дедуплицируются. Transport dedup касается **повторной доставки того же EventId**, а не разных вызовов игры. Если original выбрасывает исключение, Postfix не гарантирован: такой сценарий не покрывается обещанием успешного наблюдения.

`CommitSession.ProcessDamageCommit` — canonical host method. И local, и remote используют его и один `CommitAcceptor`. Единственный результат обработки — принятие/отклонение и диагностическая запись. Нет суммы damage, истории боя или иных статистических результатов.

## EventId и lifetime

Формат: `<SourcePeerId>/<SourceEpoch GUID N>/<LocalSequence>`.

- SourcePeerId — `ZDOMan.GetSessionID()` текущего peer, не PlayerID/SteamID.
- SourceEpoch — новый `Guid.NewGuid()` при создании transport session.
- Sequence начинается с 0; **первый созданный commit получает 1**. Увеличение на Unity main thread, checked overflow не допускает wrap/reuse.
- Retry отправляет прежний commit/EventId. Одинаковые loss/victim/attacker/HitType с разными Sequence — отдельные события.
- Host session создаётся в своём мире; client session — когда доступен ready server peer. Новый ZNet/ZRoutedRpc, смена peer UID/роли, выход в меню, disconnect/reconnect создают новую session/epoch.
- При reconnect на тот же мир даже с повторно использованным peer ID новый epoch предотвращает collision sequence=1. Host хранит старое replay window до закрытия своего мира.
- Pending queue старой client session **не переносится** в новый мир/подключение. Каждый незавершённый commit получает `DamageCommitAbandoned`: отсутствие ACK может означать как отсутствие доставки, так и уже принятую запись с потерянным ACK.

## RPC и доставка

```text
ZRoutedRpc.Register<ZPackage>("CombatMeter.DamageCommit.v1", handler)
ZRoutedRpc.Register<ZPackage>("CombatMeter.DamageCommitAck.v1", handler)
Client: ZNet.GetServerPeer().m_uid → InvokeRoutedRPC(target, CommitRpc, ZPackage)
Host:   remote sender           → InvokeRoutedRPC(sender, AckRpc, ZPackage)
```

Target всегда явный и ненулевой. **Everybody/target=0 не используется.** Host-local event вызывает processing напрямую, без RPC даже себе. Raw commits не broadcast-ятся; клиенту возвращается только ACK. По локальному decompiled API `GetServerPeer()` возвращает ready peer лишь при Connected; overload с explicit peer использует `ZDOID.None` для global RPC. Overload без target не выбран: при отсутствии server peer его внутренний `GetServerPeerID()` может вернуть 0.

Client outbox — FIFO, максимум **1024 commits**, один unacknowledged head на wire (stop-and-wait). Первый send сразу после observe; повтор head не чаще **1 раза в секунду**, время по Stopwatch, без TTL. ACK освобождает head, следующий отправляется в Update. Новые события остаются в bounded queue. Это удерживает outstanding EventId в dedup window даже при многократной потере ACK. Throughput ограничен round-trip/Update; компромисс для диагностического milestone.

ACK содержит EventId и результат. Accepted и Duplicate завершают доставку успешно: Duplicate означает, что canonical acceptance уже произошёл раньше. Другие результаты дают `DamageCommitDeliveryFailed`. Wrong/stale/out-of-order ACK не удаляет head; adapter принимает ACK лишь от текущего host peer. Нет plugin handshake: при отсутствии совместимого handler очередь ждёт ACK и повторяет head. Оба peers обязаны использовать эту build.

Переполнение очереди не вытесняет старые данные: новое наблюдение получает Warning `DamageCommitObservationRejected / PendingCapacity`, до выделения EventId. Это **провал runtime acceptance test**, а не допустимая скрытая потеря. Идентичные повреждения не схлопываются ради экономии места.

## Протокол v1

Little-endian BinaryWriter/BinaryReader в `ZPackage(byte[])`. Предельный payload **1024 bytes**, до копирования GetArray проверяется ZPackage.Size. Decoder отвергает unsupported version, truncation, trailing bytes, invalid boolean, oversized/invalid UTF-8 names. Никаких BinaryFormatter, Character/Player/HitData/ZNetView или registry-dependent ZDOID по сети нет.

| Поле по порядку | Wire type |
|---|---|
| ProtocolVersion | byte = 1 |
| SourcePeerId | Int64 |
| SourceEpoch | 16 bytes, .NET Guid.ToByteArray |
| Sequence | Int64 > 0 |
| VictimZDOID creator / object | Int64 + UInt32 (значения UserID/ID) |
| VictimIsPlayer | strict byte bool |
| VictimPlayerID | bool has-value + optional Int64 |
| AttackerClass | byte: None=0, Player=1, NPC=2, Unresolved=3 |
| AttackerPlayerID | bool has-value + optional Int64 |
| HitType | byte, vanilla enum numeric value |
| EffectiveHpLoss | Single |
| Timestamp | Int64 UTC ticks, снят при создании commit |
| VictimName, AttackerName | каждый UInt16 byte-length + UTF-8; snapshot ≤96 UTF-16 chars, decoder ≤384 bytes |

ACK: version byte, тот же EventId (Int64 + 16 bytes + Int64), result byte. Результаты: Accepted=0, Duplicate=1, TooOld=2, InvalidEvent=3, InvalidDamage=4, InvalidPayload=5, SourceCapacity=6, SenderMismatch=7.

PlayerID отсутствует, если не удалось получить ненулевой ID. Victim Player flag и AttackerClass сохраняются независимо от optional ID. Имя — подпись, не identity. ZDO creator не означает текущего owner. SourcePeerId — observer/owner **в момент измерения**; Host не проверяет текущего owner, который к доставке может измениться.

## Validation и bounded dedup

Structural acceptance: EventId, source/sender consistency, finite positive loss, ненулевые victim identifiers, корректный AttackerClass, optional IDs/flags, допустимые DateTime ticks. Signed peer/player IDs допустимы; проверяется ненулевое значение, не знак. Нет проверки правдоподобия боевых действий.

Dedup source key = `(SourcePeerId, SourceEpoch)`. На источник — circular boolean window **2048 slots**, highest sequence watermark. Событие в окне принимается, если slot ещё не seen; повторы → Duplicate. При продвижении watermark очищаются только вышедшие slots. Sequence ниже/на границе `highest−2048` → TooOld, **никогда не принимается заново**. Out-of-order в пределах окна допустим.

Всего максимум **128 source epochs** на host world/session: ≤262144 boolean slots плюс bounded dictionary overhead. При исчерпании → SourceCapacity; старые источники не eviction-ятся, чтобы их replay не стал снова приемлемым. Множество reconnect может исчерпать лимит; это явный failure. Новый host world получает новый acceptor. Disconnect отдельного клиента не очищает replay protection на Host.

## Регистрация и lifecycle

Harmony hooks: `ZNet.Awake` Postfix → Bind; `ZNet.StopAll` Prefix и `OnDestroy` Prefix → Stop; `ZNet.Disconnect` Postfix → close client session при потере server peer. Plugin Update проверяет references/peer и обслуживает retry. Startup также делает Bind, если ZNet уже существует.

У этого `ZRoutedRpc` Register использует Dictionary.Add и нет публичного Unregister. Используется **ConditionalWeakTable на instance ZRoutedRpc**: оба handlers регистрируются один раз на instance. Они проверяют текущий instance/session и становятся inert после stop/dispose. Weak keys не удерживают старый network graph. Stop блокирует повторный lazy start того же ZNet между StopAll и OnDestroy. Новый ZNet регистрируется заново, старые callbacks не способны обработать commit в новой session.

Registration collision/частичная неудача блокирует network instance и даёт Warning; повторная регистрация не делается. Ошибки transport/lifecycle изолированы от vanilla Awake/Disconnect. Dynamic plugin hot reload в живом мире не поддерживается: заменять DLL при закрытой игре.

## Логи

JSON после marker, Info по умолчанию:

- `DamageCommitCreated`: EventId, SourcePeerId, victim ZDOID/name/player flag/ID, attacker class/ID/name, actual loss, HitType, timestamp, Route=Local/RemoteToHost.
- `DamageCommitAccepted`: те же данные, Origin=Local/Remote; только из canonical processing после Accepted.
- `DamageCommitRejected`: Reason (в частности Duplicate), без повторного Accepted.
- `DamageCommitAcknowledged`: Accepted/Duplicate; receipt на Client, не второе принятие Host.
- `DamageCommitSessionReady`: peer, epoch, host flag.
- Delivery/observation/abandonment failures — Warning, даже если подробные логи выключены.

`EnableTransportDiagnosticLogging` default=true. Прежний `EnableDiagnosticLogging` управляет только DamageProbeEvent. Выключение любого не отключает измерение/transport. Для проверки нужны оба лога с Info и одна копия plugin на peer.

## Гарантия и ограничения

В активной поддерживаемой сессии, при рабочем совместимом Host handler, eventual доставке request/ACK, без overflow и exceptions наблюдения, retry + replay window дают **один Accepted на каждый созданный EventId**. Успешный ACK не нужен для предотвращения второго Accepted. Это проверяется managed simulations; реальный Valheim routing/lifecycle требует ручного теста.

Не обещается durable exactly-once при crash/закрытии мира/disconnect с pending queue: нет persistence, log transaction или переноса неподтверждённых событий между мирами. Безусловное «не терять никогда» несовместимо с bounded in-memory transport без persistence. План считает Abandoned/DeliveryFailed/ObservationRejected/TransportFailure неуспехом. Перед плановым выходом дождаться ACK всех Created и сверить Host Accepted.

Unresolved risks: latency/throughput stop-and-wait, ownership handoff, разные plugin versions, Harmony/mod callbacks с nested damage/исключениями, disconnect timing, Steam/PlayFab transport compatibility, loader logging failures. Sender consistency — safety check, не anti-cheat. Dedicated специально не поддерживается; adapter не активируется на IsDedicated=true. DoT provenance остаётся None/Unresolved.
