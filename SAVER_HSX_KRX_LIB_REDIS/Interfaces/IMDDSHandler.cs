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
        Task<bool> BuildScriptSQL(string[] arrMsg);
    }
}

