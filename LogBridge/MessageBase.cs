using System;
using System.Collections.Generic;
using System.Text;

namespace EmbyParty.LogBridge
{
    public class MessageBase
    {
        public string MessageType { get; set; }
        public string Party { get; set; }
        public object Data { get; set; }
    }
}
