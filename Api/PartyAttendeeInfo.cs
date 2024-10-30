using System;
using System.Collections.Generic;
using System.Text;

namespace EmbyParty.Api
{
    public sealed class PartyAttendeeInfo
    {
        public string UserId { get; set; }
        public bool HasPicture { get; set; }
        public string Name { get; set; }
        public bool IsHosting { get; set; }
        public bool IsMe { get; set; }
        public bool IsRemoteControlled { get; set; }
    }
}
