(() => {
    if (location.pathname.toLowerCase() !== '/applications') return;

    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

    async function getJson(url) {
        const response = await fetch(url, { cache: 'no-store' });
        const body = await response.json();
        if (!response.ok) throw new Error(body?.error || `HTTP ${response.status}`);
        return body;
    }

    async function decorateCards() {
        const list = document.getElementById('appList');
        if (!list || !list.children.length) return;
        let apps;
        try { apps = await getJson('/api/apps'); } catch { return; }
        const cards = [...list.children];
        for (let i = 0; i < Math.min(cards.length, apps.length); i++) {
            const card = cards[i];
            if (card.dataset.runtimeDecorated === apps[i].id) continue;
            try {
                const runtime = await getJson(`/api/apps/${encodeURIComponent(apps[i].id)}/runtime`);
                const badge = runtime.healthy
                    ? '<span class="rounded-full bg-emerald-500/10 px-2 py-1 text-[11px] text-emerald-300">● Healthy</span>'
                    : '<span class="rounded-full bg-rose-500/10 px-2 py-1 text-[11px] text-rose-300">● Unhealthy</span>';
                const publicUrl = runtime.publicUrl
                    ? `<div class="mt-2 truncate font-mono text-xs text-cyan-300">${esc(runtime.publicUrl)}</div>`
                    : '<div class="mt-2 text-xs text-slate-600">No public tunnel</div>';
                const extra = document.createElement('div');
                extra.className = 'mt-3 border-t border-slate-800 pt-3';
                extra.innerHTML = `<div class="flex items-center justify-between gap-2">${badge}<span class="text-[11px] text-slate-500">${esc(runtime.health)}</span></div>${publicUrl}`;
                card.appendChild(extra);
                card.dataset.runtimeDecorated = apps[i].id;
            } catch { }
        }
    }

    function ensureRuntimePanel() {
        const editor = document.getElementById('editor');
        if (!editor || document.getElementById('runtimePanel')) return;
        const panel = document.createElement('section');
        panel.id = 'runtimePanel';
        panel.className = 'mt-5 rounded-xl border border-slate-800 bg-slate-950/70 p-4';
        panel.innerHTML = `
            <div class="flex flex-wrap items-center justify-between gap-3">
                <div><h3 class="font-medium">Runtime &amp; logs</h3><p id="runtimeSummary" class="mt-1 text-xs text-slate-500">Select an application.</p></div>
                <button id="refreshRuntime" type="button" class="rounded-lg border border-slate-700 px-3 py-2 text-xs hover:bg-slate-800">Refresh</button>
            </div>
            <div id="runtimeLinks" class="mt-3 flex flex-wrap gap-2 text-xs"></div>
            <pre id="runtimeLogs" class="mt-4 max-h-72 overflow-auto whitespace-pre-wrap break-words rounded-lg border border-slate-800 bg-black p-3 font-mono text-xs leading-5 text-emerald-300">No logs loaded.</pre>`;
        editor.appendChild(panel);
        document.getElementById('refreshRuntime').addEventListener('click', refreshSelected);
    }

    async function refreshSelected() {
        const id = document.getElementById('appId')?.value?.trim();
        if (!id || document.getElementById('editor')?.classList.contains('hidden')) return;
        const summary = document.getElementById('runtimeSummary');
        const links = document.getElementById('runtimeLinks');
        const logs = document.getElementById('runtimeLogs');
        try {
            const runtime = await getJson(`/api/apps/${encodeURIComponent(id)}/runtime`);
            summary.textContent = `${runtime.healthy ? 'Healthy' : 'Unhealthy'} · ${runtime.health} · port ${runtime.port}`;
            links.innerHTML = `<a target="_blank" href="http://${location.hostname}:${runtime.port}" class="rounded-lg border border-slate-700 px-3 py-2 text-emerald-300">Open LAN</a>${runtime.publicUrl ? `<a target="_blank" href="${esc(runtime.publicUrl)}" class="rounded-lg border border-cyan-700/50 px-3 py-2 text-cyan-300">Open public</a>` : '<span class="rounded-lg border border-slate-800 px-3 py-2 text-slate-500">No ngrok tunnel</span>'}`;
            const response = await fetch(`/api/apps/${encodeURIComponent(id)}/logs?lines=200`, { cache: 'no-store' });
            logs.textContent = await response.text();
            logs.scrollTop = logs.scrollHeight;
        } catch (error) {
            summary.textContent = error.message || String(error);
        }
    }

    async function loop() {
        ensureRuntimePanel();
        let lastId = '';
        while (true) {
            await decorateCards();
            const id = document.getElementById('appId')?.value?.trim() || '';
            const visible = !document.getElementById('editor')?.classList.contains('hidden');
            if (visible && id && id !== lastId) { lastId = id; await refreshSelected(); }
            await sleep(3000);
        }
    }

    loop();
})();
