using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// =====================================================
//                   НАСТРОЙКИ СТРАТЕГИИ
// =====================================================

const string Symbol = "ETHUSDT";
const string BybitWsUrl = "wss://stream.bybit.com/v5/public/linear";
const string BybitRestUrl = "https://api.bybit.com";
const string StrategyMode = "WEEKEND_TUNED_V2_PAPER";

// TUNED V2 после дополнительного анализа 114 WATCH из watch_history_weekend_tuned.
// Главные изменения: technical confluence 30/35 -> 18/28, quality 55/53/60 -> 52/48/57,
// bootstrap target мягче, а подтверждение входа использует 2 из 3 microstructure-блоков.
// Реальные ордера НЕ отправляются: это paper-режим для получения фактической выборки.
// На bootstrap-этапе target выбирается по чистому reward/risk после costs; после 20 закрытых
// paper-сделок снова включаются историческая probability/EV-калибровка. История отдельная.

// Основной анализ потока
// Импульсный сигнал быстро устаревает: 10 минут было слишком много.
const int SignalLifetimeMinutes = 4;
const int EntryConfirmationTimeoutSeconds = 90;
const int MaxTradeLifetimeMinutes = 45;
const int SignalCooldownSeconds = 90;
const int AtrPeriod = 14;
const int BaselineMinutes = 60;
const int MarketContextLookbackMinutes = 480;
const int VolumeProfileMinutes = 240;
const int VolumeProfileBins = 48;
const decimal ValueAreaPercent = 0.70m;

// СБАЛАНСИРОВАННЫЙ РЕЖИМ.
// Первый большой импульс теперь НЕ является точкой входа: он создаёт WATCH.
// Сделка рассматривается только после отката и повторного ускорения потока.
// В этой версии WATCH мягче: вместо идеального совпадения всех фильтров
// требуется подтверждение цены + минимум 2 из 3 блоков (объём / flow / стакан).
const decimal MinMainVolumeRatio = 1.45m;
const decimal MinDirectionalShare = 0.57m;
const decimal MinPriceMovePercent = 0.03m;
const decimal MinSpikeQuality = 52m;
const decimal MinEntryQuality = 48m;
const decimal MinModelScore = 57m;
const decimal MinEstimatedProbability = 52m;
const decimal MinExpectedValuePercent = 0.004m;
const decimal MinNetPotentialProfitPercent = 0.050m;
const decimal MinTechnicalConfluencePlan = 18m;
const decimal MinTechnicalConfluenceFinal = 28m;
const decimal BootstrapMinNetRewardRisk = 0.70m;
const decimal MaxAllowedSpreadBps = 4.5m;
const decimal MinPersistentBookImbalance = 0.46m;
const decimal MinImpulseEfficiency = 0.26m;
const decimal MinDirectionalCloseLocation = 0.52m;

// Двухэтапная логика: IMPULSE -> PULLBACK -> RE-ACCELERATION -> ENTRY.
const int WatchLifetimeSeconds = 300;
const int WatchCooldownSeconds = 15;
const decimal WatchMinVolume30Ratio = 1.20m;
const decimal WatchMinVolume60Ratio = 1.40m;
const decimal WatchMinDirectionalShare = 0.56m;
const decimal WatchMinDirectionalCvd = 0.07m;
const decimal WatchMinPriceMovePercent = 0.018m;
const decimal WatchMinPullbackFraction = 0.18m;
const decimal WatchMaxPullbackFraction = 1.00m;
const decimal WatchDeepPullbackFraction = 0.80m;
const decimal ReaccelMinVolume5Ratio = 0.50m;
const decimal ReaccelMinVolume15Ratio = 0.72m;
const decimal ReaccelMinShare5 = 0.53m;
const decimal ReaccelMinShare15 = 0.55m;
const decimal ReaccelMinCvd5 = 0.00m;
const decimal ReaccelMinCvd15 = 0.025m;
const decimal ReaccelMinBounceAtr = 0.010m;
const decimal ReaccelMinBounceImpulseFraction = 0.025m;
const int FinalSignalHoldMilliseconds = 350;

// Больше не торгуем только микроволны около $1-2.
// Рабочий импульс выбирается из 15/30/60 секунд и должен быть
// минимум $2.50 и одновременно не меньше 0.14 ATR (WEEKEND).
const decimal MinImpulseRangeUsd = 2.50m;
const decimal PreferredImpulseRangeUsd = 4.50m;
const decimal MaxImpulseRangeAtr = 1.45m;
const decimal MinImpulseRangeAtr = 0.14m;

// Более глубокий ретест + более широкий структурный стоп.
const decimal EntryRetracementNear = 0.48m;
const decimal EntryRetracementDeep = 0.74m;
const decimal StopBufferAtr = 0.20m;
const decimal StopBufferImpulse = 0.12m;
const decimal MinStopDistanceAtr = 0.32m;
const decimal MinInitialRiskPercent = 0.25m;
const decimal MaxInitialRiskPercent = 0.85m;
const int EntryConfirmationMinMilliseconds = 700;
const decimal MinBounceAtr = 0.025m;

// Управление уже открытой сделкой.
const decimal BreakEvenAtR = 0.50m;
const decimal ProfitLockAtR = 0.78m;
const decimal ProfitLockR = 0.18m;
const int ReversalConfirmSeconds = 2;
const int StaleTradeMinutes = 12;

// Paper trading / модель исполнения
const decimal PaperNotionalUsdt = 1000m;

// ВАЖНО: укажи здесь свою реальную суммарную комиссию вход+выход.
// 0.11 = 0.11% за полный круг. Это лишь пример для paper-расчёта.
const decimal EstimatedRoundTripFeePercent = 0.11m;
const decimal MaxEntrySlippagePercent = 0.10m;

// История
const int MinimumHistorySamples = 20;
string HistoryFile = Path.Combine(AppContext.BaseDirectory, "trades_weekend_tuned_v2.json");
string WatchHistoryFile = Path.Combine(AppContext.BaseDirectory, "watch_history_weekend_tuned_v2.json");
string ChatIdFile = Path.Combine(AppContext.BaseDirectory, "telegram_chat_id.txt");

// Telegram token
string telegramToken = "8611038291:AAGkHJS8o3-z3U6MtUmPP1ZcHE7lYeFpco8";

// =====================================================
//                    ГЛОБАЛЬНОЕ СОСТОЯНИЕ
// =====================================================

// Telegram использует отдельный HttpClient с коротким временем жизни
// pooled-соединений. Это помогает после смены маршрута (VPN on/off, Wi-Fi и т.п.)
// не продолжать использовать старое мёртвое TCP-соединение.
CancellationTokenSource telegramBotCts = new CancellationTokenSource();
HttpClient telegramHttp = CreateTelegramHttpClient();
TelegramBotClient bot = new TelegramBotClient(telegramToken, telegramHttp, telegramBotCts.Token);

var telegramReconnectGate = new SemaphoreSlim(1, 1);
var telegramStateLock = new object();
const int TelegramFailuresBeforeReconnect = 3;
int telegramConsecutiveFailures = 0;
bool telegramReconnectRequested = false;
DateTime lastTelegramHealthyAt = DateTime.MinValue;
DateTime lastTelegramErrorAt = DateTime.MinValue;

long? chatId = LoadChatId();

using HttpClient http = new HttpClient
{
    BaseAddress = new Uri(BybitRestUrl),
    Timeout = TimeSpan.FromSeconds(10)
};

var tradeLock = new object();
var liquidationLock = new object();
var historyLock = new object();
var watchHistoryLock = new object();

var trades = new Queue<TradeTick>();
var liquidations = new Queue<LiquidationTick>();
var orderBook = new OrderBookState();
var ticker = new TickerState();

MarketContext market = new MarketContext();
ActiveTrade? activeTrade = null;
List<TradeRecord> tradeHistory = LoadTradeHistory();
List<WatchHistoryRecord> watchHistory = LoadWatchHistory();

DateTime lastTradeFinishedAt = DateTime.MinValue;
DateTime signalBlockUntil = DateTime.MinValue;
DateTime lastSignalEvaluationAt = DateTime.MinValue;
string? pendingSignalDirection = null;
DateTime pendingSignalSince = DateTime.MinValue;
DateTime pendingSignalLastSeen = DateTime.MinValue;

WatchCandidate? watchCandidate = null;
DateTime lastWatchFinishedAt = DateTime.MinValue;

var diagnosticsLock = new object();
var diagnostics = new SignalDiagnostics();

// =====================================================
//                       ЗАПУСК
// =====================================================

// Сначала подписываем обработчик ошибок, затем сообщения:
// подписка OnMessage автоматически запускает long polling Telegram.Bot.
bot.OnError += OnTelegramError;
bot.OnMessage += OnTelegramMessage;

try
{
    using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var me = await bot.GetMe(startupTimeout.Token);
    MarkTelegramHealthy();

    Console.WriteLine($"✅ Telegram-бот @{me.Username} запущен");

    if (chatId.HasValue)
    {
        try
        {
            await SendTelegramToChat(
                chatId.Value,
                "🟢 ETH-сканер WEEKEND запущен\n\n" +
                "Telegram подключён, Bybit-анализ запускается.\n" +
                "Команды: /status, /market, /stats, /watchstats, /diag, /resetstats"
            );

            Console.WriteLine($"✅ Стартовое сообщение отправлено в chat={chatId.Value}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Не удалось отправить стартовое сообщение: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("ℹ️ Chat ID ещё не сохранён. Один раз напиши боту /start — после этого при следующих запусках бот будет писать сам.");
    }
}
catch (Exception ex)
{
    MarkTelegramFailure();
    Console.WriteLine("⚠️ Telegram при старте недоступен. Bybit-сканер всё равно будет запущен.");
    Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("   Бот будет автоматически пытаться восстановить Telegram-соединение.");
}

Console.WriteLine($"📁 История: {tradeHistory.Count} сделок");
Console.WriteLine($"👀 WATCH-история: {watchHistory.Count} записей | {WatchHistoryFile}");
Console.WriteLine($"🌙 Режим стратегии: {StrategyMode} — weekend-пороги, отдельная статистика");
if (DateTime.UtcNow.DayOfWeek != DayOfWeek.Saturday && DateTime.UtcNow.DayOfWeek != DayOfWeek.Sunday)
{
    Console.WriteLine("⚠️ WEEKEND-скрипт запущен не в субботу/воскресенье. Он продолжит работать, но пороги рассчитаны именно на выходные.");
}
Console.WriteLine("Напиши боту /start");

// Отдельный watchdog проверяет Telegram и пересоздаёт клиент, если после
// переключения VPN/сети polling завис на старом маршруте.
_ = Task.Run(TelegramWatchdog);

market = await GetMarketContext();
await WatchBybit();

// =====================================================
//                 TELEGRAM-КОМАНДЫ
// =====================================================

async Task OnTelegramMessage(Message message, UpdateType type)
{
    // Сам факт прихода update означает, что long polling жив и Telegram доступен.
    MarkTelegramHealthy();

    if (message.Text == null)
        return;

    long targetChatId = message.Chat.Id;

    if (chatId != targetChatId)
    {
        chatId = targetChatId;
        SaveChatId(targetChatId);
        Console.WriteLine($"💾 Telegram Chat ID сохранён: {targetChatId}");
    }
    else
    {
        chatId = targetChatId;
    }

    // Telegram в группах может присылать команды как /resetstats@MyBot.
    // Также игнорируем аргументы после команды и лишние пробелы.
    string rawText = message.Text.Trim();
    string command = rawText
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?
        .ToLowerInvariant() ?? "";

    int mentionIndex = command.IndexOf('@');
    if (mentionIndex > 0)
        command = command[..mentionIndex];

    Console.WriteLine(
        $"📨 Telegram | chat={targetChatId} | text=\"{rawText}\" | command=\"{command}\""
    );

    try
    {
        if (command == "/start" || command == "/help")
        {
            await SendTelegramToChat(
                targetChatId,
                "✅ ETH-сканер WEEKEND запущен\n\n" +
                "💾 Этот чат сохранён. Теперь при следующих запусках скрипта бот сможет написать сюда автоматически.\n\n" +
                "🌙 Режим: WEEKEND — более мягкие пороги для выходной ликвидности.\n" +
                "Weekend-статистика хранится отдельно от будней.\n\n" +
                "Анализирую:\n" +
                "• поток сделок 5/15/30/60 сек\n" +
                "• CVD и ускорение объёма\n" +
                "• крупные сделки\n" +
                "• стакан и дисбаланс bid/ask\n" +
                "• приближённый OFI\n" +
                "• microprice и spread\n" +
                "• ликвидации\n" +
                "• Open Interest / funding\n" +
                "• mark/index divergence\n" +
                "• тренд 5/15 минут\n" +
                "• структуру рынка 5м/15м (HH/HL, LH/LL, BOS, CHOCH)\n" +
                "• VWAP и Volume Profile (POC/VAH/VAL)\n" +
                "• автоматические поддержки/сопротивления\n" +
                "• историческую статистику paper-сделок\n\n" +
                "Команды:\n" +
                "/status — текущий сигнал/сделка\n" +
                "/market — текущая картина рынка\n" +
                "/stats — статистика стратегии\n" +
                "/diag — диагностика поиска сигналов / WATCH\n" +
                "/watchstats — сводка WATCH-истории\n" +
                "/resetstats — сброс сделок/активного состояния\n" +
                "/resetwatchstats — очистить только WATCH-историю\n" +
                "/resetall — очистить сделки и WATCH-историю\n" +
                "/reset — короткий алиас /resetstats"
            );
            return;
        }

        if (command == "/status")
        {
            if (activeTrade != null)
            {
                await SendTelegramToChat(
                    targetChatId,
                    GetTradeStatusText(activeTrade)
                );
                return;
            }

            if (watchCandidate != null)
            {
                await SendTelegramToChat(
                    targetChatId,
                    GetWatchStatusText(watchCandidate)
                );
                return;
            }

            await SendTelegramToChat(
                targetChatId,
                "📊 Сейчас активной сделки или WATCH-кандидата нет.\n\n" +
                "Бот продолжает искать крупный импульс 30/60с."
            );
            return;
        }

        if (command == "/market")
        {
            await SendTelegramToChat(
                targetChatId,
                GetMarketText()
            );
            return;
        }

        if (command == "/stats")
        {
            await SendTelegramToChat(
                targetChatId,
                GetStatsText()
            );
            return;
        }

        if (command == "/diag" || command == "/why")
        {
            await SendTelegramToChat(
                targetChatId,
                GetDiagnosticsText()
            );
            return;
        }

        if (command == "/watchstats")
        {
            await SendTelegramToChat(
                targetChatId,
                GetWatchStatsText()
            );
            return;
        }

        if (command == "/resetwatchstats")
        {
            int removed = ResetWatchHistory();

            await SendTelegramToChat(
                targetChatId,
                "🧹 WATCH-ИСТОРИЯ ОЧИЩЕНА\n\n" +
                $"Удалено записей: {removed}\n" +
                $"Файл: {WatchHistoryFile}"
            );
            return;
        }

        if (command == "/resetall")
        {
            ResetStatistics();
            int removedWatch = ResetWatchHistory();

            await SendTelegramToChat(
                targetChatId,
                "🧹 ПОЛНЫЙ СБРОС ВСЕХ ДАННЫХ\n\n" +
                "✅ trades_weekend.json очищен\n" +
                "✅ watch_history_weekend.json очищен\n" +
                "✅ Активная сделка/WATCH отменены\n\n" +
                $"Удалено WATCH-записей: {removedWatch}\n" +
                $"WATCH-файл: {WatchHistoryFile}"
            );
            return;
        }

        if (
            command == "/resetstats" ||
            command == "/reset" ||
            command == "/clearstats" ||
            command == "/сброс")
        {
            bool hadActiveTrade = activeTrade != null;
            int oldHistoryCount;

            lock (historyLock)
            {
                oldHistoryCount = tradeHistory.Count;
            }

            Console.WriteLine(
                $"🧹 Получена команда сброса. История до сброса: {oldHistoryCount}, " +
                $"активная сделка: {(hadActiveTrade ? "да" : "нет")}, файл: {HistoryFile}"
            );

            ResetStatistics();

            int newHistoryCount;
            lock (historyLock)
            {
                newHistoryCount = tradeHistory.Count;
            }

            // Дополнительная проверка: файл должен существовать и содержать пустой JSON-массив.
            string fileState;
            try
            {
                fileState = File.Exists(HistoryFile)
                    ? $"существует, {new FileInfo(HistoryFile).Length} байт"
                    : "не существует";
            }
            catch
            {
                fileState = "не удалось проверить";
            }

            await SendTelegramToChat(
                targetChatId,
                "🧹 ПОЛНЫЙ СБРОС ВЫПОЛНЕН\n\n" +
                $"Удалено сделок из статистики: {oldHistoryCount}\n" +
                $"Сделок после сброса: {newHistoryCount}\n\n" +
                "✅ История paper-сделок очищена\n" +
                "✅ trades_weekend.json перезаписан\n" +
                "✅ Текущая активная сделка/сигнал отменены\n" +
                "✅ WATCH-кандидат и ожидающий сигнал сброшены\n" +
                "✅ Диагностика /diag обнулена\n" +
                "✅ Cooldown сброшен\n\n" +
                $"Файл: {HistoryFile}\n" +
                $"Состояние файла: {fileState}\n\n" +
                (hadActiveTrade
                    ? "Активная paper-сделка была удалена из отслеживания."
                    : "Активной сделки на момент сброса не было.")
            );

            return;
        }

        // Если пользователь отправил неизвестную slash-команду, больше не молчим.
        if (command.StartsWith('/'))
        {
            await SendTelegramToChat(
                targetChatId,
                $"❓ Неизвестная команда: {command}\n\n" +
                "Доступно: /status, /market, /stats, /watchstats, /diag, /resetstats, /resetwatchstats, /resetall"
            );
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("❌ ОШИБКА ОБРАБОТКИ TELEGRAM-КОМАНДЫ");
        Console.WriteLine($"Команда: {command}");
        Console.WriteLine($"Тип: {ex.GetType().Name}");
        Console.WriteLine($"Ошибка: {ex.Message}");

        try
        {
            await SendTelegramToChat(
                targetChatId,
                "❌ Не удалось выполнить команду. Ошибка записана в консоль бота."
            );
        }
        catch
        {
        }
    }
}

// =====================================================
//                  BYBIT WEBSOCKET
// =====================================================

async Task WatchBybit()
{
    while (true)
    {
        CancellationTokenSource? heartbeatCts = null;

        try
        {
            using ClientWebSocket socket = new ClientWebSocket();

            await socket.ConnectAsync(
                new Uri(BybitWsUrl),
                CancellationToken.None
            );

            Console.WriteLine("✅ WebSocket Bybit подключён");

            string subscription = $$"""
            {
                "op": "subscribe",
                "args": [
                    "publicTrade.{{Symbol}}",
                    "orderbook.50.{{Symbol}}",
                    "allLiquidation.{{Symbol}}",
                    "tickers.{{Symbol}}"
                ]
            }
            """;

            await SendWebSocketText(socket, subscription, CancellationToken.None);

            heartbeatCts = new CancellationTokenSource();
            _ = Task.Run(() => Heartbeat(socket, heartbeatCts.Token));

            DateTime nextMarketRefresh = DateTime.UtcNow.AddMinutes(1);

            while (socket.State == WebSocketState.Open)
            {
                string text = await ReceiveText(socket);

                if (DateTime.UtcNow >= nextMarketRefresh)
                {
                    try
                    {
                        market = await GetMarketContext();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Обновление рынка: {ex.Message}");
                    }

                    nextMarketRefresh = DateTime.UtcNow.AddMinutes(1);
                }

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                using JsonDocument json = JsonDocument.Parse(text);
                JsonElement root = json.RootElement;

                if (!root.TryGetProperty("topic", out JsonElement topicElement))
                    continue;

                string topic = topicElement.GetString() ?? "";
                long ts = root.TryGetProperty("ts", out JsonElement tsElement)
                    ? tsElement.GetInt64()
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (topic.StartsWith("publicTrade."))
                {
                    if (root.TryGetProperty("data", out JsonElement data))
                        await ProcessTrades(data);

                    if ((DateTime.UtcNow - lastSignalEvaluationAt).TotalMilliseconds >= 250)
                    {
                        lastSignalEvaluationAt = DateTime.UtcNow;
                        await EvaluateSignal();
                    }
                }
                else if (topic.StartsWith("orderbook."))
                {
                    string typeName = root.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString() ?? "delta"
                        : "delta";

                    if (root.TryGetProperty("data", out JsonElement data))
                        orderBook.Apply(typeName, data, ts);
                }
                else if (topic.StartsWith("allLiquidation."))
                {
                    if (root.TryGetProperty("data", out JsonElement data))
                        ProcessLiquidations(data);
                }
                else if (topic.StartsWith("tickers."))
                {
                    if (root.TryGetProperty("data", out JsonElement data))
                        ticker.Update(data, ts);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Bybit: {ex.Message}");
            Console.WriteLine("Переподключение через 3 секунды...");
        }
        finally
        {
            heartbeatCts?.Cancel();
            heartbeatCts?.Dispose();
        }

        await Task.Delay(3000);
    }
}

// =====================================================
//                     TRADE FLOW
// =====================================================

async Task ProcessTrades(JsonElement data)
{
    foreach (JsonElement item in data.EnumerateArray())
    {
        // Block trades могут дать огромный объём без обычного воздействия на стакан
        // и раньше создавали ложные volume-spike сигналы. Их не учитываем.
        if (item.TryGetProperty("BT", out JsonElement bt) && bt.ValueKind == JsonValueKind.True)
            continue;

        long timeMs = item.GetProperty("T").GetInt64();
        decimal price = ParseStringDecimal(item.GetProperty("p"));
        decimal size = ParseStringDecimal(item.GetProperty("v"));
        string side = item.GetProperty("S").GetString() ?? "";
        decimal turnover = price * size;

        lock (tradeLock)
        {
            trades.Enqueue(new TradeTick
            {
                TimeMs = timeMs,
                Time = DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime,
                Price = price,
                Size = size,
                Side = side,
                Turnover = turnover
            });

            TrimTradesUnsafe();
        }

        await CheckActiveTrade(price);
    }
}

void TrimTradesUnsafe()
{
    DateTime cutoff = DateTime.UtcNow.AddSeconds(-70);

    while (trades.Count > 0 && trades.Peek().Time < cutoff)
        trades.Dequeue();
}

FlowMetrics GetFlowMetrics(int seconds)
{
    var result = new FlowMetrics { WindowSeconds = seconds };
    DateTime cutoff = DateTime.UtcNow.AddSeconds(-seconds);
    List<TradeTick> snapshot;

    lock (tradeLock)
    {
        snapshot = trades.Where(x => x.Time >= cutoff).ToList();
    }

    if (snapshot.Count == 0)
        return result;

    result.Valid = true;
    result.FirstPrice = snapshot[0].Price;
    result.LastPrice = snapshot[^1].Price;
    result.High = snapshot.Max(x => x.Price);
    result.Low = snapshot.Min(x => x.Price);
    result.TotalTurnover = snapshot.Sum(x => x.Turnover);
    result.TotalVolume = snapshot.Sum(x => x.Size);
    result.BuyTurnover = snapshot.Where(x => x.Side == "Buy").Sum(x => x.Turnover);
    result.SellTurnover = snapshot.Where(x => x.Side == "Sell").Sum(x => x.Turnover);
    result.Cvd = result.BuyTurnover - result.SellTurnover;
    result.CvdNormalized = result.TotalTurnover > 0 ? result.Cvd / result.TotalTurnover : 0;
    result.BuyShare = result.TotalTurnover > 0 ? result.BuyTurnover / result.TotalTurnover : 0;
    result.SellShare = result.TotalTurnover > 0 ? result.SellTurnover / result.TotalTurnover : 0;
    result.PriceChangePercent = result.FirstPrice > 0
        ? (result.LastPrice - result.FirstPrice) / result.FirstPrice * 100m
        : 0;
    result.Vwap = result.TotalVolume > 0 ? result.TotalTurnover / result.TotalVolume : result.LastPrice;
    result.TradesPerSecond = snapshot.Count / (decimal)seconds;

    decimal range = result.High - result.Low;
    result.Efficiency = range > 0
        ? Math.Abs(result.LastPrice - result.FirstPrice) / range
        : 0m;
    result.CloseLocation = range > 0
        ? (result.LastPrice - result.Low) / range
        : 0.5m;

    decimal baseline = Math.Max(market.EffectiveMinuteTurnover, 1m);
    result.VolumeRatio = result.TotalTurnover / (baseline * seconds / 60m);

    decimal largeThreshold = Math.Max(50_000m, baseline * 0.005m);
    result.LargeBuyTurnover = snapshot
        .Where(x => x.Side == "Buy" && x.Turnover >= largeThreshold)
        .Sum(x => x.Turnover);
    result.LargeSellTurnover = snapshot
        .Where(x => x.Side == "Sell" && x.Turnover >= largeThreshold)
        .Sum(x => x.Turnover);

    decimal largeTotal = result.LargeBuyTurnover + result.LargeSellTurnover;
    result.LargeBuyShare = largeTotal > 0 ? result.LargeBuyTurnover / largeTotal : 0.5m;
    result.LargeSellShare = largeTotal > 0 ? result.LargeSellTurnover / largeTotal : 0.5m;
    result.LargeTurnoverShare = result.TotalTurnover > 0 ? largeTotal / result.TotalTurnover : 0m;

    return result;
}

// =====================================================
//                    ЛИКВИДАЦИИ
// =====================================================

void ProcessLiquidations(JsonElement data)
{
    foreach (JsonElement item in data.EnumerateArray())
    {
        long timeMs = item.GetProperty("T").GetInt64();
        decimal price = ParseStringDecimal(item.GetProperty("p"));
        decimal size = ParseStringDecimal(item.GetProperty("v"));
        string side = item.GetProperty("S").GetString() ?? "";

        // По документации Bybit:
        // S=Buy  -> ликвидирован LONG
        // S=Sell -> ликвидирован SHORT
        lock (liquidationLock)
        {
            liquidations.Enqueue(new LiquidationTick
            {
                Time = DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime,
                PositionSide = side,
                Turnover = price * size
            });

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-70);
            while (liquidations.Count > 0 && liquidations.Peek().Time < cutoff)
                liquidations.Dequeue();
        }
    }
}

LiquidationMetrics GetLiquidationMetrics(int seconds)
{
    DateTime cutoff = DateTime.UtcNow.AddSeconds(-seconds);
    List<LiquidationTick> snapshot;

    lock (liquidationLock)
    {
        snapshot = liquidations.Where(x => x.Time >= cutoff).ToList();
    }

    return new LiquidationMetrics
    {
        LongLiquidations = snapshot.Where(x => x.PositionSide == "Buy").Sum(x => x.Turnover),
        ShortLiquidations = snapshot.Where(x => x.PositionSide == "Sell").Sum(x => x.Turnover)
    };
}

// =====================================================
//                 REST MARKET CONTEXT
// =====================================================

async Task<MarketContext> GetMarketContext()
{
    int limit = Math.Min(1000, Math.Max(MarketContextLookbackMinutes + 2, AtrPeriod + 2));

    string url =
        $"/v5/market/kline?category=linear&symbol={Symbol}&interval=1&limit={limit}";

    string response = await http.GetStringAsync(url);
    using JsonDocument json = JsonDocument.Parse(response);

    int retCode = json.RootElement.GetProperty("retCode").GetInt32();
    if (retCode != 0)
    {
        string retMsg = json.RootElement.GetProperty("retMsg").GetString() ?? "Unknown";
        throw new Exception($"Bybit {retCode}: {retMsg}");
    }

    JsonElement list = json.RootElement.GetProperty("result").GetProperty("list");

    // Bybit отдаёт свечи от новых к старым. Для теханализа переводим их
    // в хронологический порядок и не используем текущую незакрытую свечу list[0].
    var minuteCandles = new List<Candle>();

    for (int i = list.GetArrayLength() - 1; i >= 1; i--)
    {
        JsonElement row = list[i];

        minuteCandles.Add(new Candle
        {
            StartMs = long.Parse(row[0].GetString()!, CultureInfo.InvariantCulture),
            Open = ParseStringDecimal(row[1]),
            High = ParseStringDecimal(row[2]),
            Low = ParseStringDecimal(row[3]),
            Close = ParseStringDecimal(row[4]),
            Volume = ParseStringDecimal(row[5]),
            Turnover = ParseStringDecimal(row[6])
        });
    }

    if (minuteCandles.Count < Math.Max(BaselineMinutes, AtrPeriod + 2))
        throw new Exception("Недостаточно закрытых минутных свечей для анализа.");

    List<Candle> baselineCandles = minuteCandles
        .TakeLast(Math.Min(BaselineMinutes, minuteCandles.Count))
        .ToList();

    var turnovers = baselineCandles.Select(x => x.Turnover).ToList();

    decimal mean = turnovers.Count > 0 ? turnovers.Average() : 0;
    decimal median = Median(turnovers);
    decimal fastMedian = Median(turnovers.TakeLast(Math.Min(15, turnovers.Count)).ToList());
    decimal mad = Median(turnovers.Select(x => Math.Abs(x - median)).ToList());
    decimal robust = median * 0.70m + mean * 0.30m;
    decimal effectiveTurnover = Math.Max(robust, fastMedian * 0.90m);

    decimal totalTr = 0m;
    int trCount = 0;

    for (int i = minuteCandles.Count - AtrPeriod; i < minuteCandles.Count; i++)
    {
        if (i <= 0)
            continue;

        Candle c = minuteCandles[i];
        decimal prevClose = minuteCandles[i - 1].Close;

        decimal tr = Math.Max(
            c.High - c.Low,
            Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))
        );

        totalTr += tr;
        trCount++;
    }

    decimal atr = trCount > 0 ? totalTr / trCount : 0m;
    decimal lastClosed = minuteCandles[^1].Close;

    decimal close5mAgo = minuteCandles.Count > 5
        ? minuteCandles[^6].Close
        : lastClosed;

    decimal close15mAgo = minuteCandles.Count > 15
        ? minuteCandles[^16].Close
        : lastClosed;

    decimal trend5 = close5mAgo > 0 ? (lastClosed - close5mAgo) / close5mAgo * 100m : 0m;
    decimal trend15 = close15mAgo > 0 ? (lastClosed - close15mAgo) / close15mAgo * 100m : 0m;

    var closes = minuteCandles.Select(x => x.Close).ToList();
    decimal ema9 = Ema(closes, 9);
    decimal ema21 = Ema(closes, 21);
    decimal ema50 = Ema(closes, 50);
    decimal atrPercent = lastClosed > 0 ? atr / lastClosed * 100m : 0m;
    decimal emaSpreadAtr = atr > 0 ? (ema9 - ema21) / atr : 0m;

    // Rolling VWAP + Volume Profile по последним N закрытым минутам.
    List<Candle> profileCandles = minuteCandles
        .TakeLast(Math.Min(VolumeProfileMinutes, minuteCandles.Count))
        .ToList();

    decimal profileVolume = profileCandles.Sum(x => x.Volume);
    decimal profileTurnover = profileCandles.Sum(x => x.Turnover);
    decimal rollingVwap = profileVolume > 0 ? profileTurnover / profileVolume : lastClosed;

    VolumeProfile profile = BuildVolumeProfile(profileCandles, VolumeProfileBins, ValueAreaPercent);

    List<Candle> candles5m = AggregateCandles(minuteCandles, 5);
    List<Candle> candles15m = AggregateCandles(minuteCandles, 15);

    StructureAnalysis structure5m = AnalyzeStructure(candles5m, 2);
    StructureAnalysis structure15m = AnalyzeStructure(candles15m, 2);

    TechnicalLevels levels = BuildTechnicalLevels(
        lastClosed,
        atr,
        rollingVwap,
        profile,
        structure5m,
        structure15m
    );

    return new MarketContext
    {
        MeanMinuteTurnover = mean,
        MedianMinuteTurnover = median,
        MadMinuteTurnover = mad,
        RobustMinuteTurnover = Math.Max(robust, 1m),
        FastMinuteTurnover = Math.Max(fastMedian, 1m),
        EffectiveMinuteTurnover = Math.Max(effectiveTurnover, 1m),
        Atr = atr,
        AtrPercent = atrPercent,
        Trend5mPercent = trend5,
        Trend15mPercent = trend15,
        Ema9 = ema9,
        Ema21 = ema21,
        Ema50 = ema50,
        EmaSpreadAtr = emaSpreadAtr,
        LastClosedPrice = lastClosed,
        RollingVwap = rollingVwap,
        Poc = profile.Poc,
        Vah = profile.Vah,
        Val = profile.Val,
        ProfileLow = profile.Low,
        ProfileHigh = profile.High,
        ValueAreaPosition = profile.Valid && profile.Vah > profile.Val
            ? Math.Clamp((lastClosed - profile.Val) / (profile.Vah - profile.Val), 0m, 1m)
            : 0.5m,
        NearestSupport = levels.Support,
        NearestResistance = levels.Resistance,
        Structure5m = structure5m,
        Structure15m = structure15m,
        UpdatedAt = DateTime.UtcNow
    };
}

// =====================================================
//                  ОЦЕНКА СИГНАЛА
// =====================================================

async Task EvaluateSignal()
{
    if (chatId == null || activeTrade != null)
        return;

    if (DateTime.UtcNow - lastTradeFinishedAt < TimeSpan.FromSeconds(SignalCooldownSeconds) ||
        DateTime.UtcNow < signalBlockUntil)
        return;

    FlowMetrics f5 = GetFlowMetrics(5);
    FlowMetrics f15 = GetFlowMetrics(15);
    FlowMetrics f30 = GetFlowMetrics(30);
    FlowMetrics f60 = GetFlowMetrics(60);

    if (!f5.Valid || !f15.Valid || !f30.Valid || f15.TotalTurnover <= 0)
        return;

    OrderBookMetrics book = orderBook.GetMetrics();
    TickerSnapshot tickerSnapshot = ticker.GetSnapshot();
    LiquidationMetrics liq = GetLiquidationMetrics(30);
    decimal oiChange30 = ticker.GetOpenInterestChangePercent(30);

    if (!book.Valid)
        return;

    UpdateDiagnosticsSnapshot(f5, f15, f30, f60, book, "Анализ рынка");

    // ЭТАП 2: если большой импульс уже был найден, не ищем новый.
    // Ждём откат и повторное ускорение в направлении исходной волны.
    if (watchCandidate != null)
    {
        await EvaluateWatchCandidate(
            f5,
            f15,
            f30,
            f60,
            book,
            tickerSnapshot,
            liq,
            oiChange30
        );
        return;
    }

    if (DateTime.UtcNow - lastWatchFinishedAt < TimeSpan.FromSeconds(WatchCooldownSeconds))
        return;

    // ЭТАП 1: ищем НЕ точку входа, а сформированный 30/60-секундный импульс.
    if (!TryFindWatchImpulse(f15, f30, f60, out string direction, out FlowMetrics impulse))
    {
        SetDiagnosticReason("Нет подходящего 30/60с импульса для WATCH.");
        return;
    }

    IncrementDiagnosticDirectedCandidate();

    decimal equivalentMinuteTurnover =
        impulse.TotalTurnover * (60m / Math.Max(impulse.WindowSeconds, 1));

    decimal volumeZ = RobustZScore(
        equivalentMinuteTurnover,
        market.MedianMinuteTurnover,
        market.MadMinuteTurnover
    );

    bool volumeAnomaly =
        (impulse.WindowSeconds >= 60 && impulse.VolumeRatio >= WatchMinVolume60Ratio) ||
        (impulse.WindowSeconds >= 30 && impulse.VolumeRatio >= WatchMinVolume30Ratio) ||
        volumeZ >= 3.25m;

    if (!volumeAnomaly)
    {
        SetDiagnosticReason(
            $"Импульс есть, но объём недостаточно аномальный: {impulse.WindowSeconds}с x{impulse.VolumeRatio:F2}, Z={volumeZ:F1}."
        );
        return;
    }

    if (!WatchContextAllowsDirection(direction, impulse, book, oiChange30))
    {
        SetDiagnosticReason("Импульс отклонён контекстом рынка до создания WATCH.");
        return;
    }

    decimal range = impulse.High - impulse.Low;
    decimal minimumRange = Math.Max(MinImpulseRangeUsd, market.Atr * MinImpulseRangeAtr);

    if (range < minimumRange)
    {
        SetDiagnosticReason($"Диапазон импульса слишком мал: ${range:F2}, минимум ${minimumRange:F2}.");
        return;
    }

    decimal currentPrice = f5.LastPrice > 0 ? f5.LastPrice : impulse.LastPrice;

    watchCandidate = new WatchCandidate
    {
        Id = Guid.NewGuid().ToString("N"),
        Direction = direction,
        CreatedAt = DateTime.UtcNow,
        Impulse = CloneFlowMetrics(impulse),
        VolumeZ = volumeZ,
        InitialPrice = currentPrice,
        HighestPrice = Math.Max(impulse.High, currentPrice),
        LowestPrice = Math.Min(impulse.Low, currentPrice),
        PullbackExtreme = currentPrice,
        LastF5VolumeRatio = f5.VolumeRatio,
        LastF15VolumeRatio = f15.VolumeRatio,
        InitialBookImbalance = book.Imbalance,
        InitialOiChange30 = oiChange30
    };

    RegisterWatchStart(watchCandidate, book, oiChange30);

    lock (diagnosticsLock)
    {
        diagnostics.WatchesCreated++;
        diagnostics.LastReason = $"Создан WATCH {direction}: импульс {impulse.WindowSeconds}с, x{impulse.VolumeRatio:F2}.";
        diagnostics.LastReasonAt = DateTime.UtcNow;
    }

    string emoji = direction == "LONG" ? "👀🟢" : "👀🔴";
    string sideText = direction == "LONG"
        ? $"Покупки: {impulse.BuyShare:P1}"
        : $"Продажи: {impulse.SellShare:P1}";

    await SendTelegram(
        $"{emoji} WATCH {direction} | {Symbol}\n\n" +
        "Большой импульс обнаружен, но сделку пока НЕ открываю.\n" +
        "Жду откат и повторное ускорение потока.\n\n" +
        $"💰 Цена: ${currentPrice:F2}\n" +
        $"🌊 Импульс: {impulse.WindowSeconds}с | диапазон ${range:F2}\n" +
        $"📊 Объём: x{impulse.VolumeRatio:F2} | Z={volumeZ:F1}\n" +
        $"{sideText}\n" +
        $"CVD: {impulse.CvdNormalized:+0.00;-0.00}\n" +
        $"Изменение цены: {impulse.PriceChangePercent:+0.000;-0.000}%\n\n" +
        $"⏳ WATCH действует до {WatchLifetimeSeconds} секунд.\n" +
        "Сигнал будет отправлен только после нормального pullback и нового 5/15с ускорения."
    );
}

// =====================================================
//       ЭТАП 1: ПОИСК БОЛЬШОГО ИМПУЛЬСА ДЛЯ WATCH
// =====================================================

bool TryFindWatchImpulse(
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60,
    out string direction,
    out FlowMetrics impulse)
{
    direction = "";
    impulse = new FlowMetrics();

    decimal bestScore = decimal.MinValue;

    foreach (string candidateDirection in new[] { "LONG", "SHORT" })
    {
        bool isLong = candidateDirection == "LONG";
        FlowMetrics selected = SelectImpulseFlowForWatch(candidateDirection, f15, f30, f60);

        if (!selected.Valid)
            continue;

        decimal share = isLong ? selected.BuyShare : selected.SellShare;
        decimal cvd = isLong ? selected.CvdNormalized : -selected.CvdNormalized;
        decimal priceMove = isLong ? selected.PriceChangePercent : -selected.PriceChangePercent;
        decimal range = selected.High - selected.Low;

        bool volumeOk =
            (selected.WindowSeconds >= 60 && selected.VolumeRatio >= WatchMinVolume60Ratio) ||
            (selected.WindowSeconds >= 30 && selected.VolumeRatio >= WatchMinVolume30Ratio);

        if (!volumeOk ||
            share < WatchMinDirectionalShare ||
            cvd < WatchMinDirectionalCvd ||
            priceMove < WatchMinPriceMovePercent)
        {
            continue;
        }

        decimal score =
            selected.VolumeRatio * 22m +
            share * 25m +
            cvd * 18m +
            Math.Min(priceMove / 0.15m, 2m) * 12m +
            Math.Min(range / Math.Max(PreferredImpulseRangeUsd, 1m), 2m) * 10m +
            selected.Efficiency * 8m;

        if (selected.WindowSeconds >= 60)
            score += 3m;

        if (score > bestScore)
        {
            bestScore = score;
            direction = candidateDirection;
            impulse = selected;
        }
    }

    return impulse.Valid && !string.IsNullOrEmpty(direction);
}

FlowMetrics SelectImpulseFlowForWatch(
    string direction,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60)
{
    bool isLong = direction == "LONG";
    decimal minimumRange = Math.Max(MinImpulseRangeUsd, market.Atr * MinImpulseRangeAtr);
    decimal maximumRange = market.Atr > 0
        ? market.Atr * MaxImpulseRangeAtr * 1.35m
        : decimal.MaxValue;

    FlowMetrics? best = null;
    decimal bestScore = decimal.MinValue;

    foreach (FlowMetrics flow in new[] { f30, f60, f15 })
    {
        if (!flow.Valid || flow.TotalTurnover <= 0)
            continue;

        decimal range = flow.High - flow.Low;
        if (range < minimumRange || (market.Atr > 0 && range > maximumRange))
            continue;

        decimal share = isLong ? flow.BuyShare : flow.SellShare;
        decimal cvd = isLong ? flow.CvdNormalized : -flow.CvdNormalized;
        decimal priceMove = isLong ? flow.PriceChangePercent : -flow.PriceChangePercent;
        decimal closeLocation = isLong ? flow.CloseLocation : 1m - flow.CloseLocation;

        if (share < 0.56m || cvd < 0.06m || priceMove <= 0m)
            continue;

        decimal score =
            flow.VolumeRatio * 25m +
            share * 20m +
            cvd * 15m +
            Math.Clamp(closeLocation, 0m, 1m) * 12m +
            Math.Clamp(flow.Efficiency, 0m, 1m) * 10m +
            Math.Clamp(range / Math.Max(PreferredImpulseRangeUsd, minimumRange), 0m, 2m) * 10m;

        if (flow.WindowSeconds >= 30)
            score += 4m;

        if (best == null || score > bestScore)
        {
            best = flow;
            bestScore = score;
        }
    }

    return best ?? new FlowMetrics();
}

bool WatchContextAllowsDirection(
    string direction,
    FlowMetrics impulse,
    OrderBookMetrics book,
    decimal oiChange30)
{
    bool isLong = direction == "LONG";

    if (book.SpreadBps > MaxAllowedSpreadBps * 1.25m)
        return false;

    // На стадии WATCH стакан не обязан уже подтверждать направление,
    // но он не должен быть экстремально противоположным.
    decimal bookDir = isLong ? book.Imbalance : 1m - book.Imbalance;
    decimal persistentDir = isLong ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;

    if (bookDir < 0.35m && persistentDir < 0.38m)
        return false;

    if (oiChange30 < -0.15m)
        return false;

    // Запрещаем только явно противоположную двойную старшую структуру.
    bool bothOpposite = isLong
        ? market.Structure5m.Bias == "МЕДВЕЖЬЯ" && market.Structure15m.Bias == "МЕДВЕЖЬЯ"
        : market.Structure5m.Bias == "БЫЧЬЯ" && market.Structure15m.Bias == "БЫЧЬЯ";

    if (bothOpposite)
        return false;

    decimal directionalClose = isLong ? impulse.CloseLocation : 1m - impulse.CloseLocation;
    if (directionalClose < 0.48m)
        return false;

    return true;
}

// =====================================================
//          ЭТАП 2: PULLBACK -> RE-ACCELERATION
// =====================================================

async Task EvaluateWatchCandidate(
    FlowMetrics f5,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60,
    OrderBookMetrics book,
    TickerSnapshot tickerSnapshot,
    LiquidationMetrics liq,
    decimal oiChange30)
{
    WatchCandidate watch = watchCandidate!;
    DateTime now = DateTime.UtcNow;
    decimal currentPrice = f5.LastPrice > 0 ? f5.LastPrice : f15.LastPrice;

    if (currentPrice <= 0)
        return;

    if (now - watch.CreatedAt > TimeSpan.FromSeconds(WatchLifetimeSeconds))
    {
        await EndWatchCandidate("⌛ WATCH истёк: откат/повторное ускорение не сформировались вовремя.", false);
        return;
    }

    decimal originalImpulseRange = Math.Max(watch.Impulse.High - watch.Impulse.Low, 0.01m);
    decimal invalidationBuffer = Math.Max(market.Atr * 0.12m, originalImpulseRange * 0.08m);

    if (watch.Direction == "LONG")
    {
        bool newHighBeforePullback = !watch.PullbackSeen && currentPrice > watch.HighestPrice;
        watch.HighestPrice = Math.Max(watch.HighestPrice, currentPrice);
        watch.LowestPrice = Math.Min(watch.LowestPrice, currentPrice);

        if (newHighBeforePullback)
        {
            // Пока откат ещё не начался, новый high становится новой точкой отсчёта.
            // Иначе обычное продолжение роста можно ошибочно принять за pullback.
            watch.PullbackExtreme = currentPrice;
        }
        else
        {
            watch.PullbackExtreme = watch.PullbackExtreme <= 0
                ? currentPrice
                : Math.Min(watch.PullbackExtreme, currentPrice);
        }

        if (currentPrice <= watch.Impulse.Low - invalidationBuffer)
        {
            await EndWatchCandidate("❌ WATCH LONG отменён: цена сломала основание исходного импульса.", true);
            return;
        }
    }
    else
    {
        bool newLowBeforePullback = !watch.PullbackSeen && currentPrice < watch.LowestPrice;
        watch.LowestPrice = Math.Min(watch.LowestPrice, currentPrice);
        watch.HighestPrice = Math.Max(watch.HighestPrice, currentPrice);

        if (newLowBeforePullback)
        {
            watch.PullbackExtreme = currentPrice;
        }
        else
        {
            watch.PullbackExtreme = watch.PullbackExtreme <= 0
                ? currentPrice
                : Math.Max(watch.PullbackExtreme, currentPrice);
        }

        if (currentPrice >= watch.Impulse.High + invalidationBuffer)
        {
            await EndWatchCandidate("❌ WATCH SHORT отменён: цена сломала вершину исходного импульса.", true);
            return;
        }
    }

    decimal referenceExtreme = watch.Direction == "LONG"
        ? Math.Max(watch.Impulse.High, watch.HighestPrice)
        : Math.Min(watch.Impulse.Low, watch.LowestPrice);

    decimal pullbackDistance = watch.Direction == "LONG"
        ? referenceExtreme - watch.PullbackExtreme
        : watch.PullbackExtreme - referenceExtreme;

    // ВАЖНО: раньше глубина отката делилась на диапазон ПЕРВОНАЧАЛЬНОГО импульса.
    // Если после создания WATCH цена ещё продолжала движение и обновляла экстремум,
    // процент отката искусственно раздувался (например, 90-100%), хотя основание
    // структуры ещё не было сломано. Теперь знаменатель расширяется вместе с экстремумом.
    decimal dynamicImpulseRange = watch.Direction == "LONG"
        ? Math.Max(referenceExtreme - watch.Impulse.Low, originalImpulseRange)
        : Math.Max(watch.Impulse.High - referenceExtreme, originalImpulseRange);

    decimal pullbackFraction = Math.Max(0m, pullbackDistance / Math.Max(dynamicImpulseRange, 0.01m));
    watch.PullbackFraction = Math.Max(watch.PullbackFraction, pullbackFraction);

    // Если цена полностью съела импульс, идея продолжения больше невалидна.
    // Раньше такой WATCH мог жить до структурного buffer и зря занимать 5 минут.
    if (watch.PullbackSeen && pullbackFraction > WatchMaxPullbackFraction)
    {
        await EndWatchCandidate($"❌ WATCH {watch.Direction} отменён: откат превысил 100% импульса.", true);
        return;
    }

    if (!watch.PullbackSeen &&
        pullbackFraction >= WatchMinPullbackFraction &&
        pullbackFraction <= WatchMaxPullbackFraction)
    {
        watch.PullbackSeen = true;
        watch.PullbackSeenAt = now;
        watch.PullbackBaseF5VolumeRatio = Math.Max(0.01m, f5.VolumeRatio);
        watch.PullbackBaseF15VolumeRatio = Math.Max(0.01m, f15.VolumeRatio);

        UpdateWatchHistoryPullback(watch);

        lock (diagnosticsLock)
        {
            diagnostics.PullbacksSeen++;
            diagnostics.LastReason = $"WATCH {watch.Direction}: pullback {pullbackFraction:P0} найден, жду повторное ускорение.";
            diagnostics.LastReasonAt = now;
        }

        Console.WriteLine(
            $"👀 WATCH {watch.Direction}: pullback {pullbackFraction:P0}, extreme ${watch.PullbackExtreme:F2}"
        );

        await SendTelegram(
            $"↩️ WATCH {watch.Direction}: откат найден\n\n" +
            $"Глубина отката: {pullbackFraction:P0}\n" +
            $"Экстремум отката: ${watch.PullbackExtreme:F2}\n\n" +
            "Теперь жду повторное ускорение объёма/потока. Сделка пока НЕ открыта."
        );
    }

    if (!watch.PullbackSeen)
    {
        SetDiagnosticReason($"WATCH {watch.Direction}: жду pullback, сейчас глубина {pullbackFraction:P0}.");
        watch.LastF5VolumeRatio = f5.VolumeRatio;
        watch.LastF15VolumeRatio = f15.VolumeRatio;
        return;
    }

    bool isLong = watch.Direction == "LONG";
    decimal share5 = isLong ? f5.BuyShare : f5.SellShare;
    decimal share15 = isLong ? f15.BuyShare : f15.SellShare;
    decimal cvd5 = isLong ? f5.CvdNormalized : -f5.CvdNormalized;
    decimal cvd15 = isLong ? f15.CvdNormalized : -f15.CvdNormalized;
    decimal priceMove5 = isLong ? f5.PriceChangePercent : -f5.PriceChangePercent;

    decimal bounceDistance = isLong
        ? currentPrice - watch.PullbackExtreme
        : watch.PullbackExtreme - currentPrice;

    decimal minBounce = Math.Max(
        market.Atr * ReaccelMinBounceAtr,
        originalImpulseRange * ReaccelMinBounceImpulseFraction
    );

    // Глубокий откат больше не отменяет WATCH автоматически.
    // Вместо этого включается строгий режим подтверждения: рынок должен сильнее
    // отскочить и подтвердить направление одновременно объёмом, потоком и стаканом.
    bool deepPullback = watch.PullbackFraction >= WatchDeepPullbackFraction;
    decimal requiredBounce = minBounce * (deepPullback ? 1.25m : 1.00m);
    bool priceBounce = bounceDistance >= requiredBounce && priceMove5 > -0.005m;

    // Сравниваем объём не с предыдущим тиком (это было слишком жёстко),
    // а с уровнем активности В МОМЕНТ обнаружения отката. Так ускорение
    // может накопиться за несколько секунд и WATCH реально получает шанс перейти дальше.
    decimal pullbackBaseF5 = watch.PullbackBaseF5VolumeRatio > 0
        ? watch.PullbackBaseF5VolumeRatio
        : Math.Max(0.01m, watch.LastF5VolumeRatio);

    decimal pullbackBaseF15 = watch.PullbackBaseF15VolumeRatio > 0
        ? watch.PullbackBaseF15VolumeRatio
        : Math.Max(0.01m, watch.LastF15VolumeRatio);

    bool volumeRecovered =
        (f5.VolumeRatio >= ReaccelMinVolume5Ratio &&
         f5.VolumeRatio >= pullbackBaseF5 * 1.18m) ||
        (f15.VolumeRatio >= ReaccelMinVolume15Ratio &&
         f15.VolumeRatio >= pullbackBaseF15 * 1.10m) ||
        (f5.VolumeRatio >= 0.50m && f15.VolumeRatio >= 0.70m);

    // Вместо требования, чтобы ВСЕ четыре flow-условия совпали в один тик,
    // считаем подтверждения. Для flow достаточно 3 из 4.
    int flowConfirmations = 0;
    if (share5 >= ReaccelMinShare5) flowConfirmations++;
    if (share15 >= ReaccelMinShare15) flowConfirmations++;
    if (cvd5 >= ReaccelMinCvd5) flowConfirmations++;
    if (cvd15 >= ReaccelMinCvd15) flowConfirmations++;

    bool flowRecovered = flowConfirmations >= 3;

    decimal bookNow = isLong ? book.Imbalance : 1m - book.Imbalance;
    decimal bookPersistent = isLong ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;
    decimal directedOfi = isLong ? book.Ofi5sNormalized : -book.Ofi5sNormalized;
    decimal microBps = book.MidPrice > 0
        ? (book.MicroPrice - book.MidPrice) / book.MidPrice * 10000m
        : 0m;
    decimal directedMicro = isLong ? microBps : -microBps;

    int bookConfirmations = 0;
    if (bookNow >= 0.40m) bookConfirmations++;
    if (bookPersistent >= 0.40m) bookConfirmations++;
    if (directedOfi >= -0.060m) bookConfirmations++;
    if (directedMicro >= -1.00m) bookConfirmations++;

    bool bookRecovered =
        book.SpreadBps <= MaxAllowedSpreadBps &&
        bookNow >= 0.32m &&
        bookPersistent >= 0.32m &&
        bookConfirmations >= 3;

    int reaccelConfirmations = 0;
    if (volumeRecovered) reaccelConfirmations++;
    if (flowRecovered) reaccelConfirmations++;
    if (bookRecovered) reaccelConfirmations++;

    watch.LastF5VolumeRatio = f5.VolumeRatio;
    watch.LastF15VolumeRatio = f15.VolumeRatio;

    // При обычном откате после реального отскока достаточно 2 из 3 подтверждений.
    // При глубоком откате (>= 70%) требуем все 3 из 3, чтобы не входить в
    // фактически сломанную волну только из-за одного краткого всплеска.
    int requiredReaccelConfirmations = deepPullback ? 3 : 2;

    if (!priceBounce || reaccelConfirmations < requiredReaccelConfirmations)
    {
        string flowState = flowRecovered
            ? $"OK({flowConfirmations}/4)"
            : $"нет({flowConfirmations}/4)";

        string bookState = bookRecovered
            ? $"OK({bookConfirmations}/4)"
            : $"нет({bookConfirmations}/4)";

        SetDiagnosticReason(
            $"WATCH {watch.Direction}: жду re-acceleration. " +
            $"bounce={(priceBounce ? "OK" : "нет")}, " +
            $"подтверждений={reaccelConfirmations}/3 (нужно {requiredReaccelConfirmations}/3), " +
            $"режим={(deepPullback ? "ГЛУБОКИЙ ОТКАТ" : "обычный")}, " +
            $"vol={(volumeRecovered ? "OK" : "нет")}, " +
            $"flow={flowState}, book={bookState}."
        );
        return;
    }

    decimal bounceStrength = minBounce > 0 ? Math.Clamp(bounceDistance / minBounce, 0m, 2m) : 1m;
    decimal reaccelQuality = Math.Clamp(
        35m +
        reaccelConfirmations * 15m +
        flowConfirmations * 4m +
        bookConfirmations * 3m +
        bounceStrength * 6m,
        0m,
        100m
    );

    if (!watch.ReaccelerationSeen)
    {
        watch.ReaccelerationSeen = true;
        watch.ReaccelerationSeenAt = now;

        UpdateWatchHistoryReacceleration(
            watch,
            reaccelConfirmations,
            reaccelQuality,
            f5,
            f15,
            book
        );

        lock (diagnosticsLock)
        {
            diagnostics.ReaccelerationsPassed++;
        }

        await SendTelegram(
            $"⚡ WATCH {watch.Direction}: повторное ускорение найдено\n\n" +
            $"Подтверждений: {reaccelConfirmations}/3\n" +
            $"Глубина отката: {watch.PullbackFraction:P0}\n" +
            $"Режим: {(deepPullback ? "глубокий откат — усиленная проверка" : "обычный откат")}\n" +
            $"Качество re-acceleration: {reaccelQuality:F0}/100\n\n" +
            "Проверяю финальные условия входа. Сделка пока НЕ открыта."
        );
    }

    FlowMetrics impulse = watch.Impulse;
    decimal volumeZ = watch.VolumeZ;

    EntryPlan entryPlan = BuildEntryPlan(watch.Direction, impulse, book);
    if (!entryPlan.Valid)
    {
        string reason = $"WATCH {watch.Direction}: EntryPlan невалиден — {entryPlan.RejectReason}.";
        UpdateWatchHistoryFinalAttempt(watch, reason);
        SetDiagnosticReason(reason);
        return;
    }

    decimal currentSpikeQuality = CalculateSpikeQuality(
        watch.Direction,
        f5,
        f15,
        f30,
        f60,
        book,
        liq,
        volumeZ,
        oiChange30
    );

    decimal impulseQuality = CalculateWatchImpulseQuality(watch.Direction, impulse, volumeZ, book);
    decimal spikeQuality = Math.Max(currentSpikeQuality, impulseQuality);

    decimal entryQuality = CalculateEntryQuality(
        watch.Direction,
        entryPlan,
        book,
        tickerSnapshot,
        oiChange30,
        currentPrice
    );

    decimal technicalConfluence = CalculateTechnicalConfluence(watch.Direction, entryPlan.Entry);

    if (technicalConfluence < MinTechnicalConfluenceFinal)
    {
        string reason = $"WATCH {watch.Direction}: тех. совпадение {technicalConfluence:F0}/100 < {MinTechnicalConfluenceFinal:F0}.";
        UpdateWatchHistoryFinalAttempt(watch, reason, technicalConfluence: technicalConfluence);
        SetDiagnosticReason(reason);
        return;
    }

    decimal modelScore =
        spikeQuality * 0.35m +
        entryQuality * 0.35m +
        technicalConfluence * 0.15m +
        reaccelQuality * 0.15m;

    AdaptiveThresholds adaptive = GetAdaptiveThresholds();

    if (spikeQuality < MinSpikeQuality + adaptive.ScoreBoost ||
        entryQuality < MinEntryQuality + adaptive.ScoreBoost * 0.60m ||
        modelScore < MinModelScore + adaptive.ScoreBoost)
    {
        string reason =
            $"WATCH {watch.Direction}: качество пока мало — spike {spikeQuality:F0}, entry {entryQuality:F0}, reaccel {reaccelQuality:F0}, model {modelScore:F0}.";
        UpdateWatchHistoryFinalAttempt(
            watch,
            reason,
            spikeQuality,
            entryQuality,
            technicalConfluence,
            modelScore
        );
        SetDiagnosticReason(reason);
        return;
    }

    HistoricalGate historicalGate = GetHistoricalPrecisionGate(
        watch.Direction,
        modelScore,
        impulse.VolumeRatio,
        book.Imbalance,
        oiChange30,
        market.Trend15mPercent
    );

    if (!historicalGate.Allow)
    {
        string reason = $"WATCH {watch.Direction}: исторический precision-фильтр отклонил сигнал.";
        UpdateWatchHistoryFinalAttempt(
            watch,
            reason,
            spikeQuality,
            entryQuality,
            technicalConfluence,
            modelScore
        );
        SetDiagnosticReason(reason);
        return;
    }

    TargetPlan target = OptimizeTarget(
        watch.Direction,
        entryPlan.Entry,
        entryPlan.StopLoss,
        modelScore,
        spikeQuality,
        entryQuality
    );

    if (!target.Valid ||
        target.NetPotentialProfitPercent < MinNetPotentialProfitPercent ||
        (!target.BootstrapMode &&
         (target.ExpectedValuePercent < MinExpectedValuePercent + adaptive.EvBoost ||
          target.ProbabilityPercent < MinEstimatedProbability + adaptive.ProbabilityBoost)))
    {
        string reason = $"WATCH {watch.Direction}: target-фильтр не пройден — {target.RejectReason}.";
        UpdateWatchHistoryFinalAttempt(
            watch,
            reason,
            spikeQuality,
            entryQuality,
            technicalConfluence,
            modelScore,
            entryPlan,
            target
        );
        SetDiagnosticReason(reason);
        return;
    }

    // Подтверждение должно продержаться несколько последовательных апдейтов,
    // но уже не 1.4 секунды: сам WATCH + pullback является дополнительным фильтром.
    if (pendingSignalDirection != watch.Direction ||
        now - pendingSignalLastSeen > TimeSpan.FromMilliseconds(650))
    {
        pendingSignalDirection = watch.Direction;
        pendingSignalSince = now;
    }

    pendingSignalLastSeen = now;

    if (now - pendingSignalSince < TimeSpan.FromMilliseconds(FinalSignalHoldMilliseconds))
        return;

    pendingSignalDirection = null;
    pendingSignalSince = DateTime.MinValue;
    pendingSignalLastSeen = DateTime.MinValue;

    decimal finalPullbackFraction = watch.PullbackFraction;
    DateTime watchCreatedAt = watch.CreatedAt;
    string direction = watch.Direction;

    activeTrade = new ActiveTrade
    {
        SourceWatchId = watch.Id,
        Direction = direction,
        Status = TradeStatus.WaitingForEntry,
        SignalTime = DateTime.UtcNow,
        ZoneLow = entryPlan.ZoneLow,
        ZoneHigh = entryPlan.ZoneHigh,
        SuggestedEntry = entryPlan.Entry,
        InitialStopLoss = entryPlan.StopLoss,
        CurrentStopLoss = entryPlan.StopLoss,
        TakeProfit = target.TakeProfit,
        RiskReward = target.RiskReward,
        SpikeQuality = spikeQuality,
        EntryQuality = entryQuality,
        ModelScore = modelScore,
        EstimatedProbability = target.ProbabilityPercent,
        ProbabilitySource = target.ProbabilitySource,
        ProbabilitySamples = target.Samples,
        GrossProfitPercent = target.GrossProfitPercent,
        RiskPercent = target.RiskPercent,
        EstimatedCostsPercent = target.EstimatedCostsPercent,
        NetPotentialProfitPercent = target.NetPotentialProfitPercent,
        ExpectedValuePercent = target.ExpectedValuePercent,
        VolumeRatio = impulse.VolumeRatio,
        VolumeZ = volumeZ,
        BuyShare = impulse.BuyShare,
        SellShare = impulse.SellShare,
        CvdNormalized = impulse.CvdNormalized,
        OrderBookImbalance = book.Imbalance,
        PersistentBookImbalance = book.PersistentImbalance5s,
        Ofi5s = book.Ofi5sNormalized,
        SpreadBps = book.SpreadBps,
        OiChange30 = oiChange30,
        Trend5m = market.Trend5mPercent,
        Trend15m = market.Trend15mPercent,
        ImpulseEfficiency = impulse.Efficiency,
        ImpulseWindowSeconds = impulse.WindowSeconds,
        ImpulseRangeUsd = impulse.High - impulse.Low,
        ImpulseRangeAtr = market.Atr > 0 ? (impulse.High - impulse.Low) / market.Atr : 0m,
        HistoricalSuccessEstimate = historicalGate.ProbabilityPercent,
        HistoricalSuccessSamples = historicalGate.Samples,
        TechnicalConfluence = technicalConfluence,
        SignalVwap = market.RollingVwap,
        SignalPoc = market.Poc,
        SignalVah = market.Vah,
        SignalVal = market.Val,
        SignalSupport = market.NearestSupport,
        SignalResistance = market.NearestResistance,
        Structure5mBias = market.Structure5m.Bias,
        Structure5mEvent = market.Structure5m.Event,
        Structure15mBias = market.Structure15m.Bias,
        Structure15mEvent = market.Structure15m.Event
    };

    UpdateWatchHistorySignalCreated(
        watch,
        spikeQuality,
        entryQuality,
        technicalConfluence,
        modelScore,
        entryPlan,
        target
    );

    watchCandidate = null;
    lastWatchFinishedAt = DateTime.UtcNow;

    lock (diagnosticsLock)
    {
        diagnostics.SignalsCreated++;
        diagnostics.LastReason = $"Создан сигнал {direction} после pullback {finalPullbackFraction:P0} и re-acceleration.";
        diagnostics.LastReasonAt = DateTime.UtcNow;
    }

    string emoji = direction == "LONG" ? "🚀" : "🔻";
    string flowText = direction == "LONG"
        ? $"Покупки в исходном импульсе: {impulse.BuyShare:P1}"
        : $"Продажи в исходном импульсе: {impulse.SellShare:P1}";

    string histText = historicalGate.Samples > 0
        ? $"Историческая точность похожих сигналов: {historicalGate.ProbabilityPercent:F1}% ({historicalGate.Samples} прим.)"
        : "Исторических аналогов пока недостаточно";

    await SendTelegram(
        $"{emoji} ПОДТВЕРЖДЁННЫЙ СИГНАЛ {direction} | {Symbol}\n\n" +
        "Схема: ИМПУЛЬС → ОТКАТ → ПОВТОРНОЕ УСКОРЕНИЕ\n\n" +
        $"💰 Цена сейчас: ${currentPrice:F2}\n" +
        $"🌊 Исходный импульс: {impulse.WindowSeconds}с | диапазон ${(impulse.High - impulse.Low):F2}\n" +
        $"↩️ Максимальный откат: {finalPullbackFraction:P0}\n" +
        $"⚡ Объём 5/15с сейчас: x{f5.VolumeRatio:F2} / x{f15.VolumeRatio:F2}\n" +
        $"{flowText}\n" +
        $"CVD 5/15с: {(direction == "LONG" ? f5.CvdNormalized : -f5.CvdNormalized):+0.00;-0.00} / {(direction == "LONG" ? f15.CvdNormalized : -f15.CvdNormalized):+0.00;-0.00}\n\n" +
        $"🔥 Качество всплеска: {spikeQuality:F0}/100\n" +
        $"🎯 Качество входа: {entryQuality:F0}/100\n" +
        $"🧠 Общая оценка: {modelScore:F0}/100\n" +
        $"📚 {histText}\n\n" +
        $"📚 Стакан: {book.Imbalance:P0} bid / {(1m - book.Imbalance):P0} ask\n" +
        $"OFI 5с: {book.Ofi5sNormalized:+0.000;-0.000}\n" +
        $"Spread: {book.SpreadBps:F2} bps\n\n" +
        $"🎯 Зона входа:\n${entryPlan.ZoneLow:F2} — ${entryPlan.ZoneHigh:F2}\n" +
        $"⭐ Ориентир входа: ${entryPlan.Entry:F2}\n" +
        $"🛑 Стоп: ${entryPlan.StopLoss:F2} (-{target.RiskPercent:F2}%)\n" +
        $"💰 Тейк: ${target.TakeProfit:F2} (+{target.GrossProfitPercent:F2}%)\n" +
        $"📐 R/R: 1:{target.RiskReward:F2}\n" +
        $"🎲 Оценка достижения TP: {target.ProbabilityPercent:F1}%\n" +
        $"🧮 EV: {target.ExpectedValuePercent:+0.000;-0.000}%\n\n" +
        $"WATCH длился {(DateTime.UtcNow - watchCreatedAt).TotalSeconds:F0}с.\n" +
        "Сделка ещё не открыта: бот ждёт фактическое подтверждение входа в рассчитанной зоне."
    );
}

decimal CalculateWatchImpulseQuality(
    string direction,
    FlowMetrics impulse,
    decimal volumeZ,
    OrderBookMetrics book)
{
    bool isLong = direction == "LONG";
    decimal share = isLong ? impulse.BuyShare : impulse.SellShare;
    decimal cvd = isLong ? impulse.CvdNormalized : -impulse.CvdNormalized;
    decimal closeLocation = isLong ? impulse.CloseLocation : 1m - impulse.CloseLocation;
    decimal bookDir = isLong ? book.Imbalance : 1m - book.Imbalance;

    decimal score =
        Score(impulse.VolumeRatio, 1.20m, 3.50m) * 0.27m +
        Score(volumeZ, 2m, 7m) * 0.16m +
        Score(share, 0.55m, 0.80m) * 0.18m +
        Score(cvd, 0.06m, 0.55m) * 0.14m +
        Score(impulse.Efficiency, 0.25m, 0.75m) * 0.10m +
        Score(closeLocation, 0.48m, 0.85m) * 0.08m +
        Score(bookDir, 0.42m, 0.65m) * 0.07m;

    return Math.Clamp(score, 0m, 100m);
}

async Task EndWatchCandidate(string reason, bool invalidated)
{
    WatchCandidate? old = watchCandidate;

    if (old != null)
        UpdateWatchHistoryEnd(old, invalidated ? "INVALIDATED" : "EXPIRED", reason);

    watchCandidate = null;
    lastWatchFinishedAt = DateTime.UtcNow;
    pendingSignalDirection = null;
    pendingSignalSince = DateTime.MinValue;
    pendingSignalLastSeen = DateTime.MinValue;

    lock (diagnosticsLock)
    {
        diagnostics.WatchesEnded++;
        diagnostics.LastReason = reason;
        diagnostics.LastReasonAt = DateTime.UtcNow;
    }

    Console.WriteLine(reason);

    // Не спамим каждым тихим истечением, но о сломе WATCH сообщаем.
    if (invalidated && old != null)
        await SendTelegram(reason);
}

FlowMetrics CloneFlowMetrics(FlowMetrics x)
{
    return new FlowMetrics
    {
        Valid = x.Valid,
        WindowSeconds = x.WindowSeconds,
        FirstPrice = x.FirstPrice,
        LastPrice = x.LastPrice,
        High = x.High,
        Low = x.Low,
        Vwap = x.Vwap,
        TotalTurnover = x.TotalTurnover,
        TotalVolume = x.TotalVolume,
        BuyTurnover = x.BuyTurnover,
        SellTurnover = x.SellTurnover,
        BuyShare = x.BuyShare,
        SellShare = x.SellShare,
        Cvd = x.Cvd,
        CvdNormalized = x.CvdNormalized,
        PriceChangePercent = x.PriceChangePercent,
        VolumeRatio = x.VolumeRatio,
        TradesPerSecond = x.TradesPerSecond,
        Efficiency = x.Efficiency,
        CloseLocation = x.CloseLocation,
        LargeBuyTurnover = x.LargeBuyTurnover,
        LargeSellTurnover = x.LargeSellTurnover,
        LargeBuyShare = x.LargeBuyShare,
        LargeSellShare = x.LargeSellShare,
        LargeTurnoverShare = x.LargeTurnoverShare
    };
}

// =====================================================
//        ВЫБОР РАБОЧЕГО ИМПУЛЬСА 15 / 30 / 60 СЕК
// =====================================================

FlowMetrics SelectImpulseFlow(
    string direction,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60)
{
    bool isLong = direction == "LONG";
    decimal minimumRange = Math.Max(MinImpulseRangeUsd, market.Atr * MinImpulseRangeAtr);
    decimal maximumRange = market.Atr > 0
        ? market.Atr * MaxImpulseRangeAtr
        : decimal.MaxValue;

    FlowMetrics? best = null;
    decimal bestScore = decimal.MinValue;

    foreach (FlowMetrics flow in new[] { f15, f30, f60 })
    {
        if (!flow.Valid || flow.TotalTurnover <= 0)
            continue;

        decimal range = flow.High - flow.Low;
        if (range < minimumRange)
            continue;

        if (market.Atr > 0 && range > maximumRange)
            continue;

        decimal share = isLong ? flow.BuyShare : flow.SellShare;
        decimal cvd = isLong ? flow.CvdNormalized : -flow.CvdNormalized;
        decimal priceMove = isLong ? flow.PriceChangePercent : -flow.PriceChangePercent;
        decimal closeLocation = isLong ? flow.CloseLocation : 1m - flow.CloseLocation;

        decimal minShare = flow.WindowSeconds <= 15 ? 0.62m : 0.56m;
        if (share < minShare || cvd < 0.03m || priceMove <= 0m)
            continue;

        decimal rangeScore = Math.Clamp(
            range / Math.Max(PreferredImpulseRangeUsd, minimumRange),
            0m,
            1.6m
        );

        decimal score =
            rangeScore * 30m +
            Math.Clamp(flow.Efficiency, 0m, 1m) * 22m +
            Math.Clamp(closeLocation, 0m, 1m) * 18m +
            Math.Clamp(share, 0m, 1m) * 15m +
            Math.Clamp(flow.VolumeRatio / 3m, 0m, 1.5m) * 10m +
            Math.Clamp(cvd, 0m, 1m) * 5m;

        if (flow.WindowSeconds >= 30)
            score += 2m;

        if (best == null || score > bestScore)
        {
            best = flow;
            bestScore = score;
        }
    }

    return best ?? new FlowMetrics();
}

// =====================================================
//             PRECISION-GATE СИГНАЛА
// =====================================================

bool PassPrecisionGate(
    string direction,
    FlowMetrics f5,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60,
    FlowMetrics impulse,
    OrderBookMetrics book,
    LiquidationMetrics liq,
    TickerSnapshot tickerSnapshot,
    decimal oiChange30,
    decimal volumeZ)
{
    bool isLong = direction == "LONG";

    decimal dir5 = isLong ? f5.CvdNormalized : -f5.CvdNormalized;
    decimal dir15 = isLong ? f15.CvdNormalized : -f15.CvdNormalized;
    decimal dir30 = isLong ? f30.CvdNormalized : -f30.CvdNormalized;

    decimal share5 = isLong ? f5.BuyShare : f5.SellShare;
    decimal share15 = isLong ? f15.BuyShare : f15.SellShare;
    decimal share30 = isLong ? f30.BuyShare : f30.SellShare;

    decimal directionalClose = isLong ? impulse.CloseLocation : 1m - impulse.CloseLocation;
    decimal bookNow = isLong ? book.Imbalance : 1m - book.Imbalance;
    decimal bookPersistent = isLong ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;
    decimal directedOfi = isLong ? book.Ofi5sNormalized : -book.Ofi5sNormalized;
    decimal microBps = book.MidPrice > 0
        ? (book.MicroPrice - book.MidPrice) / book.MidPrice * 10000m
        : 0m;
    decimal directedMicro = isLong ? microBps : -microBps;

    // 1) Всплеск должен быть виден не только в одном окне.
    bool multiWindowVolume =
        f15.VolumeRatio >= MinMainVolumeRatio &&
        f30.VolumeRatio >= 1.05m &&
        (f5.VolumeRatio >= 0.85m || volumeZ >= 4.0m);

    if (!multiWindowVolume)
        return false;

    // 2) Агрессивный поток должен быть устойчивым, а не одним большим принтом.
    bool flowConsistency =
        share5 >= 0.54m &&
        share15 >= MinDirectionalShare &&
        share30 >= 0.53m &&
        dir5 >= 0.03m &&
        dir15 >= 0.12m &&
        dir30 >= 0.02m;

    if (!flowConsistency)
        return false;

    // 3) Цена должна реально превращать поток в движение.
    if (impulse.Efficiency < MinImpulseEfficiency || directionalClose < MinDirectionalCloseLocation)
        return false;

    decimal impulseRange = impulse.High - impulse.Low;
    decimal minimumRangeUsd = Math.Max(MinImpulseRangeUsd, market.Atr * MinImpulseRangeAtr);
    decimal rangeAtr = market.Atr > 0
        ? impulseRange / market.Atr
        : 0m;

    // Убираем прежние микроимпульсы порядка $1-2.
    if (impulseRange < minimumRangeUsd ||
        (market.Atr > 0 && rangeAtr > MaxImpulseRangeAtr))
    {
        return false;
    }

    // 4) Не торгуем против минутного режима.
    bool trendAligned = isLong
        ? market.Ema9 > market.Ema21 &&
          market.Ema21 >= market.Ema50 * 0.9995m &&
          market.LastClosedPrice >= market.Ema21 &&
          market.Trend5mPercent > 0m &&
          market.Trend15mPercent > -0.05m
        : market.Ema9 < market.Ema21 &&
          market.Ema21 <= market.Ema50 * 1.0005m &&
          market.LastClosedPrice <= market.Ema21 &&
          market.Trend5mPercent < 0m &&
          market.Trend15mPercent < 0.05m;

    if (!trendAligned)
        return false;

    // 4.1) Структура графика 5м/15м. Не берём микровсплеск против
    // явной старшей структуры. Для переходного режима CHOCH считается
    // допустимым ранним подтверждением смены направления.
    if (!StructureAllowsDirection(direction, market.Structure5m, market.Structure15m))
        return false;

    // Если цена уже слишком далеко вылетела за value area, не догоняем.
    // Исключение — свежий BOS в ту же сторону: после него ещё можно ждать ретест.
    bool freshBreakout = direction == "LONG"
        ? IsUpStructureEvent(market.Structure5m.Event) || IsUpStructureEvent(market.Structure15m.Event)
        : IsDownStructureEvent(market.Structure5m.Event) || IsDownStructureEvent(market.Structure15m.Event);

    if (!freshBreakout && market.Atr > 0)
    {
        if (direction == "LONG" && market.Vah > 0 && impulse.LastPrice > market.Vah + market.Atr * 0.80m)
            return false;

        if (direction == "SHORT" && market.Val > 0 && impulse.LastPrice < market.Val - market.Atr * 0.80m)
            return false;
    }

    // Экстремально тихий/хаотичный режим тоже не подходит.
    if (market.AtrPercent < 0.025m || market.AtrPercent > 0.95m)
        return false;

    // 5) Стакан должен подтверждать поток и делать это устойчиво несколько секунд.
    if (book.SpreadBps > MaxAllowedSpreadBps ||
        bookNow < MinPersistentBookImbalance ||
        bookPersistent < MinPersistentBookImbalance ||
        directedMicro < -0.25m ||
        directedOfi < -0.015m)
    {
        return false;
    }

    // 6) Если крупные сделки составляют заметную часть потока, они тоже
    // должны быть в направлении сигнала.
    if (impulse.LargeTurnoverShare >= 0.08m)
    {
        decimal largeDir = isLong ? impulse.LargeBuyShare : impulse.LargeSellShare;
        if (largeDir < 0.54m)
            return false;
    }

    // 7) Сильное падение OI на фоне волны ликвидаций часто означает,
    // что squeeze уже близок к выдыханию, а не что начинается новый тренд.
    decimal supportiveLiquidations = isLong ? liq.ShortLiquidations : liq.LongLiquidations;
    decimal liqRatio = supportiveLiquidations / Math.Max(market.EffectiveMinuteTurnover, 1m);

    if (liqRatio > 0.025m && oiChange30 < -0.04m)
        return false;

    if (oiChange30 < -0.08m)
        return false;

    // 8) Если последние 5 секунд заметно слабее 15/30с, мы опоздали.
    if (f5.VolumeRatio < f15.VolumeRatio * 0.55m &&
        f5.VolumeRatio < f30.VolumeRatio * 0.70m)
    {
        return false;
    }

    // 9) Экстремальный funding/crowding — ещё один фильтр против позднего входа.
    decimal fundingPercent = tickerSnapshot.FundingRate * 100m;
    if (isLong && fundingPercent > 0.12m)
        return false;
    if (!isLong && fundingPercent < -0.12m)
        return false;

    return true;
}

// =====================================================
//        АДАПТИВНОЕ УЖЕСТОЧЕНИЕ ПО РЕЗУЛЬТАТАМ
// =====================================================

AdaptiveThresholds GetAdaptiveThresholds()
{
    List<TradeRecord> recent;

    lock (historyLock)
    {
        recent = tradeHistory
            .OrderByDescending(x => x.CloseTime)
            .Take(40)
            .ToList();
    }

    if (recent.Count < 12)
        return new AdaptiveThresholds();

    decimal successRate = recent.Count(x => x.NetPnlPercent > 0m) * 100m / recent.Count;

    if (successRate < 40m)
    {
        return new AdaptiveThresholds
        {
            ScoreBoost = 4m,
            ProbabilityBoost = 3m,
            EvBoost = 0.010m
        };
    }

    if (successRate < 50m)
    {
        return new AdaptiveThresholds
        {
            ScoreBoost = 2m,
            ProbabilityBoost = 2m,
            EvBoost = 0.006m
        };
    }

    if (successRate < 55m)
    {
        return new AdaptiveThresholds
        {
            ScoreBoost = 1m,
            ProbabilityBoost = 1m,
            EvBoost = 0.002m
        };
    }

    return new AdaptiveThresholds();
}

// =====================================================
//          ФИЛЬТР ПО СОБСТВЕННОЙ ИСТОРИИ
// =====================================================

HistoricalGate GetHistoricalPrecisionGate(
    string direction,
    decimal modelScore,
    decimal volumeRatio,
    decimal bookImbalance,
    decimal oiChange30,
    decimal trend15)
{
    List<TradeRecord> snapshot;

    lock (historyLock)
    {
        snapshot = tradeHistory
            .Where(x => x.Direction == direction)
            .OrderByDescending(x => x.CloseTime)
            .Take(250)
            .ToList();
    }

    if (snapshot.Count == 0)
        return new HistoricalGate { Allow = true };

    decimal weightedWins = 0m;
    decimal totalWeight = 0m;
    int samples = 0;

    foreach (TradeRecord x in snapshot)
    {
        decimal scoreDistance = Math.Abs(x.ModelScore - modelScore) / 12m;
        decimal volumeDistance = Math.Abs(x.VolumeRatio - volumeRatio) / Math.Max(1m, volumeRatio);
        decimal bookDistance = Math.Abs(x.OrderBookImbalance - bookImbalance) / 0.12m;
        decimal oiDistance = Math.Abs(x.OiChange30 - oiChange30) / 0.18m;

        decimal distance =
            scoreDistance * 0.38m +
            volumeDistance * 0.22m +
            bookDistance * 0.24m +
            oiDistance * 0.16m;

        if (distance > 2.50m)
            continue;

        decimal weight = 1m / (1m + distance);
        totalWeight += weight;

        if (x.NetPnlPercent > 0m)
            weightedWins += weight;

        samples++;
    }

    if (samples < 12 || totalWeight <= 0m)
    {
        return new HistoricalGate
        {
            Allow = true,
            Samples = samples,
            ProbabilityPercent = samples > 0 ? (weightedWins + 2m) / (totalWeight + 4m) * 100m : 0m
        };
    }

    // Beta-smoothing: не делаем сильный вывод из небольшой выборки.
    decimal probability = (weightedWins + 3m) / (totalWeight + 6m) * 100m;

    // Дополнительный regime-brake: если последние сделки этого направления
    // явно проваливаются, не продолжаем механически торговать ту же фазу рынка.
    var recent = snapshot.Take(12).ToList();
    decimal recentWinRate = recent.Count > 0
        ? recent.Count(x => x.NetPnlPercent > 0m) * 100m / recent.Count
        : 50m;

    bool allow = probability >= 53.5m && (recent.Count < 8 || recentWinRate >= 37.5m);

    return new HistoricalGate
    {
        Allow = allow,
        ProbabilityPercent = probability,
        Samples = samples
    };
}

// =====================================================
//                 КАЧЕСТВО ВСПЛЕСКА
// =====================================================

decimal CalculateSpikeQuality(
    string direction,
    FlowMetrics f5,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60,
    OrderBookMetrics book,
    LiquidationMetrics liq,
    decimal volumeZ,
    decimal oiChange30)
{
    decimal dirShare = direction == "LONG" ? f15.BuyShare : f15.SellShare;
    decimal dirCvd = direction == "LONG" ? f15.CvdNormalized : -f15.CvdNormalized;
    decimal dirLarge = direction == "LONG" ? f15.LargeBuyShare : f15.LargeSellShare;
    decimal bookDirNow = direction == "LONG" ? book.Imbalance : 1m - book.Imbalance;
    decimal bookDirPersistent = direction == "LONG" ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;
    decimal bookDir = bookDirNow * 0.45m + bookDirPersistent * 0.55m;
    decimal ofiDir = direction == "LONG" ? book.Ofi5sNormalized : -book.Ofi5sNormalized;

    decimal volumeScore =
        Score(f15.VolumeRatio, 1.5m, 4.5m) * 0.65m +
        Score(volumeZ, 2m, 8m) * 0.35m;

    decimal acceleration = f15.VolumeRatio > 0 ? f5.VolumeRatio / f15.VolumeRatio : 1m;
    decimal accelerationScore = Score(acceleration, 0.75m, 1.80m);
    decimal flowScore = Score(dirShare, 0.55m, 0.82m);
    decimal cvdScore = Score(dirCvd, 0.10m, 0.65m);
    decimal largeScore = Score(dirLarge, 0.45m, 0.90m);
    decimal bookScore = Score(bookDir, 0.50m, 0.70m);
    decimal ofiScore = Score(ofiDir, -0.01m, 0.12m);
    decimal efficiencyScore = Score(f15.Efficiency, 0.35m, 0.80m);
    decimal directionalClose = direction == "LONG" ? f15.CloseLocation : 1m - f15.CloseLocation;
    decimal closeLocationScore = Score(directionalClose, 0.52m, 0.88m);

    decimal supportiveLiquidations = direction == "LONG"
        ? liq.ShortLiquidations
        : liq.LongLiquidations;

    decimal liqRatio = supportiveLiquidations / Math.Max(market.EffectiveMinuteTurnover, 1m);
    decimal liquidationScore = Score(liqRatio, 0m, 0.04m);

    decimal score =
        volumeScore * 0.20m +
        accelerationScore * 0.09m +
        flowScore * 0.15m +
        cvdScore * 0.13m +
        largeScore * 0.06m +
        bookScore * 0.11m +
        ofiScore * 0.07m +
        liquidationScore * 0.05m +
        efficiencyScore * 0.08m +
        closeLocationScore * 0.06m;

    // Поглощение: огромный поток, но цена почти не двигается.
    bool absorption =
        f15.VolumeRatio >= 2.5m &&
        Math.Abs(f15.PriceChangePercent) < 0.08m &&
        dirCvd > 0.30m;

    if (absorption)
        score -= 14m;

    // Возможное выдыхание squeeze: ликвидаций много, а OI резко падает.
    if (liqRatio > 0.03m && oiChange30 < -0.10m)
        score -= 10m;

    // Если импульс уже тухнет на 5 сек относительно 15/30 сек.
    if (f5.VolumeRatio < f15.VolumeRatio * 0.65m && f15.VolumeRatio < f30.VolumeRatio)
        score -= 8m;

    return Math.Clamp(score, 0m, 100m);
}

// =====================================================
//                    ENTRY ENGINE
// =====================================================

EntryPlan BuildEntryPlan(string direction, FlowMetrics impulse, OrderBookMetrics book)
{
    decimal range = impulse.High - impulse.Low;
    if (range <= 0 || impulse.LastPrice <= 0)
        return new EntryPlan { RejectReason = "invalid impulse range/price" };

    decimal rawZoneLow;
    decimal rawZoneHigh;

    if (direction == "LONG")
    {
        // Ждём более глубокий ретест 50-72%, а не вход на вершине волны.
        rawZoneLow = impulse.High - range * EntryRetracementDeep;
        rawZoneHigh = impulse.High - range * EntryRetracementNear;
    }
    else
    {
        rawZoneLow = impulse.Low + range * EntryRetracementNear;
        rawZoneHigh = impulse.Low + range * EntryRetracementDeep;
    }

    decimal micro = book.MicroPrice > 0 ? book.MicroPrice : impulse.Vwap;
    decimal flowEntry = impulse.Vwap * 0.68m + micro * 0.32m;
    decimal rawEntry = Math.Clamp(flowEntry, rawZoneLow, rawZoneHigh);

    // Ищем совпадение зоны отката с VWAP / POC / VAL-VAH / swing-уровнями.
    decimal technicalAnchor = FindTechnicalAnchor(
        direction,
        rawEntry,
        rawZoneLow,
        rawZoneHigh
    );

    decimal center = technicalAnchor > 0
        ? rawEntry * 0.45m + technicalAnchor * 0.55m
        : rawEntry;

    decimal halfWidth = Math.Max(range * 0.085m, market.Atr * 0.065m);
    decimal zoneLow = Math.Max(rawZoneLow - market.Atr * 0.14m, center - halfWidth);
    decimal zoneHigh = Math.Min(rawZoneHigh + market.Atr * 0.14m, center + halfWidth);

    if (zoneLow >= zoneHigh)
    {
        zoneLow = rawZoneLow;
        zoneHigh = rawZoneHigh;
    }

    decimal entry = Math.Clamp(center, zoneLow, zoneHigh);

    decimal buffer = Math.Max(
        market.Atr * StopBufferAtr,
        Math.Max(
            range * StopBufferImpulse,
            Math.Max(book.SpreadPrice * 5m, impulse.LastPrice * 0.00035m)
        )
    );

    decimal minimumStopDistance = Math.Max(
        market.Atr * MinStopDistanceAtr,
        Math.Max(range * 0.45m, entry * (MinInitialRiskPercent / 100m))
    );

    decimal stop;

    if (direction == "LONG")
    {
        var supports = new List<decimal>
        {
            impulse.Low,
            market.NearestSupport,
            market.Val,
            market.Structure5m.LastSwingLow,
            market.Structure15m.LastSwingLow,
            orderBook.FindStrongBidBelow(entry, entry * 0.012m)
        }
        .Where(x => x > 0 && x < zoneLow - buffer * 0.20m)
        .ToList();

        decimal structural = supports.Count > 0
            ? supports.Max() // ближайшая реальная поддержка под зоной
            : impulse.Low;

        decimal structuralStop = structural - buffer;
        stop = Math.Min(structuralStop, entry - minimumStopDistance);
    }
    else
    {
        var resistances = new List<decimal>
        {
            impulse.High,
            market.NearestResistance,
            market.Vah,
            market.Structure5m.LastSwingHigh,
            market.Structure15m.LastSwingHigh,
            orderBook.FindStrongAskAbove(entry, entry * 0.012m)
        }
        .Where(x => x > zoneHigh + buffer * 0.20m)
        .ToList();

        decimal structural = resistances.Count > 0
            ? resistances.Min() // ближайшее реальное сопротивление над зоной
            : impulse.High;

        decimal structuralStop = structural + buffer;
        stop = Math.Max(structuralStop, entry + minimumStopDistance);
    }

    decimal riskPercent = Math.Abs(stop - entry) / entry * 100m;
    decimal technicalConfluence = CalculateTechnicalConfluence(direction, entry);

    // Если в точке входа вообще нет технического основания, пропускаем сделку.
    if (technicalConfluence < MinTechnicalConfluencePlan)
        return new EntryPlan { RejectReason = $"technical confluence {technicalConfluence:F0} < {MinTechnicalConfluencePlan:F0}" };

    if (riskPercent < MinInitialRiskPercent || riskPercent > MaxInitialRiskPercent)
        return new EntryPlan { RejectReason = $"risk {riskPercent:F3}% outside {MinInitialRiskPercent:F2}-{MaxInitialRiskPercent:F2}%" };

    return new EntryPlan
    {
        Valid = true,
        ZoneLow = zoneLow,
        ZoneHigh = zoneHigh,
        Entry = entry,
        StopLoss = stop,
        TechnicalAnchor = technicalAnchor,
        TechnicalConfluence = technicalConfluence
    };
}

decimal CalculateEntryQuality(
    string direction,
    EntryPlan plan,
    OrderBookMetrics book,
    TickerSnapshot tickerSnapshot,
    decimal oiChange30,
    decimal currentPrice)
{
    decimal spreadScore = InverseScore(book.SpreadBps, 0.5m, 6m);

    decimal distanceToZone = currentPrice < plan.ZoneLow
        ? plan.ZoneLow - currentPrice
        : currentPrice > plan.ZoneHigh
            ? currentPrice - plan.ZoneHigh
            : 0m;

    decimal distanceAtr = market.Atr > 0 ? distanceToZone / market.Atr : 0;
    decimal distanceScore = InverseScore(distanceAtr, 0.10m, 1.50m);

    decimal bookDirNow = direction == "LONG" ? book.Imbalance : 1m - book.Imbalance;
    decimal bookDirPersistent = direction == "LONG" ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;
    decimal bookDir = bookDirNow * 0.40m + bookDirPersistent * 0.60m;
    decimal bookScore = Score(bookDir, 0.50m, 0.70m);

    decimal microDeltaBps = book.MidPrice > 0
        ? (book.MicroPrice - book.MidPrice) / book.MidPrice * 10000m
        : 0;

    decimal directedMicro = direction == "LONG" ? microDeltaBps : -microDeltaBps;
    decimal microScore = Score(directedMicro, -1.5m, 3m);

    decimal trend5Dir = direction == "LONG" ? market.Trend5mPercent : -market.Trend5mPercent;
    decimal trend15Dir = direction == "LONG" ? market.Trend15mPercent : -market.Trend15mPercent;
    decimal emaDir = direction == "LONG" ? market.EmaSpreadAtr : -market.EmaSpreadAtr;
    decimal trendScore =
        Score(trend5Dir, -0.05m, 0.45m) * 0.35m +
        Score(trend15Dir, -0.05m, 0.75m) * 0.25m +
        Score(emaDir, 0m, 0.80m) * 0.20m +
        StructureDirectionScore(direction) * 0.20m;

    decimal oiScore = Score(oiChange30, -0.03m, 0.22m);
    decimal technicalScore = CalculateTechnicalConfluence(direction, plan.Entry);

    // Сильное crowding по funding немного штрафуем.
    decimal fundingPercent = tickerSnapshot.FundingRate * 100m;
    decimal crowdingPenalty = 0m;

    if (direction == "LONG" && fundingPercent > 0.08m)
        crowdingPenalty = Math.Min(15m, fundingPercent / 0.08m * 8m);
    else if (direction == "SHORT" && fundingPercent < -0.08m)
        crowdingPenalty = Math.Min(15m, Math.Abs(fundingPercent) / 0.08m * 8m);

    decimal divergenceScore = InverseScore(
        Math.Abs(tickerSnapshot.MarkIndexDivergenceBps),
        1m,
        12m
    );

    decimal score =
        spreadScore * 0.11m +
        distanceScore * 0.11m +
        bookScore * 0.16m +
        microScore * 0.09m +
        trendScore * 0.15m +
        oiScore * 0.07m +
        divergenceScore * 0.04m +
        technicalScore * 0.27m -
        crowdingPenalty;

    return Math.Clamp(score, 0m, 100m);
}

// =====================================================
//                   TARGET / EV ENGINE
// =====================================================

TargetPlan OptimizeTarget(
    string direction,
    decimal entry,
    decimal stop,
    decimal modelScore,
    decimal spikeQuality,
    decimal entryQuality)
{
    decimal riskDistance = Math.Abs(entry - stop);
    if (riskDistance <= 0 || entry <= 0)
        return new TargetPlan { RejectReason = "invalid entry/stop distance" };

    decimal riskPercent = riskDistance / entry * 100m;

    decimal entrySlip = orderBook.EstimateSlippagePercent(
        direction == "LONG" ? "BUY" : "SELL",
        PaperNotionalUsdt,
        entry
    );

    decimal exitSlip = orderBook.EstimateSlippagePercent(
        direction == "LONG" ? "SELL" : "BUY",
        PaperNotionalUsdt,
        entry
    );

    decimal costs = EstimatedRoundTripFeePercent + entrySlip + exitSlip;

    int closedSamples;
    lock (historyLock)
    {
        closedSamples = tradeHistory.Count(x => x.Result is "TP" or "SL");
    }

    // Пока нет хотя бы MinimumHistorySamples реальных paper-исходов, нельзя делать
    // жёсткий отсев по искусственной "вероятности". На bootstrap-этапе требуем
    // адекватный чистый payoff после комиссий/проскальзывания. После накопления
    // истории снова включается probability + EV gate.
    bool bootstrapMode = closedSamples < MinimumHistorySamples;

    decimal[] rrCandidates = { 1.25m, 1.50m, 1.75m, 2.00m, 2.25m, 2.50m };
    TargetPlan best = new TargetPlan
    {
        BootstrapMode = bootstrapMode,
        RejectReason = "no target candidate survived"
    };

    bool hadEnoughRr = false;
    bool hadEnoughNet = false;
    bool hadEnoughPayoff = false;
    bool hadEnoughProbability = false;
    bool hadEnoughEv = false;

    foreach (decimal requestedRr in rrCandidates)
    {
        decimal target = direction == "LONG"
            ? entry + riskDistance * requestedRr
            : entry - riskDistance * requestedRr;

        // Не прячем тейк за ближайшую сильную стенку/уровень.
        if (direction == "LONG")
        {
            decimal wall = orderBook.FindStrongAskAbove(entry, entry * 0.012m);
            if (wall > entry && wall < target)
                target = wall * 0.99985m;

            decimal techResistance = FindNearestTechnicalResistanceAbove(entry);
            if (techResistance > entry && techResistance < target)
            {
                decimal frontRun = Math.Max(market.Atr * 0.025m, entry * 0.00008m);
                target = Math.Max(entry, techResistance - frontRun);
            }
        }
        else
        {
            decimal wall = orderBook.FindStrongBidBelow(entry, entry * 0.012m);
            if (wall > 0 && wall < entry && wall > target)
                target = wall * 1.00015m;

            decimal techSupport = FindNearestTechnicalSupportBelow(entry);
            if (techSupport > 0 && techSupport < entry && techSupport > target)
            {
                decimal frontRun = Math.Max(market.Atr * 0.025m, entry * 0.00008m);
                target = Math.Min(entry, techSupport + frontRun);
            }
        }

        decimal rewardDistance = Math.Abs(target - entry);
        decimal rr = rewardDistance / riskDistance;

        // В bootstrap-режиме тейк должен оставлять хотя бы примерно 1:1 чистый payoff.
        // После накопления статистики можно снова позволять более близкие цели,
        // если фактическая вероятность/EV это подтверждают.
        decimal minGrossRr = bootstrapMode ? 0.85m : 0.52m;
        if (rr < minGrossRr)
            continue;
        hadEnoughRr = true;

        decimal grossProfit = rewardDistance / entry * 100m;
        decimal netProfit = grossProfit - costs;
        if (netProfit < MinNetPotentialProfitPercent)
            continue;
        hadEnoughNet = true;

        decimal netLoss = riskPercent + costs;
        decimal netRewardRisk = netLoss > 0 ? netProfit / netLoss : 0m;

        if (bootstrapMode && netRewardRisk < BootstrapMinNetRewardRisk)
            continue;
        hadEnoughPayoff = true;

        ProbabilityEstimate probability = EstimateTargetProbability(
            direction,
            modelScore,
            spikeQuality,
            entryQuality,
            rr,
            grossProfit,
            riskPercent
        );

        if (!bootstrapMode && probability.ProbabilityPercent < MinEstimatedProbability)
            continue;
        hadEnoughProbability = true;

        decimal p = probability.ProbabilityPercent / 100m;
        decimal ev = p * netProfit - (1m - p) * netLoss;

        if (!bootstrapMode && ev < MinExpectedValuePercent)
            continue;
        hadEnoughEv = true;

        // Bootstrap: среди приемлемых по net payoff целей берём наиболее близкую/
        // вероятную. После накопления истории снова выбираем по empirical probability + EV.
        bool better = !best.Valid;

        if (best.Valid)
        {
            if (bootstrapMode)
            {
                better = probability.ProbabilityPercent > best.ProbabilityPercent + 0.50m ||
                    (Math.Abs(probability.ProbabilityPercent - best.ProbabilityPercent) <= 0.50m &&
                     netRewardRisk > best.NetRewardRisk);
            }
            else
            {
                better = probability.ProbabilityPercent > best.ProbabilityPercent + 0.75m ||
                    (Math.Abs(probability.ProbabilityPercent - best.ProbabilityPercent) <= 0.75m &&
                     ev > best.ExpectedValuePercent);
            }
        }

        if (better)
        {
            best = new TargetPlan
            {
                Valid = true,
                BootstrapMode = bootstrapMode,
                TakeProfit = target,
                RiskReward = rr,
                ProbabilityPercent = probability.ProbabilityPercent,
                ProbabilitySource = bootstrapMode
                    ? probability.Source + " (информационно; bootstrap gate по net payoff)"
                    : probability.Source,
                Samples = probability.Samples,
                GrossProfitPercent = grossProfit,
                RiskPercent = riskPercent,
                EstimatedCostsPercent = costs,
                NetPotentialProfitPercent = netProfit,
                ExpectedValuePercent = ev,
                NetRewardRisk = netRewardRisk,
                RejectReason = ""
            };
        }
    }

    if (!best.Valid)
    {
        best.RejectReason = !hadEnoughRr
            ? "nearest wall/level leaves too little R/R"
            : !hadEnoughNet
                ? $"net TP after costs < {MinNetPotentialProfitPercent:F3}%"
                : bootstrapMode && !hadEnoughPayoff
                    ? $"net reward/risk < {BootstrapMinNetRewardRisk:F2}"
                    : !hadEnoughProbability
                        ? $"estimated probability < {MinEstimatedProbability:F0}%"
                        : !hadEnoughEv
                            ? $"EV < {MinExpectedValuePercent:F3}%"
                            : "target rejected";
    }

    return best;
}

ProbabilityEstimate EstimateTargetProbability(
    string direction,
    decimal modelScore,
    decimal spikeQuality,
    decimal entryQuality,
    decimal rr,
    decimal targetProfitPercent,
    decimal riskPercent)
{
    List<TradeRecord> similar;

    lock (historyLock)
    {
        similar = tradeHistory
            .Where(x =>
                x.Direction == direction &&
                Math.Abs(x.ModelScore - modelScore) <= 10m &&
                Math.Abs(x.SpikeQuality - spikeQuality) <= 15m &&
                Math.Abs(x.EntryQuality - entryQuality) <= 15m)
            .ToList();
    }

    // Самый надёжный вариант: похожие сделки с похожим R/R и фактическим исходом.
    var rrSimilar = similar
        .Where(x => Math.Abs(x.RiskReward - rr) <= 0.25m && x.Result is "TP" or "SL")
        .ToList();

    if (rrSimilar.Count >= 10)
    {
        int wins = rrSimilar.Count(x => x.Result == "TP");
        decimal probability = (wins + 2m) / (rrSimilar.Count + 4m) * 100m;

        return new ProbabilityEstimate
        {
            ProbabilityPercent = probability,
            Samples = rrSimilar.Count,
            Source = "история похожих сделок с похожим R/R"
        };
    }

    // Если похожего R/R мало, используем накопленные MFE/MAE.
    // Это приближение: MFE/MAE не хранит точный порядок касаний TP и SL.
    if (similar.Count >= MinimumHistorySamples)
    {
        decimal equivalentWins = 0m;

        foreach (TradeRecord x in similar)
        {
            decimal historicalRiskPercent = x.EntryPrice > 0
                ? Math.Abs(x.EntryPrice - x.StopLoss) / x.EntryPrice * 100m
                : 0m;

            if (historicalRiskPercent <= 0)
                continue;

            decimal mfeR = x.MaxFavorableExcursionPercent / historicalRiskPercent;
            decimal maeR = x.MaxAdverseExcursionPercent / historicalRiskPercent;

            bool reachedTarget = mfeR >= rr;
            bool reachedStop = maeR >= 1m;

            if (reachedTarget && !reachedStop)
                equivalentWins += 1m;
            else if (reachedTarget && reachedStop)
                equivalentWins += 0.35m; // порядок касаний неизвестен — считаем консервативно
        }

        decimal probability = (equivalentWins + 2m) / (similar.Count + 4m) * 100m;

        return new ProbabilityEstimate
        {
            ProbabilityPercent = probability,
            Samples = similar.Count,
            Source = "историческая оценка по MFE/MAE"
        };
    }

    // Пока локально похожих данных мало — используем консервативную модель,
    // но уже калибруем её общей свежей статистикой стратегии, если она есть.
    decimal baseP = 0.40m + modelScore / 100m * 0.30m;
    decimal rrPenalty = Math.Clamp(1.05m - Math.Max(0m, rr - 0.55m) * 0.20m, 0.76m, 1.05m);
    decimal pModel = Math.Clamp(baseP * rrPenalty, 0.30m, 0.78m);

    List<TradeRecord> recent;
    lock (historyLock)
    {
        recent = tradeHistory
            .Where(x => x.Direction == direction)
            .OrderByDescending(x => x.CloseTime)
            .Take(60)
            .ToList();
    }

    if (recent.Count >= 10)
    {
        int tpWins = recent.Count(x => x.Result == "TP");
        decimal empirical = (tpWins + 3m) / (recent.Count + 6m);
        pModel = pModel * 0.55m + empirical * 0.45m;
    }

    return new ProbabilityEstimate
    {
        ProbabilityPercent = Math.Clamp(pModel * 100m, 25m, 80m),
        Samples = similar.Count,
        Source = recent.Count >= 10
            ? $"модель + калибровка по {recent.Count} свежим сделкам"
            : $"консервативная модель, похожих данных мало ({similar.Count}/{MinimumHistorySamples})"
    };
}


// =====================================================
//         ТЕХНИЧЕСКИЙ АНАЛИЗ / STRUCTURE / PROFILE
// =====================================================

List<Candle> AggregateCandles(List<Candle> source, int minutes)
{
    long bucketMs = minutes * 60_000L;

    return source
        .GroupBy(x => x.StartMs / bucketMs)
        .OrderBy(g => g.Key)
        // Первый и последний bucket могут быть неполными. Для структуры
        // используем только полноценные закрытые свечи.
        .Where(g => g.Count() == minutes)
        .Select(g =>
        {
            List<Candle> rows = g.OrderBy(x => x.StartMs).ToList();

            return new Candle
            {
                StartMs = rows[0].StartMs,
                Open = rows[0].Open,
                High = rows.Max(x => x.High),
                Low = rows.Min(x => x.Low),
                Close = rows[^1].Close,
                Volume = rows.Sum(x => x.Volume),
                Turnover = rows.Sum(x => x.Turnover)
            };
        })
        .ToList();
}

StructureAnalysis AnalyzeStructure(List<Candle> candles, int span)
{
    var result = new StructureAnalysis();

    if (candles.Count < span * 2 + 6)
        return result;

    var highs = new List<PivotPoint>();
    var lows = new List<PivotPoint>();

    for (int i = span; i < candles.Count - span; i++)
    {
        bool pivotHigh = true;
        bool pivotLow = true;

        for (int j = 1; j <= span; j++)
        {
            if (candles[i].High < candles[i - j].High || candles[i].High < candles[i + j].High)
                pivotHigh = false;

            if (candles[i].Low > candles[i - j].Low || candles[i].Low > candles[i + j].Low)
                pivotLow = false;
        }

        if (pivotHigh)
            highs.Add(new PivotPoint { Index = i, Price = candles[i].High });

        if (pivotLow)
            lows.Add(new PivotPoint { Index = i, Price = candles[i].Low });
    }

    if (highs.Count > 0)
        result.LastSwingHigh = highs[^1].Price;

    if (lows.Count > 0)
        result.LastSwingLow = lows[^1].Price;

    if (highs.Count >= 2 && lows.Count >= 2)
    {
        decimal previousHigh = highs[^2].Price;
        decimal lastHigh = highs[^1].Price;
        decimal previousLow = lows[^2].Price;
        decimal lastLow = lows[^1].Price;

        result.HigherHigh = lastHigh > previousHigh;
        result.HigherLow = lastLow > previousLow;
        result.LowerHigh = lastHigh < previousHigh;
        result.LowerLow = lastLow < previousLow;

        if (result.HigherHigh && result.HigherLow)
            result.Bias = "БЫЧЬЯ";
        else if (result.LowerHigh && result.LowerLow)
            result.Bias = "МЕДВЕЖЬЯ";
        else
            result.Bias = "ДИАПАЗОН";
    }
    else
    {
        result.Bias = "ДИАПАЗОН";
    }

    decimal lastClose = candles[^1].Close;
    decimal previousCloseValue = candles[^2].Close;

    if (result.LastSwingHigh > 0 &&
        previousCloseValue <= result.LastSwingHigh &&
        lastClose > result.LastSwingHigh)
    {
        result.Event = result.Bias == "МЕДВЕЖЬЯ" ? "CHOCH ВВЕРХ" : "BOS ВВЕРХ";
    }
    else if (result.LastSwingLow > 0 &&
        previousCloseValue >= result.LastSwingLow &&
        lastClose < result.LastSwingLow)
    {
        result.Event = result.Bias == "БЫЧЬЯ" ? "CHOCH ВНИЗ" : "BOS ВНИЗ";
    }
    else
    {
        result.Event = "НЕТ";
    }

    result.Valid = highs.Count > 0 && lows.Count > 0;
    return result;
}

VolumeProfile BuildVolumeProfile(List<Candle> candles, int bins, decimal valueAreaPercent)
{
    var result = new VolumeProfile();

    if (candles.Count == 0 || bins < 8)
        return result;

    decimal low = candles.Min(x => x.Low);
    decimal high = candles.Max(x => x.High);
    decimal range = high - low;

    if (range <= 0)
        return result;

    decimal step = range / bins;
    var volumes = new decimal[bins];

    // OHLCV не даёт точное распределение внутри свечи, поэтому turnover
    // распределяется по ценовым bins, которые пересекала свеча. Для
    // автоматического фильтра это устойчивее, чем класть весь объём в close.
    foreach (Candle c in candles)
    {
        int from = (int)Math.Clamp(Math.Floor((double)((c.Low - low) / step)), 0, bins - 1);
        int to = (int)Math.Clamp(Math.Floor((double)((c.High - low) / step)), 0, bins - 1);
        int count = Math.Max(1, to - from + 1);
        decimal share = c.Turnover / count;

        for (int i = from; i <= to; i++)
            volumes[i] += share;
    }

    decimal total = volumes.Sum();
    if (total <= 0)
        return result;

    int pocIndex = 0;
    for (int i = 1; i < volumes.Length; i++)
    {
        if (volumes[i] > volumes[pocIndex])
            pocIndex = i;
    }

    decimal target = total * valueAreaPercent;
    decimal accumulated = volumes[pocIndex];
    int left = pocIndex;
    int right = pocIndex;

    while (accumulated < target && (left > 0 || right < bins - 1))
    {
        decimal leftVolume = left > 0 ? volumes[left - 1] : -1m;
        decimal rightVolume = right < bins - 1 ? volumes[right + 1] : -1m;

        if (rightVolume > leftVolume)
        {
            right++;
            accumulated += volumes[right];
        }
        else
        {
            left--;
            accumulated += volumes[left];
        }
    }

    result.Valid = true;
    result.Low = low;
    result.High = high;
    result.Poc = low + (pocIndex + 0.5m) * step;
    result.Val = low + left * step;
    result.Vah = low + (right + 1m) * step;
    return result;
}

TechnicalLevels BuildTechnicalLevels(
    decimal price,
    decimal atr,
    decimal vwap,
    VolumeProfile profile,
    StructureAnalysis structure5m,
    StructureAnalysis structure15m)
{
    var supports = new List<decimal>
    {
        structure5m.LastSwingLow,
        structure15m.LastSwingLow,
        profile.Val,
        profile.Poc,
        vwap
    }
    .Where(x => x > 0 && x < price)
    .ToList();

    var resistances = new List<decimal>
    {
        structure5m.LastSwingHigh,
        structure15m.LastSwingHigh,
        profile.Vah,
        profile.Poc,
        vwap
    }
    .Where(x => x > price)
    .ToList();

    decimal support = supports.Count > 0 ? supports.Max() : 0m;
    decimal resistance = resistances.Count > 0 ? resistances.Min() : 0m;

    // Слишком далёкий уровень не считаем актуальной ближайшей опорой.
    if (atr > 0 && support > 0 && price - support > atr * 4.5m)
        support = 0m;

    if (atr > 0 && resistance > 0 && resistance - price > atr * 4.5m)
        resistance = 0m;

    return new TechnicalLevels
    {
        Support = support,
        Resistance = resistance
    };
}

bool IsUpStructureEvent(string value) =>
    value.Contains("ВВЕРХ", StringComparison.OrdinalIgnoreCase);

bool IsDownStructureEvent(string value) =>
    value.Contains("ВНИЗ", StringComparison.OrdinalIgnoreCase);

bool StructureSupports(string direction, StructureAnalysis structure)
{
    if (!structure.Valid)
        return true;

    if (direction == "LONG")
        return structure.Bias == "БЫЧЬЯ" || IsUpStructureEvent(structure.Event) || structure.Bias == "ДИАПАЗОН";

    return structure.Bias == "МЕДВЕЖЬЯ" || IsDownStructureEvent(structure.Event) || structure.Bias == "ДИАПАЗОН";
}

bool StructureAllowsDirection(
    string direction,
    StructureAnalysis structure5m,
    StructureAnalysis structure15m)
{
    if (direction == "LONG")
    {
        bool seniorOpposes = structure15m.Valid &&
            structure15m.Bias == "МЕДВЕЖЬЯ" &&
            !IsUpStructureEvent(structure15m.Event);

        bool juniorSupports = StructureSupports("LONG", structure5m);
        return !seniorOpposes && juniorSupports;
    }

    bool seniorBullish = structure15m.Valid &&
        structure15m.Bias == "БЫЧЬЯ" &&
        !IsDownStructureEvent(structure15m.Event);

    bool juniorShortSupports = StructureSupports("SHORT", structure5m);
    return !seniorBullish && juniorShortSupports;
}

decimal StructureDirectionScore(string direction)
{
    decimal score = 50m;

    void Apply(StructureAnalysis structure, decimal weight)
    {
        if (!structure.Valid)
            return;

        if (direction == "LONG")
        {
            if (structure.Bias == "БЫЧЬЯ") score += 25m * weight;
            if (structure.Bias == "МЕДВЕЖЬЯ") score -= 25m * weight;
            if (IsUpStructureEvent(structure.Event)) score += 20m * weight;
            if (IsDownStructureEvent(structure.Event)) score -= 25m * weight;
        }
        else
        {
            if (structure.Bias == "МЕДВЕЖЬЯ") score += 25m * weight;
            if (structure.Bias == "БЫЧЬЯ") score -= 25m * weight;
            if (IsDownStructureEvent(structure.Event)) score += 20m * weight;
            if (IsUpStructureEvent(structure.Event)) score -= 25m * weight;
        }
    }

    Apply(market.Structure5m, 0.45m);
    Apply(market.Structure15m, 0.75m);

    return Math.Clamp(score, 0m, 100m);
}

decimal CalculateTechnicalConfluence(string direction, decimal price)
{
    if (price <= 0 || market.Atr <= 0)
        return 50m;

    var levels = new List<(decimal Price, decimal Weight)>();

    if (direction == "LONG")
    {
        levels.Add((market.NearestSupport, 1.35m));
        levels.Add((market.Structure5m.LastSwingLow, 1.00m));
        levels.Add((market.Structure15m.LastSwingLow, 1.20m));
        levels.Add((market.Val, 1.10m));
        levels.Add((market.Poc, 0.90m));
        levels.Add((market.RollingVwap, 1.00m));
    }
    else
    {
        levels.Add((market.NearestResistance, 1.35m));
        levels.Add((market.Structure5m.LastSwingHigh, 1.00m));
        levels.Add((market.Structure15m.LastSwingHigh, 1.20m));
        levels.Add((market.Vah, 1.10m));
        levels.Add((market.Poc, 0.90m));
        levels.Add((market.RollingVwap, 1.00m));
    }

    decimal weighted = 0m;
    decimal totalWeight = 0m;
    decimal radius = market.Atr * 0.55m;

    foreach ((decimal level, decimal weight) in levels)
    {
        if (level <= 0)
            continue;

        // Для LONG уровень должен быть около/под ценой, для SHORT около/над.
        bool wrongSide = direction == "LONG"
            ? level > price + market.Atr * 0.18m
            : level < price - market.Atr * 0.18m;

        if (wrongSide)
            continue;

        decimal distance = Math.Abs(price - level);
        decimal closeness = Math.Clamp(1m - distance / Math.Max(radius, 0.01m), 0m, 1m);

        weighted += closeness * weight;
        totalWeight += weight;
    }

    decimal location = totalWeight > 0 ? weighted / totalWeight * 100m : 25m;
    decimal structure = StructureDirectionScore(direction);

    // Положение относительно value area: LONG предпочтительнее в нижней части,
    // SHORT — в верхней. Но POC/VWAP-конфлюэнс может компенсировать середину.
    decimal valueLocation = 50m;
    if (market.Vah > market.Val)
    {
        decimal pos = Math.Clamp((price - market.Val) / (market.Vah - market.Val), 0m, 1m);
        valueLocation = direction == "LONG"
            ? (1m - pos) * 100m
            : pos * 100m;
    }

    decimal result =
        location * 0.58m +
        structure * 0.27m +
        valueLocation * 0.15m;

    return Math.Clamp(result, 0m, 100m);
}

decimal FindTechnicalAnchor(
    string direction,
    decimal rawEntry,
    decimal rawZoneLow,
    decimal rawZoneHigh)
{
    if (market.Atr <= 0)
        return 0m;

    var candidates = new List<(decimal Price, decimal Weight)>
    {
        (market.RollingVwap, 1.00m),
        (market.Poc, 0.95m),
        (direction == "LONG" ? market.Val : market.Vah, 1.10m),
        (direction == "LONG" ? market.NearestSupport : market.NearestResistance, 1.35m),
        (direction == "LONG" ? market.Structure5m.LastSwingLow : market.Structure5m.LastSwingHigh, 1.05m),
        (direction == "LONG" ? market.Structure15m.LastSwingLow : market.Structure15m.LastSwingHigh, 1.20m)
    };

    decimal extendedLow = rawZoneLow - market.Atr * 0.35m;
    decimal extendedHigh = rawZoneHigh + market.Atr * 0.35m;
    decimal weightedPrice = 0m;
    decimal totalWeight = 0m;

    foreach ((decimal level, decimal baseWeight) in candidates)
    {
        if (level <= 0 || level < extendedLow || level > extendedHigh)
            continue;

        decimal distanceAtr = Math.Abs(level - rawEntry) / market.Atr;
        decimal proximity = Math.Clamp(1m - distanceAtr / 0.70m, 0.15m, 1m);
        decimal weight = baseWeight * proximity;

        weightedPrice += level * weight;
        totalWeight += weight;
    }

    return totalWeight > 0 ? weightedPrice / totalWeight : 0m;
}

decimal FindNearestTechnicalResistanceAbove(decimal price)
{
    var levels = new List<decimal>
    {
        market.NearestResistance,
        market.Vah,
        market.Poc,
        market.Structure5m.LastSwingHigh,
        market.Structure15m.LastSwingHigh
    }
    .Where(x => x > price)
    .ToList();

    return levels.Count > 0 ? levels.Min() : 0m;
}

decimal FindNearestTechnicalSupportBelow(decimal price)
{
    var levels = new List<decimal>
    {
        market.NearestSupport,
        market.Val,
        market.Poc,
        market.Structure5m.LastSwingLow,
        market.Structure15m.LastSwingLow
    }
    .Where(x => x > 0 && x < price)
    .ToList();

    return levels.Count > 0 ? levels.Max() : 0m;
}

string FormatStructure(StructureAnalysis structure)
{
    if (!structure.Valid)
        return "недостаточно данных";

    string swings = $"H {structure.LastSwingHigh:F2} / L {structure.LastSwingLow:F2}";

    return structure.Event == "НЕТ"
        ? $"{structure.Bias} ({swings})"
        : $"{structure.Bias}, {structure.Event} ({swings})";
}

// =====================================================
//                МОНИТОРИНГ PAPER-СДЕЛКИ
// =====================================================

async Task CheckActiveTrade(decimal currentPrice)
{
    if (activeTrade == null)
        return;

    ActiveTrade trade = activeTrade;

    if (trade.Status == TradeStatus.WaitingForEntry)
    {
        if (DateTime.UtcNow - trade.SignalTime > TimeSpan.FromMinutes(SignalLifetimeMinutes))
        {
            await SendTelegram(
                $"❌ Сигнал {trade.Direction} отменён\n\n" +
                $"За {SignalLifetimeMinutes} минуты не появилось качественного подтверждённого входа."
            );

            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        bool invalidated = trade.Direction == "LONG"
            ? currentPrice <= trade.InitialStopLoss
            : currentPrice >= trade.InitialStopLoss;

        if (invalidated)
        {
            await SendTelegram(
                $"❌ Сигнал {trade.Direction} сломан до входа\n\n" +
                $"Текущая цена: ${currentPrice:F2}\n" +
                $"Уровень отмены: ${trade.InitialStopLoss:F2}"
            );

            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        bool insideZone = currentPrice >= trade.ZoneLow && currentPrice <= trade.ZoneHigh;

        if (!trade.EntryZoneTouched)
        {
            if (!insideZone)
                return;

            trade.EntryZoneTouched = true;
            trade.EntryZoneTouchTime = DateTime.UtcNow;
            trade.EntryZoneExtreme = currentPrice;

            await SendTelegram(
                $"👀 Цена вошла в зону {trade.Direction}\n\n" +
                $"Цена: ${currentPrice:F2}\n" +
                "На первом касании НЕ вхожу. Жду подтверждение возврата потока и стакана."
            );

            return;
        }

        if (trade.Direction == "LONG")
            trade.EntryZoneExtreme = Math.Min(trade.EntryZoneExtreme, currentPrice);
        else
            trade.EntryZoneExtreme = Math.Max(trade.EntryZoneExtreme, currentPrice);

        if (trade.EntryZoneTouchTime.HasValue &&
            DateTime.UtcNow - trade.EntryZoneTouchTime.Value > TimeSpan.FromSeconds(EntryConfirmationTimeoutSeconds))
        {
            await SendTelegram(
                $"❌ Вход {trade.Direction} отменён\n\n" +
                "После касания зоны цена не дала подтверждения вовремя."
            );

            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        if (!EntryConfirmationPassed(trade, currentPrice))
            return;

        string entrySide = trade.Direction == "LONG" ? "BUY" : "SELL";
        decimal fill = orderBook.EstimateFillPrice(entrySide, PaperNotionalUsdt, currentPrice);
        decimal slippage = Math.Abs(fill - currentPrice) / currentPrice * 100m;

        if (slippage > MaxEntrySlippagePercent)
        {
            await SendTelegram(
                $"⚠️ Сигнал {trade.Direction} отменён из-за исполнения\n\n" +
                $"Оценённое проскальзывание: {slippage:F3}%\n" +
                $"Допустимый максимум: {MaxEntrySlippagePercent:F3}%"
            );

            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        OrderBookMetrics liveBook = orderBook.GetMetrics();
        TickerSnapshot liveTicker = ticker.GetSnapshot();
        decimal liveOi = ticker.GetOpenInterestChangePercent(30);
        var livePlan = new EntryPlan
        {
            Valid = true,
            ZoneLow = trade.ZoneLow,
            ZoneHigh = trade.ZoneHigh,
            Entry = fill,
            StopLoss = trade.InitialStopLoss
        };

        decimal liveEntryQuality = CalculateEntryQuality(
            trade.Direction,
            livePlan,
            liveBook,
            liveTicker,
            liveOi,
            currentPrice
        );

        if (liveEntryQuality < MinEntryQuality)
            return;

        trade.ActualEntry = fill;
        trade.OpenTime = DateTime.UtcNow;
        trade.EntryQuality = Math.Max(trade.EntryQuality, liveEntryQuality);
        trade.InitialRiskDistance = Math.Abs(trade.ActualEntry - trade.InitialStopLoss);
        trade.CurrentStopLoss = trade.InitialStopLoss;

        decimal actualRiskPercent = trade.ActualEntry > 0
            ? trade.InitialRiskDistance / trade.ActualEntry * 100m
            : 0m;

        if (trade.InitialRiskDistance <= 0 ||
            actualRiskPercent < MinInitialRiskPercent ||
            actualRiskPercent > MaxInitialRiskPercent)
        {
            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        TargetPlan recalculated = OptimizeTarget(
            trade.Direction,
            trade.ActualEntry,
            trade.InitialStopLoss,
            trade.ModelScore,
            trade.SpikeQuality,
            trade.EntryQuality
        );

        AdaptiveThresholds adaptive = GetAdaptiveThresholds();

        if (!recalculated.Valid ||
            recalculated.NetPotentialProfitPercent < MinNetPotentialProfitPercent ||
            (!recalculated.BootstrapMode &&
             (recalculated.ExpectedValuePercent < MinExpectedValuePercent + adaptive.EvBoost ||
              recalculated.ProbabilityPercent < MinEstimatedProbability + adaptive.ProbabilityBoost)))
        {
            await SendTelegram(
                $"⚠️ Вход {trade.Direction} пропущен\n\n" +
                $"После подтверждения target-фильтр не прошёл: {recalculated.RejectReason}."
            );

            activeTrade = null;
            lastTradeFinishedAt = DateTime.UtcNow;
            return;
        }

        trade.TakeProfit = recalculated.TakeProfit;
        trade.RiskReward = recalculated.RiskReward;
        trade.EstimatedProbability = recalculated.ProbabilityPercent;
        trade.ProbabilitySource = recalculated.ProbabilitySource;
        trade.ProbabilitySamples = recalculated.Samples;
        trade.GrossProfitPercent = recalculated.GrossProfitPercent;
        trade.RiskPercent = recalculated.RiskPercent;
        trade.EstimatedCostsPercent = recalculated.EstimatedCostsPercent;
        trade.NetPotentialProfitPercent = recalculated.NetPotentialProfitPercent;
        trade.ExpectedValuePercent = recalculated.ExpectedValuePercent;
        trade.Status = TradeStatus.Open;

        await SendTelegram(
            $"✅ PAPER-СДЕЛКА ОТКРЫТА | {trade.Direction}\n\n" +
            $"⭐ Подтверждённый вход: ${trade.ActualEntry:F2}\n" +
            $"Оценка проскальзывания: {slippage:F3}%\n\n" +
            $"🛑 Начальный стоп: ${trade.InitialStopLoss:F2} (-{trade.RiskPercent:F2}%)\n" +
            $"💰 Один тейк: ${trade.TakeProfit:F2} (+{trade.GrossProfitPercent:F2}%)\n" +
            $"📐 R/R: 1:{trade.RiskReward:F2}\n\n" +
            $"🎲 Оценка достижения TP: {trade.EstimatedProbability:F1}%\n" +
            $"📈 Потенциальная чистая прибыль: +{trade.NetPotentialProfitPercent:F3}%\n" +
            $"🧮 EV: {trade.ExpectedValuePercent:+0.000;-0.000}%\n\n" +
            "Защитная логика: после достаточного движения стоп подтягивается, а при подтверждённом развороте потока сделка закрывается раньше полного SL."
        );

        return;
    }

    if (trade.Status != TradeStatus.Open)
        return;

    UpdateExcursions(trade, currentPrice);

    bool hitTp = trade.Direction == "LONG"
        ? currentPrice >= trade.TakeProfit
        : currentPrice <= trade.TakeProfit;

    if (hitTp)
    {
        await CloseTradeAtMarket(trade, "TP", currentPrice, "🏆 Сделка закрыта по тейку");
        return;
    }

    decimal favorableDistance = trade.Direction == "LONG"
        ? currentPrice - trade.ActualEntry
        : trade.ActualEntry - currentPrice;

    decimal currentR = trade.InitialRiskDistance > 0
        ? favorableDistance / trade.InitialRiskDistance
        : 0m;

    trade.BestR = Math.Max(trade.BestR, currentR);

    // Безубыток должен быть достижим физически: если комиссия больше половины R,
    // ждём чуть больше, а не ставим стоп выше текущей цены.
    decimal costBufferPercent = EstimatedRoundTripFeePercent + 0.02m;
    decimal costBufferDistance = trade.ActualEntry * costBufferPercent / 100m;
    decimal minRForBreakEven = trade.InitialRiskDistance > 0
        ? costBufferDistance / trade.InitialRiskDistance + 0.10m
        : BreakEvenAtR;
    decimal effectiveBreakEvenR = Math.Max(BreakEvenAtR, minRForBreakEven);

    if (!trade.BreakEvenActivated && currentR >= effectiveBreakEvenR)
    {
        decimal beStop = trade.Direction == "LONG"
            ? trade.ActualEntry + costBufferDistance
            : trade.ActualEntry - costBufferDistance;

        bool feasible = trade.Direction == "LONG"
            ? beStop < currentPrice
            : beStop > currentPrice;

        if (feasible)
        {
            trade.CurrentStopLoss = MoreProtectiveStop(trade, trade.CurrentStopLoss, beStop);
            trade.BreakEvenActivated = true;

            await SendTelegram(
                $"🛡 {trade.Direction}: риск уменьшен\n\n" +
                $"Цена прошла {currentR:F2}R.\n" +
                $"Новый защитный стоп: ${trade.CurrentStopLoss:F2}"
            );
        }
    }

    if (!trade.ProfitLockActivated && currentR >= ProfitLockAtR)
    {
        decimal lockStop = trade.Direction == "LONG"
            ? trade.ActualEntry + trade.InitialRiskDistance * ProfitLockR
            : trade.ActualEntry - trade.InitialRiskDistance * ProfitLockR;

        trade.CurrentStopLoss = MoreProtectiveStop(trade, trade.CurrentStopLoss, lockStop);
        trade.ProfitLockActivated = true;

        await SendTelegram(
            $"🔒 {trade.Direction}: прибыль защищена\n\n" +
            $"Лучшее движение: {trade.BestR:F2}R\n" +
            $"Стоп подтянут до ${trade.CurrentStopLoss:F2}"
        );
    }

    bool hitStop = trade.Direction == "LONG"
        ? currentPrice <= trade.CurrentStopLoss
        : currentPrice >= trade.CurrentStopLoss;

    if (hitStop)
    {
        string result = trade.ProfitLockActivated
            ? "TRAIL"
            : trade.BreakEvenActivated
                ? "BE"
                : "SL";

        string title = result == "SL"
            ? "🛑 Сделка закрыта по стопу"
            : result == "BE"
                ? "🛡 Сделка закрыта защитным стопом около безубытка"
                : "🔒 Сделка закрыта защитным стопом с прибылью";

        await CloseTradeAtMarket(trade, result, currentPrice, title);
        return;
    }

    bool strongReversal = IsStrongReversalAgainst(trade.Direction);

    if (strongReversal)
    {
        trade.ReversalStartedAt ??= DateTime.UtcNow;

        if (DateTime.UtcNow - trade.ReversalStartedAt.Value >= TimeSpan.FromSeconds(ReversalConfirmSeconds))
        {
            await CloseTradeAtMarket(
                trade,
                "REV",
                currentPrice,
                "⚠️ Сделка закрыта досрочно: подтверждён разворот потока"
            );
            return;
        }
    }
    else
    {
        trade.ReversalStartedAt = null;
    }

    if (trade.OpenTime.HasValue)
    {
        TimeSpan age = DateTime.UtcNow - trade.OpenTime.Value;

        if (age > TimeSpan.FromMinutes(StaleTradeMinutes) && trade.BestR < 0.25m)
        {
            await CloseTradeAtMarket(
                trade,
                "STALE",
                currentPrice,
                "⏱ Сделка закрыта: импульс потерял силу"
            );
            return;
        }

        if (age > TimeSpan.FromMinutes(MaxTradeLifetimeMinutes))
        {
            await CloseTradeAtMarket(trade, "TIME", currentPrice, "⏱ Сделка закрыта по тайм-ауту");
        }
    }
}

bool EntryConfirmationPassed(ActiveTrade trade, decimal currentPrice)
{
    if (!trade.EntryZoneTouchTime.HasValue ||
        DateTime.UtcNow - trade.EntryZoneTouchTime.Value < TimeSpan.FromMilliseconds(EntryConfirmationMinMilliseconds))
    {
        return false;
    }

    FlowMetrics f3 = GetFlowMetrics(3);
    FlowMetrics f5 = GetFlowMetrics(5);
    OrderBookMetrics book = orderBook.GetMetrics();

    if (!f3.Valid || !f5.Valid || !book.Valid)
        return false;

    bool isLong = trade.Direction == "LONG";
    decimal zoneMid = (trade.ZoneLow + trade.ZoneHigh) / 2m;
    decimal zoneWidth = Math.Max(trade.ZoneHigh - trade.ZoneLow, 0.01m);

    decimal bounceDistance = isLong
        ? currentPrice - trade.EntryZoneExtreme
        : trade.EntryZoneExtreme - currentPrice;

    bool notChasing = isLong
        ? currentPrice <= trade.ZoneHigh + zoneWidth * 0.15m
        : currentPrice >= trade.ZoneLow - zoneWidth * 0.15m;

    decimal minimumBounce = Math.Max(zoneWidth * 0.20m, market.Atr * MinBounceAtr);

    bool priceReclaimed = isLong
        ? currentPrice >= zoneMid && bounceDistance >= minimumBounce && notChasing
        : currentPrice <= zoneMid && bounceDistance >= minimumBounce && notChasing;

    decimal flow3 = isLong ? f3.CvdNormalized : -f3.CvdNormalized;
    decimal flow5 = isLong ? f5.CvdNormalized : -f5.CvdNormalized;
    decimal share3 = isLong ? f3.BuyShare : f3.SellShare;
    decimal share5 = isLong ? f5.BuyShare : f5.SellShare;
    decimal bookDir = isLong ? book.PersistentImbalance5s : 1m - book.PersistentImbalance5s;
    decimal ofi = isLong ? book.Ofi5sNormalized : -book.Ofi5sNormalized;

    decimal microBps = book.MidPrice > 0
        ? (book.MicroPrice - book.MidPrice) / book.MidPrice * 10000m
        : 0m;
    decimal directedMicro = isLong ? microBps : -microBps;

    decimal technicalConfluence = CalculateTechnicalConfluence(trade.Direction, currentPrice);
    bool structureStillValid = StructureAllowsDirection(
        trade.Direction,
        market.Structure5m,
        market.Structure15m
    );

    bool localPriceTurn = isLong
        ? f3.PriceChangePercent >= 0.008m && f5.PriceChangePercent >= -0.018m
        : f3.PriceChangePercent <= -0.008m && f5.PriceChangePercent <= 0.018m;

    // V2: прежний вход требовал одновременно пройти все microstructure-фильтры,
    // из-за чего даже сильный WATCH почти не мог открыться. Сохраняем обязательные
    // price/structure/spread/technical проверки, а flow/book/micro считаем блоками:
    // достаточно 2 из 3 подтверждений.
    int entryConfirmations = 0;

    bool flowConfirmed =
        (share3 >= 0.55m && flow3 >= 0.03m) ||
        (share5 >= 0.54m && flow5 >= 0.02m);
    if (flowConfirmed) entryConfirmations++;

    bool bookConfirmed = bookDir >= 0.47m && ofi >= -0.050m;
    if (bookConfirmed) entryConfirmations++;

    bool microConfirmed = directedMicro >= -0.75m;
    if (microConfirmed) entryConfirmations++;

    return priceReclaimed &&
        localPriceTurn &&
        book.SpreadBps <= MaxAllowedSpreadBps &&
        technicalConfluence >= 24m &&
        structureStillValid &&
        entryConfirmations >= 2;
}

decimal MoreProtectiveStop(ActiveTrade trade, decimal currentStop, decimal candidate)
{
    return trade.Direction == "LONG"
        ? Math.Max(currentStop, candidate)
        : Math.Min(currentStop, candidate);
}

bool IsStrongReversalAgainst(string direction)
{
    FlowMetrics f5 = GetFlowMetrics(5);
    OrderBookMetrics book = orderBook.GetMetrics();

    if (!f5.Valid || !book.Valid)
        return false;

    bool isLong = direction == "LONG";
    decimal oppositeShare = isLong ? f5.SellShare : f5.BuyShare;
    decimal oppositeCvd = isLong ? -f5.CvdNormalized : f5.CvdNormalized;
    decimal oppositeBook = isLong ? 1m - book.PersistentImbalance5s : book.PersistentImbalance5s;
    decimal oppositeOfi = isLong ? -book.Ofi5sNormalized : book.Ofi5sNormalized;

    int confirmations = 0;
    if (oppositeShare >= 0.64m) confirmations++;
    if (oppositeCvd >= 0.22m) confirmations++;
    if (oppositeBook >= 0.56m) confirmations++;
    if (oppositeOfi >= 0.018m) confirmations++;

    return confirmations >= 3;
}

void UpdateExcursions(ActiveTrade trade, decimal currentPrice)
{
    if (trade.ActualEntry <= 0)
        return;

    decimal favorable;
    decimal adverse;

    if (trade.Direction == "LONG")
    {
        favorable = Math.Max(0m, (currentPrice - trade.ActualEntry) / trade.ActualEntry * 100m);
        adverse = Math.Max(0m, (trade.ActualEntry - currentPrice) / trade.ActualEntry * 100m);
    }
    else
    {
        favorable = Math.Max(0m, (trade.ActualEntry - currentPrice) / trade.ActualEntry * 100m);
        adverse = Math.Max(0m, (currentPrice - trade.ActualEntry) / trade.ActualEntry * 100m);
    }

    trade.MaxFavorableExcursionPercent = Math.Max(trade.MaxFavorableExcursionPercent, favorable);
    trade.MaxAdverseExcursionPercent = Math.Max(trade.MaxAdverseExcursionPercent, adverse);
}

async Task CloseTradeAtMarket(
    ActiveTrade trade,
    string result,
    decimal triggerPrice,
    string title)
{
    string exitSide = trade.Direction == "LONG" ? "SELL" : "BUY";
    decimal fill = orderBook.EstimateFillPrice(exitSide, PaperNotionalUsdt, triggerPrice);

    decimal grossPnl = trade.Direction == "LONG"
        ? (fill - trade.ActualEntry) / trade.ActualEntry * 100m
        : (trade.ActualEntry - fill) / trade.ActualEntry * 100m;

    decimal netPnl = grossPnl - EstimatedRoundTripFeePercent;
    decimal exitSlippage = Math.Abs(fill - triggerPrice) / triggerPrice * 100m;

    string closeType = result switch
    {
        "TP" => "🏆 ЗАКРЫТА ПО ТЕЙКУ",
        "SL" => "🛑 ЗАКРЫТА ПО СТОПУ",
        "BE" => "🛑 ЗАКРЫТА ПО ЗАЩИТНОМУ СТОПУ (БЕЗУБЫТОК)",
        "TRAIL" => "🛑 ЗАКРЫТА ПО ЗАЩИТНОМУ СТОПУ (С ПРИБЫЛЬЮ)",
        "REV" => "⚠️ ЗАКРЫТА ДОСРОЧНО ПО РАЗВОРОТУ ПОТОКА",
        "STALE" => "⏱ ЗАКРЫТА ДОСРОЧНО: ИМПУЛЬС ОСЛАБ",
        "TIME" => "⏱ ЗАКРЫТА ПО ТАЙМ-АУТУ",
        _ => $"ЗАКРЫТА: {result}"
    };

    RegisterTradeResult(trade, result, fill, grossPnl, netPnl);

    await SendTelegram(
        $"{title}\n\n" +
        $"{closeType}\n\n" +
        $"Направление: {trade.Direction}\n" +
        $"Вход: ${trade.ActualEntry:F2}\n" +
        $"Выход: ${fill:F2}\n" +
        $"Проскальзывание выхода: {exitSlippage:F3}%\n\n" +
        $"Валовый PnL: {grossPnl:+0.000;-0.000}%\n" +
        $"Комиссия round-trip: -{EstimatedRoundTripFeePercent:F3}%\n" +
        $"Чистый paper PnL: {netPnl:+0.000;-0.000}%\n\n" +
        $"MFE: +{trade.MaxFavorableExcursionPercent:F3}%\n" +
        $"MAE: -{trade.MaxAdverseExcursionPercent:F3}%\n" +
        $"Оценка TP при входе: {trade.EstimatedProbability:F1}%"
    );

    activeTrade = null;
    lastTradeFinishedAt = DateTime.UtcNow;

    // После полноценного стопа рынок часто остаётся в том же шумном режиме.
    // Не перезаходим немедленно в серию похожих ложных импульсов.
    if (result == "SL")
        signalBlockUntil = DateTime.UtcNow.AddMinutes(5);
    else if (netPnl < 0m)
        signalBlockUntil = DateTime.UtcNow.AddMinutes(2);
}

// =====================================================
//                      ИСТОРИЯ
// =====================================================

void RegisterTradeResult(
    ActiveTrade trade,
    string result,
    decimal exitPrice,
    decimal grossPnl,
    decimal netPnl)
{
    var record = new TradeRecord
    {
        CloseTime = DateTime.UtcNow,
        Direction = trade.Direction,
        Result = result,
        ModelScore = trade.ModelScore,
        SpikeQuality = trade.SpikeQuality,
        EntryQuality = trade.EntryQuality,
        RiskReward = trade.RiskReward,
        EntryPrice = trade.ActualEntry,
        ExitPrice = exitPrice,
        StopLoss = trade.InitialStopLoss,
        TakeProfit = trade.TakeProfit,
        GrossPnlPercent = grossPnl,
        NetPnlPercent = netPnl,
        MaxFavorableExcursionPercent = trade.MaxFavorableExcursionPercent,
        MaxAdverseExcursionPercent = trade.MaxAdverseExcursionPercent,
        EstimatedProbability = trade.EstimatedProbability,
        EstimatedCostsPercent = trade.EstimatedCostsPercent,
        VolumeRatio = trade.VolumeRatio,
        VolumeZ = trade.VolumeZ,
        OrderBookImbalance = trade.OrderBookImbalance,
        PersistentBookImbalance = trade.PersistentBookImbalance,
        OiChange30 = trade.OiChange30,
        CvdNormalized = trade.CvdNormalized,
        Ofi5s = trade.Ofi5s,
        SpreadBps = trade.SpreadBps,
        Trend5m = trade.Trend5m,
        Trend15m = trade.Trend15m,
        ImpulseEfficiency = trade.ImpulseEfficiency,
        ImpulseWindowSeconds = trade.ImpulseWindowSeconds,
        ImpulseRangeUsd = trade.ImpulseRangeUsd,
        ImpulseRangeAtr = trade.ImpulseRangeAtr,
        TechnicalConfluence = trade.TechnicalConfluence
    };

    lock (historyLock)
    {
        tradeHistory.Add(record);
        SaveTradeHistoryUnsafe();
    }

    UpdateWatchHistoryTradeResult(
        trade.SourceWatchId,
        result,
        netPnl
    );
}

void ResetStatistics()
{
    // 1. Полностью удаляем историю закрытых paper-сделок.
    lock (historyLock)
    {
        tradeHistory.Clear();

        try
        {
            SaveTradeHistoryUnsafe();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обнуления статистики: {ex.Message}");
        }
    }

    // 2. Отменяем текущую активную сделку или сигнал ожидания входа.
    activeTrade = null;

    // 3. Полностью сбрасываем кандидата на новый сигнал и WATCH.
    if (watchCandidate != null)
        UpdateWatchHistoryEnd(watchCandidate, "RESET", "WATCH отменён командой сброса статистики.");

    pendingSignalDirection = null;
    pendingSignalSince = DateTime.MinValue;
    pendingSignalLastSeen = DateTime.MinValue;
    watchCandidate = null;
    lastWatchFinishedAt = DateTime.MinValue;
    lastSignalEvaluationAt = DateTime.MinValue;

    lock (diagnosticsLock)
    {
        diagnostics = new SignalDiagnostics();
    }

    // 4. Сбрасываем блокировки/cooldown, чтобы после reset бот начинал с чистого листа.
    lastTradeFinishedAt = DateTime.MinValue;
    signalBlockUntil = DateTime.MinValue;

    Console.WriteLine($"🧹 Полный сброс выполнен. История: {tradeHistory.Count}. Файл: {HistoryFile}");
}

List<TradeRecord> LoadTradeHistory()
{
    try
    {
        if (!File.Exists(HistoryFile))
            return new List<TradeRecord>();

        string json = File.ReadAllText(HistoryFile);
        return JsonSerializer.Deserialize<List<TradeRecord>>(json) ?? new List<TradeRecord>();
    }
    catch
    {
        return new List<TradeRecord>();
    }
}

void SaveTradeHistoryUnsafe()
{
    string json = JsonSerializer.Serialize(
        tradeHistory,
        new JsonSerializerOptions { WriteIndented = true }
    );

    File.WriteAllText(HistoryFile, json);
}


// =====================================================
//                   WATCH-ИСТОРИЯ
// =====================================================

List<WatchHistoryRecord> LoadWatchHistory()
{
    try
    {
        if (!File.Exists(WatchHistoryFile))
            return new List<WatchHistoryRecord>();

        string json = File.ReadAllText(WatchHistoryFile);
        return JsonSerializer.Deserialize<List<WatchHistoryRecord>>(json)
            ?? new List<WatchHistoryRecord>();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Не удалось загрузить WATCH-историю: {ex.Message}");
        return new List<WatchHistoryRecord>();
    }
}

void SaveWatchHistoryUnsafe()
{
    string json = JsonSerializer.Serialize(
        watchHistory,
        new JsonSerializerOptions { WriteIndented = true }
    );

    File.WriteAllText(WatchHistoryFile, json);
}

void RegisterWatchStart(WatchCandidate watch, OrderBookMetrics book, decimal oiChange30)
{
    FlowMetrics impulse = watch.Impulse;

    var record = new WatchHistoryRecord
    {
        Id = watch.Id,
        Direction = watch.Direction,
        CreatedAt = watch.CreatedAt,
        Status = "WATCH",
        ImpulseWindowSeconds = impulse.WindowSeconds,
        InitialPrice = watch.InitialPrice,
        ImpulseLow = impulse.Low,
        ImpulseHigh = impulse.High,
        ImpulseRangeUsd = impulse.High - impulse.Low,
        ImpulseRangeAtr = market.Atr > 0 ? (impulse.High - impulse.Low) / market.Atr : 0m,
        VolumeRatio = impulse.VolumeRatio,
        VolumeZ = watch.VolumeZ,
        BuyShare = impulse.BuyShare,
        SellShare = impulse.SellShare,
        CvdNormalized = impulse.CvdNormalized,
        PriceChangePercent = impulse.PriceChangePercent,
        ImpulseEfficiency = impulse.Efficiency,
        InitialBookImbalance = book.Imbalance,
        InitialPersistentBookImbalance = book.PersistentImbalance5s,
        InitialOfi5s = book.Ofi5sNormalized,
        InitialSpreadBps = book.SpreadBps,
        InitialOiChange30 = oiChange30,
        Atr = market.Atr,
        Trend5m = market.Trend5mPercent,
        Trend15m = market.Trend15mPercent,
        Vwap = market.RollingVwap,
        Poc = market.Poc,
        Vah = market.Vah,
        Val = market.Val,
        Support = market.NearestSupport,
        Resistance = market.NearestResistance,
        Structure5mBias = market.Structure5m.Bias,
        Structure5mEvent = market.Structure5m.Event,
        Structure15mBias = market.Structure15m.Bias,
        Structure15mEvent = market.Structure15m.Event
    };

    lock (watchHistoryLock)
    {
        watchHistory.Add(record);
        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistoryPullback(WatchCandidate watch)
{
    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == watch.Id);
        if (record == null) return;

        record.PullbackSeen = true;
        record.PullbackSeenAt = watch.PullbackSeenAt;
        record.MaxPullbackFraction = Math.Max(record.MaxPullbackFraction, watch.PullbackFraction);
        record.PullbackExtreme = watch.PullbackExtreme;
        record.PullbackBaseF5VolumeRatio = watch.PullbackBaseF5VolumeRatio;
        record.PullbackBaseF15VolumeRatio = watch.PullbackBaseF15VolumeRatio;
        record.Status = "PULLBACK";
        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistoryReacceleration(
    WatchCandidate watch,
    int confirmations,
    decimal quality,
    FlowMetrics f5,
    FlowMetrics f15,
    OrderBookMetrics book)
{
    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == watch.Id);
        if (record == null) return;

        record.ReaccelerationSeen = true;
        record.ReaccelerationSeenAt = watch.ReaccelerationSeenAt;
        record.ReaccelerationConfirmations = confirmations;
        record.ReaccelerationQuality = quality;
        record.ReaccelVolume5 = f5.VolumeRatio;
        record.ReaccelVolume15 = f15.VolumeRatio;
        record.ReaccelCvd5 = f5.CvdNormalized;
        record.ReaccelCvd15 = f15.CvdNormalized;
        record.ReaccelBuyShare5 = f5.BuyShare;
        record.ReaccelSellShare5 = f5.SellShare;
        record.ReaccelBookImbalance = book.Imbalance;
        record.ReaccelPersistentBookImbalance = book.PersistentImbalance5s;
        record.ReaccelOfi5s = book.Ofi5sNormalized;
        record.MaxPullbackFraction = Math.Max(record.MaxPullbackFraction, watch.PullbackFraction);
        record.Status = "REACCELERATION";
        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistoryFinalAttempt(
    WatchCandidate watch,
    string reason,
    decimal spikeQuality = 0m,
    decimal entryQuality = 0m,
    decimal technicalConfluence = 0m,
    decimal modelScore = 0m,
    EntryPlan? entryPlan = null,
    TargetPlan? target = null)
{
    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == watch.Id);
        if (record == null) return;

        record.LastFinalRejectionReason = reason;
        record.SpikeQuality = Math.Max(record.SpikeQuality, spikeQuality);
        record.EntryQuality = Math.Max(record.EntryQuality, entryQuality);
        record.TechnicalConfluence = Math.Max(record.TechnicalConfluence, technicalConfluence);
        record.ModelScore = Math.Max(record.ModelScore, modelScore);
        record.MaxPullbackFraction = Math.Max(record.MaxPullbackFraction, watch.PullbackFraction);

        if (entryPlan != null && entryPlan.Valid)
        {
            record.EntryPrice = entryPlan.Entry;
            record.StopLoss = entryPlan.StopLoss;
        }

        if (target != null && target.Valid)
        {
            record.TakeProfit = target.TakeProfit;
            record.EstimatedProbability = target.ProbabilityPercent;
            record.ExpectedValuePercent = target.ExpectedValuePercent;
            record.NetPotentialProfitPercent = target.NetPotentialProfitPercent;
            record.RiskReward = target.RiskReward;
        }

        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistorySignalCreated(
    WatchCandidate watch,
    decimal spikeQuality,
    decimal entryQuality,
    decimal technicalConfluence,
    decimal modelScore,
    EntryPlan entryPlan,
    TargetPlan target)
{
    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == watch.Id);
        if (record == null) return;

        record.Status = "SIGNAL_CREATED";
        record.SignalCreatedAt = DateTime.UtcNow;
        record.MaxPullbackFraction = Math.Max(record.MaxPullbackFraction, watch.PullbackFraction);
        record.SpikeQuality = spikeQuality;
        record.EntryQuality = entryQuality;
        record.TechnicalConfluence = technicalConfluence;
        record.ModelScore = modelScore;
        record.EntryPrice = entryPlan.Entry;
        record.StopLoss = entryPlan.StopLoss;
        record.TakeProfit = target.TakeProfit;
        record.EstimatedProbability = target.ProbabilityPercent;
        record.ExpectedValuePercent = target.ExpectedValuePercent;
        record.NetPotentialProfitPercent = target.NetPotentialProfitPercent;
        record.RiskReward = target.RiskReward;
        record.LastFinalRejectionReason = "";
        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistoryEnd(WatchCandidate watch, string status, string reason)
{
    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == watch.Id);
        if (record == null) return;

        record.Status = status;
        record.EndedAt = DateTime.UtcNow;
        record.EndReason = reason;
        record.MaxPullbackFraction = Math.Max(record.MaxPullbackFraction, watch.PullbackFraction);
        record.PullbackExtreme = watch.PullbackExtreme;
        SaveWatchHistoryUnsafe();
    }
}

void UpdateWatchHistoryTradeResult(string sourceWatchId, string result, decimal netPnlPercent)
{
    if (string.IsNullOrWhiteSpace(sourceWatchId)) return;

    lock (watchHistoryLock)
    {
        WatchHistoryRecord? record = watchHistory.LastOrDefault(x => x.Id == sourceWatchId);
        if (record == null) return;

        record.Status = "TRADE_CLOSED";
        record.TradeResult = result;
        record.TradeNetPnlPercent = netPnlPercent;
        record.TradeClosedAt = DateTime.UtcNow;
        SaveWatchHistoryUnsafe();
    }
}

int ResetWatchHistory()
{
    lock (watchHistoryLock)
    {
        int removed = watchHistory.Count;
        watchHistory.Clear();
        try { SaveWatchHistoryUnsafe(); }
        catch (Exception ex) { Console.WriteLine($"Ошибка очистки WATCH-истории: {ex.Message}"); }
        return removed;
    }
}

string GetWatchStatsText()
{
    List<WatchHistoryRecord> snapshot;
    lock (watchHistoryLock) snapshot = watchHistory.ToList();

    if (snapshot.Count == 0)
        return "👀 WATCH-СТАТИСТИКА\\n\\nWATCH-записей пока нет.\\n\\n" + $"Файл: {WatchHistoryFile}";

    int pullbacks = snapshot.Count(x => x.PullbackSeen);
    int reaccels = snapshot.Count(x => x.ReaccelerationSeen);
    int signals = snapshot.Count(x => x.Status == "SIGNAL_CREATED" || x.Status == "TRADE_CLOSED");
    int invalidated = snapshot.Count(x => x.Status == "INVALIDATED");
    int expired = snapshot.Count(x => x.Status == "EXPIRED");
    int closedTrades = snapshot.Count(x => x.Status == "TRADE_CLOSED");
    decimal avgRange = snapshot.Average(x => x.ImpulseRangeUsd);
    decimal avgPullback = snapshot.Average(x => x.MaxPullbackFraction) * 100m;

    return
        "👀 WATCH-СТАТИСТИКА\\n\\n" +
        $"Всего WATCH: {snapshot.Count}\\n" +
        $"Нашли pullback: {pullbacks}\\n" +
        $"Нашли re-acceleration: {reaccels}\\n" +
        $"Перешли в сигнал: {signals}\\n" +
        $"Закрытых сделок из WATCH: {closedTrades}\\n\\n" +
        $"Invalidated: {invalidated}\\n" +
        $"Expired: {expired}\\n\\n" +
        $"Средний диапазон импульса: ${avgRange:F2}\\n" +
        $"Средний max pullback: {avgPullback:F0}%\\n\\n" +
        $"Файл: {WatchHistoryFile}";
}

// =====================================================
//                    TELEGRAM TEXT
// =====================================================

string GetTradeStatusText(ActiveTrade trade)
{
    if (trade.Status == TradeStatus.WaitingForEntry)
    {
        return
            $"⏳ Сигнал {trade.Direction} ждёт входа\n\n" +
            $"Зона: ${trade.ZoneLow:F2} — ${trade.ZoneHigh:F2}\n" +
            $"Расчётный вход: ${trade.SuggestedEntry:F2}\n" +
            $"Стоп: ${trade.InitialStopLoss:F2}\n" +
            $"Тейк: ${trade.TakeProfit:F2}\n\n" +
            $"Качество всплеска: {trade.SpikeQuality:F0}/100\n" +
            $"Качество входа: {trade.EntryQuality:F0}/100\n" +
            $"Оценка TP: {trade.EstimatedProbability:F1}%\n" +
            $"История похожих: {trade.HistoricalSuccessEstimate:F1}% ({trade.HistoricalSuccessSamples})\n" +
            $"Статус зоны: {(trade.EntryZoneTouched ? "касание было, жду подтверждение" : "жду касание")}\n" +
            $"EV: {trade.ExpectedValuePercent:+0.000;-0.000}%";
    }

    return
        $"🟢 PAPER-СДЕЛКА ОТКРЫТА | {trade.Direction}\n\n" +
        $"Вход: ${trade.ActualEntry:F2}\n" +
        $"Начальный стоп: ${trade.InitialStopLoss:F2}\n" +
        $"Текущий защитный стоп: ${trade.CurrentStopLoss:F2}\n" +
        $"Тейк: ${trade.TakeProfit:F2}\n" +
        $"R/R: 1:{trade.RiskReward:F2}\n\n" +
        $"Оценка TP: {trade.EstimatedProbability:F1}%\n" +
        $"Потенциальная чистая прибыль: +{trade.NetPotentialProfitPercent:F3}%\n" +
        $"Тех. совпадение: {trade.TechnicalConfluence:F0}/100\n" +
        $"Структура 5м/15м: {trade.Structure5mBias} / {trade.Structure15mBias}\n" +
        $"MFE сейчас: +{trade.MaxFavorableExcursionPercent:F3}%\n" +
        $"MAE сейчас: -{trade.MaxAdverseExcursionPercent:F3}%";
}

string GetMarketText()
{
    FlowMetrics f5 = GetFlowMetrics(5);
    FlowMetrics f15 = GetFlowMetrics(15);
    OrderBookMetrics book = orderBook.GetMetrics();
    TickerSnapshot t = ticker.GetSnapshot();
    LiquidationMetrics liq = GetLiquidationMetrics(30);
    decimal oi = ticker.GetOpenInterestChangePercent(30);
    decimal price = f5.Valid ? f5.LastPrice : t.LastPrice;

    decimal longLocation = CalculateTechnicalConfluence("LONG", price);
    decimal shortLocation = CalculateTechnicalConfluence("SHORT", price);

    return
        $"📊 {Symbol} | текущая картина\\n\\n" +
        $"Цена: ${price:F2}\\n\\n" +
        $"📐 ТЕХНИЧЕСКАЯ СТРУКТУРА\\n" +
        $"5м: {FormatStructure(market.Structure5m)}\\n" +
        $"15м: {FormatStructure(market.Structure15m)}\\n" +
        $"VWAP {VolumeProfileMinutes}м: ${market.RollingVwap:F2}\\n" +
        $"VAL / POC / VAH: ${market.Val:F2} / ${market.Poc:F2} / ${market.Vah:F2}\\n" +
        $"Поддержка: ${market.NearestSupport:F2}\\n" +
        $"Сопротивление: ${market.NearestResistance:F2}\\n" +
        $"Качество локации LONG/SHORT: {longLocation:F0}/100 / {shortLocation:F0}/100\\n\\n" +
        $"📊 ORDER FLOW\\n" +
        $"Объём 5с: x{f5.VolumeRatio:F2}\\n" +
        $"Объём 15с: x{f15.VolumeRatio:F2}\\n" +
        $"Buy 15с: {f15.BuyShare:P1}\\n" +
        $"Sell 15с: {f15.SellShare:P1}\\n" +
        $"CVD 15с: {f15.CvdNormalized:+0.00;-0.00}\\n\\n" +
        $"Стакан bid/ask: {book.Imbalance:P0} / {(1m - book.Imbalance):P0}\\n" +
        $"Стойкий bid/ask 5с: {book.PersistentImbalance5s:P0} / {(1m - book.PersistentImbalance5s):P0}\\n" +
        $"OFI 5с: {book.Ofi5sNormalized:+0.000;-0.000}\\n" +
        $"Spread: {book.SpreadBps:F2} bps\\n" +
        $"Microprice: ${book.MicroPrice:F2}\\n\\n" +
        $"OI 30с: {oi:+0.00;-0.00}%\\n" +
        $"Funding: {t.FundingRate * 100m:+0.0000;-0.0000}%\\n" +
        $"Mark/Index: {t.MarkIndexDivergenceBps:+0.00;-0.00} bps\\n\\n" +
        $"Тренд 5м: {market.Trend5mPercent:+0.00;-0.00}%\\n" +
        $"Тренд 15м: {market.Trend15mPercent:+0.00;-0.00}%\\n" +
        $"EMA9/21/50: {market.Ema9:F2} / {market.Ema21:F2} / {market.Ema50:F2}\\n" +
        $"ATR(14): ${market.Atr:F2} ({market.AtrPercent:F3}%)\\n\\n" +
        $"Ликвидации LONG 30с: ${liq.LongLiquidations:N0}\\n" +
        $"Ликвидации SHORT 30с: ${liq.ShortLiquidations:N0}";
}

string GetWatchStatusText(WatchCandidate watch)
{
    decimal range = Math.Max(watch.Impulse.High - watch.Impulse.Low, 0.01m);
    string stage = !watch.PullbackSeen
        ? "Жду откат"
        : !watch.ReaccelerationSeen
            ? "Откат найден — жду повторное ускорение"
            : "Повторное ускорение найдено — проверяю финальные фильтры";

    return
        $"👀 WATCH {watch.Direction} | {Symbol}\n\n" +
        $"Стадия: {stage}\n" +
        $"Возраст: {(DateTime.UtcNow - watch.CreatedAt).TotalSeconds:F0}с / {WatchLifetimeSeconds}с\n\n" +
        $"Импульс: {watch.Impulse.WindowSeconds}с\n" +
        $"Диапазон: ${range:F2}\n" +
        $"Объём: x{watch.Impulse.VolumeRatio:F2}\n" +
        $"CVD: {watch.Impulse.CvdNormalized:+0.00;-0.00}\n" +
        $"Макс. откат: {watch.PullbackFraction:P0}\n" +
        $"Экстремум отката: ${watch.PullbackExtreme:F2}\n" +
        $"База объёма на откате 5/15с: x{watch.PullbackBaseF5VolumeRatio:F2} / x{watch.PullbackBaseF15VolumeRatio:F2}\n\n" +
        "WATCH — это ещё не торговый сигнал. Бот ждёт re-acceleration 5/15с.";
}

string GetDiagnosticsText()
{
    SignalDiagnostics d;
    lock (diagnosticsLock)
    {
        d = diagnostics.Clone();
    }

    string watchText = watchCandidate == null
        ? "нет"
        : $"{watchCandidate.Direction}, pullback={watchCandidate.PullbackFraction:P0}, " +
          $"reaccel={(watchCandidate.ReaccelerationSeen ? "да" : "нет")}, " +
          $"возраст={(DateTime.UtcNow - watchCandidate.CreatedAt).TotalSeconds:F0}с";

    string age = d.LastReasonAt == DateTime.MinValue
        ? "—"
        : $"{Math.Max(0, (DateTime.UtcNow - d.LastReasonAt).TotalSeconds):F0}с назад";

    return
        "🔎 ДИАГНОСТИКА СИГНАЛОВ\n\n" +
        $"Проверок: {d.Checks}\n" +
        $"Направленных кандидатов: {d.DirectedCandidates}\n" +
        $"Создано WATCH: {d.WatchesCreated}\n" +
        $"Найдено pullback: {d.PullbacksSeen}\n" +
        $"Прошли re-acceleration: {d.ReaccelerationsPassed}\n" +
        $"Создано сигналов: {d.SignalsCreated}\n\n" +
        $"Текущий WATCH: {watchText}\n\n" +
        $"Последнее состояние ({age}):\n{d.LastReason}\n\n" +
        $"Цена: ${d.Price:F2}\n" +
        $"Объём 5/15/30/60с: x{d.Volume5:F2} / x{d.Volume15:F2} / x{d.Volume30:F2} / x{d.Volume60:F2}\n" +
        $"BUY/SELL 15с: {d.BuyShare15:P0} / {d.SellShare15:P0}\n" +
        $"CVD 5/15с: {d.Cvd5:+0.00;-0.00} / {d.Cvd15:+0.00;-0.00}\n" +
        $"Изменение цены 15с: {d.PriceMove15:+0.000;-0.000}%\n" +
        $"Стакан bid: {d.BookImbalance:P0}, persistent: {d.PersistentBookImbalance:P0}\n" +
        $"ATR: ${market.Atr:F2} ({market.AtrPercent:F3}%)\n\n" +
        "Режим: IMPULSE → PULLBACK → RE-ACCELERATION → ENTRY.";
}

void UpdateDiagnosticsSnapshot(
    FlowMetrics f5,
    FlowMetrics f15,
    FlowMetrics f30,
    FlowMetrics f60,
    OrderBookMetrics book,
    string reason)
{
    lock (diagnosticsLock)
    {
        diagnostics.Checks++;
        diagnostics.Price = f5.LastPrice > 0 ? f5.LastPrice : f15.LastPrice;
        diagnostics.Volume5 = f5.VolumeRatio;
        diagnostics.Volume15 = f15.VolumeRatio;
        diagnostics.Volume30 = f30.VolumeRatio;
        diagnostics.Volume60 = f60.Valid ? f60.VolumeRatio : 0m;
        diagnostics.BuyShare15 = f15.BuyShare;
        diagnostics.SellShare15 = f15.SellShare;
        diagnostics.Cvd5 = f5.CvdNormalized;
        diagnostics.Cvd15 = f15.CvdNormalized;
        diagnostics.PriceMove15 = f15.PriceChangePercent;
        diagnostics.BookImbalance = book.Imbalance;
        diagnostics.PersistentBookImbalance = book.PersistentImbalance5s;
        diagnostics.LastReason = reason;
        diagnostics.LastReasonAt = DateTime.UtcNow;
    }
}

void SetDiagnosticReason(string reason)
{
    lock (diagnosticsLock)
    {
        diagnostics.LastReason = reason;
        diagnostics.LastReasonAt = DateTime.UtcNow;
    }
}

void IncrementDiagnosticDirectedCandidate()
{
    lock (diagnosticsLock)
    {
        diagnostics.DirectedCandidates++;
    }
}

string GetStatsText()
{
    List<TradeRecord> snapshot;

    lock (historyLock)
        snapshot = tradeHistory.ToList();

    if (snapshot.Count == 0)
        return "📊 Закрытых paper-сделок пока нет.";

    int tp = snapshot.Count(x => x.Result == "TP");
    int sl = snapshot.Count(x => x.Result == "SL");
    int be = snapshot.Count(x => x.Result == "BE");
    int trail = snapshot.Count(x => x.Result == "TRAIL");
    int reversal = snapshot.Count(x => x.Result == "REV");
    int stale = snapshot.Count(x => x.Result == "STALE");
    int time = snapshot.Count(x => x.Result == "TIME");

    int profitable = snapshot.Count(x => x.NetPnlPercent > 0m);
    int losing = snapshot.Count(x => x.NetPnlPercent < 0m);
    int flat = snapshot.Count - profitable - losing;

    decimal successRate = snapshot.Count > 0
        ? profitable * 100m / snapshot.Count
        : 0m;

    decimal tpRate = snapshot.Count > 0
        ? tp * 100m / snapshot.Count
        : 0m;

    decimal net = snapshot.Sum(x => x.NetPnlPercent);
    decimal avg = snapshot.Average(x => x.NetPnlPercent);
    decimal avgMfe = snapshot.Average(x => x.MaxFavorableExcursionPercent);
    decimal avgMae = snapshot.Average(x => x.MaxAdverseExcursionPercent);

    decimal winsSum = snapshot.Where(x => x.NetPnlPercent > 0).Sum(x => x.NetPnlPercent);
    decimal lossesSum = Math.Abs(snapshot.Where(x => x.NetPnlPercent < 0).Sum(x => x.NetPnlPercent));
    decimal profitFactor = lossesSum > 0 ? winsSum / lossesSum : winsSum > 0 ? 99m : 0m;

    var recent = snapshot.OrderByDescending(x => x.CloseTime).Take(30).ToList();
    decimal recentSuccess = recent.Count > 0
        ? recent.Count(x => x.NetPnlPercent > 0m) * 100m / recent.Count
        : 0m;

    return
        "📊 СТАТИСТИКА PRECISION PAPER-СТРАТЕГИИ\n\n" +
        $"Всего закрыто: {snapshot.Count}\n" +
        $"✅ Положительных: {profitable}\n" +
        $"❌ Отрицательных: {losing}\n" +
        $"➖ Около нуля: {flat}\n\n" +
        $"🎯 Успешных по чистому PnL: {successRate:F1}%\n" +
        $"🏆 TP достигнут: {tpRate:F1}%\n" +
        $"Последние {recent.Count}: {recentSuccess:F1}% положительных\n\n" +
        $"Причины выхода:\n" +
        $"TP {tp} | SL {sl} | BE {be} | TRAIL {trail}\n" +
        $"REV {reversal} | STALE {stale} | TIME {time}\n\n" +
        $"Средний чистый PnL: {avg:+0.000;-0.000}%\n" +
        $"Суммарный PnL: {net:+0.000;-0.000}%\n" +
        $"Profit Factor: {profitFactor:F2}\n\n" +
        $"Средний MFE: +{avgMfe:F3}%\n" +
        $"Средний MAE: -{avgMae:F3}%";
}

// =====================================================
//                       HELPERS
// =====================================================

// -----------------------------------------------------
// TELEGRAM: HTTP CLIENT / ДИАГНОСТИКА / RECONNECT
// -----------------------------------------------------

long? LoadChatId()
{
    try
    {
        if (!File.Exists(ChatIdFile))
            return null;

        string text = File.ReadAllText(ChatIdFile).Trim();

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            return value;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Не удалось прочитать сохранённый Telegram Chat ID: {ex.Message}");
    }

    return null;
}

void SaveChatId(long value)
{
    try
    {
        File.WriteAllText(
            ChatIdFile,
            value.ToString(CultureInfo.InvariantCulture)
        );
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Не удалось сохранить Telegram Chat ID: {ex.Message}");
    }
}

HttpClient CreateTelegramHttpClient()
{
    var handler = new SocketsHttpHandler
    {
        // После смены VPN/сети старые соединения быстро выбрасываются из пула.
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        ConnectTimeout = TimeSpan.FromSeconds(10),

        AutomaticDecompression =
            DecompressionMethods.GZip |
            DecompressionMethods.Deflate,

        // Не держим бесконечное число соединений.
        MaxConnectionsPerServer = 20
    };

    return new HttpClient(handler, disposeHandler: true)
    {
        // Long polling Telegram специально может держать HTTP-запрос открытым
        // десятки секунд. 2 минуты не позволяют HttpClient обрывать нормальный
        // polling на границе короткого 50-секундного таймаута.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };
}

void MarkTelegramHealthy()
{
    lock (telegramStateLock)
    {
        telegramConsecutiveFailures = 0;
        telegramReconnectRequested = false;
        lastTelegramHealthyAt = DateTime.UtcNow;
    }
}

int MarkTelegramFailure(bool forceReconnect = false)
{
    lock (telegramStateLock)
    {
        telegramConsecutiveFailures++;
        lastTelegramErrorAt = DateTime.UtcNow;

        // Один случайный timeout long polling — ещё не повод полностью
        // пересоздавать Telegram-клиент. Реконнект делаем только после
        // нескольких последовательных ошибок либо при явном forceReconnect.
        if (forceReconnect || telegramConsecutiveFailures >= TelegramFailuresBeforeReconnect)
        {
            telegramReconnectRequested = true;
        }

        return telegramConsecutiveFailures;
    }
}

bool TelegramNeedsReconnect()
{
    lock (telegramStateLock)
    {
        return telegramReconnectRequested ||
               telegramConsecutiveFailures >= TelegramFailuresBeforeReconnect;
    }
}

async Task OnTelegramError(
    Exception exception,
    Telegram.Bot.Polling.HandleErrorSource source)
{
    int failures = MarkTelegramFailure();

    Console.WriteLine();
    Console.WriteLine("❌ TELEGRAM POLLING ERROR");
    Console.WriteLine($"Источник: {source}");
    Console.WriteLine($"Тип: {exception.GetType().Name}");
    Console.WriteLine($"Ошибка: {exception.Message}");

    if (exception.InnerException != null)
    {
        Console.WriteLine(
            $"Внутренняя ошибка: {exception.InnerException.GetType().Name}: " +
            exception.InnerException.Message
        );
    }

    if (TelegramNeedsReconnect())
    {
        Console.WriteLine(
            $"Ошибок подряд: {failures}/{TelegramFailuresBeforeReconnect}. " +
            "Пересоздаю Telegram-клиент..."
        );

        // Даём текущему polling-циклу выйти из обработчика ошибки,
        // после чего полностью создаём новые HTTP/DNS/TCP соединения.
        await Task.Delay(750);
        await RecreateTelegramClientAsync("несколько ошибок long polling подряд");
    }
    else
    {
        Console.WriteLine(
            $"Ошибок подряд: {failures}/{TelegramFailuresBeforeReconnect}. " +
            "Полный реконнект пока не нужен — polling продолжит работу автоматически."
        );
    }

    Console.WriteLine();
}

async Task TelegramWatchdog()
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));

            try
            {
                // Короткая проверка не ждёт общего HttpClient.Timeout.
                using var pingTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(8));

                TelegramBotClient currentBot = bot;
                await currentBot.GetMe(pingTimeout.Token);

                MarkTelegramHealthy();
            }
            catch (Exception ex)
            {
                int failures = MarkTelegramFailure();

                Console.WriteLine(
                    $"⚠️ Telegram watchdog: {ex.GetType().Name}: {ex.Message} " +
                    $"(ошибок подряд {failures}/{TelegramFailuresBeforeReconnect})"
                );
            }

            if (TelegramNeedsReconnect())
            {
                await RecreateTelegramClientAsync(
                    $"{TelegramFailuresBeforeReconnect} последовательных ошибок связи"
                );
            }
        }
        catch (Exception ex)
        {
            // Watchdog не должен умирать из-за собственной ошибки.
            Console.WriteLine($"⚠️ Telegram watchdog internal: {ex.Message}");
            await Task.Delay(3000);
        }
    }
}

async Task RecreateTelegramClientAsync(string reason)
{
    // Если другой поток уже восстанавливает Telegram, второй не нужен.
    if (!await telegramReconnectGate.WaitAsync(0))
        return;

    try
    {
        if (!TelegramNeedsReconnect())
            return;

        Console.WriteLine($"🔄 Пересоздаю Telegram-клиент: {reason}");

        TelegramBotClient oldBot = bot;
        HttpClient oldHttp = telegramHttp;
        CancellationTokenSource oldCts = telegramBotCts;

        // Останавливаем старый event-polling.
        try
        {
            oldBot.OnMessage -= OnTelegramMessage;
            oldBot.OnError -= OnTelegramError;
        }
        catch
        {
        }

        try
        {
            oldCts.Cancel();
        }
        catch
        {
        }

        // Полностью новый handler => новые DNS/TCP/TLS соединения и новый маршрут.
        var newCts = new CancellationTokenSource();
        var newHttp = CreateTelegramHttpClient();
        var newBot = new TelegramBotClient(telegramToken, newHttp, newCts.Token);

        bot = newBot;
        telegramHttp = newHttp;
        telegramBotCts = newCts;

        newBot.OnError += OnTelegramError;
        newBot.OnMessage += OnTelegramMessage;

        // Старый HttpClient больше не нужен после отмены старого polling.
        try
        {
            oldHttp.Dispose();
            oldCts.Dispose();
        }
        catch
        {
        }

        try
        {
            using var reconnectTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var me = await newBot.GetMe(reconnectTimeout.Token);
            MarkTelegramHealthy();

            Console.WriteLine(
                $"✅ Telegram переподключён: @{me.Username}"
            );
        }
        catch (Exception ex)
        {
            // Если прямого маршрута к Telegram сейчас вообще нет,
            // watchdog продолжит повторять попытки.
            MarkTelegramFailure(forceReconnect: true);

            Console.WriteLine(
                $"❌ Telegram пока недоступен после переподключения: " +
                $"{ex.GetType().Name}: {ex.Message}"
            );
        }
    }
    finally
    {
        telegramReconnectGate.Release();
    }
}

async Task SendTelegramToChat(long targetChatId, string text)
{
    try
    {
        TelegramBotClient currentBot = bot;
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await currentBot.SendMessage(targetChatId, text, cancellationToken: sendTimeout.Token);
        MarkTelegramHealthy();
    }
    catch (Exception ex)
    {
        int failures = MarkTelegramFailure();

        Console.WriteLine();
        Console.WriteLine("❌ TELEGRAM SEND ERROR");
        Console.WriteLine($"Chat ID: {targetChatId}");
        Console.WriteLine($"Тип: {ex.GetType().Name}");
        Console.WriteLine($"Ошибка: {ex.Message}");

        if (ex.InnerException != null)
        {
            Console.WriteLine(
                $"Внутренняя ошибка: {ex.InnerException.GetType().Name}: " +
                ex.InnerException.Message
            );
        }

        Console.WriteLine(
            $"Ошибок подряд: {failures}/{TelegramFailuresBeforeReconnect}"
        );

        // Один сетевой сбой не должен приводить к полной пересборке клиента.
        // После трёх последовательных ошибок создаём новые HTTP/DNS/TCP соединения.
        if (TelegramNeedsReconnect())
        {
            await RecreateTelegramClientAsync("несколько ошибок Telegram подряд");
        }

        throw;
    }
}

async Task SendTelegram(string text)
{
    if (chatId == null)
        return;

    try
    {
        await SendTelegramToChat(chatId.Value, text);
    }
    catch
    {
        // Ошибка уже записана и переподключение уже запрошено внутри SendTelegramToChat.
    }
}

async Task Heartbeat(ClientWebSocket socket, CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);

            if (socket.State != WebSocketState.Open)
                return;

            await SendWebSocketText(
                socket,
                """
                {"op":"ping"}
                """,
                token
            );
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Heartbeat: {ex.Message}");
    }
}

async Task SendWebSocketText(
    ClientWebSocket socket,
    string text,
    CancellationToken token)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        token
    );
}

async Task<string> ReceiveText(ClientWebSocket socket)
{
    byte[] buffer = new byte[64 * 1024];
    using MemoryStream stream = new MemoryStream();
    WebSocketReceiveResult result;

    do
    {
        result = await socket.ReceiveAsync(
            new ArraySegment<byte>(buffer),
            CancellationToken.None
        );

        if (result.MessageType == WebSocketMessageType.Close)
            throw new Exception("Bybit закрыл WebSocket");

        stream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return Encoding.UTF8.GetString(stream.ToArray());
}

decimal ParseStringDecimal(JsonElement element)
{
    return decimal.Parse(
        element.GetString() ?? "0",
        CultureInfo.InvariantCulture
    );
}

decimal Ema(List<decimal> values, int period)
{
    if (values.Count == 0)
        return 0m;

    decimal alpha = 2m / (period + 1m);
    decimal ema = values[0];

    for (int i = 1; i < values.Count; i++)
        ema = values[i] * alpha + ema * (1m - alpha);

    return ema;
}

decimal Median(List<decimal> values)
{
    if (values.Count == 0)
        return 0;

    var sorted = values.OrderBy(x => x).ToList();
    int middle = sorted.Count / 2;

    return sorted.Count % 2 == 1
        ? sorted[middle]
        : (sorted[middle - 1] + sorted[middle]) / 2m;
}

decimal RobustZScore(decimal value, decimal median, decimal mad)
{
    decimal sigma = mad * 1.4826m;
    if (sigma <= 0)
        return 0;

    return (value - median) / sigma;
}

decimal Score(decimal value, decimal weak, decimal strong)
{
    if (value <= weak)
        return 0m;

    if (value >= strong)
        return 100m;

    return (value - weak) / (strong - weak) * 100m;
}

decimal InverseScore(decimal value, decimal good, decimal bad)
{
    if (value <= good)
        return 100m;

    if (value >= bad)
        return 0m;

    return (bad - value) / (bad - good) * 100m;
}

// =====================================================
//                         TYPES
// =====================================================

enum TradeStatus
{
    WaitingForEntry,
    Open
}

class TradeTick
{
    public long TimeMs { get; set; }
    public DateTime Time { get; set; }
    public decimal Price { get; set; }
    public decimal Size { get; set; }
    public string Side { get; set; } = "";
    public decimal Turnover { get; set; }
}

class FlowMetrics
{
    public bool Valid { get; set; }
    public int WindowSeconds { get; set; }
    public decimal FirstPrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Vwap { get; set; }
    public decimal TotalTurnover { get; set; }
    public decimal TotalVolume { get; set; }
    public decimal BuyTurnover { get; set; }
    public decimal SellTurnover { get; set; }
    public decimal BuyShare { get; set; }
    public decimal SellShare { get; set; }
    public decimal Cvd { get; set; }
    public decimal CvdNormalized { get; set; }
    public decimal PriceChangePercent { get; set; }
    public decimal VolumeRatio { get; set; }
    public decimal TradesPerSecond { get; set; }
    public decimal Efficiency { get; set; }
    public decimal CloseLocation { get; set; }
    public decimal LargeBuyTurnover { get; set; }
    public decimal LargeSellTurnover { get; set; }
    public decimal LargeBuyShare { get; set; }
    public decimal LargeSellShare { get; set; }
    public decimal LargeTurnoverShare { get; set; }
}

class LiquidationTick
{
    public DateTime Time { get; set; }
    public string PositionSide { get; set; } = "";
    public decimal Turnover { get; set; }
}

class LiquidationMetrics
{
    public decimal LongLiquidations { get; set; }
    public decimal ShortLiquidations { get; set; }
}

class Candle
{
    public long StartMs { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal Turnover { get; set; }
}

class PivotPoint
{
    public int Index { get; set; }
    public decimal Price { get; set; }
}

class StructureAnalysis
{
    public bool Valid { get; set; }
    public string Bias { get; set; } = "ДИАПАЗОН";
    public string Event { get; set; } = "НЕТ";
    public decimal LastSwingHigh { get; set; }
    public decimal LastSwingLow { get; set; }
    public bool HigherHigh { get; set; }
    public bool HigherLow { get; set; }
    public bool LowerHigh { get; set; }
    public bool LowerLow { get; set; }
}

class VolumeProfile
{
    public bool Valid { get; set; }
    public decimal Low { get; set; }
    public decimal High { get; set; }
    public decimal Poc { get; set; }
    public decimal Vah { get; set; }
    public decimal Val { get; set; }
}

class TechnicalLevels
{
    public decimal Support { get; set; }
    public decimal Resistance { get; set; }
}

class MarketContext
{
    public decimal MeanMinuteTurnover { get; set; }
    public decimal MedianMinuteTurnover { get; set; }
    public decimal MadMinuteTurnover { get; set; }
    public decimal RobustMinuteTurnover { get; set; }
    public decimal FastMinuteTurnover { get; set; }
    public decimal EffectiveMinuteTurnover { get; set; }
    public decimal Atr { get; set; }
    public decimal AtrPercent { get; set; }
    public decimal Trend5mPercent { get; set; }
    public decimal Trend15mPercent { get; set; }
    public decimal Ema9 { get; set; }
    public decimal Ema21 { get; set; }
    public decimal Ema50 { get; set; }
    public decimal EmaSpreadAtr { get; set; }
    public decimal LastClosedPrice { get; set; }
    public decimal RollingVwap { get; set; }
    public decimal Poc { get; set; }
    public decimal Vah { get; set; }
    public decimal Val { get; set; }
    public decimal ProfileLow { get; set; }
    public decimal ProfileHigh { get; set; }
    public decimal ValueAreaPosition { get; set; }
    public decimal NearestSupport { get; set; }
    public decimal NearestResistance { get; set; }
    public StructureAnalysis Structure5m { get; set; } = new StructureAnalysis();
    public StructureAnalysis Structure15m { get; set; } = new StructureAnalysis();
    public DateTime UpdatedAt { get; set; }
}

class EntryPlan
{
    public bool Valid { get; set; }
    public string RejectReason { get; set; } = "";
    public decimal ZoneLow { get; set; }
    public decimal ZoneHigh { get; set; }
    public decimal Entry { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TechnicalAnchor { get; set; }
    public decimal TechnicalConfluence { get; set; }
}

class TargetPlan
{
    public bool Valid { get; set; }
    public bool BootstrapMode { get; set; }
    public string RejectReason { get; set; } = "";
    public decimal NetRewardRisk { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal RiskReward { get; set; }
    public decimal ProbabilityPercent { get; set; }
    public string ProbabilitySource { get; set; } = "";
    public int Samples { get; set; }
    public decimal GrossProfitPercent { get; set; }
    public decimal RiskPercent { get; set; }
    public decimal EstimatedCostsPercent { get; set; }
    public decimal NetPotentialProfitPercent { get; set; }
    public decimal ExpectedValuePercent { get; set; }
}

class ProbabilityEstimate
{
    public decimal ProbabilityPercent { get; set; }
    public string Source { get; set; } = "";
    public int Samples { get; set; }
}

class HistoricalGate
{
    public bool Allow { get; set; }
    public decimal ProbabilityPercent { get; set; }
    public int Samples { get; set; }
}

class AdaptiveThresholds
{
    public decimal ScoreBoost { get; set; }
    public decimal ProbabilityBoost { get; set; }
    public decimal EvBoost { get; set; }
}

class WatchCandidate
{
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public FlowMetrics Impulse { get; set; } = new FlowMetrics();
    public decimal VolumeZ { get; set; }
    public decimal InitialPrice { get; set; }
    public decimal HighestPrice { get; set; }
    public decimal LowestPrice { get; set; }
    public decimal PullbackExtreme { get; set; }
    public decimal PullbackFraction { get; set; }
    public bool PullbackSeen { get; set; }
    public DateTime? PullbackSeenAt { get; set; }
    public decimal LastF5VolumeRatio { get; set; }
    public decimal LastF15VolumeRatio { get; set; }
    public decimal PullbackBaseF5VolumeRatio { get; set; }
    public decimal PullbackBaseF15VolumeRatio { get; set; }
    public decimal InitialBookImbalance { get; set; }
    public decimal InitialOiChange30 { get; set; }
    public bool ReaccelerationSeen { get; set; }
    public DateTime? ReaccelerationSeenAt { get; set; }
}

class SignalDiagnostics
{
    public long Checks { get; set; }
    public long DirectedCandidates { get; set; }
    public long WatchesCreated { get; set; }
    public long PullbacksSeen { get; set; }
    public long ReaccelerationsPassed { get; set; }
    public long WatchesEnded { get; set; }
    public long SignalsCreated { get; set; }
    public string LastReason { get; set; } = "Ещё нет данных.";
    public DateTime LastReasonAt { get; set; }
    public decimal Price { get; set; }
    public decimal Volume5 { get; set; }
    public decimal Volume15 { get; set; }
    public decimal Volume30 { get; set; }
    public decimal Volume60 { get; set; }
    public decimal BuyShare15 { get; set; }
    public decimal SellShare15 { get; set; }
    public decimal Cvd5 { get; set; }
    public decimal Cvd15 { get; set; }
    public decimal PriceMove15 { get; set; }
    public decimal BookImbalance { get; set; }
    public decimal PersistentBookImbalance { get; set; }

    public SignalDiagnostics Clone()
    {
        return (SignalDiagnostics)MemberwiseClone();
    }
}

class ActiveTrade
{
    public string SourceWatchId { get; set; } = "";
    public string Direction { get; set; } = "";
    public TradeStatus Status { get; set; }
    public DateTime SignalTime { get; set; }
    public DateTime? OpenTime { get; set; }

    public decimal ZoneLow { get; set; }
    public decimal ZoneHigh { get; set; }
    public decimal SuggestedEntry { get; set; }
    public decimal ActualEntry { get; set; }
    public decimal InitialStopLoss { get; set; }
    public decimal CurrentStopLoss { get; set; }
    public decimal InitialRiskDistance { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal RiskReward { get; set; }

    public bool EntryZoneTouched { get; set; }
    public DateTime? EntryZoneTouchTime { get; set; }
    public decimal EntryZoneExtreme { get; set; }
    public bool BreakEvenActivated { get; set; }
    public bool ProfitLockActivated { get; set; }
    public decimal BestR { get; set; }
    public DateTime? ReversalStartedAt { get; set; }

    public decimal SpikeQuality { get; set; }
    public decimal EntryQuality { get; set; }
    public decimal ModelScore { get; set; }
    public decimal EstimatedProbability { get; set; }
    public string ProbabilitySource { get; set; } = "";
    public int ProbabilitySamples { get; set; }

    public decimal GrossProfitPercent { get; set; }
    public decimal RiskPercent { get; set; }
    public decimal EstimatedCostsPercent { get; set; }
    public decimal NetPotentialProfitPercent { get; set; }
    public decimal ExpectedValuePercent { get; set; }

    public decimal VolumeRatio { get; set; }
    public decimal VolumeZ { get; set; }
    public decimal BuyShare { get; set; }
    public decimal SellShare { get; set; }
    public decimal CvdNormalized { get; set; }
    public decimal OrderBookImbalance { get; set; }
    public decimal PersistentBookImbalance { get; set; }
    public decimal Ofi5s { get; set; }
    public decimal SpreadBps { get; set; }
    public decimal OiChange30 { get; set; }
    public decimal Trend5m { get; set; }
    public decimal Trend15m { get; set; }
    public decimal ImpulseEfficiency { get; set; }
    public int ImpulseWindowSeconds { get; set; }
    public decimal ImpulseRangeUsd { get; set; }
    public decimal ImpulseRangeAtr { get; set; }
    public decimal HistoricalSuccessEstimate { get; set; }
    public int HistoricalSuccessSamples { get; set; }

    public decimal TechnicalConfluence { get; set; }
    public decimal SignalVwap { get; set; }
    public decimal SignalPoc { get; set; }
    public decimal SignalVah { get; set; }
    public decimal SignalVal { get; set; }
    public decimal SignalSupport { get; set; }
    public decimal SignalResistance { get; set; }
    public string Structure5mBias { get; set; } = "";
    public string Structure5mEvent { get; set; } = "";
    public string Structure15mBias { get; set; } = "";
    public string Structure15mEvent { get; set; } = "";

    public decimal MaxFavorableExcursionPercent { get; set; }
    public decimal MaxAdverseExcursionPercent { get; set; }
}

class TradeRecord
{
    public DateTime CloseTime { get; set; }
    public string Direction { get; set; } = "";
    public string Result { get; set; } = "";

    public decimal ModelScore { get; set; }
    public decimal SpikeQuality { get; set; }
    public decimal EntryQuality { get; set; }
    public decimal RiskReward { get; set; }
    public decimal EstimatedProbability { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }

    public decimal GrossPnlPercent { get; set; }
    public decimal NetPnlPercent { get; set; }
    public decimal MaxFavorableExcursionPercent { get; set; }
    public decimal MaxAdverseExcursionPercent { get; set; }
    public decimal EstimatedCostsPercent { get; set; }

    public decimal VolumeRatio { get; set; }
    public decimal VolumeZ { get; set; }
    public decimal OrderBookImbalance { get; set; }
    public decimal PersistentBookImbalance { get; set; }
    public decimal OiChange30 { get; set; }
    public decimal CvdNormalized { get; set; }
    public decimal Ofi5s { get; set; }
    public decimal SpreadBps { get; set; }
    public decimal Trend5m { get; set; }
    public decimal Trend15m { get; set; }
    public decimal ImpulseEfficiency { get; set; }
    public int ImpulseWindowSeconds { get; set; }
    public decimal ImpulseRangeUsd { get; set; }
    public decimal ImpulseRangeAtr { get; set; }
    public decimal TechnicalConfluence { get; set; }
}

class WatchHistoryRecord
{
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PullbackSeenAt { get; set; }
    public DateTime? ReaccelerationSeenAt { get; set; }
    public DateTime? SignalCreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? TradeClosedAt { get; set; }
    public int ImpulseWindowSeconds { get; set; }
    public decimal InitialPrice { get; set; }
    public decimal ImpulseLow { get; set; }
    public decimal ImpulseHigh { get; set; }
    public decimal ImpulseRangeUsd { get; set; }
    public decimal ImpulseRangeAtr { get; set; }
    public decimal VolumeRatio { get; set; }
    public decimal VolumeZ { get; set; }
    public decimal BuyShare { get; set; }
    public decimal SellShare { get; set; }
    public decimal CvdNormalized { get; set; }
    public decimal PriceChangePercent { get; set; }
    public decimal ImpulseEfficiency { get; set; }
    public decimal InitialBookImbalance { get; set; }
    public decimal InitialPersistentBookImbalance { get; set; }
    public decimal InitialOfi5s { get; set; }
    public decimal InitialSpreadBps { get; set; }
    public decimal InitialOiChange30 { get; set; }
    public decimal Atr { get; set; }
    public decimal Trend5m { get; set; }
    public decimal Trend15m { get; set; }
    public decimal Vwap { get; set; }
    public decimal Poc { get; set; }
    public decimal Vah { get; set; }
    public decimal Val { get; set; }
    public decimal Support { get; set; }
    public decimal Resistance { get; set; }
    public string Structure5mBias { get; set; } = "";
    public string Structure5mEvent { get; set; } = "";
    public string Structure15mBias { get; set; } = "";
    public string Structure15mEvent { get; set; } = "";
    public bool PullbackSeen { get; set; }
    public decimal MaxPullbackFraction { get; set; }
    public decimal PullbackExtreme { get; set; }
    public decimal PullbackBaseF5VolumeRatio { get; set; }
    public decimal PullbackBaseF15VolumeRatio { get; set; }
    public bool ReaccelerationSeen { get; set; }
    public int ReaccelerationConfirmations { get; set; }
    public decimal ReaccelerationQuality { get; set; }
    public decimal ReaccelVolume5 { get; set; }
    public decimal ReaccelVolume15 { get; set; }
    public decimal ReaccelCvd5 { get; set; }
    public decimal ReaccelCvd15 { get; set; }
    public decimal ReaccelBuyShare5 { get; set; }
    public decimal ReaccelSellShare5 { get; set; }
    public decimal ReaccelBookImbalance { get; set; }
    public decimal ReaccelPersistentBookImbalance { get; set; }
    public decimal ReaccelOfi5s { get; set; }
    public string LastFinalRejectionReason { get; set; } = "";
    public string EndReason { get; set; } = "";
    public decimal SpikeQuality { get; set; }
    public decimal EntryQuality { get; set; }
    public decimal TechnicalConfluence { get; set; }
    public decimal ModelScore { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal EstimatedProbability { get; set; }
    public decimal ExpectedValuePercent { get; set; }
    public decimal NetPotentialProfitPercent { get; set; }
    public decimal RiskReward { get; set; }
    public string TradeResult { get; set; } = "";
    public decimal TradeNetPnlPercent { get; set; }
}

class TickerSnapshot
{
    public decimal LastPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal IndexPrice { get; set; }
    public decimal OpenInterest { get; set; }
    public decimal FundingRate { get; set; }

    public decimal MarkIndexDivergenceBps => IndexPrice > 0
        ? (MarkPrice - IndexPrice) / IndexPrice * 10000m
        : 0m;
}

class TickerPoint
{
    public DateTime Time { get; set; }
    public decimal OpenInterest { get; set; }
}

class TickerState
{
    private readonly object _lock = new object();
    private readonly Queue<TickerPoint> _oiHistory = new Queue<TickerPoint>();
    private readonly TickerSnapshot _snapshot = new TickerSnapshot();
    private DateTime _lastOiSample = DateTime.MinValue;

    public void Update(JsonElement data, long timestampMs)
    {
        lock (_lock)
        {
            TrySet(data, "lastPrice", x => _snapshot.LastPrice = x);
            TrySet(data, "markPrice", x => _snapshot.MarkPrice = x);
            TrySet(data, "indexPrice", x => _snapshot.IndexPrice = x);
            TrySet(data, "openInterest", x => _snapshot.OpenInterest = x);
            TrySet(data, "fundingRate", x => _snapshot.FundingRate = x);

            DateTime now = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;

            if (_snapshot.OpenInterest > 0 &&
                now - _lastOiSample >= TimeSpan.FromMilliseconds(500))
            {
                _oiHistory.Enqueue(new TickerPoint
                {
                    Time = now,
                    OpenInterest = _snapshot.OpenInterest
                });

                _lastOiSample = now;
            }

            DateTime cutoff = now.AddMinutes(-5);
            while (_oiHistory.Count > 0 && _oiHistory.Peek().Time < cutoff)
                _oiHistory.Dequeue();
        }
    }

    public TickerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TickerSnapshot
            {
                LastPrice = _snapshot.LastPrice,
                MarkPrice = _snapshot.MarkPrice,
                IndexPrice = _snapshot.IndexPrice,
                OpenInterest = _snapshot.OpenInterest,
                FundingRate = _snapshot.FundingRate
            };
        }
    }

    public decimal GetOpenInterestChangePercent(int seconds)
    {
        lock (_lock)
        {
            if (_snapshot.OpenInterest <= 0 || _oiHistory.Count == 0)
                return 0m;

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-seconds);
            TickerPoint? old = _oiHistory.FirstOrDefault(x => x.Time >= cutoff);

            if (old == null || old.OpenInterest <= 0)
                old = _oiHistory.Peek();

            return old.OpenInterest > 0
                ? (_snapshot.OpenInterest - old.OpenInterest) / old.OpenInterest * 100m
                : 0m;
        }
    }

    private static void TrySet(JsonElement data, string name, Action<decimal> setter)
    {
        if (!data.TryGetProperty(name, out JsonElement element))
            return;

        string? text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
            setter(value);
    }
}

class BookFlowSample
{
    public DateTime Time { get; set; }
    public decimal BidLiquidity { get; set; }
    public decimal AskLiquidity { get; set; }
    public decimal Imbalance { get; set; }
    public decimal OfiDelta { get; set; }
}

class OrderBookMetrics
{
    public bool Valid { get; set; }
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public decimal BestBidSize { get; set; }
    public decimal BestAskSize { get; set; }
    public decimal MidPrice { get; set; }
    public decimal SpreadPrice { get; set; }
    public decimal SpreadBps { get; set; }
    public decimal MicroPrice { get; set; }
    public decimal BidLiquidityUsd { get; set; }
    public decimal AskLiquidityUsd { get; set; }
    public decimal Imbalance { get; set; }
    public decimal PersistentImbalance5s { get; set; }
    public decimal Ofi5sNormalized { get; set; }
    public decimal BidDrain5s { get; set; }
    public decimal AskDrain5s { get; set; }
}

class OrderBookState
{
    private readonly object _lock = new object();
    private readonly Dictionary<decimal, decimal> _bids = new Dictionary<decimal, decimal>();
    private readonly Dictionary<decimal, decimal> _asks = new Dictionary<decimal, decimal>();
    private readonly Queue<BookFlowSample> _samples = new Queue<BookFlowSample>();

    private decimal _lastBidLiquidity10;
    private decimal _lastAskLiquidity10;
    private DateTime _lastSampleTime = DateTime.MinValue;

    public void Apply(string type, JsonElement data, long timestampMs)
    {
        lock (_lock)
        {
            bool isSnapshot = type == "snapshot";

            if (isSnapshot)
            {
                _bids.Clear();
                _asks.Clear();
                _samples.Clear();
                _lastBidLiquidity10 = 0m;
                _lastAskLiquidity10 = 0m;
                _lastSampleTime = DateTime.MinValue;
            }

            if (data.TryGetProperty("b", out JsonElement bids))
                ApplySide(_bids, bids);

            if (data.TryGetProperty("a", out JsonElement asks))
                ApplySide(_asks, asks);

            DateTime now = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;

            if (now - _lastSampleTime >= TimeSpan.FromMilliseconds(100))
            {
                decimal bid10 = GetTopLiquidityUnsafe(_bids, true, 10);
                decimal ask10 = GetTopLiquidityUnsafe(_asks, false, 10);

                decimal ofiDelta = (isSnapshot || _lastBidLiquidity10 == 0m || _lastAskLiquidity10 == 0m)
                    ? 0m
                    : (bid10 - _lastBidLiquidity10) - (ask10 - _lastAskLiquidity10);

                decimal total10 = bid10 + ask10;
                decimal imbalance10 = total10 > 0 ? bid10 / total10 : 0.5m;

                _samples.Enqueue(new BookFlowSample
                {
                    Time = now,
                    BidLiquidity = bid10,
                    AskLiquidity = ask10,
                    Imbalance = imbalance10,
                    OfiDelta = ofiDelta
                });

                _lastBidLiquidity10 = bid10;
                _lastAskLiquidity10 = ask10;
                _lastSampleTime = now;

                DateTime cutoff = now.AddSeconds(-6);
                while (_samples.Count > 0 && _samples.Peek().Time < cutoff)
                    _samples.Dequeue();
            }
        }
    }

    public OrderBookMetrics GetMetrics()
    {
        lock (_lock)
        {
            if (_bids.Count == 0 || _asks.Count == 0)
                return new OrderBookMetrics();

            var topBids = _bids.OrderByDescending(x => x.Key).Take(20).ToList();
            var topAsks = _asks.OrderBy(x => x.Key).Take(20).ToList();

            decimal bestBid = topBids[0].Key;
            decimal bestAsk = topAsks[0].Key;
            decimal bestBidSize = topBids[0].Value;
            decimal bestAskSize = topAsks[0].Value;
            decimal mid = (bestBid + bestAsk) / 2m;
            decimal spread = bestAsk - bestBid;

            decimal bidUsd = topBids.Sum(x => x.Key * x.Value);
            decimal askUsd = topAsks.Sum(x => x.Key * x.Value);

            // Ближние уровни важнее дальних: простой top-20 sum слишком легко
            // искажается крупной стенкой вдалеке от best bid/ask.
            decimal weightedBidUsd = topBids.Sum(x =>
            {
                decimal distanceBps = bestBid > 0 ? (bestBid - x.Key) / bestBid * 10000m : 0m;
                decimal weight = 1m / (1m + Math.Max(distanceBps, 0m) / 4m);
                return x.Key * x.Value * weight;
            });

            decimal weightedAskUsd = topAsks.Sum(x =>
            {
                decimal distanceBps = bestAsk > 0 ? (x.Key - bestAsk) / bestAsk * 10000m : 0m;
                decimal weight = 1m / (1m + Math.Max(distanceBps, 0m) / 4m);
                return x.Key * x.Value * weight;
            });

            decimal total = weightedBidUsd + weightedAskUsd;

            decimal micro = bestBidSize + bestAskSize > 0
                ? (bestAsk * bestBidSize + bestBid * bestAskSize) / (bestBidSize + bestAskSize)
                : mid;

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-5);
            var samples = _samples.Where(x => x.Time >= cutoff).ToList();
            decimal ofi = samples.Sum(x => x.OfiDelta);
            decimal ofiNorm = (bidUsd + askUsd) > 0 ? ofi / (bidUsd + askUsd) : 0;
            decimal persistentImbalance = samples.Count > 0
                ? samples.Average(x => x.Imbalance)
                : (total > 0 ? weightedBidUsd / total : 0.5m);

            decimal bidDrain = 0m;
            decimal askDrain = 0m;

            if (samples.Count > 0)
            {
                BookFlowSample old = samples[0];
                BookFlowSample current = samples[^1];

                if (old.BidLiquidity > 0)
                    bidDrain = (old.BidLiquidity - current.BidLiquidity) / old.BidLiquidity;

                if (old.AskLiquidity > 0)
                    askDrain = (old.AskLiquidity - current.AskLiquidity) / old.AskLiquidity;
            }

            return new OrderBookMetrics
            {
                Valid = true,
                BestBid = bestBid,
                BestAsk = bestAsk,
                BestBidSize = bestBidSize,
                BestAskSize = bestAskSize,
                MidPrice = mid,
                SpreadPrice = spread,
                SpreadBps = mid > 0 ? spread / mid * 10000m : 0,
                MicroPrice = micro,
                BidLiquidityUsd = bidUsd,
                AskLiquidityUsd = askUsd,
                Imbalance = total > 0 ? weightedBidUsd / total : 0.5m,
                PersistentImbalance5s = persistentImbalance,
                Ofi5sNormalized = ofiNorm,
                BidDrain5s = bidDrain,
                AskDrain5s = askDrain
            };
        }
    }

    public decimal FindStrongBidBelow(decimal price, decimal maxDistance)
    {
        lock (_lock)
        {
            var candidates = _bids
                .Where(x => x.Key < price && price - x.Key <= maxDistance)
                .Select(x => new { Price = x.Key, Usd = x.Key * x.Value })
                .OrderByDescending(x => x.Usd)
                .Take(10)
                .ToList();

            return candidates.Count > 0 ? candidates[0].Price : 0m;
        }
    }

    public decimal FindStrongAskAbove(decimal price, decimal maxDistance)
    {
        lock (_lock)
        {
            var candidates = _asks
                .Where(x => x.Key > price && x.Key - price <= maxDistance)
                .Select(x => new { Price = x.Key, Usd = x.Key * x.Value })
                .OrderByDescending(x => x.Usd)
                .Take(10)
                .ToList();

            return candidates.Count > 0 ? candidates[0].Price : 0m;
        }
    }

    public decimal EstimateFillPrice(string side, decimal quoteNotional, decimal fallbackPrice)
    {
        lock (_lock)
        {
            IEnumerable<KeyValuePair<decimal, decimal>> levels = side == "BUY"
                ? _asks.OrderBy(x => x.Key)
                : _bids.OrderByDescending(x => x.Key);

            decimal remainingQuote = quoteNotional;
            decimal acquiredBase = 0m;
            decimal spentQuote = 0m;

            foreach (var level in levels)
            {
                decimal levelQuote = level.Key * level.Value;
                decimal takeQuote = Math.Min(remainingQuote, levelQuote);

                if (takeQuote <= 0)
                    continue;

                decimal takeBase = takeQuote / level.Key;
                acquiredBase += takeBase;
                spentQuote += takeQuote;
                remainingQuote -= takeQuote;

                if (remainingQuote <= 0.0001m)
                    break;
            }

            if (acquiredBase <= 0 || remainingQuote > quoteNotional * 0.10m)
                return fallbackPrice;

            return spentQuote / acquiredBase;
        }
    }

    public decimal EstimateSlippagePercent(string side, decimal quoteNotional, decimal referencePrice)
    {
        if (referencePrice <= 0)
            return 0m;

        decimal fill = EstimateFillPrice(side, quoteNotional, referencePrice);
        return Math.Abs(fill - referencePrice) / referencePrice * 100m;
    }

    private static void ApplySide(Dictionary<decimal, decimal> side, JsonElement updates)
    {
        foreach (JsonElement level in updates.EnumerateArray())
        {
            decimal price = decimal.Parse(level[0].GetString() ?? "0", CultureInfo.InvariantCulture);
            decimal size = decimal.Parse(level[1].GetString() ?? "0", CultureInfo.InvariantCulture);

            if (size == 0)
                side.Remove(price);
            else
                side[price] = size;
        }
    }

    private static decimal GetTopLiquidityUnsafe(
        Dictionary<decimal, decimal> side,
        bool descending,
        int levels)
    {
        IEnumerable<KeyValuePair<decimal, decimal>> ordered = descending
            ? side.OrderByDescending(x => x.Key)
            : side.OrderBy(x => x.Key);

        return ordered.Take(levels).Sum(x => x.Key * x.Value);
    }
}
