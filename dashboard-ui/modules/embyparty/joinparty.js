define(["exports",
    "./partyapiclient.js",
    "./../common/globalize.js",
    "./../actionsheet/actionsheet.js",
    "./../loading/loading.js",
    "./../prompt/prompt.js",
    "./../common/playback/playbackmanager.js"
], function(_exports, _partyapiclient, _globalize, _actionsheet, _loading, _prompt, _playbackmanager) {

    function JoinParty() {
    }
    
    JoinParty.prototype.getCurrentTargetSessionId = function() {
        return _playbackmanager.default.getPlayerInfo()?.currentSessionId;
    },
    
    JoinParty.prototype.show = async function(anchor) {
        _loading.default.show();
        let parties = await _partyapiclient.default.getParties({});
        _loading.default.hide();

        let partyList = parties.map(result => ({
            ...result,
            icon: "hub",
            iconClass: "accentText"
        }));
        
        partyList.push({
            Name: "Create New Party",
            id: "new",
            icon: "add_box"
        });

        let selection = await _actionsheet.default.show({
            title: "Join Party",
            items: partyList,
            positionTo: anchor,
            positionY: "bottom",
            positionX: "right",
            transformOrigin: "right top",
            resolveOnClick: true,
            hasItemIcon: true,
            fields: ["Name"],
            hasItemSelectionState: true
        });
        
        if (selection != "new") {
        
            _loading.default.show();
            let joinresult = await _partyapiclient.default.joinParty(selection, this.getCurrentTargetSessionId())
            _loading.default.hide();
            
            return joinresult;
        } else {
        
            let name = await _prompt.default({
                title: "New Party",
                label: _globalize.default.translate("LabelName"),
                confirmText: _globalize.default.translate("Create")
            });
            
            _loading.default.show();
            let createresult = await _partyapiclient.default.createParty(name, this.getCurrentTargetSessionId());
            _loading.default.hide();
            
            return createresult;
        }
    }

    _exports.default = new JoinParty();
});