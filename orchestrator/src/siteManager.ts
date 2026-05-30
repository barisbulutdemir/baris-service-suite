export interface SiteInfo {
  id: string; // Unique site identifier
  name: string; // Human readable name (e.g., "Italy", "Kuwait")
  status: 'online' | 'offline';
  rustDeskId?: string;
  rustDeskPassword?: string;
  socketId?: string;
  lastSeen?: number;
}

export class SiteManager {
  private sites: Map<string, SiteInfo> = new Map();

  // Load initial sites configuration or database (for now, we'll auto-register them dynamically)
  constructor() {
    // Add default placeholders to keep track of sites that have been registered before
    // Once an agent connects, it updates its status to online.
  }

  public registerAgent(id: string, name: string, socketId: string, rustDeskId?: string, rustDeskPassword?: string): SiteInfo {
    const existing = this.sites.get(id);
    const updated: SiteInfo = {
      id,
      name: name || existing?.name || id,
      status: 'online',
      socketId,
      rustDeskId: rustDeskId || existing?.rustDeskId,
      rustDeskPassword: rustDeskPassword || existing?.rustDeskPassword,
      lastSeen: Date.now()
    };
    this.sites.set(id, updated);
    return updated;
  }

  public disconnectSocket(socketId: string): SiteInfo | null {
    for (const [id, site] of this.sites.entries()) {
      if (site.socketId === socketId) {
        site.status = 'offline';
        site.socketId = undefined;
        site.lastSeen = Date.now();
        return site;
      }
    }
    return null;
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
