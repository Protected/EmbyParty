define(["exports",
    "./../emby-apiclient/connectionmanager.js",
    "./partyapiclient.js",
    "./../emby-apiclient/events.js",
    "./joinparty.js",
    "./../toast/toast.js",
    "./../common/dialogs/confirm.js",
    "./../common/dialogs/alert.js",
    "./../focusmanager.js",
    "./emoji.js",
    "./emoticon.js",
    "./../common/input/api.js",
    "./../common/playback/playbackmanager.js"
], function(_exports, _connectionmanager, _partyapiclient, _events, _joinParty, _toast, _confirm, _alert, _focusmanager, _emoji, _emoticon, _api, _playbackmanager) {

    require(["material-icons", "css!modules/embyparty/partyheader.css"]);

    const LS_SIDEBAR_STATE = "party-sidebar";
    const LS_SIDEBAR_WIDTH = "party-sidebar-width";
    const LS_DOCK_MODE = "party-sidebar-undock";
    const LS_HAS_REMOTE_TARGET  = "party-hasremote";
    
    const PING_INTERVAL = 29000;
    const RECONNECT_DELAYS = [5, 5, 10, 20, 30, 60];
    
    const CHAT_SEPARATOR_DELAY = 3600000;

    function PartyHeader() {
        this._focusOnChat = false;
        this._sidebarMinWidth = 250;
        this._sidebarMinWidthPct = 0.1;
        this._sidebarMaxWidthPct = 0.25;
        
        this._undocked = localStorage.getItem(LS_DOCK_MODE) == "true";
        
        this.partyStatus = null;
        
        this._meseeks = null;  //Timer for blinking seek warning in attendee list
        this._expireSync = null;  //Timer for changing syncing party attendee to expired in the list
        
        this._hasVideoPlayer = false;
        this._videoOsds = [];
        this._nextOsdTag = 1;
        
        this._loggedIn = false;
        this._pingTimer = null;
        this._reconnectAttempt = 0;
        this._reconnectTimer = null;
        
        this._lastLogEntry = null;
        
        window.t_ph = this;
    }
    
    PartyHeader.prototype.getSidebarMinWidth = function() {
        return Math.min(this._sidebarMinWidth, this._sidebarMinWidthPct * window.innerWidth);
    }
    
    PartyHeader.prototype.getSidebarMaxWidth = function() {
        return this._sidebarMaxWidthPct * window.innerWidth;
    }
    
    PartyHeader.prototype.getPartyStatus = async function() {
        this.partyStatus = await _partyapiclient.default.getPartyStatus();
        if (!this.partyStatus?.CurrentParty) {
            this.partyStatus = null;
        }
    }
    
    // ##### Initialize party header and attach it to an anchor #####
    
    PartyHeader.prototype.show = async function(anchor) {
        let apiClient = _connectionmanager.default.currentApiClient();
        this.partyStatus = null;
        
        setTimeout(() => {  //Delay DOM modifications until after page has loaded
        
            let body = document.body,
                bodyElements = [...body.children];
                
            //Wrap entire vanilla app/make it squeezable
            
            let mainWrapper = document.createElement('div'),
                innerWrapper = document.createElement('div');
            
            mainWrapper.className = 'appcontainer';
            innerWrapper.className = body.className;

            mainWrapper.append(innerWrapper);
            body.append(mainWrapper);
            body.className = "";
            for (let bodyElement of bodyElements) {
                innerWrapper.append(bodyElement);
            }
            
            //Add sidebar
            
            let sidebar = document.createElement('div');
            sidebar.className = 'party-sidebar focuscontainer';
            sidebar.innerHTML = `
                <div class="party-sidebarflex">
                    <div class="party-name">
                        <button type="button" is="paper-icon-button-light" class="btnPartyDock md-icon paper-icon-button-light hide" title="Dock party sidebar" aria-label="Dock party sidebar">&#xe6f9;</button>
                        <button type="button" is="paper-icon-button-light" class="btnPartyUndock md-icon paper-icon-button-light hide" title="Undock party sidebar" aria-label="Undock party sidebar">&#xe6aa;</button>
                        <button type="button" is="paper-icon-button-light" class="btnPartyWipe md-icon paper-icon-button-light" title="Empty log" aria-label="Empty log">&#xe28d;</button>
                        <h3></h3>
                    </div>
                    <div class="party-attendees"></div>
                    <div class="party-logcontainer">
                        <div class="party-log"></div>
                    </div>
                    <div class="party-send">
                        <div class="chatwrap"><textarea name="chat" wrap="soft" class="focusable"></textarea></div>
                        <button type="button" is="paper-icon-button-light" class="btnChatSend md-icon paper-icon-button-light" title="Send message" aria-label="Send message">&#xe163;</button>
                    </div>
                </div>
            `
            body.append(sidebar);
            
            let m_pos;
            let resize = (e) => {
                const dx = m_pos - e.x;
                m_pos = e.x;
                this.showSidebar(parseInt(getComputedStyle(sidebar, '').width) + dx, true);
            }
            
            //Sidebar events

            sidebar.addEventListener("mousedown", (e) => {
              if (e.offsetX < 5) {
                m_pos = e.x;
                document.addEventListener("mousemove", resize, false);
              } else {
                this._focusOnChat = false;
              }
            }, false);

            document.addEventListener("mouseup", () => {
                document.removeEventListener("mousemove", resize, false);
            }, false);
            
            sidebar.querySelector(".btnPartyDock").addEventListener("click", () => {
                this.setDocked();
            });
            
            sidebar.querySelector(".btnPartyUndock").addEventListener("click", () => {
                this.setUndocked();
            });
            
            sidebar.querySelector(".btnPartyWipe").addEventListener("click", () => {
                this.clearMessageLog();
            });
            
            let textarea = sidebar.querySelector(".party-send textarea");
            
            textarea.addEventListener("keydown", (e) => {
                e.stopPropagation();
                if (e.key == "Enter") {
                    this.sendMessageFromChatbox();
                    e.preventDefault();
                }
            });
            
            textarea.addEventListener("mousedown", (e) => {
                e.stopPropagation();
            });
            
            textarea.addEventListener("focus", (e) => {
                this._focusOnChat = true;
            });
            
            textarea.addEventListener("blur", (e) => {
                setTimeout(() => {
                    if (this._focusOnChat) {
                        if (!document.activeElement.classList.contains('btnOption')) {
                            this.focusOnChatSend();
                        }
                    }
                }, 1);
            });
            
            sidebar.querySelector(".btnChatSend").addEventListener("click", () => {
                this.sendMessageFromChatbox();
                this.focusOnChatSend();
            });
            
            sidebar.addEventListener("focus", (e) => {
                e.stopPropagation();
            });
            
            sidebar.addEventListener("mouseenter", (e) => {
                e.stopPropagation();
            });
            
            sidebar.addEventListener("pointermove", (e) => {
                e.stopPropagation();
            });
            
            let bodyFix = new MutationObserver((mutations) => {
                //Move late additions to the DOM into the wrapper
                for (let mutation of mutations) {
                    for (let newElement of Array.from(mutation.addedNodes).filter(node => node.nodeType == 1)) {
                        if (newElement.classList.contains("htmlVideoPlayerContainer") || newElement.classList.contains("dialogContainer")) continue;
                        innerWrapper.append(newElement);
                    }
                }
            });
            bodyFix.observe(body, {childList: true});
            
            let bodyCheck = new MutationObserver((mutations) => {
                for (let mutation of mutations) {
                    
                    let newElements = Array.from(mutation.addedNodes).filter(node => node.nodeType == 1);
                    if (newElements.length > 0) {
                    
                        //Catch new videoosd views
                        for (let newElement of newElements) {
                            if (!newElement.classList.contains("view")) continue;
                            
                            let temp = new MutationObserver((mutations) => {
                                if (newElement.classList.contains("view-videoosd-videoosd")) {
                                    this._videoOsds.push(newElement);
                                    newElement.setAttribute("osdtag", this._nextOsdTag++);
                                }
                                temp.disconnect(newElement);
                            });
                            temp.observe(newElement, {attributes: true, attributeFilter: ["class"]});
                        }
                        
                        //Refocus on chat when an auto-focused element is added to the DOM (such as video osd controls)
                        if (this._focusOnChat) {
                            setTimeout(() => {
                                this.focusOnChatSend();
                            }, 1);
                        }
                        
                    }
                    
                    //Remove videoosd views
                    let oldElements = Array.from(mutation.removedNodes).filter(node => node.nodeType == 1);
                    for (let oldElement of oldElements) {
                        if (oldElement.classList.contains("view-videoosd-videoosd")) {
                            this._videoOsds = this._videoOsds.filter(videoosd => videoosd != oldElement);
                        }
                    }
                }
            });
            bodyCheck.observe(innerWrapper, {childList: true});
            
            window.addEventListener("keydown", (e) => {
                if (!this._focusOnChat) {
                    if (this.getSidebarWidth() > 0) {
                        //Prevent accidentally nuking the server with pause commands when someone is trying to type in chat
                        e.stopPropagation();
                    } else if (this.isASyncPlaceholderVisible()) {
                        //Manual shenanigans during synchronization are discouraged
                        e.stopPropagation();
                    }
                }
            }, {capture: true});
            
        }, 1);
        
        //Header (access point)
        
        let partyHeader = document.createElement('div');
        partyHeader.className = 'ef-partyheader';
        
        partyHeader.innerHTML = `
            <button type="button" is="paper-icon-button-light" class="headerPartyButton headerButton headerSectionItem md-icon paper-icon-button-light" title="Join/leave a party" aria-label="Join/leave a party">&#xe9f4;</button>
            <span class="headerPartyName"></span>
            <button type="button" is="paper-icon-button-light" class="headerPartyReturnButton headerSectionItem md-icon paper-icon-button-light hide" title="Return to video" aria-label="Return to video">&#xf06a;</button>
            <button type="button" is="paper-icon-button-light" class="headerPartySidebarButton headerSectionItem md-icon paper-icon-button-light hide" title="Toggle sidebar" aria-label="Toggle sidebar">&#xf114;</button>
        `;
        
        let partyButton = partyHeader.querySelector(".headerPartyButton");
        
        partyButton.addEventListener("click", () => {
            if (this.partyStatus) {
                _confirm.default({
                    title: "Leave " + (this.partyStatus?.CurrentParty?.Name || "party") + "?",
                    confirmText: "Leave",
                    primary: "submit",
                    autoFocus: 1
                })
                    .then(() => _partyapiclient.default.leaveParty())
                    .then(() => {
                        this.partyStatus = null;
                        this.updatePartyStatusDisplay();
                    })
                    .catch(() => {})
                    ;
                let confirmButton = document.querySelector(".confirmDialog button");
                setTimeout(() => confirmButton && _focusmanager.default.focus(confirmButton), 10);
            } else {
                _joinParty.default.show(partyButton)
                    .then(async (result) => {
                        
                        if (!result?.Success) {
                            _alert.default({
                                title: "Unable to join party",
                                text: result?.Reason || undefined,
                                primary: "submit",
                                autoFocus: 1
                            });
                            let confirmButton = document.querySelector(".alertDialog button");
                            setTimeout(() => confirmButton && _focusmanager.default.focus(confirmButton), 10);
                            return;
                        }
                        
                        await this.getPartyStatus();
                        this.updatePartyStatusDisplay();
                        
                        if (!this.partyStatus) {
                            return;
                        }
                        
                        for (let attendee of (this.partyStatus.Attendees || [])) {
                            this.addAttendeeToList(attendee);
                        }
                        
                        if (localStorage.getItem(LS_SIDEBAR_STATE) == "true") {
                            this.showSidebar(localStorage.getItem(LS_SIDEBAR_WIDTH));
                        }
                        
                        let player = _playbackmanager.default.getCurrentPlayer();
                        if (player != null) {
                            let remoteControlSafety = await _partyapiclient.default.getRemoteControlSafety({RemoteControl: player.currentSessionId});
                            if (remoteControlSafety && !remoteControlSafety.IsSafe) {
                                _playbackmanager.default.setDefaultPlayerActive();
                                _toast.default({text: "Remote control disabled because it would create a loop.", icon: "warning"});
                            }
                        }
                    })
                    .catch(() => {});
                    
            }
        });
        
        partyHeader.querySelector(".headerPartyReturnButton").addEventListener("click", () => {
            this.openCurrentVideoPlayer();
        });
        
        partyHeader.querySelector(".headerPartySidebarButton").addEventListener("click", () => {
            if (this.getSidebarWidth() > 0) {
                this.hideSidebar();
            } else {
                this.showSidebar(Math.min(Math.max(localStorage.getItem(LS_SIDEBAR_WIDTH) || this.getSidebarMinWidth(), this.getSidebarMinWidth()), this.getSidebarMaxWidth()));
            }
        });
    
        anchor.prepend(partyHeader);
        
        //Initialize things
    
        try {
            await this.getPartyStatus();
            this.updatePartyStatusDisplay();
        } catch (e) { }
        
        let returning = () => {
            _events.default.off(apiClient, "websocketopen", returning);
            if (this.partyStatus?.CurrentQueue) {
                this.sendRefreshNotification();
            }
            if (this.partyStatus && localStorage.getItem(LS_HAS_REMOTE_TARGET) == this.partyStatus.Id) {
                this.setRemoteControlInParty(null);
            }
        };
        _events.default.on(apiClient, "websocketopen", returning);
        
        setTimeout(() => {
            //Things that use both the sidebar and party should run after mounting sidebar and after party status retrieval
            //These sequence bump timers can be promisified later
            
            for (let attendee of (this.partyStatus?.Attendees || [])) {
                this.addAttendeeToList(attendee);
            }
            
            if (this.partyStatus) {
                if (localStorage.getItem(LS_SIDEBAR_STATE) == "true") {
                    this.showSidebar(localStorage.getItem(LS_SIDEBAR_WIDTH));
                }
            }
        }, 2);
        
        if (apiClient.getCurrentUserId()) {
            partyHeader.style.display = 'block';
        }
        
        //Event handlers
        
        let setUpReconnectWebSocket = (attempt) => {
            this._reconnectAttempt = attempt;
            this._reconnectTimer = setTimeout(() => reconnectWebSocket(), RECONNECT_DELAYS[Math.min(this._reconnectAttempt, RECONNECT_DELAYS.length - 1)] * 1000);
        }
        
        let reconnectWebSocket = () => {
            this._reconnectTimer = null;
            if (!this.partyStatus) return;
            if (!apiClient || apiClient.isWebSocketOpenOrConnecting()) return;
            
            console.log("Attempting to reconnect to websocket.");
            apiClient.ensureWebSocket();
            
            setUpReconnectWebSocket(this._reconnectAttempt + 1);
        }
        
        let abortReconnectWebSocket = () => {
            if (this._reconnectTimer) {
                clearTimeout(this._reconnectTimer);
            }
            this._reconnectTimer = null;
        }
        
        _events.default.on(apiClient, "websocketopen", async () => {
            if (this._reconnectAttempt) {
                await this.getPartyStatus();
                this.updatePartyStatusDisplay();
                this.addGenericMessageToLog("Reconnect", "Connection to server re-established" + (this._reconnectAttempt > 1 ? ` after ${this._reconnectAttempt} attempts` : ""), "redo");
            }
        });
        _events.default.on(apiClient, "websocketclose", () => {
            setUpReconnectWebSocket(0);
        });
        
        _events.default.on(apiClient, "message", (...args) => this.webSocketMessageHandler.apply(this, args));
        
        _events.default.on(_playbackmanager.default, "playerchange", (...args) => this.playerChangeHandler.apply(this, args));
        
        _events.default.on(_connectionmanager.default, "localusersignedin", (e, serverId, userId) => {
            partyHeader.style.display = 'block';
            this._loggedIn = true;
        });
        
        _events.default.on(_connectionmanager.default, "localusersignedout", (e) => {
            partyHeader.style.display = 'none';
            this._loggedIn = false;
            abortReconnectWebSocket();
            this.partyStatus = null;
            this.updatePartyStatusDisplay();
        });
        
        _events.default.on(_api.default, "UserUpdated", (e, apiClient, data) => { partyHeader.style.display = (data ? 'block' : 'none'); });
        
        let captureClick = (e) => {
            if (this.isASyncPlaceholderVisible()) {
                //Manual pause/pause during synchronization is discouraged
                e.stopPropagation();
            }
        }
        
        _events.default.on(_playbackmanager.default, "playbackstart", () => {
            for (let videoOsd of this._videoOsds) {
                videoOsd.addEventListener("click", captureClick, {capture: true});
            }
        });
        _events.default.on(_playbackmanager.default, "playbackstop", () => {
            for (let videoOsd of this._videoOsds) {
                videoOsd.removeEventListener("click", captureClick, {capture: true});
            }
        });
        
    }
    
    PartyHeader.prototype.updatePartyStatusDisplay = function() {
        let skinHeader = document.querySelector(".skinHeader");
        let partyHeader = document.querySelector('.ef-partyheader');
        let partyButton = partyHeader.querySelector('.headerPartyButton');
        let partySidebarButton = partyHeader.querySelector('.headerPartySidebarButton');
        let partySidebar = document.querySelector('.party-sidebar');
        
        if (this.partyStatus) {
            let partyname = this.partyStatus.CurrentParty.Name + " (" + this.partyStatus.Attendees.length + ")"
            partyHeader.querySelector('.headerPartyName').textContent = partyname;
            if (partySidebar) partySidebar.querySelector('.party-name > h3').textContent = partyname;
            
            partyHeader.classList.add('inparty');
            partyButton.title = "Leave this party";
            
            if (this.partyStatus.Attendees.length > 1) {
                skinHeader.classList.add("guestsAreIn");
            } else {
                skinHeader.classList.remove("guestsAreIn");
            }
            
            let host = this.partyStatus.Attendees.find(attendee => attendee.IsHosting);
            if (host?.IsMe) {
                skinHeader.classList.add("hostIsMe");
            } else {
                skinHeader.classList.remove("hostIsMe");
            }
            
            partySidebarButton.classList.remove('hide');
            
        } else {
            this.hideSidebar(true);
            this.clearAttendeeList();
            this.clearMessageLog();
        
            partyHeader.querySelector('.headerPartyName').textContent = "";
            
            partyHeader.classList.remove('inparty');
            partyButton.title = "Join a party";
            
            skinHeader.classList.remove("guestsAreIn");
            skinHeader.classList.remove("hostIsMe");
         
            partySidebarButton.classList.add('hide');
        }
        
        this.updatePartyReturnButtonDisplay();
    }
    
    PartyHeader.prototype.updatePartyReturnButtonDisplay = function() {
        let partyHeader = document.querySelector('.ef-partyheader');
        let partyReturnButton = partyHeader.querySelector('.headerPartyReturnButton');
        if (this.partyStatus) {
            if (!this._hasVideoPlayer && this.partyStatus.CurrentQueue) {
                partyReturnButton.classList.remove('hide');
            } else {
                partyReturnButton.classList.add('hide');
            }        
        } else {
            partyReturnButton.classList.add('hide');
        }
    }
    
    PartyHeader.prototype.openCurrentVideoPlayer = function() {
        if (!this.partyStatus) return false;
        let apiClient = _connectionmanager.default.currentApiClient();
        
        return _playbackmanager.default.play({
            serverId: apiClient.serverId(),
            mediaSourceId: this.partyStatus.MediaSourceId,
            ids: this.partyStatus.CurrentQueue,
            startIndex: this.partyStatus.CurrentIndex,
            audioStreamIndex: this.partyStatus.AudioStreamIndex,
            subtitleStreamIndex: this.partyStatus.SubtitleStreamIndex,
            startPositionTicks: 0
        });
    }
    
    // ##### Web socket (our GeneralCommands) #####
    
    PartyHeader.prototype.webSocketMessageHandler = async function(e, message) {
        let data = message.Data;
    
        //console.log("##### " + message.MessageType + ":", data);
    
        //Blink circle icon when receiving a seek order
        if (message.MessageType == "Playstate" && data.Command == "Seek") {
            let isWaiting = document.querySelector('.party-attendees > .party-isme .isWaiting');
            if (!isWaiting || isWaiting.classList.contains('hide')) {
                let me = document.querySelector('.party-attendees > .party-isme');
                me.classList.add('meSeeks');
                me.title = "Position adjusted by party";
                if (this._meseeks) clearTimeout(this._meseeks);
                this._meseeks = setTimeout(() => {
                    me.classList.remove('meSeeks');
                    me.title = "This is me!";
                    this._meseeks = null;
                }, 2000);
            }
        }
        
        //Infer party status from play orders
        
        if (message.MessageType == "Play" && data.PlayCommand == "PlayNow" && this.partyStatus) {
            this.partyStatus.CurrentQueue = data.ItemIds.slice();
            this.partyStatus.CurrentIndex = data.StartIndex;
            this.partyStatus.MediaSourceId = data.MediaSourceId;
            this.partyStatus.AudioStreamIndex = data.AudioStreamIndex;
            this.partyStatus.SubtitleStreamIndex = data.SubtitleStreamIndex;
        }
        
        if (message.MessageType == "Playstate" && data.Command == "Stop") {
            this.partyStatus.CurrentQueue = null;
            this.partyStatus.CurrentIndex = 0;
            this.clearAllAttendeeIndicators();
        }

        if (message.MessageType != "GeneralCommand") { return; }
        
        let skinHeader = document.querySelector(".skinHeader");
        
        //Custom GeneralCommands
        
        if (data.Name == "PartyJoin") {
            if (this.partyStatus?.Attendees) {
                let attendee = {Name: data.Arguments.Name, UserId: data.Arguments.UserId, IsHosting: false, HasPicture: data.Arguments.HasPicture && data.Arguments.HasPicture != "false", IsRemoteControlled: data.Arguments.IsRemoteControlled == "true"};
                this.partyStatus.Attendees.push(attendee);
                this.updatePartyStatusDisplay();
                this.addAttendeeToList(attendee);

                this.addGenericMessageToLog("Join", data.Arguments.Name, "login");
                if (this.getSidebarWidth() < 1) {
                    _toast.default({text: data.Arguments.Name + " joined " + this.partyStatus.CurrentParty.Name + ".", icon: "login"});
                }
            }
        }
        if (data.Name == "PartyLeave") {
            if (this.partyStatus?.Attendees) {
                let target = this.partyStatus.Attendees.find(attendee => attendee.Name == data.Arguments.Name);
                this.partyStatus.Attendees = this.partyStatus.Attendees.filter(attendee => attendee.Name != data.Arguments.Name);
                this.updatePartyStatusDisplay();
                this.removeAttendeeFromList(data.Arguments.Name);
                
                if (target?.IsMe) {
                    await this.getPartyStatus();
                    this.updatePartyStatusDisplay();
                    this.placeholdersHidden();
                    return;
                }
                
                this.addGenericMessageToLog("Part", data.Arguments.Name, "logout");
                if (this.getSidebarWidth() < 1) {
                    _toast.default({text: data.Arguments.Name + " left " + this.partyStatus.CurrentParty.Name, icon: "logout"});
                }
            }
        }
        
        if (data.Name == "PartyUpdateName") {
            if (this.partyStatus?.Attendees) {
                let target = this.partyStatus.Attendees.find(attendee => attendee.Name == data.Arguments.OldName);
                if (target) {
                    this.renameAttendeeItem(target.Name, data.Arguments.NewName);
                    target.Name = data.Arguments.NewName;
                    this.addGenericMessageToLog("Name change", data.Arguments.OldName + " is now " + data.Arguments.NewName, "id_card");
                }
            }
        }
        
        if (data.Name == "PartyUpdateHost") {
            if (this.partyStatus?.Attendees) {
                let oldHost = this.partyStatus.Attendees.find(attendee => attendee.IsHosting);
                if (oldHost) {
                    oldHost.IsHosting = false;
                    this.setAttendeeItemHost(oldHost.Name, false);
                }
                let target = this.partyStatus.Attendees.find(attendee => attendee.Name == data.Arguments.Host);
                if (target) {
                    target.IsHosting = true;
                    this.setAttendeeItemHost(target.Name, true);
                    if (target.IsMe) {
                        skinHeader.classList.add("hostIsMe");
                    } else {
                        skinHeader.classList.remove("hostIsMe");
                    }
                                        
                    this.addGenericMessageToLog("Host", target.Name, "hub");
                    if (this.getSidebarWidth() < 1) {
                        _toast.default({text: target.Name + " is now hosting.", icon: "hub"});
                    }
                } else if (oldHost) {
                    this.addGenericMessageToLog("Host", "<s>" + oldHost.Name + "</s>", "hub");
                    this.partyStatus.CurrentQueue = null;
                    this.clearAllAttendeeIndicators();
                    this.updatePartyReturnButtonDisplay();
                    if (this.getSidebarWidth() < 1) {
                        _toast.default({text: oldHost.Name + " is no longer hosting.", icon: "hub"});
                    }
                }
            }
        }
        
        if (data.Name == "ChatBroadcast") {
            this.addChatMessageToLog(data.Arguments);
        }
        
        if (data.Name == "ChatExternal") {
            this.addChatExternalMessageToLog(data.Arguments);
        }
        
        if (data.Name == "PartyLogMessage") {
            let type = data.Arguments.Type;
            let subject = data.Arguments.Subject;
            let icon = null;
            
            if (this.getSidebarWidth() < 1) {
                if (type == "Pause") {
                    _toast.default({text: data.Arguments.Subject + " paused the playback.", icon: "pause"});
                }
                if (type == "Unpause") {
                    _toast.default({text: data.Arguments.Subject + " unpaused the playback.", icon: "resume"});
                }
            }
            
            if (type == "Now Playing") icon = "play_arrow";
            if (type == "Pause") icon = "pause";
            if (type == "Unpause") icon = "resume";
            if (type == "Reject") { icon = "do_not_disturb_on"; subject = "Not all attendees have permission to access this item."; }
            this.addGenericMessageToLog(type, subject, icon);
        }
        
        let setupExpireSync = () => {
            if (this._expireSync != null) clearTimeout(this._expireSync);
            this._expireSync = setTimeout(() => {
                let attendees = document.querySelectorAll('.party-attendees > :not(.party-ishost):not(.party-isremotecontrolled)');
                for (let attendee of attendees) {
                    if (!attendee.querySelector('.isWaiting').classList.contains("hide")) {
                        attendee.querySelector('.isWaiting').classList.add("hide");
                        attendee.querySelector('.isNotResponding').classList.remove("hide");
                        if (skinHeader.classList.contains("hostIsMe")) {
                            attendee.querySelector('.btnSendAttendeePlay').classList.remove("hide");
                            attendee.querySelector('.btnSendAttendeeKick').classList.remove("hide");
                        }
                    }
                }
            }, 21000);
        }
        
        if (data.Name == "PartySyncStart") {
            this.placeholdersSyncing();
            
            for (let attendee of document.querySelectorAll('.party-attendees > :not(.party-ishost):not(.party-isremotecontrolled)')) {
                if (!attendee.querySelector('.isSyncing').classList.contains("hide")) continue;  //Guest started before host
                attendee.querySelector('.isWaiting').classList.remove("hide");
            }
            
            setupExpireSync();
        }
        
        if (data.Name == "PartySyncWaiting") {
            
            let attendee = this.findAttendeeListItem(data.Arguments.Name);
            if (attendee) {
                attendee.querySelector('.isWaiting').classList.add("hide");
                attendee.querySelector('.isNotResponding').classList.add("hide");
                attendee.querySelector('.btnSendAttendeePlay').classList.add("hide");
                attendee.querySelector('.btnSendAttendeeKick').classList.add("hide");
                attendee.querySelector('.isSyncing').classList.remove("hide");
            }
            
            let target = this.partyStatus.Attendees.find(attendee => attendee.Name == data.Arguments.Name);
            if (target?.IsMe) {
                this.placeholdersReady();
            }
        }
        
        if (data.Name == "PartySyncReset") {
            
            let attendee = this.findAttendeeListItem(data.Arguments.Name);
            if (attendee) {
                attendee.querySelector('.isSyncing').classList.add("hide");
                attendee.querySelector('.isNotResponding').classList.add("hide");
                attendee.querySelector('.btnSendAttendeePlay').classList.add("hide");
                attendee.querySelector('.btnSendAttendeeKick').classList.add("hide");
                attendee.querySelector('.isWaiting').classList.remove("hide");
            }
            
            setupExpireSync();
            
            let target = this.partyStatus.Attendees.find(attendee => attendee.Name == data.Arguments.Name);
            if (target?.IsMe) {
                this.placeholdersSyncing();
            }
        }
        
        if (data.Name == "PartySyncEnd") {
            if (this._expireSync != null) { clearTimeout(this._expireSync); this._expireSync = null; }
            this.placeholdersHidden();
            this.clearAllAttendeeIndicators();
        }
        
        if (data.Name == "PartyUpdateRemoteControlled") {
           let attendees = document.querySelectorAll('.party-attendees > :not(.party-ishost)');
            for (let attendee of attendees) {
                if (attendee.querySelector('.actualname').textContent == data.Arguments.Name) {
                    if (data.Arguments.Status == "true") {
                        attendee.classList.add("party-isremotecontrolled");
                        attendee.querySelector('.isRemoteControlled').classList.remove("hide");
                    } else {
                        attendee.classList.remove("party-isremotecontrolled");
                        attendee.querySelector('.isRemoteControlled').classList.add("hide");
                    }
                    break;
                }
            }
        }
        
        if (data.Name == "PartyRefreshDone") {
            await this.getPartyStatus();
            this.updatePartyStatusDisplay();
            let video = document.querySelector('video.htmlvideoplayer');
            if (!video) {
                return;
            }
            
            if (!this.partyStatus) {
                video.stop();
                return;
            }
            
            let host = this.partyStatus.Attendees.find(attendee => attendee.IsHosting);
            if (!host?.IsMe && video.paused) {
                //Possibly in the future pause play session in this case
            }
        }
        
        if (data.Name == "PartyPing") {
            this.partyPong(data.Arguments.ts);
        }
    }
    
    PartyHeader.prototype.startPartyPing = function() {
        if (this._pingTimer) clearinterval(this._pingTimer);
        
        let apiClient = _connectionmanager.default.currentApiClient();
        this._pingTimer = setInterval(() => {
            if (this.partyStatus) {
                apiClient.sendWebSocketMessage("PartyPing", JSON.stringify({Id: this.partyStatus?.CurrentParty?.Id}));
            }
        }, PING_INTERVAL);
    }
    
    PartyHeader.prototype.stopPartyPing = function() {
        if (this._pingTimer) {
            clearinterval(this._pingTimer);
            this._pingTimer = null;
        }
    }
    
    PartyHeader.prototype.partyPong = function(ts) {
        let apiClient = _connectionmanager.default.currentApiClient();
        apiClient.sendWebSocketMessage("PartyPong", JSON.stringify({ts: ts}));
    }
    
    // ##### Player button placeholders and changes #####
    
    PartyHeader.prototype.getSyncPlaceholder = function(videoOsd) {
        let sync = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-sync');
        if (!sync) {
            let playpause = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-btnPause');
            sync = document.createElement('button');
            sync.className = "osdIconButton videoOsd-sync autofocus paper-icon-button-light videoOsd-btnPause-autolayout";
            sync.innerHTML = `<i class="md-icon md-icon-fill osdIconButton-icon autortl">&#xe627;</i>`;
            sync.title = "Synchronizing...";
            if (playpause) playpause.after(sync);
        }
        return sync;
    }
    
    PartyHeader.prototype.getSyncReadyPlaceholder = function(videoOsd) {
        let ready = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-ready');
        if (!ready) {
            let playpause = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-btnPause');
            ready = document.createElement('button');
            ready.className = "osdIconButton videoOsd-ready autofocus paper-icon-button-light videoOsd-btnPause-autolayout";
            ready.innerHTML = `<i class="md-icon md-icon-fill osdIconButton-icon autortl">&#xe5ca;</i>`;
            ready.title = "Ready, please wait for host";
            if (playpause) playpause.after(ready);
        }
        return ready;
    }
    
    PartyHeader.prototype.getSyncNowPlaying = function() {
        let sync = document.querySelector('.nowPlayingBar .syncPlaceholder');
        if (!sync) {
            let playpause = document.querySelector('.nowPlayingBar .playPauseButton');
            sync = document.createElement('button');
            sync.className = "nowPlayingBar-hidetv syncPlaceholder mediaButton md-icon md-icon-fill paper-icon-button-light";
            sync.innerHTML = `&#xe627;`;
            sync.title = "Synchronizing...";
            if (playpause) playpause.after(sync);
        }
        return sync;
    }
    
    PartyHeader.prototype.getSyncReadyNowPlaying = function() {
        let ready = document.querySelector('.nowPlayingBar .readyPlaceholder');
        if (!ready) {
            let playpause = document.querySelector('.nowPlayingBar .playPauseButton');
            ready = document.createElement('button');
            ready.className = "nowPlayingBar-hidetv readyPlaceholder mediaButton md-icon md-icon-fill paper-icon-button-light";
            ready.innerHTML = `&#xe5ca;`;
            ready.title = "Ready, please wait for host";
            if (playpause) playpause.after(ready);
        }
        return ready;
    }
    
    PartyHeader.prototype.placeholdersHidden = function() {
        let pause = document.querySelector('.nowPlayingBar .playPauseButton');
        if (pause) {
            pause.style.display = "block";
            this.getSyncNowPlaying().style.display = "none";
            this.getSyncReadyNowPlaying().style.display = "none";
        }
    
        for (let videoOsd of this._videoOsds) {
            pause = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-btnPause');
            if (pause) {
                pause.style.display = "flex";
                this.getSyncPlaceholder(videoOsd).style.display = "none";
                this.getSyncReadyPlaceholder(videoOsd).style.display = "none";
            }
        }
    }
    
    PartyHeader.prototype.placeholdersSyncing = function() {
        let pause = document.querySelector('.nowPlayingBar .playPauseButton');
        if (pause) {
            pause.style.display = "none";
            this.getSyncNowPlaying().style.display = "block";
            this.getSyncReadyNowPlaying().style.display = "none";
        }
    
        for (let videoOsd of this._videoOsds) {
            pause = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-btnPause');
            if (pause) {
                pause.style.display = "none";
                this.getSyncPlaceholder(videoOsd).style.display = "flex";
                this.getSyncReadyPlaceholder(videoOsd).style.display = "none";
            }        
        }
    }
    
    PartyHeader.prototype.placeholdersReady = function() {
        let pause = document.querySelector('.nowPlayingBar .playPauseButton');
        if (pause) {
            pause.style.display = "none";
            this.getSyncNowPlaying().style.display = "none";
            this.getSyncReadyNowPlaying().style.display = "block";
        }
        
        for (let videoOsd of this._videoOsds) {
            pause = videoOsd.querySelector('.videoOsd-belowtransportbuttons .videoOsd-btnPause');
            if (pause) {
                pause.style.display = "none";
                this.getSyncPlaceholder(videoOsd).style.display = "none";
                this.getSyncReadyPlaceholder(videoOsd).style.display = "flex";
            }
        }
    }
    
    PartyHeader.prototype.isASyncPlaceholderVisible = function() {
        for (let videoOsd of this._videoOsds) {
            if (this.getSyncPlaceholder(videoOsd).style.display == "flex" || this.getSyncReadyPlaceholder(videoOsd).style.display == "flex") {
                return true;
            }
        }
        return false;
    }
    
    // ##### Remote control support #####
    
    PartyHeader.prototype.getCurrentTargetSessionId = function() {
        return _playbackmanager.default.getPlayerInfo()?.currentSessionId;
    }
    
    PartyHeader.prototype.setRemoteControlInParty = function(targetId) {
        let apiClient = _connectionmanager.default.currentApiClient();
        localStorage.setItem(LS_HAS_REMOTE_TARGET, targetId != null ? this.partyStatus.Id : null);
        apiClient.sendWebSocketMessage("PartyUpdateRemoteControl", JSON.stringify({RemoteControl: targetId}));
    }
    
    PartyHeader.prototype.playerChangeHandler = async function(e, newPlayer, newTarget, previousPlayer) {
        //console.log("Player change handler:", newPlayer, newTarget, previousPlayer);
    
        if (newPlayer && !newPlayer.isLocalPlayer) {
            let remoteControlSafety = await _partyapiclient.default.getRemoteControlSafety({RemoteControl: newPlayer.currentSessionId});
            if (remoteControlSafety && !remoteControlSafety.IsSafe) {
                _playbackmanager.default.setDefaultPlayerActive();
                newPlayer = null;
                _toast.default({text: "Remote control denied because it would create a loop.", icon: "warning"});
            }
        }
    
        if (newPlayer && this.partyStatus) {
            this.setRemoteControlInParty(newPlayer?.currentSessionId || null);
        }
        
        this._hasVideoPlayer = !!newPlayer;
        
        setTimeout(async () => {
            this.updatePartyReturnButtonDisplay();
            
            if (previousPlayer && !previousPlayer.isLocalPlayer && !this._hasVideoPlayer) {
                //Cease to be a remote controller
                await this.getPartyStatus();  //Former remote controller is missing playback queue
                if (this.partyStatus?.CurrentQueue) {
                    this.openCurrentVideoPlayer();
                }
            }
        }, 100);
    }
    
    // ##### Sidebar log (chat) #####
    
    PartyHeader.prototype.getProfilePictureHTML = function(userId) {
        let userAttendee = (this.partyStatus?.Attendees || []).find(attendee => attendee.UserId == userId);
        if (userAttendee?.HasPicture) {
            return `<img class="profilepic" src="/emby/Users/${userId}/Images/Primary?height=40&tag=&quality=100">`;
        } else {
            return `<div class="profilepic"><i class="md-icon">&#xe7FD;</i></div>`;
        }
    }
    
    PartyHeader.prototype.getExternalProfileHTML = function(avatarUrl) {
        if (avatarUrl) {
            return `<img class="profilepic" src="${avatarUrl}">`;
        } else {
            return `<div class="profilepic"><i class="md-icon">&#xe7FD;</i></div>`;
        }
    }
    
    PartyHeader.prototype.sendMessageFromChatbox = function() {
        let textarea = document.querySelector(".party-send textarea");
        this.sendChatMessage(textarea.value);
        textarea.value = "";
    }
    
    PartyHeader.prototype.sendChatMessage = function(message) {
        if (!message) return;
        let apiClient = _connectionmanager.default.currentApiClient();
        
        let escapeRegex = (str) => str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        let codepointsToEmoji = (str) => str.split("-").map(codepoint => String.fromCodePoint(parseInt(codepoint, 16))).join("");
        
        message = message.replaceAll(/\>\!(.*?)\!\</g,'||$1||');
        
        message = message.replaceAll(/\:([A-Za-z0-9_]+)\:/g, (all, shortname) => {
            if (_emoji.default[shortname]) {
                return codepointsToEmoji(_emoji.default[shortname]);
            }
            return all;
        });

        message = message.replaceAll(new RegExp('(^|(?<=\\W))(' + Object.keys(_emoticon.default).map(ei => escapeRegex(ei)).join("|") + ')((?=\\W)|$)', 'g'), (all, d, ei) => {
            if (_emoticon.default[ei]) {
                return codepointsToEmoji(_emoticon.default[ei]);
            }
            return all;
        });
        
        message = String.fromCodePoint(0x200B) + message;  //Straighten out Emby's encoding detection
        
        apiClient.sendWebSocketMessage("Chat", JSON.stringify({Message: message}));
    }
    
    PartyHeader.prototype.sendRefreshNotification = function() {
        let apiClient = _connectionmanager.default.currentApiClient();
        apiClient.sendWebSocketMessage("PartyRefresh", JSON.stringify({}));
    }
    
    PartyHeader.prototype.isNameMe = function(name) {
        let target = this.partyStatus.Attendees.find(attendee => attendee.Name == name);
        return target && target.IsMe;
    }
    
    PartyHeader.prototype.addChatMessageToLog = function(messageData) {
        return this.addChatToLog(
            messageData.Name,
            messageData.Message,
            this.getProfilePictureHTML(messageData.UserId),
            null,
            this.isNameMe(messageData.Name)
        );
    }
    
    PartyHeader.prototype.addChatExternalMessageToLog = function(externalMessageData) {
        return this.addChatToLog(
            externalMessageData.Name,
            externalMessageData.Message,
            this.getExternalProfileHTML(externalMessageData.AvatarUrl),
            "externalmessage",
            false
        );
    }
    
    PartyHeader.prototype.addChatToLog = function(name, message, avatarHTML, extraClasses, forceScroll) {
        let newEntry = document.createElement('div');
        newEntry.className = "chatmessage" + (extraClasses ? " " + extraClasses : "");
        
        message = message
            .replaceAll(/\*\*(.*?)\*\*/g,'<b>$1</b>')
            .replaceAll(/\*(.*?)\*/g,'<i>$1</i>')
            .replaceAll(/\_\_(.*?)\_\_/g,'<u>$1</u>')
            .replaceAll(/\_(.*?)\_/g,'<i>$1</i>')
            .replaceAll(/~~(.*?)~~/g,'<s>$1</s>')
            .replaceAll(/`(.*?)`/g,'<pre>$1</pre>')
            .replaceAll(/\|\|(.*?)\|\|/g,'<span class="spoiler">$1</span>')
            .replaceAll(/\>\!(.*?)\!\</g,'<span class="spoiler">$1</span>')
            ;

        newEntry.innerHTML = `
            ${avatarHTML || ""}
            <div>
                <div class="name"><span class="actualname">${name}</span></div>
                <div class="message">${message}</div>
                <div class="timestamp">${this.getCurrentTimestamp()}</div>
            </div>
        `;
        
        if (this._lastLogEntry !== null && Date.now() - this._lastLogEntry > CHAT_SEPARATOR_DELAY) {
            this.addSeparatorToLog();
        }
        
        return this.addMessageElementToLog(newEntry, forceScroll);
    }
    
    PartyHeader.prototype.addGenericMessageToLog = function(type, subject, icon) {
        let newEntry = document.createElement('div');
        newEntry.className = "chatmessage generic";
        
        let iconpart = icon ? `<span class="md-icon">${icon}</span>` : "";
        
        newEntry.innerHTML = `
            <div>
                <div class="message"><b>${iconpart}${type}</b> ${subject}</div>
                <div class="timestamp">${this.getCurrentTimestamp()}</div>
            </div>
        `;
        
        if (this._lastLogEntry !== null && Date.now() - this._lastLogEntry > CHAT_SEPARATOR_DELAY) {
            this.addSeparatorToLog();
        }
        
        return this.addMessageElementToLog(newEntry);
    }
    
    PartyHeader.prototype.addSeparatorToLog = function() {
        let newEntry = document.createElement('div');
        newEntry.className = "chatseparator";
        return this.addMessageElementToLog(newEntry);
    }
    
    PartyHeader.prototype.getCurrentTimestamp = function(now) {
        if (!now) now = new Date();
        return `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
    }
    
    PartyHeader.prototype.addMessageElementToLog = function(element, forceScroll) {
        let container = document.querySelector('.party-sidebar .party-logcontainer');
        let messageLog = document.querySelector('.party-sidebar .party-log');
        
        let atBottom = container.scrollTop > (container.scrollHeight - container.clientHeight) - 30;
        
        messageLog.append(element);
        
        let oldest = [...messageLog.children].slice(0, -100);
        for (let child of oldest) {
            child.remove();
        }
        
        if (atBottom || forceScroll) {
            container.scrollTop = container.scrollHeight;
        }
        
        this._lastLogEntry = Date.now();
    }
    
    PartyHeader.prototype.clearMessageLog = function() {
        let messageLog = document.querySelector('.party-sidebar .party-log');
        messageLog.innerHTML = "";
    }
    
    // ##### Other sidebar control methods #####
    
    PartyHeader.prototype.getSidebarWidth = function() {
        return parseInt(getComputedStyle(document.querySelector(".party-sidebar"), '').width);
    }
    
    PartyHeader.prototype.showSidebar = function(width, saveWidth) {
        if (width < this.getSidebarMinWidth()) width = this.getSidebarMinWidth();
        if (width > this.getSidebarMaxWidth()) width = this.getSidebarMaxWidth();
        let sidebar = document.querySelector(".party-sidebar");
        let appcontainer = document.querySelector(".appcontainer");
        let currentWidth = this.getSidebarWidth();
        let offset = width - currentWidth;
        sidebar.style.display = "block";
        sidebar.style.width = `${width}px`;
        appcontainer.style.right = sidebar.style.width;
        if (this._undocked) {
            this.setUndocked();
        } else {
            this.setDocked();
        }
        document.body.style.setProperty('--party-sidebar-width', this._undocked ? 0 : sidebar.style.width);
        localStorage.setItem(LS_SIDEBAR_STATE, true);
        if (saveWidth) {
            localStorage.setItem(LS_SIDEBAR_WIDTH, width);
        }
    }
    
    PartyHeader.prototype.focusOnChatSend = function() {
        _focusmanager.default.focus(document.querySelector('.party-send textarea'));
    }
    
    PartyHeader.prototype.hideSidebar = function(transient) {
        let sidebar = document.querySelector(".party-sidebar");
        if (!sidebar) return;
        sidebar.style.width = 0;
        sidebar.style.display = "none";
        document.querySelector(".appcontainer").style.right = 0;
        document.body.style.setProperty('--party-sidebar-width', 0);
        if (!transient) {
            localStorage.setItem(LS_SIDEBAR_STATE, false);
        }
        this._focusOnChat = false;
        this._lastLogEntry = null;
    }
    
    PartyHeader.prototype.addAttendeeToList = function(attendee) {
        let container = document.querySelector('.party-sidebar .party-logcontainer');
        let attendeeList = document.querySelector(".party-attendees");
        
        let atBottom = container.scrollTop > (container.scrollHeight - container.clientHeight) - 30;
        
        let newAttendee = document.createElement('div');
        newAttendee.innerHTML = `
            ${this.getProfilePictureHTML(attendee.UserId)}
            <div class="name"><div>
                <span class="md-icon isHost ${attendee.IsHosting ? '' : 'hide'}" title="Current host">&#xe9f4;</span>
                <span class="actualname">${attendee.Name}</span>
                <span class="md-icon isRemoteControlled ${attendee.IsRemoteControlled ? '' : 'hide'}" title="Remote controlled by another member">&#xe308;</span>
                <span class="md-icon isWaiting hide" title="Waiting...">&#xe627;</span>
                <span class="md-icon isSyncing hide" title="Ready to play">&#xe5ca;</span>
                <span class="md-icon isNotResponding hide" title="This attendee isn't responding to sync!">&#xe629;</span>
                <button class="md-icon btnSendAttendeePlay paper-icon-button-light hide" type="button" title="Send new playback command">&#xf06a;</button>
                <button class="md-icon btnSendAttendeeKick paper-icon-button-light hide" type="button" title="Remove from party">&#xe5c9;</button>
            </div></div>
        `;
        if (attendee.IsMe) {
            newAttendee.title = "This is me!";
            newAttendee.classList.add("party-isme");
        }
        if (attendee.IsHosting) newAttendee.classList.add("party-ishost");
        if (attendee.IsRemoteControlled) newAttendee.classList.add("party-isremotecontrolled");
        attendeeList.append(newAttendee);
        
        newAttendee.querySelector(".btnSendAttendeePlay").addEventListener("click", () => {
            this.sendAttendeePlay(attendee.Name);
        });
        
        newAttendee.querySelector(".btnSendAttendeeKick").addEventListener("click", () => {
            this.sendAttendeeKick(attendee.Name);
        });
        
        if (atBottom) {
            container.scrollTop = container.scrollHeight;
        }
    }
    
    PartyHeader.prototype.sendAttendeePlay = function(attendeename) {
        let apiClient = _connectionmanager.default.currentApiClient();
        apiClient.sendWebSocketMessage("PartyAttendeePlay", JSON.stringify({Name: attendeename}));
    }
    
    PartyHeader.prototype.sendAttendeeKick = function(attendeename) {
        let apiClient = _connectionmanager.default.currentApiClient();
        apiClient.sendWebSocketMessage("PartyAttendeeKick", JSON.stringify({Name: attendeename}));
    }
    
    PartyHeader.prototype.findAttendeeListItem = function(name) {
        return [...document.querySelector(".party-attendees").children].find(child => child.querySelector('.actualname').innerText == name);
    }
    
    PartyHeader.prototype.clearAllAttendeeIndicators = function() {
        for (let indicator of document.querySelectorAll('.party-attendees .isWaiting, .party-attendees .isSyncing, .party-attendees .isNotResponding, .party-attendees .btnSendAttendeePlay, .party-attendees .btnSendAttendeeKick')) {
            indicator.classList.add("hide");
        }
    }
    
    PartyHeader.prototype.removeAttendeeFromList = function(name) {
        let item = this.findAttendeeListItem(name);
        if (item) {
            item.remove();
        }
    }
    
    PartyHeader.prototype.renameAttendeeItem = function(name, newName) {
        let item = this.findAttendeeListItem(name);
        if (item) {
            item.querySelector('.actualname').innerText = newName;
        }
    }
    
    PartyHeader.prototype.setAttendeeItemHost = function(name, state) {
        let item = this.findAttendeeListItem(name);
        if (item) {
            if (state) {
                item.classList.add("party-ishost");
                item.querySelector(".isHost").classList.remove("hide");
                for (let indicator of item.querySelectorAll('.isWaiting, .isSyncing, .isNotResponding, .btnSendAttendeePlay, .btnSendAttendeeKick')) {
                    indicator.classList.add("hide");
                }
            } else {
                item.classList.remove("party-ishost");
                item.querySelector(".isHost").classList.add("hide");
            }
        }
    }
    
    PartyHeader.prototype.clearAttendeeList = function () {
        let attendeeList = document.querySelector(".party-attendees");
        attendeeList.innerHTML = "";
    }
    
    PartyHeader.prototype.setUndocked = function() {
        this._undocked = true;
        localStorage.setItem(LS_DOCK_MODE, true);
        let sidebar = document.querySelector('.party-sidebar');
        sidebar.querySelector(".btnPartyDock").classList.remove('hide');
        sidebar.querySelector(".btnPartyUndock").classList.add('hide');
        sidebar.classList.add("undocked");
        document.body.style.setProperty('--party-sidebar-width', 0);
    }
    
    PartyHeader.prototype.setDocked = function() {
        this._undocked = false;
        localStorage.setItem(LS_DOCK_MODE, false);
        let sidebar = document.querySelector('.party-sidebar');
        sidebar.querySelector(".btnPartyDock").classList.add('hide');
        sidebar.querySelector(".btnPartyUndock").classList.remove('hide');
        sidebar.classList.remove("undocked");
        document.body.style.setProperty('--party-sidebar-width', sidebar.style.width);
    }
    
    
    _exports.default = PartyHeader;
});