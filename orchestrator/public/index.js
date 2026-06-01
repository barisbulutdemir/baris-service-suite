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
let localIceCandidatesCount = 0;
let remoteIceCandidatesCount = 0;

let rtcConfiguration = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' }
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

// ==========================================================================
// SYSTEM DIAGNOSTICS & LOGGING ENGINE
// ==========================================================================
function toggleDiagnosticPanel() {
    const panel = document.getElementById('diagnosticPanel');
    if (panel) {
        panel.classList.toggle('active');
    }
}

function addDiagLog(message, type = 'info') {
    const output = document.getElementById('diagLogsOutput');
    if (output) {
        const time = new Date().toTimeString().split(' ')[0];
        const span = document.createElement('div');
        span.style.marginBottom = '6px';
        span.style.lineHeight = '14px';
        
        let color = '#2ecc71'; // default green (success)
        if (type === 'error') color = '#e74c3c'; // red
        if (type === 'warn') color = '#f1c40f'; // yellow
        if (type === 'info') color = '#3498db'; // blue
        
        span.innerHTML = `<span style="color: #8A8A8A;">[${time}]</span> <span style="color: ${color};">${message}</span>`;
        output.appendChild(span);
        output.scrollTop = output.scrollHeight;
    }
    console.log(`[Diagnostic] [${type.toUpperCase()}] ${message}`);
}

function updateDiagStatus(elementId, status, text) {
    const el = document.getElementById(elementId);
    if (el) {
        el.textContent = text;
        el.className = 'card-value';
        if (status === 'success') el.classList.add('badge-success');
        else if (status === 'error') el.classList.add('badge-error');
        else if (status === 'warn') el.classList.add('badge-warn');
        else if (status === 'info') el.classList.add('badge-info');
        else el.classList.add('badge-loading');
    }
}

function updateDiagIceExchange() {
    const el = document.getElementById('diagIceExchange');
    if (el) {
        el.textContent = `L: ${localIceCandidatesCount} | R: ${remoteIceCandidatesCount}`;
    }
}

// ==========================================================================
// 1. Initial Connection Setup
// ==========================================================================
window.addEventListener('DOMContentLoaded', () => {
    addDiagLog("Sistem başlatılıyor...");
    updateDiagStatus('diagMediaAccess', 'info', 'Bekleniyor');
    updateDiagStatus('diagSocketConn', 'info', 'Bağlanılıyor...');
    updateDiagStatus('diagIceServers', 'info', 'Bekleniyor');

    // Fetch ICE/TURN Server Configuration from Orchestrator dynamically
    addDiagLog("Orkestratörden dinamik STUN/TURN ağ ayarları talep ediliyor...");
    fetch('/api/chat/status')
        .then(res => res.json())
        .then(data => {
            if (data.iceServers) {
                rtcConfiguration.iceServers = data.iceServers;
                addDiagLog(`STUN/TURN ağ ayarları başarıyla yüklendi. Yüklenen sunucu sayısı: ${data.iceServers.length}`);
                updateDiagStatus('diagIceServers', 'success', `${data.iceServers.length} Sunucu Aktif`);
                
                data.iceServers.forEach((srv, idx) => {
                    addDiagLog(`-> ICE Sunucu #${idx + 1}: ${srv.urls} (Kullanıcı: ${srv.username || 'Yok'})`);
                });
            } else {
                addDiagLog("Sunucudan ICE ayarı dönmedi, yerel STUN kullanılacak.", "warn");
                updateDiagStatus('diagIceServers', 'warn', 'Yerel STUN Aktif');
            }
        })
        .catch(err => {
            addDiagLog(`Ağ ayarları sunucudan çekilemedi (Cloudflare engeli olabilir): ${err.message}`, 'error');
            updateDiagStatus('diagIceServers', 'error', 'Hata Oluştu');
        });

    // Connect to Orchestrator socket.io backend
    addDiagLog("Soket sinyalleşme bağlantısı kuruluyor...");
    socket = io(window.location.origin, {
        query: { role: 'chat-user' },
        reconnectionDelay: 1000,
        reconnectionDelayMax: 5000,
        reconnection: true
    });

    socket.on('connect', () => {
        addDiagLog(`Sinyalleşme sunucusuna bağlandı. Socket ID: ${socket.id}`, 'success');
        updateDiagStatus('diagSocketConn', 'success', 'Soket Bağlı');
    });

    socket.on('disconnect', (reason) => {
        addDiagLog(`Sinyalleşme sunucu bağlantısı koptu: ${reason}`, 'warn');
        updateDiagStatus('diagSocketConn', 'error', 'Bağlantı Koptu');
    });

    socket.on('connect_error', (err) => {
        addDiagLog(`Soket bağlantı hatası: ${err.message}`, 'error');
        updateDiagStatus('diagSocketConn', 'error', 'Hata Oluştu');
    });

    // Global System State checks
    socket.on('chat-system-status', (data) => {
        if (data.enabled) {
            systemDisabledScreen.style.display = 'none';
        } else {
            systemDisabledScreen.style.display = 'flex';
            addDiagLog("Görüşme sistemi yönetici tarafından pasif hale getirildi!", "warn");
            handleLogout(); // Kick user to login if they are currently inside lobby
        }
    });

    socket.on('chat-system-disabled', () => {
        systemDisabledScreen.style.display = 'flex';
        addDiagLog("Görüşme sistemi pasif moda geçti. Lobi kapatıldı.", "warn");
        handleLogout();
    });

    // Handle Active Users Lobby directory updates
    socket.on('chat-users-list', (users) => {
        addDiagLog(`Lobi kullanıcı listesi güncellendi. Çevrimiçi aktif kişi sayısı: ${users.length - 1}`);
        populateLobbyList(users);
    });

    // WebRTC Signaling Receivers
    socket.on('rtc-call-incoming', (data) => {
        addDiagLog(`Gelen arama sinyali! Arayan kişi: ${data.fromName} (${data.from})`, 'info');
        handleIncomingCallRequest(data.from, data.fromName);
    });

    socket.on('rtc-call-accepted', async (data) => {
        addDiagLog("Karşı taraf aramayı kabul etti! WebRTC tünel el sıkışması başlatılıyor...", "success");
        callStatusText.textContent = "Bağlantı kuruluyor...";
        await setupRtcConnection(callingSocketId, true);
    });

    socket.on('rtc-offer', async (data) => {
        addDiagLog(`Uzak tünelden teklif (SDP Offer) alındı. Gönderen: ${data.from}`);
        await handleRtcOffer(data.from, data.offer);
    });

    socket.on('rtc-answer', async (data) => {
        addDiagLog("Uzak tünelden onay cevabı (SDP Answer) alındı.");
        await handleRtcAnswer(data.answer);
    });

    socket.on('rtc-ice-candidate', async (data) => {
        if (peerConnection && peerConnection.remoteDescription) {
            try {
                remoteIceCandidatesCount++;
                updateDiagIceExchange();
                addDiagLog(`ICE Adayı anında tünele eklendi: ${data.candidate.candidate.substring(0, 40)}...`);
                await peerConnection.addIceCandidate(new RTCIceCandidate(data.candidate));
            } catch (err) {
                addDiagLog(`ICE Adayı eklenirken hata: ${err.message}`, 'error');
            }
        } else {
            addDiagLog(`Tünel henüz hazır olmadığı için uzak ICE adayı geçici sıraya alındı.`);
            remoteIceCandidatesQueue.push(data.candidate);
        }
    });

    socket.on('rtc-hangup', () => {
        addDiagLog("Uzak taraftan kapatma sinyali alındı. Tünel sonlandırılıyor...", "warn");
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

    addDiagLog(`Lobiye giriş isteği gönderiliyor. İsim: ${name}`);

    // Emit socket join lobby event
    socket.emit('join-chat-lobby', { name, pin, mainPassword }, (response) => {
        if (response.success) {
            myName = name;
            myPin = pin;
            mySessionPassword = mainPassword;

            addDiagLog("Lobiye başarıyla giriş yapıldı.", "success");

            // Update UI with user details
            myDisplayName.textContent = name;
            myAvatar.textContent = name.charAt(0).toUpperCase();

            // Switch to Lobby Panel
            loginPanel.classList.remove('active');
            lobbyPanel.classList.add('active');
        } else {
            addDiagLog(`Lobiye giriş reddedildi: ${response.error}`, "error");
            loginError.textContent = response.error || "Giriş başarısız.";
        }
    });
}

function handleLogout() {
    addDiagLog("Kullanıcı lobiden çıkış yaptı.");
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

    addDiagLog(`Arama süreci tetiklendi. Hedef kullanıcı: ${user.name}`);

    if (user.hasPin) {
        addDiagLog("Hedef kullanıcı şifre korumalı. PIN penceresi gösteriliyor.");
        pinInputModal.style.display = 'flex';
    } else {
        dialTargetUser(user.id, null);
    }
}

function closePinModal() {
    addDiagLog("PIN girişi iptal edildi.");
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

    addDiagLog(`Karşı tarafa arama izni sorgusu atılıyor. Hedef ID: ${targetSocketId}`);

    // Request signaling permission from target callee
    socket.emit('rtc-call-request', { to: targetSocketId, pin }, async (response) => {
        if (response.success) {
            addDiagLog("Arama izni onaylandı! Kameralar hazırlanıyor...");
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
            addDiagLog(`Arama isteği reddedildi veya hata oluştu: ${response.error}`, 'error');
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
    addDiagLog("Gelen çağrı reddedildi.");
    incomingCallModal.style.display = 'none';
    if (incomingCallerId) {
        socket.emit('rtc-hangup', { to: incomingCallerId });
    }
    incomingCallerId = null;
}

async function acceptCall() {
    addDiagLog("Gelen arama kabul edildi! Kameralar açılıyor...");
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
    addDiagLog("Arayan tarafa kabul sinyali gönderiliyor...");
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

        addDiagLog("Kamera ve mikrofon donanımı talep ediliyor...");
        updateDiagStatus('diagMediaAccess', 'info', 'Donanım İsteniyor');

        // Capture microphone and video streams with premium and compatible HD 720p constraints
        localStream = await navigator.mediaDevices.getUserMedia({
            video: {
                width: { ideal: 1280 },
                height: { ideal: 720 }
            },
            audio: true
        });
        localVideo.srcObject = localStream;
        
        const videoTrack = localStream.getVideoTracks()[0];
        const settings = videoTrack ? videoTrack.getSettings() : {};
        addDiagLog(`Kamera ve mikrofon başarıyla yakalandı. Kalite: ${settings.width || '?' }x${settings.height || '?' } @ ${settings.frameRate || '?' }fps`, 'success');
        updateDiagStatus('diagMediaAccess', 'success', 'Erişim İzin Verildi');
    } catch (err) {
        addDiagLog(`Kamera veya mikrofon açılamadı! Hata: ${err.name} - ${err.message}`, 'error');
        updateDiagStatus('diagMediaAccess', 'error', 'İzin Alınamadı / Kilitli');
        alert("Görüntülü konuşma için kamera ve mikrofon izinlerinin verilmesi zorunludur!");
        closeActiveCall();
    }
}

async function setupRtcConnection(targetSocketId, isCaller) {
    try {
        addDiagLog(`WebRTC RTCPeerConnection oluşturuluyor... Rol: ${isCaller ? 'Caller (Arayan)' : 'Callee (Aranan)'}`);
        updateDiagStatus('diagRtcState', 'info', 'Tünel Kuruluyor...');
        
        localIceCandidatesCount = 0;
        remoteIceCandidatesCount = 0;
        updateDiagIceExchange();

        peerConnection = new RTCPeerConnection(rtcConfiguration);

        // Add local media tracks to RTC tunnel
        if (localStream) {
            addDiagLog("Kamera/Mikrofon verisi WebRTC tünel kanallarına ekleniyor...");
            localStream.getTracks().forEach(track => {
                peerConnection.addTrack(track, localStream);
            });
        }

        // When remote stream tracks arrive, bind them to HTML remote video window
        peerConnection.ontrack = (event) => {
            addDiagLog("Uzak cihazdan gelen ilk ses/görüntü paketi başarıyla ulaştı!", "success");
            if (!remoteStream) {
                remoteStream = new MediaStream();
                remoteVideo.srcObject = remoteStream;
            }
            event.streams[0].getTracks().forEach(track => {
                remoteStream.addTrack(track);
            });

            // Hide the connecting spinner, show active video
            remotePlaceholder.style.display = 'none';
            updateDiagStatus('diagRtcState', 'success', 'Tünel Aktif (Görüşülüyor)');
        };

        // Forward candidate to peer when local ICE candidate generated
        peerConnection.onicecandidate = (event) => {
            if (event.candidate) {
                localIceCandidatesCount++;
                updateDiagIceExchange();
                addDiagLog(`Yerel ağ adayı (ICE Candidate) üretildi: ${event.candidate.candidate.substring(0, 45)}...`);
                socket.emit('rtc-ice-candidate', {
                    to: targetSocketId,
                    candidate: event.candidate
                });
            }
        };

        peerConnection.onconnectionstatechange = () => {
            addDiagLog(`Tünel Bağlantı Durumu Değişti: ${peerConnection.connectionState.toUpperCase()}`, peerConnection.connectionState === 'connected' ? 'success' : (peerConnection.connectionState === 'failed' ? 'error' : 'info'));
            updateDiagStatus('diagRtcState', peerConnection.connectionState === 'connected' ? 'success' : (peerConnection.connectionState === 'failed' ? 'error' : 'info'), peerConnection.connectionState.toUpperCase());
            
            if (peerConnection.connectionState === 'disconnected' || peerConnection.connectionState === 'failed') {
                addDiagLog("Bağlantı kesildi veya tünel ağ engeline takıldı.", "error");
                closeActiveCall();
            }
        };

        peerConnection.oniceconnectionstatechange = () => {
            addDiagLog(`Ağ Geçiş (ICE) Durumu Değişti: ${peerConnection.iceConnectionState.toUpperCase()}`, peerConnection.iceConnectionState === 'connected' ? 'success' : 'info');
            if (peerConnection.iceConnectionState === 'failed') {
                addDiagLog("Ağ Adayları birbiriyle eşleşemedi. Güvenlik duvarı P2P UDP ve TCP portlarını tamamen bloklamaktadır!", 'error');
            }
        };

        peerConnection.onsignalingstatechange = () => {
            addDiagLog(`Sinyal Durumu Değişti: ${peerConnection.signalingState}`);
        };

        // If caller, immediately generate the SDP offer handshake
        if (isCaller) {
            addDiagLog("Yerel tünel el sıkışma teklifi (SDP Offer) oluşturuluyor...");
            const offer = await peerConnection.createOffer();
            addDiagLog("Yerel teklif kaydediliyor (setLocalDescription)...");
            await peerConnection.setLocalDescription(offer);
            
            addDiagLog("Teklif sinyalleşme sunucusuna aktarılıyor...");
            socket.emit('rtc-offer', {
                to: targetSocketId,
                offer: offer
            });
            console.log("RTC: Caller SDP Offer generated and transmitted.");
        }

    } catch (err) {
        addDiagLog(`RTCPeerConnection kurulum hatası: ${err.message}`, 'error');
        updateDiagStatus('diagRtcState', 'error', 'Tünel Hatası');
        closeActiveCall();
    }
}

async function processQueuedIceCandidates() {
    if (!peerConnection || !peerConnection.remoteDescription) return;
    addDiagLog(`Kuyrukta bekleyen ${remoteIceCandidatesQueue.length} adet uzak ICE adayı şimdi tünele aktarılıyor...`);
    for (const candidate of remoteIceCandidatesQueue) {
        try {
            remoteIceCandidatesCount++;
            updateDiagIceExchange();
            await peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
        } catch (err) {
            addDiagLog(`Uzak ICE adayı tünele yüklenirken hata: ${err.message}`, 'warn');
        }
    }
    remoteIceCandidatesQueue = [];
}

async function handleRtcOffer(fromSocketId, offer) {
    addDiagLog("Uzak tünel teklifi alındı. Cevaplama mekanizması tetiklendi...");
    if (!peerConnection) {
        // Callee accepted call but RTC is not fully initiated yet
        await setupRtcConnection(fromSocketId, false);
    }

    try {
        addDiagLog("Gelen tünel teklifi tünele kaydediliyor (setRemoteDescription)...");
        await peerConnection.setRemoteDescription(new RTCSessionDescription(offer));
        await processQueuedIceCandidates();
        
        addDiagLog("Yerel el sıkışma cevabı (SDP Answer) oluşturuluyor...");
        const answer = await peerConnection.createAnswer();
        addDiagLog("Yerel cevap tünele kaydediliyor (setLocalDescription)...");
        await peerConnection.setLocalDescription(answer);

        addDiagLog("Cevap arayan kişiye sinyalleşme üzerinden gönderiliyor...");
        socket.emit('rtc-answer', {
            to: fromSocketId,
            answer: answer
        });
        console.log("RTC: Callee SDP Answer generated and transmitted.");
    } catch (err) {
        addDiagLog(`SDP Offer işlenirken hata: ${err.message}`, 'error');
        closeActiveCall();
    }
}

async function handleRtcAnswer(answer) {
    addDiagLog("Karşı taraftan bağlantı cevabı (SDP Answer) alındı. Bağlantı doğrulanıyor...");
    if (peerConnection) {
        try {
            await peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
            addDiagLog("Tünel el sıkışması (Handshake) başarıyla doğrulandı ve kilitlendi!", "success");
            await processQueuedIceCandidates();
        } catch (err) {
            addDiagLog(`SDP Answer işlenirken hata: ${err.message}`, 'error');
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
                addDiagLog("Mikrofon sessize alındı.");
            } else {
                btn.classList.remove('active');
                btn.title = "Mikrofonu Kapat";
                addDiagLog("Mikrofon açıldı.");
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
                addDiagLog("Kamera görüntüsü duraklatıldı.");
            } else {
                btn.classList.remove('active');
                btn.title = "Kamerayı Kapat";
                addDiagLog("Kamera görüntüsü aktif edildi.");
            }
            console.log(`Camera ${isCamMuted ? 'MUTED' : 'UNMUTED'}`);
        }
    }
}

function hangUpCall() {
    addDiagLog("Görüşme kullanıcı tarafından kapatıldı.");
    if (callingSocketId) {
        socket.emit('rtc-hangup', { to: callingSocketId });
    }
    closeActiveCall();
}

function closeActiveCall() {
    addDiagLog("Tünel kapatılıyor, akış kanalları temizleniyor...");
    
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
    localIceCandidatesCount = 0;
    remoteIceCandidatesCount = 0;
    updateDiagIceExchange();
    updateDiagStatus('diagRtcState', 'info', 'Boşta');

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
