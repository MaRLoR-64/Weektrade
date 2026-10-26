WEEKEND-TRADE — готовый проект

1. Установи Visual Studio 2022 с компонентом ".NET desktop development"
   и .NET 9 SDK, если они ещё не установлены.

2. Открой файл:
   weekend-trade.csproj

3. В Program.cs найди строку:
   string telegramToken = "ТВОЙ_TELEGRAM_TOKEN";
   и вставь НОВЫЙ токен своего Telegram-бота.
   Не отправляй токен в чат и никому его не показывай.

4. Visual Studio автоматически восстановит NuGet-пакет Telegram.Bot 22.10.3.
   Если нет: ПКМ по проекту -> Restore NuGet Packages / Восстановить пакеты NuGet.

5. Запусти проект кнопкой Start / F5.

Альтернативный запуск без Visual Studio:
- дважды нажми start.bat
- для этого команда dotnet должна быть доступна в PATH.

Статистика новой tuned-версии создаётся автоматически рядом с исполняемым файлом:
- watch_history_weekend_tuned.json
- trades_weekend_tuned.json

ВАЖНО:
- Эта версия остаётся PAPER-режимом: реальные ордера Bybit она не отправляет.
- Если Bybit возвращает 403, проблема может быть в текущем VPN/IP.
