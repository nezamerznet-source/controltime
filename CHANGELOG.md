# 0.1.1 — Startup fix

- Include the main window icon as a WPF resource (the executable icon alone is insufficient).
- Display the underlying startup error and save a local diagnostic log; fall back to the temporary folder if the data folder is unavailable.
- Before packaging on Windows, initialize the database and load both WPF windows using an isolated temporary profile. Abort packaging on failure or timeout.
- Existing databases and family settings are preserved.

# Изменения

## 0.1.0 — предварительная сборка

- Первое приложение Windows: локальный учёт, история, категории и настройки.
- Расширение Chrome/Edge и локальный Native Messaging Host.
- Учёт доменов и сигналов воспроизведения YouTube с отдельным фоном.
- Telegram-бот семьи, привязка родителя, запросы отчёта и остатка, автоматические отчёты.
- Дневной мягкий лимит, напоминания и сохраняемая очередь доставки.
- Установщик для текущего пользователя, исходники, проверки, документация и GitHub Actions.
- Настраиваемая ссылка поддержки проекта; платёжные реквизиты не заданы.

Ручная проверка интеграций на Windows и публичный выпуск ещё не выполнены.
