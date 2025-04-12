using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PriceLib.Models
{
    public class CMsgSeq
    {
        public string Exchange {  get; set; }
        public string MsgType { get; set; }
        public long SeqMiss { get; set; }
        public long SeqOld { get; set; }
        public long SeqNew { get; set; }
        public DateTime Time {  get; set; }
    }
}
