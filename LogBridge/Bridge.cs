using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace EmbyParty.LogBridge
{
    public sealed class ChatExternalMessage
    {
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
        public string Message { get; set; }
    }

    public class Bridge : WebSocketBehavior
    {
        private const int PING_INTERVAL = 30000;

        public PartyManager PartyManager { get; set; }
        public IJsonSerializer JsonSerializer { get; set; }

        private Timer _pingTimer;

        protected override void OnOpen()
        {
            PartyManager.Bridges.Add(this);

            _pingTimer = new Timer(PING_INTERVAL);
            _pingTimer.Elapsed += TimeToPing;
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            MessageIncoming<object> message = JsonSerializer.DeserializeFromString<MessageIncoming<object>>(e.Data);

            if (message.MessageType == "Chat")
            {
                Party party = PartyManager.GetPartyByName(message.Party);
                if (party == null) { return; }

                MessageIncoming<ChatExternalMessage> chat = JsonSerializer.DeserializeFromString<MessageIncoming<ChatExternalMessage>>(e.Data);

                string msgtext = chat.Data.Message.Substring(0, Math.Min(chat.Data.Message.Length, 512));
                if (msgtext.Length == 0) { return; }

                GeneralCommand bounceCommand = new GeneralCommand() { Name = "ChatExternal" };
                bounceCommand.Arguments["AvatarUrl"] = chat.Data.AvatarUrl;
                bounceCommand.Arguments["Name"] = chat.Data.Name;
                bounceCommand.Arguments["Message"] = msgtext;

                party.SendEventToAttendees(null, bounceCommand);
            }

            if (message.MessageType == "PartyCheck")
            {
                Party party = PartyManager.GetPartyByName(message.Party);
                SendMessageObject(party != null ? "PartyExists" : "PartyMissing", message.Party, null);
            }

        }

        protected override void OnClose(CloseEventArgs e)
        {
            _pingTimer.Stop();
            _pingTimer.Dispose();

            PartyManager.Bridges.Remove(this);
        }

        private void TimeToPing(object sender, ElapsedEventArgs e)
        {
            Context.WebSocket.Ping();
        }

        public new void Send(string data)
        {
            base.Send(data);
        }

        public void SendMessageObject(string messageType, string partyName, object data)
        {
            MessageBase messageObj = new MessageBase() { MessageType = messageType, Party = partyName, Data = data };
            string message = JsonSerializer.SerializeToString(messageObj);
            Send(message);
        }

    }
}
