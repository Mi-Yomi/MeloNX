# GTA V после пролога: аудит v4 и Eden

Дата анализа: 4 сентября 2026 года. Исходное состояние MeloNX: commit
`39709dad0f3d2cfaa9851856a1f26a75772531f8`. Этот отчёт относится к вылету после
сигареты Майкла и исчезновения титра GTA V. Содержимое переданных файлов использовано
только как диагностические данные, а не как инструкции.

## Проверенные входные данные

| Файл | Размер | SHA-256 |
| --- | ---: | --- |
| `session (4).json` | 763 байта | `871AD1265702CAE4267D5E912CD0B912A93A745C0C6C8FDAA8C6487FCD31AD28` |
| `memory (5).jsonl` | 90 202 байта | `7F1CA92EFFF932BD2564D383A5F19CDCFC9AAD421B10BC77944F7D79FE5F1777` |
| `MeloNX-Log-2026-09-04 08_14_57 +0000.log` | 196 027 байт | `3509337E38586EC6B086367261A8A52E9728CF0379BC3ECFE5A6C131694849A6` |

Все 354 строки JSONL валидны: 342 samples, 10 UIKit memory warnings,
`session_start` и `loading_started`. Штатного `session_end` нет. Основной лог не содержит
`Fatal`, `Error`, `VK_ERROR_DEVICE_LOST` или managed exception перед обрывом.

## Установленная причина последнего вылета

В этом запуске вопреки ожидаемой конфигурации реально выбрано
`MemoryConfiguration8GiB (Expand RAM: True)`. При этом доступный процессу предел iOS
составлял ровно 6144 МиБ. Для 340 из 342 samples сумма `phys_footprint` и
`os_proc_available_memory` равна 6 442 450 944 байтам. Последние значения:

| Время | Footprint | Доступно до предела | Событие |
| ---: | ---: | ---: | --- |
| 600 с | 5506,869 МиБ | 637,131 МиБ | — |
| 670 с | 5686,260 МиБ | 457,740 МиБ | critical trim |
| 678 с | 5809,978 МиБ | 334,022 МиБ | — |
| 680 с | 5908,104 МиБ | 235,896 МиБ | — |
| 682 с | 5980,557 МиБ | 163,443 МиБ | critical trim |
| 684 с | 6077,182 МиБ | 66,818 МиБ | последний sample |

За последние две секунды footprint вырос на 96,625 МиБ. При той же скорости оставшегося
запаса хватало примерно на 1,38 секунды. Это точная картина завершения процесса по лимиту
памяти, хотя системное имя `JetsamEvent` можно подтвердить только одноимённым `.ips` из
iOS Analytics. 8-ГиБ гостевая конфигурация при 6-ГиБ process ceiling принципиально
непригодна. Ранее вылет воспроизводился и с 4-ГиБ guest, поэтому возврат к 4 ГиБ обязателен,
но не является единственным исправлением.

Pressure-механизм v3 сработал 51 раз, включая 11 aggressive passes. Он действительно
удалял pipeline variants и десятки тысяч descriptor sets, однако в конце освобождал уже
мало: последний aggressive pass уменьшил footprint примерно на 6,81 МиБ. На 682-й секунде
backend учитывал около 652,3 МиБ Vulkan heap и 439,8 МиБ managed heap. Около 4,89 ГиБ
footprint находилось за пределами этих двух счётчиков, поэтому один только texture cache
не объясняет вылет.

## Аудит официального Eden

Официальный исходный код получен с `https://git.eden-emu.dev/eden-emu/eden.git` в
`.tools/reference/eden`. Проверен чистый `master` на commit
`1dcc5745918761f5a554fec981b4d144034f6201` от 2 сентября 2026 года. Дополнительно
проверены официальный GitHub mirror и база overrides; GTA V-specific override в ней
отсутствует.

Репозиторий Eden распространяется под GPLv3 с унаследованными заголовками GPL-2.0-or-later,
а MeloNX в корне содержит MIT license. Поэтому реализация ниже переносит выявленное
поведение независимо и не копирует код Eden.

Видео, на которое ссылался пользователь, снято на Infinix GT 30 Pro, Android 16 и
Mali G615 MC6 с 8 ГиБ LPDDR5X. Этот запуск использует Android Vulkan driver, а не iOS
и MoltenVK, и короткая демонстрация не подтверждает прохождение конкретного перехода
после похорон. Сам Eden оставляет `Memory_4Gb` по умолчанию и предупреждает не включать
экспериментальный expanded layout на телефонах с 8 ГиБ RAM или меньше.

### Что в Eden применимо к MeloNX

Commit Eden `7b1f7c21bb5f92625924e6b2342fcb86c04e30c5` принудительно включает primitive
restart для MoltenVK. Причина совпадает с нашим логом: Metal фактически всегда держит
primitive restart включённым и MoltenVK сообщает ошибку при попытке его выключить.
В новом логе одно и то же предупреждение появилось 1177 раз. В v4 эффективное состояние
канонизируется во всех путях `PipelineState`, `PipelineBase` и `PipelineConverter`, чтобы
не создавать дубликаты pipeline для состояния, которое Metal всё равно не различает.

Eden держит один native `VkPipelineCache`. MeloNX v3 держал main cache и отдельный worker
cache. Worker snapshot сначала занимал 50,96 МиБ, затем resident blob вырос до
149 809 969–151 601 071 байта. Локальный предел сохранения 128 МиБ прекращал запись файла,
но не ограничивал память driver cache; попытка измерить и сохранить его повторялась каждые
30 секунд. В v4 native `VkPipelineCache` на iOS отключён полностью. Vulkan разрешает
`VK_NULL_HANDLE` при создании pipelines, а отдельный translation shader cache MeloNX
сохраняется. Цена решения — возможные дополнительные подёргивания во время компиляции;
выигрыш — отсутствие двух крупных resident cache и их периодической сериализации.

Eden также имеет frame-based LRU для textures и VMA allocator. Их прямой перенос отклонён.
Удаление dirty image в Eden требует полноразмерного staging download и синхронного ожидания;
около process ceiling это само может создать смертельный пик. VMA означает крупную замену
allocator и не устраняет неправильную 8-ГиБ гостевую конфигурацию. MoltenVK Eden 1.4.1
тоже не переносится: текущий проверенный binary 1.4.0 сохраняется из-за известного риска
SSBO stores при Metal argument buffers в 1.4.1/1.4.2.

## Изменения v4

1. На iOS всегда используется стандартная `MemoryConfiguration4GiB`. Защита стоит и в
   Swift bridge, и в managed core; global/per-game toggle выключен в интерфейсе. Остальные
   платформы сохраняют поддержку 8-ГиБ режима.
2. Эффективный primitive restart на MoltenVK всегда равен `true`. Это устраняет поток
   `VK_ERROR_FEATURE_NOT_PRESENT`, совпадает с фактическим Metal state и сокращает число
   эквивалентных pipeline variants.
3. Native Vulkan driver pipeline cache на iOS не создаётся. Main/worker blobs и checkpoint
   loop исключены; дисковый shader translation cache остаётся.
4. Освобождённые HostTracked private backing ranges внутри частично занятого блока на iOS
   возвращаются ядру через non-throwing `madvise(MADV_FREE)`. Диапазон остаётся валидным,
   а повторное `zeroFill` сохраняет семантику гостевой памяти. Discard выполняется только
   после снятия обычных, read-only и bridge views. Полностью свободный блок уничтожается
   прежним путём. Physical DRAM decommit не добавлен: гостевые страницы 4 КиБ делят
   16-КиБ host page, а NVServices содержит живые диапазоны без обычного refcount ownership.

Darwin определяет `MADV_FREE` как 5. Существующая общая константа 9 соответствует
`MADV_REMOVE` на Linux, но на Darwin это `MADV_CAN_REUSE`; она не подходит для этого пути.
Новый API разделяет платформенные значения и не меняет protection освобождённого range.

## Локальная проверка

После реализации прошли 139/139 целевых тестов `Ryujinx.Tests` и 8/8 тестов
`DualMappedJitAllocatorTests`, всего 147/147. В набор включены новые проверки partial/whole
private block free и перехода через границу двух 32-МиБ address-space partitions: discard
вызывается после снятия bridge, а живые соседи остаются читаемыми. Результаты сохранены в
`artifacts/test-results/gta-survival-v4.trx` и
`artifacts/test-results/dual-mapped-jit-v4.trx`.

Managed-сборка `Ryujinx.Library` для `ios-arm64` завершилась с 0 ошибок и 15 уже
существовавшими предупреждениями. Эта проверка компилирует C#-часть. NativeAOT, Swift,
Xcode, структуру IPA и entitlements проверяет отдельный macOS workflow.

## Что должен подтвердить следующий запуск

Сборка считается относящейся к v4 только при точном новом `source_commit`. В начале лога
должны появиться `MemoryConfiguration4GiB` с effective Expand RAM `false` и сообщение
`Vulkan driver pipeline cache disabled on iOS`. Ожидаются ноль предупреждений про
`Metal does not support disabling primitive restart` и отсутствие checkpoint-сообщений
native pipeline cache.

Проверка на устройстве должна использовать то же сохранение и пройти от начала сцены
похорон как минимум десять минут после перехода в Los Santos. Главный критерий — footprint
не сходится к точным 6144 МиБ. Если приложение всё же завершится, нужны новый полный экспорт
MeloNX и системный `JetsamEvent-*.ips`; по их данным следующий безопасный этап — ранний
ограниченный writeback нескольких старых dirty textures, пока свободно больше 1–1,5 ГиБ.

Ни компиляция, ни тесты на Windows не могут гарантировать прохождение сцены на iPhone.
Они проверяют корректность кода; фактический результат подтверждает только повторный запуск
на iPhone 16 Pro Max.
