"use strict";

window.Softphone = {
    _ua: null,
    _registerer: null,
    _session: null,
    _dotNetRef: null,
    _audioElement: null,

    async register(wssUrl, extension, password, displayName, dotNetRef) {
        this._dotNetRef = dotNetRef;
        this._audioElement = document.getElementById("softphone-remote-audio");

        try {
            const uri = SIP.UserAgent.makeURI("sip:" + extension + "@" + new URL(wssUrl).hostname);
            if (!uri) {
                dotNetRef.invokeMethodAsync("OnRegistrationFailed", "Invalid SIP URI");
                return;
            }

            this._ua = new SIP.UserAgent({
                uri: uri,
                transportOptions: { server: wssUrl },
                authorizationUsername: extension,
                authorizationPassword: password,
                displayName: displayName || extension,
                delegate: {
                    onInvite: (invitation) => this._handleIncoming(invitation)
                }
            });

            this._registerer = new SIP.Registerer(this._ua);

            await this._ua.start();
            await this._registerer.register();
            dotNetRef.invokeMethodAsync("OnRegistered");
        } catch (err) {
            dotNetRef.invokeMethodAsync("OnRegistrationFailed", err.message || "Registration failed");
        }
    },

    async call(destination) {
        if (!this._ua || this._session) return;
        try {
            const target = SIP.UserAgent.makeURI("sip:" + destination + "@" + this._ua.configuration.uri.host);
            if (!target) return;

            this._session = new SIP.Inviter(this._ua, target);
            this._setupSessionListeners(this._session);

            await this._session.invite({
                sessionDescriptionHandlerOptions: {
                    constraints: { audio: true, video: false }
                }
            });
            this._dotNetRef.invokeMethodAsync("OnRingingOut");
        } catch (err) {
            this._dotNetRef.invokeMethodAsync("OnCallFailed", err.message || "Call failed");
            this._session = null;
        }
    },

    async answer() {
        if (!this._session) return;
        try {
            await this._session.accept({
                sessionDescriptionHandlerOptions: {
                    constraints: { audio: true, video: false }
                }
            });
        } catch (err) {
            this._dotNetRef.invokeMethodAsync("OnCallFailed", err.message || "Answer failed");
        }
    },

    async hangup() {
        if (!this._session) return;
        try {
            switch (this._session.state) {
                case SIP.SessionState.Established:
                    await this._session.bye();
                    break;
                case SIP.SessionState.Establishing:
                    await this._session.cancel();
                    break;
                default:
                    try { await this._session.reject(); } catch(e) { /* ignore */ }
                    break;
            }
        } catch (e) { /* best effort */ }
        this._session = null;
    },

    async hold() {
        if (!this._session) return;
        try {
            await this._session.hold();
            this._dotNetRef.invokeMethodAsync("OnHoldChanged", true);
        } catch (e) { /* ignore */ }
    },

    async unhold() {
        if (!this._session) return;
        try {
            await this._session.unhold();
            this._dotNetRef.invokeMethodAsync("OnHoldChanged", false);
        } catch (e) { /* ignore */ }
    },

    mute() {
        if (!this._session) return;
        const pc = this._session.sessionDescriptionHandler?.peerConnection;
        if (pc) {
            pc.getSenders().forEach(function(s) { if (s.track) s.track.enabled = false; });
            this._dotNetRef.invokeMethodAsync("OnMuteChanged", true);
        }
    },

    unmute() {
        if (!this._session) return;
        const pc = this._session.sessionDescriptionHandler?.peerConnection;
        if (pc) {
            pc.getSenders().forEach(function(s) { if (s.track) s.track.enabled = true; });
            this._dotNetRef.invokeMethodAsync("OnMuteChanged", false);
        }
    },

    sendDtmf(digit) {
        if (!this._session) return;
        try {
            this._session.sessionDescriptionHandler?.sendDtmf(digit);
        } catch (e) {
            try { this._session.info({ body: { contentDisposition: "render", contentType: "application/dtmf-relay", content: "Signal=" + digit + "\r\nDuration=100" } }); } catch(e2) { /* ignore */ }
        }
    },

    async unregister() {
        try {
            if (this._session) await this.hangup();
            if (this._registerer) await this._registerer.unregister();
            if (this._ua) await this._ua.stop();
        } catch (e) { /* cleanup best-effort */ }
        this._ua = null;
        this._registerer = null;
        this._session = null;
    },

    _handleIncoming(invitation) {
        this._session = invitation;
        this._setupSessionListeners(invitation);
        var from = invitation.remoteIdentity;
        var name = from?.displayName || "";
        var number = from?.uri?.user || "Unknown";
        this._dotNetRef.invokeMethodAsync("OnIncomingCall", name, number);
    },

    _setupSessionListeners(session) {
        var self = this;

        // Attach ontrack handler early — before session establishes
        // This ensures we capture remote audio tracks as they arrive via ICE
        session.sessionDescriptionHandlerOptionsReply = {};
        var trackHandler = function() {
            var sdh = session.sessionDescriptionHandler;
            if (!sdh) return;
            var pc = sdh.peerConnection;
            if (!pc) return;

            // Remove previous handler if re-attaching
            pc.ontrack = function(event) {
                if (!self._audioElement) return;
                var remoteStream = self._audioElement.srcObject;
                if (!remoteStream) {
                    remoteStream = new MediaStream();
                    self._audioElement.srcObject = remoteStream;
                }
                event.streams.forEach(function(s) {
                    s.getTracks().forEach(function(t) {
                        remoteStream.addTrack(t);
                    });
                });
                if (!event.streams.length && event.track) {
                    remoteStream.addTrack(event.track);
                }
                self._audioElement.play().catch(function() {});
            };

            // Also grab any tracks already present
            pc.getReceivers().forEach(function(r) {
                if (r.track && self._audioElement) {
                    var stream = self._audioElement.srcObject;
                    if (!stream) {
                        stream = new MediaStream();
                        self._audioElement.srcObject = stream;
                    }
                    stream.addTrack(r.track);
                }
            });
            if (self._audioElement && self._audioElement.srcObject) {
                self._audioElement.play().catch(function() {});
            }
        };

        session.stateChange.addListener(function(state) {
            switch (state) {
                case SIP.SessionState.Establishing:
                    // SDH is created — attach ontrack
                    trackHandler();
                    break;
                case SIP.SessionState.Established:
                    // Also try again in case Establishing was missed
                    trackHandler();
                    self._dotNetRef.invokeMethodAsync("OnCallAnswered");
                    break;
                case SIP.SessionState.Terminated:
                    self._cleanupMedia();
                    self._session = null;
                    self._dotNetRef.invokeMethodAsync("OnCallEnded");
                    break;
            }
        });

        // For incoming calls, SDH may already exist
        if (session.sessionDescriptionHandler) {
            trackHandler();
        }
    },

    _cleanupMedia() {
        if (this._audioElement) {
            this._audioElement.srcObject = null;
        }
    }
};
