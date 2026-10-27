WEEKEND TRADE — TUNED V2 (PAPER)
================================

1. Открой weekend-trade.csproj в Visual Studio.
2. В Program.cs найди:
   string telegramToken = "ТВОЙ_TELEGRAM_TOKEN";
   и вставь НОВЫЙ токен своего Telegram-бота.
3. Запусти проект.

ВАЖНО:
- Версия остаётся PAPER: реальные ордера на Bybit не отправляются.
- Новая статистика пишется отдельно:
  trades_weekend_tuned_v2.json
  watch_history_weekend_tuned_v2.json
- После появления хотя бы 20-30 закрытых paper-сделок пришли оба JSON для следующей калибровки.

Что изменено после анализа 114 WATCH:
- technical confluence EntryPlan: 30 -> 18
- technical confluence final: 35 -> 28
- spike quality: 55 -> 52
- entry quality: 53 -> 48
- model score: 60 -> 57
- min net TP: 0.080% -> 0.050%
- bootstrap net reward/risk: 0.90 -> 0.70
- bootstrap min gross R/R: 1.05 -> 0.85
- entry confirmation wait: 1100 ms -> 700 ms
- финальное подтверждение входа: 2 из 3 блоков flow/book/micro вместо требования пройти все микрофильтры одновременно.
