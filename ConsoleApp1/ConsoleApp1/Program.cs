using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;


// =====================================================
//                      НАСТРОЙКИ
// =====================================================

const string Symbol = "ETHUSDT";

const string BybitWsUrl =
    "wss://stream.bybit.com/v5/public/linear";

const string BybitRestUrl =
    "https://api.bybit.com";


// Сколько закрытых минутных свечей
// используем для среднего объёма
const int BaselineCandles = 20;


// ATR
const int AtrPeriod = 14;


// Одно окно анализа order flow
const int WindowSeconds = 30;


// Первые N секунд окна только собираем данные
const int MinSignalSeconds = 10;


// Во сколько раз объём должен превышать обычный темп
const decimal SpikeMultiplier = 2.5m;


// Минимальная доля агрессивной стороны
const decimal DirectionShare = 0.65m;


// Минимальное движение цены за окно
const decimal MinPriceMovePercent = 0.10m;


// Минимальная модельная сила сигнала
const decimal MinSignalScore = 55m;


// Сколько минут ждём входа в зону
const int SignalLifetimeMinutes = 10;


// После закрытия сделки не ищем новую N секунд
const int SignalCooldownSeconds = 60;


// Для исторической вероятности нужно хотя бы
// столько похожих закрытых сделок
const int MinimumHistorySamples = 12;


// Файл статистики
const string HistoryFile = "trades.json";


// =====================================================
//                  TELEGRAM TOKEN
// =====================================================

// НЕ отправляй этот токен никому.
// Если старый токен уже показывал на скриншоте —
// создай новый через BotFather.

string telegramToken =
    "8611038291:AAGkHJS8o3-z3U6MtUmPP1ZcHE7lYeFpco8";


// =====================================================
//                  ГЛОБАЛЬНЫЕ ДАННЫЕ
// =====================================================

var bot =
    new TelegramBotClient(
        telegramToken
    );


long? chatId = null;


// Одна активная paper-сделка одновременно
ActiveTrade? activeTrade = null;


// Последнее время завершения сделки
DateTime lastTradeFinishedAt =
    DateTime.MinValue;


// История paper-сделок
List<TradeRecord> tradeHistory =
    LoadTradeHistory();


using HttpClient http =
    new HttpClient
    {
        BaseAddress =
            new Uri(BybitRestUrl)
    };


// =====================================================
//                  TELEGRAM START
// =====================================================

var me =
    await bot.GetMe();


Console.WriteLine(
    $"✅ Telegram бот @{me.Username} запущен"
);

Console.WriteLine(
    $"📁 Загружено сделок из истории: {tradeHistory.Count}"
);

Console.WriteLine(
    "Напиши боту /start"
);


bot.OnMessage += OnTelegramMessage;


// =====================================================
//                  ЗАПУСК BYBIT
// =====================================================

await WatchBybit();


// =====================================================
//                  TELEGRAM COMMANDS
// =====================================================

async Task OnTelegramMessage(
    Message message,
    UpdateType type)
{
    if (message.Text == null)
        return;


    string command =
        message.Text
            .Trim()
            .ToLower();


    // -------------------------------------------------
    // /start
    // -------------------------------------------------

    if (command == "/start")
    {
        chatId =
            message.Chat.Id;


        await SendTelegram(
            "✅ ETH Volume Scanner запущен\n\n" +

            $"Биржа: Bybit\n" +
            $"Пара: {Symbol}\n\n" +

            $"Окно анализа: {WindowSeconds} сек\n" +
            $"Volume threshold: x{SpikeMultiplier}\n" +
            $"Buy/Sell threshold: {DirectionShare:P0}\n" +
            $"Минимальное движение: {MinPriceMovePercent:F2}%\n\n" +

            "Команды:\n" +
            "/status — текущая сделка\n" +
            "/stats — статистика сигналов"
        );


        Console.WriteLine(
            $"Telegram Chat ID: {chatId}"
        );


        return;
    }


    // -------------------------------------------------
    // /status
    // -------------------------------------------------

    if (command == "/status")
    {
        chatId =
            message.Chat.Id;


        if (activeTrade == null)
        {
            await SendTelegram(
                "📊 Активной сделки сейчас нет.\n\n" +
                "Бот продолжает анализировать ETHUSDT."
            );


            return;
        }


        await SendTelegram(
            GetTradeStatusText(
                activeTrade
            )
        );


        return;
    }


    // -------------------------------------------------
    // /stats
    // -------------------------------------------------

    if (command == "/stats")
    {
        chatId =
            message.Chat.Id;


        await SendTelegram(
            GetStatsText()
        );


        return;
    }
}


// =====================================================
//          ПОЛУЧАЕМ VOLUME BASELINE + ATR
// =====================================================

async Task<MarketBaseline>
    GetMarketBaseline()
{
    // Нужно минимум 20 свечей для volume
    // + ещё одна предыдущая для ATR.

    int limit =
        Math.Max(
            BaselineCandles + 2,
            AtrPeriod + 2
        );


    string url =
        $"/v5/market/kline" +
        $"?category=linear" +
        $"&symbol={Symbol}" +
        $"&interval=1" +
        $"&limit={limit}";


    string response =
        await http.GetStringAsync(
            url
        );


    using JsonDocument json =
        JsonDocument.Parse(
            response
        );


    CheckBybitResponse(
        json
    );


    JsonElement list =
        json.RootElement
            .GetProperty("result")
            .GetProperty("list");


    if (
        list.GetArrayLength() <
        limit - 1)
    {
        throw new Exception(
            "Bybit вернул недостаточно свечей."
        );
    }


    // -------------------------------------------------
    // Средний turnover
    // -------------------------------------------------

    decimal totalTurnover = 0;


    // list[0] — текущая незакрытая свеча.
    // Поэтому используем list[1] и далее.

    for (
        int i = 1;
        i <= BaselineCandles;
        i++)
    {
        // [6] = turnover
        decimal turnover =
            ParseDecimal(
                list[i][6]
            );


        totalTurnover +=
            turnover;
    }


    decimal averageTurnover =
        totalTurnover /
        BaselineCandles;


    // -------------------------------------------------
    // ATR
    // -------------------------------------------------

    decimal totalTrueRange = 0;


    for (
        int i = 1;
        i <= AtrPeriod;
        i++)
    {
        decimal high =
            ParseDecimal(
                list[i][2]
            );


        decimal low =
            ParseDecimal(
                list[i][3]
            );


        decimal previousClose =
            ParseDecimal(
                list[i + 1][4]
            );


        decimal highLow =
            high - low;


        decimal highPrevious =
            Math.Abs(
                high -
                previousClose
            );


        decimal lowPrevious =
            Math.Abs(
                low -
                previousClose
            );


        decimal trueRange =
            Math.Max(
                highLow,
                Math.Max(
                    highPrevious,
                    lowPrevious
                )
            );


        totalTrueRange +=
            trueRange;
    }


    decimal atr =
        totalTrueRange /
        AtrPeriod;


    return new MarketBaseline
    {
        AverageMinuteTurnover =
            averageTurnover,

        Atr =
            atr
    };
}


// =====================================================
//                  OPEN INTEREST
// =====================================================

async Task<decimal> GetOpenInterest()
{
    string url =
        $"/v5/market/tickers" +
        $"?category=linear" +
        $"&symbol={Symbol}";


    string response =
        await http.GetStringAsync(
            url
        );


    using JsonDocument json =
        JsonDocument.Parse(
            response
        );


    CheckBybitResponse(
        json
    );


    JsonElement ticker =
        json.RootElement
            .GetProperty("result")
            .GetProperty("list")[0];


    return decimal.Parse(
        ticker
            .GetProperty("openInterest")
            .GetString()!,

        CultureInfo.InvariantCulture
    );
}


// =====================================================
//              ПРОВЕРКА ОТВЕТА BYBIT
// =====================================================

void CheckBybitResponse(
    JsonDocument json)
{
    int retCode =
        json.RootElement
            .GetProperty("retCode")
            .GetInt32();


    if (retCode == 0)
        return;


    string message =
        json.RootElement
            .GetProperty("retMsg")
            .GetString()
        ?? "Unknown Bybit error";


    throw new Exception(
        $"Bybit {retCode}: {message}"
    );
}


// =====================================================
//                   ОСНОВНОЙ SCANNER
// =====================================================

async Task WatchBybit()
{
    while (true)
    {
        CancellationTokenSource?
            heartbeatCancellation = null;


        try
        {
            // =========================================
            // BASELINE
            // =========================================

            MarketBaseline baseline =
                await GetMarketBaseline();


            Console.WriteLine();

            Console.WriteLine(
                $"📊 Avg turnover: " +
                $"${baseline.AverageMinuteTurnover:N0}"
            );

            Console.WriteLine(
                $"📏 ATR(14): " +
                $"${baseline.Atr:F2}"
            );


            // =========================================
            // WEBSOCKET
            // =========================================

            using ClientWebSocket socket =
                new ClientWebSocket();


            await socket.ConnectAsync(
                new Uri(
                    BybitWsUrl
                ),
                CancellationToken.None
            );


            Console.WriteLine(
                "✅ Bybit WebSocket подключён"
            );


            // =========================================
            // SUBSCRIBE
            // =========================================

            string subscription =
                $$"""
                {
                    "op": "subscribe",
                    "args": [
                        "publicTrade.{{Symbol}}"
                    ]
                }
                """;


            await SendWebSocketText(
                socket,
                subscription,
                CancellationToken.None
            );


            // =========================================
            // HEARTBEAT
            // =========================================

            heartbeatCancellation =
                new CancellationTokenSource();


            _ = Task.Run(
                () =>
                    Heartbeat(
                        socket,
                        heartbeatCancellation.Token
                    )
            );


            FlowWindow window =
                new FlowWindow();


            decimal windowStartOi =
                await SafeGetOpenInterest();


            DateTime nextBaselineUpdate =
                DateTime.UtcNow
                    .AddMinutes(1);


            // =========================================
            // RECEIVE LOOP
            // =========================================

            while (
                socket.State ==
                WebSocketState.Open)
            {
                string text =
                    await ReceiveText(
                        socket
                    );


                if (
                    string.IsNullOrWhiteSpace(
                        text))
                {
                    continue;
                }


                // -------------------------------------
                // Раз в минуту обновляем ATR/volume
                // -------------------------------------

                if (
                    DateTime.UtcNow >=
                    nextBaselineUpdate)
                {
                    try
                    {
                        baseline =
                            await GetMarketBaseline();


                        nextBaselineUpdate =
                            DateTime.UtcNow
                                .AddMinutes(1);
                    }
                    catch (
                        Exception ex)
                    {
                        Console.WriteLine(
                            $"⚠️ Baseline: {ex.Message}"
                        );
                    }
                }


                using JsonDocument json =
                    JsonDocument.Parse(
                        text
                    );


                JsonElement root =
                    json.RootElement;


                // ping / subscribe response
                if (
                    !root.TryGetProperty(
                        "topic",
                        out JsonElement topic))
                {
                    continue;
                }


                string? topicName =
                    topic.GetString();


                if (
                    topicName == null ||
                    !topicName.StartsWith(
                        "publicTrade."))
                {
                    continue;
                }


                if (
                    !root.TryGetProperty(
                        "data",
                        out JsonElement trades))
                {
                    continue;
                }


                // =====================================
                // КАЖДАЯ СДЕЛКА BYBIT
                // =====================================

                foreach (
                    JsonElement trade
                    in trades.EnumerateArray())
                {
                    long tradeTime =
                        trade
                            .GetProperty("T")
                            .GetInt64();


                    decimal price =
                        decimal.Parse(
                            trade
                                .GetProperty("p")
                                .GetString()!,

                            CultureInfo.InvariantCulture
                        );


                    decimal size =
                        decimal.Parse(
                            trade
                                .GetProperty("v")
                                .GetString()!,

                            CultureInfo.InvariantCulture
                        );


                    string side =
                        trade
                            .GetProperty("S")
                            .GetString()!;


                    // =================================
                    // ВЕДЁМ СУЩЕСТВУЮЩУЮ СДЕЛКУ
                    // =================================

                    await CheckActiveTrade(
                        price
                    );


                    // =================================
                    // ИНИЦИАЛИЗАЦИЯ ОКНА
                    // =================================

                    if (
                        window.StartTime == 0)
                    {
                        window.Reset(
                            tradeTime,
                            price
                        );


                        windowStartOi =
                            await SafeGetOpenInterest();
                    }


                    decimal elapsed =
                        (tradeTime -
                         window.StartTime)
                        /
                        1000m;


                    // =================================
                    // НОВОЕ 30-СЕК ОКНО
                    // =================================

                    if (
                        elapsed >=
                        WindowSeconds)
                    {
                        window.Reset(
                            tradeTime,
                            price
                        );


                        windowStartOi =
                            await SafeGetOpenInterest();


                        elapsed = 0;
                    }


                    window.AddTrade(
                        price,
                        size,
                        side
                    );


                    elapsed =
                        Math.Max(
                            1,

                            (tradeTime -
                             window.StartTime)
                            /
                            1000m
                        );


                    // Первые секунды только собираем
                    if (
                        elapsed <
                        MinSignalSeconds)
                    {
                        continue;
                    }


                    decimal expectedTurnover =
                        baseline.AverageMinuteTurnover
                        *
                        (elapsed / 60m);


                    if (
                        expectedTurnover <= 0)
                    {
                        continue;
                    }


                    // =================================
                    // VOLUME SPIKE
                    // =================================

                    decimal volumeRatio =
                        window.TotalTurnover
                        /
                        expectedTurnover;


                    decimal buyShare =
                        window.TotalTurnover == 0
                            ? 0
                            : window.BuyTurnover /
                              window.TotalTurnover;


                    decimal sellShare =
                        window.TotalTurnover == 0
                            ? 0
                            : window.SellTurnover /
                              window.TotalTurnover;


                    decimal priceChange =
                        (
                            (
                                window.LastPrice -
                                window.OpenPrice
                            )
                            /
                            window.OpenPrice
                        )
                        * 100m;


                    Console.WriteLine(
                        $"{DateTime.Now:HH:mm:ss} | " +

                        $"${price:F2} | " +

                        $"VOL x{volumeRatio:F2} | " +

                        $"BUY {buyShare:P0} | " +

                        $"SELL {sellShare:P0} | " +

                        $"{priceChange:+0.00;-0.00}%"
                    );


                    // Уже есть сигнал/сделка
                    if (
                        activeTrade != null)
                    {
                        continue;
                    }


                    // Cooldown
                    if (
                        DateTime.UtcNow -
                        lastTradeFinishedAt
                        <
                        TimeSpan.FromSeconds(
                            SignalCooldownSeconds))
                    {
                        continue;
                    }


                    if (
                        window.AlertSent)
                    {
                        continue;
                    }


                    // =================================
                    // БАЗОВЫЕ УСЛОВИЯ LONG
                    // =================================

                    bool longSignal =
                        volumeRatio >=
                            SpikeMultiplier
                        &&
                        buyShare >=
                            DirectionShare
                        &&
                        priceChange >=
                            MinPriceMovePercent;


                    // =================================
                    // БАЗОВЫЕ УСЛОВИЯ SHORT
                    // =================================

                    bool shortSignal =
                        volumeRatio >=
                            SpikeMultiplier
                        &&
                        sellShare >=
                            DirectionShare
                        &&
                        priceChange <=
                            -MinPriceMovePercent;


                    if (
                        !longSignal &&
                        !shortSignal)
                    {
                        continue;
                    }


                    decimal currentOi =
                        await SafeGetOpenInterest();


                    decimal oiChange = 0;


                    if (
                        windowStartOi > 0 &&
                        currentOi > 0)
                    {
                        oiChange =
                            (
                                (
                                    currentOi -
                                    windowStartOi
                                )
                                /
                                windowStartOi
                            )
                            *
                            100m;
                    }


                    string direction =
                        longSignal
                            ? "LONG"
                            : "SHORT";


                    decimal relevantShare =
                        direction == "LONG"
                            ? buyShare
                            : sellShare;


                    // =================================
                    // SIGNAL SCORE
                    // =================================

                    decimal signalScore =
                        CalculateSignalScore(
                            volumeRatio,
                            relevantShare,
                            priceChange,
                            oiChange
                        );


                    if (
                        signalScore <
                        MinSignalScore)
                    {
                        continue;
                    }


                    bool created =
                        await CreateSignal(
                            direction,
                            window,
                            baseline,
                            volumeRatio,
                            buyShare,
                            sellShare,
                            priceChange,
                            oiChange,
                            signalScore
                        );


                    if (created)
                    {
                        window.AlertSent =
                            true;
                    }
                }
            }
        }
        catch (
            Exception ex)
        {
            Console.WriteLine();

            Console.WriteLine(
                $"❌ Bybit: {ex.Message}"
            );

            Console.WriteLine(
                "Переподключение через 3 секунды..."
            );
        }
        finally
        {
            if (
                heartbeatCancellation != null)
            {
                heartbeatCancellation.Cancel();

                heartbeatCancellation.Dispose();
            }
        }


        await Task.Delay(
            3000
        );
    }
}


// =====================================================
//           МОДЕЛЬНАЯ ОЦЕНКА СИГНАЛА
// =====================================================

decimal CalculateSignalScore(
    decimal volumeRatio,
    decimal directionShare,
    decimal priceChange,
    decimal oiChange)
{
    // Максимум:
    // Volume    = 35
    // Flow      = 30
    // Price     = 20
    // OI        = 15
    //
    // TOTAL     = 100


    decimal volumeScore =
        Scale(
            volumeRatio,
            SpikeMultiplier,
            5.0m,
            0.50m,
            1.00m
        )
        * 35m;


    decimal flowScore =
        Scale(
            directionShare,
            DirectionShare,
            0.85m,
            0.50m,
            1.00m
        )
        * 30m;


    decimal priceScore =
        Scale(
            Math.Abs(
                priceChange
            ),
            MinPriceMovePercent,
            0.50m,
            0.50m,
            1.00m
        )
        * 20m;


    decimal oiMultiplier;


    // Падающий OI обычно делает
    // такой сигнал менее убедительным.
    if (oiChange < -0.05m)
    {
        oiMultiplier =
            0.20m;
    }
    else
    {
        oiMultiplier =
            Scale(
                oiChange,
                0m,
                0.25m,
                0.40m,
                1.00m
            );
    }


    decimal oiScore =
        oiMultiplier *
        15m;


    decimal total =
        volumeScore +
        flowScore +
        priceScore +
        oiScore;


    return Math.Clamp(
        total,
        0m,
        100m
    );
}


// =====================================================
//                  SCALE FUNCTION
// =====================================================

decimal Scale(
    decimal value,
    decimal minimum,
    decimal strong,
    decimal minimumScore,
    decimal maximumScore)
{
    if (value <= minimum)
    {
        return minimumScore;
    }


    if (value >= strong)
    {
        return maximumScore;
    }


    decimal progress =
        (value - minimum)
        /
        (strong - minimum);


    return
        minimumScore
        +
        progress *
        (
            maximumScore -
            minimumScore
        );
}


// =====================================================
//            ОЦЕНКА ВЕРОЯТНОСТИ TP
// =====================================================

ProbabilityResult
    GetEstimatedProbability(
        string direction,
        decimal signalScore)
{
    // Ищем прошлые сделки того же направления
    // с похожим score ±10.

    List<TradeRecord> similar =
        tradeHistory
            .Where(
                x =>
                    x.Direction ==
                        direction
                    &&
                    Math.Abs(
                        x.SignalScore -
                        signalScore
                    )
                    <= 10m
            )
            .ToList();


    // Если статистики уже достаточно —
    // используем реальные результаты.

    if (
        similar.Count >=
        MinimumHistorySamples)
    {
        int wins =
            similar.Count(
                x => x.Result == "TP"
            );


        // Небольшое Bayesian smoothing.
        // Не даём, например, 5/5 превратиться
        // сразу в "100% вероятность".

        decimal probability =
            (
                wins + 2m
            )
            /
            (
                similar.Count + 4m
            )
            *
            100m;


        return new ProbabilityResult
        {
            Probability =
                probability,

            IsHistorical =
                true,

            Samples =
                similar.Count
        };
    }


    // Пока истории мало —
    // score используется как модельная оценка.

    return new ProbabilityResult
    {
        Probability =
            signalScore,

        IsHistorical =
            false,

        Samples =
            similar.Count
    };
}


// =====================================================
//                СОЗДАЁМ SIGNAL
// =====================================================

async Task<bool> CreateSignal(
    string direction,
    FlowWindow window,
    MarketBaseline baseline,
    decimal volumeRatio,
    decimal buyShare,
    decimal sellShare,
    decimal priceChange,
    decimal oiChange,
    decimal signalScore)
{
    if (
        activeTrade != null ||
        chatId == null)
    {
        return false;
    }


    decimal range =
        window.HighPrice -
        window.LowPrice;


    if (
        range <= 0)
    {
        return false;
    }


    // ================================================
    // VWAP
    // ================================================

    decimal vwap =
        window.TotalVolume == 0
            ? window.LastPrice
            : window.TotalTurnover
              /
              window.TotalVolume;


    // ================================================
    // ENTRY ZONE
    // ================================================

    decimal zoneLow;

    decimal zoneHigh;


    if (
        direction == "LONG")
    {
        // Откат импульса 38.2%-50%

        zoneLow =
            window.HighPrice -
            range * 0.50m;


        zoneHigh =
            window.HighPrice -
            range * 0.382m;
    }
    else
    {
        zoneLow =
            window.LowPrice +
            range * 0.382m;


        zoneHigh =
            window.LowPrice +
            range * 0.50m;
    }


    decimal suggestedEntry =
        Math.Clamp(
            vwap,
            zoneLow,
            zoneHigh
        );


    // ================================================
    // STOP LOSS
    // ================================================

    // Stop buffer учитывает как размер текущего
    // импульса, так и текущую волатильность ATR.

    decimal stopBuffer =
        Math.Max(
            range * 0.20m,
            baseline.Atr * 0.15m
        );


    decimal stopLoss;


    if (
        direction == "LONG")
    {
        stopLoss =
            window.LowPrice -
            stopBuffer;
    }
    else
    {
        stopLoss =
            window.HighPrice +
            stopBuffer;
    }


    decimal risk =
        direction == "LONG"
            ? suggestedEntry -
              stopLoss

            : stopLoss -
              suggestedEntry;


    if (
        risk <= 0)
    {
        return false;
    }


    // ================================================
    // ОДИН TAKE PROFIT
    // ================================================

    // Target адаптируется к ATR.
    //
    // Минимум 1.1R
    // Максимум 1.8R

    decimal atrReward =
        baseline.Atr *
        1.25m;


    decimal rewardDistance =
        Math.Clamp(
            atrReward,
            risk * 1.10m,
            risk * 1.80m
        );


    decimal takeProfit =
        direction == "LONG"
            ? suggestedEntry +
              rewardDistance

            : suggestedEntry -
              rewardDistance;


    decimal riskReward =
        rewardDistance /
        risk;


    // ================================================
    // ПОТЕНЦИАЛЬНАЯ ПРИБЫЛЬ %
    // ================================================

    decimal expectedProfitPercent =
        Math.Abs(
            (
                takeProfit -
                suggestedEntry
            )
            /
            suggestedEntry
            *
            100m
        );


    decimal riskPercent =
        Math.Abs(
            (
                stopLoss -
                suggestedEntry
            )
            /
            suggestedEntry
            *
            100m
        );


    // ================================================
    // ВЕРОЯТНОСТЬ
    // ================================================

    ProbabilityResult probabilityResult =
        GetEstimatedProbability(
            direction,
            signalScore
        );


    // ================================================
    // СОХРАНЯЕМ ACTIVE TRADE
    // ================================================

    activeTrade =
        new ActiveTrade
        {
            Direction =
                direction,

            Status =
                TradeStatus.WaitingForEntry,

            SignalTime =
                DateTime.UtcNow,

            ZoneLow =
                zoneLow,

            ZoneHigh =
                zoneHigh,

            SuggestedEntry =
                suggestedEntry,

            InitialStopLoss =
                stopLoss,

            TakeProfit =
                takeProfit,

            Atr =
                baseline.Atr,

            SignalScore =
                signalScore,

            EstimatedProbability =
                probabilityResult.Probability,

            ProbabilityIsHistorical =
                probabilityResult.IsHistorical,

            ProbabilitySamples =
                probabilityResult.Samples,

            ExpectedProfitPercent =
                expectedProfitPercent,

            ExpectedRiskPercent =
                riskPercent,

            RiskReward =
                riskReward,

            SignalVolumeRatio =
                volumeRatio,

            SignalBuyShare =
                buyShare,

            SignalSellShare =
                sellShare
        };


    string emoji =
        direction == "LONG"
            ? "🚀"
            : "🔻";


    string probabilitySource;


    if (
        probabilityResult.IsHistorical)
    {
        probabilitySource =
            $"по {probabilityResult.Samples} " +
            $"похожим прошлым сделкам";
    }
    else
    {
        probabilitySource =
            "модельная оценка — " +
            $"истории пока мало " +
            $"({probabilityResult.Samples}/" +
            $"{MinimumHistorySamples})";
    }


    string waitingText =
        direction == "LONG"
            ? "Ждём откат цены в зону входа."
            : "Ждём возврат цены вверх в зону входа.";


    await SendTelegram(
        $"{emoji} {direction} SIGNAL\n" +
        $"{Symbol} | BYBIT\n\n" +

        $"💰 Цена сейчас: " +
        $"${window.LastPrice:F2}\n\n" +

        $"🔥 Volume spike: " +
        $"x{volumeRatio:F2}\n" +

        $"🟢 Aggressive Buy: " +
        $"{buyShare:P1}\n" +

        $"🔴 Aggressive Sell: " +
        $"{sellShare:P1}\n\n" +

        $"📊 Движение: " +
        $"{priceChange:+0.00;-0.00}%\n" +

        $"📊 Open Interest: " +
        $"{oiChange:+0.00;-0.00}%\n" +

        $"📏 ATR: " +
        $"${baseline.Atr:F2}\n\n" +

        $"🧠 Signal score: " +
        $"{signalScore:F0}/100\n\n" +

        $"🎲 Оценка вероятности TP: " +
        $"{probabilityResult.Probability:F1}%\n" +

        $"ℹ️ {probabilitySource}\n\n" +

        $"🎯 ЗОНА ВХОДА:\n" +
        $"${zoneLow:F2} — " +
        $"${zoneHigh:F2}\n\n" +

        $"⭐ Расчётный вход: " +
        $"${suggestedEntry:F2}\n\n" +

        $"🛑 STOP LOSS:\n" +
        $"${stopLoss:F2}\n" +

        $"Риск: " +
        $"{riskPercent:F2}%\n\n" +

        $"💰 TAKE PROFIT:\n" +
        $"${takeProfit:F2}\n\n" +

        $"📈 Потенциальная прибыль: " +
        $"+{expectedProfitPercent:F2}%\n\n" +

        $"📐 Risk / Reward: " +
        $"1:{riskReward:F2}\n\n" +

        $"⏳ {waitingText}\n\n" +

        $"Сигнал действует " +
        $"{SignalLifetimeMinutes} минут."
    );


    Console.WriteLine();

    Console.WriteLine(
        $"🚨 {direction} SIGNAL | " +
        $"Score {signalScore:F0} | " +
        $"Probability " +
        $"{probabilityResult.Probability:F1}%"
    );


    return true;
}


// =====================================================
//                 ВЕДЁМ PAPER TRADE
// =====================================================

async Task CheckActiveTrade(
    decimal currentPrice)
{
    if (
        activeTrade == null)
    {
        return;
    }


    ActiveTrade trade =
        activeTrade;


    // =================================================
    // WAITING FOR ENTRY
    // =================================================

    if (
        trade.Status ==
        TradeStatus.WaitingForEntry)
    {
        // ---------------------------------------------
        // Сигнал протух
        // ---------------------------------------------

        if (
            DateTime.UtcNow -
            trade.SignalTime
            >
            TimeSpan.FromMinutes(
                SignalLifetimeMinutes))
        {
            await SendTelegram(
                $"❌ СИГНАЛ ОТМЕНЁН\n\n" +

                $"{GetDirectionEmoji(trade.Direction)} " +
                $"{Symbol} {trade.Direction}\n\n" +

                $"Цена не вошла в зону " +
                $"в течение " +
                $"{SignalLifetimeMinutes} минут."
            );


            activeTrade =
                null;


            return;
        }


        // ---------------------------------------------
        // LONG invalidated до входа
        // ---------------------------------------------

        if (
            trade.Direction == "LONG"
            &&
            currentPrice <=
            trade.InitialStopLoss)
        {
            await SendTelegram(
                "❌ LONG SIGNAL INVALIDATED\n\n" +

                $"Цена: ${currentPrice:F2}\n\n" +

                "Уровень отмены был пробит " +
                "до открытия сделки."
            );


            activeTrade =
                null;


            return;
        }


        // ---------------------------------------------
        // SHORT invalidated до входа
        // ---------------------------------------------

        if (
            trade.Direction == "SHORT"
            &&
            currentPrice >=
            trade.InitialStopLoss)
        {
            await SendTelegram(
                "❌ SHORT SIGNAL INVALIDATED\n\n" +

                $"Цена: ${currentPrice:F2}\n\n" +

                "Уровень отмены был пробит " +
                "до открытия сделки."
            );


            activeTrade =
                null;


            return;
        }


        // ---------------------------------------------
        // Цена вошла в ENTRY ZONE
        // ---------------------------------------------

        bool insideZone =
            currentPrice >=
            trade.ZoneLow
            &&
            currentPrice <=
            trade.ZoneHigh;


        if (!insideZone)
            return;


        // =============================================
        // OPEN
        // =============================================

        trade.ActualEntry =
            currentPrice;


        trade.OpenTime =
            DateTime.UtcNow;


        decimal risk =
            trade.Direction == "LONG"
                ? trade.ActualEntry -
                  trade.InitialStopLoss

                : trade.InitialStopLoss -
                  trade.ActualEntry;


        if (
            risk <= 0)
        {
            activeTrade =
                null;


            return;
        }


        // Пересчитываем ONE TP уже
        // относительно фактического входа.

        decimal atrReward =
            trade.Atr *
            1.25m;


        decimal rewardDistance =
            Math.Clamp(
                atrReward,
                risk * 1.10m,
                risk * 1.80m
            );


        trade.TakeProfit =
            trade.Direction == "LONG"
                ? trade.ActualEntry +
                  rewardDistance

                : trade.ActualEntry -
                  rewardDistance;


        trade.RiskReward =
            rewardDistance /
            risk;


        trade.ExpectedProfitPercent =
            Math.Abs(
                (
                    trade.TakeProfit -
                    trade.ActualEntry
                )
                /
                trade.ActualEntry
                *
                100m
            );


        trade.ExpectedRiskPercent =
            Math.Abs(
                (
                    trade.InitialStopLoss -
                    trade.ActualEntry
                )
                /
                trade.ActualEntry
                *
                100m
            );


        trade.Status =
            TradeStatus.Open;


        await SendTelegram(
            $"✅ PAPER-СДЕЛКА ОТКРЫТА\n\n" +

            $"{GetDirectionEmoji(trade.Direction)} " +
            $"{Symbol} {trade.Direction}\n\n" +

            $"⭐ Вход: " +
            $"${trade.ActualEntry:F2}\n\n" +

            $"🎲 Оценка TP: " +
            $"{trade.EstimatedProbability:F1}%\n\n" +

            $"🛑 STOP LOSS: " +
            $"${trade.InitialStopLoss:F2}\n" +

            $"Риск: " +
            $"{trade.ExpectedRiskPercent:F2}%\n\n" +

            $"💰 TAKE PROFIT: " +
            $"${trade.TakeProfit:F2}\n\n" +

            $"📈 Потенциальная прибыль: " +
            $"+{trade.ExpectedProfitPercent:F2}%\n\n" +

            $"📐 Risk / Reward: " +
            $"1:{trade.RiskReward:F2}"
        );


        Console.WriteLine();

        Console.WriteLine(
            $"✅ PAPER {trade.Direction} OPEN " +
            $"@ ${trade.ActualEntry:F2}"
        );


        return;
    }


    // =================================================
    // OPEN
    // =================================================

    if (
        trade.Status !=
        TradeStatus.Open)
    {
        return;
    }


    // =================================================
    // LONG
    // =================================================

    if (
        trade.Direction == "LONG")
    {
        // ---------------------------------------------
        // STOP
        // ---------------------------------------------

        if (
            currentPrice <=
            trade.InitialStopLoss)
        {
            decimal pnl =
                GetPnlPercent(
                    trade,
                    trade.InitialStopLoss
                );


            await SendTelegram(
                $"🛑 LONG ЗАКРЫТ ПО STOP LOSS\n\n" +

                $"⭐ Вход: " +
                $"${trade.ActualEntry:F2}\n" +

                $"❌ Выход: " +
                $"${trade.InitialStopLoss:F2}\n\n" +

                $"🎲 Оценка до сделки: " +
                $"{trade.EstimatedProbability:F1}%\n\n" +

                $"📉 Результат: " +
                $"{pnl:F2}%"
            );


            RegisterTradeResult(
                trade,
                "SL",
                trade.InitialStopLoss,
                pnl
            );


            FinishTrade();

            return;
        }


        // ---------------------------------------------
        // TAKE PROFIT
        // ---------------------------------------------

        if (
            currentPrice >=
            trade.TakeProfit)
        {
            decimal pnl =
                GetPnlPercent(
                    trade,
                    trade.TakeProfit
                );


            await SendTelegram(
                $"🏆 LONG ЗАКРЫТ ПО TAKE PROFIT\n\n" +

                $"⭐ Вход: " +
                $"${trade.ActualEntry:F2}\n" +

                $"✅ Выход: " +
                $"${trade.TakeProfit:F2}\n\n" +

                $"🎲 Оценка до сделки: " +
                $"{trade.EstimatedProbability:F1}%\n\n" +

                $"📈 Прибыль: " +
                $"+{pnl:F2}%"
            );


            RegisterTradeResult(
                trade,
                "TP",
                trade.TakeProfit,
                pnl
            );


            FinishTrade();

            return;
        }
    }


    // =================================================
    // SHORT
    // =================================================

    else
    {
        // ---------------------------------------------
        // STOP
        // ---------------------------------------------

        if (
            currentPrice >=
            trade.InitialStopLoss)
        {
            decimal pnl =
                GetPnlPercent(
                    trade,
                    trade.InitialStopLoss
                );


            await SendTelegram(
                $"🛑 SHORT ЗАКРЫТ ПО STOP LOSS\n\n" +

                $"⭐ Вход: " +
                $"${trade.ActualEntry:F2}\n" +

                $"❌ Выход: " +
                $"${trade.InitialStopLoss:F2}\n\n" +

                $"🎲 Оценка до сделки: " +
                $"{trade.EstimatedProbability:F1}%\n\n" +

                $"📉 Результат: " +
                $"{pnl:F2}%"
            );


            RegisterTradeResult(
                trade,
                "SL",
                trade.InitialStopLoss,
                pnl
            );


            FinishTrade();

            return;
        }


        // ---------------------------------------------
        // TAKE PROFIT
        // ---------------------------------------------

        if (
            currentPrice <=
            trade.TakeProfit)
        {
            decimal pnl =
                GetPnlPercent(
                    trade,
                    trade.TakeProfit
                );


            await SendTelegram(
                $"🏆 SHORT ЗАКРЫТ ПО TAKE PROFIT\n\n" +

                $"⭐ Вход: " +
                $"${trade.ActualEntry:F2}\n" +

                $"✅ Выход: " +
                $"${trade.TakeProfit:F2}\n\n" +

                $"🎲 Оценка до сделки: " +
                $"{trade.EstimatedProbability:F1}%\n\n" +

                $"📈 Прибыль: " +
                $"+{pnl:F2}%"
            );


            RegisterTradeResult(
                trade,
                "TP",
                trade.TakeProfit,
                pnl
            );


            FinishTrade();

            return;
        }
    }
}


// =====================================================
//                  ЗАПИСЬ РЕЗУЛЬТАТА
// =====================================================

void RegisterTradeResult(
    ActiveTrade trade,
    string result,
    decimal exitPrice,
    decimal pnl)
{
    tradeHistory.Add(
        new TradeRecord
        {
            CloseTime =
                DateTime.UtcNow,

            Direction =
                trade.Direction,

            SignalScore =
                trade.SignalScore,

            EstimatedProbability =
                trade.EstimatedProbability,

            EntryPrice =
                trade.ActualEntry,

            ExitPrice =
                exitPrice,

            StopLoss =
                trade.InitialStopLoss,

            TakeProfit =
                trade.TakeProfit,

            ProfitPercent =
                pnl,

            Result =
                result
        }
    );


    SaveTradeHistory();
}


// =====================================================
//                  ЗАВЕРШЕНИЕ TRADE
// =====================================================

void FinishTrade()
{
    activeTrade =
        null;


    lastTradeFinishedAt =
        DateTime.UtcNow;


    Console.WriteLine();

    Console.WriteLine(
        "🏁 Paper trade завершён"
    );
}


// =====================================================
//                       PNL
// =====================================================

decimal GetPnlPercent(
    ActiveTrade trade,
    decimal exitPrice)
{
    if (
        trade.Direction == "LONG")
    {
        return
            (
                (
                    exitPrice -
                    trade.ActualEntry
                )
                /
                trade.ActualEntry
            )
            *
            100m;
    }


    return
        (
            (
                trade.ActualEntry -
                exitPrice
            )
            /
            trade.ActualEntry
        )
        *
        100m;
}


// =====================================================
//                     /STATUS
// =====================================================

string GetTradeStatusText(
    ActiveTrade trade)
{
    string emoji =
        GetDirectionEmoji(
            trade.Direction
        );


    if (
        trade.Status ==
        TradeStatus.WaitingForEntry)
    {
        return
            $"⏳ СИГНАЛ ЖДЁТ ВХОДА\n\n" +

            $"{emoji} {Symbol} " +
            $"{trade.Direction}\n\n" +

            $"🎲 Вероятность: " +
            $"{trade.EstimatedProbability:F1}%\n\n" +

            $"🎯 Зона:\n" +
            $"${trade.ZoneLow:F2} — " +
            $"${trade.ZoneHigh:F2}\n\n" +

            $"⭐ Расчётный вход: " +
            $"${trade.SuggestedEntry:F2}\n\n" +

            $"🛑 SL: " +
            $"${trade.InitialStopLoss:F2}\n\n" +

            $"💰 TP: " +
            $"${trade.TakeProfit:F2}\n\n" +

            $"📈 Потенциальная прибыль: " +
            $"+{trade.ExpectedProfitPercent:F2}%";
    }


    return
        $"🟢 PAPER-СДЕЛКА ОТКРЫТА\n\n" +

        $"{emoji} {Symbol} " +
        $"{trade.Direction}\n\n" +

        $"⭐ Вход: " +
        $"${trade.ActualEntry:F2}\n\n" +

        $"🎲 Вероятность: " +
        $"{trade.EstimatedProbability:F1}%\n\n" +

        $"🛑 SL: " +
        $"${trade.InitialStopLoss:F2}\n\n" +

        $"💰 TP: " +
        $"${trade.TakeProfit:F2}\n\n" +

        $"📈 Потенциальная прибыль: " +
        $"+{trade.ExpectedProfitPercent:F2}%\n\n" +

        $"📐 R/R: " +
        $"1:{trade.RiskReward:F2}";
}


// =====================================================
//                       /STATS
// =====================================================

string GetStatsText()
{
    if (
        tradeHistory.Count == 0)
    {
        return
            "📊 Статистики пока нет.\n\n" +
            "Нужно дождаться закрытых paper-сделок.";
    }


    int total =
        tradeHistory.Count;


    int wins =
        tradeHistory.Count(
            x => x.Result == "TP"
        );


    int losses =
        tradeHistory.Count(
            x => x.Result == "SL"
        );


    decimal winRate =
        total == 0
            ? 0
            : wins * 100m /
              total;


    decimal totalPnl =
        tradeHistory.Sum(
            x =>
                x.ProfitPercent
        );


    decimal averagePnl =
        total == 0
            ? 0
            : totalPnl /
              total;


    int longs =
        tradeHistory.Count(
            x =>
                x.Direction ==
                "LONG"
        );


    int shorts =
        tradeHistory.Count(
            x =>
                x.Direction ==
                "SHORT"
        );


    return
        $"📊 PAPER TRADING STATISTICS\n\n" +

        $"Всего сделок: {total}\n" +
        $"✅ TP: {wins}\n" +
        $"❌ SL: {losses}\n\n" +

        $"🎯 Winrate: " +
        $"{winRate:F1}%\n\n" +

        $"📈 Средний PnL: " +
        $"{averagePnl:+0.00;-0.00}%\n\n" +

        $"Σ PnL: " +
        $"{totalPnl:+0.00;-0.00}%\n\n" +

        $"🚀 LONG: {longs}\n" +
        $"🔻 SHORT: {shorts}";
}


// =====================================================
//                  HISTORY LOAD
// =====================================================

List<TradeRecord>
    LoadTradeHistory()
{
    try
    {
        if (
            !File.Exists(
                HistoryFile))
        {
            return new List<TradeRecord>();
        }


        string json =
            File.ReadAllText(
                HistoryFile
            );


        return
            JsonSerializer.Deserialize<
                List<TradeRecord>
            >(json)
            ??
            new List<TradeRecord>();
    }
    catch
    {
        return
            new List<TradeRecord>();
    }
}


// =====================================================
//                  HISTORY SAVE
// =====================================================

void SaveTradeHistory()
{
    try
    {
        string json =
            JsonSerializer.Serialize(
                tradeHistory,

                new JsonSerializerOptions
                {
                    WriteIndented =
                        true
                }
            );


        File.WriteAllText(
            HistoryFile,
            json
        );
    }
    catch (
        Exception ex)
    {
        Console.WriteLine(
            $"Ошибка записи истории: " +
            $"{ex.Message}"
        );
    }
}


// =====================================================
//                  SAFE GET OI
// =====================================================

async Task<decimal>
    SafeGetOpenInterest()
{
    try
    {
        return
            await GetOpenInterest();
    }
    catch (
        Exception ex)
    {
        Console.WriteLine(
            $"⚠️ OI: {ex.Message}"
        );


        return 0;
    }
}


// =====================================================
//                  TELEGRAM SEND
// =====================================================

async Task SendTelegram(
    string text)
{
    if (
        chatId == null)
    {
        return;
    }


    try
    {
        await bot.SendMessage(
            chatId.Value,
            text
        );
    }
    catch (
        Exception ex)
    {
        Console.WriteLine(
            $"Telegram: {ex.Message}"
        );
    }
}


// =====================================================
//                       EMOJI
// =====================================================

string GetDirectionEmoji(
    string direction)
{
    return
        direction == "LONG"
            ? "🚀"
            : "🔻";
}


// =====================================================
//                    HEARTBEAT
// =====================================================

async Task Heartbeat(
    ClientWebSocket socket,
    CancellationToken token)
{
    try
    {
        while (
            !token.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(
                    20),
                token
            );


            if (
                socket.State !=
                WebSocketState.Open)
            {
                return;
            }


            await SendWebSocketText(
                socket,

                """
                {
                    "op": "ping"
                }
                """,

                token
            );
        }
    }
    catch (
        OperationCanceledException)
    {
        // normal
    }
    catch (
        Exception ex)
    {
        Console.WriteLine(
            $"Heartbeat: {ex.Message}"
        );
    }
}


// =====================================================
//                SEND WEBSOCKET TEXT
// =====================================================

async Task SendWebSocketText(
    ClientWebSocket socket,
    string text,
    CancellationToken token)
{
    byte[] bytes =
        Encoding.UTF8.GetBytes(
            text
        );


    await socket.SendAsync(
        new ArraySegment<byte>(
            bytes),

        WebSocketMessageType.Text,

        true,

        token
    );
}


// =====================================================
//              RECEIVE WEBSOCKET TEXT
// =====================================================

async Task<string> ReceiveText(
    ClientWebSocket socket)
{
    byte[] buffer =
        new byte[
            16 * 1024
        ];


    using MemoryStream stream =
        new MemoryStream();


    WebSocketReceiveResult result;


    do
    {
        result =
            await socket.ReceiveAsync(
                new ArraySegment<byte>(
                    buffer),

                CancellationToken.None
            );


        if (
            result.MessageType ==
            WebSocketMessageType.Close)
        {
            throw new Exception(
                "Bybit закрыл WebSocket."
            );
        }


        stream.Write(
            buffer,
            0,
            result.Count
        );
    }
    while (
        !result.EndOfMessage);


    return
        Encoding.UTF8.GetString(
            stream.ToArray()
        );
}


// =====================================================
//                  PARSE DECIMAL
// =====================================================

decimal ParseDecimal(
    JsonElement element)
{
    return
        decimal.Parse(
            element.GetString()!,

            CultureInfo.InvariantCulture
        );
}


// =====================================================
//                       ENUM
// =====================================================

enum TradeStatus
{
    WaitingForEntry,

    Open
}


// =====================================================
//                   ACTIVE TRADE
// =====================================================

class ActiveTrade
{
    public string Direction
    {
        get;
        set;
    } = "";


    public TradeStatus Status
    {
        get;
        set;
    }


    public DateTime SignalTime
    {
        get;
        set;
    }


    public DateTime? OpenTime
    {
        get;
        set;
    }


    public decimal ZoneLow
    {
        get;
        set;
    }


    public decimal ZoneHigh
    {
        get;
        set;
    }


    public decimal SuggestedEntry
    {
        get;
        set;
    }


    public decimal ActualEntry
    {
        get;
        set;
    }


    public decimal InitialStopLoss
    {
        get;
        set;
    }


    public decimal TakeProfit
    {
        get;
        set;
    }


    public decimal Atr
    {
        get;
        set;
    }


    public decimal SignalScore
    {
        get;
        set;
    }


    public decimal EstimatedProbability
    {
        get;
        set;
    }


    public bool ProbabilityIsHistorical
    {
        get;
        set;
    }


    public int ProbabilitySamples
    {
        get;
        set;
    }


    public decimal ExpectedProfitPercent
    {
        get;
        set;
    }


    public decimal ExpectedRiskPercent
    {
        get;
        set;
    }


    public decimal RiskReward
    {
        get;
        set;
    }


    public decimal SignalVolumeRatio
    {
        get;
        set;
    }


    public decimal SignalBuyShare
    {
        get;
        set;
    }


    public decimal SignalSellShare
    {
        get;
        set;
    }
}


// =====================================================
//                  TRADE RECORD
// =====================================================

class TradeRecord
{
    public DateTime CloseTime
    {
        get;
        set;
    }


    public string Direction
    {
        get;
        set;
    } = "";


    public decimal SignalScore
    {
        get;
        set;
    }


    public decimal EstimatedProbability
    {
        get;
        set;
    }


    public decimal EntryPrice
    {
        get;
        set;
    }


    public decimal ExitPrice
    {
        get;
        set;
    }


    public decimal StopLoss
    {
        get;
        set;
    }


    public decimal TakeProfit
    {
        get;
        set;
    }


    public decimal ProfitPercent
    {
        get;
        set;
    }


    public string Result
    {
        get;
        set;
    } = "";
}


// =====================================================
//                  PROBABILITY RESULT
// =====================================================

class ProbabilityResult
{
    public decimal Probability
    {
        get;
        set;
    }


    public bool IsHistorical
    {
        get;
        set;
    }


    public int Samples
    {
        get;
        set;
    }
}


// =====================================================
//                  MARKET BASELINE
// =====================================================

class MarketBaseline
{
    public decimal AverageMinuteTurnover
    {
        get;
        set;
    }


    public decimal Atr
    {
        get;
        set;
    }
}


// =====================================================
//                     FLOW WINDOW
// =====================================================

class FlowWindow
{
    public long StartTime
    {
        get;
        private set;
    }


    public decimal OpenPrice
    {
        get;
        private set;
    }


    public decimal HighPrice
    {
        get;
        private set;
    }


    public decimal LowPrice
    {
        get;
        private set;
    }


    public decimal LastPrice
    {
        get;
        private set;
    }


    public decimal BuyTurnover
    {
        get;
        private set;
    }


    public decimal SellTurnover
    {
        get;
        private set;
    }


    public decimal TotalVolume
    {
        get;
        private set;
    }


    public decimal TotalTurnover =>
        BuyTurnover +
        SellTurnover;


    public bool AlertSent
    {
        get;
        set;
    }


    public void Reset(
        long time,
        decimal price)
    {
        StartTime =
            time;


        OpenPrice =
            price;


        HighPrice =
            price;


        LowPrice =
            price;


        LastPrice =
            price;


        BuyTurnover =
            0;


        SellTurnover =
            0;


        TotalVolume =
            0;


        AlertSent =
            false;
    }


    public void AddTrade(
        decimal price,
        decimal size,
        string side)
    {
        LastPrice =
            price;


        if (
            price >
            HighPrice)
        {
            HighPrice =
                price;
        }


        if (
            price <
            LowPrice)
        {
            LowPrice =
                price;
        }


        decimal turnover =
            price *
            size;


        TotalVolume +=
            size;


        if (
            side == "Buy")
        {
            BuyTurnover +=
                turnover;
        }
        else if (
            side == "Sell")
        {
            SellTurnover +=
                turnover;
        }
    }
}