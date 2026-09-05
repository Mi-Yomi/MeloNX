# MeloNX v11: атрибуция памяти и исправление накопления пустых записей

Experimental, база v10 `041ece0d2800d46a6d3ab5dc623e179f162ef2ad`.
Ветка `codex/gta-v11-memory-attribution`. Это проверяемая итерация для
диагностики вылетов при переходах между сценами. Устойчивое прохождение
GTA V на iPhone этой сборкой пока не подтверждено.

## Подтверждённое исправление

`CacheByRange` создавал пустой список и запись диапазона при неудачном
поиске. Последовательность новых диапазонов оставляла эти записи в кэше
до очистки владельца. До исправления synthetic regression на 20 000
поисков выделял 2 599 696 байт; удаления отсутствующих диапазонов —
640 000 байт. Теперь поиск, удаление и добавление зависимости для
отсутствующего диапазона используют lookup без создания записи.
Добавление настоящего ресурса и освобождение существующих ресурсов
сохраняют прежние правила, включая повторный вход при Dispose.

Это измеренный дефект метаданных. Его доля в памяти GTA на устройстве
ещё неизвестна; эти цифры нельзя масштабировать до обещания экономии
нескольких гигабайтов или считать доказательством устранения вылета.

## Что измеряет сборка

- Существующий bounded scratch pool получил категории по реальным
  местам аренды: Decode, Recompress, LayoutConvert, GuestBridge, Upload
  и Unclassified. Публикуются leased/peak/idle payload, создание,
  переиспользование и отбрасывание массивов. Idle относится к последнему
  вернувшему массив назначению. Пики категорий не складываются; снимки
  общего пула и категорий могут пересечь параллельную операцию.
- Census текстур хранит до 64 комбинаций форматов и размеров плюс
  явно обозначенный overflow. Формат guest сопоставляется с реально
  выбранным **GAL** format, причиной fallback, типом, слоями и mip-уровнями.
  Views имеют отдельный count без повторного payload. Дополнительные
  текстуры upload/readback учитываются отдельными ролями. Это выданные
  GAL lifetimes и логические bytes, а не residency Vulkan/Metal.
- Счётчики преобразований сохраняются после удаления текстур: число,
  ошибки, объём входа/выхода и CPU wall time. Peak transient оценивается
  отдельно по активным арендованным массивам; вход+выход не выдаются за
  одновременный пик всей conversion job.
- Фиксированные CPU timers измеряют FIFO processing, backpressure GAL,
  синхронные ожидания команд/внешних interrupts/кадра, создание shader
  modules, vkQueueSubmit, FenceHolder.Wait, acquire и presentation.
  Это пересекающиеся CPU wall intervals. `gpu_work_us=unknown` и
  `guest_cpu_us=unknown` остаются явными: hardware GPU timestamps и
  полное время гостевых CPU threads в этой итерации не измеряются.
- Managed counters дополнены общим объёмом выделений и Gen0/1/2.
  Heap/committed/fragmentation описывают последний GC snapshot.
  Первый allocation rate неизвестен, пока нет пары отсчётов.

Таймеры используют value-type scope без выделений, форматирование —
примерно раз в 10 секунд. Census ограничен фиксированным числом bins,
снимок не обходит все текстуры. Время диагностического снимка также
измеряется. Host microbenchmark и fake renderer tests не заменяют
измерения overhead на iPhone.

Области без полной атрибуции: raw byte[] readback и mirror buffers,
пулы объектов и зависимости драйвера, некоторые прямые fence waits,
pipeline compilation и ASTC decoder overloads вне MemoryOwner. Ноль
в счётчике охваченного пути не означает отсутствие таких выделений.
Virtual/reserved, logical, managed и driver bytes пересекаются и не
складываются в новый «total». Контрольная величина — OS footprint.

## Анализ полного экспорта

```sh
python3 tools/analyze_session.py --session session.json \
  --memory memory.jsonl memory.1.jsonl --core-log core.log \
  --core-utc-offset +05:00 --out analysis
```

Указывайте все сегменты независимо от порядка. Анализатор обрабатывает
дубликаты, оборванный конец, gaps и reset счётчиков; формирует JSON, CSV
и Markdown. SHA берётся из session.source_commit; отсутствующий номер
версии остаётся unknown. Настройки session и runtime observations
сохраняются отдельно, чтобы видеть расхождения. Ручные метки задаются
`--phase therapy=420 --phase franklin=510`
с **фактическими** секундами от старта session; числа в примере условные.
По elapsed нельзя автоматически назначить сцену. При отсутствии точной
timezone привязка core либо маркируется как inference при близких
временах начала, либо остаётся unknown; поздний фрагмент требует
`--core-utc-offset`. При нескольких host PID нужен `--core-pid`.
FPS рассчитывается
из счётчиков и реальных интервалов; p95/p99 кадров не выдумываются из
10-секундных агрегатов. Unknown сохраняется как unknown/null.

## Сборка и проверки

Workflow собирает ровно `${github.sha}` без подготовительных изменений
source или push в v10. Сохраняются все 150 регрессий из baseline, новые
проверки реальных путей/диагностики, полный memory project и Swift suite.
Manifest перечисляет обязательные тесты; missing/skipped не заменяются
условием «хотя бы один passed». Четыре исторических NotExecuted memory
tests явно перечисляются отдельно от passed. Результаты Python,
TRX и verification.json прикладываются к release.

NativeAOT и Xcode 26.2 на Apple Silicon упаковывают unprovisioned IPA.
Проверяются MeloNXSourceCommit, arm64, совпадение UUID приложения и
native dylib с dSYM, checksum IPA/source/symbols и bundled MoltenVK.
Публикация — experimental prerelease, `latest=false`, после gates.
IPA требует повторной подписи. Raw пользовательские evidence и игровые
данные в release не включены.

## Проверка на iPhone и возврат

Сравнить v10 и v11 на одном устройстве, одной версии игры/модов,
сохранении и состоянии shader cache. Перед каждой попыткой полностью
перезапустить приложение; не менять несколько настроек в одной сессии.
Baseline: handheld, Auto threading, 8 command buffers, caches128/128MiB,
JIT Automatic, guest4GiB, Bilinear, AAoff, ShaderCacheOn, AsyncOff,
RecompressionOff, VSyncSwitch. Пользовательские настройки автоматически
не перезаписываются.

Первичная цель — 1.0x с подтверждённым internal1280×720. Drawable имеет
свой размер; апскейл низкого internal resolution не считается native720p.
Нужен маршрут пролог/поезд → терапия → Франклин → минимум30 минут езды.
Отметить время переходов, затем Stop и60 секунд foreground; проверить
повторный запуск. Экспортировать session, все memory segments, core и
matching системный .ips/JetsamEvent, если он существует.

Ориентиры: отсутствие вылета и ошибок данных, отсутствие длительного
провала30→18→6FPS, запас памяти у перехода желательно≥512MiB, отсутствие
непрерывного роста владельцев после warmup. Это критерии будущего
device-run, а не полученные результаты.1.5x/native1080p проверяется
отдельно после стабильного720p.

Возврат: установить прежний v10 prerelease с тем же bundle identity и
сохранёнными данными. Не удалять приложение с сохранениями ради rollback.
Ветка/tag v10 не изменяются.
