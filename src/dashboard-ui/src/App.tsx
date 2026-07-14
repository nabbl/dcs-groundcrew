import { useEffect, useMemo, useState } from 'react'
import {
  Activity, Ban, ChevronRight, CircleGauge, Clock3, Cpu, ExternalLink,
  FileArchive, FolderCog, Gauge, HardDrive, Headphones, Home, LayoutDashboard,
  MemoryStick, MessageSquareText, MoreHorizontal, Network, PanelLeftClose,
  Play, PlugZap, Power, Radio, RefreshCw, RotateCcw, Send, Server, Settings,
  ShieldAlert, Square, Users, X,
} from 'lucide-react'
import { browseServer, getSettings, getSnapshot, saveSettings, sendChatMessage, serverAction, subscribeToSnapshots, switchMission } from './api'
import { mockSnapshot } from './mockData'
import type { DashboardSettings, DashboardSnapshot, FileBrowserResult, Integration, Player, ServerState } from './types'

type Page = 'overview' | 'missions' | 'players' | 'integrations' | 'chat' | 'settings'

const nav: { id: Page; label: string; icon: typeof Home }[] = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard },
  { id: 'missions', label: 'Missions', icon: FileArchive },
  { id: 'players', label: 'Players', icon: Users },
  { id: 'integrations', label: 'Integrations', icon: PlugZap },
  { id: 'chat', label: 'Server chat', icon: MessageSquareText },
  { id: 'settings', label: 'Settings', icon: Settings },
]

const missionOptions = [
  { name: 'Operation Enduring Resolve v4.2', theatre: 'Syria', size: '48.2 MB', modified: 'Today, 11:42', active: true },
  { name: 'Flashpoint Levant — Dawn', theatre: 'Syria', size: '36.7 MB', modified: '12 Jul 2026', active: false },
  { name: 'Caucasus Training Range', theatre: 'Caucasus', size: '12.4 MB', modified: '08 Jul 2026', active: false },
  { name: 'Marianas Carrier Ops', theatre: 'Marianas', size: '28.9 MB', modified: '02 Jul 2026', active: false },
]

function duration(seconds: number) {
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return `${h}h ${m}m`
}

function StateDot({ state }: { state: ServerState | boolean }) {
  const on = state === true || state === 'running'
  return <span className={`state-dot ${on ? 'online' : ''}`} />
}

function MetricCard({ icon: Icon, label, value, detail, progress }: { icon: typeof Cpu; label: string; value: string; detail: string; progress: number }) {
  return (
    <article className="metric-card">
      <div className="metric-icon"><Icon size={19} /></div>
      <div className="metric-copy"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>
      <div className="meter"><i style={{ width: `${Math.min(progress, 100)}%` }} /></div>
    </article>
  )
}

function PlayerRow({ player, compact = false }: { player: Player; compact?: boolean }) {
  return (
    <div className={`player-row ${compact ? 'compact' : ''}`}>
      <span className={`coalition ${player.side.toLowerCase()}`}>{player.side.slice(0, 1)}</span>
      <div className="player-name"><strong>{player.name}</strong><small>{player.slot}</small></div>
      <span className="ping"><Network size={13} /> {player.ping} ms</span>
      {!compact && <button className="icon-button" aria-label={`Moderate ${player.name}`}><MoreHorizontal size={18} /></button>}
    </div>
  )
}

function IntegrationRow({ item, expanded, onExpand, onOpen }: { item: Integration; expanded: boolean; onExpand: () => void; onOpen: () => void }) {
  return (
    <article className={`integration-row ${expanded ? 'expanded' : ''}`}>
      <button className="integration-summary" onClick={onExpand}>
        <span className="integration-mark"><Radio size={19} /></span>
        <span className="integration-copy"><strong>{item.name}</strong><small>{item.description}</small></span>
        <span className={`status-label ${item.running ? 'good' : item.installed ? 'idle' : 'missing'}`}>
          <StateDot state={item.running} />{item.running ? 'Running' : item.installed ? 'Stopped' : 'Not installed'}
        </span>
        <ChevronRight className="row-chevron" size={19} />
      </button>
      {expanded && (
        <div className="integration-detail">
          <div><span>Version</span><strong>{item.version ?? '—'}</strong></div>
          <div><span>Web interface</span><strong>{item.url ?? 'Not configured'}</strong></div>
          <div className="integration-actions">
            {item.installed ? <button className="button outline"><RefreshCw size={15} /> Restart</button> : <button className="button outline"><HardDrive size={15} /> Configure install</button>}
            <button className="button ghost"><Settings size={15} /> Configuration</button>
            {item.url && <button className="button ghost" onClick={onOpen}><ExternalLink size={15} /> Open tool</button>}
          </div>
        </div>
      )}
    </article>
  )
}

function Overview({ data, onNavigate }: { data: DashboardSnapshot; onNavigate: (page: Page) => void }) {
  const cpu = data.metrics[0]
  const memory = data.metrics[1]
  const process = data.metrics[2]
  const disk = data.metrics[3]
  return (
    <div className="page-stack">
      <section className="mission-hero">
        <div className="mission-grid" />
        <div className="hero-copy">
          <span className="eyebrow">ACTIVE MISSION · {data.server.theatre.toUpperCase()}</span>
          <h1>{data.server.mission}</h1>
          <p>{data.server.state === 'running' ? 'Live server telemetry and mission services are operating normally.' : 'The instance is offline. Configure the host, then start the dedicated server.'}</p>
          <div className="hero-stats">
            <span><Users size={16} /><strong>{data.server.players}</strong> / {data.server.maxPlayers} pilots</span>
            <span><Gauge size={16} /><strong>{data.server.fps}</strong> FPS</span>
            <span><Clock3 size={16} /><strong>{duration(data.server.uptimeSeconds)}</strong> uptime</span>
          </div>
        </div>
        <div className={`server-health ${data.server.state !== 'running' ? 'offline' : ''}`}><Activity size={17} /> {data.server.state === 'running' ? 'All systems nominal' : 'Instance offline'}</div>
      </section>

      <section className="metrics-grid">
        <MetricCard icon={Cpu} label="HOST CPU" value={`${cpu.value}${cpu.unit}`} detail="8 logical cores" progress={cpu.value} />
        <MetricCard icon={MemoryStick} label="SYSTEM MEMORY" value={`${memory.value} ${memory.unit}`} detail={`${memory.max} GB installed`} progress={(memory.value / memory.max) * 100} />
        <MetricCard icon={CircleGauge} label="DCS PROCESS" value={`${process.value} ${process.unit}`} detail="Working set" progress={(process.value / process.max) * 100} />
        <MetricCard icon={HardDrive} label="STORAGE" value={`${disk.value} GB`} detail="Free on mission drive" progress={100 - (disk.value / disk.max) * 100} />
      </section>

      <div className="content-grid">
        <section className="panel">
          <header className="panel-header"><div><span className="eyebrow">CONNECTED</span><h2>Players</h2></div><button className="text-button" onClick={() => onNavigate('players')}>View all <ChevronRight size={15} /></button></header>
          <div className="rows">{data.players.slice(0, 4).map(p => <PlayerRow key={p.id} player={p} compact />)}</div>
        </section>
        <section className="panel">
          <header className="panel-header"><div><span className="eyebrow">SERVICES</span><h2>Integrations</h2></div><button className="text-button" onClick={() => onNavigate('integrations')}>Manage <ChevronRight size={15} /></button></header>
          <div className="service-grid">
            {data.integrations.map(item => <div className="service-tile" key={item.id}><StateDot state={item.running} /><span>{item.name.replace('SimpleRadio Standalone', 'SRS')}</span><small>{item.running ? 'Online' : item.installed ? 'Stopped' : 'Setup required'}</small></div>)}
          </div>
        </section>
      </div>
    </div>
  )
}

function Missions({ onSwitch }: { onSwitch: (mission: string) => void }) {
  const [browserOpen, setBrowserOpen] = useState(false)
  return <><section className="panel page-panel">
    <header className="panel-header large"><div><span className="eyebrow">SAVED GAMES / MISSIONS</span><h1>Mission library</h1><p>Select a .miz file to load, then confirm the server restart.</p></div><button className="button outline" onClick={() => setBrowserOpen(true)}><FolderCog size={16} /> Browse server</button></header>
    <div className="table-head"><span>Mission name</span><span>Theatre</span><span>Modified</span><span>Size</span><span /></div>
    <div className="mission-list">
      {missionOptions.map(m => <div className={`mission-row ${m.active ? 'active' : ''}`} key={m.name}>
        <span className="file-mark"><FileArchive size={19} /></span><div><strong>{m.name}</strong><small>{m.active ? 'Currently running' : `${m.name}.miz`}</small></div><span>{m.theatre}</span><span>{m.modified}</span><span>{m.size}</span><button className={m.active ? 'button ghost' : 'button outline'} disabled={m.active} onClick={() => onSwitch(m.name)}>{m.active ? 'Active' : 'Load mission'}</button>
      </div>)}
    </div>
  </section>{browserOpen && <FileBrowserDialog title="Select mission file" initialPath="D:\\DCS\\Missions" extension=".miz" onSelect={path => { setBrowserOpen(false); onSwitch(path) }} onClose={() => setBrowserOpen(false)} />}</>
}

function Players({ players }: { players: Player[] }) {
  return <section className="panel page-panel"><header className="panel-header large"><div><span className="eyebrow">LIVE ROSTER</span><h1>Connected players</h1><p>{players.length} players currently connected. Moderation actions are logged.</p></div></header><div className="rows roomy">{players.map(p => <PlayerRow key={p.id} player={p} />)}</div><div className="moderation-note"><ShieldAlert size={18} /><div><strong>Moderation controls</strong><span>Open a player’s action menu to kick, ban, mute, or move them to spectators.</span></div></div></section>
}

function Integrations({ integrations }: { integrations: Integration[] }) {
  const [expanded, setExpanded] = useState('srs')
  const [tool, setTool] = useState<Integration | null>(null)
  return <><section className="panel page-panel"><header className="panel-header large"><div><span className="eyebrow">COMPANION SERVICES</span><h1>Integrations</h1><p>Install, configure, control, and open tools connected to this DCS instance.</p></div><button className="button outline"><RefreshCw size={16} /> Scan host</button></header><div className="integration-list">{integrations.map(item => <IntegrationRow key={item.id} item={item} expanded={expanded === item.id} onExpand={() => setExpanded(expanded === item.id ? '' : item.id)} onOpen={() => setTool(item)} />)}</div></section>{tool?.url && <ToolFrame tool={tool} onClose={() => setTool(null)} />}</>
}

function ToolFrame({ tool, onClose }: { tool: Integration; onClose: () => void }) {
  return <div className="tool-frame"><header><div><StateDot state={tool.running} /><strong>{tool.name}</strong><span>{tool.url}</span></div><div><a className="button ghost" href={tool.url} target="_blank" rel="noreferrer"><ExternalLink size={15} /> New tab</a><button className="icon-button" onClick={onClose} aria-label="Close embedded tool"><X size={20} /></button></div></header><iframe src={tool.url} title={tool.name} /><footer>If the tool refuses to load here, use “New tab”. Some integrations block iframe embedding.</footer></div>
}

function Chat({ data }: { data: DashboardSnapshot }) {
  const [message, setMessage] = useState('')
  const [messages, setMessages] = useState(data.chat)
  const [error, setError] = useState('')
  const send = async () => {
    const text = message.trim()
    if (!text) return
    if (!data.demoMode) {
      const result = await sendChatMessage(text)
      if (!result.ok) { setError(result.error ?? 'Message failed.'); return }
    }
    setMessages([...messages, { id: crypto.randomUUID(), author: 'ADMIN', message: text, timestamp: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) }])
    setError(''); setMessage('')
  }
  return <section className="panel page-panel chat-panel"><header className="panel-header large"><div><span className="eyebrow">LIVE CHANNEL</span><h1>Server chat</h1><p>Messages are sent to all connected players.</p></div><span className={`status-label ${data.demoMode ? 'idle' : 'good'}`}><StateDot state={!data.demoMode} /> {data.demoMode ? 'Preview' : 'Connected'}</span></header><div className="chat-log">{messages.map(m => <div className={`chat-message ${m.system ? 'system' : ''}`} key={m.id}><span>{m.timestamp}</span><strong>{m.author}</strong><p>{m.message}</p></div>)}</div>{error && <div className="chat-error"><ShieldAlert size={15} />{error}</div>}<div className="chat-compose"><input value={message} onChange={e => setMessage(e.target.value)} onKeyDown={e => e.key === 'Enter' && void send()} placeholder="Message all players…" /><button className="button primary" onClick={() => void send()}><Send size={16} /> Send</button></div></section>
}

function SettingsPage() {
  const [paths, setPaths] = useState<Record<string, string>>({
    'DCS executable': 'C:\\Program Files\\Eagle Dynamics\\DCS World Server\\bin-mt\\DCS.exe',
    'Saved Games': 'C:\\Users\\dcs-server\\Saved Games\\DCS.openbeta_server',
    'Mission library': 'D:\\DCS\\Missions',
    'Tacview recordings': 'D:\\DCS\\Tacview',
  })
  const [browsing, setBrowsing] = useState<string | null>(null)
  const [settings, setSettings] = useState<DashboardSettings | null>(null)
  const [saved, setSaved] = useState<'idle' | 'ok' | 'error'>('idle')
  useEffect(() => { void getSettings().then(value => { if (!value) return; setSettings(value); setPaths({ 'DCS executable': value.dcsExecutablePath, 'Saved Games': value.savedGamesPath, 'Mission library': value.missionLibraryPath, 'Tacview recordings': value.tacviewRecordingsPath }) }) }, [])
  const save = async () => {
    if (!settings) { setSaved('error'); return }
    const next = { ...settings, dcsExecutablePath: paths['DCS executable'], savedGamesPath: paths['Saved Games'], missionLibraryPath: paths['Mission library'], tacviewRecordingsPath: paths['Tacview recordings'] }
    setSaved(await saveSettings(next) ? 'ok' : 'error')
  }
  return <><section className="panel page-panel"><header className="panel-header large"><div><span className="eyebrow">HOST CONFIGURATION</span><h1>Settings</h1><p>Paths are selected on the Windows host and never expose unrestricted filesystem access.</p></div><div className="save-area">{saved === 'ok' && <span>Saved</span>}{saved === 'error' && <span className="error">Backend unavailable</span>}<button className="button primary" onClick={() => void save()}>Save changes</button></div></header><div className="settings-section"><h3>Server paths</h3>{Object.entries(paths).map(([label, path]) => <div className="path-field" key={label}><span>{label}</span><div><input value={path} onChange={event => setPaths({ ...paths, [label]: event.target.value })} /><button type="button" className="button outline" onClick={() => setBrowsing(label)}><FolderCog size={16} /> Browse</button></div></div>)}</div><div className="settings-section"><h3>Network</h3><div className="path-field"><span>Dashboard bind address</span><div><input defaultValue="100.x.x.x:5080" /><small>Configured through ASPNETCORE_URLS; Tailscale address recommended</small></div></div></div></section>{browsing && <FileBrowserDialog title={`Select ${browsing}`} initialPath={browsing === 'DCS executable' ? paths[browsing].replace(/\\[^\\]+$/, '') : paths[browsing]} allowDirectory={browsing !== 'DCS executable'} extension={browsing === 'DCS executable' ? '.exe' : undefined} onSelect={path => { setPaths({ ...paths, [browsing]: path }); setBrowsing(null) }} onClose={() => setBrowsing(null)} />}</>
}

function FileBrowserDialog({ title, initialPath, extension, allowDirectory = false, onSelect, onClose }: { title: string; initialPath?: string; extension?: string; allowDirectory?: boolean; onSelect: (path: string) => void; onClose: () => void }) {
  const [result, setResult] = useState<FileBrowserResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const load = async (path?: string) => {
    setLoading(true); setError('')
    try { setResult(await browseServer(path)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to browse this location.') }
    finally { setLoading(false) }
  }
  useEffect(() => { void load(initialPath) }, [initialPath])
  const entries = result?.entries.filter(entry => entry.isDirectory || !extension || entry.name.toLowerCase().endsWith(extension.toLowerCase())) ?? []
  return <div className="dialog-backdrop" role="presentation" onMouseDown={onClose}><div className="file-browser" role="dialog" aria-modal="true" onMouseDown={event => event.stopPropagation()}><header><div><span className="eyebrow">WINDOWS HOST</span><h2>{title}</h2></div><button className="icon-button" onClick={onClose} aria-label="Close file browser"><X size={19} /></button></header><div className="browser-path"><button className="icon-button" disabled={!result?.parentPath} onClick={() => result?.parentPath && void load(result.parentPath)}>‹</button><HardDrive size={15} /><span>{result?.currentPath ?? initialPath ?? 'This PC'}</span></div><div className="browser-list">{loading && <div className="browser-empty"><RefreshCw className="spin" size={18} /> Reading server files…</div>}{error && <div className="browser-empty error"><ShieldAlert size={18} />{error}<button className="button outline" onClick={() => void load()}>Show drives</button></div>}{!loading && !error && entries.map(entry => <div className="browser-entry" key={entry.fullPath}><button onClick={() => entry.isDirectory ? void load(entry.fullPath) : onSelect(entry.fullPath)}>{entry.isDirectory ? <FolderCog size={17} /> : <FileArchive size={17} />}<span><strong>{entry.name}</strong><small>{entry.isDirectory ? 'Folder' : `${Math.round((entry.size ?? 0) / 104857.6) / 10} MB`}</small></span></button>{!entry.isDirectory && <button className="button outline" onClick={() => onSelect(entry.fullPath)}>Select</button>}</div>)}{!loading && !error && entries.length === 0 && <div className="browser-empty">No matching files in this location.</div>}</div><footer><span>{extension ? `Showing ${extension} files` : 'Server-side filesystem'}</span>{allowDirectory && result && result.currentPath !== 'This PC' && <button className="button primary" onClick={() => onSelect(result.currentPath)}>Select this folder</button>}</footer></div></div>
}

function ConfirmDialog({ title, body, action, danger, onConfirm, onClose }: { title: string; body: string; action: string; danger?: boolean; onConfirm: () => void; onClose: () => void }) {
  return <div className="dialog-backdrop" role="presentation" onMouseDown={onClose}><div className="dialog" role="dialog" aria-modal="true" onMouseDown={e => e.stopPropagation()}><button className="dialog-close" onClick={onClose}><X size={18} /></button><span className={`dialog-icon ${danger ? 'danger' : ''}`}>{danger ? <ShieldAlert /> : <RotateCcw />}</span><h2>{title}</h2><p>{body}</p><div className="dialog-actions"><button className="button ghost" onClick={onClose}>Cancel</button><button className={`button ${danger ? 'danger' : 'primary'}`} onClick={onConfirm}>{action}</button></div></div></div>
}

export default function App() {
  const [page, setPage] = useState<Page>('overview')
  const [data, setData] = useState<DashboardSnapshot>(mockSnapshot)
  const [pending, setPending] = useState<{ kind: 'start' | 'stop' | 'restart' | 'mission'; value?: string } | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    getSnapshot().then(setData)
    const unsubscribe = subscribeToSnapshots(setData)
    const polling = window.setInterval(() => { void getSnapshot().then(setData) }, 5000)
    return () => { unsubscribe(); window.clearInterval(polling) }
  }, [])
  const title = useMemo(() => nav.find(item => item.id === page)?.label ?? 'Overview', [page])
  const srs = data.integrations.find(item => item.id === 'srs')
  const olympus = data.integrations.find(item => item.id === 'olympus')
  const act = async () => {
    if (!pending) return
    setBusy(true)
    const action = pending.kind === 'mission' ? 'restart' : pending.kind
    const missionResult = pending.kind === 'mission' && pending.value && !data.demoMode ? await switchMission(pending.value) : null
    if (pending.kind !== 'mission' || data.demoMode) await serverAction(action)
    const missionName = pending.value?.split(/[\\/]/).pop()?.replace(/\.miz$/i, '')
    if (!missionResult || missionResult.ok) setData(current => ({ ...current, server: { ...current.server, state: action === 'stop' ? 'stopped' : 'running', mission: missionName ?? current.server.mission } }))
    setBusy(false); setPending(null)
  }

  return (
    <div className="app-shell">
      <aside className="nav-rail">
        <div className="brand-mark"><span>G</span><i /></div>
        <nav>{nav.map(({ id, label, icon: Icon }) => <button key={id} className={page === id ? 'active' : ''} onClick={() => setPage(id)} aria-label={label} data-label={label}><Icon size={21} /></button>)}</nav>
        <button className="rail-bottom" aria-label="Collapse navigation"><PanelLeftClose size={20} /></button>
      </aside>

      <main className="workspace">
        <header className="topbar"><div><span>GROUNDCREW</span><strong>{title}</strong></div><div className="topbar-meta">{data.demoMode && <span className="demo-badge">PREVIEW DATA</span>}<span><StateDot state={data.server.state} /> Host connected</span><span>14:38 CEST</span></div></header>
        <div className="page-content">
          {page === 'overview' && <Overview data={data} onNavigate={setPage} />}
          {page === 'missions' && <Missions onSwitch={value => setPending({ kind: 'mission', value })} />}
          {page === 'players' && <Players players={data.players} />}
          {page === 'integrations' && <Integrations integrations={data.integrations} />}
          {page === 'chat' && <Chat data={data} />}
          {page === 'settings' && <SettingsPage />}
        </div>
      </main>

      <aside className="control-rail">
        <div className="wordmark"><span>GROUNDCREW</span><small>DCS SERVER OPERATIONS</small></div>
        <div className="server-state"><span className="eyebrow">INSTANCE STATUS</span><div><StateDot state={data.server.state} /><strong>{data.server.state.toUpperCase()}</strong></div><p>{data.server.name}</p></div>
        <dl className="facts"><div><dt>Version</dt><dd>{data.server.version}</dd></div><div><dt>Mission</dt><dd>{data.server.mission}</dd></div><div><dt>Uptime</dt><dd>{duration(data.server.uptimeSeconds)}</dd></div><div><dt>Server FPS</dt><dd>{data.server.fps}</dd></div></dl>
        <div className="control-actions">
          {data.server.state === 'stopped' ? <button className="button primary wide" onClick={() => setPending({ kind: 'start' })}><Play size={17} fill="currentColor" /> Start server</button> : <><button className="button outline wide" onClick={() => setPending({ kind: 'restart' })}><RefreshCw size={17} /> Restart server</button><button className="button outline wide muted" onClick={() => setPending({ kind: 'stop' })}><Square size={15} fill="currentColor" /> Stop server</button></>}
        </div>
        <div className="quick-status"><span><Headphones size={16} /> SRS <i className={srs?.running ? 'ok' : ''}>{srs?.running ? 'Online' : srs?.installed ? 'Stopped' : 'Not installed'}</i></span><span><Radio size={16} /> Olympus <i className={olympus?.running ? 'ok' : ''}>{olympus?.running ? 'Online' : olympus?.installed ? 'Stopped' : 'Not installed'}</i></span><span><Ban size={16} /> Alerts <i>None</i></span></div>
        <button className="power-button" onClick={() => setPending({ kind: data.server.state === 'running' ? 'stop' : 'start' })}><Power size={19} /> {data.server.state === 'running' ? 'SHUT DOWN INSTANCE' : 'START INSTANCE'}</button>
      </aside>

      {pending && <ConfirmDialog title={pending.kind === 'mission' ? 'Load mission and restart?' : `${pending.kind[0].toUpperCase()}${pending.kind.slice(1)} DCS server?`} body={pending.kind === 'mission' ? `${pending.value} will become the active mission. Connected players will be disconnected during the restart.` : `This action controls the DCS dedicated server process on the Windows host.${pending.kind !== 'start' ? ' Connected players may be disconnected.' : ''}`} action={busy ? 'Working…' : pending.kind === 'mission' ? 'Load & restart' : `${pending.kind} server`} danger={pending.kind === 'stop'} onConfirm={act} onClose={() => !busy && setPending(null)} />}
    </div>
  )
}
