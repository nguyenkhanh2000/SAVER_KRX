﻿using CommonLib.Interfaces;
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
        private readonly Dictionary<string,Dictionary<string,long>> dic_preSeq = new Dictionary<string,Dictionary<string,long>>();
        // Dic lưu danh sách các sequence bị thiếu theo msgType
        private readonly Dictionary<string,Dictionary<string,SequenceGapInfo>> dic_missSeq = new Dictionary<string, Dictionary<string, SequenceGapInfo>>();
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
                List<EPrice> lst_eP = new List<EPrice>();
                List<EPriceRecovery> lst_ePRecovery = new List<EPriceRecovery>();
                var oracleScriptsByType = new Dictionary<string, List<string>>();
                var ScriptOracle = new List<string>();

                var oracleBeginBlock = EGlobalConfig.__STRING_ORACLE_BLOCK_BEGIN;
                var oracleCommit = EGlobalConfig.__STRING_ORACLE_COMMIT;
                var oracleEndBlock = EGlobalConfig.__STRING_ORACLE_BLOCK_END;
                var oracleNewLineTab = $"{EGlobalConfig.__STRING_RETURN_NEW_LINE}{EGlobalConfig.__STRING_TAB}{EGlobalConfig.__STRING_SPACE}";

                var SW_RD = Stopwatch.StartNew();

                foreach (string msg in arrMsg)
                {
                    string msgType = this._app.Common.GetMsgType(msg);
                    ProcessMessageResult processMessageResult = ProcessMessage(msgType, msg).GetAwaiter().GetResult();
                    if (processMessageResult == null) continue;

                    long currentSeq = processMessageResult.MsgSeqNum;
                    string groupMsgType = marketDataTypes.Contains(msgType) ? "MarketData" : msgType;
                    string strExchange = processMessageResult.strExchange;
                    // Nếu là DVX thì bổ sung thêm "MX" vào danh sách MarketData
                    if (strExchange == "DVX" && !marketDataTypes.Contains("MX"))
                    {
                        marketDataTypes.Add("MX");
                    }

                    if (processMessageResult.obj_X != null)
                    {
                        lst_eP.Add(processMessageResult.obj_X);
                        continue;
                    }

                    if (processMessageResult.obj_W != null)
                    {
                        lst_ePRecovery.Add(processMessageResult.obj_W);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(processMessageResult.Script.OracleScript))
                    {
                        if (!oracleScriptsByType.TryGetValue(msgType, out var oracleList))
                        {
                            oracleList = new List<string>();
                            oracleScriptsByType[msgType] = oracleList;
                        }
                        oracleList.Add(processMessageResult.Script.OracleScript);
                    }

                    //Xử lý log sequence bị miss
                    if (currentSeq == 0 || strExchange == null) continue;
                    try
                    {
                        if (msgType != "M1")
                        {
                            // Khởi tạo dictionary nếu chưa có
                            if (!dic_preSeq.ContainsKey(strExchange))
                                dic_preSeq[strExchange] = new Dictionary<string, long>();

                            if (!dic_preSeq[strExchange].TryGetValue(groupMsgType, out long lastSeq))
                            {
                                // Lần đầu tiên thấy groupMsgType này
                                dic_preSeq[strExchange][groupMsgType] = currentSeq;
                            }
                            else
                            {
                                if (currentSeq > lastSeq + 1)
                                {
                                    var gapInfo = new SequenceGapInfo
                                    {
                                        OldSequence = lastSeq,
                                        NewSequence = currentSeq
                                    };

                                    for (long missing = lastSeq + 1; missing < currentSeq; missing++)
                                    {
                                        gapInfo.MissingSequences.Add(missing);
                                        this._app.SqlLogger.LogSciptSQL(
                                            $"SEQ_MISS_{strExchange}_{groupMsgType}",
                                            $"Missing Sequence: {missing} (Old: {lastSeq}, New: {currentSeq})"
                                        );
                                    }

                                    if (!dic_missSeq.ContainsKey(strExchange))
                                        dic_missSeq[strExchange] = new Dictionary<string, SequenceGapInfo>();

                                    dic_missSeq[strExchange][groupMsgType] = gapInfo;
                                }

                                dic_preSeq[strExchange][groupMsgType] = currentSeq;
                            }
                        }
                    }
                    catch (Exception ex) 
                    {
                        throw;
                    }                                       
                }

                foreach (var (msgTypes, scripts) in oracleScriptsByType)
                {
                    var oracleBatchBuilder = new StringBuilder(oracleBeginBlock);
                    foreach (var script in scripts)
                    {
                        oracleBatchBuilder.Append(oracleNewLineTab).Append(script);
                    }
                    oracleBatchBuilder.Append(oracleCommit).Append(oracleEndBlock);
                    ScriptOracle.Add(oracleBatchBuilder.ToString());
                }

                var parallelTasks = new List<Task>();

                if (dic_missSeq.Count > 0)
                {
                    parallelTasks.Add(Proc_MissSeq(dic_missSeq));
                    dic_missSeq.Clear();
                }

                if (lst_eP.Count > 0)
                {
                    parallelTasks.Add(Oracle_BulkUpdate_msgX(lst_eP));
                    parallelTasks.Add(Oracle_BulkIns_msgX(lst_eP));
                }

                if (lst_ePRecovery.Count > 0)
                {
                    parallelTasks.Add(Oracle_BulkUpdate_msgW(lst_ePRecovery));
                    parallelTasks.Add(Oracle_BulkIns_msgW(lst_ePRecovery));
                }

                if (ScriptOracle.Any())
                {
                    parallelTasks.Add(this._repository.ExecBulkScript(ScriptOracle));
                }

                await Task.WhenAll(parallelTasks);

                this._monitor.SendStatusToMonitor(
                    this._app.Common.GetLocalDateTime(),
                    this._app.Common.GetLocalIp(),
                    CMonitor.MONITOR_APP.HNX_Saver5G_DB,
                    arrMsg.Length,
                    SW_RD.ElapsedMilliseconds
                );

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

        //public async Task<bool> BuildScriptSQL(string[] arrMsg)
        //{
        //    await _semaphore.WaitAsync();
        //    try
        //    {
        //        List<EPrice> lst_eP = new List<EPrice>();
        //        List<EPriceRecovery> lst_ePRecovery = new List<EPriceRecovery>();
        //        var oracleScriptsByType = new Dictionary<string, List<string>>();
        //        var ScriptOracle = new List<string>();

        //        var oracleBeginBlock = EGlobalConfig.__STRING_ORACLE_BLOCK_BEGIN;
        //        var oracleCommit = EGlobalConfig.__STRING_ORACLE_COMMIT;
        //        var oracleEndBlock = EGlobalConfig.__STRING_ORACLE_BLOCK_END;
        //        var oracleNewLineTab = $"{EGlobalConfig.__STRING_RETURN_NEW_LINE}{EGlobalConfig.__STRING_TAB}{EGlobalConfig.__STRING_SPACE}";
        //        var SW_RD = Stopwatch.StartNew();
        //        // Duyệt từng tin nhắn và nhóm theo msgType
        //        foreach (string msg in arrMsg)
        //        {
        //            //Log Dequeue
        //            //this._app.InfoLogger.LogInfo(msg);
        //            string msgType = this._app.Common.GetMsgType(msg);

        //            //var eBulkScript = await ProcessMessage(msgType, msg);
        //            ProcessMessageResult processMessageResult = ProcessMessage(msgType, msg).GetAwaiter().GetResult();

        //            if (processMessageResult == null) continue;

        //            string groupMsgType = marketDataTypes.Contains(msgType) ? "MarketData" : msgType;
        //            //So sanh seq new >< old
        //            long currentSeq = processMessageResult.MsgSeqNum;

        //            if(msgType != "M1")
        //            {
        //                if (dic_preSeq.TryGetValue(groupMsgType, out long lastSeq))
        //                {
        //                    if (currentSeq > lastSeq + 1)
        //                    {
        //                        var gapInfo = new SequenceGapInfo
        //                        {
        //                            OldSequence = lastSeq,
        //                            NewSequence = currentSeq
        //                        };
        //                        for (long missing = lastSeq + 1; missing < currentSeq; missing++)
        //                        {
        //                            gapInfo.MissingSequences.Add(missing);

        //                            this._app.SqlLogger.LogSciptSQL($"AAA_Oracle_{groupMsgType}", $"Check_Sequence: OldSequence: {lastSeq} - MissingSequence: {missing}");
        //                        }
        //                        dic_missSeq[groupMsgType] = gapInfo;
        //                    }
        //                    // Sau khi xử lý missing sequence, cập nhật lại sequence mới nhất
        //                    dic_preSeq[groupMsgType] = currentSeq;
        //                }
        //                else
        //                {
        //                    // Lần đầu thấy groupMsgType này, khởi tạo sequence
        //                    dic_preSeq[groupMsgType] = currentSeq;
        //                }
        //            }

        //            //thêm vào list_msgX
        //            if (processMessageResult.obj_X != null) 
        //            {
        //                lst_eP.Add(processMessageResult.obj_X);
        //                continue;
        //            }
        //            //thêm vào list_msgW
        //            if (processMessageResult.obj_W != null)
        //            {
        //                lst_ePRecovery.Add(processMessageResult.obj_W);
        //                continue;
        //            }

        //            if (!string.IsNullOrEmpty(processMessageResult.Script.OracleScript))
        //            {
        //                if (!oracleScriptsByType.TryGetValue(msgType, out var oracleList))
        //                {
        //                    oracleList = new List<string>();
        //                    oracleScriptsByType[msgType] = oracleList;
        //                }
        //                oracleList.Add(processMessageResult.Script.OracleScript);
        //            }
        //        }

        //        // Tạo batch script cho Oracle
        //        foreach (var (msgTypes, scripts) in oracleScriptsByType)
        //        {
        //            var oracleBatchBuilder = new StringBuilder(oracleBeginBlock);
        //            foreach (var script in scripts)
        //            {
        //                oracleBatchBuilder.Append(oracleNewLineTab).Append(script);
        //            }
        //            oracleBatchBuilder.Append(oracleCommit).Append(oracleEndBlock);

        //            //this._app.SqlLogger.LogSql(oracleBatchBuilder.ToString());
        //            ScriptOracle.Add(oracleBatchBuilder.ToString());
        //            // Ghi log Oracle các nhóm khác
        //            //this._app.SqlLogger.LogSciptSQL($"Oracle_{msgTypes}", oracleBatchBuilder.ToString());

        //            //Ghi log count
        //            //this._app.SqlLogger.LogSciptSQL($"Oracle_{msgTypes}", $"{oracleBatchBuilder.ToString().Length}");
        //        }
        //        //Hàm xử lý insert rớt sequence
        //        if (dic_missSeq.Count > 0)
        //        {
        //            await Proc_MissSeq(dic_missSeq);
        //            //clear dic 
        //            dic_missSeq.Clear();    
        //        }       
        //        if (lst_eP.Count > 0)
        //        {
        //            var updateTask = Oracle_BulkUpdate_msgX(lst_eP);
        //            var insertTask = Oracle_BulkIns_msgX(lst_eP);

        //            await Task.WhenAll(updateTask, insertTask);
        //        }
        //        if (lst_ePRecovery.Count > 0)
        //        {
        //            var updateTask = Oracle_BulkUpdate_msgW(lst_ePRecovery);
        //            var insertTask = Oracle_BulkIns_msgW(lst_ePRecovery);

        //            await Task.WhenAll(updateTask, insertTask);
        //        }

        //        // Thực thi batch scripts
        //        if (ScriptOracle.Any())
        //        {
        //            await this._repository.ExecBulkScript(ScriptOracle);

        //            this._monitor.SendStatusToMonitor(
        //                this._app.Common.GetLocalDateTime(),
        //                this._app.Common.GetLocalIp(),
        //                CMonitor.MONITOR_APP.HSX_Saver5G,
        //                arrMsg.Length,
        //                SW_RD.ElapsedMilliseconds
        //            );
        //        }

        //        return true;
        //    }
        //    catch (Exception ex)
        //    {
        //        this._app.ErrorLogger.LogError(ex);
        //        return false;
        //    }
        //    finally
        //    {
        //        _semaphore.Release();
        //    }
        //}
        public async Task Proc_MissSeq(Dictionary<string, Dictionary<string, SequenceGapInfo>> dic_Seq)
        {
            try
            {
                var missingLogList = new List<CMsgSeq>();

                foreach (var exchangePair in dic_Seq)
                {
                    string exchange = exchangePair.Key;
                    var msgTypeDict = exchangePair.Value;

                    foreach (var msgTypePair in msgTypeDict)
                    {
                        string msgType = msgTypePair.Key;
                        var info = msgTypePair.Value;

                        foreach (var seq in info.MissingSequences)
                        {
                            missingLogList.Add(new CMsgSeq
                            {
                                Exchange = exchange,
                                MsgType = msgType,
                                SeqMiss = seq,
                                SeqOld = info.OldSequence,
                                SeqNew = info.NewSequence,
                                Time = DateTime.Now
                            });
                        }
                    }
                }

                using (var connection = new OracleConnection(this._priceConfig.ConnectionOracle))
                {
                    connection.Open();

                    using (var bulkCopy = new OracleBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = "MISSING_SEQUENCE_LOG";

                        var table = new DataTable();
                        table.Columns.Add("EXCHANGE", typeof(string));
                        table.Columns.Add("MSGTYPE", typeof(string));
                        table.Columns.Add("SEQMISS", typeof(long));
                        table.Columns.Add("SEQOLD", typeof(long));
                        table.Columns.Add("SEQNEW", typeof(long));
                        table.Columns.Add("TIME", typeof(DateTime));

                        foreach (var item in missingLogList)
                        {
                            table.Rows.Add(item.Exchange, item.MsgType, item.SeqMiss, item.SeqOld, item.SeqNew, item.Time);
                        }

                        bulkCopy.WriteToServer(table);
                    }
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }

        public async Task Oracle_BulkUpdate_msgX(List<EPrice> lst_eP)
        {
            try
            {
                // Gộp dữ liệu theo Symbol, MarketID, BoardID: giữ object cuối cùng
                var groupedDict = new Dictionary<string, EPrice>();
                foreach (var item in lst_eP)
                {
                    string key = $"{item.Symbol}|{item.MarketID}|{item.BoardID}";

                    if (!groupedDict.ContainsKey(key))
                    {
                        groupedDict[key] = item;
                    }
                    else
                    {
                        var existing = groupedDict[key];
                        // Merge từng trường: nếu trường mới != "-9999999" thì cập nhật
                        existing.BeginString = item.BeginString ?? existing.BeginString;
                        existing.BodyLength = item.BodyLength != 0 ? item.BodyLength : existing.BodyLength;
                        existing.MsgType = item.MsgType ?? existing.MsgType;
                        existing.SenderCompID = item.SenderCompID ?? existing.SenderCompID;
                        existing.TargetCompID = item.TargetCompID ?? existing.TargetCompID;
                        existing.MsgSeqNum = item.MsgSeqNum != 0 ? item.MsgSeqNum : existing.MsgSeqNum;
                        existing.SendingTime = item.SendingTime ?? existing.SendingTime;
                        existing.MarketID = item.MarketID;
                        existing.BoardID = item.BoardID;
                        existing.TradingSessionID = item.TradingSessionID ?? existing.TradingSessionID;
                        existing.Symbol = item.Symbol;
                        existing.TradeDate = item.TradeDate ?? existing.TradeDate;
                        existing.TransactTime = item.TransactTime ?? existing.TransactTime;

                        existing.TotalVolumeTraded = item.TotalVolumeTraded != -9999999 ? item.TotalVolumeTraded : existing.TotalVolumeTraded;
                        existing.GrossTradeAmt = item.GrossTradeAmt != 0 ? item.GrossTradeAmt : existing.GrossTradeAmt;
                        existing.BuyTotOrderQty = item.BuyTotOrderQty != -9999999 ? item.BuyTotOrderQty : existing.BuyTotOrderQty;
                        existing.BuyValidOrderCnt = item.BuyValidOrderCnt != -9999999 ? item.BuyValidOrderCnt : existing.BuyValidOrderCnt;
                        existing.SellTotOrderQty = item.SellTotOrderQty != -9999999 ? item.SellTotOrderQty : existing.SellTotOrderQty;
                        existing.SellValidOrderCnt = item.SellValidOrderCnt != -9999999 ? item.SellValidOrderCnt : existing.SellValidOrderCnt;
                        existing.NoMDEntries = item.NoMDEntries != -9999999 ? item.NoMDEntries : existing.NoMDEntries;

                        existing.BuyPrice1 = item.BuyPrice1 != -9999999 ? item.BuyPrice1 : existing.BuyPrice1;
                        existing.BuyQuantity1 = item.BuyQuantity1 != -9999999 ? item.BuyQuantity1 : existing.BuyQuantity1;
                        existing.BuyPrice1_NOO = item.BuyPrice1_NOO != -9999999 ? item.BuyPrice1_NOO : existing.BuyPrice1_NOO;
                        existing.BuyPrice1_MDEY = item.BuyPrice1_MDEY != -9999999 ? item.BuyPrice1_MDEY : existing.BuyPrice1_MDEY;
                        existing.BuyPrice1_MDEMMS = item.BuyPrice1_MDEMMS != -9999999 ? item.BuyPrice1_MDEMMS : existing.BuyPrice1_MDEMMS;
                        existing.BuyPrice2 = item.BuyPrice2 != -9999999 ? item.BuyPrice2 : existing.BuyPrice2;
                        existing.BuyQuantity2 = item.BuyQuantity2 != -9999999 ? item.BuyQuantity2 : existing.BuyQuantity2;
                        existing.BuyPrice2_NOO = item.BuyPrice2_NOO != -9999999 ? item.BuyPrice2_NOO : existing.BuyPrice2_NOO;
                        existing.BuyPrice2_MDEY = item.BuyPrice2_MDEY != -9999999 ? item.BuyPrice2_MDEY : existing.BuyPrice2_MDEY;
                        existing.BuyPrice2_MDEMMS = item.BuyPrice2_MDEMMS != -9999999 ? item.BuyPrice2_MDEMMS : existing.BuyPrice2_MDEMMS;
                        existing.BuyPrice3 = item.BuyPrice3 != -9999999 ? item.BuyPrice3 : existing.BuyPrice3;
                        existing.BuyQuantity3 = item.BuyQuantity3 != -9999999 ? item.BuyQuantity3 : existing.BuyQuantity3;
                        existing.BuyPrice3_NOO = item.BuyPrice3_NOO != -9999999 ? item.BuyPrice3_NOO : existing.BuyPrice3_NOO;
                        existing.BuyPrice3_MDEY = item.BuyPrice3_MDEY != -9999999 ? item.BuyPrice3_MDEY : existing.BuyPrice3_MDEY;
                        existing.BuyPrice3_MDEMMS = item.BuyPrice3_MDEMMS != -9999999 ? item.BuyPrice3_MDEMMS : existing.BuyPrice3_MDEMMS;
                        existing.BuyPrice4 = item.BuyPrice4 != -9999999 ? item.BuyPrice4 : existing.BuyPrice4;
                        existing.BuyQuantity4 = item.BuyQuantity4 != -9999999 ? item.BuyQuantity4 : existing.BuyQuantity4;
                        existing.BuyPrice4_NOO = item.BuyPrice4_NOO != -9999999 ? item.BuyPrice4_NOO : existing.BuyPrice4_NOO;
                        existing.BuyPrice4_MDEY = item.BuyPrice4_MDEY != -9999999 ? item.BuyPrice4_MDEY : existing.BuyPrice4_MDEY;
                        existing.BuyPrice4_MDEMMS = item.BuyPrice4_MDEMMS != -9999999 ? item.BuyPrice4_MDEMMS : existing.BuyPrice4_MDEMMS;
                        existing.BuyPrice5 = item.BuyPrice5 != -9999999 ? item.BuyPrice5 : existing.BuyPrice5;
                        existing.BuyQuantity5 = item.BuyQuantity5 != -9999999 ? item.BuyQuantity5 : existing.BuyQuantity5;
                        existing.BuyPrice5_NOO = item.BuyPrice5_NOO != -9999999 ? item.BuyPrice5_NOO : existing.BuyPrice5_NOO;
                        existing.BuyPrice5_MDEY = item.BuyPrice5_MDEY != -9999999 ? item.BuyPrice5_MDEY : existing.BuyPrice5_MDEY;
                        existing.BuyPrice5_MDEMMS = item.BuyPrice5_MDEMMS != -9999999 ? item.BuyPrice5_MDEMMS : existing.BuyPrice5_MDEMMS;
                        existing.BuyPrice6 = item.BuyPrice6 != -9999999 ? item.BuyPrice6 : existing.BuyPrice6;
                        existing.BuyQuantity6 = item.BuyQuantity6 != -9999999 ? item.BuyQuantity6 : existing.BuyQuantity6;
                        existing.BuyPrice6_NOO = item.BuyPrice6_NOO != -9999999 ? item.BuyPrice6_NOO : existing.BuyPrice6_NOO;
                        existing.BuyPrice6_MDEY = item.BuyPrice6_MDEY != -9999999 ? item.BuyPrice6_MDEY : existing.BuyPrice6_MDEY;
                        existing.BuyPrice6_MDEMMS = item.BuyPrice6_MDEMMS != -9999999 ? item.BuyPrice6_MDEMMS : existing.BuyPrice6_MDEMMS;
                        existing.BuyPrice7 = item.BuyPrice7 != -9999999 ? item.BuyPrice7 : existing.BuyPrice7;
                        existing.BuyQuantity7 = item.BuyQuantity7 != -9999999 ? item.BuyQuantity7 : existing.BuyQuantity7;
                        existing.BuyPrice7_NOO = item.BuyPrice7_NOO != -9999999 ? item.BuyPrice7_NOO : existing.BuyPrice7_NOO;
                        existing.BuyPrice7_MDEY = item.BuyPrice7_MDEY != -9999999 ? item.BuyPrice7_MDEY : existing.BuyPrice7_MDEY;
                        existing.BuyPrice7_MDEMMS = item.BuyPrice7_MDEMMS != -9999999 ? item.BuyPrice7_MDEMMS : existing.BuyPrice7_MDEMMS;
                        existing.BuyPrice8 = item.BuyPrice8 != -9999999 ? item.BuyPrice8 : existing.BuyPrice8;
                        existing.BuyQuantity8 = item.BuyQuantity8 != -9999999 ? item.BuyQuantity8 : existing.BuyQuantity8;
                        existing.BuyPrice8_NOO = item.BuyPrice8_NOO != -9999999 ? item.BuyPrice8_NOO : existing.BuyPrice8_NOO;
                        existing.BuyPrice8_MDEY = item.BuyPrice8_MDEY != -9999999 ? item.BuyPrice8_MDEY : existing.BuyPrice8_MDEY;
                        existing.BuyPrice8_MDEMMS = item.BuyPrice8_MDEMMS != -9999999 ? item.BuyPrice8_MDEMMS : existing.BuyPrice8_MDEMMS;
                        existing.BuyPrice9 = item.BuyPrice9 != -9999999 ? item.BuyPrice9 : existing.BuyPrice9;
                        existing.BuyQuantity9 = item.BuyQuantity9 != -9999999 ? item.BuyQuantity9 : existing.BuyQuantity9;
                        existing.BuyPrice9_NOO = item.BuyPrice9_NOO != -9999999 ? item.BuyPrice9_NOO : existing.BuyPrice9_NOO;
                        existing.BuyPrice9_MDEY = item.BuyPrice9_MDEY != -9999999 ? item.BuyPrice9_MDEY : existing.BuyPrice9_MDEY;
                        existing.BuyPrice9_MDEMMS = item.BuyPrice9_MDEMMS != -9999999 ? item.BuyPrice9_MDEMMS : existing.BuyPrice9_MDEMMS;
                        existing.BuyPrice10 = item.BuyPrice10 != -9999999 ? item.BuyPrice10 : existing.BuyPrice10;
                        existing.BuyQuantity10 = item.BuyQuantity10 != -9999999 ? item.BuyQuantity10 : existing.BuyQuantity10;
                        existing.BuyPrice10_NOO = item.BuyPrice10_NOO != -9999999 ? item.BuyPrice10_NOO : existing.BuyPrice10_NOO;
                        existing.BuyPrice10_MDEY = item.BuyPrice10_MDEY != -9999999 ? item.BuyPrice10_MDEY : existing.BuyPrice10_MDEY;
                        existing.BuyPrice10_MDEMMS = item.BuyPrice10_MDEMMS != -9999999 ? item.BuyPrice10_MDEMMS : existing.BuyPrice10_MDEMMS;

                        existing.SellPrice1 = item.SellPrice1 != -9999999 ? item.SellPrice1 : existing.SellPrice1;
                        existing.SellQuantity1 = item.SellQuantity1 != -9999999 ? item.SellQuantity1 : existing.SellQuantity1;
                        existing.SellPrice1_NOO = item.SellPrice1_NOO != -9999999 ? item.SellPrice1_NOO : existing.SellPrice1_NOO;
                        existing.SellPrice1_MDEY = item.SellPrice1_MDEY != -9999999 ? item.SellPrice1_MDEY : existing.SellPrice1_MDEY;
                        existing.SellPrice1_MDEMMS = item.SellPrice1_MDEMMS != -9999999 ? item.SellPrice1_MDEMMS : existing.SellPrice1_MDEMMS;
                        existing.SellPrice2 = item.SellPrice2 != -9999999 ? item.SellPrice2 : existing.SellPrice2;
                        existing.SellQuantity2 = item.SellQuantity2 != -9999999 ? item.SellQuantity2 : existing.SellQuantity2;
                        existing.SellPrice2_NOO = item.SellPrice2_NOO != -9999999 ? item.SellPrice2_NOO : existing.SellPrice2_NOO;
                        existing.SellPrice2_MDEY = item.SellPrice2_MDEY != -9999999 ? item.SellPrice2_MDEY : existing.SellPrice2_MDEY;
                        existing.SellPrice2_MDEMMS = item.SellPrice2_MDEMMS != -9999999 ? item.SellPrice2_MDEMMS : existing.SellPrice2_MDEMMS;
                        existing.SellPrice3 = item.SellPrice3 != -9999999 ? item.SellPrice3 : existing.SellPrice3;
                        existing.SellQuantity3 = item.SellQuantity3 != -9999999 ? item.SellQuantity3 : existing.SellQuantity3;
                        existing.SellPrice3_NOO = item.SellPrice3_NOO != -9999999 ? item.SellPrice3_NOO : existing.SellPrice3_NOO;
                        existing.SellPrice3_MDEY = item.SellPrice3_MDEY != -9999999 ? item.SellPrice3_MDEY : existing.SellPrice3_MDEY;
                        existing.SellPrice3_MDEMMS = item.SellPrice3_MDEMMS != -9999999 ? item.SellPrice3_MDEMMS : existing.SellPrice3_MDEMMS;
                        existing.SellPrice4 = item.SellPrice4 != -9999999 ? item.SellPrice4 : existing.SellPrice4;
                        existing.SellQuantity4 = item.SellQuantity4 != -9999999 ? item.SellQuantity4 : existing.SellQuantity4;
                        existing.SellPrice4_NOO = item.SellPrice4_NOO != -9999999 ? item.SellPrice4_NOO : existing.SellPrice4_NOO;
                        existing.SellPrice4_MDEY = item.SellPrice4_MDEY != -9999999 ? item.SellPrice4_MDEY : existing.SellPrice4_MDEY;
                        existing.SellPrice4_MDEMMS = item.SellPrice4_MDEMMS != -9999999 ? item.SellPrice4_MDEMMS : existing.SellPrice4_MDEMMS;
                        existing.SellPrice5 = item.SellPrice5 != -9999999 ? item.SellPrice5 : existing.SellPrice5;
                        existing.SellQuantity5 = item.SellQuantity5 != -9999999 ? item.SellQuantity5 : existing.SellQuantity5;
                        existing.SellPrice5_NOO = item.SellPrice5_NOO != -9999999 ? item.SellPrice5_NOO : existing.SellPrice5_NOO;
                        existing.SellPrice5_MDEY = item.SellPrice5_MDEY != -9999999 ? item.SellPrice5_MDEY : existing.SellPrice5_MDEY;
                        existing.SellPrice5_MDEMMS = item.SellPrice5_MDEMMS != -9999999 ? item.SellPrice5_MDEMMS : existing.SellPrice5_MDEMMS;
                        existing.SellPrice6 = item.SellPrice6 != -9999999 ? item.SellPrice6 : existing.SellPrice6;
                        existing.SellQuantity6 = item.SellQuantity6 != -9999999 ? item.SellQuantity6 : existing.SellQuantity6;
                        existing.SellPrice6_NOO = item.SellPrice6_NOO != -9999999 ? item.SellPrice6_NOO : existing.SellPrice6_NOO;
                        existing.SellPrice6_MDEY = item.SellPrice6_MDEY != -9999999 ? item.SellPrice6_MDEY : existing.SellPrice6_MDEY;
                        existing.SellPrice6_MDEMMS = item.SellPrice6_MDEMMS != -9999999 ? item.SellPrice6_MDEMMS : existing.SellPrice6_MDEMMS;
                        existing.SellPrice7 = item.SellPrice7 != -9999999 ? item.SellPrice7 : existing.SellPrice7;
                        existing.SellQuantity7 = item.SellQuantity7 != -9999999 ? item.SellQuantity7 : existing.SellQuantity7;
                        existing.SellPrice7_NOO = item.SellPrice7_NOO != -9999999 ? item.SellPrice7_NOO : existing.SellPrice7_NOO;
                        existing.SellPrice7_MDEY = item.SellPrice7_MDEY != -9999999 ? item.SellPrice7_MDEY : existing.SellPrice7_MDEY;
                        existing.SellPrice7_MDEMMS = item.SellPrice7_MDEMMS != -9999999 ? item.SellPrice7_MDEMMS : existing.SellPrice7_MDEMMS;
                        existing.SellPrice8 = item.SellPrice8 != -9999999 ? item.SellPrice8 : existing.SellPrice8;
                        existing.SellQuantity8 = item.SellQuantity8 != -9999999 ? item.SellQuantity8 : existing.SellQuantity8;
                        existing.SellPrice8_NOO = item.SellPrice8_NOO != -9999999 ? item.SellPrice8_NOO : existing.SellPrice8_NOO;
                        existing.SellPrice8_MDEY = item.SellPrice8_MDEY != -9999999 ? item.SellPrice8_MDEY : existing.SellPrice8_MDEY;
                        existing.SellPrice8_MDEMMS = item.SellPrice8_MDEMMS != -9999999 ? item.SellPrice8_MDEMMS : existing.SellPrice8_MDEMMS;
                        existing.SellPrice9 = item.SellPrice9 != -9999999 ? item.SellPrice9 : existing.SellPrice9;
                        existing.SellQuantity9 = item.SellQuantity9 != -9999999 ? item.SellQuantity9 : existing.SellQuantity9;
                        existing.SellPrice9_NOO = item.SellPrice9_NOO != -9999999 ? item.SellPrice9_NOO : existing.SellPrice9_NOO;
                        existing.SellPrice9_MDEY = item.SellPrice9_MDEY != -9999999 ? item.SellPrice9_MDEY : existing.SellPrice9_MDEY;
                        existing.SellPrice9_MDEMMS = item.SellPrice9_MDEMMS != -9999999 ? item.SellPrice9_MDEMMS : existing.SellPrice9_MDEMMS;
                        existing.SellPrice10 = item.SellPrice10 != -9999999 ? item.SellPrice10 : existing.SellPrice10;
                        existing.SellQuantity10 = item.SellQuantity10 != -9999999 ? item.SellQuantity10 : existing.SellQuantity10;
                        existing.SellPrice10_NOO = item.SellPrice10_NOO != -9999999 ? item.SellPrice10_NOO : existing.SellPrice10_NOO;
                        existing.SellPrice10_MDEY = item.SellPrice10_MDEY != -9999999 ? item.SellPrice10_MDEY : existing.SellPrice10_MDEY;
                        existing.SellPrice10_MDEMMS = item.SellPrice10_MDEMMS != -9999999 ? item.SellPrice10_MDEMMS : existing.SellPrice10_MDEMMS;
                        existing.MatchPrice = item.MatchPrice != -9999999 ? item.MatchPrice : existing.MatchPrice;
                        existing.MatchQuantity = item.MatchQuantity != -9999999 ? item.MatchQuantity : existing.MatchQuantity;
                        existing.OpenPrice = item.OpenPrice != -9999999 ? item.OpenPrice : existing.OpenPrice;
                        existing.ClosePrice = item.ClosePrice != -9999999 ? item.ClosePrice : existing.ClosePrice;
                        existing.HighestPrice = item.HighestPrice != -9999999 ? item.HighestPrice : existing.HighestPrice;
                        existing.LowestPrice = item.LowestPrice != -9999999 ? item.LowestPrice : existing.LowestPrice;
                        existing.CheckSum = item.CheckSum ?? existing.CheckSum;
                    }
                    //groupedDict[key] = item; // Nếu đã tồn tại, object sau sẽ ghi đè object trước
                }
                var distinctList = groupedDict.Values.ToList();

                DataTable dt = ConvertEPriceListToDataTable(distinctList);

                using (var conn = new OracleConnection(this._priceConfig.ConnectionOracle))
                {
                    conn.Open();

                    // Bắt đầu transaction để đảm bảo tính toàn vẹn dữ liệu
                    using (var transaction = conn.BeginTransaction())
                    {
                        try
                        {
                            // Sử dụng OracleBulkCopy để insert dữ liệu vào bảng tạm
                            try
                            {
                                using (var bulkCopy = new OracleBulkCopy(conn))
                                {
                                    bulkCopy.DestinationTableName = "table_temporary_X";
                                    bulkCopy.BatchSize = 5000;

                                    foreach (DataColumn col in dt.Columns)
                                    {
                                        bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                                    }

                                    bulkCopy.WriteToServer(dt);
                                }
                                Console.WriteLine("Bulk insert thành công.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Bulk insert lỗi: " + ex.Message);
                            }
                            using (OracleCommand checkCmd = new OracleCommand("SELECT COUNT(*) FROM table_temporary_X", conn))
                            {
                                checkCmd.Transaction = transaction;
                                var count = Convert.ToInt32(checkCmd.ExecuteScalar());
                                Console.WriteLine("Số dòng trong bảng tạm: " + count);
                            }
                            // Gọi stored procedure để insert/update dữ liệu vào bảng chính
                            using (OracleCommand cmdProc = new OracleCommand("PROC_MERGE_MSG_X", conn))
                            {
                                cmdProc.CommandType = System.Data.CommandType.StoredProcedure;

                                // Thêm các tham số nếu cần thiết
                                //cmdProc.Parameters.Add("preturnMess", OracleDbType.Varchar2).Value = value1;
                                OracleParameter outputParam = new OracleParameter("v_err_msg", OracleDbType.Varchar2, 1000);
                                outputParam.Direction = ParameterDirection.Output;
                                cmdProc.Parameters.Add(outputParam);

                                cmdProc.Transaction = transaction; // Thiết lập transaction cho stored procedure
                                cmdProc.ExecuteNonQuery(); // Thực thi SP
                            }
                            // Xóa dữ liệu trong bảng tạm sau khi xử lý xong
                            using (OracleCommand cmdTruncate = new OracleCommand("TRUNCATE TABLE table_temporary_X", conn))
                            {
                                cmdTruncate.Transaction = transaction;
                                cmdTruncate.ExecuteNonQuery();
                            }
                            using (OracleCommand checkCmd = new OracleCommand("SELECT COUNT(*) FROM table_temporary_X", conn))
                            {
                                checkCmd.Transaction = transaction;
                                var count = Convert.ToInt32(checkCmd.ExecuteScalar());
                                Console.WriteLine("Số dòng trong bảng tạm: " + count);
                            }
                            // Commit transaction sau khi tất cả các thao tác thành công
                            transaction.Commit();
                        }
                        catch (Exception ex)
                        {
                            // Nếu có lỗi, rollback transaction và in ra thông báo lỗi
                            transaction.Rollback();
                            Console.WriteLine("Error: " + ex.Message);
                            this._app.ErrorLogger.LogError(ex);
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task Oracle_BulkIns_msgX(List<EPrice> lst_eP)
        {
            try
            {
                DataTable dt = ConvertEPriceListToDataTable(lst_eP);

                using (var conn = new OracleConnection(this._priceConfig.ConnectionOracle))
                {
                    conn.Open();
                    using (var bulkCopy = new OracleBulkCopy(conn))
                    {
                        bulkCopy.DestinationTableName = "tprice_intraday";
                        bulkCopy.BatchSize = 5000;

                        foreach (DataColumn col in dt.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                        }

                        bulkCopy.WriteToServer(dt);
                    }
                }
            }
            catch (Exception ex) 
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task Oracle_BulkUpdate_msgW(List<EPriceRecovery> lst_ePRecovery)
        {
            try
            {
                // Gộp dữ liệu theo Symbol, MarketID, BoardID: giữ object cuối cùng
                var groupedDict = new Dictionary<string, EPriceRecovery>();
                foreach (var item in lst_ePRecovery)
                {
                    string key = $"{item.Symbol}|{item.MarketID}|{item.BoardID}";

                    if (!groupedDict.ContainsKey(key))
                    {
                        groupedDict[key] = item;
                    }
                    else
                    {
                        var existing = groupedDict[key];
                        // Merge từng trường: nếu trường mới != "-9999999" thì cập nhật
                        existing.BeginString = item.BeginString ?? existing.BeginString;
                        existing.BodyLength = item.BodyLength != 0 ? item.BodyLength : existing.BodyLength;
                        existing.MsgType = item.MsgType ?? existing.MsgType;
                        existing.SenderCompID = item.SenderCompID ?? existing.SenderCompID;
                        existing.TargetCompID = item.TargetCompID ?? existing.TargetCompID;
                        existing.MsgSeqNum = item.MsgSeqNum != 0 ? item.MsgSeqNum : existing.MsgSeqNum;
                        existing.SendingTime = item.SendingTime ?? existing.SendingTime;
                        existing.MarketID = item.MarketID;
                        existing.BoardID = item.BoardID;
                        existing.TradingSessionID = item.TradingSessionID ?? existing.TradingSessionID;
                        existing.Symbol = item.Symbol;
                        existing.OpnPx = item.OpnPx != 0 ? item.OpnPx : existing.OpnPx;
                        existing.TrdSessnHighPx = item.TrdSessnHighPx != 0 ? item.TrdSessnHighPx : existing.TrdSessnHighPx;
                        existing.TrdSessnLowPx = item.TrdSessnLowPx != 0 ? item.TrdSessnLowPx : existing.TrdSessnLowPx;
                        existing.SymbolCloseInfoPx = item.SymbolCloseInfoPx != 0 ? item.SymbolCloseInfoPx : existing.SymbolCloseInfoPx;
                        //existing.TradeDate = item.TradeDate ?? existing.TradeDate;
                        //existing.TransactTime = item.TransactTime ?? existing.TransactTime;

                        existing.TotalVolumeTraded = item.TotalVolumeTraded != -9999999 ? item.TotalVolumeTraded : existing.TotalVolumeTraded;
                        existing.GrossTradeAmt = item.GrossTradeAmt != 0 ? item.GrossTradeAmt : existing.GrossTradeAmt;
                        existing.BuyTotOrderQty = item.BuyTotOrderQty != -9999999 ? item.BuyTotOrderQty : existing.BuyTotOrderQty;
                        existing.BuyValidOrderCnt = item.BuyValidOrderCnt != -9999999 ? item.BuyValidOrderCnt : existing.BuyValidOrderCnt;
                        existing.SellTotOrderQty = item.SellTotOrderQty != -9999999 ? item.SellTotOrderQty : existing.SellTotOrderQty;
                        existing.SellValidOrderCnt = item.SellValidOrderCnt != -9999999 ? item.SellValidOrderCnt : existing.SellValidOrderCnt;
                        existing.NoMDEntries = item.NoMDEntries != -9999999 ? item.NoMDEntries : existing.NoMDEntries;

                        existing.BuyPrice1 = item.BuyPrice1 != -9999999 ? item.BuyPrice1 : existing.BuyPrice1;
                        existing.BuyQuantity1 = item.BuyQuantity1 != -9999999 ? item.BuyQuantity1 : existing.BuyQuantity1;
                        existing.BuyPrice1_NOO = item.BuyPrice1_NOO != -9999999 ? item.BuyPrice1_NOO : existing.BuyPrice1_NOO;
                        existing.BuyPrice1_MDEY = item.BuyPrice1_MDEY != -9999999 ? item.BuyPrice1_MDEY : existing.BuyPrice1_MDEY;
                        existing.BuyPrice1_MDEMMS = item.BuyPrice1_MDEMMS != -9999999 ? item.BuyPrice1_MDEMMS : existing.BuyPrice1_MDEMMS;
                        existing.BuyPrice2 = item.BuyPrice2 != -9999999 ? item.BuyPrice2 : existing.BuyPrice2;
                        existing.BuyQuantity2 = item.BuyQuantity2 != -9999999 ? item.BuyQuantity2 : existing.BuyQuantity2;
                        existing.BuyPrice2_NOO = item.BuyPrice2_NOO != -9999999 ? item.BuyPrice2_NOO : existing.BuyPrice2_NOO;
                        existing.BuyPrice2_MDEY = item.BuyPrice2_MDEY != -9999999 ? item.BuyPrice2_MDEY : existing.BuyPrice2_MDEY;
                        existing.BuyPrice2_MDEMMS = item.BuyPrice2_MDEMMS != -9999999 ? item.BuyPrice2_MDEMMS : existing.BuyPrice2_MDEMMS;
                        existing.BuyPrice3 = item.BuyPrice3 != -9999999 ? item.BuyPrice3 : existing.BuyPrice3;
                        existing.BuyQuantity3 = item.BuyQuantity3 != -9999999 ? item.BuyQuantity3 : existing.BuyQuantity3;
                        existing.BuyPrice3_NOO = item.BuyPrice3_NOO != -9999999 ? item.BuyPrice3_NOO : existing.BuyPrice3_NOO;
                        existing.BuyPrice3_MDEY = item.BuyPrice3_MDEY != -9999999 ? item.BuyPrice3_MDEY : existing.BuyPrice3_MDEY;
                        existing.BuyPrice3_MDEMMS = item.BuyPrice3_MDEMMS != -9999999 ? item.BuyPrice3_MDEMMS : existing.BuyPrice3_MDEMMS;
                        existing.BuyPrice4 = item.BuyPrice4 != -9999999 ? item.BuyPrice4 : existing.BuyPrice4;
                        existing.BuyQuantity4 = item.BuyQuantity4 != -9999999 ? item.BuyQuantity4 : existing.BuyQuantity4;
                        existing.BuyPrice4_NOO = item.BuyPrice4_NOO != -9999999 ? item.BuyPrice4_NOO : existing.BuyPrice4_NOO;
                        existing.BuyPrice4_MDEY = item.BuyPrice4_MDEY != -9999999 ? item.BuyPrice4_MDEY : existing.BuyPrice4_MDEY;
                        existing.BuyPrice4_MDEMMS = item.BuyPrice4_MDEMMS != -9999999 ? item.BuyPrice4_MDEMMS : existing.BuyPrice4_MDEMMS;
                        existing.BuyPrice5 = item.BuyPrice5 != -9999999 ? item.BuyPrice5 : existing.BuyPrice5;
                        existing.BuyQuantity5 = item.BuyQuantity5 != -9999999 ? item.BuyQuantity5 : existing.BuyQuantity5;
                        existing.BuyPrice5_NOO = item.BuyPrice5_NOO != -9999999 ? item.BuyPrice5_NOO : existing.BuyPrice5_NOO;
                        existing.BuyPrice5_MDEY = item.BuyPrice5_MDEY != -9999999 ? item.BuyPrice5_MDEY : existing.BuyPrice5_MDEY;
                        existing.BuyPrice5_MDEMMS = item.BuyPrice5_MDEMMS != -9999999 ? item.BuyPrice5_MDEMMS : existing.BuyPrice5_MDEMMS;
                        existing.BuyPrice6 = item.BuyPrice6 != -9999999 ? item.BuyPrice6 : existing.BuyPrice6;
                        existing.BuyQuantity6 = item.BuyQuantity6 != -9999999 ? item.BuyQuantity6 : existing.BuyQuantity6;
                        existing.BuyPrice6_NOO = item.BuyPrice6_NOO != -9999999 ? item.BuyPrice6_NOO : existing.BuyPrice6_NOO;
                        existing.BuyPrice6_MDEY = item.BuyPrice6_MDEY != -9999999 ? item.BuyPrice6_MDEY : existing.BuyPrice6_MDEY;
                        existing.BuyPrice6_MDEMMS = item.BuyPrice6_MDEMMS != -9999999 ? item.BuyPrice6_MDEMMS : existing.BuyPrice6_MDEMMS;
                        existing.BuyPrice7 = item.BuyPrice7 != -9999999 ? item.BuyPrice7 : existing.BuyPrice7;
                        existing.BuyQuantity7 = item.BuyQuantity7 != -9999999 ? item.BuyQuantity7 : existing.BuyQuantity7;
                        existing.BuyPrice7_NOO = item.BuyPrice7_NOO != -9999999 ? item.BuyPrice7_NOO : existing.BuyPrice7_NOO;
                        existing.BuyPrice7_MDEY = item.BuyPrice7_MDEY != -9999999 ? item.BuyPrice7_MDEY : existing.BuyPrice7_MDEY;
                        existing.BuyPrice7_MDEMMS = item.BuyPrice7_MDEMMS != -9999999 ? item.BuyPrice7_MDEMMS : existing.BuyPrice7_MDEMMS;
                        existing.BuyPrice8 = item.BuyPrice8 != -9999999 ? item.BuyPrice8 : existing.BuyPrice8;
                        existing.BuyQuantity8 = item.BuyQuantity8 != -9999999 ? item.BuyQuantity8 : existing.BuyQuantity8;
                        existing.BuyPrice8_NOO = item.BuyPrice8_NOO != -9999999 ? item.BuyPrice8_NOO : existing.BuyPrice8_NOO;
                        existing.BuyPrice8_MDEY = item.BuyPrice8_MDEY != -9999999 ? item.BuyPrice8_MDEY : existing.BuyPrice8_MDEY;
                        existing.BuyPrice8_MDEMMS = item.BuyPrice8_MDEMMS != -9999999 ? item.BuyPrice8_MDEMMS : existing.BuyPrice8_MDEMMS;
                        existing.BuyPrice9 = item.BuyPrice9 != -9999999 ? item.BuyPrice9 : existing.BuyPrice9;
                        existing.BuyQuantity9 = item.BuyQuantity9 != -9999999 ? item.BuyQuantity9 : existing.BuyQuantity9;
                        existing.BuyPrice9_NOO = item.BuyPrice9_NOO != -9999999 ? item.BuyPrice9_NOO : existing.BuyPrice9_NOO;
                        existing.BuyPrice9_MDEY = item.BuyPrice9_MDEY != -9999999 ? item.BuyPrice9_MDEY : existing.BuyPrice9_MDEY;
                        existing.BuyPrice9_MDEMMS = item.BuyPrice9_MDEMMS != -9999999 ? item.BuyPrice9_MDEMMS : existing.BuyPrice9_MDEMMS;
                        existing.BuyPrice10 = item.BuyPrice10 != -9999999 ? item.BuyPrice10 : existing.BuyPrice10;
                        existing.BuyQuantity10 = item.BuyQuantity10 != -9999999 ? item.BuyQuantity10 : existing.BuyQuantity10;
                        existing.BuyPrice10_NOO = item.BuyPrice10_NOO != -9999999 ? item.BuyPrice10_NOO : existing.BuyPrice10_NOO;
                        existing.BuyPrice10_MDEY = item.BuyPrice10_MDEY != -9999999 ? item.BuyPrice10_MDEY : existing.BuyPrice10_MDEY;
                        existing.BuyPrice10_MDEMMS = item.BuyPrice10_MDEMMS != -9999999 ? item.BuyPrice10_MDEMMS : existing.BuyPrice10_MDEMMS;

                        existing.SellPrice1 = item.SellPrice1 != -9999999 ? item.SellPrice1 : existing.SellPrice1;
                        existing.SellQuantity1 = item.SellQuantity1 != -9999999 ? item.SellQuantity1 : existing.SellQuantity1;
                        existing.SellPrice1_NOO = item.SellPrice1_NOO != -9999999 ? item.SellPrice1_NOO : existing.SellPrice1_NOO;
                        existing.SellPrice1_MDEY = item.SellPrice1_MDEY != -9999999 ? item.SellPrice1_MDEY : existing.SellPrice1_MDEY;
                        existing.SellPrice1_MDEMMS = item.SellPrice1_MDEMMS != -9999999 ? item.SellPrice1_MDEMMS : existing.SellPrice1_MDEMMS;
                        existing.SellPrice2 = item.SellPrice2 != -9999999 ? item.SellPrice2 : existing.SellPrice2;
                        existing.SellQuantity2 = item.SellQuantity2 != -9999999 ? item.SellQuantity2 : existing.SellQuantity2;
                        existing.SellPrice2_NOO = item.SellPrice2_NOO != -9999999 ? item.SellPrice2_NOO : existing.SellPrice2_NOO;
                        existing.SellPrice2_MDEY = item.SellPrice2_MDEY != -9999999 ? item.SellPrice2_MDEY : existing.SellPrice2_MDEY;
                        existing.SellPrice2_MDEMMS = item.SellPrice2_MDEMMS != -9999999 ? item.SellPrice2_MDEMMS : existing.SellPrice2_MDEMMS;
                        existing.SellPrice3 = item.SellPrice3 != -9999999 ? item.SellPrice3 : existing.SellPrice3;
                        existing.SellQuantity3 = item.SellQuantity3 != -9999999 ? item.SellQuantity3 : existing.SellQuantity3;
                        existing.SellPrice3_NOO = item.SellPrice3_NOO != -9999999 ? item.SellPrice3_NOO : existing.SellPrice3_NOO;
                        existing.SellPrice3_MDEY = item.SellPrice3_MDEY != -9999999 ? item.SellPrice3_MDEY : existing.SellPrice3_MDEY;
                        existing.SellPrice3_MDEMMS = item.SellPrice3_MDEMMS != -9999999 ? item.SellPrice3_MDEMMS : existing.SellPrice3_MDEMMS;
                        existing.SellPrice4 = item.SellPrice4 != -9999999 ? item.SellPrice4 : existing.SellPrice4;
                        existing.SellQuantity4 = item.SellQuantity4 != -9999999 ? item.SellQuantity4 : existing.SellQuantity4;
                        existing.SellPrice4_NOO = item.SellPrice4_NOO != -9999999 ? item.SellPrice4_NOO : existing.SellPrice4_NOO;
                        existing.SellPrice4_MDEY = item.SellPrice4_MDEY != -9999999 ? item.SellPrice4_MDEY : existing.SellPrice4_MDEY;
                        existing.SellPrice4_MDEMMS = item.SellPrice4_MDEMMS != -9999999 ? item.SellPrice4_MDEMMS : existing.SellPrice4_MDEMMS;
                        existing.SellPrice5 = item.SellPrice5 != -9999999 ? item.SellPrice5 : existing.SellPrice5;
                        existing.SellQuantity5 = item.SellQuantity5 != -9999999 ? item.SellQuantity5 : existing.SellQuantity5;
                        existing.SellPrice5_NOO = item.SellPrice5_NOO != -9999999 ? item.SellPrice5_NOO : existing.SellPrice5_NOO;
                        existing.SellPrice5_MDEY = item.SellPrice5_MDEY != -9999999 ? item.SellPrice5_MDEY : existing.SellPrice5_MDEY;
                        existing.SellPrice5_MDEMMS = item.SellPrice5_MDEMMS != -9999999 ? item.SellPrice5_MDEMMS : existing.SellPrice5_MDEMMS;
                        existing.SellPrice6 = item.SellPrice6 != -9999999 ? item.SellPrice6 : existing.SellPrice6;
                        existing.SellQuantity6 = item.SellQuantity6 != -9999999 ? item.SellQuantity6 : existing.SellQuantity6;
                        existing.SellPrice6_NOO = item.SellPrice6_NOO != -9999999 ? item.SellPrice6_NOO : existing.SellPrice6_NOO;
                        existing.SellPrice6_MDEY = item.SellPrice6_MDEY != -9999999 ? item.SellPrice6_MDEY : existing.SellPrice6_MDEY;
                        existing.SellPrice6_MDEMMS = item.SellPrice6_MDEMMS != -9999999 ? item.SellPrice6_MDEMMS : existing.SellPrice6_MDEMMS;
                        existing.SellPrice7 = item.SellPrice7 != -9999999 ? item.SellPrice7 : existing.SellPrice7;
                        existing.SellQuantity7 = item.SellQuantity7 != -9999999 ? item.SellQuantity7 : existing.SellQuantity7;
                        existing.SellPrice7_NOO = item.SellPrice7_NOO != -9999999 ? item.SellPrice7_NOO : existing.SellPrice7_NOO;
                        existing.SellPrice7_MDEY = item.SellPrice7_MDEY != -9999999 ? item.SellPrice7_MDEY : existing.SellPrice7_MDEY;
                        existing.SellPrice7_MDEMMS = item.SellPrice7_MDEMMS != -9999999 ? item.SellPrice7_MDEMMS : existing.SellPrice7_MDEMMS;
                        existing.SellPrice8 = item.SellPrice8 != -9999999 ? item.SellPrice8 : existing.SellPrice8;
                        existing.SellQuantity8 = item.SellQuantity8 != -9999999 ? item.SellQuantity8 : existing.SellQuantity8;
                        existing.SellPrice8_NOO = item.SellPrice8_NOO != -9999999 ? item.SellPrice8_NOO : existing.SellPrice8_NOO;
                        existing.SellPrice8_MDEY = item.SellPrice8_MDEY != -9999999 ? item.SellPrice8_MDEY : existing.SellPrice8_MDEY;
                        existing.SellPrice8_MDEMMS = item.SellPrice8_MDEMMS != -9999999 ? item.SellPrice8_MDEMMS : existing.SellPrice8_MDEMMS;
                        existing.SellPrice9 = item.SellPrice9 != -9999999 ? item.SellPrice9 : existing.SellPrice9;
                        existing.SellQuantity9 = item.SellQuantity9 != -9999999 ? item.SellQuantity9 : existing.SellQuantity9;
                        existing.SellPrice9_NOO = item.SellPrice9_NOO != -9999999 ? item.SellPrice9_NOO : existing.SellPrice9_NOO;
                        existing.SellPrice9_MDEY = item.SellPrice9_MDEY != -9999999 ? item.SellPrice9_MDEY : existing.SellPrice9_MDEY;
                        existing.SellPrice9_MDEMMS = item.SellPrice9_MDEMMS != -9999999 ? item.SellPrice9_MDEMMS : existing.SellPrice9_MDEMMS;
                        existing.SellPrice10 = item.SellPrice10 != -9999999 ? item.SellPrice10 : existing.SellPrice10;
                        existing.SellQuantity10 = item.SellQuantity10 != -9999999 ? item.SellQuantity10 : existing.SellQuantity10;
                        existing.SellPrice10_NOO = item.SellPrice10_NOO != -9999999 ? item.SellPrice10_NOO : existing.SellPrice10_NOO;
                        existing.SellPrice10_MDEY = item.SellPrice10_MDEY != -9999999 ? item.SellPrice10_MDEY : existing.SellPrice10_MDEY;
                        existing.SellPrice10_MDEMMS = item.SellPrice10_MDEMMS != -9999999 ? item.SellPrice10_MDEMMS : existing.SellPrice10_MDEMMS;
                        existing.MatchPrice = item.MatchPrice != -9999999 ? item.MatchPrice : existing.MatchPrice;
                        existing.MatchQuantity = item.MatchQuantity != -9999999 ? item.MatchQuantity : existing.MatchQuantity;
                        existing.OpenPrice = item.OpenPrice != -9999999 ? item.OpenPrice : existing.OpenPrice;
                        existing.ClosePrice = item.ClosePrice != -9999999 ? item.ClosePrice : existing.ClosePrice;
                        existing.HighestPrice = item.HighestPrice != -9999999 ? item.HighestPrice : existing.HighestPrice;
                        existing.LowestPrice = item.LowestPrice != -9999999 ? item.LowestPrice : existing.LowestPrice;
                        existing.CheckSum = item.CheckSum ?? existing.CheckSum;
                    }
                }
                var distinctList = groupedDict.Values.ToList();
                DataTable dt = ConvertEPriceRecoveryListToDataTable(distinctList);
                using (var conn = new OracleConnection(this._priceConfig.ConnectionOracle))
                {
                    conn.Open();

                    // Bắt đầu transaction để đảm bảo tính toàn vẹn dữ liệu
                    using (var transaction = conn.BeginTransaction())
                    {
                        try
                        {
                            // Sử dụng OracleBulkCopy để insert dữ liệu vào bảng tạm
                            try
                            {
                                using (var bulkCopy = new OracleBulkCopy(conn))
                                {
                                    bulkCopy.DestinationTableName = "table_temporary_W";
                                    bulkCopy.BatchSize = 5000;

                                    foreach (DataColumn col in dt.Columns)
                                    {
                                        bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                                    }

                                    bulkCopy.WriteToServer(dt);
                                }
                                //Console.WriteLine("Bulk insert thành công.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Bulk insert lỗi: " + ex.Message);
                            }
                            //using (OracleCommand checkCmd = new OracleCommand("SELECT COUNT(*) FROM table_temporary_W_HSX", conn))
                            //{
                            //    checkCmd.Transaction = transaction;
                            //    var count = Convert.ToInt32(checkCmd.ExecuteScalar());
                            //    Console.WriteLine("Số dòng trong bảng tạm: " + count);
                            //}
                            // Gọi stored procedure để insert/update dữ liệu vào bảng chính
                            using (OracleCommand cmdProc = new OracleCommand("PROC_MERGE_MSG_W", conn))
                            {
                                cmdProc.CommandType = System.Data.CommandType.StoredProcedure;

                                // Thêm các tham số nếu cần thiết
                                //cmdProc.Parameters.Add("preturnMess", OracleDbType.Varchar2).Value = value1;
                                OracleParameter outputParam = new OracleParameter("v_err_msg", OracleDbType.Varchar2, 1000);
                                outputParam.Direction = ParameterDirection.Output;
                                cmdProc.Parameters.Add(outputParam);

                                cmdProc.Transaction = transaction; // Thiết lập transaction cho stored procedure
                                cmdProc.ExecuteNonQuery(); // Thực thi SP
                            }
                            // Xóa dữ liệu trong bảng tạm sau khi xử lý xong
                            using (OracleCommand cmdTruncate = new OracleCommand("TRUNCATE TABLE table_temporary_W", conn))
                            {
                                cmdTruncate.Transaction = transaction;
                                cmdTruncate.ExecuteNonQuery();
                            }
                            //using (OracleCommand checkCmd = new OracleCommand("SELECT COUNT(*) FROM table_temporary_W_HSX", conn))
                            //{
                            //    checkCmd.Transaction = transaction;
                            //    var count = Convert.ToInt32(checkCmd.ExecuteScalar());
                            //    Console.WriteLine("Số dòng trong bảng tạm: " + count);
                            //}
                            // Commit transaction sau khi tất cả các thao tác thành công
                            transaction.Commit();
                        }
                        catch (Exception ex)
                        {
                            // Nếu có lỗi, rollback transaction và in ra thông báo lỗi
                            transaction.Rollback();
                            Console.WriteLine("Error: " + ex.Message);
                            this._app.ErrorLogger.LogError(ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
            }
        }
        public async Task Oracle_BulkIns_msgW(List<EPriceRecovery> lst_ePRecovery)
        {
            try
            {
                DataTable dt = ConvertEPriceRecoveryListToDataTable(lst_ePRecovery);
                using (var conn = new OracleConnection(this._priceConfig.ConnectionOracle))
                {
                    conn.Open();
                    using (var bulkCopy = new OracleBulkCopy(conn))
                    {
                        bulkCopy.DestinationTableName = "tpricerecovery_intraday";
                        bulkCopy.BatchSize = 5000;

                        foreach (DataColumn col in dt.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                        }

                        bulkCopy.WriteToServer(dt);
                    }
                }
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
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
        /// 2020-07-31 14:44:33 ngocta2
        /// xu ly data : convert raw data thanh obj, pass vao DAL >> can lam song song
        /// </summary>
        /// <param name="msgType"></param>
        /// <param name="rawData"></param>
        /// <returns></returns>
        public async Task<ProcessMessageResult> ProcessMessage(string msgType, string rawData)
        {
            TExecutionContext ec = this._app.DebugLogger.WriteBufferBegin($"ProcessMessage msgType={msgType}; rawData={rawData}", true);
            try
            {
                var result = new ProcessMessageResult();
                EBulkScript eBulkScript = new EBulkScript();
                EPrice ePrice = null;
                EPriceRecovery ePRecovery = null;
                long sequence = 0;
                string strExchange = null;
                switch (msgType)
                {
                    // 4.1 - Security Definition
                    case ESecurityDefinition.__MSG_TYPE:
                        ESecurityDefinition eSD = await this.Raw2Entity<ESecurityDefinition>(rawData);
                        sequence = eSD.MsgSeqNum;
                        strExchange = eSD.MarketID;
                        // Lấy ra key msg lưu db
                        if ((eSD.MarketID == "STX" || eSD.MarketID == "UPX" || eSD.MarketID == "DVX") && eSD.BoardID == "G1" && !d_dic_stockno.ContainsKey(eSD.Symbol))
                        {
                            d_dic_stockno[eSD.Symbol] = eSD.TickerCode;
                            string stockno = JsonConvert.SerializeObject(d_dic_stockno);
                            _redis.SetCacheBI(TEMPLATE_REDIS_KEY_STOCK_NO_HNX, stockno, intPeriod);
                        }
                        eBulkScript = await _repository.GetScriptSecurityDefinition(eSD);

                        break;
                    // 4.2 - Security Status
                    case ESecurityStatus.__MSG_TYPE:
                        ESecurityStatus eSS = await this.Raw2Entity<ESecurityStatus>(rawData);
                        sequence = eSS.MsgSeqNum;
                        strExchange = eSS.MarketID;
                        eBulkScript = await _repository.GetScriptSecurityStatus(eSS);
                        break;
                    // 4.3 - Security Information Notification
                    case ESecurityInformationNotification.__MSG_TYPE:
                        ESecurityInformationNotification eSIN = await this.Raw2Entity<ESecurityInformationNotification>(rawData);
                        sequence = eSIN.MsgSeqNum;
                        strExchange = eSIN.MarketID;
                        eBulkScript = await _repository.GetScriptSecurityInformationNotification(eSIN);
                        break;
                    // 4.4 - Symbol Closing Information
                    case ESymbolClosingInformation.__MSG_TYPE:
                        ESymbolClosingInformation eSCI = await this.Raw2Entity<ESymbolClosingInformation>(rawData);
                        sequence = eSCI.MsgSeqNum;
                        strExchange = eSCI.MarketID;
                        eBulkScript = await _repository.GetScriptSymbolClosingInformation(eSCI);
                        break;
                    // 4.5 - Volatility Interruption
                    case EVolatilityInterruption.__MSG_TYPE:
                        EVolatilityInterruption eVI = await this.Raw2Entity<EVolatilityInterruption>(rawData);
                        sequence = eVI.MsgSeqNum;
                        strExchange = eVI.MarketID;
                        eBulkScript = await _repository.GetScriptVolatilityInterruption(eVI);
                        break;
                    // 4.6 - Market Maker Information
                    case EMarketMakerInformation.__MSG_TYPE:
                        EMarketMakerInformation eMMI = await this.Raw2Entity<EMarketMakerInformation>(rawData);
                        sequence = eMMI.MsgSeqNum;
                        strExchange = eMMI.MarketID;
                        eBulkScript = await _repository.GetScriptMarketMakerInformation(eMMI);
                        break;
                    // 4.7 - Symbol Event
                    case ESymbolEvent.__MSG_TYPE:
                        ESymbolEvent eSE = await this.Raw2Entity<ESymbolEvent>(rawData);
                        eSE.EventStartDate = this._app.Common.FixToDateString(eSE.EventStartDate.ToString());
                        eSE.EventEndDate = this._app.Common.FixToDateString(eSE.EventEndDate.ToString());// can phai chuyen SendingTime tu string sang DateTime											              
                        sequence = eSE.MsgSeqNum;
                        strExchange = eSE.MarketID;
                        eBulkScript = await _repository.GetScriptSymbolEvent(eSE);
                        break;
                    // 4.8 - Index Constituents Information
                    case EIndexConstituentsInformation.__MSG_TYPE:
                        EIndexConstituentsInformation eICI = await this.Raw2Entity<EIndexConstituentsInformation>(rawData);
                        sequence = eICI.MsgSeqNum;
                        strExchange = eICI.MarketID;
                        eBulkScript = await _repository.GetScriptIndexConstituentsInformation(eICI);
                        break;
                    // 4.9 - Random End
                    case ERandomEnd.__MSG_TYPE:
                        ERandomEnd eRE = await this.Raw2Entity<ERandomEnd>(rawData);
                        eRE.TransactTime = this._app.Common.FixToTimeString(eRE.TransactTime.ToString());// can phai chuyen SendingTime tu string sang DateTime											              
                        sequence = eRE.MsgSeqNum;
                        strExchange = eRE.MarketID;
                        eBulkScript = await _repository.GetScriptRandomEnd(eRE);
                        break;
                    // 4.10 Price
                    case EPrice.__MSG_TYPE:
                        EPrice eP = this._app.HandCode.Fix_Fix2EPrice(rawData, true, 1, 2, 1);
                        sequence = eP.MsgSeqNum;
                        strExchange = eP.MarketID;
                        ePrice = eP;
                        eBulkScript = await _repository.GetScriptPriceAll(eP);
                        break;
                    // 4.11 Price Recovery
                    case EPriceRecovery.__MSG_TYPE:
                        EPriceRecovery ePR = this._app.HandCode.Fix_Fix2EPriceRecovery(rawData, true, 1, 2, 1);
                        sequence = ePR.MsgSeqNum;
                        strExchange = ePR.MarketID;
                        ePRecovery = ePR;
                        eBulkScript = await _repository.GetScriptPriceRecoveryAll(ePR);
                        break;
                    // 4.13 - Index
                    case EIndex.__MSG_TYPE:
                        EIndex eI = await this.Raw2Entity<EIndex>(rawData);
                        eI.TransDate = this._app.Common.FixToTransDateString(eI.SendingTime.ToString());
                        eI.TransactTime = this._app.Common.FixToTimeString(eI.TransactTime.ToString());// can phai chuyen SendingTime tu string sang DateTime
                        sequence = eI.MsgSeqNum;
                        strExchange = eI.MarketID; 											              
                        eBulkScript = await _repository.GetScriptIndex(eI);
                        break;
                    // 4.14 - Investor per Industry
                    case EInvestorPerIndustry.__MSG_TYPE:
                        EInvestorPerIndustry eIPI = await this.Raw2Entity<EInvestorPerIndustry>(rawData);
                        eIPI.TransactTime = this._app.Common.FixToTimeString(eIPI.TransactTime.ToString());// can phai chuyen SendingTime tu string sang DateTime											              
                        sequence = eIPI.MsgSeqNum;
                        strExchange = eIPI.MarketID;
                        eBulkScript = await _repository.GetScriptInvestorperIndustry(eIPI);
                        break;

                    // 4.17 - Investor per Symbol
                    case EInvestorPerSymbol.__MSG_TYPE:
                        EInvestorPerSymbol eIPS = await this.Raw2Entity<EInvestorPerSymbol>(rawData);
                        sequence = eIPS.MsgSeqNum;
                        strExchange = eIPS.MarketID;
                        eBulkScript = await _repository.GetScriptInvestorperSymbol(eIPS);
                        break;
                    // 4.18 - Top N Members per Symbol
                    case ETopNMembersPerSymbol.__MSG_TYPE:
                        ETopNMembersPerSymbol eTNMPS = await this.Raw2Entity<ETopNMembersPerSymbol>(rawData);
                        sequence = eTNMPS.MsgSeqNum;
                        strExchange = eTNMPS.MarketID;
                        eBulkScript = await _repository.GetScriptTopNMembersperSymbol(eTNMPS);
                        break;
                    // 4.19 - Open Interest
                    case EOpenInterest.__MSG_TYPE:
                        EOpenInterest eOI = await this.Raw2Entity<EOpenInterest>(rawData);
                        eOI.TradeDate = this._app.Common.FixToDateString(eOI.TradeDate.ToString());
                        sequence = eOI.MsgSeqNum;
                        strExchange = eOI.MarketID;
                        eBulkScript = await _repository.GetScriptOpenInterest(eOI);
                        break;
                    // 4.20 - Deem Trade Price
                    case EDeemTradePrice.__MSG_TYPE:
                        EDeemTradePrice eDTP = await this.Raw2Entity<EDeemTradePrice>(rawData);
                        sequence = eDTP.MsgSeqNum;
                        strExchange = eDTP.MarketID;
                        eBulkScript = await _repository.GetScriptDeemTradePrice(eDTP);
                        break;
                    // 4.21 - Foreigner Order Limit
                    case EForeignerOrderLimit.__MSG_TYPE:
                        EForeignerOrderLimit eFOL = await this.Raw2Entity<EForeignerOrderLimit>(rawData);
                        sequence = eFOL.MsgSeqNum;
                        strExchange = eFOL.MarketID;
                        eBulkScript = await _repository.GetScriptForeignerOrderLimit(eFOL);
                        break;
                    // 4.22 - Price Limit Expansion
                    case EPriceLimitExpansion.__MSG_TYPE:
                        EPriceLimitExpansion ePLE = await this.Raw2Entity<EPriceLimitExpansion>(rawData);
                        sequence = ePLE.MsgSeqNum;
                        strExchange = ePLE.MarketID;
                        eBulkScript = await _repository.GetScriptPriceLimitExpansion(ePLE);
                        break;
                    // 4.23 - EETF iNav
                    case EETFiNav.__MSG_TYPE:
                        EETFiNav eEiN = await this.Raw2Entity<EETFiNav>(rawData);
                        sequence = eEiN.MsgSeqNum;
                        strExchange = eEiN.MarketID;
                        eBulkScript = await _repository.GetScriptETFiNav(eEiN);
                        break;
                    // 4.24 - EETF iIndex
                    case EETFiIndex.__MSG_TYPE:
                        EETFiIndex eEiI = await this.Raw2Entity<EETFiIndex>(rawData);
                        sequence = eEiI.MsgSeqNum;
                        strExchange = eEiI.MarketID;
                        eBulkScript = await _repository.GetScriptETFiIndex(eEiI);
                        break;
                    // 4.25 - EETF TrackingError
                    case EETFTrackingError.__MSG_TYPE:
                        EETFTrackingError eETE = await this.Raw2Entity<EETFTrackingError>(rawData);
                        eETE.TradeDate = this._app.Common.FixToDateString(eETE.TradeDate.ToString());
                        sequence = eETE.MsgSeqNum;
                        strExchange = eETE.MarketID;
                        eBulkScript = await _repository.GetScriptETFTrackingError(eETE);
                        break;
                    // 4.26 - Top N Symbols with Trading Quantity
                    case ETopNSymbolsWithTradingQuantity.__MSG_TYPE:
                        ETopNSymbolsWithTradingQuantity ETNSWTQ = await this.Raw2Entity<ETopNSymbolsWithTradingQuantity>(rawData);
                        sequence = ETNSWTQ.MsgSeqNum;
                        strExchange = ETNSWTQ.MarketID;
                        eBulkScript = await _repository.GetScriptTopNSymbolswithTradingQuantity(ETNSWTQ);
                        break;
                    // 4.27 - Top N Symbols with  Current Price
                    case ETopNSymbolsWithCurrentPrice.__MSG_TYPE:
                        ETopNSymbolsWithCurrentPrice ETNSWCP = await this.Raw2Entity<ETopNSymbolsWithCurrentPrice>(rawData);
                        sequence = ETNSWCP.MsgSeqNum;
                        strExchange = ETNSWCP.MarketID;
                        eBulkScript = await _repository.GetScriptTopNSymbolswithCurrentPrice(ETNSWCP);
                        break;
                    // 4.28 - Top N Symbols with High Ratio of Price
                    case ETopNSymbolsWithHighRatioOfPrice.__MSG_TYPE:
                        ETopNSymbolsWithHighRatioOfPrice ETNSWHROP = await this.Raw2Entity<ETopNSymbolsWithHighRatioOfPrice>(rawData);
                        sequence = ETNSWHROP.MsgSeqNum;
                        strExchange = ETNSWHROP.MarketID;
                        eBulkScript = await _repository.GetScriptTopNSymbolswithHighRatioofPrice(ETNSWHROP);
                        break;

                    // 4.29 - Top N Symbols with Low Ratio of Price
                    case ETopNSymbolsWithLowRatioOfPrice.__MSG_TYPE:
                        ETopNSymbolsWithLowRatioOfPrice ETNSWLROP = await this.Raw2Entity<ETopNSymbolsWithLowRatioOfPrice>(rawData);
                        sequence = ETNSWLROP.MsgSeqNum;
                        strExchange = ETNSWLROP.MarketID;
                        eBulkScript = await _repository.GetScriptTopNSymbolswithLowRatioofPrice(ETNSWLROP);
                        break;
                    // 4.30 - Trading Result of Foreign Investors
                    case ETradingResultOfForeignInvestors.__MSG_TYPE:
                        ETradingResultOfForeignInvestors ETRFI = await this.Raw2Entity<ETradingResultOfForeignInvestors>(rawData);
                        if (ETRFI.TransactTime != null)
                        {
                            ETRFI.TransactTime = this._app.Common.FixToTimeString(ETRFI.TransactTime.ToString());// can phai chuyen SendingTime tu string sang DateTime	
                            sequence = ETRFI.MsgSeqNum;
                            strExchange = ETRFI.MarketID;
                        }
                        
                        eBulkScript = await _repository.GetScriptTradingResultofForeignInvestors(ETRFI);
                        break;
                    // 4.31 - Disclosure
                    case EDisclosure.__MSG_TYPE:
                        EDisclosure eD = await this.Raw2Entity<EDisclosure>(rawData);
                        eD.PublicInformationDate = this._app.Common.FixToDateString(eD.PublicInformationDate.ToString());
                        eD.TransmissionDate = this._app.Common.FixToDateString(eD.TransmissionDate.ToString());// can phai chuyen SendingTime tu string sang DateTime		
                        sequence = eD.MsgSeqNum;
                        strExchange = eD.MarketID;
                        eBulkScript = await _repository.GetScriptDisclosure(eD);
                        break;
                    // 4.32 - TimeStampPolling
                    //case ETimeStampPolling.__MSG_TYPE:
                    //    ETimeStampPolling eTSP = await this.Raw2Entity<ETimeStampPolling>(rawData);
                    //    eTSP.TransactTime = this._app.Common.FixToTimeString(eTSP.TransactTime.ToString());// can phai chuyen SendingTime tu string sang DateTime		
                    //    eBulkScript = await _repository.GetScriptTimeStampPolling(eTSP);
                    //    break;
                    // 4.33 - DrvProductEvent
                    case EDrvProductEvent.__MSG_TYPE:
                        EDrvProductEvent eDRV = await this.Raw2Entity<EDrvProductEvent>(rawData);
                        //  eDRV.PublicInformationDate = this._app.Common.FixToDateString(eDRV.PublicInformationDate.ToString());
                        // eDRV.TransmissionDate = this._app.Common.FixToDateString(eDRV.TransmissionDate.ToString());// can phai chuyen SendingTime tu string sang DateTime		
                        eBulkScript = await _repository.GetScriptDrvProductEvent(eDRV);
                        break;
                }

                result.Script = eBulkScript;
                result.obj_X = ePrice;
                result.obj_W = ePRecovery;
                result.MsgSeqNum = sequence;
                result.strExchange = strExchange;
                return result;
            }
            catch (Exception ex)
            {
                // log error + buffer data
                this._app.ErrorLogger.LogErrorContext(ex, ec);
                return null;
            }
        }
        public DataTable ConvertEPriceListToDataTable(List<EPrice> lst_eP)
        {
            try
            {
                DataTable dt = new DataTable();

                dt.Columns.Add("aBeginString", typeof(string));
                dt.Columns.Add("aBodyLength", typeof(string));
                dt.Columns.Add("aMsgType", typeof(string));
                dt.Columns.Add("aSenderCompID", typeof(string));
                dt.Columns.Add("aTargetCompID", typeof(string));
                dt.Columns.Add("aMsgSeqNum", typeof(string));
                dt.Columns.Add("aSendingTime", typeof(DateTime));
                dt.Columns.Add("aMarketID", typeof(string));
                dt.Columns.Add("aBoardID", typeof(string));
                dt.Columns.Add("aTradingSessionID", typeof(string));
                dt.Columns.Add("aSymbol", typeof(string));
                dt.Columns.Add("aTradeDate", typeof(string));
                dt.Columns.Add("aTransactTime", typeof(string));
                dt.Columns.Add("aTotalVolumeTraded", typeof(string));
                dt.Columns.Add("aGrossTradeAmt", typeof(string));
                dt.Columns.Add("aBuyTotOrderQty", typeof(string));
                dt.Columns.Add("aBuyValidOrderCnt", typeof(string));
                dt.Columns.Add("aSellTotOrderQty", typeof(string));
                dt.Columns.Add("aSellValidOrderCnt", typeof(string));
                dt.Columns.Add("aNoMDEntries", typeof(string));

                dt.Columns.Add("aBuyPrice1", typeof(string));
                dt.Columns.Add("aBuyQuantity1", typeof(string));
                dt.Columns.Add("aBuyPrice1_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice1_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice1_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice2", typeof(string));
                dt.Columns.Add("aBuyQuantity2", typeof(string));
                dt.Columns.Add("aBuyPrice2_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice2_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice2_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice3", typeof(string));
                dt.Columns.Add("aBuyQuantity3", typeof(string));
                dt.Columns.Add("aBuyPrice3_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice3_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice3_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice4", typeof(string));
                dt.Columns.Add("aBuyQuantity4", typeof(string));
                dt.Columns.Add("aBuyPrice4_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice4_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice4_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice5", typeof(string));
                dt.Columns.Add("aBuyQuantity5", typeof(string));
                dt.Columns.Add("aBuyPrice5_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice5_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice5_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice6", typeof(string));
                dt.Columns.Add("aBuyQuantity6", typeof(string));
                dt.Columns.Add("aBuyPrice6_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice6_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice6_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice7", typeof(string));
                dt.Columns.Add("aBuyQuantity7", typeof(string));
                dt.Columns.Add("aBuyPrice7_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice7_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice7_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice8", typeof(string));
                dt.Columns.Add("aBuyQuantity8", typeof(string));
                dt.Columns.Add("aBuyPrice8_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice8_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice8_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice9", typeof(string));
                dt.Columns.Add("aBuyQuantity9", typeof(string));
                dt.Columns.Add("aBuyPrice9_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice9_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice9_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice10", typeof(string));
                dt.Columns.Add("aBuyQuantity10", typeof(string));
                dt.Columns.Add("aBuyPrice10_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice10_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice10_MDEMMS", typeof(string));


                dt.Columns.Add("aSellPrice1", typeof(string));
                dt.Columns.Add("aSellQuantity1", typeof(string));
                dt.Columns.Add("aSellPrice1_NOO", typeof(string));
                dt.Columns.Add("aSellPrice1_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice1_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice2", typeof(string));
                dt.Columns.Add("aSellQuantity2", typeof(string));
                dt.Columns.Add("aSellPrice2_NOO", typeof(string));
                dt.Columns.Add("aSellPrice2_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice2_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice3", typeof(string));
                dt.Columns.Add("aSellQuantity3", typeof(string));
                dt.Columns.Add("aSellPrice3_NOO", typeof(string));
                dt.Columns.Add("aSellPrice3_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice3_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice4", typeof(string));
                dt.Columns.Add("aSellQuantity4", typeof(string));
                dt.Columns.Add("aSellPrice4_NOO", typeof(string));
                dt.Columns.Add("aSellPrice4_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice4_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice5", typeof(string));
                dt.Columns.Add("aSellQuantity5", typeof(string));
                dt.Columns.Add("aSellPrice5_NOO", typeof(string));
                dt.Columns.Add("aSellPrice5_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice5_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice6", typeof(string));
                dt.Columns.Add("aSellQuantity6", typeof(string));
                dt.Columns.Add("aSellPrice6_NOO", typeof(string));
                dt.Columns.Add("aSellPrice6_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice6_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice7", typeof(string));
                dt.Columns.Add("aSellQuantity7", typeof(string));
                dt.Columns.Add("aSellPrice7_NOO", typeof(string));
                dt.Columns.Add("aSellPrice7_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice7_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice8", typeof(string));
                dt.Columns.Add("aSellQuantity8", typeof(string));
                dt.Columns.Add("aSellPrice8_NOO", typeof(string));
                dt.Columns.Add("aSellPrice8_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice8_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice9", typeof(string));
                dt.Columns.Add("aSellQuantity9", typeof(string));
                dt.Columns.Add("aSellPrice9_NOO", typeof(string));
                dt.Columns.Add("aSellPrice9_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice9_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice10", typeof(string));
                dt.Columns.Add("aSellQuantity10", typeof(string));
                dt.Columns.Add("aSellPrice10_NOO", typeof(string));
                dt.Columns.Add("aSellPrice10_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice10_MDEMMS", typeof(string));
                dt.Columns.Add("aMatchPrice", typeof(string));
                dt.Columns.Add("aMatchQuantity", typeof(string));
                dt.Columns.Add("aOpenPrice", typeof(string));
                dt.Columns.Add("aClosePrice", typeof(string));
                dt.Columns.Add("aHighestPrice", typeof(string));
                dt.Columns.Add("aLowestPrice", typeof(string));
                //dt.Columns.Add("RepeatingDataFix", typeof(string));
                //dt.Columns.Add("RepeatingDataJson", typeof(string));
                dt.Columns.Add("aCheckSum", typeof(string));
                foreach (var item in lst_eP)
                {
                    DataRow row = dt.NewRow();
                    row["aBeginString"] = item.BeginString;
                    row["aBodyLength"] = item.BodyLength;
                    row["aMsgType"] = item.MsgType;
                    row["aSenderCompID"] = item.SenderCompID;
                    row["aTargetCompID"] = item.TargetCompID;
                    row["aMsgSeqNum"] = item.MsgSeqNum;
                    // Parse chuỗi "20250404 09:38:55.584" thành DateTime
                    if (DateTime.TryParseExact(item.SendingTime, "yyyyMMdd HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime sendingTime))
                    {
                        row["aSendingTime"] = sendingTime;
                    }
                    else
                    {
                        // Nếu lỗi format, để NULL
                        row["aSendingTime"] = DBNull.Value;
                    }
                    row["aMarketID"] = item.MarketID;
                    row["aBoardID"] = item.BoardID;
                    row["aTradingSessionID"] = item.TradingSessionID;
                    row["aSymbol"] = item.Symbol;
                    row["aTradeDate"] = item.TradeDate;
                    row["aTransactTime"] = item.TransactTime;

                    row["aTotalVolumeTraded"] = item.TotalVolumeTraded != -9999999 ? (object)item.TotalVolumeTraded : DBNull.Value;
                    row["aGrossTradeAmt"] = item.GrossTradeAmt != -9999999 ? (object)item.GrossTradeAmt : DBNull.Value; 
                    row["aBuyTotOrderQty"] = item.BuyTotOrderQty != -9999999 ? (object)item.BuyTotOrderQty : DBNull.Value;  
                    row["aBuyValidOrderCnt"] = item.BuyValidOrderCnt != -9999999 ? (object)item.BuyValidOrderCnt : DBNull.Value; 
                    row["aSellTotOrderQty"] = item.SellTotOrderQty != -9999999 ? (object)item.SellTotOrderQty : DBNull.Value;   
                    row["aSellValidOrderCnt"] = item.SellValidOrderCnt != -9999999 ? (object)item.SellValidOrderCnt : DBNull.Value;  
                    row["aNoMDEntries"] = item.NoMDEntries != -9999999 ? (object)item.NoMDEntries : DBNull.Value;  


                    row["aBuyPrice1"] = item.BuyPrice1 != -9999999 ? (object)item.BuyPrice1 : DBNull.Value;
                    row["aBuyQuantity1"] = item.BuyQuantity1 != -9999999 ? (object)item.BuyQuantity1 : DBNull.Value;
                    row["aBuyPrice1_NOO"] = item.BuyPrice1_NOO;
                    row["aBuyPrice1_MDEY"] = item.BuyPrice1_MDEY;
                    row["aBuyPrice1_MDEMMS"] = item.BuyPrice1_MDEMMS;
                    row["aBuyPrice2"] = item.BuyPrice2 != -9999999 ? (object)item.BuyPrice2 : DBNull.Value;
                    row["aBuyQuantity2"] = item.BuyQuantity2 != -9999999 ? (object)item.BuyQuantity2 : DBNull.Value;
                    row["aBuyPrice2_NOO"] = item.BuyPrice2_NOO;
                    row["aBuyPrice2_MDEY"] = item.BuyPrice2_MDEY;
                    row["aBuyPrice2_MDEMMS"] = item.BuyPrice2_MDEMMS;
                    row["aBuyPrice3"] = item.BuyPrice3 != -9999999 ? (object)item.BuyPrice3 : DBNull.Value;
                    row["aBuyQuantity3"] = item.BuyQuantity3 != -9999999 ? (object)item.BuyQuantity3 : DBNull.Value;
                    row["aBuyPrice3_NOO"] = item.BuyPrice3_NOO;
                    row["aBuyPrice3_MDEY"] = item.BuyPrice3_MDEY;
                    row["aBuyPrice3_MDEMMS"] = item.BuyPrice3_MDEMMS;

                    row["aBuyPrice4"] = item.BuyPrice4 != -9999999 ? (object)item.BuyPrice4 : DBNull.Value;
                    row["aBuyQuantity4"] = item.BuyQuantity4 != -9999999 ? (object)item.BuyQuantity4 : DBNull.Value;
                    row["aBuyPrice4_NOO"] = item.BuyPrice4_NOO;
                    row["aBuyPrice4_MDEY"] = item.BuyPrice4_MDEY;
                    row["aBuyPrice4_MDEMMS"] = item.BuyPrice4_MDEMMS;
                    row["aBuyPrice5"] = item.BuyPrice5 != -9999999 ? (object)item.BuyPrice5 : DBNull.Value;
                    row["aBuyQuantity5"] = item.BuyQuantity5 != -9999999 ? (object)item.BuyQuantity5 : DBNull.Value;
                    row["aBuyPrice5_NOO"] = item.BuyPrice5_NOO;
                    row["aBuyPrice5_MDEY"] = item.BuyPrice5_MDEY;
                    row["aBuyPrice5_MDEMMS"] = item.BuyPrice5_MDEMMS;
                    row["aBuyPrice6"] = item.BuyPrice6 != -9999999 ? (object)item.BuyPrice6 : DBNull.Value;
                    row["aBuyQuantity6"] = item.BuyQuantity6 != -9999999 ? (object)item.BuyQuantity6 : DBNull.Value;
                    row["aBuyPrice6_NOO"] = item.BuyPrice6_NOO;
                    row["aBuyPrice6_MDEY"] = item.BuyPrice6_MDEY;
                    row["aBuyPrice6_MDEMMS"] = item.BuyPrice6_MDEMMS;

                    row["aBuyPrice7"] = item.BuyPrice7 != -9999999 ? (object)item.BuyPrice7 : DBNull.Value;
                    row["aBuyQuantity7"] = item.BuyQuantity7 != -9999999 ? (object)item.BuyQuantity7 : DBNull.Value;
                    row["aBuyPrice7_NOO"] = item.BuyPrice7_NOO;
                    row["aBuyPrice7_MDEY"] = item.BuyPrice7_MDEY;
                    row["aBuyPrice7_MDEMMS"] = item.BuyPrice7_MDEMMS;
                    row["aBuyPrice8"] = item.BuyPrice8 != -9999999 ? (object)item.BuyPrice8 : DBNull.Value;
                    row["aBuyQuantity8"] = item.BuyQuantity8 != -9999999 ? (object)item.BuyQuantity8 : DBNull.Value;
                    row["aBuyPrice8_NOO"] = item.BuyPrice8_NOO;
                    row["aBuyPrice8_MDEY"] = item.BuyPrice8_MDEY;
                    row["aBuyPrice8_MDEMMS"] = item.BuyPrice8_MDEMMS;
                    row["aBuyPrice9"] = item.BuyPrice9 != -9999999 ? (object)item.BuyPrice9 : DBNull.Value;
                    row["aBuyQuantity9"] = item.BuyQuantity9 != -9999999 ? (object)item.BuyQuantity9 : DBNull.Value;
                    row["aBuyPrice9_NOO"] = item.BuyPrice9_NOO;
                    row["aBuyPrice9_MDEY"] = item.BuyPrice9_MDEY;
                    row["aBuyPrice9_MDEMMS"] = item.BuyPrice9_MDEMMS;
                    row["aBuyPrice10"] = item.BuyPrice10 != -9999999 ? (object)item.BuyPrice10 : DBNull.Value;
                    row["aBuyQuantity10"] = item.BuyQuantity10 != -9999999 ? (object)item.BuyQuantity10 : DBNull.Value;
                    row["aBuyPrice10_NOO"] = item.BuyPrice10_NOO;
                    row["aBuyPrice10_MDEY"] = item.BuyPrice10_MDEY;
                    row["aBuyPrice10_MDEMMS"] = item.BuyPrice10_MDEMMS;

                    row["aSellPrice1"] = item.SellPrice1 != -9999999 ? (object)item.SellPrice1 : DBNull.Value;
                    row["aSellQuantity1"] = item.SellQuantity1 != -9999999 ? (object)item.SellQuantity1 : DBNull.Value;
                    row["aSellPrice1_NOO"] = item.SellPrice1_NOO;
                    row["aSellPrice1_MDEY"] = item.SellPrice1_MDEY;
                    row["aSellPrice1_MDEMMS"] = item.SellPrice1_MDEMMS;
                    row["aSellPrice2"] = item.SellPrice2 != -9999999 ? (object)item.SellPrice2 : DBNull.Value;
                    row["aSellQuantity2"] = item.SellQuantity2 != -9999999 ? (object)item.SellQuantity2 : DBNull.Value;
                    row["aSellPrice2_NOO"] = item.SellPrice2_NOO;
                    row["aSellPrice2_MDEY"] = item.SellPrice2_MDEY;
                    row["aSellPrice2_MDEMMS"] = item.SellPrice2_MDEMMS;
                    row["aSellPrice3"] = item.SellPrice3 != -9999999 ? (object)item.SellPrice3 : DBNull.Value;
                    row["aSellQuantity3"] = item.SellQuantity3 != -9999999 ? (object)item.SellQuantity3 : DBNull.Value;
                    row["aSellPrice3_NOO"] = item.SellPrice3_NOO;
                    row["aSellPrice3_MDEY"] = item.SellPrice3_MDEY;
                    row["aSellPrice3_MDEMMS"] = item.SellPrice3_MDEMMS;
                    row["aSellPrice4"] = item.SellPrice4 != -9999999 ? (object)item.SellPrice4 : DBNull.Value;
                    row["aSellQuantity4"] = item.SellQuantity4 != -9999999 ? (object)item.SellQuantity4 : DBNull.Value;
                    row["aSellPrice4_NOO"] = item.SellPrice4_NOO;
                    row["aSellPrice4_MDEY"] = item.SellPrice4_MDEY;
                    row["aSellPrice4_MDEMMS"] = item.SellPrice4_MDEMMS;
                    row["aSellPrice5"] = item.SellPrice5 != -9999999 ? (object)item.SellPrice5 : DBNull.Value;
                    row["aSellQuantity5"] = item.SellQuantity5 != -9999999 ? (object)item.SellQuantity5 : DBNull.Value;
                    row["aSellPrice5_NOO"] = item.SellPrice5_NOO;
                    row["aSellPrice5_MDEY"] = item.SellPrice5_MDEY;
                    row["aSellPrice5_MDEMMS"] = item.SellPrice5_MDEMMS;
                    row["aSellPrice6"] = item.SellPrice6 != -9999999 ? (object)item.SellPrice6 : DBNull.Value;
                    row["aSellQuantity6"] = item.SellQuantity6 != -9999999 ? (object)item.SellQuantity6 : DBNull.Value;
                    row["aSellPrice6_NOO"] = item.SellPrice6_NOO;
                    row["aSellPrice6_MDEY"] = item.SellPrice6_MDEY;
                    row["aSellPrice6_MDEMMS"] = item.SellPrice6_MDEMMS;
                    row["aSellPrice7"] = item.SellPrice7 != -9999999 ? (object)item.SellPrice7 : DBNull.Value;
                    row["aSellQuantity7"] = item.SellQuantity7 != -9999999 ? (object)item.SellQuantity7 : DBNull.Value;
                    row["aSellPrice7_NOO"] = item.SellPrice7_NOO;
                    row["aSellPrice7_MDEY"] = item.SellPrice7_MDEY;
                    row["aSellPrice7_MDEMMS"] = item.SellPrice7_MDEMMS;
                    row["aSellPrice8"] = item.SellPrice8 != -9999999 ? (object)item.SellPrice8 : DBNull.Value;
                    row["aSellQuantity8"] = item.SellQuantity8 != -9999999 ? (object)item.SellQuantity8 : DBNull.Value;
                    row["aSellPrice8_NOO"] = item.SellPrice8_NOO;
                    row["aSellPrice8_MDEY"] = item.SellPrice8_MDEY;
                    row["aSellPrice8_MDEMMS"] = item.SellPrice8_MDEMMS;
                    row["aSellPrice9"] = item.SellPrice9 != -9999999 ? (object)item.SellPrice9 : DBNull.Value;
                    row["aSellQuantity9"] = item.SellQuantity9 != -9999999 ? (object)item.SellQuantity9 : DBNull.Value;
                    row["aSellPrice9_NOO"] = item.SellPrice9_NOO;
                    row["aSellPrice9_MDEY"] = item.SellPrice9_MDEY;
                    row["aSellPrice9_MDEMMS"] = item.SellPrice9_MDEMMS;
                    row["aSellPrice10"] = item.SellPrice10 != -9999999 ? (object)item.SellPrice10 : DBNull.Value;
                    row["aSellQuantity10"] = item.SellQuantity10 != -9999999 ? (object)item.SellQuantity10 : DBNull.Value;
                    row["aSellPrice10_NOO"] = item.SellPrice10_NOO;
                    row["aSellPrice10_MDEY"] = item.SellPrice10_MDEY;
                    row["aSellPrice10_MDEMMS"] = item.SellPrice10_MDEMMS;

                    row["aMatchPrice"] = item.MatchPrice != -9999999 ? (object)item.MatchPrice : DBNull.Value;
                    row["aMatchQuantity"] = item.MatchQuantity != -9999999 ? (object)item.MatchQuantity : DBNull.Value;
                    row["aOpenPrice"] = item.OpenPrice != -9999999 ? (object)item.OpenPrice : DBNull.Value;
                    row["aClosePrice"] = item.ClosePrice != -9999999 ? (object)item.ClosePrice : DBNull.Value;
                    row["aHighestPrice"] = item.HighestPrice != -9999999 ? (object)item.HighestPrice : DBNull.Value;
                    row["aLowestPrice"] = item.LowestPrice != -9999999 ? (object)item.LowestPrice : DBNull.Value;
                    //row["RepeatingDataFix"] = item.RepeatingDataFix;
                    //row["RepeatingDataJson"] = item.RepeatingDataJson;
                    row["aCheckSum"] = item.CheckSum;
                    dt.Rows.Add(row);
                }
                return dt;
            }
            catch (Exception ex) 
            {
                this._app.ErrorLogger.LogError(ex);
                return null;
            }
        }
        public DataTable ConvertEPriceRecoveryListToDataTable(List<EPriceRecovery> lst_ePRecovery)
        {
            try
            {
                DataTable dt = new DataTable();

                dt.Columns.Add("aBeginString", typeof(string));
                dt.Columns.Add("aBodyLength", typeof(string));
                dt.Columns.Add("aMsgType", typeof(string));
                dt.Columns.Add("aSenderCompID", typeof(string));
                dt.Columns.Add("aTargetCompID", typeof(string));
                dt.Columns.Add("aMsgSeqNum", typeof(string));
                dt.Columns.Add("aSendingTime", typeof(DateTime));
                dt.Columns.Add("aMarketID", typeof(string));
                dt.Columns.Add("aBoardID", typeof(string));
                dt.Columns.Add("aTradingSessionID", typeof(string));
                dt.Columns.Add("aSymbol", typeof(string));


                dt.Columns.Add("aOpnPx", typeof(string));
                dt.Columns.Add("aTrdSessnHighPx", typeof(string));
                dt.Columns.Add("aTrdSessnLowPx", typeof(string));
                dt.Columns.Add("aSymbolCloseInfoPx", typeof(string));
                //dt.Columns.Add("aTradeDate", typeof(string));
                //dt.Columns.Add("aTransactTime", typeof(string));

                dt.Columns.Add("aTotalVolumeTraded", typeof(string));
                dt.Columns.Add("aGrossTradeAmt", typeof(string));
                dt.Columns.Add("aBuyTotOrderQty", typeof(string));
                dt.Columns.Add("aBuyValidOrderCnt", typeof(string));
                dt.Columns.Add("aSellTotOrderQty", typeof(string));
                dt.Columns.Add("aSellValidOrderCnt", typeof(string));
                dt.Columns.Add("aNoMDEntries", typeof(string));

                dt.Columns.Add("aBuyPrice1", typeof(string));
                dt.Columns.Add("aBuyQuantity1", typeof(string));
                dt.Columns.Add("aBuyPrice1_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice1_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice1_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice2", typeof(string));
                dt.Columns.Add("aBuyQuantity2", typeof(string));
                dt.Columns.Add("aBuyPrice2_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice2_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice2_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice3", typeof(string));
                dt.Columns.Add("aBuyQuantity3", typeof(string));
                dt.Columns.Add("aBuyPrice3_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice3_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice3_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice4", typeof(string));
                dt.Columns.Add("aBuyQuantity4", typeof(string));
                dt.Columns.Add("aBuyPrice4_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice4_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice4_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice5", typeof(string));
                dt.Columns.Add("aBuyQuantity5", typeof(string));
                dt.Columns.Add("aBuyPrice5_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice5_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice5_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice6", typeof(string));
                dt.Columns.Add("aBuyQuantity6", typeof(string));
                dt.Columns.Add("aBuyPrice6_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice6_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice6_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice7", typeof(string));
                dt.Columns.Add("aBuyQuantity7", typeof(string));
                dt.Columns.Add("aBuyPrice7_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice7_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice7_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice8", typeof(string));
                dt.Columns.Add("aBuyQuantity8", typeof(string));
                dt.Columns.Add("aBuyPrice8_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice8_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice8_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice9", typeof(string));
                dt.Columns.Add("aBuyQuantity9", typeof(string));
                dt.Columns.Add("aBuyPrice9_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice9_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice9_MDEMMS", typeof(string));
                dt.Columns.Add("aBuyPrice10", typeof(string));
                dt.Columns.Add("aBuyQuantity10", typeof(string));
                dt.Columns.Add("aBuyPrice10_NOO", typeof(string));
                dt.Columns.Add("aBuyPrice10_MDEY", typeof(string));
                dt.Columns.Add("aBuyPrice10_MDEMMS", typeof(string));


                dt.Columns.Add("aSellPrice1", typeof(string));
                dt.Columns.Add("aSellQuantity1", typeof(string));
                dt.Columns.Add("aSellPrice1_NOO", typeof(string));
                dt.Columns.Add("aSellPrice1_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice1_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice2", typeof(string));
                dt.Columns.Add("aSellQuantity2", typeof(string));
                dt.Columns.Add("aSellPrice2_NOO", typeof(string));
                dt.Columns.Add("aSellPrice2_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice2_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice3", typeof(string));
                dt.Columns.Add("aSellQuantity3", typeof(string));
                dt.Columns.Add("aSellPrice3_NOO", typeof(string));
                dt.Columns.Add("aSellPrice3_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice3_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice4", typeof(string));
                dt.Columns.Add("aSellQuantity4", typeof(string));
                dt.Columns.Add("aSellPrice4_NOO", typeof(string));
                dt.Columns.Add("aSellPrice4_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice4_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice5", typeof(string));
                dt.Columns.Add("aSellQuantity5", typeof(string));
                dt.Columns.Add("aSellPrice5_NOO", typeof(string));
                dt.Columns.Add("aSellPrice5_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice5_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice6", typeof(string));
                dt.Columns.Add("aSellQuantity6", typeof(string));
                dt.Columns.Add("aSellPrice6_NOO", typeof(string));
                dt.Columns.Add("aSellPrice6_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice6_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice7", typeof(string));
                dt.Columns.Add("aSellQuantity7", typeof(string));
                dt.Columns.Add("aSellPrice7_NOO", typeof(string));
                dt.Columns.Add("aSellPrice7_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice7_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice8", typeof(string));
                dt.Columns.Add("aSellQuantity8", typeof(string));
                dt.Columns.Add("aSellPrice8_NOO", typeof(string));
                dt.Columns.Add("aSellPrice8_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice8_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice9", typeof(string));
                dt.Columns.Add("aSellQuantity9", typeof(string));
                dt.Columns.Add("aSellPrice9_NOO", typeof(string));
                dt.Columns.Add("aSellPrice9_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice9_MDEMMS", typeof(string));
                dt.Columns.Add("aSellPrice10", typeof(string));
                dt.Columns.Add("aSellQuantity10", typeof(string));
                dt.Columns.Add("aSellPrice10_NOO", typeof(string));
                dt.Columns.Add("aSellPrice10_MDEY", typeof(string));
                dt.Columns.Add("aSellPrice10_MDEMMS", typeof(string));
                dt.Columns.Add("aMatchPrice", typeof(string));
                dt.Columns.Add("aMatchQuantity", typeof(string));
                dt.Columns.Add("aOpenPrice", typeof(string));
                dt.Columns.Add("aClosePrice", typeof(string));
                dt.Columns.Add("aHighestPrice", typeof(string));
                dt.Columns.Add("aLowestPrice", typeof(string));
                //dt.Columns.Add("RepeatingDataFix", typeof(string));
                //dt.Columns.Add("RepeatingDataJson", typeof(string));
                dt.Columns.Add("aCheckSum", typeof(string));
                foreach (var item in lst_ePRecovery)
                {
                    DataRow row = dt.NewRow();
                    row["aBeginString"] = item.BeginString;
                    row["aBodyLength"] = item.BodyLength;
                    row["aMsgType"] = item.MsgType;
                    row["aSenderCompID"] = item.SenderCompID;
                    row["aTargetCompID"] = item.TargetCompID;
                    row["aMsgSeqNum"] = item.MsgSeqNum;
                    // Parse chuỗi "20250404 09:38:55.584" thành DateTime
                    if (DateTime.TryParseExact(item.SendingTime, "yyyyMMdd HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime sendingTime))
                    {
                        row["aSendingTime"] = sendingTime;
                    }
                    else
                    {
                        // Nếu lỗi format, để NULL
                        row["aSendingTime"] = DBNull.Value;
                    }
                    row["aMarketID"] = item.MarketID;
                    row["aBoardID"] = item.BoardID;
                    row["aTradingSessionID"] = item.TradingSessionID;
                    row["aSymbol"] = item.Symbol;

                    row["aOpnPx"] = item.OpnPx;
                    row["aTrdSessnHighPx"] = item.TrdSessnHighPx;
                    row["aTrdSessnLowPx"] = item.TrdSessnLowPx;
                    row["aSymbolCloseInfoPx"] = item.SymbolCloseInfoPx;
                    //row["aTradeDate"] = item.TradeDate;
                    //row["aTransactTime"] = item.TransactTime;

                    row["aTotalVolumeTraded"] = item.TotalVolumeTraded != -9999999 ? (object)item.TotalVolumeTraded : DBNull.Value;
                    row["aGrossTradeAmt"] = item.GrossTradeAmt != -9999999 ? (object)item.GrossTradeAmt : DBNull.Value;
                    row["aBuyTotOrderQty"] = item.BuyTotOrderQty != -9999999 ? (object)item.BuyTotOrderQty : DBNull.Value;
                    row["aBuyValidOrderCnt"] = item.BuyValidOrderCnt != -9999999 ? (object)item.BuyValidOrderCnt : DBNull.Value;
                    row["aSellTotOrderQty"] = item.SellTotOrderQty != -9999999 ? (object)item.SellTotOrderQty : DBNull.Value;
                    row["aSellValidOrderCnt"] = item.SellValidOrderCnt != -9999999 ? (object)item.SellValidOrderCnt : DBNull.Value;
                    row["aNoMDEntries"] = item.NoMDEntries != -9999999 ? (object)item.NoMDEntries : DBNull.Value;


                    row["aBuyPrice1"] = item.BuyPrice1 != -9999999 ? (object)item.BuyPrice1 : DBNull.Value;
                    row["aBuyQuantity1"] = item.BuyQuantity1 != -9999999 ? (object)item.BuyQuantity1 : DBNull.Value;
                    row["aBuyPrice1_NOO"] = item.BuyPrice1_NOO;
                    row["aBuyPrice1_MDEY"] = item.BuyPrice1_MDEY;
                    row["aBuyPrice1_MDEMMS"] = item.BuyPrice1_MDEMMS;
                    row["aBuyPrice2"] = item.BuyPrice2 != -9999999 ? (object)item.BuyPrice2 : DBNull.Value;
                    row["aBuyQuantity2"] = item.BuyQuantity2 != -9999999 ? (object)item.BuyQuantity2 : DBNull.Value;
                    row["aBuyPrice2_NOO"] = item.BuyPrice2_NOO;
                    row["aBuyPrice2_MDEY"] = item.BuyPrice2_MDEY;
                    row["aBuyPrice2_MDEMMS"] = item.BuyPrice2_MDEMMS;
                    row["aBuyPrice3"] = item.BuyPrice3 != -9999999 ? (object)item.BuyPrice3 : DBNull.Value;
                    row["aBuyQuantity3"] = item.BuyQuantity3 != -9999999 ? (object)item.BuyQuantity3 : DBNull.Value;
                    row["aBuyPrice3_NOO"] = item.BuyPrice3_NOO;
                    row["aBuyPrice3_MDEY"] = item.BuyPrice3_MDEY;
                    row["aBuyPrice3_MDEMMS"] = item.BuyPrice3_MDEMMS;

                    row["aBuyPrice4"] = item.BuyPrice4 != -9999999 ? (object)item.BuyPrice4 : DBNull.Value;
                    row["aBuyQuantity4"] = item.BuyQuantity4 != -9999999 ? (object)item.BuyQuantity4 : DBNull.Value;
                    row["aBuyPrice4_NOO"] = item.BuyPrice4_NOO;
                    row["aBuyPrice4_MDEY"] = item.BuyPrice4_MDEY;
                    row["aBuyPrice4_MDEMMS"] = item.BuyPrice4_MDEMMS;
                    row["aBuyPrice5"] = item.BuyPrice5 != -9999999 ? (object)item.BuyPrice5 : DBNull.Value;
                    row["aBuyQuantity5"] = item.BuyQuantity5 != -9999999 ? (object)item.BuyQuantity5 : DBNull.Value;
                    row["aBuyPrice5_NOO"] = item.BuyPrice5_NOO;
                    row["aBuyPrice5_MDEY"] = item.BuyPrice5_MDEY;
                    row["aBuyPrice5_MDEMMS"] = item.BuyPrice5_MDEMMS;
                    row["aBuyPrice6"] = item.BuyPrice6 != -9999999 ? (object)item.BuyPrice6 : DBNull.Value;
                    row["aBuyQuantity6"] = item.BuyQuantity6 != -9999999 ? (object)item.BuyQuantity6 : DBNull.Value;
                    row["aBuyPrice6_NOO"] = item.BuyPrice6_NOO;
                    row["aBuyPrice6_MDEY"] = item.BuyPrice6_MDEY;
                    row["aBuyPrice6_MDEMMS"] = item.BuyPrice6_MDEMMS;

                    row["aBuyPrice7"] = item.BuyPrice7 != -9999999 ? (object)item.BuyPrice7 : DBNull.Value;
                    row["aBuyQuantity7"] = item.BuyQuantity7 != -9999999 ? (object)item.BuyQuantity7 : DBNull.Value;
                    row["aBuyPrice7_NOO"] = item.BuyPrice7_NOO;
                    row["aBuyPrice7_MDEY"] = item.BuyPrice7_MDEY;
                    row["aBuyPrice7_MDEMMS"] = item.BuyPrice7_MDEMMS;
                    row["aBuyPrice8"] = item.BuyPrice8 != -9999999 ? (object)item.BuyPrice8 : DBNull.Value;
                    row["aBuyQuantity8"] = item.BuyQuantity8 != -9999999 ? (object)item.BuyQuantity8 : DBNull.Value;
                    row["aBuyPrice8_NOO"] = item.BuyPrice8_NOO;
                    row["aBuyPrice8_MDEY"] = item.BuyPrice8_MDEY;
                    row["aBuyPrice8_MDEMMS"] = item.BuyPrice8_MDEMMS;
                    row["aBuyPrice9"] = item.BuyPrice9 != -9999999 ? (object)item.BuyPrice9 : DBNull.Value;
                    row["aBuyQuantity9"] = item.BuyQuantity9 != -9999999 ? (object)item.BuyQuantity9 : DBNull.Value;
                    row["aBuyPrice9_NOO"] = item.BuyPrice9_NOO;
                    row["aBuyPrice9_MDEY"] = item.BuyPrice9_MDEY;
                    row["aBuyPrice9_MDEMMS"] = item.BuyPrice9_MDEMMS;
                    row["aBuyPrice10"] = item.BuyPrice10 != -9999999 ? (object)item.BuyPrice10 : DBNull.Value;
                    row["aBuyQuantity10"] = item.BuyQuantity10 != -9999999 ? (object)item.BuyQuantity10 : DBNull.Value;
                    row["aBuyPrice10_NOO"] = item.BuyPrice10_NOO;
                    row["aBuyPrice10_MDEY"] = item.BuyPrice10_MDEY;
                    row["aBuyPrice10_MDEMMS"] = item.BuyPrice10_MDEMMS;

                    row["aSellPrice1"] = item.SellPrice1 != -9999999 ? (object)item.SellPrice1 : DBNull.Value;
                    row["aSellQuantity1"] = item.SellQuantity1 != -9999999 ? (object)item.SellQuantity1 : DBNull.Value;
                    row["aSellPrice1_NOO"] = item.SellPrice1_NOO;
                    row["aSellPrice1_MDEY"] = item.SellPrice1_MDEY;
                    row["aSellPrice1_MDEMMS"] = item.SellPrice1_MDEMMS;
                    row["aSellPrice2"] = item.SellPrice2 != -9999999 ? (object)item.SellPrice2 : DBNull.Value;
                    row["aSellQuantity2"] = item.SellQuantity2 != -9999999 ? (object)item.SellQuantity2 : DBNull.Value;
                    row["aSellPrice2_NOO"] = item.SellPrice2_NOO;
                    row["aSellPrice2_MDEY"] = item.SellPrice2_MDEY;
                    row["aSellPrice2_MDEMMS"] = item.SellPrice2_MDEMMS;
                    row["aSellPrice3"] = item.SellPrice3 != -9999999 ? (object)item.SellPrice3 : DBNull.Value;
                    row["aSellQuantity3"] = item.SellQuantity3 != -9999999 ? (object)item.SellQuantity3 : DBNull.Value;
                    row["aSellPrice3_NOO"] = item.SellPrice3_NOO;
                    row["aSellPrice3_MDEY"] = item.SellPrice3_MDEY;
                    row["aSellPrice3_MDEMMS"] = item.SellPrice3_MDEMMS;
                    row["aSellPrice4"] = item.SellPrice4 != -9999999 ? (object)item.SellPrice4 : DBNull.Value;
                    row["aSellQuantity4"] = item.SellQuantity4 != -9999999 ? (object)item.SellQuantity4 : DBNull.Value;
                    row["aSellPrice4_NOO"] = item.SellPrice4_NOO;
                    row["aSellPrice4_MDEY"] = item.SellPrice4_MDEY;
                    row["aSellPrice4_MDEMMS"] = item.SellPrice4_MDEMMS;
                    row["aSellPrice5"] = item.SellPrice5 != -9999999 ? (object)item.SellPrice5 : DBNull.Value;
                    row["aSellQuantity5"] = item.SellQuantity5 != -9999999 ? (object)item.SellQuantity5 : DBNull.Value;
                    row["aSellPrice5_NOO"] = item.SellPrice5_NOO;
                    row["aSellPrice5_MDEY"] = item.SellPrice5_MDEY;
                    row["aSellPrice5_MDEMMS"] = item.SellPrice5_MDEMMS;
                    row["aSellPrice6"] = item.SellPrice6 != -9999999 ? (object)item.SellPrice6 : DBNull.Value;
                    row["aSellQuantity6"] = item.SellQuantity6 != -9999999 ? (object)item.SellQuantity6 : DBNull.Value;
                    row["aSellPrice6_NOO"] = item.SellPrice6_NOO;
                    row["aSellPrice6_MDEY"] = item.SellPrice6_MDEY;
                    row["aSellPrice6_MDEMMS"] = item.SellPrice6_MDEMMS;
                    row["aSellPrice7"] = item.SellPrice7 != -9999999 ? (object)item.SellPrice7 : DBNull.Value;
                    row["aSellQuantity7"] = item.SellQuantity7 != -9999999 ? (object)item.SellQuantity7 : DBNull.Value;
                    row["aSellPrice7_NOO"] = item.SellPrice7_NOO;
                    row["aSellPrice7_MDEY"] = item.SellPrice7_MDEY;
                    row["aSellPrice7_MDEMMS"] = item.SellPrice7_MDEMMS;
                    row["aSellPrice8"] = item.SellPrice8 != -9999999 ? (object)item.SellPrice8 : DBNull.Value;
                    row["aSellQuantity8"] = item.SellQuantity8 != -9999999 ? (object)item.SellQuantity8 : DBNull.Value;
                    row["aSellPrice8_NOO"] = item.SellPrice8_NOO;
                    row["aSellPrice8_MDEY"] = item.SellPrice8_MDEY;
                    row["aSellPrice8_MDEMMS"] = item.SellPrice8_MDEMMS;
                    row["aSellPrice9"] = item.SellPrice9 != -9999999 ? (object)item.SellPrice9 : DBNull.Value;
                    row["aSellQuantity9"] = item.SellQuantity9 != -9999999 ? (object)item.SellQuantity9 : DBNull.Value;
                    row["aSellPrice9_NOO"] = item.SellPrice9_NOO;
                    row["aSellPrice9_MDEY"] = item.SellPrice9_MDEY;
                    row["aSellPrice9_MDEMMS"] = item.SellPrice9_MDEMMS;
                    row["aSellPrice10"] = item.SellPrice10 != -9999999 ? (object)item.SellPrice10 : DBNull.Value;
                    row["aSellQuantity10"] = item.SellQuantity10 != -9999999 ? (object)item.SellQuantity10 : DBNull.Value;
                    row["aSellPrice10_NOO"] = item.SellPrice10_NOO;
                    row["aSellPrice10_MDEY"] = item.SellPrice10_MDEY;
                    row["aSellPrice10_MDEMMS"] = item.SellPrice10_MDEMMS;

                    row["aMatchPrice"] = item.MatchPrice != -9999999 ? (object)item.MatchPrice : DBNull.Value;
                    row["aMatchQuantity"] = item.MatchQuantity != -9999999 ? (object)item.MatchQuantity : DBNull.Value;
                    row["aOpenPrice"] = item.OpenPrice != -9999999 ? (object)item.OpenPrice : DBNull.Value;
                    row["aClosePrice"] = item.ClosePrice != -9999999 ? (object)item.ClosePrice : DBNull.Value;
                    row["aHighestPrice"] = item.HighestPrice != -9999999 ? (object)item.HighestPrice : DBNull.Value;
                    row["aLowestPrice"] = item.LowestPrice != -9999999 ? (object)item.LowestPrice : DBNull.Value;
                    //row["RepeatingDataFix"] = item.RepeatingDataFix;
                    //row["RepeatingDataJson"] = item.RepeatingDataJson;
                    row["aCheckSum"] = item.CheckSum;
                    dt.Rows.Add(row);
                }
                return dt;
            }
            catch (Exception ex)
            {
                this._app.ErrorLogger.LogError(ex);
                return null;
            }
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
        public string strExchange { get; set; }
    }
    public class SequenceGapInfo
    {
        public long OldSequence { get; set; }
        public long NewSequence { get; set; }
        public List<long> MissingSequences { get; set; } = new List<long>();
    }
}
