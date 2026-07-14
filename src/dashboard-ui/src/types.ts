export type ServerState = 'running' | 'stopped' | 'starting' | 'stopping' | 'error'

export interface Metric {
  label: string
  value: number
  unit: string
  max: number
}

export interface Player {
  id: string
  name: string
  side: 'Blue' | 'Red' | 'Spectator'
  slot: string
  ping: number
  joinedAt: string
}

export interface Integration {
  id: string
  name: string
  description: string
  kind: string
  installed: boolean
  running: boolean
  version?: string
  url?: string
  configurable: boolean
}

export interface ChatMessage {
  id: string
  author: string
  message: string
  timestamp: string
  system?: boolean
}

export interface DashboardSnapshot {
  demoMode: boolean
  server: {
    state: ServerState
    name: string
    version: string
    mission: string
    theatre: string
    uptimeSeconds: number
    fps: number
    players: number
    maxPlayers: number
  }
  metrics: Metric[]
  players: Player[]
  integrations: Integration[]
  chat: ChatMessage[]
}

export interface FileSystemEntry {
  name: string
  fullPath: string
  isDirectory: boolean
  size: number | null
  modified: string
}

export interface FileBrowserResult {
  currentPath: string
  parentPath: string | null
  entries: FileSystemEntry[]
}

export interface DashboardSettings {
  serverName: string
  dcsExecutablePath: string
  dcsArguments: string
  savedGamesPath: string
  missionLibraryPath: string
  tacviewRecordingsPath: string
  activeMissionPath: string
  maxPlayers: number
  integrations: Array<{
    id: string
    name: string
    description: string
    executablePath: string
    arguments: string
    kind: string
    configPath: string
    host: string
    port?: number
    srsAddress: string
    telemetryAddress: string
    url?: string
  }>
}

export interface MissionFile {
  name: string
  fullPath: string
  relativePath: string
  size: number
  modified: string
  active: boolean
}

export interface MissionLibraryResult {
  rootPath: string
  configured: boolean
  exists: boolean
  missions: MissionFile[]
}

export interface DcsServerConfiguration {
  path: string
  exists: boolean
  modified: string | null
  name: string
  description: string
  passwordConfigured: boolean
  maxPlayers: number
  port: number
  isPublic: boolean
  bindAddress: string
  listLoop: boolean
  listShuffle: boolean
  resumeMode: number
  maxPing: number
  requirePureClients: boolean
  requirePureScripts: boolean
  requirePureTextures: boolean
  requirePureModels: boolean
  allowOwnshipExport: boolean
  allowObjectExport: boolean
  allowSensorExport: boolean
  allowChangeSkin: boolean
  allowChangeTailNumber: boolean
  voiceChatServer: boolean
  allowTrialOnlyClients: boolean
  allowDynamicRadio: boolean
  allowPlayersPool: boolean
  serverCanScreenshot: boolean
}

export type DcsServerConfigurationUpdate = Omit<DcsServerConfiguration, 'path' | 'exists' | 'modified' | 'passwordConfigured'> & {
  password: string | null
  clearPassword: boolean
}

export interface DcsServerConfigurationSaveResult {
  configuration: DcsServerConfiguration
  backupPath: string | null
}
