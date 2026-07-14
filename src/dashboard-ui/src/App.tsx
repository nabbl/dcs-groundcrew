import { useEffect, useMemo, useState } from 'react'
import {
  Activity, Ban, ChevronRight, CircleGauge, Clock3, Cpu, ExternalLink,
  FileArchive, FolderCog, Gauge, HardDrive, Headphones, Home, LayoutDashboard,
  MemoryStick, MessageSquareText, MoreHorizontal, Network, PanelLeftClose, PanelLeftOpen,
  Play, PlugZap, Power, Radio, RefreshCw, RotateCcw, Send, Server, Settings,
  ShieldAlert, Square, Users, X, Info,
} from 'lucide-react'
import { browseServer, getMissionLibrary, getServerConfiguration, getSettings, getSnapshot, inspectMission, integrationAction, saveServerConfiguration, saveSettings, sendChatMessage, serverAction, subscribeToSnapshots, switchMission } from './api'
import { mockMissionLibrary, mockMissionReadiness, mockServerConfiguration, mockSettings, mockSnapshot } from './mockData'
import type { DashboardSettings, DashboardSnapshot, DcsServerConfiguration, DcsServerConfigurationUpdate, FileBrowserResult, Integration, MissionFile, MissionLibraryResult, MissionReadinessReport, Player, ServerState } from './types'

type Page = 'overview' | 'missions' | 'serverConfig' | 'players' | 'integrations' | 'chat' | 'settings'

const nav: { id: Page; label: string; icon: typeof Home }[] = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard },
  { id: 'missions', label: 'Missions', icon: FileArchive },
  { id: 'serverConfig', label: 'Server config', icon: Server },
  { id: 'players', label: 'Players', icon: Users },
  { id: 'integrations', label: 'Integrations', icon: PlugZap },
  { id: 'chat', label: 'Server chat', icon: MessageSquareText },
  { id: 'settings', label: 'Settings', icon: Settings },
]

type IntegrationConfig = DashboardSettings['integrations'][number]

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

function IntegrationRow({ item, config, expanded, busy, error, onExpand, onOpen, onConfigure, onRestart }: { item: Integration; config?: IntegrationConfig; expanded: boolean; busy: boolean; error?: string; onExpand: () => void; onOpen: () => void; onConfigure: () => void; onRestart: () => void }) {
  const webOnly = item.kind === 'web'
  const endpoint = item.url ?? (config?.port ? `${config.host || '127.0.0.1'}:${config.port}` : undefined)
  const status = webOnly ? (item.installed ? 'Web app' : 'Not configured') : item.running ? 'Running' : item.installed ? 'Stopped' : 'Not configured'
  return (
    <article className={`integration-row ${expanded ? 'expanded' : ''}`}>
      <button className="integration-summary" onClick={onExpand}>
        <span className="integration-mark"><Radio size={19} /></span>
        <span className="integration-copy"><strong>{item.name}</strong><small>{item.description}</small></span>
        <span className={`status-label ${item.running || webOnly && item.installed ? 'good' : item.installed ? 'idle' : 'missing'}`}>
          <StateDot state={item.running || webOnly && item.installed} />{status}
        </span>
        <ChevronRight className="row-chevron" size={19} />
      </button>
      {expanded && (
        <div className="integration-detail">
          <div><span>Version</span><strong>{item.version ?? '—'}</strong></div>
          <div><span>{webOnly || item.kind === 'web-process' ? 'Web interface' : 'Endpoint'}</span><strong>{endpoint ?? 'Not configured'}</strong></div>
          <div className="integration-actions">
            {!webOnly && item.kind !== 'telemetry' && item.installed && <button className="button outline" disabled={busy} onClick={onRestart}><RefreshCw className={busy ? 'spin' : ''} size={15} /> Restart</button>}
            <button className={item.installed ? 'button ghost' : 'button outline'} onClick={onConfigure}>{item.installed ? <Settings size={15} /> : <HardDrive size={15} />} {item.installed ? 'Configuration' : 'Configure'}</button>
            {item.url && <button className="button ghost" onClick={onOpen}><ExternalLink size={15} /> {item.id === 'dks' ? 'Open & sign in' : 'Open tool'}</button>}
          </div>
          {error && <div className="integration-error"><ShieldAlert size={14} />{error}</div>}
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
            {data.integrations.map(item => {
              const available = item.running || (item.kind === 'web' && item.installed)
              return <div className="service-tile" key={item.id}><StateDot state={available} /><span>{item.name.replace('SimpleRadio Standalone', 'SRS')}</span><small>{item.kind === 'web' && item.installed ? 'Web app' : item.running ? 'Online' : item.installed ? 'Stopped' : 'Setup required'}</small></div>
            })}
          </div>
        </section>
      </div>
    </div>
  )
}

function Missions({ settings, demoMode, olympusUrl, onSwitch, onOpenSettings }: { settings: DashboardSettings; demoMode: boolean; olympusUrl?: string; onSwitch: (mission: string) => void; onOpenSettings: () => void }) {
  const [browserOpen, setBrowserOpen] = useState(false)
  const [inspecting, setInspecting] = useState<MissionFile | null>(null)
  const [library, setLibrary] = useState<MissionLibraryResult | null>(demoMode ? mockMissionLibrary : null)
  const [loading, setLoading] = useState(!demoMode)
  const [error, setError] = useState('')
  const load = async () => {
    if (demoMode) { setLibrary(mockMissionLibrary); setLoading(false); return }
    setLoading(true); setError('')
    try { setLibrary(await getMissionLibrary()) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to load the mission library.') }
    finally { setLoading(false) }
  }
  useEffect(() => { void load() }, [settings.missionLibraryPath, demoMode])
  const formatSize = (bytes: number) => `${Math.round(bytes / 104857.6) / 10} MB`
  const formatModified = (value: string) => new Date(value).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
  return <><section className="panel page-panel">
    <header className="panel-header large"><div><span className="eyebrow">CONFIGURED MISSION LIBRARY</span><h1>Mission library</h1><p>{library?.rootPath || settings.missionLibraryPath || 'Choose a mission folder in Settings.'}</p></div><div className="header-actions"><button className="button ghost" onClick={() => void load()} disabled={loading}><RefreshCw className={loading ? 'spin' : ''} size={16} /> Refresh</button><button className="button outline" onClick={() => setBrowserOpen(true)}><FolderCog size={16} /> Browse server</button></div></header>
    <div className="table-head"><span>Mission name</span><span>Folder</span><span>Modified</span><span>Size</span><span /></div>
    <div className="mission-list">
      {loading && <div className="mission-empty"><RefreshCw className="spin" size={18} /> Reading {settings.missionLibraryPath || 'mission folder'}…</div>}
      {error && <div className="mission-empty error"><ShieldAlert size={18} />{error}<button className="button outline" onClick={onOpenSettings}>Review settings</button></div>}
      {!loading && !error && library && !library.configured && <div className="mission-empty"><FolderCog size={20} /><strong>No mission folder configured</strong><span>Choose the server-side folder containing your .miz files.</span><button className="button primary" onClick={onOpenSettings}>Open settings</button></div>}
      {!loading && !error && library?.configured && !library.exists && <div className="mission-empty error"><ShieldAlert size={18} /><strong>Mission folder not found</strong><span>{library.rootPath}</span><button className="button outline" onClick={onOpenSettings}>Change folder</button></div>}
      {!loading && !error && library?.exists && library.missions.length === 0 && <div className="mission-empty"><FileArchive size={20} /><strong>No .miz files found</strong><span>{library.rootPath}</span></div>}
      {!loading && !error && library?.missions.map(m => <div className={`mission-row ${m.active ? 'active' : ''}`} key={m.fullPath}>
        <span className="file-mark"><FileArchive size={19} /></span><div><strong>{m.name}</strong><small>{m.active ? 'Currently selected' : m.relativePath}</small></div><span>{m.relativePath.split(/[\\/]/).length > 1 ? m.relativePath.split(/[\\/]/).slice(0, -1).join('\\') : 'Root'}</span><span>{formatModified(m.modified)}</span><span>{formatSize(m.size)}</span><div className="mission-actions"><button className="button ghost" onClick={() => setInspecting(m)}>Details</button><button className={m.active ? 'button ghost' : 'button outline'} disabled={m.active} onClick={() => onSwitch(m.fullPath)}>{m.active ? 'Active' : 'Load'}</button></div>
      </div>)}
    </div>
  </section>{browserOpen && <FileBrowserDialog title="Select mission file" initialPath={settings.missionLibraryPath || undefined} extension=".miz" onSelect={path => { setBrowserOpen(false); onSwitch(path) }} onClose={() => setBrowserOpen(false)} />}{inspecting && <MissionReadinessDrawer mission={inspecting} demoMode={demoMode} olympusUrl={olympusUrl} onClose={() => setInspecting(null)} />}</>
}

function MissionReadinessDrawer({ mission, demoMode, olympusUrl, onClose }: { mission: MissionFile; demoMode: boolean; olympusUrl?: string; onClose: () => void }) {
  const [report, setReport] = useState<MissionReadinessReport | null>(demoMode ? { ...mockMissionReadiness, path: mission.fullPath, title: mission.name, size: mission.size, modified: mission.modified } : null)
  const [error, setError] = useState('')
  useEffect(() => {
    if (demoMode) return
    setReport(null); setError('')
    void inspectMission(mission.fullPath).then(setReport).catch(reason => setError(reason instanceof Error ? reason.message : 'Mission inspection failed.'))
  }, [mission.fullPath, demoMode])
  const checkIcon = (severity: string) => severity === 'pass' ? <Activity size={15} /> : severity === 'info' ? <Info size={15} /> : <ShieldAlert size={15} />
  return <div className="readiness-backdrop" role="presentation" onMouseDown={onClose}><aside className="readiness-drawer" role="dialog" aria-modal="true" aria-label={`Mission readiness for ${mission.name}`} onMouseDown={event => event.stopPropagation()}>
    <header><div><span className="eyebrow">READ-ONLY MIZ SUMMARY</span><h2>{report?.title ?? mission.name}</h2><p>{mission.relativePath}</p></div><button className="icon-button" onClick={onClose} aria-label="Close mission details"><X size={20} /></button></header>
    {!report && !error && <div className="readiness-loading"><RefreshCw className="spin" size={20} />Inspecting mission archive…</div>}
    {error && <div className="readiness-loading error"><ShieldAlert size={20} />{error}</div>}
    {report && <div className="readiness-content">
      <div className={`readiness-status ${report.status}`}><span>{report.status === 'ready' ? <Activity size={17} /> : <ShieldAlert size={17} />}</span><div><strong>{report.status === 'ready' ? 'Ready for review' : report.status === 'warning' ? 'Review recommended' : 'Attention required'}</strong><small>Archive {report.readable ? 'read successfully' : 'could not be read'} · SHA-256 {report.hash.slice(0, 10)}</small></div></div>
      <section className="readiness-facts"><div><span>Theatre</span><strong>{report.theatre}</strong></div><div><span>Date</span><strong>{report.missionDate}</strong></div><div><span>Start</span><strong>{report.startTime}</strong></div><div className="wide"><span>Weather</span><strong>{report.weather}</strong></div></section>
      <section className="readiness-section"><header><div><span className="eyebrow">STATIC CLIENT / PLAYER UNITS</span><h3>Flyable slots</h3></div><strong className="slot-total">{report.totalSlots}</strong></header><div className="coalition-totals"><span className="blue">Blue <strong>{report.blueSlots}</strong></span><span className="red">Red <strong>{report.redSlots}</strong></span>{report.neutralSlots > 0 && <span>Neutral <strong>{report.neutralSlots}</strong></span>}</div><div className="slot-list">{report.slots.map(slot => <div key={`${slot.coalition}-${slot.airframe}`}><span className={`coalition ${slot.coalition.toLowerCase()}`}>{slot.coalition.slice(0, 1)}</span><strong>{slot.airframe}</strong><b>{slot.count}</b></div>)}{report.slots.length === 0 && <p>No static flyable slots detected.</p>}</div></section>
      <section className="readiness-section"><header><div><span className="eyebrow">DECLARED & DETECTED</span><h3>Dependencies</h3></div></header><div className="dependency-list">{report.dependencies.map(item => <span className={item.status} key={`${item.kind}-${item.name}`}><small>{item.kind}</small>{item.name}<i>{item.status}</i></span>)}{report.dependencies.length === 0 && <p>No declared dependencies found.</p>}</div>{report.frameworks.length > 0 && <div className="framework-list"><small>Recognized scripts</small><div>{report.frameworks.map(name => <span key={name}>{name}</span>)}</div></div>}</section>
      <section className="readiness-section"><header><div><span className="eyebrow">OPERATIONAL REVIEW</span><h3>Checks</h3></div></header><div className="readiness-checks">{report.checks.map(check => <div className={check.severity} key={`${check.severity}-${check.title}`}><span>{checkIcon(check.severity)}</span><div><strong>{check.title}</strong><p>{check.detail}</p></div></div>)}</div></section>
      <p className="readiness-note">Slot totals cover static Client and Player units. Runtime scripts can add behavior Groundcrew cannot determine without executing mission code.</p>
    </div>}
    <footer><button className="button ghost" onClick={onClose}>Close</button>{olympusUrl && <a className="button outline" href={olympusUrl} target="_blank" rel="noreferrer"><ExternalLink size={15} /> Open in Olympus</a>}</footer>
  </aside></div>
}

function ConfigToggle({ label, detail, checked, onChange, caution = false }: { label: string; detail: string; checked: boolean; onChange: (checked: boolean) => void; caution?: boolean }) {
  return <label className={`config-toggle ${caution ? 'caution' : ''}`}><span><strong>{label}</strong><small>{detail}</small></span><input type="checkbox" checked={checked} onChange={event => onChange(event.target.checked)} /><i /></label>
}

function ServerConfigurationPage({ demoMode, onOpenSettings }: { demoMode: boolean; onOpenSettings: () => void }) {
  const [config, setConfig] = useState<DcsServerConfiguration | null>(demoMode ? mockServerConfiguration : null)
  const [loading, setLoading] = useState(!demoMode)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [password, setPassword] = useState('')
  const [clearPassword, setClearPassword] = useState(false)
  const load = async () => {
    if (demoMode) { setConfig(mockServerConfiguration); setLoading(false); return }
    setLoading(true); setError('')
    try { setConfig(await getServerConfiguration()) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to read DCS server configuration.') }
    finally { setLoading(false) }
  }
  useEffect(() => { void load() }, [demoMode])
  const update = <K extends keyof DcsServerConfiguration>(key: K, value: DcsServerConfiguration[K]) => setConfig(current => current ? { ...current, [key]: value } : current)
  const save = async () => {
    if (!config) return
    setSaving(true); setError(''); setNotice('')
    const request = { ...config, password: password || null, clearPassword } as DcsServerConfigurationUpdate
    if (demoMode) {
      setConfig({ ...config, exists: true, modified: new Date().toISOString(), passwordConfigured: clearPassword ? false : password ? true : config.passwordConfigured })
      setNotice('Preview saved. Restart DCS after changing the real file.'); setPassword(''); setClearPassword(false); setSaving(false); return
    }
    const response = await saveServerConfiguration(request)
    if (!response.ok || !response.result) setError(response.error ?? 'Server configuration could not be saved.')
    else {
      setConfig(response.result.configuration); setPassword(''); setClearPassword(false)
      setNotice(response.result.backupPath ? `Saved. Backup: ${response.result.backupPath}` : 'serverSettings.lua created. Restart DCS to apply it.')
    }
    setSaving(false)
  }
  if (loading) return <section className="panel page-panel"><div className="mission-empty"><RefreshCw className="spin" size={19} />Reading serverSettings.lua…</div></section>
  if (!config) return <section className="panel page-panel"><div className="mission-empty error"><ShieldAlert size={19} />{error || 'Server configuration is unavailable.'}</div></section>
  return <section className="panel page-panel server-config-page">
    <header className="panel-header large"><div><span className="eyebrow">DCS DEDICATED SERVER</span><h1>Server configuration</h1><p>{config.path || 'Choose the DCS Saved Games directory before editing serverSettings.lua.'}</p></div><div className="save-area">{notice && <span>{notice}</span>}{error && <span className="error">{error}</span>}<button className="button ghost" disabled={loading} onClick={() => void load()}><RefreshCw size={15} /> Reload</button><button className="button primary" disabled={saving || !config.path} onClick={() => void save()}>{saving ? 'Saving…' : 'Save configuration'}</button></div></header>

    {!config.path && <div className="config-banner error"><ShieldAlert size={18} /><div><strong>Saved Games path required</strong><span>Groundcrew derives this file as Saved Games\Config\serverSettings.lua.</span></div><button className="button outline" onClick={onOpenSettings}>Open settings</button></div>}
    {config.path && !config.exists && <div className="config-banner"><FileArchive size={18} /><div><strong>serverSettings.lua does not exist yet</strong><span>Saving will create a valid base file and include the currently selected mission when one is configured.</span></div></div>}
    <div className="config-banner info"><Users size={18} /><div><strong>Player cap versus aircraft slots</strong><span>Maximum players limits simultaneous connections. Flyable aircraft slots and roles are defined inside the selected .miz mission.</span></div></div>

    <div className="server-config-grid">
      <section className="server-config-section wide"><header><span>01</span><div><h2>Identity & access</h2><p>How the server appears in the multiplayer browser.</p></div></header><div className="server-form-grid">
        <label className="server-field wide"><span>Server name</span><input value={config.name} maxLength={200} onChange={event => update('name', event.target.value)} /></label>
        <label className="server-field wide"><span>Description</span><textarea value={config.description} maxLength={4000} rows={4} onChange={event => update('description', event.target.value)} /></label>
        <label className="server-field"><span>New password</span><input type="password" value={password} maxLength={200} disabled={clearPassword} onChange={event => setPassword(event.target.value)} placeholder={config.passwordConfigured ? 'Leave blank to keep current password' : 'No password configured'} /></label>
        <ConfigToggle label="Public server" detail="Publish this instance in the DCS multiplayer server list." checked={config.isPublic} onChange={value => update('isPublic', value)} />
        {config.passwordConfigured && <ConfigToggle label="Remove password" detail="Clear the existing join password when saving." checked={clearPassword} onChange={setClearPassword} caution />}
      </div></section>

      <section className="server-config-section"><header><span>02</span><div><h2>Capacity & network</h2><p>Connection limits and the DCS game listener.</p></div></header><div className="server-form-grid">
        <label className="server-field"><span>Maximum players</span><input type="number" min="1" max="256" value={config.maxPlayers} onChange={event => update('maxPlayers', Number(event.target.value))} /><small>Global player cap—not aircraft slots.</small></label>
        <label className="server-field"><span>Game port</span><input type="number" min="1" max="65535" value={config.port} onChange={event => update('port', Number(event.target.value))} /><small>Default: 10308 TCP/UDP.</small></label>
        <label className="server-field"><span>Bind address</span><input value={config.bindAddress} onChange={event => update('bindAddress', event.target.value)} placeholder="Blank = all interfaces" /></label>
        <label className="server-field"><span>Maximum ping</span><input type="number" min="0" max="5000" value={config.maxPing} onChange={event => update('maxPing', Number(event.target.value))} /><small>Milliseconds; 0 disables the limit.</small></label>
      </div></section>

      <section className="server-config-section"><header><span>03</span><div><h2>Mission lifecycle</h2><p>When simulation resumes and how missions rotate.</p></div></header><div className="server-form-grid">
        <label className="server-field wide"><span>Resume simulation</span><select value={config.resumeMode} onChange={event => update('resumeMode', Number(event.target.value))}><option value={0}>Manual</option><option value={1}>On mission load</option><option value={2}>When clients connect</option></select></label>
        <ConfigToggle label="Loop mission list" detail="Return to the first mission after the list finishes." checked={config.listLoop} onChange={value => update('listLoop', value)} />
        <ConfigToggle label="Shuffle mission list" detail="Choose the next configured mission randomly." checked={config.listShuffle} onChange={value => update('listShuffle', value)} />
      </div></section>

      <section className="server-config-section"><header><span>04</span><div><h2>Integrity checks</h2><p>Require matching client content for fair multiplayer.</p></div></header><div className="toggle-stack">
        <ConfigToggle label="Pure clients" detail="Enable DCS client integrity checking." checked={config.requirePureClients} onChange={value => update('requirePureClients', value)} />
        <ConfigToggle label="Pure scripts" detail="Require protected script files to match." checked={config.requirePureScripts} onChange={value => update('requirePureScripts', value)} />
        <ConfigToggle label="Pure textures" detail="Require protected textures to match." checked={config.requirePureTextures} onChange={value => update('requirePureTextures', value)} />
        <ConfigToggle label="Pure models" detail="Require protected 3D models to match." checked={config.requirePureModels} onChange={value => update('requirePureModels', value)} />
      </div></section>

      <section className="server-config-section"><header><span>05</span><div><h2>Data exports</h2><p>Control telemetry available to client-side tools.</p></div></header><div className="toggle-stack">
        <ConfigToggle label="Ownship export" detail="Allow clients to export their own aircraft data." checked={config.allowOwnshipExport} onChange={value => update('allowOwnshipExport', value)} />
        <ConfigToggle label="Object export" detail="Allow export of world-object information." checked={config.allowObjectExport} onChange={value => update('allowObjectExport', value)} caution />
        <ConfigToggle label="Sensor export" detail="Allow export of aircraft sensor information." checked={config.allowSensorExport} onChange={value => update('allowSensorExport', value)} caution />
      </div></section>

      <section className="server-config-section wide"><header><span>06</span><div><h2>Player capabilities & services</h2><p>Convenience options exposed by modern DCS server builds.</p></div></header><div className="toggle-grid">
        <ConfigToggle label="Change livery" detail="Allow players to select aircraft skins." checked={config.allowChangeSkin} onChange={value => update('allowChangeSkin', value)} />
        <ConfigToggle label="Change tail number" detail="Allow editable aircraft tail numbers." checked={config.allowChangeTailNumber} onChange={value => update('allowChangeTailNumber', value)} />
        <ConfigToggle label="DCS voice chat" detail="Enable the built-in DCS voice-chat server." checked={config.voiceChatServer} onChange={value => update('voiceChatServer', value)} />
        <ConfigToggle label="Trial-only clients" detail="Allow clients using only trial modules." checked={config.allowTrialOnlyClients} onChange={value => update('allowTrialOnlyClients', value)} />
        <ConfigToggle label="Dynamic radio" detail="Enable dynamic radio support when available." checked={config.allowDynamicRadio} onChange={value => update('allowDynamicRadio', value)} />
        <ConfigToggle label="Players pool" detail="Enable the shared player-slot pool behavior." checked={config.allowPlayersPool} onChange={value => update('allowPlayersPool', value)} />
        <ConfigToggle label="Server screenshots" detail="Allow the server to request client screenshots." checked={config.serverCanScreenshot} onChange={value => update('serverCanScreenshot', value)} caution />
      </div></section>
    </div>
    <footer className="server-config-footer"><ShieldAlert size={16} /><span>Groundcrew preserves unrecognized Lua settings and creates a timestamped backup before every update. Restart DCS after saving so the new configuration is loaded.</span></footer>
  </section>
}

function Players({ players }: { players: Player[] }) {
  return <section className="panel page-panel"><header className="panel-header large"><div><span className="eyebrow">LIVE ROSTER</span><h1>Connected players</h1><p>{players.length} players currently connected. Moderation actions are logged.</p></div></header><div className="rows roomy">{players.map(p => <PlayerRow key={p.id} player={p} />)}</div><div className="moderation-note"><ShieldAlert size={18} /><div><strong>Moderation controls</strong><span>Open a player’s action menu to kick, ban, mute, or move them to spectators.</span></div></div></section>
}

function Integrations({ integrations, settings, onSaveSettings, onRefresh }: { integrations: Integration[]; settings: DashboardSettings; onSaveSettings: (settings: DashboardSettings) => Promise<boolean>; onRefresh: () => Promise<void> }) {
  const [expanded, setExpanded] = useState('srs')
  const [tool, setTool] = useState<Integration | null>(null)
  const [configuring, setConfiguring] = useState<IntegrationConfig | null>(null)
  const [busy, setBusy] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const open = (item: Integration) => {
    if (!item.url) return
    if (item.id === 'dks') window.open(item.url, '_blank', 'noopener,noreferrer')
    else setTool(item)
  }
  const restart = async (item: Integration) => {
    setBusy(item.id); setErrors(current => ({ ...current, [item.id]: '' }))
    const result = await integrationAction(item.id, 'restart')
    if (!result.ok) setErrors(current => ({ ...current, [item.id]: result.error ?? 'Restart failed.' }))
    else await onRefresh()
    setBusy('')
  }
  const saveConfiguration = async (next: DashboardSettings) => {
    const ok = await onSaveSettings(next)
    if (ok) await onRefresh()
    return ok
  }
  return <><section className="panel page-panel"><header className="panel-header large"><div><span className="eyebrow">COMPANION SERVICES</span><h1>Integrations</h1><p>Configure actual processes, endpoints, and files used by this DCS host.</p></div></header><div className="integration-list">{integrations.map(item => {
    const config = settings.integrations.find(value => value.id === item.id)
    return <IntegrationRow key={item.id} item={item} config={config} expanded={expanded === item.id} busy={busy === item.id} error={errors[item.id]} onExpand={() => setExpanded(expanded === item.id ? '' : item.id)} onOpen={() => open(item)} onConfigure={() => config && setConfiguring(config)} onRestart={() => void restart(item)} />
  })}</div></section>{tool?.url && <ToolFrame tool={tool} onClose={() => setTool(null)} />}{configuring && <IntegrationConfigDialog key={configuring.id} config={configuring} settings={settings} onSave={saveConfiguration} onClose={() => setConfiguring(null)} />}</>
}

function IntegrationConfigDialog({ config, settings, onSave, onClose }: { config: IntegrationConfig; settings: DashboardSettings; onSave: (settings: DashboardSettings) => Promise<boolean>; onClose: () => void }) {
  const [value, setValue] = useState<IntegrationConfig>({ ...config })
  const [browsing, setBrowsing] = useState<'executablePath' | 'configPath' | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const update = <K extends keyof IntegrationConfig>(key: K, next: IntegrationConfig[K]) => setValue(current => ({ ...current, [key]: next }))
  const save = async () => {
    setSaving(true); setError('')
    const next = { ...settings, integrations: settings.integrations.map(item => item.id === value.id ? value : item) }
    if (await onSave(next)) onClose()
    else setError('Groundcrew could not save this configuration.')
    setSaving(false)
  }
  const browsePath = browsing ? value[browsing] : ''
  return <><div className="dialog-backdrop" role="presentation" onMouseDown={onClose}><div className="integration-dialog" role="dialog" aria-modal="true" onMouseDown={event => event.stopPropagation()}>
    <header><div><span className="eyebrow">INTEGRATION CONFIGURATION</span><h2>{value.name}</h2><p>{value.description}</p></div><button className="icon-button" onClick={onClose} aria-label="Close configuration"><X size={19} /></button></header>
    <div className="config-form">
      {value.kind !== 'web' && value.id !== 'tacview' && <ConfigPathField label="Executable" value={value.executablePath} onChange={next => update('executablePath', next)} onBrowse={() => setBrowsing('executablePath')} />}
      {value.kind !== 'web' && <ConfigPathField label={value.id === 'tacview' ? 'DCS options file' : 'Configuration file'} value={value.configPath} onChange={next => update('configPath', next)} onBrowse={() => setBrowsing('configPath')} />}
      {(value.id === 'srs' || value.id === 'olympus' || value.id === 'tacview') && <div className="config-pair"><label><span>Host</span><input value={value.host} onChange={event => update('host', event.target.value)} placeholder="127.0.0.1" /></label><label><span>Port</span><input type="number" min="1" max="65535" value={value.port ?? ''} onChange={event => update('port', event.target.value ? Number(event.target.value) : undefined)} /></label></div>}
      {value.id === 'skyeye' && <><label className="config-field"><span>SRS server address</span><input value={value.srsAddress} onChange={event => update('srsAddress', event.target.value)} placeholder="127.0.0.1:5002" /></label><label className="config-field"><span>Tacview telemetry address</span><input value={value.telemetryAddress} onChange={event => update('telemetryAddress', event.target.value)} placeholder="127.0.0.1:42674" /></label></>}
      {(value.kind === 'web' || value.kind === 'web-process') && <label className="config-field"><span>Web URL</span><input value={value.url ?? ''} onChange={event => update('url', event.target.value)} placeholder="http://127.0.0.1:3000" /></label>}
      {value.kind !== 'web' && value.id !== 'tacview' && <label className="config-field"><span>Launch arguments</span><input value={value.arguments} onChange={event => update('arguments', event.target.value)} placeholder={value.id === 'skyeye' ? '--config-file config.yaml' : 'Optional'} /></label>}
      <div className="config-note">{value.id === 'srs' && 'SRS normally listens on TCP and UDP port 5002. Groundcrew detects the running service from the process or local TCP listener.'}{value.id === 'olympus' && 'Olympus normally exposes its frontend over HTTP on port 3000. The URL is used for the embedded tool view.'}{value.id === 'tacview' && 'Tacview real-time telemetry defaults to TCP 42674. Recording storage is configured separately under Server paths.'}{value.id === 'skyeye' && 'SkyEye connects to both SRS and Tacview. Its Windows config.yaml normally sits beside skyeye.exe.'}{value.id === 'dks' && 'DKS is a hosted web application. Groundcrew opens it in a new browser tab so you can sign in with Discord or Google.'}</div>
      {error && <div className="integration-error"><ShieldAlert size={14} />{error}</div>}
    </div>
    <footer><button className="button ghost" onClick={onClose}>Cancel</button>{value.url && <a className="button outline" href={value.url} target="_blank" rel="noreferrer"><ExternalLink size={15} /> Test URL</a>}<button className="button primary" disabled={saving} onClick={() => void save()}>{saving ? 'Saving…' : 'Save configuration'}</button></footer>
  </div></div>{browsing && <FileBrowserDialog title={`Select ${browsing === 'executablePath' ? 'executable' : 'configuration file'}`} initialPath={browsePath.replace(/[\\/][^\\/]+$/, '') || undefined} extension={browsing === 'executablePath' ? '.exe' : undefined} onSelect={path => { update(browsing, path); setBrowsing(null) }} onClose={() => setBrowsing(null)} />}</>
}

function ConfigPathField({ label, value, onChange, onBrowse }: { label: string; value: string; onChange: (value: string) => void; onBrowse: () => void }) {
  return <label className="config-field"><span>{label}</span><div><input value={value} onChange={event => onChange(event.target.value)} placeholder="Select a file on the server host" /><button className="button outline" type="button" onClick={onBrowse}><FolderCog size={15} /> Browse</button></div></label>
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

function SettingsPage({ settings, onSave }: { settings: DashboardSettings; onSave: (settings: DashboardSettings) => Promise<boolean> }) {
  const [paths, setPaths] = useState<Record<string, string>>({
    'DCS executable': settings.dcsExecutablePath,
    'Saved Games': settings.savedGamesPath,
    'Mission library': settings.missionLibraryPath,
    'Tacview recordings': settings.tacviewRecordingsPath,
  })
  const [browsing, setBrowsing] = useState<string | null>(null)
  const [saved, setSaved] = useState<'idle' | 'ok' | 'error'>('idle')
  useEffect(() => { setPaths({ 'DCS executable': settings.dcsExecutablePath, 'Saved Games': settings.savedGamesPath, 'Mission library': settings.missionLibraryPath, 'Tacview recordings': settings.tacviewRecordingsPath }) }, [settings])
  const save = async () => {
    const next = { ...settings, dcsExecutablePath: paths['DCS executable'], savedGamesPath: paths['Saved Games'], missionLibraryPath: paths['Mission library'], tacviewRecordingsPath: paths['Tacview recordings'] }
    setSaved(await onSave(next) ? 'ok' : 'error')
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
  const [settings, setSettings] = useState<DashboardSettings>(mockSettings)
  const [navExpanded, setNavExpanded] = useState(false)
  const [pending, setPending] = useState<{ kind: 'start' | 'stop' | 'restart' | 'mission'; value?: string } | null>(null)
  const [busy, setBusy] = useState(false)

  const refreshSnapshot = async () => setData(await getSnapshot())
  useEffect(() => {
    void refreshSnapshot()
    void getSettings().then(value => value && setSettings(value))
    const unsubscribe = subscribeToSnapshots(setData)
    const polling = window.setInterval(() => { void refreshSnapshot() }, 5000)
    return () => { unsubscribe(); window.clearInterval(polling) }
  }, [])
  const persistSettings = async (next: DashboardSettings) => {
    const ok = data.demoMode || await saveSettings(next)
    if (ok) setSettings(next)
    return ok
  }
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
    if (!missionResult || missionResult.ok) {
      setData(current => ({ ...current, server: { ...current.server, state: action === 'stop' ? 'stopped' : 'running', mission: missionName ?? current.server.mission } }))
      if (pending.kind === 'mission' && pending.value) setSettings(current => ({ ...current, activeMissionPath: pending.value! }))
    }
    setBusy(false); setPending(null)
  }

  return (
    <div className={`app-shell ${navExpanded ? 'nav-expanded' : ''}`}>
      <aside className="nav-rail">
        <div className="brand-mark"><img src="/groundcrew-mark.svg" alt="" /><span>Groundcrew</span></div>
        <nav>{nav.map(({ id, label, icon: Icon }) => <button key={id} className={page === id ? 'active' : ''} onClick={() => setPage(id)} aria-label={label} data-label={label}><Icon size={21} /><span className="nav-label">{label}</span></button>)}</nav>
        <button className="rail-bottom" onClick={() => setNavExpanded(value => !value)} aria-label={navExpanded ? 'Collapse navigation' : 'Expand navigation'} aria-expanded={navExpanded}>{navExpanded ? <PanelLeftClose size={20} /> : <PanelLeftOpen size={20} />}<span className="nav-label">Collapse menu</span></button>
      </aside>

      <main className="workspace">
        <header className="topbar"><div><span>GROUNDCREW</span><strong>{title}</strong></div><div className="topbar-meta">{data.demoMode && <span className="demo-badge">PREVIEW DATA</span>}<span><StateDot state={data.server.state} /> Host connected</span><span>14:38 CEST</span></div></header>
        <div className="page-content">
          {page === 'overview' && <Overview data={data} onNavigate={setPage} />}
          {page === 'missions' && <Missions settings={settings} demoMode={data.demoMode} olympusUrl={olympus?.url ?? settings.integrations.find(item => item.id === 'olympus')?.url} onSwitch={value => setPending({ kind: 'mission', value })} onOpenSettings={() => setPage('settings')} />}
          {page === 'serverConfig' && <ServerConfigurationPage demoMode={data.demoMode} onOpenSettings={() => setPage('settings')} />}
          {page === 'players' && <Players players={data.players} />}
          {page === 'integrations' && <Integrations integrations={data.integrations} settings={settings} onSaveSettings={persistSettings} onRefresh={refreshSnapshot} />}
          {page === 'chat' && <Chat data={data} />}
          {page === 'settings' && <SettingsPage settings={settings} onSave={persistSettings} />}
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
