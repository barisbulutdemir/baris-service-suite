// ==========================================================================
// BARIŞ WEB GÖRÜŞME SİSTEMİ - WEBRTC ENGINE (CLIENT-SIDE JS)
// ==========================================================================

let socket;
let localStream;
let remoteStream;
let peerConnection;
let callingSocketId = null;
let currentCallPartnerName = "";
let myName = "";
let myPin = "";
let mySessionPassword = "";
let remoteIceCandidatesQueue = [];
let rtcConfiguration = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' },
        { urls: 'stun:stun2.l.google.com:19302' }
    ]
};

// HTML Elements
const loginPanel = document.getElementById('loginPanel');
const lobbyPanel = document.getElementById('lobbyPanel');
const callPanel = document.getElementById('callPanel');
const usersList = document.getElementById('usersList');
const myDisplayName = document.getElementById('myDisplayName');
const myAvatar = document.getElementById('myAvatar');
const systemDisabledScreen = document.getElementById('systemDisabledScreen');

const localVideo = document.getElementById('localVideo');
const remoteVideo = document.getElementById('remoteVideo');
const remotePlaceholder = document.getElementById('remotePlaceholder');
const callStatusText = document.getElementById('callStatusText');
const callPartnerName = document.getElementById('callPartnerName');
const callerLabel = document.getElementById('callerLabel');

const incomingCallModal = document.getElementById('incomingCallModal');
const incomingCallText = document.getElementById('incomingCallText');
const pinInputModal = document.getElementById('pinInputModal');
const targetUserPinInput = document.getElementById('targetUserPinInput');

const loginError = document.getElementById('loginError');
const pinModalError = document.getElementById('pinModalError');

let incomingCallerId = null;
let targetCallUser = null;

// Audio & Video mute toggles
let isMicMuted = false;
let isCamMuted = false;

// 1. Initial Connection Setup
window.addEventListener('DOMContentLoaded', () => {
    // Connect to Orchestrator socket.io backend
    socket = io(window.location.origin, {
        query: { role: 'chat-user' },
        reconnectionDelay: 1000,
        reconnectionDelayMax: 5000,
        reconnection: true
    });

    // Global System State checks
    socket.on('chat-system-status', (data) => {
        if (data.enabled) {
            systemDisabledScreen.style.display = 'none';
        } else {
            systemDisabledScreen.style.display = 'flex';
            handleLogout(); // Kick user to login if they are currently inside lobby
        }
    });

    socket.on('chat-system-disabled', () => {
        systemDisabledScreen.style.display = 'flex';
        handleLogout();
    });

    // Handle Active Users Lobby directory updates
    socket.on('chat-users-list', (users) => {
        populateLobbyList(users);
    });

    // WebRTC Signaling Receivers
    socket.on('rtc-call-incoming', (data) => {
        handleIncomingCallRequest(data.from, data.fromName);
    });

    socket.on('rtc-call-accepted', async (data) => {
        console.log("RTC: Call accepted by peer. Establishing PeerConnection...");
        callStatusText.textContent = "Bağlantı kuruluyor...";
        await setupRtcConnection(callingSocketId, true);
    });

    socket.on('rtc-offer', async (data) => {
        console.log("RTC: Offer received from " + data.from);
        await handleRtcOffer(data.from, data.offer);
    });

    socket.on('rtc-answer', async (data) => {
        console.log("RTC: Answer received from " + data.from);
        await handleRtcAnswer(data.answer);
    });

    socket.on('rtc-ice-candidate', async (data) => {
        if (peerConnection && peerConnection.remoteDescription) {
            try {
                await peerConnection.addIceCandidate(new RTCIceCandidate(data.candidate));
            } catch (err) {
                console.error("Error adding ICE candidate", err);
            }
        } else {
            remoteIceCandidatesQueue.push(data.candidate);
        }
    });

    socket.on('rtc-hangup', () => {
        console.log("RTC: Hangup event received from peer");
        closeActiveCall();
    });
});

// ==========================================================================
// 2. LOBBY & ACCOUNT MANAGEMENT
// ==========================================================================

function handleLogin() {
    const passwordInput = document.getElementById('mainPassword');
    const nameInput = document.getElementById('userName');
    const pinInput = document.getElementById('personalPin');

    const mainPassword = passwordInput.value.trim();
    const name = nameInput.value.trim();
    const pin = pinInput.value.trim();

    loginError.textContent = "";

    // Emit socket join lobby event
    socket.emit('join-chat-lobby', { name, pin, mainPassword }, (response) => {
        if (response.success) {
            myName = name;
            myPin = pin;
            mySessionPassword = mainPassword;

            // Update UI with user details
            myDisplayName.textContent = name;
            myAvatar.textContent = name.charAt(0).toUpperCase();

            // Switch to Lobby Panel
            loginPanel.classList.remove('active');
            lobbyPanel.classList.add('active');
        } else {
            loginError.textContent = response.error || "Giriş başarısız.";
        }
    });
}

function handleLogout() {
    myName = "";
    myPin = "";
    mySessionPassword = "";
    closeActiveCall();

    // Reset Forms
    document.getElementById('loginForm').reset();
    
    // Switch Panels
    lobbyPanel.classList.remove('active');
    callPanel.classList.remove('active');
    loginPanel.classList.add('active');

    // Notify server by disconnecting and reconnecting
    socket.disconnect();
    socket.connect();
}

function populateLobbyList(users) {
    usersList.innerHTML = "";
    
    // Filter out our own user socket id
    const otherUsers = users.filter(u => u.id !== socket.id);

    if (otherUsers.length === 0) {
        usersList.innerHTML = '<li class="empty-list-msg">Lobide başka çevrimiçi kullanıcı bulunmuyor...</li>';
        return;
    }

    otherUsers.forEach(user => {
        const li = document.createElement('li');
        
        const nameSpan = document.createElement('span');
        nameSpan.className = 'user-item-name';
        nameSpan.textContent = user.name;

        const metaDiv = document.createElement('div');
        metaDiv.className = 'user-item-meta';

        if (user.hasPin) {
            const pinBadge = document.createElement('span');
            pinBadge.className = 'pin-lock-badge';
            pinBadge.textContent = '🔒 Şifreli';
            metaDiv.appendChild(pinBadge);
        }

        const callButton = document.createElement('button');
        callButton.className = 'call-btn';
        callButton.textContent = 'Ara';
        callButton.onclick = () => initiateCallSequence(user);

        metaDiv.appendChild(callButton);
        li.appendChild(nameSpan);
        li.appendChild(metaDiv);
        
        usersList.appendChild(li);
    });
}

// ==========================================================================
// 3. CALL INITIATION (CALLER SIDE)
// ==========================================================================

function initiateCallSequence(user) {
    targetCallUser = user;
    pinModalError.textContent = "";
    targetUserPinInput.value = "";

    if (user.hasPin) {
        // Target requires custom PIN password to call
        pinInputModal.style.display = 'flex';
    } else {
        // No PIN required, dial immediately
        dialTargetUser(user.id, null);
    }
}

function closePinModal() {
    pinInputModal.style.display = 'none';
    targetCallUser = null;
}

function submitCallPin() {
    const pinValue = targetUserPinInput.value.trim();
    if (targetCallUser) {
        dialTargetUser(targetCallUser.id, pinValue);
    }
}

function dialTargetUser(targetSocketId, pin) {
    pinInputModal.style.display = 'none';
    pinModalError.textContent = "";

    console.log(`RTC: Dialing target user: ${targetSocketId} with PIN: ${pin}`);

    // Request signaling permission from target callee
    socket.emit('rtc-call-request', { to: targetSocketId, pin }, async (response) => {
        if (response.success) {
            callingSocketId = targetSocketId;
            currentCallPartnerName = targetCallUser ? targetCallUser.name : "Görüşülen Kişi";
            
            // Switch to Call UI Panel
            lobbyPanel.classList.remove('active');
            callPanel.classList.add('active');
            callPartnerName.textContent = currentCallPartnerName;
            callStatusText.textContent = "Karşı tarafın kabul etmesi bekleniyor...";

            // Start capturing local media tracks
            await initializeLocalMedia();

            // Wait for callee to click "Accept" (which will trigger rtc-call-accepted socket event)
        } else {
            if (targetCallUser && targetCallUser.hasPin) {
                // Keep modal open and show error if PIN was wrong
                pinInputModal.style.display = 'flex';
                pinModalError.textContent = response.error || "Hatalı PIN.";
            } else {
                alert("Arama hatası: " + response.error);
            }
        }
    });
}

// ==========================================================================
// 4. CALL INCOMING & ACCEPTANCE (CALLEE SIDE)
// ==========================================================================

function handleIncomingCallRequest(fromSocketId, fromName) {
    incomingCallerId = fromSocketId;
    incomingCallText.textContent = `${fromName} Arıyor`;
    incomingCallModal.style.display = 'flex';
    currentCallPartnerName = fromName;
}

function declineCall() {
    incomingCallModal.style.display = 'none';
    if (incomingCallerId) {
        socket.emit('rtc-hangup', { to: incomingCallerId });
    }
    incomingCallerId = null;
}

async function acceptCall() {
    incomingCallModal.style.display = 'none';
    if (!incomingCallerId) return;

    callingSocketId = incomingCallerId;
    incomingCallerId = null;

    // Switch to Call UI Panel
    lobbyPanel.classList.remove('active');
    callPanel.classList.add('active');
    callPartnerName.textContent = currentCallPartnerName;
    callStatusText.textContent = "Bağlantı kuruluyor...";

    // Start capturing local media
    await initializeLocalMedia();

    // Notify caller that we accepted the call
    socket.emit('rtc-call-accepted', { to: callingSocketId });
}

// ==========================================================================
// 5. WEBRTC MEDIA & PEER CONNECTION ENGINE
// ==========================================================================

async function initializeLocalMedia() {
    try {
        isMicMuted = false;
        isCamMuted = false;
        document.getElementById('toggleMicBtn').classList.remove('active');
        document.getElementById('toggleCamBtn').classList.remove('active');

        // Capture microphone and video streams with premium and compatible HD 720p constraints
        localStream = await navigator.mediaDevices.getUserMedia({
            video: {
                width: { ideal: 1280 },
                height: { ideal: 720 }
            },
            audio: true
        });
        localVideo.srcObject = localStream;
        console.log("Local camera and mic stream captured successfully.");
    } catch (err) {
        console.error("Fatal error accessing user media tools:", err);
        alert("Görüntülü konuşma için kamera ve mikrofon izinlerinin verilmesi zorunludur!");
        closeActiveCall();
    }
}

async function setupRtcConnection(targetSocketId, isCaller) {
    try {
        peerConnection = new RTCPeerConnection(rtcConfiguration);

        // Add local media tracks to RTC tunnel
        if (localStream) {
            localStream.getTracks().forEach(track => {
                peerConnection.addTrack(track, localStream);
            });
        }

        // When remote stream tracks arrive, bind them to HTML remote video window
        peerConnection.ontrack = (event) => {
            console.log("RTC: Remote stream track received!");
            if (!remoteStream) {
                remoteStream = new MediaStream();
                remoteVideo.srcObject = remoteStream;
            }
            event.streams[0].getTracks().forEach(track => {
                remoteStream.addTrack(track);
            });

            // Hide the connecting spinner, show active video
            remotePlaceholder.style.display = 'none';
        };

        // Forward candidate to peer when local ICE candidate generated
        peerConnection.onicecandidate = (event) => {
            if (event.candidate) {
                socket.emit('rtc-ice-candidate', {
                    to: targetSocketId,
                    candidate: event.candidate
                });
            }
        };

        peerConnection.onconnectionstatechange = () => {
            console.log("RTC Connection state changed: " + peerConnection.connectionState);
            if (peerConnection.connectionState === 'disconnected' || peerConnection.connectionState === 'failed') {
                closeActiveCall();
            }
        };

        // If caller, immediately generate the SDP offer handshake
        if (isCaller) {
            const offer = await peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);
            
            socket.emit('rtc-offer', {
                to: targetSocketId,
                offer: offer
            });
            console.log("RTC: Caller SDP Offer generated and transmitted.");
        }

    } catch (err) {
        console.error("Error setting up RTCPeerConnection:", err);
        closeActiveCall();
    }
}

async function processQueuedIceCandidates() {
    if (!peerConnection || !peerConnection.remoteDescription) return;
    console.log(`WebRTC: Processing ${remoteIceCandidatesQueue.length} queued ICE candidates.`);
    for (const candidate of remoteIceCandidatesQueue) {
        try {
            await peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
        } catch (err) {
            console.error("Error adding queued ICE candidate", err);
        }
    }
    remoteIceCandidatesQueue = [];
}

async function handleRtcOffer(fromSocketId, offer) {
    if (!peerConnection) {
        // Callee accepted call but RTC is not fully initiated yet
        await setupRtcConnection(fromSocketId, false);
    }

    try {
        await peerConnection.setRemoteDescription(new RTCSessionDescription(offer));
        await processQueuedIceCandidates();
        
        // Generate SDP Answer handshake
        const answer = await peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answer);

        socket.emit('rtc-answer', {
            to: fromSocketId,
            answer: answer
        });
        console.log("RTC: Callee SDP Answer generated and transmitted.");
    } catch (err) {
        console.error("Error processing RTC SDP Offer:", err);
        closeActiveCall();
    }
}

async function handleRtcAnswer(answer) {
    if (peerConnection) {
        try {
            await peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
            console.log("RTC: Handshake successfully completed.");
            await processQueuedIceCandidates();
        } catch (err) {
            console.error("Error setting remote SDP Answer:", err);
            closeActiveCall();
        }
    }
}

// ==========================================================================
// 6. CALL CONTROLS (MIC, CAM, HANGUP)
// ==========================================================================

function toggleMuteMic() {
    if (localStream) {
        const audioTrack = localStream.getAudioTracks()[0];
        if (audioTrack) {
            isMicMuted = !isMicMuted;
            audioTrack.enabled = !isMicMuted;
            
            const btn = document.getElementById('toggleMicBtn');
            if (isMicMuted) {
                btn.classList.add('active');
                btn.title = "Mikrofonu Aç";
            } else {
                btn.classList.remove('active');
                btn.title = "Mikrofonu Kapat";
            }
            console.log(`Microphone ${isMicMuted ? 'MUTED' : 'UNMUTED'}`);
        }
    }
}

function toggleMuteCam() {
    if (localStream) {
        const videoTrack = localStream.getVideoTracks()[0];
        if (videoTrack) {
            isCamMuted = !isCamMuted;
            videoTrack.enabled = !isCamMuted;

            const btn = document.getElementById('toggleCamBtn');
            if (isCamMuted) {
                btn.classList.add('active');
                btn.title = "Kamerayı Aç";
            } else {
                btn.classList.remove('active');
                btn.title = "Kamerayı Kapat";
            }
            console.log(`Camera ${isCamMuted ? 'MUTED' : 'UNMUTED'}`);
        }
    }
}

function hangUpCall() {
    if (callingSocketId) {
        socket.emit('rtc-hangup', { to: callingSocketId });
    }
    closeActiveCall();
}

function closeActiveCall() {
    console.log("Cleaning up and closing active call streams...");
    
    // Stop local media tracks
    if (localStream) {
        localStream.getTracks().forEach(track => track.stop());
        localStream = null;
    }

    // Terminate Peer Connection
    if (peerConnection) {
        peerConnection.close();
        peerConnection = null;
    }

    remoteIceCandidatesQueue = [];

    // Reset video objects
    localVideo.srcObject = null;
    remoteVideo.srcObject = null;
    remoteStream = null;

    // Reset Modals & Overlays
    incomingCallModal.style.display = 'none';
    pinInputModal.style.display = 'none';
    remotePlaceholder.style.display = 'flex';
    callStatusText.textContent = "Bağlantı kuruluyor...";

    callingSocketId = null;
    incomingCallerId = null;
    targetCallUser = null;

    // Switch Panel back to Lobby
    if (myName !== "") {
        callPanel.classList.remove('active');
        lobbyPanel.classList.add('active');
    }
}
