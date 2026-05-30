import { Server, Socket } from 'socket.io';
import { SiteManager } from './siteManager';

export function setupSocketHandler(io: Server, siteManager: SiteManager, authToken: string) {
  // Map of Master socket IDs to site IDs they are currently tunneling to
  const activeSessions = new Map<string, { masterSocketId: string; agentSocketId: string; siteId: string }>();

  io.use((socket: Socket, next) => {
    const token = socket.handshake.auth?.token || socket.handshake.headers?.['x-auth-token'];
    if (token !== authToken) {
      console.log(`[Socket] Auth failed for socket ${socket.id}`);
      return next(new Error('Authentication error: Invalid Token'));
    }
    next();
  });

  io.on('connection', (socket: Socket) => {
    const role = socket.handshake.query.role as string; // 'master' or 'agent'
    console.log(`[Socket] Client connected: ${socket.id}, Role: ${role}`);

    if (role === 'agent') {
      const siteId = socket.handshake.query.siteId as string;
      const siteName = socket.handshake.query.siteName as string;
      const rustDeskId = socket.handshake.query.rustDeskId as string;
      const rustDeskPassword = socket.handshake.query.rustDeskPassword as string;

      if (!siteId) {
        console.log(`[Socket] Agent connection rejected: Missing siteId`);
        socket.disconnect();
        return;
      }

      console.log(`[Socket] Agent registered: ${siteId} (${siteName})`);
      siteManager.registerAgent(siteId, siteName, socket.id, rustDeskId, rustDeskPassword);

      // Notify all master clients that sites list updated
      io.to('masters').emit('sites-list', siteManager.getSitesList());

      socket.on('disconnect', () => {
        console.log(`[Socket] Agent disconnected: ${siteId}`);
        const updatedSite = siteManager.disconnectSocket(socket.id);
        
        // Clean up any active tunnel sessions involving this agent
        for (const [sessionId, session] of activeSessions.entries()) {
          if (session.agentSocketId === socket.id) {
            console.log(`[Socket] Terminating active session ${sessionId} due to agent disconnect`);
            io.to(session.masterSocketId).emit('session-terminated', { reason: 'Agent disconnected' });
            activeSessions.delete(sessionId);
          }
        }

        io.to('masters').emit('sites-list', siteManager.getSitesList());
      });

      // Relay tunnel opened, data, and close events back to the master
      socket.on('tunnel-opened', (data: { masterSocketId: string; connectionId: string; success: boolean; error?: string }) => {
        io.to(data.masterSocketId).emit('tunnel-opened', {
          connectionId: data.connectionId,
          success: data.success,
          error: data.error
        });
      });

      socket.on('tunnel-data', (data: { masterSocketId: string; connectionId: string; chunk: any }) => {
        io.to(data.masterSocketId).emit('tunnel-data', {
          connectionId: data.connectionId,
          chunk: data.chunk
        });
      });

      socket.on('tunnel-close', (data: { masterSocketId: string; connectionId: string }) => {
        io.to(data.masterSocketId).emit('tunnel-close', {
          connectionId: data.connectionId
        });
      });

      socket.on('tunnel-udp', (data: { masterSocketId: string; connectionId: string; host: string; port: number; chunk: any }) => {
        io.to(data.masterSocketId).emit('tunnel-udp', {
          connectionId: data.connectionId,
          host: data.host,
          port: data.port,
          chunk: data.chunk
        });
      });

    } else if (role === 'master') {
      socket.join('masters');
      // Send current sites list immediately
      socket.emit('sites-list', siteManager.getSitesList());

      socket.on('disconnect', () => {
        console.log(`[Socket] Master disconnected: ${socket.id}`);
        // Clean up active sessions for this master
        for (const [sessionId, session] of activeSessions.entries()) {
          if (session.masterSocketId === socket.id) {
            console.log(`[Socket] Terminating active session ${sessionId} due to master disconnect`);
            io.to(session.agentSocketId).emit('session-terminated', { reason: 'Master disconnected' });
            activeSessions.delete(sessionId);
          }
        }
      });

      // Master requests to initiate connection session
      socket.on('start-session', (data: { siteId: string }, callback: (res: { success: boolean; error?: string }) => void) => {
        const site = siteManager.getSiteById(data.siteId);
        if (!site || site.status !== 'online' || !site.socketId) {
          return callback({ success: false, error: 'Site is offline or not found' });
        }

        const sessionId = `${socket.id}_${site.socketId}`;
        activeSessions.set(sessionId, {
          masterSocketId: socket.id,
          agentSocketId: site.socketId,
          siteId: data.siteId
        });

        // Notify Agent that a session started
        io.to(site.socketId).emit('session-started', { masterSocketId: socket.id });
        console.log(`[Socket] Session started: ${sessionId}`);
        callback({ success: true });
      });

      // Master requests to terminate session
      socket.on('stop-session', (data: { siteId: string }) => {
        const site = siteManager.getSiteById(data.siteId);
        if (site && site.socketId) {
          const sessionId = `${socket.id}_${site.socketId}`;
          activeSessions.delete(sessionId);
          io.to(site.socketId).emit('session-stopped', { masterSocketId: socket.id });
          console.log(`[Socket] Session stopped by master: ${sessionId}`);
        }
      });

      // Tunnel relaying events from master to agent
      socket.on('tunnel-open', (data: { siteId: string; connectionId: string; host: string; port: number }) => {
        const site = siteManager.getSiteById(data.siteId);
        if (site && site.socketId) {
          io.to(site.socketId).emit('tunnel-open', {
            masterSocketId: socket.id,
            connectionId: data.connectionId,
            host: data.host,
            port: data.port
          });
        }
      });

      socket.on('tunnel-data', (data: { siteId: string; connectionId: string; chunk: any }) => {
        const site = siteManager.getSiteById(data.siteId);
        if (site && site.socketId) {
          io.to(site.socketId).emit('tunnel-data', {
            masterSocketId: socket.id,
            connectionId: data.connectionId,
            chunk: data.chunk
          });
        }
      });

      socket.on('tunnel-close', (data: { siteId: string; connectionId: string }) => {
        const site = siteManager.getSiteById(data.siteId);
        if (site && site.socketId) {
          io.to(site.socketId).emit('tunnel-close', {
            masterSocketId: socket.id,
            connectionId: data.connectionId
          });
        }
      });

      socket.on('tunnel-udp', (data: { siteId: string; connectionId: string; host: string; port: number; chunk: any }) => {
        const site = siteManager.getSiteById(data.siteId);
        if (site && site.socketId) {
          io.to(site.socketId).emit('tunnel-udp', {
            masterSocketId: socket.id,
            connectionId: data.connectionId,
            host: data.host,
            port: data.port,
            chunk: data.chunk
          });
        }
      });
    }
  });
}
