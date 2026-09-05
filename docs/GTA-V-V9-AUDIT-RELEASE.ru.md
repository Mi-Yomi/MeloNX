# MeloNX GTA V v9 — readback / pressure / ownership (experimental)

Основа: v8 `25f42e9a2a8579d752b30b92563a8fe0348eb040`. Отдельная ветка `codex/gta-v9-readback-pressure`; master, v7 и v8 сохранены. Это кандидат для проверки на устройстве, не подтверждение полной совместимости GTA V.

## Что установлено по последнему v8-прогону

Контрольная конфигурация: iPhone 16 Pro Max, iOS 27.0 build 24A5430a, Auto→On, 8 command buffers, buffer/texture cache 128/128 MiB, JIT Auto 512 MiB, handheld, 1x, Shader Cache On, Async Off, Texture Recompression Off. Эффективная guest RAM — 4 GiB, несмотря на старое UI requested=true.

В core log около 357 s UIKit Critical с 1229 MiB доступной памяти обнуляет buffer cache, устанавливает ceiling 64 MiB и сбрасывает 1067 pipeline variants. Позднее появляются окна около 18, 11 и 6 FPS. Это временная связь и воспроизводимый нежелательный механизм очистки, а не доказательство единственной причины замедления. Нет внешних timestamps, позволяющих точно привязать запись к кадру столкновения с поездом.

Последний из 347 memory samples, elapsed=694 s: phys_footprint=6419835336 bytes (6122.43 MiB), available=22599224 bytes (21.55 MiB); compressed=3135225856 bytes; JIT used=150425252 bytes. Окончание близко к наблюдаемому 6-GiB process ceiling. В core log нет matching managed exception, BufferMap miss или штатного Stop. Это сильно согласуется с memory-limit termination, но без matching системного .ips нельзя объявлять Jetsam доказанным.

## Шесть пунктов исходного аудита

| Пункт | Изменение | Граница проверки |
|---|---|---|
| A1: неверный size GPU copy | v8 mSize сохранён; regression sentinel/partial-range | CPU fake backend, не запуск GTA |
| A2: второй producer при background readback | v9 использует backend interrupt вне SPSC ring; mapping проверяется на owner; exceptions возвращаются вызывающему потоку | Реальная threaded queue + concurrent copy tests; Metal run ещё нужен |
| A3: GPU teardown | v8 request-only Stop, producer drain, backend-owner disposal сохранены | Shutdown fake integration; полный Stop на iPhone ещё нужен |
| A4: потерянный completion | v8 finally/completion сохранён | Не означает автоматического успешного GPU completion при fault |
| A5: post-stop sampler | v8 OS-only окно 60 s сохранено; v9 thermal/low-power и owner telemetry | После аварийного SIGKILL приложению недоступны post-stop samples |
| A6: RAM benchmark ownership | v8 cleanup на cancellation/failure сохранён | Семь Swift regression scenarios в CI |

Дополнительно v9 закрывает ещё один выявленный кодовый дефект: `PageTable<T>.Unmap` сравнивал через `Equals(object)` с неявным null, поэтому пустые value-type leaf arrays оставались удержанными. Используется typed equality с ранним выходом; проверяются живой сосед, повторное sparse map/unmap и nuint. Размер экономии на реальном GTA-прогоне пока неизвестен; этот дефект нельзя объявлять объяснением всех недостающих гигабайтов.

## Pressure policy v9

Временный buffer target больше не равен нулю на каждом Critical: сохраняется 64 MiB hot set, 32 MiB при headroom ≤256 MiB. Low остаётся 64 MiB. UIKit warning при process headroom >1024 MiB по-прежнему вызывает безопасную очистку, но не фиксирует постоянное понижение ceiling.

На iOS лёгкий trim освобождает только работу с завершёнными fences и unused pool memory, без безусловного device-idle. Сброс descriptor caches разрешён при headroom ≤512 MiB (30 s cooldown, 8 s в emergency), полная инвалидизация pipeline variants — при ≤256 MiB с прежним cooldown. Forced GC остаётся условным и throttled; другие платформы сохраняют прежнюю policy. In-flight ресурсы не освобождаются преждевременно.

Это экспериментальное изменение обмена память/производительность: сохранение горячих объектов может удерживать больше памяти между trims. Возврат FPS и запаса ≥512 MiB нельзя выводить из unit tests; нужен новый device run. Pressure-only texture eviction остаётся выключенным. Слепой discard guest pages, уменьшение JIT и переключение Auto→Off не применяются.

## Дополнительные lifecycle tests

Реальная GAL FIFO с 1/24000 texture-to-buffer copies перед Release/Delete, отказ от новой copy после release request, идемпотентность release, imported/non-imported high-level flush-buffer unmap→dispose. Concurrent buffer tests совмещают 24000 обычных copies и 128 внешних readbacks, проверяют sentinel bytes и owner thread. Ошибка внутри interrupt не должна навсегда блокировать следующий interrupt. Fake imported flag не является тестом настоящего Vulkan/Metal external-memory import.

## Новая диагностика

Не чаще примерно одного раза в 10 секунд логируются versioned GPU/guest/native owner records: caches и normal eviction counters; GAL producer/consumer и background copies; private allocator reserved/allocated/blocks; native page-table reserved/committed и managed leaf count; texture storage/view owners и logical bytes; host-import mappings; native allocator и driver budget; managed heap/committed/fragmentation/allocation rate. Swift samples содержат thermal state и Low Power Mode.

Counters явно помечены logical/virtual/driver-reported и перекрываются. Их нельзя складывать и называть phys_footprint. Per-owner resident/compressed VM accounting, внутренние Metal allocation stacks, полная атрибуция sampler/pool references и полноценный device soak этим кодом не заменяются. Условные ветки старого handoff (texture writeback/admission, JIT reclaim, замена MoltenVK) не включены вслепую: их необходимость должен показать измеренный владелец роста.

## Проверки сборки и устройства

CI должен применить изменения отдельными source commits, выполнить C# regression suite, Swift tests, NativeAOT/Xcode packaging и только затем публиковать prerelease. Конкретные результаты находятся в приложенных TRX и logs; отсутствие результатов нельзя считать успешным тестом. IPA unprovisioned, перед sideload необходим re-sign.

Первый device run: полный перезапуск приложения, прежние настройки и прогретый Shader Cache. Пролог → поезд/столкновение → терапия Майкла → управление Франклином → минимум 30 минут streaming. Затем отдельный Stop, 60 секунд в foreground без нового запуска и повторный старт. Для широкой совместимости нужны дополнительные 45–60 минут, смена персонажей, полиция/взрывы и поздние миссии. Экспортировать session.json, все memory JSONL segments, core log, crash marker и matching .ips при наличии.

Главные gates: нет BufferMap miss/DeviceLost/late-copy; нет устойчивого провала 30→18→6 FPS; переход пережит не на последнем десятке MiB; потоковая память стабилизируется, а после Stop owners возвращаются к baseline. Выполнение этих gates на iPhone в CI не проверяется.
