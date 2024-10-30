using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmbyParty.Api
{

    public sealed class ChatMessage
    {
        public string Message { get; set; }
    }

    public sealed class NameMessage
    {
        public string Name { get; set; }
    }

    public sealed class RemoteControlMessage
    {
        public string RemoteControl { get; set; }
    }

    public sealed class PingMessage
    {
        public long ts { get; set; }
    }

    public class WebSocketListener : IWebSocketListener
    {
        private ILogger _logger;
        private Plugin _plugin;
        private IJsonSerializer _jsonSerializer;
        private ISessionManager _sessionManager;

        protected PartyManager PartyManager;

        public WebSocketListener(ILogManager logManager, IApplicationHost applicationHost, IJsonSerializer jsonSerializer, ISessionManager sessionManager)
        {
            _logger = logManager.GetLogger("Party");
            _plugin = applicationHost.Plugins.OfType<Plugin>().First();
            _jsonSerializer = jsonSerializer;
            _sessionManager = sessionManager;
            _plugin.OnPartyManagerSet(partyManager => { PartyManager = partyManager; });
        }

        protected void Log(string message)
        {
            _logger.Info(message);
        }

        protected void Debug(string message)
        {
            _logger.Debug(message);
        }

        public Task ProcessMessage(WebSocketMessageInfo message)
        {
            SessionInfo session = _sessionManager.GetSessionByAuthenticationToken(message.Connection.QueryString["api_key"], message.Connection.QueryString["deviceId"], message.Connection.RemoteAddress, null);   
            if (session == null) { return Task.CompletedTask; }

            Debug("Websocket message: " + session.Id + " > " + message.MessageType + " > " + message.Data);

            if (message.MessageType == "Chat")
            {
                ChatMessage chat = _jsonSerializer.DeserializeFromString<ChatMessage>(message.Data);

                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }

                Attendee attendee = party.GetAttendee(session.Id);

                string msgtext = chat.Message.Substring(0, Math.Min(chat.Message.Length, 512));
                if (msgtext.Length == 0) { return Task.CompletedTask; }

                GeneralCommand bounceCommand = new GeneralCommand() { Name = "ChatBroadcast" };
                bounceCommand.Arguments["UserId"] = attendee.UserId;
                bounceCommand.Arguments["Name"] = attendee.DisplayName;
                bounceCommand.Arguments["Message"] = msgtext;

                party.SendEventToAttendees(null, bounceCommand);
                PartyManager.BroadcastToBridges("GeneralCommand", party.Name, bounceCommand);
            }

            //Keep track of remote control target
            if (message.MessageType == "PartyUpdateRemoteControl")
            {
                RemoteControlMessage updateRemoteControl = _jsonSerializer.DeserializeFromString<RemoteControlMessage>(message.Data);

                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }

                party.SetRemoteControl(session.Id, updateRemoteControl.RemoteControl);
            }

            //Keep alive
            if (message.MessageType == "PartyPing")
            {
                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }
                Attendee attendee = party.GetAttendee(session.Id);
                party.SendEventToSingleAttendee(attendee, new GeneralCommand() { Name = "PartyPong" });
            }
            if (message.MessageType == "PartyPong")
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }
                Attendee attendee = party.GetAttendee(session.Id);

                PingMessage ping = _jsonSerializer.DeserializeFromString<PingMessage>(message.Data);
                if (attendee.PendingPing.Remove(ping.ts)) {
                    attendee.Ping = (now.ToUnixTimeMilliseconds() - ping.ts) / 2;
                }
            }

            //User refreshed/reopened the page
            if (message.MessageType == "PartyRefresh")
            {
                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }

                if (party.CurrentQueue != null)
                {
                    Attendee attendee = party.GetAttendee(session.Id);

                    if (!attendee.IsHost)
                    {
                        PartyManager.LateJoiner(party, attendee);
                    }
                    else
                    {
                        PartyManager.RemoveFromParty(attendee.Id);
                    }

                    _sessionManager.SendGeneralCommand(null, attendee.Id, new GeneralCommand() { Name = "PartyRefreshDone" }, new System.Threading.CancellationToken());
                }
            }

            //Host tools for when guest doesn't respond
            if (message.MessageType == "PartyAttendeePlay" || message.MessageType == "PartyAttendeeKick")
            {
                NameMessage nameMessage = _jsonSerializer.DeserializeFromString<NameMessage>(message.Data);

                Party party = PartyManager.GetAttendeeParty(session.Id);
                if (party == null) { return Task.CompletedTask; }

                Attendee attendee = party.GetAttendee(session.Id);
                if (!attendee.IsHost) { return Task.CompletedTask; }

                Attendee target = party.GetAttendeeByDisplayName(nameMessage.Name);
                if (target == null || target.State != AttendeeState.WaitForPlay && target.State != AttendeeState.WaitForSeek || target.MsSinceStateChange < 20000) { return Task.CompletedTask; }

                Debug("-----Party attendee action");

                if (message.MessageType == "PartyAttendeePlay")
                {
                    PartyManager.SyncPartyPlay(party, new Attendee[] { target }, target.State, PlayCommand.PlayNow);
                }

                if (message.MessageType == "PartyAttendeeKick")
                {
                    PartyManager.RemoveFromParty(target.Id);
                    
                    GeneralCommand notification = new GeneralCommand() { Name = "PartyLeave" };
                    notification.Arguments["Name"] = target.DisplayName;
                    notification.Arguments["IsHosting"] = "false";
                    party.SendEventToSingleAttendee(target, notification);
                }
            }

            return Task.CompletedTask;
        }

    }
}
