import fs from 'fs';
import path from 'path';

const DB_PATH = path.join(process.cwd(), 'sites.json');

export interface SiteInfo {
  id: string; // Unique site identifier
  name: string; // Human readable name (e.g., "Italy", "Kuwait")
  status: 'online' | 'offline';
  rustDeskId?: string;
  rustDeskPassword?: string;
  socketId?: string;
  lastSeen?: number;
  location?: {
    country?: string;
    city?: string;
    lat?: number;
    lon?: number;
    isp?: string;
  };
}

export interface ChatUser {
  id: string; // Socket ID
  name: string;
  hasPin: boolean;
  pin?: string;
}

export class SiteManager {
  private sites: Map<string, SiteInfo> = new Map();
  private chatUsers: Map<string, ChatUser> = new Map();
  private chatSystemEnabled: boolean = true;
  private readonly mainPassword = '632536';

  constructor() {
    this.loadSites();
  }

  private loadSites() {
    try {
      if (fs.existsSync(DB_PATH)) {
        const data = fs.readFileSync(DB_PATH, 'utf8');
        const list: SiteInfo[] = JSON.parse(data);
        for (const site of list) {
          site.status = 'offline';
          site.socketId = undefined;
          this.sites.set(site.id, site);
        }
        console.log(`[SiteManager] Loaded ${this.sites.size} sites from database.`);
      }
    } catch (err) {
      console.error('[SiteManager] Error loading sites database:', err);
    }
  }

  private saveSites() {
    try {
      const list = Array.from(this.sites.values()).map(s => ({
        id: s.id,
        name: s.name,
        rustDeskId: s.rustDeskId,
        rustDeskPassword: s.rustDeskPassword,
        location: s.location
      }));
      fs.writeFileSync(DB_PATH, JSON.stringify(list, null, 2), 'utf8');
    } catch (err) {
      console.error('[SiteManager] Error saving sites database:', err);
    }
  }

  // Web Chat System Controls
  public isChatSystemEnabled(): boolean {
    return this.chatSystemEnabled;
  }

  public setChatSystemEnabled(enabled: boolean) {
    this.chatSystemEnabled = enabled;
    console.log(`[SiteManager] Web Görüntülü Görüşme Sistemi: ${enabled ? 'AKTİF' : 'PASİF'}`);
  }

  public verifyMainPassword(password: string): boolean {
    return password === this.mainPassword;
  }

  public getIceServersConfig() {
    const turnUrl = process.env.TURN_URL;
    const turnUsername = process.env.TURN_USERNAME;
    const turnCredential = process.env.TURN_PASSWORD;

    const servers: any[] = [
      { urls: 'stun:stun.l.google.com:19302' },
      { urls: 'stun:stun1.l.google.com:19302' },
      { urls: 'stun:stun2.l.google.com:19302' }
    ];

    if (turnUrl && turnUsername && turnCredential) {
      // If it is Metered.ca, populate the complete high-compatibility lists they provided!
      if (turnUrl.includes('metered.ca')) {
        const host = turnUrl.includes('global.relay.metered.ca') ? 'global.relay.metered.ca' : 'global.turn.metered.ca';
        const stunHost = turnUrl.includes('global.relay.metered.ca') ? 'stun.relay.metered.ca' : 'stun.turn.metered.ca';
        
        servers.push({ urls: `stun:${stunHost}:80` });
        servers.push({ urls: `turn:${host}:80`, username: turnUsername, credential: turnCredential });
        servers.push({ urls: `turn:${host}:80?transport=tcp`, username: turnUsername, credential: turnCredential });
        servers.push({ urls: `turn:${host}:443`, username: turnUsername, credential: turnCredential });
        servers.push({ urls: `turns:${host}:443?transport=tcp`, username: turnUsername, credential: turnCredential });
      } else {
        // Fallback for standard custom TURN
        servers.push({
          urls: turnUrl,
          username: turnUsername,
          credential: turnCredential
        });
      }
    }
    return servers;
  }

  // Web Chat User Directory Management
  public addChatUser(id: string, name: string, pin?: string): ChatUser {
    const user: ChatUser = {
      id,
      name,
      hasPin: !stringIsEmpty(pin),
      pin: pin || undefined
    };
    this.chatUsers.set(id, user);
    console.log(`[SiteManager] Lobi kullanıcısı eklendi: ${name} (${id}), PIN Koruması: ${user.hasPin ? 'EVET' : 'HAYIR'}`);
    return user;
  }

  public removeChatUser(id: string): ChatUser | null {
    const user = this.chatUsers.get(id);
    if (user) {
      this.chatUsers.delete(id);
      console.log(`[SiteManager] Lobi kullanıcısı ayrıldı: ${user.name} (${id})`);
      return user;
    }
    return null;
  }

  public getChatUsersList(): { id: string; name: string; hasPin: boolean }[] {
    return Array.from(this.chatUsers.values()).map(u => ({
      id: u.id,
      name: u.name,
      hasPin: u.hasPin
    }));
  }

  public getChatUserById(id: string): ChatUser | undefined {
    return this.chatUsers.get(id);
  }

  // Site Agent Directory Management
  public registerAgent(
    id: string, 
    name: string, 
    socketId: string, 
    rustDeskId?: string, 
    rustDeskPassword?: string,
    location?: { country?: string; city?: string; lat?: number; lon?: number; isp?: string }
  ): SiteInfo {
    const existing = this.sites.get(id);
    const updated: SiteInfo = {
      id,
      name: existing?.name || name || id,
      status: 'online',
      socketId,
      rustDeskId: rustDeskId || existing?.rustDeskId,
      rustDeskPassword: rustDeskPassword || existing?.rustDeskPassword,
      lastSeen: Date.now(),
      location: location || existing?.location
    };
    this.sites.set(id, updated);
    this.saveSites();
    return updated;
  }

  public disconnectSocket(socketId: string): SiteInfo | null {
    for (const [id, site] of this.sites.entries()) {
      if (site.socketId === socketId) {
        site.status = 'offline';
        site.socketId = undefined;
        site.lastSeen = Date.now();
        this.saveSites();
        return site;
      }
    }
    return null;
  }

  public renameSite(id: string, newName: string): boolean {
    const site = this.sites.get(id);
    if (site) {
      site.name = newName;
      this.saveSites();
      return true;
    }
    return false;
  }

  public deleteSite(id: string): boolean {
    if (this.sites.has(id)) {
      this.sites.delete(id);
      this.saveSites();
      return true;
    }
    return false;
  }

  public getSitesList(): SiteInfo[] {
    return Array.from(this.sites.values());
  }

  public getSiteById(id: string): SiteInfo | undefined {
    return this.sites.get(id);
  }

  public getSiteBySocketId(socketId: string): SiteInfo | undefined {
    for (const site of this.sites.values()) {
      if (site.socketId === socketId) {
        return site;
      }
    }
    return undefined;
  }
}

function stringIsEmpty(str: any): boolean {
  return !str || str.toString().trim().length === 0;
}
