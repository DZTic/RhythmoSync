
export interface DialogueBlock {
  id: string;
  text: string;
  startTime: number; // in seconds
  duration: number; // in seconds
  characterName: string;
  color: string;
  lane: number; // 0-indexed track ID
  groupId?: string; // Group blocks together
}

export interface AudioTrack {
  id: string;
  name: string;
  url: string | null;
  volume: number; // 0.0 to 1.0
  muted: boolean;
  solo: boolean;
  isOriginal?: boolean; // Used to identify the video's linked audio track
}

export interface AppState {
  isPlaying: boolean;
  currentTime: number; // Used for UI updates that don't need 60fps (like timecode display)
  duration: number;
  zoomLevel: number; // Pixels per second (PPS)
  syncOffset: number; // Latency calibration in seconds
  totalLanes: number; // Dynamic number of tracks
  dialogues: DialogueBlock[];
  selectedBlockIds: string[];
  copiedBlocks: DialogueBlock[];
  laneHeights: Record<number, number>; // Maps lane index to its height
  globalLaneHeight: number; // Current height for all lanes by default
  videoElement: HTMLVideoElement | null;
  videoSource: string; // URL or Blob URL
  videoPath: string | null; // Local file path for persistence
  proxyVideoSource: string | null; // Proxy Blob URL (lightweight, All-Intra for scrubbing)
  proxyStatus: 'idle' | 'encoding' | 'ready' | 'error'; // State of proxy generation
  useProxy: boolean; // Whether the player is currently showing the proxy

  audioTracks: AudioTrack[]; // Audio tracks for the mixer

  // History
  past: DialogueBlock[][];
  future: DialogueBlock[][];

  // Settings
  fps: number; // Frames Per Second (e.g., 24, 25, 30, 60)
  defaultBlockDuration: number; // Default duration for new blocks in seconds
  playbackRate: number; // Variable playback speed
  snapEnabled: boolean; // Magnétisme des blocs
}

export interface AppActions {
  setIsPlaying: (isPlaying: boolean) => void;
  setCurrentTime: (time: number) => void;
  setDuration: (duration: number) => void;
  setZoomLevel: (zoom: number) => void;
  setSyncOffset: (offset: number) => void;
  setTotalLanes: (lanes: number) => void;
  setLaneHeight: (lane: number, height: number) => void;
  setGlobalLaneHeight: (height: number) => void;
  setVideoElement: (el: HTMLVideoElement | null) => void;
  setVideoSource: (src: string, path?: string) => void; // Updated action
  setProxyVideoSource: (src: string | null) => void;
  setProxyStatus: (status: AppState['proxyStatus']) => void;
  setUseProxy: (use: boolean) => void;

  setAudioTracks: (tracks: AudioTrack[]) => void;
  updateAudioTrack: (id: string, updates: Partial<AudioTrack>) => void;


  addDialogue: (dialogue: DialogueBlock) => void;
  addDialogues: (dialogues: DialogueBlock[]) => void;
  setDialogues: (dialogues: DialogueBlock[]) => void;
  // skipHistory allow us to update state without pushing to undo stack (e.g. while dragging or typing)
  updateDialogue: (id: string, updates: Partial<DialogueBlock>, skipHistory?: boolean) => void;
  updateDialogues: (updatesArray: { id: string, changes: Partial<DialogueBlock> }[], skipHistory?: boolean) => void;
  deleteDialogue: (id: string) => void; // New action
  deleteDialogues: (ids: string[]) => void; // New action for multiple
  groupDialogues: (ids: string[]) => void;
  ungroupDialogues: (ids: string[]) => void;
  selectBlock: (id: string | null, multi?: boolean) => void;
  setCopiedBlocks: (blocks: DialogueBlock[]) => void;

  importProject: (data: Partial<AppState>) => void; // New action
  resetProject: () => void; // New action

  // History Actions
  undo: () => void;
  redo: () => void;
  snapshotHistory: () => void; // Manually push current state to history

  // Settings Actions
  setFps: (fps: number) => void;
  setDefaultBlockDuration: (duration: number) => void;
  setPlaybackRate: (rate: number) => void;
  setSnapEnabled: (enabled: boolean) => void;

  // Outils Actions
  shiftTimeline: (offsetSeconds: number) => void;
  globalFindReplace: (find: string, replace: string) => void;
}

export const SYNC_LINE_POSITION_X = 300; // The fixed vertical red line position in pixels
export const DEFAULT_PPS = 200; // Default pixels per second
export const LANE_HEIGHT = 80; // Default height for a tighter look
export const MAX_CHARS_PER_SECOND = 20; // Recommended reading speed limit (characters per second)

export const getLaneHeight = (laneHeights: Record<number, number>, lane: number, globalHeight: number = LANE_HEIGHT) => 
  laneHeights[lane] ?? globalHeight;

export const getLaneY = (laneHeights: Record<number, number>, lane: number, globalHeight: number = LANE_HEIGHT) => {
  let y = 0;
  for (let i = 0; i < lane; i++) {
    y += getLaneHeight(laneHeights, i, globalHeight);
  }
  return y;
};

export const getTotalBandHeight = (laneHeights: Record<number, number>, totalLanes: number, globalHeight: number = LANE_HEIGHT) => {
  let height = 0;
  for (let i = 0; i < totalLanes; i++) {
    height += getLaneHeight(laneHeights, i, globalHeight);
  }
  return height;
};

export const getLaneFromY = (laneHeights: Record<number, number>, totalLanes: number, y: number, globalHeight: number = LANE_HEIGHT) => {
  let currentY = 0;
  for (let i = 0; i < totalLanes; i++) {
    const h = getLaneHeight(laneHeights, i, globalHeight);
    if (y >= currentY && y < currentY + h) return i;
    currentY += h;
  }
  // Return closest if out of bounds
  return y < 0 ? 0 : totalLanes - 1;
};