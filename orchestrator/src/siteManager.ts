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

export class SiteManager {
  private sites: Map<string, SiteInfo> = new Map();

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
