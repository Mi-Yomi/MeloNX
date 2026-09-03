# Сборка экспериментального MeloNX для iPhone

Подготовлено 3 сентября 2026 года для iPhone 16 Pro Max и неофициального Switch-порта GTA V.
Основа выгрузки — ревизия `6a1c15962` (`Fix Toolbar`). Точный commit готового пакета будет
указан в его `build-info.txt`.

Локальные C#-проверки и полный облачный цикл NativeAOT → Swift/Xcode → IPA пройдены.
[Успешная сборка 33763880056](https://github.com/Mi-Yomi/MeloNX/actions/runs/33763880056)
использует commit `3da533d714921dcb06f356791db9e686c062d948`.
Создан `MeloNX-3da533d71492-unprovisioned.ipa`; проверены ARM64, архив ZIP
и `increased-memory-limit` в подписи. [Скачать артефакт](https://github.com/Mi-Yomi/MeloNX/actions/runs/33763880056/artifacts/9896837437).
Установка на телефон и запуск GTA V ещё не проверены.

IPA скачан в `artifacts/ios-run-33763880056/package/`, размер — 46 183 271 байт.
SHA256 скачанного артефакта совпал с GitHub; IPA и архив исходников сверены с `SHA256SUMS.txt`.
SHA256 IPA: `275de59c99edb7fb232e60f6a7be98d678922f8986a673cdeb1e5da016b67a7b`.

## Сборка через GitHub с телефона

Личный Mac для этого пути не нужен: Xcode выполняется на macOS runner GitHub Actions.
Репозиторий для сборки — [Mi-Yomi/MeloNX](https://github.com/Mi-Yomi/MeloNX).
Импорт исходников завершён; основная ветка — `master`, workflow уже доступен.

Для повторной сборки:

1. Откройте **Actions → Experimental iOS package → Run workflow**.
2. Выберите ветку с экспериментальными изменениями и запустите один build.
3. После успешного завершения откройте его **Artifacts** и скачайте
   `MeloNX-ios-experimental-<полный SHA>`.
4. Распакуйте архив. Внутри должен быть `MeloNX-<короткий SHA>-unprovisioned.ipa`.
5. Переподпишите его своим рабочим sideload-инструментом с сохранением entitlement памяти,
   установите и включите свой проверенный JIT/Get More RAM.

Для ручного запуска файл workflow должен присутствовать в основной ветке репозитория;
потом можно выбрать другую ветку с изменениями. Для скачивания артефакта нужен вход в GitHub.
[Ручной запуск](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow),
[скачивание артефактов](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts).

Настройки первого запуска и порядок сбора логов описаны в
[эксперименте GTA V](GTAV-EXPERIMENT.ru.md). Если сборка завершилась ошибкой, сохраните
её `build-info.txt` и каталог `logs`; IPA при такой ошибке может отсутствовать.
Исследование исходников и гипотез: [GTA-V-RESEARCH.ru.md](GTA-V-RESEARCH.ru.md).

## Как устроена облачная сборка

Workflow `.github/workflows/ios-experimental.yml` запускается только вручную, имеет
разрешение `contents: read` и ограничение 45 минут. Он использует стандартный ARM64
runner `macos-26`, Xcode 26.2 и отдельную установку .NET SDK 10.0.400.
В приватном репозитории запуск расходует доступную квоту GitHub Actions; платный larger runner
не запрашивается. Доступность Xcode проверяется перед сборкой, поскольку образ runner обновляется.
[Образ macOS ARM64](https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md),
[правила использования Actions](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

Скрипт `distribution/ios/build-unsigned-ipa.sh`:

- Разрешает Swift-пакеты по сохранённому `Package.resolved` и проверяет, что файл не изменился.
- Запускает Xcode-схему MeloNX; её legacy target Ryujinx выполняет настоящий `dotnet publish` для `ios-arm64`.
- Собирает без Apple-сертификата и provisioning profile, проверяет ARM64 executable и `Ryujinx.Library.dylib`.
- Добавляет основному executable ad-hoc подпись как носитель
  `com.apple.developer.kernel.increased-memory-limit` и проверяет наличие этого entitlement.
- Упаковывает `Payload/MeloNX.app` в IPA и сохраняет исходники того же коммита, SHA256, лицензии и логи.

Это **пакет для переподписи**, а не готовая подписанная установка.
`MeloNX.entitlements` и `embedded-entitlements.plist` находятся рядом с IPA;
наличие entitlement в артефакте нужно ещё подтвердить после переподписи на телефоне.
Идентификатор приложения пока сохранён: `com.stossy11.MeloNX`.
Артефакты workflow хранятся 7 дней; диагностика загружается и при неудачной сборке.

Для NativeAOT важен именно `publish` с собственным restore. В SDK 10.0.400 обычный
`dotnet restore`/`build` не добавляет метку `RuntimePackLabels=NativeAOT` к framework reference;
она включается при публикации. Поэтому готовый managed `Ryujinx.Library.dll` ещё не проверяет
нативную iOS-компоновку. Workflow не меняет framework references и не устанавливает MAUI workload.

## Какие изменения подготовлены

- Выбор JIT-кэша **Automatic / 512 / 768 / 1024 MiB** в **Settings → Advanced →
  JIT Cache & Memory Diagnostics** (раздел **Experimental**). Значение запрашивается при запуске процесса; после изменения
  требуется полностью закрыть и снова открыть MeloNX.
- Automatic сохраняет исходный выбор: 512 MiB с TXM, 1024 MiB без TXM.
- C#-лог сообщает размер исполняемого кэша, пересечение уровней 75/90/95% и подробности его исчерпания.
- Память измеряется каждые 2 секунды с начала загрузки игры: `phys_footprint`, доступная процессу память,
  пики, предупреждения памяти и события ухода в фон. Экспорт включает последнюю сессию и связанный
  лог эмуляции, если он доступен.

Это инструменты проверки конкретной гипотезы о JIT-кэше. Гостевая конфигурация памяти не изменена;
Get More RAM не увеличивает гостевую RAM Switch. Успешный запуск GTA V этими изменениями ещё не доказан.

## Что проверено

| Проверка | Результат | Что она подтверждает |
| --- | --- | --- |
| `Ryujinx.sln`, Release | 0 ошибок, 5 предупреждений | Компиляция desktop и общих C#-проектов |
| `Ryujinx.Library.csproj`, Release, `ios-arm64` | 0 ошибок, 15 предупреждений | Компиляция managed iOS-библиотеки |
| Новые тесты конфигурации и учёта JIT-кэша | 19 пройдено | Выбор размера, выравнивание, границы и сообщения allocator |
| Bash-скрипты и diff | `bash -n`, `git diff --check` пройдены | Синтаксис скриптов и отсутствие ошибок пробелов |
| Workflow | `actionlint 1.7.12`, 0 ошибок | Схема и выражения GitHub Actions |
| NativeAOT и Swift/Xcode на macOS ARM64 | `BUILD SUCCEEDED`, запуск 33763880056 | Нативная компоновка iOS-библиотеки и компиляция приложения |
| Упаковка IPA | Успешно, запуск 33763880056 | ARM64, entitlement увеличенного лимита памяти, целостность ZIP |
| Установка и GTA V на iPhone | Пока не проверено | Нужен тест на устройстве |

Количество предупреждений при инкрементальной сборке может быть меньше.
Логи C#-проверок: `artifacts/logs/build-windows.log`, `artifacts/logs/build-ios-managed.log`.
Среди предупреждений есть транзитивная зависимость `Tmds.DBus.Protocol 0.21.2` (`NU1903`),
nullable/platform-анализ и конфигурация Apple-аудиопроекта в решении.
`Ryujinx.sln` не включает приложение Swift и `Ryujinx.Library`, поэтому последняя проверена отдельно.

## Загруженные зависимости

| Компонент | Версия / локальный путь |
| --- | --- |
| .NET SDK Windows x64 | 10.0.400, `.tools/dotnet` |
| Архив SDK macOS ARM64 | `.tools/downloads/dotnet-sdk-10.0.400-osx-arm64.tar.gz` |
| Архив SDK Windows x64 | `.tools/downloads/dotnet-sdk-10.0.400-win-x64.zip` |
| NuGet решения и iOS-библиотеки | `.tools/nuget/packages` |
| NativeAOT compiler macOS ARM64 и runtime iOS ARM64 | 10.0.11, тот же NuGet-кэш |
| Melo-Controller | `efe0373ede6ca4dc7d6533d7fa47ad52b4230fe8`, `.tools/swift-packages/melo-controller` |
| NavigationStackBackport 1.1.0 | `55acfe7693c233ddc7f62f887f0df6ebd779ef01`, `.tools/swift-packages/navigation-stack-backport` |

SHA512 архивов SDK сверены с
[официальными метаданными Microsoft](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json).
Swift-ревизии совпадают с `Package.resolved`; дополнительных внешних зависимостей у этих пакетов нет.
SDK установлен в проекте; системный .NET и постоянный PATH не изменены.
В исходной выгрузке уже были ARM64-библиотеки SDL3, BreakpointJIT, MoltenVK и FFmpeg.
Submodules, LFS-заглушек и отсутствующих буквальных `ProjectReference` при проверке не обнаружено.

`.tools/` и `artifacts/` исключены из Git. Для GitHub Actions переносить локальный кэш не нужно:
workflow скачает зависимости самостоятельно. Скачанные Swift checkouts — запас исходников;
они не подменяют удалённые SwiftPM repositories автоматически.

## Повторная проверка на Windows

Из корня проекта:

```powershell
powershell.exe -NoProfile -File scripts/prepare-build.ps1
powershell.exe -NoProfile -File scripts/build-local.ps1
```

`build-local.ps1` также принимает `-Target Desktop` или `-Target IosManaged`.
Оба скрипта возвращают прежнее окружение после завершения. Windows-проверка ограничена C#;
Visual Studio, CMake и отдельный Vulkan SDK для неё не потребовались.

## Необязательная сборка на своём Mac

Вместо GitHub Actions можно использовать Mac с Apple Silicon, Xcode 26.2 и iPhoneOS SDK.
После переноса репозитория и подготовленных архивов, из корня чистого закоммиченного checkout:

```bash
mkdir -p .tools/dotnet-macos
tar -xzf .tools/downloads/dotnet-sdk-10.0.400-osx-arm64.tar.gz -C .tools/dotnet-macos
export DOTNET_ROOT="$PWD/.tools/dotnet-macos"
export DOTNET="$DOTNET_ROOT/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export NUGET_PACKAGES="$PWD/.tools/nuget/packages"
export DEVELOPER_DIR="/Applications/Xcode_26.2.app/Contents/Developer"
bash distribution/ios/build-unsigned-ipa.sh
```

Результат появится в `artifacts/ios`. На облачном Mac подтверждён полный цикл:
NativeAOT, Swift/Xcode и упаковка IPA с проверкой архитектуры и entitlement.
Текущий скрипт рассчитан на физическое ARM64-устройство, не на iOS Simulator.
