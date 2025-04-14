using BaseSaverLib.Implementations;
using MDDSCore.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SystemCore.Entities;

namespace BaseSaverLib.Interfaces
{
    public interface IMDDSHandler
    {
        Task<EBulkScript> ProcessMessage(string msgType, string rawData);
        //Task<EBulkScript> ProcessMessage(string msgType, string rawData);
        Task<bool> BuildScriptSQL(string[] arrMsg);
    }
}

