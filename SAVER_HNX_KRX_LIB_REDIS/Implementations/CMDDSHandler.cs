using CommonLib.Interfaces;
using MDDSCore.Messages;
using Newtonsoft.Json;
using BaseSaverLib.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemCore.Entities;
using SystemCore.Temporaries;
using SystemCore.Interfaces;
using CommonLib.Implementations;
using PriceLib;
using System.Globalization;
using System.Diagnostics;
using StackExchange.Redis;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Newtonsoft.Json.Linq;
using static App.Metrics.Formatters.Json.TimerMetric;
using System.Text.RegularExpressions;
using static App.Metrics.Formatters.Json.BucketTimerMetric;
using StockCore.TAChart.Entities;
using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics.Metrics;
using SAVER_HSX_KRX_Lib.Models;
using static SystemCore.Entities.EGlobalConfig;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using Confluent.Kafka;
using Oracle.ManagedDataAccess.Types;
using PriceLib.Models;

namespace BaseSaverLib.Implementations
{
    public class CMDDSHandler : IMDDSHandler
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim semaphoreREDIS = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim semaphoreSQL = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim semaphoreORACLE = new SemaphoreSlim(1, 1);
        private ConcurrentQueue<EPrice> m_queueRedis = new ConcurrentQueue<EPrice>();
        private ConcurrentQueue<SqlMessage> m_queueSQL = new ConcurrentQueue<SqlMessage>();
        private ConcurrentQueue<SqlMessageWithObj> m_queueOracle = new ConcurrentQueue<SqlMessageWithObj>();
        public HashSet<string> marketDataTypes = new HashSet<string> { "X", "MF", "M8", "ME", "M7", "f" };

        // Dic lưu sequence trước theo msgType
        private readonly Dictionary<string, long> dic_preSeq = new Dictionary<string, long>();
        // Dic lưu danh sách các sequence bị thiếu theo msgType
        private readonly Dictionary<string, SequenceGapInfo> dic_missSeq = new Dictionary<string, SequenceGapInfo>();
        Stopwatch m_SW = new Stopwatch();

        // vars
        private readonly IS6GApp _app;
        private readonly IMDDSRepository _repository;
        private readonly EPriceConfig _priceConfig;
        private readonly CMonitor _monitor;
        private object m_objLocker = new object();
        private const string TEMPLATE_REDIS_KEY_REALTIME = "REALTIME:S5G_(Symbol)"; //   REALTIME:S5G_ABT
        public const int intPeriod = 43830; //đủ time cho key sống 1 tháng
        //KL theo thời gian lô chẵn
        private const string TEMPLATE_REDIS_KEY_LE = "LE:S5G_(Symbol)";       //   LE:S5G_ABT
        private const string TEMPLATE_REDIS_KEY_LE_TKTT_VOL = "TKTT:VOL:(Symbol):0";
        private const string TEMPLATE_REDIS_KEY_LE_TKTT_VAL = "TKTT:VAL:(Symbol):0";
        private const string TEMPLATE_REDIS_KEY_LS = "LS:(Symbol)";

        private const string TEMPLATE_REDIS_KEY_PT = "PT:SYMBOL:(Symbol):(Board)";
        private const string TEMPLATE_REDIS_KEY_PT_ALL = "PT:ALL:HSX:KL:(Board)";
        private const string TEMPLATE_REDIS_KEY_PT_SIDE_B = "PT:ALL:HSX:BUY:(Board)";
        private const string TEMPLATE_REDIS_KEY_PT_SIDE_S = "PT:ALL:HSX:SELL:(Board)";

        private const string TEMPLATE_JSONC_LE = "{\"MT\":\"(MT)\",\"MQ\":(MQ),\"MP\":(MP),\"TQ\":(TQ)}";
        private const string TEMPLATE_JSONC_LE_TKTT = "{\"MT\":\"(MT)\",\"MP\":(MP),\"TQ\":(TQ),\"TV\":(TV)}";
        private const string TEMPLATE_JSONC_LS = "{\"MT\":\"(MT)\",\"CN\":(CN),\"MP\":(MP),\"MQ\":(MQ),\"SIDE\":(SIDE)}";

        // lô lẻ hsx
        public const string TEMPLATE_JSONC_PO = "{\"T\":\"(T)\",\"S\":\"(S)\",\"BP1\":(BP1),\"BQ1\":(BQ1),\"BP2\":(BP2),\"BQ2\":(BQ2),\"BP3\":(BP3),\"BQ3\":(BQ3),\"SP1\":(SP1),\"SQ1\":(SQ1),\"SP2\":(SP2),\"SQ2\":(SQ2),\"SP3\":(SP3),\"SQ3\":(SQ3)}";    //
        public const string TEMPLATE_REDIS_KEY_PO = "PO:S5G_(Symbol)";
        public const string TEMPLATE_REDIS_KEY_STOCK_NO_HNX = "Key_StockNo_Saver_HNX";
        public const string TEMPLATE_REDIS_KEY_STOCK_NO_HSX = "Key_StockNo_Saver_HSX";
        private const string TEMPLATE_REDIS_VALUE = "{\"Time\":\"(Now)\",\"Data\":[(RedisData)]}";

        private readonly CRedisConfig _redisConfig;
        private readonly CRedis_New _redis;
        private readonly CRedisNewApp _redisNewApp;
        private Dictionary<string, string> d_dic_stockno = new Dictionary<string, string>();//dic lưu stock no của mess d
        public TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); // UTC+7
        /// <summary>
        /// 2020-07-30 13:39:41 ngocta2
        /// constructor
        /// </summary>
        /// <param name="app"></param>
        /// <param name="repository"></param>
        public CMDDSHandler(IS6GApp app, IMDDSRepository repository, CRedisConfig redisConfig, CRedis_New redis, CRedisNewApp _redis_NewApp, CMonitor monitor, EPriceConfig priceConfig)
        {
            this._app = app;
            this._repository = repository;
            this._redisConfig = redisConfig;
            this._redis = redis;
            this._redisNewApp = _redis_NewApp;
            this._monitor = monitor;
            this._priceConfig = priceConfig;
        }
        public string GetMsgTypeSAN(string rawData)
        {
            string msgType = Regex.Match(rawData, "30001=(.*?)", RegexOptions.Multiline).Groups[1].Value;
            return msgType;
        }
        public async Task<bool> BuildScriptSQL(string[] arrMsg)
        {
            await _semaphore.WaitAsync();
            try
            {
                var stopWatch = Stopwatch.StartNew();
                int intTotalRow = 0;
                // Duyệt từng tin nhắn và nhóm theo msgType
                foreach (string msg in arrMsg)
                {
                    //Log Dequeue
                    //this._app.InfoLogger.LogInfo(msg);
                    string msgType = this._app.Common.GetMsgType(msg);
                    if(msgType == ESecurityDefinition.__MSG_TYPE)
                    {
                        ESecurityDefinition eSD = await this.Raw2Entity<ESecurityDefinition>(msg);
                        // Lấy ra key msg lưu db
                        if ((eSD.MarketID == "STX" || eSD.MarketID == "UPX" || eSD.MarketID == "DVX") && eSD.BoardID == "G1" && !d_dic_stockno.ContainsKey(eSD.Symbol))
                        {
                            d_dic_stockno[eSD.Symbol] = eSD.TickerCode;
                            string stockno = JsonConvert.SerializeObject(d_dic_stockno);
                            _redis.SetCacheBI(TEMPLATE_REDIS_KEY_STOCK_NO_HNX, stockno, intPeriod);
                        }
                    }
                    if(msgType == EPrice.__MSG_TYPE)
                    {
                        intTotalRow++;   
                        EPrice eP = this._app.HandCode.Fix_Fix2EPrice(msg, true, 1, 2, 1);

                        if ((eP.MarketID == "STX" || eP.MarketID == "UPX" || eP.MarketID == "DVX") && eP.BoardID == "G1" && eP.Side == null)
                        {
                            await Task.WhenAll(UpdateRedisLE_TKTT2Redis(eP), UpdateRedisLE(eP), UpdateRedisLS(eP));
                        }
                        else if ((eP.MarketID == "STX" || eP.MarketID == "UPX" || eP.MarketID == "DVX") && eP.BoardID == "G4" && eP.Side != null)
                        {
                            // Giao dịch lô lẻ cho phần chi tiết giá
                            await UpdateRedisPO(eP);
                        }
                        else if ((eP.MarketID == "STX" || eP.MarketID == "UPX" || eP.MarketID == "DVX") && (eP.BoardID == "T1" || eP.BoardID == "T4" || eP.BoardID == "T2" || eP.BoardID == "T3" || eP.BoardID == "T6" || eP.BoardID == "R1") /*&& eP.Side != null*/)
                        {
                            await Task.WhenAll(UpdateRedisPT_KL(eP, eP.BoardID), UpdateRedisPT_ForAll_Side(eP, eP.BoardID));
                        }
                    }
                }
                //send Monitor
                this._monitor.SendStatusToMonitor(
                this._app.Common.GetLocalDateTime(),
                this._app.Common.GetLocalIp(),
                CMonitor.MONITOR_APP.HNX_Saver5G,
                intTotalRow,
                stopWatch.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }


        /// <summary>
        /// 2020-08-19 09:23:03 ngocta2
        /// giam lap code, ko tot
        /// chu y: bo keyword async tai day
        /// https://stackoverflow.com/questions/29923215/should-i-worry-about-this-async-method-lacks-await-operators-and-will-run-syn
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rawData"></param>
        /// <returns></returns>
        public Task<T> Raw2Entity<T>(string rawData) where T : EBase
        {
            try
            {
                string json = this._app.Common.Fix2Json(rawData);
                T entity = JsonConvert.DeserializeObject<T>(json);
                entity.SendingTime = this._app.Common.FixToDateTimeString(TimeZoneInfo.ConvertTimeFromUtc(DateTime.ParseExact(entity.SendingTime, "yyyyMMdd HH:mm:ss.fff", null), timeZone).ToString("yyyyMMdd HH:mm:ss.fff")); // can phai chuyen SendingTime tu string sang DateTime
                //entity.SendingTime = this._app.Common.FixToDateTimeString(entity.SendingTime.ToString());
                return Task.FromResult(entity);
            }
            catch (Exception ex)
            {
                // log error + buffer data
                this._app.ErrorLogger.LogError(ex);
                return null;
            }
        }

        /// <summary>
        /// Lưu key Redis cho:
        /// - Phần chi tiết mã - GD thỏa thuận
        /// - Màn hình GD thỏa thuận - phần Khớp lệnh
        /// </summary>
        /// <param name="eP"></param>
        /// <returns></returns>
        public async Task UpdateRedisPT_KL(EPrice eP, string BoardID)
        {
            try
            {
                string Symbol = "";
                string value = "";
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {

                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }
                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    Symbol = d_dic_stockno[eP.Symbol];
                    string time = (eP.SendingTime.Split(' ')[1]).Split('.')[0];

                    PT_Model pt_model = new PT_Model
                    {
                        MT = time,
                        MP = ProcessPrice(eP.MatchPrice),
                        MQ = eP.MatchQuantity,
                        TQ = eP.TotalVolumeTraded,
                        TV = eP.GrossTradeAmt
                    };
                    PT_ForAll pt_all = new PT_ForAll
                    {
                        Symbol = Symbol,
                        Data = pt_model
                    };
                    string strJson_Symbol = JsonConvert.SerializeObject(pt_model);
                    string strJson_All = JsonConvert.SerializeObject(pt_all);

                    string Z_KEY_SYMBOL = TEMPLATE_REDIS_KEY_PT.Replace("(Symbol)", Symbol).Replace("(Board)", BoardID);
                    string exchange = eP.MarketID switch
                    {
                        "STX" => "HNX",
                        "UPX" => "UPCOM",
                        "DVX" => "FU",
                        _ => ""
                    };
                    string Z_KEY_ALL = TEMPLATE_REDIS_KEY_PT_ALL.Replace("(Exchange)", exchange).Replace("(Board)", BoardID);

                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));

                    await Task.WhenAll(
                        this._redis.SortedSetAddAsync(Z_KEY_SYMBOL, strJson_Symbol, Z_SCORE),
                        this._redis.SortedSetAddAsync(Z_KEY_ALL, strJson_All, Z_SCORE)
                        );
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        /// <summary>
        /// Lưu key Redis cho phần chi tiết mã - Giao dịch thỏa thuận
        /// </summary>
        /// <param name="eP"></param>
        /// <returns></returns>
        public async Task UpdateRedisPT_ForAll_Side(EPrice eP, string BoardID)
        {
            try
            {
                string Symbol = "";
                string value = "";
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {

                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }
                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    Symbol = d_dic_stockno[eP.Symbol];

                    PT_Side_Model sideB = new PT_Side_Model
                    {
                        Symbol = Symbol,
                        Data = new Side_Data
                        {
                            MP = eP.BuyPrice1,
                            MQ = eP.BuyQuantity1
                        }
                    };

                    PT_Side_Model sideS = new PT_Side_Model
                    {
                        Symbol = Symbol,
                        Data = new Side_Data
                        {
                            MP = eP.SellPrice1,
                            MQ = eP.SellQuantity1
                        }
                    };

                    string exchange = eP.MarketID switch
                    {
                        "STX" => "HNX",
                        "UPX" => "UPCOM",
                        "DVX" => "FU",
                        _ => ""
                    };
                    string Z_KEY_BUY = TEMPLATE_REDIS_KEY_PT_SIDE_B.Replace("(Exchange)", exchange).Replace("(Board)", BoardID);
                    string Z_KEY_SELL = TEMPLATE_REDIS_KEY_PT_SIDE_S.Replace("(Exchange)", exchange).Replace("(Board)", BoardID);

                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));

                    // Chỉ insert nếu có dữ liệu hợp lệ
                    if (sideB.Data.MP > 0 & sideB.Data.MQ > 0)
                    {
                        string strJsonC_Buy = JsonConvert.SerializeObject(sideB);
                        await this._redis.SortedSetAddAsync(Z_KEY_BUY, strJsonC_Buy, Z_SCORE);
                    }

                    if (sideS.Data.MP > 0 & sideS.Data.MQ > 0)
                    {
                        string strJsonC_Sell = JsonConvert.SerializeObject(sideS);
                        await this._redis.SortedSetAddAsync(Z_KEY_SELL, strJsonC_Sell, Z_SCORE);
                    }
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task UpdateRedisLS(EPrice eP)
        {
            try
            {
                string Symbol = "";
                string value = "";
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {

                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }

                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    Symbol = d_dic_stockno[eP.Symbol];
                    bool checkDI = false;
                    if (eP.MarketID == "DVX" || eP.MarketID == "dvx")
                    {
                        checkDI = true;
                    }
                    LS_Model ls_model = new LS_Model();
                    string time = (eP.SendingTime.Split(' ')[1]).Split('.')[0];
                    string strJsonC = "";
                    //Xử lý CN -  Lấy Guid (random)
                    Guid guid = Guid.NewGuid();
                    string guidString = guid.ToString("N"); // Lấy chuỗi không dấu gạch ngang
                    string first10Digits = guidString.Substring(0, 10);
                    // Chuyển đổi 10 ký tự này thành số nguyên long
                    long lsCN = long.Parse(first10Digits.Substring(0, 10), System.Globalization.NumberStyles.HexNumber);
                    // Đảm bảo ls.CN có 10 chữ số bằng cách chia cho 10 nếu cần
                    while (lsCN >= 10000000000)
                    {
                        lsCN /= 10;
                    }
                    ls_model.CN = lsCN;
                    ls_model.MT = time.ToString();
                    ls_model.MP = eP.MatchPrice;
                    ls_model.MQ = eP.NoMDEntries;
                    ls_model.SIDE = eP.Side ?? string.Empty;

                    strJsonC = JsonConvert.SerializeObject(checkDI ? ls_model : new
                    {
                        ls_model.CN,
                        ls_model.MT,
                        MP = (int)ls_model.MP,
                        ls_model.MQ,
                        ls_model.SIDE
                    });

                    string Z_KEY = TEMPLATE_REDIS_KEY_LS.Replace("(Symbol)", Symbol);
                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));

                    if (this._redisNewApp != null)
                    {
                        _redisNewApp.SortedSetAddAsync(Z_KEY, strJsonC, Z_SCORE);
                    }

                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task UpdateRedisLE_TKTT2Redis(EPrice eP)
        {
            try
            {
                string Symbol = "";
                string value = "";
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {

                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }
                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    LE_TKTT_Model le_tktt = new LE_TKTT_Model();
                    bool checkDI = false;
                    if (eP.MarketID == "DVX" || eP.MarketID == "dvx")
                    {
                        checkDI = true;
                    }
                    Symbol = d_dic_stockno[eP.Symbol];
                    string time = (eP.SendingTime.Split(' ')[1]).Split('.')[0];
                    string strJsonC = "";
                    string strJson_Base = "";

                    le_tktt.MT = time.ToString();
                    le_tktt.MP = eP.MatchPrice;
                    le_tktt.TQ = eP.TotalVolumeTraded;
                    le_tktt.TV = eP.GrossTradeAmt;

                    strJson_Base = JsonConvert.SerializeObject(new
                    {
                        le_tktt.MT,
                        MP = (int)le_tktt.MP,
                        le_tktt.TQ,
                        le_tktt.TV
                    });

                    strJsonC = checkDI ? JsonConvert.SerializeObject(le_tktt) : strJson_Base;

                    string Z_KEY_VAL = TEMPLATE_REDIS_KEY_LE_TKTT_VAL.Replace("(Symbol)", Symbol);
                    string Z_KEY_VOL = TEMPLATE_REDIS_KEY_LE_TKTT_VOL.Replace("(Symbol)", Symbol);
                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                    //string Z_VALUE = strJsonC;
                    if (this._redisNewApp != null)
                    {
                        await Task.WhenAll(
                            _redisNewApp.SortedSetAddAsync2(Z_KEY_VOL, strJsonC, Z_SCORE),
                            _redisNewApp.SortedSetAddAsync2(Z_KEY_VAL, strJson_Base, Z_SCORE)
                        );
                    }

                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task UpdateRedisLE(EPrice eP)
        {
            try
            {
                string Symbol = "";
                string value = "";
                var stopWatch = Stopwatch.StartNew();
                //var SW = Stopwatch.StartNew();
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {

                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }
                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    Symbol = d_dic_stockno[eP.Symbol];
                    string time = (eP.SendingTime.Split(' ')[1]).Split('.')[0];
                    string strJsonC = "";

                    var leModel = new LE_Model
                    {
                        MT = time.ToString(),
                        MQ = eP.MatchQuantity,
                        MP = ProcessPrice(eP.MatchPrice),
                        TQ = eP.TotalVolumeTraded
                    };

                    strJsonC = JsonConvert.SerializeObject(leModel);

                    string Z_KEY = TEMPLATE_REDIS_KEY_LE.Replace("(Symbol)", Symbol);
                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                    string Z_VALUE = strJsonC;

                    await this._redis.SortedSetAddAsync(Z_KEY, Z_VALUE, Z_SCORE);
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task UpdateRedisPO(EPrice eP)
        {
            try
            {
                string Symbol = "";
                string value = "";
                var stopWatch = Stopwatch.StartNew();
                if (d_dic_stockno.Count < 1)
                {
                    value = _redis.RC_1.StringGet(TEMPLATE_REDIS_KEY_STOCK_NO_HNX);

                    if (!string.IsNullOrEmpty(value))
                    {
                        Dictionary<string, string> storedDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(value);

                        foreach (var kew in storedDictionary)
                        {
                            if (d_dic_stockno.ContainsKey(kew.Key))
                            {
                                d_dic_stockno[kew.Key] = kew.Value;
                            }
                            else
                            {
                                d_dic_stockno.Add(kew.Key, kew.Value);
                            }
                        }
                    }
                }
                if (d_dic_stockno.ContainsKey(eP.Symbol))
                {
                    Symbol = d_dic_stockno[eP.Symbol];
                    string sbJsonC = TEMPLATE_JSONC_PO
                                        .Replace("(T)", eP.SendingTime.Replace(" ", "-").Substring(0, eP.SendingTime.IndexOf(".")))
                                        .Replace("(S)", Symbol)
                                        .Replace("(BP1)", ProcessPrice(eP.BuyPrice1).ToString())
                                        .Replace("(BQ1)", eP.BuyQuantity1.ToString())
                                        .Replace("(BP2)", ProcessPrice(eP.BuyPrice2).ToString())
                                        .Replace("(BQ2)", eP.BuyQuantity2.ToString())
                                        .Replace("(BP3)", ProcessPrice(eP.BuyPrice3).ToString())
                                        .Replace("(BQ3)", eP.BuyQuantity3.ToString())
                                        .Replace("(SP1)", ProcessPrice(eP.SellPrice1).ToString())
                                        .Replace("(SQ1)", eP.SellQuantity1.ToString())
                                        .Replace("(SP2)", ProcessPrice(eP.SellPrice2).ToString())
                                        .Replace("(SQ2)", eP.SellQuantity2.ToString())
                                        .Replace("(SP3)", ProcessPrice(eP.SellPrice3).ToString())
                                        .Replace("(SQ3)", eP.SellQuantity3.ToString());

                    string Z_KEY = TEMPLATE_REDIS_KEY_PO
                            .Replace("(Symbol)", Symbol);

                    long Z_SCORE = Convert.ToInt64(DateTime.Now.ToString("yyyyMMddHHmmssfff"));

                    this._redis.SetCache(Z_KEY, sbJsonC, intPeriod);
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }

        public double ProcessPrice(double priceString, int priceDividedBy = 1000, int priceRoundDigitsCount = 2)
        {
            double price = priceString; // 43100
            price = price / priceDividedBy; // 43.1
            price = Math.Round(price, priceRoundDigitsCount); // 43.1
            return price;
        }

        public int Processkl(long priceString, int priceDividedBy = 10)
        {
            int kl = Convert.ToInt32(priceString); // 43100
            kl = kl / priceDividedBy; // 43.1
            return kl;
        }
    }
    public class SqlMessage
    {
        public string MsgType { get; set; }
        public string Script { get; set; }

        public SqlMessage(string msgType, string script)
        {
            MsgType = msgType;
            Script = script;
        }
    }
    public class SqlMessageWithObj
    {
        public string MsgType { get; set; }
        public string OracleScript { get; set; }
        public EPrice ePrice { get; set; }
        public EPriceRecovery ePriceRecovery { get; set; }  

        public SqlMessageWithObj(string msgType, string oracleScript, EPrice obj_msgX, EPriceRecovery obj_msgW)
        {
            this.MsgType = msgType;
            this.OracleScript = oracleScript;
            this.ePrice = obj_msgX;
            this.ePriceRecovery = obj_msgW;
        }
    }
    public class ProcessMessageResult
    {
        public EBulkScript Script { get; set; }
        public EPrice obj_X { get; set; }
        public EPriceRecovery obj_W {  get; set; }  
        public long MsgSeqNum { get; set; }
    }
    public class SequenceGapInfo
    {
        public long OldSequence { get; set; }
        public long NewSequence { get; set; }
        public List<long> MissingSequences { get; set; } = new List<long>();
    }
}
