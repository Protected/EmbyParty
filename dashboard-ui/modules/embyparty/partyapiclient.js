define(["exports",
    "./../emby-apiclient/connectionmanager.js"
], function(_exports, _connectionmanager) {

    function PartyApiClient() {
        this._apiClient = _connectionmanager.default.currentApiClient();
    }
    
    PartyApiClient.prototype.getParties = function(options, signal) {
        let query = {...options};
        return this._apiClient.getJSON(this._apiClient.getUrl("Party/List", query), signal);
    }
            
    PartyApiClient.prototype.getPartyStatus = function(options, signal) {
        let query = {...options};
        return this._apiClient.getJSON(this._apiClient.getUrl("Party/Status", query), signal);
    }

    PartyApiClient.prototype.createParty = function(name, remoteControl) {
        let url = this._apiClient.getUrl("Party/Join");
        return this._apiClient.ajax({
            type: "POST",
            url: url,
            data: JSON.stringify({
                Name: name,
                RemoteControl: remoteControl || null
            }),
            contentType: 'application/json'
        })
            .then((result) => result.json());
    }

    PartyApiClient.prototype.joinParty = function(partyId, remoteControl) {
        let url = this._apiClient.getUrl("Party/Join");
        return this._apiClient.ajax({
            type: "POST",
            url: url,
            data: JSON.stringify({
                Id: partyId,
                RemoteControl: remoteControl || null
            }),
            contentType: 'application/json'
        })
            .then((result) => result.json());
    }
        
    PartyApiClient.prototype.leaveParty = function() {
        let url = this._apiClient.getUrl("Party/Leave");
        return this._apiClient.ajax({
            type: "POST",
            url: url,
            data: JSON.stringify({}),
            contentType: 'application/json'
        })
            .then((result) => { });
    }
    
    PartyApiClient.prototype.getRemoteControlSafety = function(options, signal) {
        let query = {...options};
        return this._apiClient.getJSON(this._apiClient.getUrl("Party/RemoteControlSafety", query), signal);
    }

    _exports.default = new PartyApiClient();
});