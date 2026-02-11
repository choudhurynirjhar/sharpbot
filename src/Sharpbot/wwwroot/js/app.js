// ============================================================================
// Sharpbot — Frontend Application
// ============================================================================

const API = {
    chat: '/api/chat',
    sessions: '/api/chat/sessions',
    status: '/api/status',
    config: '/api/config',
    onboard: '/api/config/onboard',
    cron: '/api/cron',
    channels: '/api/channels',
    skills: '/api/skills',
    logs: '/api/logs',
    usage: '/api/usage',
    usageHistory: '/api/usage/history',
};

// ── State ───────────────────────────────────────────────────────────────────
let currentSessionId = 'web:default';
let isProcessing = false;
let chatHistory = []; // local display history

// ── Initialization ──────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
    initNavigation();
    initChat();
    initSettings();
    initCron();
    initSessionsPanel();
    initSkills();
    initChannels();
    initLogs();
    initUsage();
    initTheme();
    initSidebar();
    loadSessions();
    loadStatus();
});

// ============================================================================
// Navigation
// ============================================================================
function initNavigation() {
    document.querySelectorAll('.nav-link').forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            const tab = link.dataset.tab;
            switchTab(tab);
        });
    });
}

function switchTab(tab) {
    // Update nav links
    document.querySelectorAll('.nav-link').forEach(l => l.classList.remove('active'));
    document.querySelector(`.nav-link[data-tab="${tab}"]`)?.classList.add('active');

    // Update panels
    document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
    document.getElementById(`panel-${tab}`)?.classList.add('active');

    // Load data for the tab
    if (tab === 'status') loadStatus();
    if (tab === 'settings') loadSettings();
    if (tab === 'cron') loadCronJobs();
    if (tab === 'sessions') loadSessionsPanel();
    if (tab === 'skills') loadSkills();
    if (tab === 'channels') loadChannels();
    if (tab === 'logs') { loadLogs(); startLogsAutoRefresh(); }
    if (tab === 'usage') loadUsage();

    // Close mobile sidebar
    document.getElementById('sidebar')?.classList.remove('open');
}

// ============================================================================
// Chat
// ============================================================================
function initChat() {
    const input = document.getElementById('chat-input');
    const sendBtn = document.getElementById('send-btn');
    const newChatBtn = document.getElementById('new-chat-btn');

    // Send message
    sendBtn.addEventListener('click', () => sendMessage());

    // Enter to send, Shift+Enter for new line
    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });

    // Auto-resize textarea
    input.addEventListener('input', () => {
        input.style.height = 'auto';
        input.style.height = Math.min(input.scrollHeight, 200) + 'px';
    });

    // New chat
    newChatBtn.addEventListener('click', () => {
        currentSessionId = 'web:' + Date.now();
        chatHistory = [];
        renderMessages();
        document.getElementById('welcome-message').classList.remove('hidden');
        input.focus();
    });

    // Suggestion chips
    document.querySelectorAll('.suggestion-chip').forEach(chip => {
        chip.addEventListener('click', () => {
            input.value = chip.dataset.msg;
            sendMessage();
        });
    });
}

async function sendMessage() {
    const input = document.getElementById('chat-input');
    const message = input.value.trim();
    if (!message || isProcessing) return;

    // Hide welcome
    const welcome = document.getElementById('welcome-message');
    if (welcome) welcome.classList.add('hidden');

    // Add user message
    chatHistory.push({ role: 'user', content: message });
    renderMessages();

    // Clear input
    input.value = '';
    input.style.height = 'auto';

    // Show typing indicator
    isProcessing = true;
    updateSendButton();
    showTypingIndicator();

    try {
        const response = await fetch(API.chat, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ message, sessionId: currentSessionId }),
        });

        const data = await response.json();
        removeTypingIndicator();

        if (data.error) {
            chatHistory.push({ role: 'assistant', content: `⚠️ ${data.message}`, isError: true });
        } else {
            chatHistory.push({
                role: 'assistant',
                content: data.message,
                toolCalls: data.toolCalls || [],
                stats: data.stats || null,
                timestamp: data.timestamp,
            });
            currentSessionId = data.sessionId || currentSessionId;
        }
    } catch (err) {
        removeTypingIndicator();
        chatHistory.push({ role: 'assistant', content: `⚠️ Network error: ${err.message}`, isError: true });
    }

    isProcessing = false;
    updateSendButton();
    renderMessages();
    loadSessions(); // refresh session list
}

function renderMessages() {
    const container = document.getElementById('chat-messages');
    const welcome = document.getElementById('welcome-message');

    // Keep only the welcome message div, remove all message divs
    const existingMessages = container.querySelectorAll('.message');
    existingMessages.forEach(m => m.remove());

    if (chatHistory.length === 0 && welcome) {
        welcome.classList.remove('hidden');
        return;
    }

    if (welcome) welcome.classList.add('hidden');

    chatHistory.forEach(msg => {
        const el = createMessageElement(msg);
        container.appendChild(el);
    });

    scrollToBottom();
}

function createMessageElement(msg) {
    const div = document.createElement('div');
    div.className = `message ${msg.role}`;

    const avatar = msg.role === 'user' ? 'You' : '🐈';
    const roleName = msg.role === 'user' ? 'You' : 'Sharpbot';

    // Build tool calls section if present
    let toolCallsHtml = '';
    if (msg.toolCalls && msg.toolCalls.length > 0) {
        const toolBadges = msg.toolCalls.map((tc, idx) => {
            const statusIcon = tc.success ? '✓' : '✗';
            const statusClass = tc.success ? 'tool-success' : 'tool-error';
            const duration = formatDuration(tc.durationMs);
            return `
                <div class="tool-call-badge ${statusClass}" onclick="toggleToolDetail(this)">
                    <span class="tool-call-icon">${statusIcon}</span>
                    <span class="tool-call-name">${escapeHtml(tc.name)}</span>
                    <span class="tool-call-duration">${duration}</span>
                    ${tc.error ? `<div class="tool-call-detail hidden"><span class="tool-call-error">Error: ${escapeHtml(tc.error)}</span></div>` : ''}
                </div>
            `;
        }).join('');

        toolCallsHtml = `<div class="tool-calls-section">${toolBadges}</div>`;
    }

    // Build stats section if present
    let statsHtml = '';
    if (msg.stats) {
        const s = msg.stats;
        const totalTime = formatDuration(s.totalDurationMs);
        const parts = [`${totalTime}`];
        if (s.iterations > 1) parts.push(`${s.iterations} iterations`);
        if (s.totalTokens > 0) parts.push(`${s.totalTokens.toLocaleString()} tokens`);
        if (s.model) parts.push(s.model);
        statsHtml = `<div class="message-stats">${parts.join(' · ')}</div>`;
    }

    div.innerHTML = `
        <div class="message-avatar">${avatar}</div>
        <div class="message-body">
            <div class="message-role">${roleName}</div>
            ${toolCallsHtml}
            <div class="message-content">${renderMarkdown(msg.content)}</div>
            ${statsHtml}
        </div>
    `;

    return div;
}

function toggleToolDetail(badge) {
    const detail = badge.querySelector('.tool-call-detail');
    if (detail) detail.classList.toggle('hidden');
}

function formatDuration(ms) {
    if (ms < 1000) return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    return `${(ms / 60000).toFixed(1)}m`;
}

function showTypingIndicator() {
    const container = document.getElementById('chat-messages');
    const div = document.createElement('div');
    div.className = 'message assistant typing-msg';
    div.innerHTML = `
        <div class="message-avatar">🐈</div>
        <div class="message-body">
            <div class="message-role">Sharpbot</div>
            <div class="message-content">
                <div class="typing-indicator">
                    <span></span><span></span><span></span>
                </div>
            </div>
        </div>
    `;
    container.appendChild(div);
    scrollToBottom();
}

function removeTypingIndicator() {
    document.querySelector('.typing-msg')?.remove();
}

function updateSendButton() {
    const btn = document.getElementById('send-btn');
    btn.disabled = isProcessing;
}

function scrollToBottom() {
    const container = document.getElementById('chat-messages');
    container.scrollTop = container.scrollHeight;
}

// ── Simple Markdown Renderer ────────────────────────────────────────────────
function renderMarkdown(text) {
    if (!text) return '';

    // Escape HTML
    let html = text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');

    // Code blocks (```...```)
    html = html.replace(/```(\w*)\n?([\s\S]*?)```/g, (_, lang, code) => {
        return `<pre><code>${code.trim()}</code></pre>`;
    });

    // Inline code (`...`)
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

    // Bold (**...**)
    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');

    // Italic (*...*)
    html = html.replace(/(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)/g, '<em>$1</em>');

    // Headers
    html = html.replace(/^### (.+)$/gm, '<h4>$1</h4>');
    html = html.replace(/^## (.+)$/gm, '<h3>$1</h3>');
    html = html.replace(/^# (.+)$/gm, '<h2>$1</h2>');

    // Links [text](url)
    html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');

    // Unordered lists
    html = html.replace(/^[-*] (.+)$/gm, '<li>$1</li>');
    html = html.replace(/(<li>.*<\/li>\n?)+/g, '<ul>$&</ul>');

    // Paragraphs (split by double newlines)
    html = html
        .split(/\n\n+/)
        .map(block => {
            block = block.trim();
            if (!block) return '';
            if (block.startsWith('<h') || block.startsWith('<pre') || block.startsWith('<ul') || block.startsWith('<ol'))
                return block;
            // Wrap in <p> but handle single newlines as <br>
            return '<p>' + block.replace(/\n/g, '<br>') + '</p>';
        })
        .join('\n');

    return html;
}

// ============================================================================
// Sessions
// ============================================================================
async function loadSessions() {
    try {
        const response = await fetch(API.sessions);
        const sessions = await response.json();
        renderSessions(sessions);
    } catch (err) {
        console.error('Failed to load sessions:', err);
    }
}

function renderSessions(sessions) {
    const container = document.getElementById('sessions-list');
    const header = '<div class="sessions-header">Sessions</div>';

    if (!sessions || sessions.length === 0) {
        container.innerHTML = header + '<div class="empty-state" style="padding:12px;font-size:0.8rem;">No sessions yet</div>';
        return;
    }

    const items = sessions.map(s => {
        const key = s.key || '';
        const isActive = key === currentSessionId;
        const label = key.length > 28 ? key.substring(0, 28) + '…' : key;
        return `
            <div class="session-item ${isActive ? 'active' : ''}" data-key="${escapeHtml(key)}">
                <span>${escapeHtml(label)}</span>
                <button class="delete-btn" data-key="${escapeHtml(key)}" title="Delete session">✕</button>
            </div>
        `;
    }).join('');

    container.innerHTML = header + items;

    // Session click handlers
    container.querySelectorAll('.session-item').forEach(item => {
        item.addEventListener('click', (e) => {
            if (e.target.classList.contains('delete-btn')) return;
            const key = item.dataset.key;
            switchSession(key);
        });
    });

    // Delete handlers
    container.querySelectorAll('.delete-btn').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.stopPropagation();
            const key = btn.dataset.key;
            if (confirm(`Delete session "${key}"?`)) {
                await deleteSession(key);
            }
        });
    });
}

function switchSession(key) {
    currentSessionId = key;
    chatHistory = []; // clear local history (server has the real history)
    renderMessages();
    loadSessions();
    showToast(`Switched to session: ${key}`);
}

async function deleteSession(key) {
    try {
        const encoded = encodeURIComponent(key.replace(/:/g, '_'));
        await fetch(`${API.sessions}/${encoded}`, { method: 'DELETE' });
        if (key === currentSessionId) {
            currentSessionId = 'web:default';
            chatHistory = [];
            renderMessages();
        }
        loadSessions();
        showToast('Session deleted');
    } catch (err) {
        showToast('Failed to delete session');
    }
}

// ============================================================================
// Channels
// ============================================================================
const CHANNEL_ICONS = {
    telegram: '✈️',
    whatsapp: '💬',
    discord: '🎮',
    feishu: '🐦',
    slack: '💼',
};

function initChannels() {
    document.getElementById('refresh-channels-btn')?.addEventListener('click', loadChannels);
}

async function loadChannels() {
    try {
        const response = await fetch(API.channels);
        const data = await response.json();
        renderChannels(data);
    } catch (err) {
        document.getElementById('channels-grid').innerHTML =
            '<div class="empty-state">Failed to load channels</div>';
    }
}

function renderChannels(data) {
    const container = document.getElementById('channels-grid');
    const channels = data.channels || [];

    if (channels.length === 0) {
        container.innerHTML = '<div class="empty-state">No channels configured</div>';
        return;
    }

    container.innerHTML = channels.map(ch => {
        const icon = CHANNEL_ICONS[ch.id] || '📡';
        let statusBadge, statusClass;
        if (ch.running) {
            statusBadge = '<span class="badge badge-success">Running</span>';
            statusClass = 'channel-running';
        } else if (ch.enabled) {
            statusBadge = '<span class="badge badge-warning">Enabled</span>';
            statusClass = 'channel-enabled';
        } else {
            statusBadge = '<span class="badge badge-neutral">Disabled</span>';
            statusClass = 'channel-disabled';
        }

        // Build config details
        const configDetails = [];
        const cfg = ch.config || {};
        if (cfg.hasToken !== undefined) configDetails.push(cfg.hasToken ? 'Token set' : 'No token');
        if (cfg.hasAppId !== undefined) configDetails.push(cfg.hasAppId ? 'App ID set' : 'No app ID');
        if (cfg.hasBridgeUrl !== undefined) configDetails.push(cfg.hasBridgeUrl ? 'Bridge configured' : 'No bridge URL');
        if (cfg.hasBotToken !== undefined) configDetails.push(cfg.hasBotToken ? 'Bot token set' : 'No bot token');
        if (cfg.hasAppToken !== undefined) configDetails.push(cfg.hasAppToken ? 'App token set' : 'No app token');
        if (cfg.mode) configDetails.push(`Mode: ${cfg.mode}`);
        if (cfg.allowedUsers > 0) configDetails.push(`${cfg.allowedUsers} allowed user(s)`);
        if (cfg.bridgeUrl) configDetails.push(cfg.bridgeUrl);

        const detailsHtml = configDetails.length > 0
            ? configDetails.map(d => `<div class="channel-detail">${escapeHtml(d)}</div>`).join('')
            : '<div class="channel-detail" style="color:var(--text-muted)">Not configured</div>';

        return `
            <div class="channel-card ${statusClass}">
                <div class="channel-card-header">
                    <span class="channel-icon">${icon}</span>
                    <span class="channel-label">${escapeHtml(ch.label)}</span>
                    ${statusBadge}
                </div>
                <div class="channel-card-body">
                    ${detailsHtml}
                </div>
                <div class="channel-card-footer">
                    <span class="channel-id">${escapeHtml(ch.id)}</span>
                </div>
            </div>
        `;
    }).join('');
}

// ============================================================================
// Logs
// ============================================================================
let logsAutoRefreshTimer = null;
let logsLastId = 0;

function initLogs() {
    document.getElementById('refresh-logs-btn')?.addEventListener('click', loadLogs);
    document.getElementById('clear-logs-btn')?.addEventListener('click', clearLogs);
    document.getElementById('logs-level-filter')?.addEventListener('change', () => { logsLastId = 0; loadLogs(); });
    document.getElementById('logs-search')?.addEventListener('input', debounce(() => { logsLastId = 0; loadLogs(); }, 300));
    document.getElementById('logs-auto-refresh')?.addEventListener('change', (e) => {
        if (e.target.checked) startLogsAutoRefresh();
        else stopLogsAutoRefresh();
    });
}

function startLogsAutoRefresh() {
    stopLogsAutoRefresh();
    const checkbox = document.getElementById('logs-auto-refresh');
    if (!checkbox?.checked) return;
    logsAutoRefreshTimer = setInterval(loadLogs, 3000);
}

function stopLogsAutoRefresh() {
    if (logsAutoRefreshTimer) {
        clearInterval(logsAutoRefreshTimer);
        logsAutoRefreshTimer = null;
    }
}

async function loadLogs() {
    try {
        const level = document.getElementById('logs-level-filter')?.value || '';
        const search = document.getElementById('logs-search')?.value || '';

        const params = new URLSearchParams();
        if (level) params.set('level', level);
        if (search) params.set('search', search);
        params.set('limit', '500');

        const response = await fetch(`${API.logs}?${params}`);
        const data = await response.json();

        // Update stats
        const stats = document.getElementById('logs-stats');
        stats.textContent = `${data.count} entries shown · ${data.bufferSize} in buffer · ${data.totalEntries} total`;

        renderLogs(data.entries || []);
    } catch (err) {
        document.getElementById('logs-output').innerHTML =
            '<div class="empty-state">Failed to load logs</div>';
    }
}

function renderLogs(entries) {
    const container = document.getElementById('logs-output');

    if (entries.length === 0) {
        container.innerHTML = '<div class="empty-state">No log entries yet.</div>';
        return;
    }

    const shouldAutoScroll = container.scrollTop + container.clientHeight >= container.scrollHeight - 50;

    container.innerHTML = entries.map(e => {
        const levelClass = `log-${e.level.toLowerCase()}`;
        const levelShort = { Information: 'INF', Warning: 'WRN', Error: 'ERR', Critical: 'CRT', Debug: 'DBG', Trace: 'TRC' }[e.level] || e.level;
        const time = new Date(e.timestamp).toLocaleTimeString();
        const msg = escapeHtml(e.message);
        const cat = escapeHtml(e.category);
        const exc = e.exception ? `\n<span class="log-exception">${escapeHtml(e.exception)}</span>` : '';

        return `<div class="log-line ${levelClass}"><span class="log-time">${time}</span> <span class="log-level">${levelShort}</span> <span class="log-category">${cat}</span> <span class="log-msg">${msg}</span>${exc}</div>`;
    }).join('');

    if (shouldAutoScroll) {
        container.scrollTop = container.scrollHeight;
    }
}

async function clearLogs() {
    if (!confirm('Clear all buffered logs?')) return;
    try {
        await fetch(API.logs, { method: 'DELETE' });
        logsLastId = 0;
        loadLogs();
        showToast('Logs cleared');
    } catch (err) {
        showToast('Failed to clear logs');
    }
}

function debounce(fn, ms) {
    let timer;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), ms);
    };
}

// ============================================================================
// Sessions Panel
// ============================================================================
function initSessionsPanel() {
    document.getElementById('refresh-sessions-btn')?.addEventListener('click', loadSessionsPanel);
    document.getElementById('delete-all-sessions-btn')?.addEventListener('click', deleteAllSessions);
}

async function loadSessionsPanel() {
    try {
        const response = await fetch(API.sessions);
        const sessions = await response.json();
        renderSessionsTable(sessions);
    } catch (err) {
        document.getElementById('sessions-table-wrap').innerHTML =
            '<div class="empty-state">Failed to load sessions</div>';
    }
}

function renderSessionsTable(sessions) {
    const container = document.getElementById('sessions-table-wrap');

    if (!sessions || sessions.length === 0) {
        container.innerHTML = '<div class="empty-state">No sessions yet. Start a chat to create one.</div>';
        return;
    }

    const rows = sessions.map(s => {
        const key = s.key || '';
        const isActive = key === currentSessionId;
        const msgs = s.messageCount ?? '—';
        const updated = s.updated_at ? formatRelativeTime(s.updated_at) : '—';
        const created = s.created_at ? formatRelativeTime(s.created_at) : '—';

        // Parse channel from key (format "channel:id")
        const colonIdx = key.indexOf(':');
        const channel = colonIdx >= 0 ? key.substring(0, colonIdx) : '—';

        return `
            <tr class="${isActive ? 'row-active' : ''}">
                <td class="cell-key">
                    <span class="session-key-text" title="${escapeHtml(key)}">${escapeHtml(key)}</span>
                    ${isActive ? '<span class="badge badge-success" style="margin-left:6px">Active</span>' : ''}
                </td>
                <td><span class="badge badge-neutral">${escapeHtml(channel)}</span></td>
                <td class="cell-center">${msgs}</td>
                <td class="cell-muted">${updated}</td>
                <td class="cell-muted">${created}</td>
                <td class="cell-actions">
                    <button class="btn btn-ghost btn-sm" onclick="openSessionChat('${escapeHtml(key)}')">Open</button>
                    <button class="btn btn-danger btn-sm" onclick="deleteSessionFromPanel('${escapeHtml(key)}')">Delete</button>
                </td>
            </tr>
        `;
    }).join('');

    container.innerHTML = `
        <table class="data-table">
            <thead>
                <tr>
                    <th>Session</th>
                    <th>Channel</th>
                    <th class="cell-center">Messages</th>
                    <th>Last Activity</th>
                    <th>Created</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody>${rows}</tbody>
        </table>
    `;
}

window.openSessionChat = function(key) {
    currentSessionId = key;
    chatHistory = [];
    renderMessages();
    loadSessions();
    switchTab('chat');
    showToast(`Switched to session: ${key}`);
};

window.deleteSessionFromPanel = async function(key) {
    if (!confirm(`Delete session "${key}"?`)) return;
    try {
        const encoded = encodeURIComponent(key.replace(/:/g, '_'));
        await fetch(`${API.sessions}/${encoded}`, { method: 'DELETE' });
        if (key === currentSessionId) {
            currentSessionId = 'web:default';
            chatHistory = [];
            renderMessages();
        }
        loadSessionsPanel();
        loadSessions();
        showToast('Session deleted');
    } catch (err) {
        showToast('Failed to delete session');
    }
};

async function deleteAllSessions() {
    if (!confirm('Delete ALL sessions? This cannot be undone.')) return;
    try {
        const response = await fetch(API.sessions);
        const sessions = await response.json();
        for (const s of sessions) {
            const encoded = encodeURIComponent((s.key || '').replace(/:/g, '_'));
            await fetch(`${API.sessions}/${encoded}`, { method: 'DELETE' });
        }
        currentSessionId = 'web:default';
        chatHistory = [];
        renderMessages();
        loadSessionsPanel();
        loadSessions();
        showToast(`Deleted ${sessions.length} sessions`);
    } catch (err) {
        showToast('Failed to delete sessions');
    }
}

function formatRelativeTime(isoStr) {
    try {
        const date = new Date(isoStr);
        const now = new Date();
        const diffMs = now - date;
        const diffSec = Math.floor(diffMs / 1000);
        if (diffSec < 60) return 'just now';
        const diffMin = Math.floor(diffSec / 60);
        if (diffMin < 60) return `${diffMin}m ago`;
        const diffHrs = Math.floor(diffMin / 60);
        if (diffHrs < 24) return `${diffHrs}h ago`;
        const diffDays = Math.floor(diffHrs / 24);
        if (diffDays < 30) return `${diffDays}d ago`;
        return date.toLocaleDateString();
    } catch { return isoStr; }
}

// ============================================================================
// Skills
// ============================================================================
let allSkillsData = [];

function initSkills() {
    document.getElementById('refresh-skills-btn')?.addEventListener('click', loadSkills);
    document.getElementById('skills-search')?.addEventListener('input', filterSkills);
    document.getElementById('skills-filter')?.addEventListener('change', filterSkills);
}

async function loadSkills() {
    try {
        const response = await fetch(API.skills);
        const data = await response.json();
        allSkillsData = data.skills || [];

        // Update summary
        const summary = document.getElementById('skills-summary');
        summary.innerHTML = `
            <span class="skills-stat">${data.total ?? 0} total</span>
            <span class="skills-stat skills-stat-ok">${data.available ?? 0} available</span>
            ${data.unavailable > 0 ? `<span class="skills-stat skills-stat-warn">${data.unavailable} unavailable</span>` : ''}
        `;

        filterSkills();
    } catch (err) {
        document.getElementById('skills-list').innerHTML =
            '<div class="empty-state">Failed to load skills</div>';
    }
}

function filterSkills() {
    const query = (document.getElementById('skills-search')?.value || '').toLowerCase().trim();
    const filter = document.getElementById('skills-filter')?.value || 'all';

    let filtered = allSkillsData;

    if (filter === 'available') filtered = filtered.filter(s => s.available);
    if (filter === 'unavailable') filtered = filtered.filter(s => !s.available);

    if (query) {
        filtered = filtered.filter(s =>
            s.name.toLowerCase().includes(query) ||
            (s.description || '').toLowerCase().includes(query)
        );
    }

    renderSkillsList(filtered);
}

function renderSkillsList(skills) {
    const container = document.getElementById('skills-list');

    if (skills.length === 0) {
        container.innerHTML = '<div class="empty-state">No skills found.</div>';
        return;
    }

    container.innerHTML = skills.map(s => {
        const statusBadge = s.available
            ? '<span class="badge badge-success">Available</span>'
            : '<span class="badge badge-error">Unavailable</span>';

        const sourceBadge = `<span class="badge badge-neutral">${escapeHtml(s.source)}</span>`;

        const metaItems = Object.entries(s.metadata || {})
            .filter(([k]) => k !== 'description')
            .map(([k, v]) => `<span class="skill-meta-item">${escapeHtml(k)}: ${escapeHtml(v)}</span>`)
            .join('');

        return `
            <div class="skill-card ${s.available ? '' : 'skill-unavailable'}">
                <div class="skill-card-header">
                    <div class="skill-card-title">
                        <span class="skill-name">${escapeHtml(s.name)}</span>
                        ${statusBadge}
                        ${sourceBadge}
                    </div>
                </div>
                <div class="skill-card-desc">${escapeHtml(s.description || 'No description')}</div>
                ${!s.available && s.unavailableReason ? `<div class="skill-card-reason">${escapeHtml(s.unavailableReason)}</div>` : ''}
                ${metaItems ? `<div class="skill-meta">${metaItems}</div>` : ''}
                <div class="skill-card-path">${escapeHtml(s.path)}</div>
            </div>
        `;
    }).join('');
}

// ============================================================================
// Settings
// ============================================================================
const PROVIDERS = [
    { key: 'anthropic', label: 'Anthropic', configKey: 'Anthropic' },
    { key: 'openai', label: 'OpenAI', configKey: 'OpenAI' },
    { key: 'openrouter', label: 'OpenRouter', configKey: 'OpenRouter' },
    { key: 'deepseek', label: 'DeepSeek', configKey: 'DeepSeek' },
    { key: 'groq', label: 'Groq', configKey: 'Groq' },
    { key: 'gemini', label: 'Gemini', configKey: 'Gemini' },
    { key: 'zhipu', label: 'Zhipu', configKey: 'Zhipu' },
    { key: 'dashscope', label: 'DashScope', configKey: 'DashScope' },
    { key: 'vllm', label: 'vLLM', configKey: 'Vllm' },
    { key: 'moonshot', label: 'Moonshot', configKey: 'Moonshot' },
    { key: 'aihubmix', label: 'AiHubMix', configKey: 'AiHubMix' },
];

function initSettings() {
    // Render provider fields
    const container = document.getElementById('provider-fields');
    const grid = document.createElement('div');
    grid.className = 'provider-grid';

    PROVIDERS.forEach(p => {
        const envVar = `SHARPBOT_Providers__${p.configKey}__ApiKey`;
        grid.innerHTML += `
            <div class="provider-field">
                <label>${p.label}</label>
                <span class="provider-env-hint" title="${envVar}"><code>${envVar}</code></span>
                <span class="provider-status" id="provider-status-${p.key}"></span>
            </div>
        `;
    });
    container.appendChild(grid);

    // Save button
    document.getElementById('save-settings-btn').addEventListener('click', saveSettings);
    document.getElementById('reload-settings-btn').addEventListener('click', loadSettings);
}

async function loadSettings() {
    try {
        const response = await fetch(API.config);
        const config = await response.json();

        // Agent defaults
        document.getElementById('setting-model').value = config.agents?.defaults?.model || '';
        document.getElementById('setting-max-tokens').value = config.agents?.defaults?.maxTokens || '';
        document.getElementById('setting-temperature').value = config.agents?.defaults?.temperature || '';
        document.getElementById('setting-max-iterations').value = config.agents?.defaults?.maxToolIterations || '';

        // Provider statuses (read-only — secrets come from env vars)
        const providers = config.providers || {};
        PROVIDERS.forEach(p => {
            const status = document.getElementById(`provider-status-${p.key}`);
            const providerData = providers[p.key];
            if (providerData?.hasApiKey) {
                status.textContent = providerData.maskedKey || '✓ Set';
                status.style.color = 'var(--success)';
            } else {
                status.textContent = 'Not set';
                status.style.color = 'var(--text-secondary)';
            }
        });

        // Tools — Brave search key is env-var only
        const braveStatus = document.getElementById('brave-key-status');
        if (braveStatus) {
            if (config.tools?.web?.search?.hasApiKey) {
                braveStatus.textContent = '✓ Set';
                braveStatus.style.color = 'var(--success)';
            } else {
                braveStatus.textContent = 'Not set';
                braveStatus.style.color = 'var(--text-secondary)';
            }
        }
        document.getElementById('setting-exec-timeout').value = config.tools?.exec?.timeout || 60;
        document.getElementById('setting-restrict-workspace').checked = config.tools?.restrictToWorkspace || false;

    } catch (err) {
        showToast('Failed to load settings');
        console.error(err);
    }
}

async function saveSettings() {
    const statusEl = document.getElementById('save-status');
    statusEl.textContent = 'Saving...';
    statusEl.style.color = 'var(--text-secondary)';

    try {
        // Build config update object — only include changed fields
        // Note: secrets (API keys, tokens) are NOT saved here; they come from env vars only
        const update = {
            Agents: {
                Defaults: {}
            },
            Tools: {
                Web: { Search: {} },
                Exec: {}
            }
        };

        // Agent defaults
        const model = document.getElementById('setting-model').value.trim();
        if (model) update.Agents.Defaults.Model = model;

        const maxTokens = parseInt(document.getElementById('setting-max-tokens').value);
        if (!isNaN(maxTokens)) update.Agents.Defaults.MaxTokens = maxTokens;

        const temp = parseFloat(document.getElementById('setting-temperature').value);
        if (!isNaN(temp)) update.Agents.Defaults.Temperature = temp;

        const maxIter = parseInt(document.getElementById('setting-max-iterations').value);
        if (!isNaN(maxIter)) update.Agents.Defaults.MaxToolIterations = maxIter;

        // Tools (non-secret settings only)
        const execTimeout = parseInt(document.getElementById('setting-exec-timeout').value);
        if (!isNaN(execTimeout)) update.Tools.Exec.Timeout = execTimeout;

        update.Tools.RestrictToWorkspace = document.getElementById('setting-restrict-workspace').checked;

        const response = await fetch(API.config, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(update),
        });

        const result = await response.json();

        if (result.success) {
            statusEl.textContent = '✓ Saved! Restart server to apply changes.';
            statusEl.style.color = 'var(--success)';
            loadSettings(); // refresh display
        } else {
            statusEl.textContent = `✗ ${result.message}`;
            statusEl.style.color = 'var(--error)';
        }
    } catch (err) {
        statusEl.textContent = `✗ Error: ${err.message}`;
        statusEl.style.color = 'var(--error)';
    }

    // Clear status after 5 seconds
    setTimeout(() => { statusEl.textContent = ''; }, 5000);
}

// ============================================================================
// Cron Jobs
// ============================================================================
function initCron() {
    document.getElementById('add-cron-btn').addEventListener('click', () => {
        document.getElementById('cron-form').classList.toggle('hidden');
    });

    document.getElementById('cancel-cron-btn').addEventListener('click', () => {
        document.getElementById('cron-form').classList.add('hidden');
    });

    document.getElementById('submit-cron-btn').addEventListener('click', addCronJob);
    document.getElementById('refresh-cron-btn').addEventListener('click', loadCronJobs);
}

async function loadCronJobs() {
    try {
        const response = await fetch(`${API.cron}?includeDisabled=true`);
        const data = await response.json();
        renderCronJobs(data.jobs || []);
    } catch (err) {
        document.getElementById('cron-list').innerHTML = '<div class="empty-state">Failed to load cron jobs</div>';
    }
}

function renderCronJobs(jobs) {
    const container = document.getElementById('cron-list');

    if (jobs.length === 0) {
        container.innerHTML = '<div class="empty-state">No scheduled jobs. Click "+ Add Job" to create one.</div>';
        return;
    }

    container.innerHTML = jobs.map(job => {
        const scheduleText = job.schedule.kind === 'every'
            ? `Every ${(job.schedule.everyMs || 0) / 1000}s`
            : job.schedule.kind === 'cron'
                ? `Cron: ${job.schedule.expr}`
                : 'One-time';

        const nextRun = job.state.nextRunAtMs
            ? new Date(job.state.nextRunAtMs).toLocaleString()
            : 'N/A';

        const statusBadge = job.enabled
            ? '<span class="badge badge-success">Enabled</span>'
            : '<span class="badge badge-neutral">Disabled</span>';

        return `
            <div class="cron-job-card">
                <div class="cron-job-info">
                    <div class="cron-job-name">${escapeHtml(job.name)} ${statusBadge}</div>
                    <div class="cron-job-details">
                        ${escapeHtml(scheduleText)} · Next: ${nextRun} · ID: ${job.id}
                    </div>
                </div>
                <div class="cron-job-actions">
                    <button class="btn btn-ghost btn-sm" onclick="toggleCronJob('${job.id}', ${!job.enabled})">
                        ${job.enabled ? 'Disable' : 'Enable'}
                    </button>
                    <button class="btn btn-ghost btn-sm" onclick="runCronJob('${job.id}')">Run</button>
                    <button class="btn btn-danger btn-sm" onclick="removeCronJob('${job.id}')">Delete</button>
                </div>
            </div>
        `;
    }).join('');
}

async function addCronJob() {
    const name = document.getElementById('cron-name').value.trim();
    const message = document.getElementById('cron-message').value.trim();
    const type = document.getElementById('cron-type').value;
    const value = document.getElementById('cron-value').value.trim();

    if (!name || !message || !value) {
        showToast('Please fill in all fields');
        return;
    }

    const body = { name, message };
    if (type === 'every') {
        body.everySeconds = parseInt(value);
    } else {
        body.cronExpr = value;
    }

    try {
        const response = await fetch(API.cron, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
        });
        const data = await response.json();

        if (data.success) {
            showToast(`Job "${name}" added`);
            document.getElementById('cron-form').classList.add('hidden');
            document.getElementById('cron-name').value = '';
            document.getElementById('cron-message').value = '';
            document.getElementById('cron-value').value = '';
            loadCronJobs();
        } else {
            showToast(data.message || 'Failed to add job');
        }
    } catch (err) {
        showToast('Failed to add job');
    }
}

// Global functions for inline onclick handlers
window.toggleCronJob = async function(id, enabled) {
    try {
        await fetch(`${API.cron}/${id}/enable`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled }),
        });
        loadCronJobs();
    } catch (err) {
        showToast('Failed to update job');
    }
};

window.runCronJob = async function(id) {
    try {
        await fetch(`${API.cron}/${id}/run`, { method: 'POST' });
        showToast('Job triggered');
        loadCronJobs();
    } catch (err) {
        showToast('Failed to trigger job');
    }
};

window.removeCronJob = async function(id) {
    if (!confirm('Delete this job?')) return;
    try {
        await fetch(`${API.cron}/${id}`, { method: 'DELETE' });
        showToast('Job deleted');
        loadCronJobs();
    } catch (err) {
        showToast('Failed to delete job');
    }
};

// ============================================================================
// Status
// ============================================================================
async function loadStatus() {
    try {
        const response = await fetch(API.status);
        const data = await response.json();
        renderStatus(data);
    } catch (err) {
        document.getElementById('status-container').innerHTML =
            '<div class="empty-state">Failed to load status</div>';
    }
}

function renderStatus(data) {
    const container = document.getElementById('status-container');

    const agentBadge = data.agentReady
        ? '<span class="badge badge-success">● Ready</span>'
        : '<span class="badge badge-error">● Not Ready</span>';

    const uptimeStr = formatUptime(data.uptimeSeconds || 0);

    // ── Health overview card ──────────────────────────────────
    const healthCard = `
        <div class="status-card status-card-hero">
            <div class="hero-status">
                <div class="hero-indicator ${data.agentReady ? 'hero-ok' : 'hero-err'}"></div>
                <div class="hero-info">
                    <div class="hero-title">Sharpbot ${agentBadge}</div>
                    <div class="hero-subtitle">v${data.version || '?'} · ${escapeHtml(data.runtime || '')} · ${escapeHtml(data.os || '')}</div>
                </div>
            </div>
            ${data.agentError ? `<div class="hero-error">${escapeHtml(data.agentError)}</div>` : ''}
            <div class="hero-stats">
                <div class="hero-stat">
                    <div class="hero-stat-value">${uptimeStr}</div>
                    <div class="hero-stat-label">Uptime</div>
                </div>
                <div class="hero-stat">
                    <div class="hero-stat-value">${escapeHtml(data.model || 'N/A')}</div>
                    <div class="hero-stat-label">Model</div>
                </div>
                <div class="hero-stat">
                    <div class="hero-stat-value">${data.sessionCount ?? 0}</div>
                    <div class="hero-stat-label">Sessions</div>
                </div>
                <div class="hero-stat">
                    <div class="hero-stat-value">${data.toolCount ?? 0}</div>
                    <div class="hero-stat-label">Tools</div>
                </div>
            </div>
        </div>
    `;

    // ── Providers card ────────────────────────────────────────
    const providerRows = (data.providers || []).map(p => `
        <div class="status-row">
            <span class="status-label">${escapeHtml(p.label)}</span>
            <span>${p.configured
                ? '<span class="badge badge-success">Configured</span>'
                : '<span class="badge badge-neutral">Not Set</span>'}</span>
        </div>
    `).join('');

    // ── Channels card ─────────────────────────────────────────
    const channelRows = (data.channels || []).map(ch => {
        let badge;
        if (ch.running) badge = '<span class="badge badge-success">Running</span>';
        else if (ch.enabled) badge = '<span class="badge badge-warning">Enabled</span>';
        else badge = '<span class="badge badge-neutral">Disabled</span>';
        const name = ch.name.charAt(0).toUpperCase() + ch.name.slice(1);
        return `
            <div class="status-row">
                <span class="status-label">${escapeHtml(name)}</span>
                <span>${badge}</span>
            </div>
        `;
    }).join('');

    // ── Tools card ────────────────────────────────────────────
    const toolRows = (data.tools || []).map((t, i) =>
        `<div class="tool-list-item${i % 2 === 0 ? '' : ' alt'}">${escapeHtml(t)}</div>`
    ).join('');

    // ── Cron card ─────────────────────────────────────────────
    const cronCard = `
        <div class="status-card">
            <h3>⏰ Cron Jobs</h3>
            <div class="status-row">
                <span class="status-label">Status</span>
                <span>${data.cron?.enabled
                    ? '<span class="badge badge-success">Running</span>'
                    : '<span class="badge badge-neutral">Stopped</span>'}</span>
            </div>
            <div class="status-row">
                <span class="status-label">Jobs</span>
                <span class="status-value">${data.cron?.jobs ?? 0}</span>
            </div>
        </div>
    `;

    // ── System info card ──────────────────────────────────────
    const sysCard = `
        <div class="status-card">
            <h3>🗂 System Info</h3>
            <div class="status-row">
                <span class="status-label">Config Path</span>
                <span class="status-value">${escapeHtml(data.configPath || '')}</span>
            </div>
            <div class="status-row">
                <span class="status-label">Workspace</span>
                <span class="status-value">${escapeHtml(data.workspace || '')}</span>
            </div>
        </div>
    `;

    container.innerHTML = `
        ${healthCard}
        <div class="status-grid">
            <div class="status-card">
                <h3>🔌 Providers</h3>
                ${providerRows || '<div class="empty-state">No providers</div>'}
            </div>
            <div class="status-card">
                <h3>📡 Channels</h3>
                ${channelRows || '<div class="empty-state">No channels configured</div>'}
            </div>
            ${cronCard}
            ${sysCard}
        </div>
        <div class="status-card">
            <h3>🛠 Registered Tools <span class="tool-count">${data.toolCount ?? 0}</span></h3>
            <div class="tools-list">
                ${toolRows || '<div class="empty-state">No tools</div>'}
            </div>
        </div>
    `;

    // Update version in sidebar
    document.getElementById('version-text').textContent = `v${data.version || '0.2.0'}`;
}

function formatUptime(seconds) {
    if (seconds < 60) return `${seconds}s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    if (h < 24) return `${h}h ${m}m`;
    const d = Math.floor(h / 24);
    return `${d}d ${h % 24}h`;
}

// ============================================================================
// Usage Tracking
// ============================================================================
let usageActivePeriod = '7d';
let _lastUsageSummary = null;
let _lastUsageHistory = [];

function initUsage() {
    document.getElementById('refresh-usage-btn')?.addEventListener('click', loadUsage);
    document.getElementById('clear-usage-btn')?.addEventListener('click', clearUsage);

    // Export dropdown
    const exportBtn = document.getElementById('export-btn');
    const exportMenu = document.getElementById('export-menu');
    if (exportBtn && exportMenu) {
        exportBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            exportMenu.classList.toggle('open');
        });
        document.addEventListener('click', () => exportMenu.classList.remove('open'));
        exportMenu.addEventListener('click', (e) => e.stopPropagation());
    }
    document.getElementById('export-csv-btn')?.addEventListener('click', exportUsageCsv);
    document.getElementById('export-json-btn')?.addEventListener('click', exportUsageJson);

    // Quick-period buttons
    document.querySelectorAll('.usage-quick-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            usageActivePeriod = btn.dataset.period;
            document.querySelectorAll('.usage-quick-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            applyUsagePeriodDates(usageActivePeriod);
            loadUsage();
        });
    });

    // Date input changes clear the quick-button highlight
    document.getElementById('usage-date-from')?.addEventListener('change', () => {
        usageActivePeriod = 'custom';
        document.querySelectorAll('.usage-quick-btn').forEach(b => b.classList.remove('active'));
    });
    document.getElementById('usage-date-to')?.addEventListener('change', () => {
        usageActivePeriod = 'custom';
        document.querySelectorAll('.usage-quick-btn').forEach(b => b.classList.remove('active'));
    });

    // Set initial date values and load
    applyUsagePeriodDates('7d');
}

function applyUsagePeriodDates(period) {
    const fromEl = document.getElementById('usage-date-from');
    const toEl = document.getElementById('usage-date-to');
    if (!fromEl || !toEl) return;

    const now = new Date();
    const toDate = formatDateInput(now);
    toEl.value = toDate;

    if (period === 'all') {
        fromEl.value = '';
        toEl.value = '';
    } else if (period === 'today') {
        fromEl.value = toDate;
    } else if (period === '7d') {
        fromEl.value = formatDateInput(new Date(now.getTime() - 7 * 86400000));
    } else if (period === '30d') {
        fromEl.value = formatDateInput(new Date(now.getTime() - 30 * 86400000));
    }
}

function formatDateInput(d) {
    return d.getFullYear() + '-' +
        String(d.getMonth() + 1).padStart(2, '0') + '-' +
        String(d.getDate()).padStart(2, '0');
}

async function loadUsage() {
    const params = getUsageDateParams();

    try {
        const [summaryRes, historyRes] = await Promise.all([
            fetch(`${API.usage}?${params}`),
            fetch(`${API.usageHistory}?limit=50&${params}`),
        ]);
        const summary = await summaryRes.json();
        const historyData = await historyRes.json();

        _lastUsageSummary = summary;
        _lastUsageHistory = historyData.entries || [];

        renderUsageCards(summary);
        renderUsageBreakdowns(summary);
        renderActivityHeatmaps(summary);
        renderUsageSessions(summary);
        renderUsageHistory(_lastUsageHistory);
    } catch (e) {
        document.getElementById('usage-cards').innerHTML =
            '<div class="empty-state">Failed to load usage data.</div>';
    }
}

function getUsageDateParams() {
    const fromVal = document.getElementById('usage-date-from')?.value;
    const toVal = document.getElementById('usage-date-to')?.value;

    const parts = [];
    if (fromVal) parts.push(`from=${fromVal}T00:00:00.000Z`);
    if (toVal) parts.push(`to=${toVal}T23:59:59.999Z`);
    return parts.join('&');
}

function renderUsageCards(s) {
    const container = document.getElementById('usage-cards');
    if (!s || s.totalRequests === 0) {
        container.innerHTML = '<div class="empty-state">No usage data yet. Send some messages to start tracking.</div>';
        document.getElementById('usage-breakdowns').innerHTML = '';
        document.getElementById('usage-history').innerHTML = '';
        return;
    }

    const avgDuration = s.totalRequests > 0 ? (s.totalDurationMs / s.totalRequests / 1000).toFixed(1) : '0';
    const avgTokens = s.totalRequests > 0 ? formatTokenCount(Math.round(s.totalTokens / s.totalRequests)) : '0';
    const errorRate = s.totalRequests > 0 ? ((s.failedRequests / s.totalRequests) * 100).toFixed(1) : '0.0';
    const totalDurSec = (s.totalDurationMs / 1000).toFixed(0);
    const uniqueModels = s.byModel?.length || 0;
    const uniqueChannels = s.byChannel?.length || 0;
    const uniqueTools = s.byTool?.length || 0;
    const topModel = s.byModel?.[0]?.model || '—';
    const topChannel = s.byChannel?.[0]?.channel || '—';

    container.innerHTML = `
        <div class="usage-overview-header">
            <h3>Usage Overview</h3>
            <span class="usage-overview-summary">${formatTokenCount(s.totalTokens)} tokens &middot; ${s.totalRequests} requests</span>
        </div>
        <div class="usage-card-grid">
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Requests</span>
                    <span class="usage-card-icon">📨</span>
                </div>
                <div class="usage-card-value">${s.totalRequests.toLocaleString()}</div>
                <div class="usage-card-sub">${s.successfulRequests} ok &middot; ${s.failedRequests} failed</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Total Tokens</span>
                    <span class="usage-card-icon">🔤</span>
                </div>
                <div class="usage-card-value">${formatTokenCount(s.totalTokens)}</div>
                <div class="usage-card-sub">${formatTokenCount(s.promptTokens)} prompt &middot; ${formatTokenCount(s.completionTokens)} completion</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Tool Calls</span>
                    <span class="usage-card-icon">🔧</span>
                </div>
                <div class="usage-card-value">${s.totalToolCalls.toLocaleString()}</div>
                <div class="usage-card-sub">${uniqueTools} unique tools</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Avg Tokens / Req</span>
                    <span class="usage-card-icon">📊</span>
                </div>
                <div class="usage-card-value">${avgTokens}</div>
                <div class="usage-card-sub">Across ${s.totalRequests} requests</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Avg Duration</span>
                    <span class="usage-card-icon">⏱️</span>
                </div>
                <div class="usage-card-value">${avgDuration}s</div>
                <div class="usage-card-sub">${totalDurSec}s total</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Error Rate</span>
                    <span class="usage-card-icon">⚠️</span>
                </div>
                <div class="usage-card-value ${parseFloat(errorRate) > 0 ? 'usage-card-value-error' : 'usage-card-value-success'}">${errorRate}%</div>
                <div class="usage-card-sub">${s.failedRequests} errors</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Models</span>
                    <span class="usage-card-icon">🤖</span>
                </div>
                <div class="usage-card-value">${uniqueModels}</div>
                <div class="usage-card-sub">${escapeHtml(topModel)}</div>
            </div>
            <div class="usage-card">
                <div class="usage-card-header">
                    <span class="usage-card-label">Channels</span>
                    <span class="usage-card-icon">📡</span>
                </div>
                <div class="usage-card-value">${uniqueChannels}</div>
                <div class="usage-card-sub">${escapeHtml(topChannel)}</div>
            </div>
        </div>
    `;
}

function renderUsageBreakdowns(s) {
    const container = document.getElementById('usage-breakdowns');
    if (!s || s.totalRequests === 0) { container.innerHTML = ''; return; }

    let html = '';

    // ── Daily Activity bar chart (full width) ──────────────────────────
    if (s.daily && s.daily.length > 0) {
        html += `
            <div class="usage-breakdown-card usage-breakdown-wide" id="daily-chart-card">
                <div class="usage-breakdown-card-header">
                    <h4>Daily Activity</h4>
                    <div class="daily-chart-controls">
                        <div class="daily-chart-toggle">
                            <button class="daily-toggle-btn active" data-metric="tokens">Tokens</button>
                            <button class="daily-toggle-btn" data-metric="requests">Requests</button>
                            <button class="daily-toggle-btn" data-metric="toolCalls">Tool Calls</button>
                        </div>
                        <span class="usage-breakdown-card-meta">${s.daily.length} days</span>
                    </div>
                </div>
                <div class="daily-chart-wrapper">
                    <div class="daily-chart-y-axis" id="daily-y-axis"></div>
                    <div class="daily-chart-body">
                        <div class="daily-chart-grid" id="daily-grid-lines"></div>
                        <div class="usage-bar-chart" id="daily-bar-container"></div>
                    </div>
                </div>
                <div class="daily-chart-tooltip" id="daily-tooltip"></div>
            </div>
        `;
    }

    // ── Top breakdowns grid ────────────────────────────────────────────
    html += '<div class="usage-breakdown-grid">';

    // Top Models
    html += `
        <div class="usage-breakdown-card">
            <div class="usage-breakdown-card-header">
                <h4>Top Models</h4>
            </div>
            <div class="usage-breakdown-list">
                ${s.byModel && s.byModel.length > 0 ? s.byModel.map(m => `
                    <div class="usage-breakdown-row">
                        <span class="usage-breakdown-name">${escapeHtml(m.model)}</span>
                        <div class="usage-breakdown-values">
                            <span class="usage-breakdown-primary">${formatTokenCount(m.tokens)}</span>
                            <span class="usage-breakdown-secondary">${m.requests} req</span>
                        </div>
                    </div>
                `).join('') : '<div class="usage-breakdown-empty">No model data</div>'}
            </div>
        </div>
    `;

    // Top Tools
    html += `
        <div class="usage-breakdown-card">
            <div class="usage-breakdown-card-header">
                <h4>Top Tools</h4>
            </div>
            <div class="usage-breakdown-list">
                ${s.byTool && s.byTool.length > 0 ? s.byTool.map(t => `
                    <div class="usage-breakdown-row">
                        <span class="usage-breakdown-name">${escapeHtml(t.tool)}</span>
                        <div class="usage-breakdown-values">
                            <span class="usage-breakdown-primary">${t.calls}</span>
                            <span class="usage-breakdown-secondary">calls${t.failures > 0 ? ` &middot; ${t.failures} failed` : ''}</span>
                        </div>
                    </div>
                `).join('') : '<div class="usage-breakdown-empty">No tool data</div>'}
            </div>
        </div>
    `;

    // Top Channels
    html += `
        <div class="usage-breakdown-card">
            <div class="usage-breakdown-card-header">
                <h4>Top Channels</h4>
            </div>
            <div class="usage-breakdown-list">
                ${s.byChannel && s.byChannel.length > 0 ? s.byChannel.map(c => `
                    <div class="usage-breakdown-row">
                        <span class="usage-breakdown-name">${escapeHtml(c.channel)}</span>
                        <div class="usage-breakdown-values">
                            <span class="usage-breakdown-primary">${formatTokenCount(c.tokens)}</span>
                            <span class="usage-breakdown-secondary">${c.requests} req</span>
                        </div>
                    </div>
                `).join('') : '<div class="usage-breakdown-empty">No channel data</div>'}
            </div>
        </div>
    `;

    html += '</div>';
    container.innerHTML = html;

    // ── Wire up the daily chart if it exists ────────────────────────
    if (s.daily && s.daily.length > 0) {
        window._dailyChartData = s.daily;
        renderDailyChart(s.daily, 'tokens');

        document.querySelectorAll('.daily-toggle-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                document.querySelectorAll('.daily-toggle-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                renderDailyChart(window._dailyChartData, btn.dataset.metric);
            });
        });
    }
}

// ── Daily Activity Chart renderer ──────────────────────────────────────
function renderDailyChart(daily, metric) {
    const barContainer = document.getElementById('daily-bar-container');
    const yAxis = document.getElementById('daily-y-axis');
    const gridLines = document.getElementById('daily-grid-lines');
    const tooltip = document.getElementById('daily-tooltip');
    if (!barContainer) return;

    const getValue = (d) => {
        if (metric === 'tokens') return d.tokens;
        if (metric === 'requests') return d.requests;
        return d.toolCalls;
    };

    const formatValue = (v) => {
        if (metric === 'tokens') return formatTokenCount(v);
        return v.toLocaleString();
    };

    const metricLabel = metric === 'tokens' ? 'tokens' : metric === 'requests' ? 'requests' : 'tool calls';
    const maxVal = Math.max(...daily.map(getValue), 1);

    // Build Y-axis ticks (5 ticks)
    const tickCount = 4;
    let yHtml = '';
    for (let i = tickCount; i >= 0; i--) {
        const val = Math.round((maxVal / tickCount) * i);
        const pct = (i / tickCount) * 100;
        yHtml += `<span class="daily-y-tick" style="bottom:${pct}%">${formatValue(val)}</span>`;
    }
    yAxis.innerHTML = yHtml;

    // Build grid lines
    let gridHtml = '';
    for (let i = 0; i <= tickCount; i++) {
        const pct = (i / tickCount) * 100;
        gridHtml += `<div class="daily-grid-line" style="bottom:${pct}%"></div>`;
    }
    gridLines.innerHTML = gridHtml;

    // Build bars
    barContainer.innerHTML = daily.map((d, idx) => {
        const val = getValue(d);
        const pct = Math.max(1, (val / maxVal) * 100);
        const label = d.date.substring(5); // MM-DD
        return `<div class="usage-bar-col" data-idx="${idx}">
            <div class="usage-bar" style="height:${pct}%"></div>
            <div class="usage-bar-label">${label}</div>
        </div>`;
    }).join('');

    // Tooltip handlers
    barContainer.querySelectorAll('.usage-bar-col').forEach(col => {
        col.addEventListener('mouseenter', (e) => {
            const idx = parseInt(col.dataset.idx);
            const d = daily[idx];
            tooltip.innerHTML = `
                <div class="daily-tooltip-date">${d.date}</div>
                <div class="daily-tooltip-row"><span>Tokens</span><span>${formatTokenCount(d.tokens)}</span></div>
                <div class="daily-tooltip-row"><span>Requests</span><span>${d.requests}</span></div>
                <div class="daily-tooltip-row"><span>Tool Calls</span><span>${d.toolCalls}</span></div>
            `;
            tooltip.classList.add('visible');

            // Position tooltip near the bar
            const colRect = col.getBoundingClientRect();
            const cardRect = document.getElementById('daily-chart-card').getBoundingClientRect();
            tooltip.style.left = `${colRect.left - cardRect.left + colRect.width / 2}px`;
            tooltip.style.top = `${colRect.top - cardRect.top - 8}px`;
        });

        col.addEventListener('mouseleave', () => {
            tooltip.classList.remove('visible');
        });
    });
}

// ── Activity Heatmaps ──────────────────────────────────────────────────
function renderActivityHeatmaps(s) {
    const container = document.getElementById('usage-heatmaps');
    if (!container) return;

    const hasDow = s.byDayOfWeek && s.byDayOfWeek.length > 0;
    const hasHour = s.byHour && s.byHour.length > 0;
    if (!hasDow && !hasHour) { container.innerHTML = ''; return; }

    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

    // Build full arrays (fill missing slots with 0)
    const dowData = new Array(7).fill(null).map((_, i) => {
        const found = s.byDayOfWeek?.find(d => d.day === i);
        return { day: i, label: dayNames[i], tokens: found?.tokens || 0, requests: found?.requests || 0 };
    });

    const hourData = new Array(24).fill(null).map((_, i) => {
        const found = s.byHour?.find(h => h.hour === i);
        return { hour: i, label: `${i.toString().padStart(2, '0')}:00`, tokens: found?.tokens || 0, requests: found?.requests || 0 };
    });

    const maxDowTokens = Math.max(...dowData.map(d => d.tokens), 1);
    const maxHourTokens = Math.max(...hourData.map(h => h.tokens), 1);

    // Color intensity helper (returns 0..1)
    const intensity = (val, max) => max > 0 ? val / max : 0;

    let html = `<div class="heatmap-section-header">
        <h4>Activity by Time</h4>
    </div>
    <div class="heatmap-grid">`;

    // Day of Week heatmap
    html += `
        <div class="heatmap-card">
            <div class="heatmap-card-title">Day of Week</div>
            <div class="heatmap-row-list">
                ${dowData.map(d => {
                    const pct = intensity(d.tokens, maxDowTokens);
                    return `<div class="heatmap-row" title="${d.label}: ${formatTokenCount(d.tokens)} tokens, ${d.requests} requests">
                        <span class="heatmap-label">${d.label}</span>
                        <div class="heatmap-cell" style="--intensity:${pct}"></div>
                        <span class="heatmap-value">${formatTokenCount(d.tokens)}</span>
                    </div>`;
                }).join('')}
            </div>
        </div>
    `;

    // Hours heatmap (horizontal grid)
    html += `
        <div class="heatmap-card heatmap-card-wide">
            <div class="heatmap-card-title">Hours (UTC)</div>
            <div class="heatmap-hour-grid">
                ${hourData.map(h => {
                    const pct = intensity(h.tokens, maxHourTokens);
                    return `<div class="heatmap-hour-cell" title="${h.label}: ${formatTokenCount(h.tokens)} tokens, ${h.requests} requests" style="--intensity:${pct}">
                        <span class="heatmap-hour-val">${h.hour}</span>
                    </div>`;
                }).join('')}
            </div>
            <div class="heatmap-hour-legend">
                <span>12am</span><span>6am</span><span>12pm</span><span>6pm</span><span>11pm</span>
            </div>
        </div>
    `;

    html += '</div>';
    container.innerHTML = html;
}

// ── Sessions Panel ─────────────────────────────────────────────────────
let _sessionsData = [];
let _sessionsSort = 'recent'; // recent | tokens | messages | errors

function renderUsageSessions(s) {
    const container = document.getElementById('usage-sessions');
    if (!container) return;

    if (!s.sessions || s.sessions.length === 0) {
        container.innerHTML = '';
        return;
    }

    _sessionsData = s.sessions;
    _sessionsSort = 'recent';
    renderSessionsPanel();
}

function renderSessionsPanel() {
    const container = document.getElementById('usage-sessions');
    if (!container || !_sessionsData.length) return;

    const sorted = [..._sessionsData];
    switch (_sessionsSort) {
        case 'tokens':   sorted.sort((a, b) => b.tokens - a.tokens); break;
        case 'messages':  sorted.sort((a, b) => b.messages - a.messages); break;
        case 'errors':    sorted.sort((a, b) => b.errors - a.errors); break;
        case 'recent':
        default:          sorted.sort((a, b) => new Date(b.lastSeen) - new Date(a.lastSeen)); break;
    }

    const sortBtn = (value, label) =>
        `<button class="sessions-sort-btn ${_sessionsSort === value ? 'active' : ''}" data-sort="${value}">${label}</button>`;

    container.innerHTML = `
        <div class="sessions-panel">
            <div class="sessions-header">
                <h4>Sessions</h4>
                <div class="sessions-sort-group">
                    ${sortBtn('recent', 'Recent')}
                    ${sortBtn('tokens', 'Tokens')}
                    ${sortBtn('messages', 'Messages')}
                    ${sortBtn('errors', 'Errors')}
                </div>
                <span class="sessions-count">${_sessionsData.length} sessions</span>
            </div>
            <div class="sessions-list">
                ${sorted.map(sess => {
                    const duration = sess.durationMs < 1000
                        ? `${Math.round(sess.durationMs)}ms`
                        : sess.durationMs < 60000
                            ? `${(sess.durationMs / 1000).toFixed(1)}s`
                            : `${Math.round(sess.durationMs / 60000)}m`;
                    const timeAgo = formatTimeAgo(new Date(sess.lastSeen));
                    const shortKey = sess.sessionKey.length > 16
                        ? sess.sessionKey.substring(0, 16) + '…'
                        : sess.sessionKey;

                    return `<div class="session-card">
                        <div class="session-card-top">
                            <span class="session-key" title="${escapeHtml(sess.sessionKey)}">${escapeHtml(shortKey)}</span>
                            <span class="session-time">${timeAgo}</span>
                        </div>
                        <div class="session-card-meta">
                            <span class="session-channel">${escapeHtml(sess.channel)}</span>
                            <span class="session-model">${escapeHtml(sess.model)}</span>
                        </div>
                        <div class="session-card-stats">
                            <span title="Messages">${sess.messages} msg</span>
                            <span title="Tokens">${formatTokenCount(sess.tokens)}</span>
                            <span title="Tool calls">${sess.toolCalls} tools</span>
                            <span title="Duration">${duration}</span>
                            ${sess.errors > 0 ? `<span class="session-errors" title="Errors">${sess.errors} err</span>` : ''}
                        </div>
                    </div>`;
                }).join('')}
            </div>
        </div>
    `;

    // Wire sort buttons
    container.querySelectorAll('.sessions-sort-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            _sessionsSort = btn.dataset.sort;
            renderSessionsPanel();
        });
    });
}

function formatTimeAgo(date) {
    const now = new Date();
    const diffMs = now - date;
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 7) return `${diffDays}d ago`;
    return date.toLocaleDateString();
}

function renderUsageHistory(entries) {
    const container = document.getElementById('usage-history');
    if (!entries || entries.length === 0) {
        container.innerHTML = '';
        return;
    }

    let html = `
        <h4 class="usage-history-title">Recent Requests</h4>
        <div class="usage-table-wrap">
            <table class="data-table">
                <thead>
                    <tr>
                        <th>Time</th>
                        <th>Channel</th>
                        <th>Model</th>
                        <th>Tokens</th>
                        <th>Tools</th>
                        <th>Duration</th>
                        <th>Status</th>
                    </tr>
                </thead>
                <tbody>
    `;

    for (const e of entries) {
        const time = new Date(e.timestamp).toLocaleString();
        const status = e.success
            ? '<span class="usage-badge usage-badge-ok">OK</span>'
            : `<span class="usage-badge usage-badge-fail" title="${escapeHtml(e.error || '')}">Failed</span>`;
        const duration = e.totalDurationMs < 1000
            ? `${Math.round(e.totalDurationMs)}ms`
            : `${(e.totalDurationMs / 1000).toFixed(1)}s`;
        const toolsList = e.toolsUsed?.length > 0 ? e.toolsUsed.join(', ') : '-';

        html += `
            <tr>
                <td class="cell-nowrap">${time}</td>
                <td>${escapeHtml(e.channel)}</td>
                <td class="cell-mono">${escapeHtml(e.model)}</td>
                <td>${e.totalTokens.toLocaleString()}</td>
                <td title="${escapeHtml(toolsList)}">${e.toolCalls}</td>
                <td>${duration}</td>
                <td>${status}</td>
            </tr>
        `;
    }

    html += '</tbody></table></div>';
    container.innerHTML = html;
}

function formatTokenCount(n) {
    if (n >= 1000000) return (n / 1000000).toFixed(1) + 'M';
    if (n >= 1000) return (n / 1000).toFixed(1) + 'K';
    return n.toLocaleString();
}

// ── Export functions ───────────────────────────────────────────────────
function exportUsageCsv() {
    document.getElementById('export-menu')?.classList.remove('open');

    if (!_lastUsageHistory || _lastUsageHistory.length === 0) {
        showToast('No usage data to export.');
        return;
    }

    const headers = ['Timestamp', 'Channel', 'Session', 'Model', 'Status', 'Error',
        'Prompt Tokens', 'Completion Tokens', 'Total Tokens',
        'Tool Calls', 'Failed Tool Calls', 'Duration (ms)', 'Tools Used'];

    const csvEscape = (val) => {
        const s = String(val ?? '');
        return s.includes(',') || s.includes('"') || s.includes('\n')
            ? `"${s.replace(/"/g, '""')}"` : s;
    };

    const rows = _lastUsageHistory.map(e => [
        e.timestamp,
        e.channel,
        e.sessionKey,
        e.model,
        e.success ? 'OK' : 'Failed',
        e.error || '',
        e.promptTokens,
        e.completionTokens,
        e.totalTokens,
        e.toolCalls,
        e.failedToolCalls,
        Math.round(e.totalDurationMs),
        (e.toolsUsed || []).join('; '),
    ].map(csvEscape));

    const csv = [headers.join(','), ...rows.map(r => r.join(','))].join('\n');
    downloadFile(csv, 'usage-export.csv', 'text/csv');
    showToast('CSV exported successfully.');
}

function exportUsageJson() {
    document.getElementById('export-menu')?.classList.remove('open');

    if (!_lastUsageSummary && (!_lastUsageHistory || _lastUsageHistory.length === 0)) {
        showToast('No usage data to export.');
        return;
    }

    const data = {
        exportedAt: new Date().toISOString(),
        summary: _lastUsageSummary,
        entries: _lastUsageHistory,
    };

    const json = JSON.stringify(data, null, 2);
    downloadFile(json, 'usage-export.json', 'application/json');
    showToast('JSON exported successfully.');
}

function downloadFile(content, filename, mimeType) {
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}

async function clearUsage() {
    if (!confirm('Clear all usage tracking data?')) return;
    try {
        await fetch(API.usage, { method: 'DELETE' });
        showToast('Usage data cleared');
        loadUsage();
    } catch (e) {
        showToast('Failed to clear usage data');
    }
}

// ============================================================================
// Theme
// ============================================================================
function initTheme() {
    const saved = localStorage.getItem('sharpbot-theme') || 'dark';
    document.documentElement.setAttribute('data-theme', saved);

    document.getElementById('theme-toggle').addEventListener('click', () => {
        const current = document.documentElement.getAttribute('data-theme');
        const next = current === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', next);
        localStorage.setItem('sharpbot-theme', next);
    });
}

// ============================================================================
// Mobile Sidebar
// ============================================================================
function initSidebar() {
    const toggle = document.getElementById('sidebar-toggle');
    const sidebar = document.getElementById('sidebar');

    toggle.addEventListener('click', () => {
        sidebar.classList.toggle('open');
    });

    // Close sidebar on content click (mobile)
    document.querySelector('.content').addEventListener('click', () => {
        sidebar.classList.remove('open');
    });
}

// ============================================================================
// Utilities
// ============================================================================
function escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

function showToast(message) {
    const existing = document.querySelector('.toast');
    if (existing) existing.remove();

    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;
    document.body.appendChild(toast);

    setTimeout(() => toast.remove(), 3000);
}
