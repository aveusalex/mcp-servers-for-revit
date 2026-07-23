import { SESSION_TTL_MS } from "./config.js";

/**
 * @typedef {object} DocumentMeta
 * @property {string} docId    - Document.ProjectInformation.UniqueId (stable across "save as").
 * @property {string} title
 * @property {string} pathName
 * @property {boolean} isActive
 * @property {boolean} isWorkshared
 * @property {string} revitVersion
 * @property {string} sessionId - owning plugin session (PID-version).
 */

/**
 * Tracks which Revit process (session) owns which documents, and how to reach
 * each session. Routing key is the DOCUMENT, not the port: Revit is
 * single-process/multi-document, so one session usually owns several docs.
 */
export class Registry {
  /** @param {number} [ttlMs] heartbeat time-to-live; sessions idle longer are swept. */
  constructor(ttlMs = SESSION_TTL_MS) {
    this.ttlMs = ttlMs;
    /** @type {Map<string, { ws: import("ws").WebSocket, meta: object, lastBeat: number }>} */
    this.sessions = new Map();
    /** @type {Map<string, DocumentMeta>} docId -> meta (includes sessionId) */
    this.documents = new Map();
  }

  /**
   * Register or refresh a plugin session and replace its full document set.
   * The plugin is the source of truth: every heartbeat carries the complete
   * list of open documents, so we reconcile rather than patch.
   *
   * @param {string} sessionId
   * @param {import("ws").WebSocket} ws
   * @param {object} sessionMeta - { revitVersion, pid, ... }
   * @param {DocumentMeta[]} docs
   */
  upsertSession(sessionId, ws, sessionMeta, docs) {
    this.sessions.set(sessionId, {
      ws,
      meta: sessionMeta || {},
      lastBeat: Date.now(),
    });
    this._reconcileDocuments(sessionId, docs || []);
  }

  /** Refresh only the liveness clock (used on any inbound message). */
  touch(sessionId) {
    const s = this.sessions.get(sessionId);
    if (s) s.lastBeat = Date.now();
  }

  _reconcileDocuments(sessionId, docs) {
    // Drop this session's stale docs, then re-add the current set.
    for (const [docId, meta] of this.documents) {
      if (meta.sessionId === sessionId) this.documents.delete(docId);
    }
    for (const d of docs) {
      if (!d || !d.docId) continue;
      this.documents.set(d.docId, { ...d, sessionId });
    }
  }

  /** @param {string} sessionId */
  removeSession(sessionId) {
    this.sessions.delete(sessionId);
    for (const [docId, meta] of this.documents) {
      if (meta.sessionId === sessionId) this.documents.delete(docId);
    }
  }

  /** Remove whichever session owns this socket (used on 'close'). */
  removeBySocket(ws) {
    for (const [sessionId, s] of this.sessions) {
      if (s.ws === ws) {
        this.removeSession(sessionId);
        return sessionId;
      }
    }
    return null;
  }

  /** @returns {DocumentMeta[]} */
  listDocuments() {
    return [...this.documents.values()];
  }

  /**
   * Resolve the session socket that owns a docId.
   * @param {string} docId
   * @returns {{ ws: import("ws").WebSocket, meta: object } | null}
   */
  sessionForDoc(docId) {
    const doc = this.documents.get(docId);
    if (!doc) return null;
    const s = this.sessions.get(doc.sessionId);
    return s ? { ws: s.ws, meta: s.meta } : null;
  }

  /**
   * Evict sessions whose heartbeat has lapsed. Returns the evicted sessionIds.
   * @returns {string[]}
   */
  sweep(now = Date.now()) {
    const dead = [];
    for (const [sessionId, s] of this.sessions) {
      if (now - s.lastBeat > this.ttlMs) dead.push(sessionId);
    }
    for (const sessionId of dead) this.removeSession(sessionId);
    return dead;
  }
}
