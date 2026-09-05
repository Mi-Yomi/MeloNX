# MeloNX GTA V v8 audit — experimental

Основа: v7 3bc06315dd25467dfc0081426f5344241f0ff65d. Изолированная ветка codex/gta-v8-audit-fixes; master и v7 не изменены.

Включены: исправление длины GPU-копирования по пересечению, request-only Stop, завершение producer перед drain и backend disposal на owner thread, удержание Metal view до завершения core, OS-only memory sampling до 60 секунд после возврата core, освобождение всех RAM benchmark allocations при Stop и malloc failure.

Сохранены Auto→On, восемь command buffers, 128/128 MiB caches, JIT Auto и bundled MoltenVK. Новые тесты проверяют фактический copy/readback через CPU fake renderer, реальную threaded queue с 0/64/12000 copies и поздними deletes, а также семь Swift cancellation/failure cases. Результат конкретного запуска находится в приложенных CI logs/TRX, а не предполагается заранее.

ВАЖНО: общий background-flush путь, способный записывать с guest thread в single-producer GAL queue, пока НЕ исправлен. Расширенный prototype отозван: простой lock/deferred-consumption вариант может создать deadlock либо stale read. Из этого release нельзя делать вывод, что все вылеты закрыты. Native device run и прохождение Майкл→Франклин здесь не выполнены. Teardown integration протестирован с CPU backend, а не Metal на iPhone.

IPA unprovisioned: требуется повторная подпись для sideload. Для первого A/B полностью перезапустить приложение, сохранить настройки и прогретый Shader Cache. После Stop оставить приложение открытым на 60 секунд без нового запуска; background suspension может отложить samples, фактическое elapsed записывается. Экспортировать session.json, все memory*.jsonl, core log и matching .ips при наличии.
