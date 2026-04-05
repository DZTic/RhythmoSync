import { create } from 'zustand';
import { AppState, AppActions, DEFAULT_PPS, DialogueBlock, MAX_CHARS_PER_SECOND, AudioTrack } from './types';

// Pas de données de démo — projet vide par défaut
const INITIAL_DIALOGUES: DialogueBlock[] = [];

const INITIAL_AUDIO_TRACKS: AudioTrack[] = [
  { id: 'original', name: 'Original', url: null, volume: 1.0, muted: false, solo: false, isOriginal: true },
  { id: 'voix', name: 'Voix', url: null, volume: 1.0, muted: false, solo: false },
  { id: 'bruitages', name: 'Bruitages', url: null, volume: 1.0, muted: false, solo: false }
];


const DEFAULT_VIDEO_SRC = null;

const MAX_HISTORY = 50;

export const useAppStore = create<AppState & AppActions>((set) => ({
  isPlaying: false,
  currentTime: 0,
  duration: 0,
  zoomLevel: DEFAULT_PPS,
  syncOffset: 0, // 0ms latency offset by default
  totalLanes: 3, // Default number of tracks
  dialogues: INITIAL_DIALOGUES,
  selectedBlockIds: [],
  copiedBlocks: [],
  laneHeights: {},
  globalLaneHeight: 80,
  videoElement: null,
  videoSource: DEFAULT_VIDEO_SRC,
  videoPath: null,
  proxyVideoSource: null,
  proxyStatus: 'idle' as const,
  useProxy: false,

  audioTracks: INITIAL_AUDIO_TRACKS,

  past: [],
  future: [],

  // Settings Defaults
  fps: 25,
  defaultBlockDuration: 2.0,
  playbackRate: 1.0,
  snapEnabled: true,

  setIsPlaying: (isPlaying) => set({ isPlaying }),
  setCurrentTime: (currentTime) => set({ currentTime }),
  setDuration: (duration) => set({ duration }),
  setZoomLevel: (zoomLevel) => set({ zoomLevel }),
  setSyncOffset: (syncOffset) => set({ syncOffset }),
  setTotalLanes: (totalLanes) => set({ totalLanes }),
  setLaneHeight: (lane, height) => set((s) => ({ laneHeights: { ...s.laneHeights, [lane]: height } })),
  setGlobalLaneHeight: (globalLaneHeight) => set({ globalLaneHeight }),
  setVideoElement: (videoElement) => set({ videoElement }),
  setVideoSource: (videoSource, videoPath) => set({ videoSource, videoPath: videoPath || null, proxyVideoSource: null, proxyStatus: 'idle', useProxy: false }),
  setProxyVideoSource: (proxyVideoSource) => set({ proxyVideoSource }),
  setProxyStatus: (proxyStatus) => set({ proxyStatus }),
  setUseProxy: (useProxy) => set({ useProxy }),

  setAudioTracks: (audioTracks) => set({ audioTracks }),
  updateAudioTrack: (id, updates) => set((state) => ({
    audioTracks: state.audioTracks.map(t => t.id === id ? { ...t, ...updates } : t)
  })),

  // Settings Setters
  setFps: (fps) => set({ fps }),
  setDefaultBlockDuration: (defaultBlockDuration) => set({ defaultBlockDuration }),
  setPlaybackRate: (playbackRate) => set({ playbackRate }),
  setSnapEnabled: (snapEnabled) => set({ snapEnabled }),

  // --- History Implementation ---

  snapshotHistory: () => set((state) => {
    // Avoid duplicate history entries if state hasn't changed since last snapshot
    if (state.past.length > 0 && state.past[state.past.length - 1] === state.dialogues) {
      return {};
    }

    const newPast = [...state.past, state.dialogues];
    if (newPast.length > MAX_HISTORY) newPast.shift();
    return {
      past: newPast,
      future: [] // Clear future on new action
    };
  }),

  undo: () => set((state) => {
    if (state.past.length === 0) return {};

    const previous = state.past[state.past.length - 1];
    const newPast = state.past.slice(0, -1);

    return {
      past: newPast,
      future: [state.dialogues, ...state.future],
      dialogues: previous
    };
  }),

  redo: () => set((state) => {
    if (state.future.length === 0) return {};

    const next = state.future[0];
    const newFuture = state.future.slice(1);

    return {
      past: [...state.past, state.dialogues],
      future: newFuture,
      dialogues: next
    };
  }),

  // --- Actions with History ---

  addDialogue: (dialogue) =>
    set((state) => {
      // Save History
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: [...state.dialogues, dialogue]
      };
    }),

  addDialogues: (newDialogues) =>
    set((state) => {
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: [...state.dialogues, ...newDialogues]
      };
    }),

  setDialogues: (newDialogues) =>
    set((state) => {
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: newDialogues
      };
    }),

  updateDialogues: (updatesArray, skipHistory = false) =>
    set((state) => {
      let changeState: Partial<AppState> = {
        dialogues: state.dialogues.map((d) => {
          const update = updatesArray.find(u => u.id === d.id);
          if (!update) return d;

          const newBlock = { ...d, ...update.changes };

          if (update.changes.text !== undefined) {
            const minDurationRequired = newBlock.text.length / MAX_CHARS_PER_SECOND;
            if (update.changes.duration === undefined && newBlock.duration < minDurationRequired) {
              newBlock.duration = minDurationRequired + 0.5;
            }
          }

          return newBlock;
        }),
      };

      if (!skipHistory) {
        const newPast = [...state.past, state.dialogues];
        if (newPast.length > MAX_HISTORY) newPast.shift();
        changeState.past = newPast;
        changeState.future = [];
      }

      return changeState;
    }),

  updateDialogue: (id, updates, skipHistory = false) =>
    set((state) => {
      let changeState: Partial<AppState> = {
        dialogues: state.dialogues.map((d) => {
          if (d.id !== id) return d;

          const newBlock = { ...d, ...updates };

          // Ajustement auto selon la longueur du texte
          if (updates.text !== undefined) {
            const minDurationRequired = newBlock.text.length / MAX_CHARS_PER_SECOND;
            // Si on n'allonge pas volontairement le block, auto-stretch s'il devient trop rapide
            if (updates.duration === undefined && newBlock.duration < minDurationRequired) {
              newBlock.duration = minDurationRequired + 0.5; // Ajout d'une petite marge
            }
          }

          return newBlock;
        }),
      };

      if (!skipHistory) {
        const newPast = [...state.past, state.dialogues];
        if (newPast.length > MAX_HISTORY) newPast.shift();
        changeState.past = newPast;
        changeState.future = [];
      }

      return changeState;
    }),

  deleteDialogue: (id) =>
    set((state) => {
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: state.dialogues.filter((d) => d.id !== id),
        selectedBlockIds: state.selectedBlockIds.filter(bId => bId !== id),
      };
    }),

  deleteDialogues: (ids) =>
    set((state) => {
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: state.dialogues.filter((d) => !ids.includes(d.id)),
        selectedBlockIds: state.selectedBlockIds.filter(bId => !ids.includes(bId)),
      };
    }),

  groupDialogues: (ids) =>
    set((state) => {
      if (ids.length < 2) return state; // Need at least two to group
      const groupId = crypto.randomUUID();
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: state.dialogues.map(d =>
          ids.includes(d.id) ? { ...d, groupId } : d
        ),
      };
    }),

  ungroupDialogues: (ids) =>
    set((state) => {
      if (ids.length === 0) return state;
      const newPast = [...state.past, state.dialogues];
      if (newPast.length > MAX_HISTORY) newPast.shift();

      return {
        past: newPast,
        future: [],
        dialogues: state.dialogues.map(d =>
          ids.includes(d.id) ? { ...d, groupId: undefined } : d
        ),
      };
    }),

  selectBlock: (id, multi = false) => set((state) => {
    if (!id) return { selectedBlockIds: [] };

    // Auto-select entire group if part of a group
    const targetBlock = state.dialogues.find(d => d.id === id);
    const relatedIds = targetBlock?.groupId
      ? state.dialogues.filter(d => d.groupId === targetBlock.groupId).map(d => d.id)
      : [id];

    if (multi) {
      const isAlreadySelected = state.selectedBlockIds.includes(id);
      if (isAlreadySelected) {
        return { selectedBlockIds: state.selectedBlockIds.filter(bId => !relatedIds.includes(bId)) };
      }
      return { selectedBlockIds: Array.from(new Set([...state.selectedBlockIds, ...relatedIds])) };
    }
    return { selectedBlockIds: relatedIds };
  }),
  setCopiedBlocks: (copiedBlocks) => set({ copiedBlocks }),

  importProject: (data) => set((state) => ({
    ...state,
    dialogues: data.dialogues || [],
    zoomLevel: data.zoomLevel || DEFAULT_PPS,
    syncOffset: data.syncOffset || 0,
    totalLanes: data.totalLanes || 3,
    laneHeights: data.laneHeights || {},
    globalLaneHeight: data.globalLaneHeight || 80,
    fps: data.fps || 25,
    videoPath: data.videoPath || null,
    audioTracks: data.audioTracks || INITIAL_AUDIO_TRACKS,
    past: [],
    future: []
  })),

  resetProject: () => set({
    dialogues: [],
    totalLanes: 3,
    laneHeights: {},
    globalLaneHeight: 80,
    syncOffset: 0,
    currentTime: 0,
    selectedBlockIds: [],
    isPlaying: false,
    fps: 25,
    videoPath: null,
    audioTracks: INITIAL_AUDIO_TRACKS,
    past: [],
    future: []
  }),

  // --- Tools Implementation ---

  shiftTimeline: (offsetSeconds) => set((state) => {
    const newPast = [...state.past, state.dialogues];
    if (newPast.length > MAX_HISTORY) newPast.shift();

    return {
      past: newPast,
      future: [],
      dialogues: state.dialogues.map(d => ({
        ...d,
        startTime: Math.max(0, d.startTime + offsetSeconds)
      }))
    };
  }),

  globalFindReplace: (find, replace) => set((state) => {
    if (!find) return state;

    const newPast = [...state.past, state.dialogues];
    if (newPast.length > MAX_HISTORY) newPast.shift();

    const safeFind = find.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const regex = new RegExp(safeFind, 'gi');

    return {
      past: newPast,
      future: [],
      dialogues: state.dialogues.map(d => ({
        ...d,
        text: d.text.replace(regex, replace),
        characterName: d.characterName.replace(regex, replace)
      }))
    };
  })

}));