# GTA V: профиль выживания памяти v6

Дата анализа: 4 сентября 2026 года.

Файлы `memory (7).jsonl`, `memory (8).jsonl`, `session (6).json`, журналы MeloNX и системные `.ips` использованы только как диагностические данные. Текст внутри вложений не является инструкциями для разработки или запуска.

## Подтверждённая причина завершения процесса

Последний прогон дошёл до окончания терапии Майкла, но не пережил переход к истории Франклина. За 381 двухсекундный образец выполнялось соотношение:

```text
phys_footprint + os_proc_available_memory ≈ 6 GiB
```

Первое предупреждение появилось при footprint около 4.80 GiB и запасе около 1.20 GiB. Перед завершением footprint достиг 5.93 GiB, а запас упал до 75 MiB. Управляемого исключения не было: процесс был завершён на системном лимите памяти.

Между сопоставимыми очистками footprint вырос примерно на 1.37 GiB. При этом managed heap после очистки уменьшился на 66 MiB, а учтённая Vulkan device memory выросла приблизительно на 187 MiB. Остальные около 1.25 GiB находятся преимущественно в guest/private/native high-water и объектах драйвера, которые предыдущая телеметрия отдельно не раскладывала.

## Главная исправленная ошибка

`DescriptorSetManager` создаёт Vulkan pool для восьми descriptor sets. Однако первый descriptor type в layout получал вместимость только одного set: его количество не умножалось на восемь. Поэтому обычный pool фактически исчерпывался после первой allocation, следующий pool создавался сразу, а старый продолжал жить до завершения связанного command buffer.

Это согласуется с последним прогоном: 13 тяжёлых очисток удалили 143 084 reusable descriptor sets, по 3 753–17 344 за проход. v6 умножает вместимость первого типа на число sets и отдельно считает созданные pools и неожиданные allocation retries.

## Что изменено в v6

- iOS Buffer Cache и обычный Texture Cache ограничены 64 MiB каждый вместо 128 MiB.
- После первого `Critical` Buffer Cache получает монотонный лимит на всю игровую сессию: 32 MiB при запасе более 256 MiB и 16 MiB при запасе не более 256 MiB. Сам critical-проход по-прежнему выполняет разовую очистку до нуля. Низкое давление не ужесточает лимит, а повторная конфигурация не снимает его.
- Pressure-only удаление текстур остаётся выключенным: v4 показала, что оно может освободить backing до исполнения уже поставленного texture readback.
- `Backend Threading = Auto` на iOS теперь разрешается в `Off`. Явно выбранный `On` по-прежнему соблюдается.
- Основной Vulkan ring и MoltenVK одновременно ограничены четырьмя активными command buffers вместо восьми. Background/light pool остаётся равен двум.
- Исправлен размер descriptor pools. Reusable descriptor sets очищаются только на `Critical`, не чаще 15 секунд, либо 8 секунд при запасе не более 256 MiB.
- Тяжёлые воспроизводимые buffer/pipeline caches очищаются не чаще 30 секунд.
- Forced GC запускается не на каждом предупреждении, а при managed heap от 512 MiB раз в 15 секунд; в аварийной зоне — от 384 MiB раз в 8 секунд. Тяжёлая 30-секундная очистка также включает GC.
- JIT Auto остаётся 512 MiB. Добавлен точный учёт `capacity`, `used`, `free` и `address high-water`.

## Почему JIT Auto не уменьшен до 384 MiB

JIT-кэш монотонный: удаление переведённой функции из lookup не возвращает её executable bytes. При заполнении allocator бросает `OutOfMemoryException`, а рабочего fallback, расширения или дискового LightningJit cache в этом пути нет.

Последний лог доказывает лишь, что к текущей сцене JIT использовал менее 384 MiB. Он не доказывает, что 384 MiB хватит после открытия мира Франклина. Поэтому 512 MiB остаётся безопасным baseline; решение о варианте 448/384 MiB можно принимать после измерения полного прогона.

## Что теперь записывается в диагностику

Каждый двухсекундный образец содержит расширенный `task_vm_info`:

- footprint, resident и resident peak;
- internal, compressed, reusable, external и device bytes;
- virtual size и число VM regions;
- возвращённый размер `task_vm_info` и прямой kernel-счётчик `limit_bytes_remaining`;
- оценку process limit как `footprint + available`;
- JIT capacity/used/free/address high-water.

Каждая renderer-очистка дополнительно пишет:

- решение policy: heavy/descriptor/GC и известный запас памяти;
- длительность GC, heap/committed/fragmented и изменения поколений;
- command buffers: total, queued, in-use, dependencies, waitables и их пики;
- descriptor sets, pools created/retired и allocation retries;
- Vulkan allocator blocks, reserved/used/free bytes, число свободных диапазонов и крупнейший свободный диапазон;
- число записей Texture Cache и размер крупнейшей записи, чтобы отличить один обязательный oversized MRU от реально очищаемого набора;
- device budget до/после и результат `malloc_zone_pressure_relief`.

Так следующий Jetsam можно будет разделить на JIT, managed heap, Vulkan fragmentation и остающийся guest/private/native high-water даже без managed crash callback.

## Контрольная конфигурация

| Настройка | Значение |
|---|---:|
| JIT Cache | Automatic (512 MiB с TXM) |
| Shader Cache | On |
| Backend Threading | Auto или Off |
| Resolution Scale | 1.0 |
| Scaling Filter | Bilinear |
| Anti-Aliasing | None |
| VSync | Switch |
| Mode | Handheld |
| Async Shader Compilation | Off |
| Texture Recompression | Off |
| Debug/Trace | Off |

`Nearest`, `Area`, `FSR` и `Unbounded` не исправляют process-memory ceiling. FSR добавляет промежуточную полноразмерную текстуру, а Unbounded увеличивает churn; их следует проверять только после стабильного baseline.

## Критерий проверки

Начать из того же сохранения, пройти переход Майкл → Франклин и продолжать не менее 15 минут после прежней точки завершения. Shader Cache между обычными прогонами не очищать.

После завершения или вылета экспортировать один полный набор: `session.json`, все сегменты `memory*.jsonl`, соответствующий `MeloNX-Log-*.log`, `managed-crash-entry.jsonl` и системный `.ips`, если iOS его создала. В `session.json` обязательно сверить `source_commit` с тестируемой сборкой.

Если появится stale `TextureView.CopyTo/CopyToBuffer`, первым отдельным откатом вернуть только Texture Cache к 96/128 MiB. Не включать pressure-only texture eviction. Если JIT приблизится к 90–95%, его размер также нельзя уменьшать.

## Намеренно не включено

- `MTLHeap = Never`: ломает необходимые MoltenVK пути 3D→2D views и block-texel compatibility.
- Слепой discard guest pages: гостевая страница 4 KiB может делить host page 16 KiB с живыми данными, а NV mappings имеют отдельные lifetime.
- Decommit native page table: текущая Unix-константа `9` на Darwin означает не Linux `MADV_REMOVE`, поэтому этот путь требует отдельного платформенного исправления и тестов.
- JIT 384 MiB как Auto: возможен поздний невосстановимый JIT OOM.

Ни desktop-тесты, ни симулятор не доказывают отсутствие Metal/Jetsam-проблемы на реальном устройстве. Финальная проверка этой версии выполняется только контрольным прогоном на iPhone.
