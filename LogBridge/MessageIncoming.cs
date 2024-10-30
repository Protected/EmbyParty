using System;
using System.Collections.Generic;
using System.Text;

namespace EmbyParty.LogBridge
{
    public class MessageIncoming<T>
    {
        public string MessageType { get; set; }
        public string Party { get; set; }
        public T Data { get; set; }
    }
}
