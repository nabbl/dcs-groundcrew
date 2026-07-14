import type { DashboardSnapshot } from './types'

export const mockSnapshot: DashboardSnapshot = {
  demoMode: true,
  server: {
    state: 'running',
    name: 'DCS SERVER ONE',
    version: '2.9.18.12722',
    mission: 'Operation Enduring Resolve v4.2',
    theatre: 'Syria',
    uptimeSeconds: 17342,
    fps: 58,
    players: 12,
    maxPlayers: 32,
  },
  metrics: [
    { label: 'CPU', value: 38, unit: '%', max: 100 },
    { label: 'Memory', value: 7.8, unit: 'GB', max: 16 },
    { label: 'DCS process', value: 5.2, unit: 'GB', max: 10 },
    { label: 'Disk', value: 614, unit: 'GB free', max: 1000 },
  ],
  players: [
    { id: '1', name: 'Viper 1-1 | Reaper', side: 'Blue', slot: 'F-16C · Kutaisi', ping: 42, joinedAt: '2026-07-14T12:22:00Z' },
    { id: '2', name: 'Colt 2-1 | Hound', side: 'Blue', slot: 'F/A-18C · CVN-73', ping: 67, joinedAt: '2026-07-14T13:04:00Z' },
    { id: '3', name: 'Enfield 3-2 | Mako', side: 'Red', slot: 'MiG-29A · Bassel Al-Assad', ping: 84, joinedAt: '2026-07-14T13:18:00Z' },
    { id: '4', name: 'Overlord | GCI', side: 'Spectator', slot: 'Game Master', ping: 31, joinedAt: '2026-07-14T11:51:00Z' },
  ],
  integrations: [
    { id: 'srs', name: 'SimpleRadio Standalone', description: 'Voice communications', installed: true, running: true, version: '2.2.0.1', configurable: true },
    { id: 'olympus', name: 'DCS Olympus', description: 'Real-time mission control', installed: true, running: true, version: '2.1.3', url: 'http://localhost:3000', configurable: true },
    { id: 'tacview', name: 'Tacview', description: 'Flight recording and ACMI', installed: true, running: false, version: '1.9.4', configurable: true },
    { id: 'skyeye', name: 'SkyEye', description: 'AI-powered GCI', installed: false, running: false, configurable: true },
    { id: 'dks', name: 'Digital Kneeboard', description: 'Mission kneeboard tools', installed: false, running: false, configurable: true },
  ],
  chat: [
    { id: 'c1', author: 'SERVER', message: 'Mission loaded: Operation Enduring Resolve v4.2', timestamp: '14:31', system: true },
    { id: 'c2', author: 'Viper 1-1 | Reaper', message: 'Taxiing runway 25, Kutaisi.', timestamp: '14:34' },
    { id: 'c3', author: 'Overlord | GCI', message: 'Picture clean west of the mountains.', timestamp: '14:35' },
  ],
}

