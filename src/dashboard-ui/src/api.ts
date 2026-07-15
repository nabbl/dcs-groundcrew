import type { DashboardSettings, DashboardSnapshot, DcsServerConfiguration, DcsServerConfigurationSaveResult, DcsServerConfigurationUpdate, DcsUpdateStatus, FileBrowserResult, FileSystemEntry, GrpcInstallationResult, GrpcInstallationStatus, GrpcInstallerLog, MissionLibraryResult, MissionReadinessReport, ModerationAction, ModerationAuditEntry } from './types'
import { mockSnapshot } from './mockData'
import { HubConnectionBuilder, HttpTransportType } from '@microsoft/signalr'

export async function getSnapshot(): Promise<DashboardSnapshot> {
  try {
    const response = await fetch('/api/snapshot', { signal: AbortSignal.timeout(1200) })
    if (!response.ok) throw new Error('Backend unavailable')
    return await response.json() as DashboardSnapshot
  } catch {
    return mockSnapshot
  }
}

export async function serverAction(action: 'start' | 'stop' | 'restart'): Promise<{ ok: boolean; error?: string }> {
  try {
    const response = await fetch(`/api/server/${action}`, { method: 'POST' })
    if (response.ok) return { ok: true }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? `DCS could not ${action}.` }
  } catch {
    return { ok: false, error: 'Groundcrew could not reach its backend service.' }
  }
}

export async function getDcsUpdateStatus(): Promise<DcsUpdateStatus> {
  const response = await fetch('/api/dcs-update/status')
  if (!response.ok) throw new Error('Groundcrew could not inspect the DCS updater.')
  return await response.json() as DcsUpdateStatus
}

export async function checkDcsUpdate(): Promise<{ ok: boolean; status?: DcsUpdateStatus; error?: string }> {
  try {
    const response = await fetch('/api/dcs-update/check', { method: 'POST' })
    if (response.ok) return { ok: true, status: await response.json() as DcsUpdateStatus }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? 'The update check failed.' }
  } catch { return { ok: false, error: 'The Groundcrew backend is not reachable.' } }
}

export async function applyDcsUpdate(): Promise<{ ok: boolean; status?: DcsUpdateStatus; error?: string }> {
  try {
    const response = await fetch('/api/dcs-update/apply', { method: 'POST' })
    if (response.ok) return { ok: true, status: await response.json() as DcsUpdateStatus }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? 'The DCS update could not be started.' }
  } catch { return { ok: false, error: 'The Groundcrew backend is not reachable.' } }
}

export function subscribeToSnapshots(onSnapshot: (snapshot: DashboardSnapshot) => void) {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/dashboard', { transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling })
    .withAutomaticReconnect()
    .build()
  connection.on('snapshot', onSnapshot)
  connection.start().catch(() => undefined)
  return () => { void connection.stop() }
}

export async function sendChatMessage(message: string): Promise<{ ok: boolean; error?: string }> {
  try {
    const response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message }),
    })
    if (response.ok) return { ok: true }
    const problem = await response.json().catch(() => null) as { detail?: string } | null
    return { ok: false, error: problem?.detail ?? 'The server did not accept the message.' }
  } catch {
    return { ok: false, error: 'The dashboard backend is not reachable.' }
  }
}

export async function moderatePlayer(playerId: string, playerName: string, action: ModerationAction, reason: string, durationSeconds?: number): Promise<{ ok: boolean; entry?: ModerationAuditEntry; error?: string }> {
  try {
    const response = await fetch(`/api/players/${encodeURIComponent(playerId)}/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ playerName, reason, durationSeconds: action === 'ban' ? durationSeconds : null }),
    })
    if (response.ok) return { ok: true, entry: await response.json() as ModerationAuditEntry }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? `Groundcrew could not ${action} this player.` }
  } catch {
    return { ok: false, error: 'The Groundcrew backend is not reachable.' }
  }
}

export async function browseServer(path?: string): Promise<FileBrowserResult> {
  const response = path
    ? await fetch(`/api/files?path=${encodeURIComponent(path)}`)
    : await fetch('/api/files/roots')
  if (!response.ok) throw new Error('This location cannot be read by the dashboard service.')
  if (path) return await response.json() as FileBrowserResult
  const entries = await response.json() as FileSystemEntry[]
  return { currentPath: 'This PC', parentPath: null, entries }
}

export async function switchMission(path: string): Promise<{ ok: boolean; error?: string }> {
  const response = await fetch('/api/missions/switch', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  })
  if (response.ok) return { ok: true }
  const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
  return { ok: false, error: problem?.error ?? problem?.detail ?? 'Mission switch failed.' }
}

export async function getMissionLibrary(): Promise<MissionLibraryResult> {
  const response = await fetch('/api/missions')
  if (!response.ok) throw new Error('The configured mission folder cannot be read by Groundcrew.')
  return await response.json() as MissionLibraryResult
}

export async function inspectMission(path: string): Promise<MissionReadinessReport> {
  const response = await fetch(`/api/missions/inspect?path=${encodeURIComponent(path)}`)
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    throw new Error(problem?.error ?? problem?.detail ?? 'Groundcrew could not inspect this mission.')
  }
  return await response.json() as MissionReadinessReport
}

export async function getServerConfiguration(): Promise<DcsServerConfiguration> {
  const response = await fetch('/api/server-config')
  if (!response.ok) throw new Error('Groundcrew could not read Config\\serverSettings.lua.')
  return await response.json() as DcsServerConfiguration
}

export async function saveServerConfiguration(update: DcsServerConfigurationUpdate): Promise<{ ok: boolean; result?: DcsServerConfigurationSaveResult; error?: string }> {
  try {
    const response = await fetch('/api/server-config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(update),
    })
    if (response.ok) return { ok: true, result: await response.json() as DcsServerConfigurationSaveResult }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? 'Server configuration could not be saved.' }
  } catch {
    return { ok: false, error: 'The Groundcrew backend is not reachable.' }
  }
}

export async function integrationAction(id: string, action: 'start' | 'stop' | 'restart'): Promise<{ ok: boolean; error?: string }> {
  try {
    const response = await fetch(`/api/integrations/${encodeURIComponent(id)}/${action}`, { method: 'POST' })
    if (response.ok) return { ok: true }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? `Could not ${action} this integration.` }
  } catch {
    return { ok: false, error: 'The dashboard backend is not reachable.' }
  }
}

export async function getGrpcStatus(): Promise<GrpcInstallationStatus> {
  const response = await fetch('/api/grpc/status')
  if (!response.ok) throw new Error('Groundcrew could not inspect the DCS-gRPC installation.')
  return await response.json() as GrpcInstallationStatus
}

export async function getGrpcInstallerLog(): Promise<GrpcInstallerLog> {
  const response = await fetch('/api/grpc/install-log')
  if (!response.ok) throw new Error('Groundcrew could not read the DCS-gRPC installer log.')
  return await response.json() as GrpcInstallerLog
}

export async function installGrpc(): Promise<{ ok: boolean; result?: GrpcInstallationResult; error?: string }> {
  try {
    const response = await fetch('/api/grpc/install', { method: 'POST' })
    if (response.ok) return { ok: true, result: await response.json() as GrpcInstallationResult }
    const problem = await response.json().catch(() => null) as { error?: string; detail?: string } | null
    return { ok: false, error: problem?.error ?? problem?.detail ?? 'DCS-gRPC could not be installed.' }
  } catch {
    return { ok: false, error: 'The Groundcrew backend is not reachable.' }
  }
}

export async function getSettings(): Promise<DashboardSettings | null> {
  try {
    const response = await fetch('/api/settings')
    return response.ok ? await response.json() as DashboardSettings : null
  } catch { return null }
}

export async function saveSettings(settings: DashboardSettings): Promise<boolean> {
  try {
    const response = await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settings),
    })
    return response.ok
  } catch { return false }
}
