import React, { useState, useEffect, useRef } from 'react';
import { VideoPlayer } from './components/VideoPlayer';
import { RhythmoBand } from './components/RhythmoBand';
import { Controls } from './components/Controls';
import { Waveform } from './components/Waveform';
import { EditorSidebar } from './components/EditorSidebar';
import { WhisperPanel } from './components/WhisperPanel';
import { HistoryPanel } from './components/HistoryPanel';
import { SYNC_LINE_POSITION_X, LANE_HEIGHT, AppState, DEFAULT_PPS, getTotalBandHeight, getLaneY, getLaneHeight } from './types';
import { useAppStore } from './store';
import Konva from 'konva';

import { open, save, message } from '@tauri-apps/plugin-dialog';
import { readTextFile, writeTextFile, writeFile } from '@tauri-apps/plugin-fs';
// @ts-ignore
import { convertFileSrc, invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import {
    FileText, Download, Wrench, Settings, ChevronDown,
    FolderOpen, Save, FileVideo, PlusCircle, Move,
    Search, BarChart, RotateCcw, RotateCw, X,
    Captions, FileSpreadsheet, Keyboard, Sliders, Film,
    Command, MoreVertical, LayoutTemplate, Share2, Wand2, History, Magnet, Info, Cpu, Zap,
    Maximize2, Minimize2, Play, Pause
} from 'lucide-react';

export default function App() {
    const {
        totalLanes, dialogues, syncOffset, zoomLevel,
        resetProject, importProject, setVideoSource, videoPath,
        shiftTimeline, globalFindReplace, setZoomLevel, setCurrentTime,
        fps, setFps, defaultBlockDuration, setDefaultBlockDuration,
        videoElement, isPlaying, setIsPlaying, setSyncOffset,
        undo, redo, addDialogues, setDialogues, snapshotHistory,
        snapEnabled, setSnapEnabled,
        setProxyVideoSource, setProxyStatus, setUseProxy,
        deleteDialogue, deleteDialogues, selectedBlockIds, selectBlock,
        laneHeights, globalLaneHeight, setGlobalLaneHeight
    } = useAppStore();

    // RhythmoBand now takes full width since sidebar is only in top section
    const [bandDimensions, setBandDimensions] = useState({
        width: window.innerWidth,
        height: getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight)
    });

    // Export State
    const [isExportingVideo, setIsExportingVideo] = useState(false);
    const isExportingRef = useRef(false); // Ref for loop access to current state
    const [exportProgress, setExportProgress] = useState(0);
    const [estimatedTimeRemaining, setEstimatedTimeRemaining] = useState<string | null>(null);
    const [exportConfig, setExportConfig] = useState({ 
        resolution: 'SOURCE' as 'SOURCE' | '1080p' | '720p' | '480p',
        bandScale: 1.0 
    });
    const rhythmoStageRef = useRef<Konva.Stage>(null);
    const activeTapsRef = useRef<Map<string, { id: string, startTime: number }>>(new Map()); // Tap to Add
    const [ffmpegStatus, setFfmpegStatus] = useState<'unknown' | 'checking' | 'downloading' | 'ready' | 'not_found'>('unknown');
    const [ffmpegMessage, setFfmpegMessage] = useState('');
    const [exportFpsEffective, setExportFpsEffective] = useState(0);
    const [gpuEncoderInfo, setGpuEncoderInfo] = useState<{ encoder: string; label: string; is_gpu: boolean } | null>(null);

    // Menu State
    const [isFileMenuOpen, setIsFileMenuOpen] = useState(false);
    const [isEditMenuOpen, setIsEditMenuOpen] = useState(false);
    const [isViewMenuOpen, setIsViewMenuOpen] = useState(false);
    const [isExportMenuOpen, setIsExportMenuOpen] = useState(false);

    // Modals State
    const [activeModal, setActiveModal] = useState<null | 'SHIFT' | 'FIND' | 'STATS' | 'SETTINGS' | 'EXPORT_CONFIG'>(null);
    const [isWhisperPanelOpen, setIsWhisperPanelOpen] = useState(false);
    const [isHistoryPanelOpen, setIsHistoryPanelOpen] = useState(false);
    const [settingsTab, setSettingsTab] = useState<'GENERAL' | 'SHORTCUTS'>('GENERAL');

    // Mode Présentation/Doublage
    const [isPresentationMode, setIsPresentationMode] = useState(false);
    const presentationBandRef = useRef<Konva.Stage>(null);
    const [presentationBandDims, setPresentationBandDims] = useState({ width: window.innerWidth, height: getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight) });
    const presentationBandContainerRef = useRef<HTMLDivElement>(null);

    // Tool Inputs State
    const [shiftAmount, setShiftAmount] = useState("0");
    const [findText, setFindText] = useState("");
    const [replaceText, setReplaceText] = useState("");

    const fileMenuRef = useRef<HTMLDivElement>(null);
    const editMenuRef = useRef<HTMLDivElement>(null);
    const viewMenuRef = useRef<HTMLDivElement>(null);
    const exportMenuRef = useRef<HTMLDivElement>(null);

    // Resizing Workstation State
    const Math_min_height = Math.floor(window.innerHeight * 0.4) || 350;
    const [workstationHeight, setWorkstationHeight] = useState<number | 'auto'>(Math_min_height);
    const isResizingWorkstation = useRef(false);

    // Alert Configuration
    const [alertConfig, setAlertConfig] = useState({
        isOpen: false,
        type: 'info' as 'info' | 'confirm',
        title: '',
        message: '',
        onConfirm: undefined as (() => void) | undefined
    });

    const showAlert = (title: string, message: string) => {
        setAlertConfig({ isOpen: true, type: 'info', title, message, onConfirm: undefined });
    };

    const showConfirm = (title: string, message: string, onConfirm: () => void) => {
        setAlertConfig({ isOpen: true, type: 'confirm', title, message, onConfirm });
    };

    const closeAlert = () => {
        setAlertConfig(prev => ({ ...prev, isOpen: false }));
    };

    const handleAlertConfirm = () => {
        if (alertConfig.onConfirm) alertConfig.onConfirm();
        closeAlert();
    };

    // ————————————————————————————————————————————————————————————————————————————————
    useEffect(() => {
        let rafId: number | null = null;
        const handleMouseMove = (e: MouseEvent) => {
            if (!isResizingWorkstation.current) return;
            if (rafId !== null) cancelAnimationFrame(rafId);
            rafId = requestAnimationFrame(() => {
                const newHeight = window.innerHeight - e.clientY;
                // Minimum size for workstation: 150px, Max size: windowHeight - 100px
                if (newHeight >= 150 && newHeight <= window.innerHeight - 100) {
                    setWorkstationHeight(newHeight);
                }
                rafId = null;
            });
        };

        const handleMouseUp = () => {
            if (isResizingWorkstation.current) {
                isResizingWorkstation.current = false;
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                if (rafId !== null) cancelAnimationFrame(rafId);
            }
        };

        window.addEventListener('mousemove', handleMouseMove);
        window.addEventListener('mouseup', handleMouseUp);

        return () => {
            window.removeEventListener('mousemove', handleMouseMove);
            window.removeEventListener('mouseup', handleMouseUp);
            if (rafId !== null) cancelAnimationFrame(rafId);
        };
    }, []);

    const startResizingWorkstation = (e: React.MouseEvent) => {
        e.preventDefault();
        isResizingWorkstation.current = true;
        document.body.style.cursor = 'ns-resize';
        document.body.style.userSelect = 'none';
    };

    const rhythmoContainerRef = useRef<HTMLDivElement>(null);

    // PERFORMANCE: Use ResizeObserver localized to the RhythmoBand container
    // to update dimensions exactly to its width, and at least its height to prevent empty space.
    useEffect(() => {
        const container = rhythmoContainerRef.current;
        if (!container) return;

        let rafId: number | null = null;
        const resizeObserver = new ResizeObserver((entries) => {
            for (let entry of entries) {
                if (rafId !== null) cancelAnimationFrame(rafId);
                rafId = requestAnimationFrame(() => {
                    setBandDimensions({
                        width: entry.contentRect.width,
                        // Ensure the band is at least as tall as the container so there's no "empty hole"
                        // but can be taller if there are many lanes to allow scrolling.
                        height: Math.max(entry.contentRect.height, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight))
                    });
                    rafId = null;
                });
            }
        });

        resizeObserver.observe(container);
        return () => {
            resizeObserver.disconnect();
            if (rafId !== null) cancelAnimationFrame(rafId);
        };
    }, [totalLanes, globalLaneHeight, laneHeights]);

    // Mise à jour immédiate de la hauteur du canvas quand globalLaneHeight, totalLanes ou laneHeights changent
    // (le ResizeObserver ne se déclenche pas quand seule la hauteur des pistes change)
    useEffect(() => {
        const container = rhythmoContainerRef.current;
        const newBandHeight = getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight);
        setBandDimensions(prev => ({
            width: container ? container.clientWidth || prev.width : prev.width,
            height: Math.max(container ? container.clientHeight || newBandHeight : newBandHeight, newBandHeight)
        }));
    }, [globalLaneHeight, totalLanes, laneHeights]);

    // Ajuste automatiquement la hauteur de la workstation pour que toutes les pistes soient visibles
    // WAVEFORM_HEIGHT = 60px (constante dans Waveform.tsx) + 4px de padding
    const WAVEFORM_AREA_HEIGHT = 64;
    useEffect(() => {
        const bandHeight = getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight);
        const needed = bandHeight + WAVEFORM_AREA_HEIGHT;
        const minH = 150;
        const maxH = window.innerHeight - 150; // laisser de la place pour la vidéo
        setWorkstationHeight(Math.max(minH, Math.min(needed, maxH)));
    }, [globalLaneHeight, totalLanes, laneHeights]);

    // Click outside to close menus
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            const target = event.target as Node;
            if (fileMenuRef.current && !fileMenuRef.current.contains(target)) setIsFileMenuOpen(false);
            if (editMenuRef.current && !editMenuRef.current.contains(target)) setIsEditMenuOpen(false);
            if (viewMenuRef.current && !viewMenuRef.current.contains(target)) setIsViewMenuOpen(false);
            if (exportMenuRef.current && !exportMenuRef.current.contains(target)) setIsExportMenuOpen(false);
        };
        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    // Global Keyboard Shortcuts
    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            const target = e.target as HTMLElement;
            const isInput = ['INPUT', 'TEXTAREA'].includes(target.tagName) || target.isContentEditable;
            if (isExportingVideo) return;

            // Ignore global shortcuts when typing in an input
            if (isInput) return;

            // UNDO / REDO
            if (e.ctrlKey || e.metaKey) {
                if (e.key.toLowerCase() === 'z') {
                    e.preventDefault();
                    e.shiftKey ? redo() : undo();
                    return;
                }
                if (e.key.toLowerCase() === 'y') {
                    e.preventDefault();
                    redo();
                    return;
                }
            }

            if (e.code === 'Space' || e.key === ' ') {
                e.preventDefault();
                const storeState = useAppStore.getState();
                const ve = storeState.videoElement;
                if (ve) {
                    if (ve.paused) {
                        ve.play().catch((err: Error) => {
                            if (err.name !== 'AbortError') console.error('Play error:', err);
                        });
                    } else {
                        ve.pause();
                    }
                }
            }

            if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
                e.preventDefault();
                const veArrow = useAppStore.getState().videoElement;
                if (veArrow) {
                    veArrow.pause();
                    // setIsPlaying(false) sera déclenché par onPause → handlePause
                    const direction = e.key === 'ArrowLeft' ? -1 : 1;
                    const multiplier = e.shiftKey ? 1.0 : (1 / fps);
                    veArrow.currentTime = Math.max(0, veArrow.currentTime + (direction * multiplier));
                }
            }

            // COPY selected block(s)
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'c') {
                if (selectedBlockIds.length > 0) {
                    const storeState = useAppStore.getState();
                    const blocksToCopy = storeState.dialogues.filter(d => selectedBlockIds.includes(d.id));
                    if (blocksToCopy.length > 0) {
                        e.preventDefault();
                        storeState.setCopiedBlocks(blocksToCopy);
                    }
                }
            }

            // PASTE copied block(s)
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v') {
                const copiedBlocks = useAppStore.getState().copiedBlocks;
                if (copiedBlocks && copiedBlocks.length > 0) {
                    e.preventDefault();
                    const storeState = useAppStore.getState();
                    storeState.snapshotHistory();

                    const minStartTime = Math.min(...copiedBlocks.map(b => b.startTime));
                    const pasteCursorTime = storeState.videoElement ? storeState.videoElement.currentTime : 0;

                    const newIds: string[] = [];
                    const newBlocks: typeof copiedBlocks = [];

                    copiedBlocks.forEach(block => {
                        const newId = crypto.randomUUID();
                        newIds.push(newId);
                        const relativeOffset = block.startTime - minStartTime;
                        newBlocks.push({
                            ...block,
                            id: newId,
                            startTime: pasteCursorTime + relativeOffset
                        });
                    });

                    storeState.addDialogues(newBlocks);
                    storeState.selectBlock(null);
                    newIds.forEach(id => storeState.selectBlock(id, true));
                }
            }

            // GROUP selected blocks
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'g') {
                if (selectedBlockIds.length > 1) {
                    e.preventDefault();
                    useAppStore.getState().groupDialogues(selectedBlockIds);
                }
            }

            // UNGROUP selected blocks
            if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'g') {
                if (selectedBlockIds.length > 0) {
                    e.preventDefault();
                    useAppStore.getState().ungroupDialogues(selectedBlockIds);
                }
            }

            // DELETE selected block(s)
            if ((e.key === 'Delete' || e.key === 'Backspace') && selectedBlockIds.length > 0) {
                e.preventDefault();
                deleteDialogues(selectedBlockIds);
                selectBlock(null);
            }

            // TAP TO ADD (Création "À la volée")
            // Touches 1, 2, 3... correspondent aux pistes 1, 2, 3...
            const laneNum = parseInt(e.key);
            if (!isNaN(laneNum) && laneNum >= 1 && laneNum <= totalLanes && !e.repeat && !e.ctrlKey && !e.metaKey && !e.altKey) {
                e.preventDefault();
                const laneIndex = laneNum - 1;
                const newId = crypto.randomUUID();
                const startTime = videoElement ? videoElement.currentTime : 0;

                useAppStore.getState().snapshotHistory();
                activeTapsRef.current.set(e.key, { id: newId, startTime });

                useAppStore.getState().addDialogue({
                    id: newId,
                    text: "...",
                    characterName: "Nouveau",
                    color: '#2563eb', // Default blue color
                    lane: laneIndex,
                    startTime: startTime,
                    duration: 0.1 // Minimum temporaire, sera étendu au keyup
                });
                selectBlock(newId);
                return;
            }
        };

        const handleKeyUp = (e: KeyboardEvent) => {
            const tapSession = activeTapsRef.current.get(e.key);
            if (tapSession) {
                e.preventDefault();
                const endTime = videoElement ? videoElement.currentTime : tapSession.startTime + 0.1;
                const duration = Math.max(0.1, endTime - tapSession.startTime);

                // Mettre à jour la durée exacte du bloc à la fin du maintien de la touche
                const dialogues = useAppStore.getState().dialogues;
                const updatedDialogues = dialogues.map(d => d.id === tapSession.id ? { ...d, duration } : d);
                useAppStore.getState().setDialogues(updatedDialogues);

                activeTapsRef.current.delete(e.key);
            }
        };

        window.addEventListener('keydown', handleKeyDown, { capture: true });
        window.addEventListener('keyup', handleKeyUp, { capture: true });
        return () => {
            window.removeEventListener('keydown', handleKeyDown, { capture: true });
            window.removeEventListener('keyup', handleKeyUp, { capture: true });
        };
    }, [videoElement, setIsPlaying, isExportingVideo, undo, redo, fps, selectedBlockIds, deleteDialogues, selectBlock, totalLanes]);

    // NATIVE: Block default context menu (right-click)
    useEffect(() => {
        const handleContextMenu = (e: MouseEvent) => {
            // Allow context menu only in development for debugging, or on inputs if needed
            // @ts-ignore
            if (!import.meta.env.DEV) {
                e.preventDefault();
            }
        };
        document.addEventListener('contextmenu', handleContextMenu);
        return () => document.removeEventListener('contextmenu', handleContextMenu);
    }, []);

    // NATIVE: Block browser reload shortcuts (F5, Ctrl+R)
    useEffect(() => {
        const handleSystemKeys = (e: KeyboardEvent) => {
            if (
                e.key === 'F5' ||
                (e.ctrlKey && e.key === 'r') ||
                (e.metaKey && e.key === 'r')
            ) {
                e.preventDefault();
            }
            // F11 — toggle Mode Présentation
            if (e.key === 'F11') {
                e.preventDefault();
                setIsPresentationMode(prev => !prev);
            }
            // Escape — sortir du Mode Présentation
            if (e.key === 'Escape') {
                setIsPresentationMode(false);
            }
        };
        window.addEventListener('keydown', handleSystemKeys);
        return () => window.removeEventListener('keydown', handleSystemKeys);
    }, []);

    // ResizeObserver pour la bande rythmo du Mode Présentation
    useEffect(() => {
        const container = presentationBandContainerRef.current;
        if (!container || !isPresentationMode) return;
        let rafId: number | null = null;
        const ro = new ResizeObserver((entries) => {
            for (const entry of entries) {
                if (rafId !== null) cancelAnimationFrame(rafId);
                rafId = requestAnimationFrame(() => {
                    setPresentationBandDims({
                        width: entry.contentRect.width,
                        height: Math.max(entry.contentRect.height, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight)),
                    });
                    rafId = null;
                });
            }
        });
        ro.observe(container);
        // Initialisation immédiate
        setPresentationBandDims({
            width: container.clientWidth,
            height: Math.max(container.clientHeight, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight)),
        });
        return () => ro.disconnect();
    }, [isPresentationMode, totalLanes, globalLaneHeight, laneHeights]);


    // --- File Actions ---


    const handleNewProject = () => {
        showConfirm(
            "Créer un nouveau projet ?",
            "Les données non sauvegardées seront perdues. Voulez-vous continuer ?",
            () => {
                resetProject();
                setIsFileMenuOpen(false);
            }
        );
    };

    const handleDeleteProject = () => {
        showConfirm(
            "Supprimer le projet actuel ?",
            "Cela fermera le projet et supprimera la vidéo proxy de la mémoire de l'ordinateur afin de libérer de l'espace. Le fichier vidéo original et le fichier de sauvegarde (.json) ne seront pas supprimés.",
            async () => {
                if (videoPath) {
                    try {
                        await invoke('delete_proxy_video', { videoPath });
                        console.log("Proxy vidéo supprimé.");
                    } catch (e) {
                        console.error("Erreur lors de la suppression du proxy: ", e);
                    }
                }
                resetProject();
                setIsFileMenuOpen(false);
            }
        );
    };

    const handleSaveProject = async () => {
        const projectData = {
            version: "1.0",
            timestamp: Date.now(),
            dialogues,
            totalLanes,
            syncOffset,
            zoomLevel,
            fps,
            videoPath: videoPath || null
        };
        try {
            const filePath = await save({
                filters: [{ name: 'RhythmoSync Project', extensions: ['rsp', 'json'] }],
                defaultPath: 'projet-rhythmosync.rsp'
            });
            if (filePath) {
                await writeTextFile(filePath, JSON.stringify(projectData, null, 2));
                // Optional: show quick feedback
            }
        } catch (error) {
            console.error("Save error:", error);
            alert("Erreur lors de la sauvegarde.");
        }
        setIsFileMenuOpen(false);
    };

    const triggerOpenProject = async () => {
        try {
            const selected = await open({
                multiple: false,
                filters: [{ name: 'RhythmoSync Project', extensions: ['rsp', 'json'] }]
            });
            if (selected) {
                const content = await readTextFile(selected as string);
                try {
                    const json = JSON.parse(content);
                    if (json.version && Array.isArray(json.dialogues)) {
                        importProject(json as Partial<AppState>);

                        // Attempt to load associated video
                        if (json.videoPath) {
                            try {
                                const assetUrl = convertFileSrc(json.videoPath);
                                setVideoSource(assetUrl, json.videoPath);
                                showAlert("Succès", "Projet et vidéo chargés avec succès.");
                            } catch (videoError) {
                                console.warn("Could not load linked video:", videoError);
                                showAlert("Attention", "Le projet a été chargé, mais la vidéo associée (" + json.videoPath + ") est introuvable ou inaccessible.");
                            }
                        } else {
                            showAlert("Succès", "Projet chargé (sans vidéo associée).");
                        }
                    } else {
                        alert("Fichier invalide.");
                    }
                } catch (e) {
                    alert("Erreur de lecture JSON.");
                }
            }
        } catch (error) {
            console.error(error);
        }
        setIsFileMenuOpen(false);
    };

    const triggerImportVideo = async () => {
        try {
            const selected = await open({
                multiple: false,
                filters: [{ name: 'Vidéo', extensions: ['mp4', 'webm', 'mov', 'mkv', 'avi', 'm4v', 'ts', 'mts', 'flv', '3gp'] }]
            });
            if (selected) {
                const filePath = selected as string;
                const assetUrl = convertFileSrc(filePath);

                // ————————————————————————————————————————————————————————————————————————————————
                // Interroger FFmpeg pour connaître le codec et décider si
                // un proxy est nécessaire pour la lecture dans la WebView.
                let videoInfo: { codec_name: string; container: string; width: number; height: number; duration: number; needs_proxy: boolean; reason: string } | null = null;
                const ffmpegReady: boolean = await invoke<boolean>('check_ffmpeg').catch(() => false);

                if (ffmpegReady) {
                    try {
                        videoInfo = await invoke('get_video_info', { videoPath: filePath });
                    } catch (e) {
                        console.warn('[VideoInfo] Impossible de sonder la vidéo:', e);
                    }
                }

                const needsProxyForCompat = videoInfo?.needs_proxy ?? false;
                const compatReason = videoInfo?.reason ?? '';
                const containerIsNative = !needsProxyForCompat;

                if (needsProxyForCompat) {
                    // Format non natif : charger le proxy AVANT d'afficher la vidéo
                    // pour éviter une erreur de lecture immédiate
                    console.log(`[Import Vidéo] Format non natif détecté (${videoInfo?.container}/${videoInfo?.codec_name}). Proxy requis : ${compatReason}`);

                    if (ffmpegReady) {
                        // Indiquer à l'utilisateur que la vidéo doit être convertie
                        setVideoSource(assetUrl, filePath); // Set le path pour les métadonnées
                        setProxyStatus('encoding');

                        invoke<string>('generate_proxy_video', { videoPath: filePath })
                            .then((proxyPath) => {
                                const proxyAssetUrl = convertFileSrc(proxyPath);
                                setProxyVideoSource(proxyAssetUrl);
                                setProxyStatus('ready');
                                setUseProxy(true); // Forcer le proxy pour ce format
                            })
                            .catch((err) => {
                                console.warn('[Proxy] Échec de génération proxy:', err);
                                setProxyStatus('error');
                                showAlert('Erreur de compatibilité', `La vidéo (${videoInfo?.container?.toUpperCase()}) ne peut pas être lue directement et la conversion a échoué.\n\nErreur : ${err}`);
                            });
                    } else {
                        // Pas de FFmpeg : prévenir l'utilisateur
                        showAlert(
                            `Format vidéo non supporté (${(videoInfo?.container ?? 'inconnu').toUpperCase()})`,
                            `Ce format (${videoInfo?.container?.toUpperCase()} / ${videoInfo?.codec_name?.toUpperCase()}) n'est pas lisible nativement.\n\nPour l'ouvrir, veuillez d'abord télécharger FFmpeg via Fichier > Télécharger FFmpeg, qui permettra la conversion automatique.`
                        );
                        setVideoSource(assetUrl, filePath);
                    }
                } else {
                    // Format natif : lecture directe
                    setVideoSource(assetUrl, filePath);

                    // Proxy optionnel pour le seek rapide (en arrière-plan)
                    if (ffmpegReady) {
                        setProxyStatus('encoding');
                        invoke<string>('generate_proxy_video', { videoPath: filePath })
                            .then((proxyPath) => {
                                const proxyAssetUrl = convertFileSrc(proxyPath);
                                setProxyVideoSource(proxyAssetUrl);
                                setProxyStatus('ready');
                                setUseProxy(true);
                            })
                            .catch((err) => {
                                console.warn('[Proxy] Échec de génération proxy:', err);
                                setProxyStatus('error');
                            });
                    }
                }
            }
        } catch (error) {
            console.error(error);
        }
        setIsFileMenuOpen(false);
    };

    const triggerImportSubtitle = async () => {
        setIsFileMenuOpen(false);
        try {
            const selected = await open({
                multiple: false,
                filters: [{ name: 'Sous-titres', extensions: ['srt', 'vtt'] }]
            });
            if (!selected) return;

            const path = selected as string;

            try {
                // Call the Rust command
                const newDialogues: any[] = await invoke('import_subtitles', { path });

                if (newDialogues.length === 0) {
                    showAlert("Erreur", "Fichier vide ou aucun sous-titre trouvé.");
                    return;
                }

                // Apply import directly — no nested showAlert inside confirm callback
                // @ts-ignore
                setDialogues(newDialogues);

                // Update total lanes based on imported data
                const maxLane = newDialogues.reduce((max: number, d: any) => Math.max(max, d.lane ?? 0), 0);
                useAppStore.getState().setTotalLanes(Math.max(maxLane + 1, 3));

                showAlert("Succès", `${newDialogues.length} sous-titres importés avec succès !`);

            } catch (error) {
                console.error("Importer Rust error:", error);
                showAlert("Erreur d'importation", String(error));
            }
        } catch (error) {
            console.error("Selection error:", error);
        }
    };





    const handleExportScene = () => {
        handleExportSRT();
        setIsFileMenuOpen(false);
    };

    // --- Export Actions ---

    const formatTimestamp = (seconds: number, separator: string = ',') => {
        const pad = (num: number, size: number) => num.toString().padStart(size, '0');
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        const ms = Math.floor((seconds % 1) * 1000);
        return `${pad(h, 2)}:${pad(m, 2)}:${pad(s, 2)}${separator}${pad(ms, 3)}`;
    };

    const downloadFile = (content: string, filename: string, type: string) => {
        const blob = new Blob([content], { type });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
        setIsExportMenuOpen(false);
    };

    const getSortedDialogues = () => [...dialogues].sort((a, b) => a.startTime - b.startTime);

    const handleExportSRT = () => {
        const sorted = getSortedDialogues();
        let content = "";
        sorted.forEach((d, index) => {
            const start = formatTimestamp(d.startTime, ",");
            const end = formatTimestamp(d.startTime + d.duration, ",");
            content += `${index + 1}\n${start} --> ${end}\n${d.text}\n\n`;
        });
        downloadFile(content, `subtitles.srt`, "text/plain");
    };

    const handleExportVTT = () => {
        const sorted = getSortedDialogues();
        let content = "WEBVTT\n\n";
        sorted.forEach((d) => {
            const start = formatTimestamp(d.startTime, ".");
            const end = formatTimestamp(d.startTime + d.duration, ".");
            content += `${start} --> ${end}\n${d.text}\n\n`;
        });
        downloadFile(content, `captions.vtt`, "text/vtt");
    };

    const handleExportCSV = () => {
        const sorted = getSortedDialogues();
        let content = "ID,StartTime,Duration,EndTime,Character,Text\n";
        sorted.forEach((d) => {
            const safeText = `"${d.text.replace(/"/g, '""')}"`;
            const safeChar = `"${d.characterName.replace(/"/g, '""')}"`;
            content += `${d.id},${d.startTime.toFixed(3)},${d.duration.toFixed(3)},${(d.startTime + d.duration).toFixed(3)},${safeChar},${safeText}\n`;
        });
        downloadFile(content, `dialogues.csv`, "text/csv");
    };

    const handleExportTXT = () => {
        const sorted = getSortedDialogues();
        let content = "";
        sorted.forEach((d) => {
            const start = formatTimestamp(d.startTime, ":").split(',')[0];
            content += `[${start}] ${d.characterName}: ${d.text}\n`;
        });
        downloadFile(content, `transcript.txt`, "text/plain");
    };

    // --- OFFLINE VIDEO EXPORT ---
    const handleExportOffline = async () => {
        setActiveModal('EXPORT_CONFIG');
        setIsExportMenuOpen(false);
        // Detect GPU encoder availability
        try {
            const info = await invoke<{ encoder: string; label: string; is_gpu: boolean }>('detect_gpu_encoder');
            setGpuEncoderInfo(info);
        } catch (e) {
            console.warn('GPU detection failed:', e);
            setGpuEncoderInfo({ encoder: 'libx264', label: 'CPU (libx264)', is_gpu: false });
        }
    };

    const startVideoExport = async () => {
        closeModal();
        if (!videoElement) {
            alert("Erreur: Vidéo introuvable (non chargée en mémoire).");
            return;
        }
        if (!videoPath) {
            alert("Erreur: Chemin vidéo manquant. Veuillez recharger la vidéo.");
            return;
        }

        setIsExportMenuOpen(false);
        setIsExportingVideo(true);
        isExportingRef.current = true;
        setExportProgress(0);
        setEstimatedTimeRemaining("Préparation...");

        // Listen for encoder info from Rust (during export start)
        const unlistenEncoder = await listen<{ encoder: string; label: string; is_gpu: boolean }>('export-encoder-info', (event) => {
            setGpuEncoderInfo(event.payload);
        });
        setExportFpsEffective(0);

        videoElement.pause();
        setIsPlaying(false);

        try {
            // --- VALIDATION ---
            if (videoElement.readyState < 1) {
                alert("Vidéo non prête. Veuillez charger une vidéo valide.");
                setIsExportingVideo(false);
                return;
            }

            const duration = videoElement.duration;
            if (!Number.isFinite(duration) || duration <= 0) {
                alert("Durée de vidéo invalide. Veuillez charger une vidéo.");
                setIsExportingVideo(false);
                return;
            }

            if (!Number.isFinite(fps) || fps <= 0) {
                throw new Error(`FPS invalide (${fps}).`);
            }

            if (videoElement.videoWidth === 0 || videoElement.videoHeight === 0) {
                throw new Error("Dimensions de la vidéo invalides (0x0).");
            }

            // --- CHECK/DOWNLOAD FFMPEG ---
            setEstimatedTimeRemaining("Vérification de FFmpeg...");
            let ffmpegReady = false;
            try {
                ffmpegReady = await invoke<boolean>('check_ffmpeg');
            } catch (e) {
                console.warn("FFmpeg check failed:", e);
            }

            if (!ffmpegReady) {
                setEstimatedTimeRemaining("Téléchargement de FFmpeg (~80MB)...");
                setFfmpegStatus('downloading');

                const unlisten = await listen<string>('ffmpeg-download-progress', (event) => {
                    setFfmpegMessage(event.payload);
                    setEstimatedTimeRemaining(event.payload);
                });

                try {
                    await invoke<string>('download_ffmpeg');
                    ffmpegReady = true;
                    setFfmpegStatus('ready');
                } catch (e) {
                    console.error("FFmpeg download failed:", e);
                    setFfmpegStatus('not_found');
                    alert("Impossible de télécharger FFmpeg. Vérifiez votre connexion internet.\n\nErreur: " + e);
                    return;
                } finally {
                    unlisten();
                }
            } else {
                setFfmpegStatus('ready');
            }

            // --- LETTERBOX DETECTION ---
            setEstimatedTimeRemaining("Détection du letterbox...");
            const detectTime = Math.min(duration * 0.1, 5);
            videoElement.currentTime = detectTime;
            await new Promise<void>(resolve => {
                videoElement.addEventListener('seeked', () => resolve(), { once: true });
            });

            const nativeW = videoElement.videoWidth;
            const nativeH = videoElement.videoHeight;
            const detectCanvas = document.createElement('canvas');
            detectCanvas.width = nativeW;
            detectCanvas.height = nativeH;
            const detectCtx = detectCanvas.getContext('2d')!;
            detectCtx.drawImage(videoElement, 0, 0, nativeW, nativeH);
            const imgData = detectCtx.getImageData(0, 0, nativeW, nativeH);
            const pxls = imgData.data;

            const BRIGHTNESS_THRESHOLD = 15;
            const SAMPLE_COLS = 20;
            const isRowBlack = (row: number): boolean => {
                let totalBrightness = 0;
                for (let s = 0; s < SAMPLE_COLS; s++) {
                    const col = Math.floor((s / SAMPLE_COLS) * nativeW);
                    const idx = (row * nativeW + col) * 4;
                    totalBrightness += pxls[idx] * 0.299 + pxls[idx + 1] * 0.587 + pxls[idx + 2] * 0.114;
                }
                return (totalBrightness / SAMPLE_COLS) < BRIGHTNESS_THRESHOLD;
            };

            let cropTop = 0;
            let cropBottom = nativeH;
            for (let y = 0; y < nativeH; y++) {
                if (!isRowBlack(y)) { cropTop = y; break; }
            }
            for (let y = nativeH - 1; y >= cropTop; y--) {
                if (!isRowBlack(y)) { cropBottom = y + 1; break; }
            }

            const croppedNativeHeight = cropBottom - cropTop;
            if (croppedNativeHeight < nativeH * 0.5) {
                console.warn(`Letterbox: cropped area too small (${croppedNativeHeight}/${nativeH}px). Disabling crop.`);
                cropTop = 0;
                cropBottom = nativeH;
            }
            const finalNativeHeight = cropBottom - cropTop;
            console.log(`Letterbox: cropTop=${cropTop}, cropBottom=${cropBottom}, content=${finalNativeHeight}px`);
            detectCanvas.remove();

            // --- DIMENSIONS ---
            const actualLanes = dialogues.length > 0 ? Math.max(...dialogues.map(d => d.lane)) + 1 : 1;
            const bandStripHeight = getTotalBandHeight(laneHeights, actualLanes, globalLaneHeight);

            let targetWidth = 1920;
            let targetHeight = 1080;
            
            // La demande utilisateur: "la bande rythmo soit de bonne qualité 1920x1080 même si on exporte la vidéo en 480p"
            // Cela implique que le conteneur final (et le canvas) doit toujours être 1920x1080 pour garder le texte net, 
            // et que le choix 480p / 720p ne doit impacter que le bitrate (la compression) de la vidéo.

            // L'export doit faire EXACTEMENT targetWidth x targetHeight
            const exportWidth = targetWidth % 2 === 0 ? targetWidth : targetWidth - 1;
            const exportHeight = targetHeight % 2 === 0 ? targetHeight : targetHeight - 1;

            // 1. Calcul de la hauteur idéale de la vidéo (mise à l'échelle de l'original sans déformation)
            const videoScaleFactor = exportWidth / nativeW;
            const idealVideoHeight = Math.round(finalNativeHeight * videoScaleFactor);

            // 2. Hauteur minimale que l'on veut garder pour la vidéo (ex: 40% de l'écran)
            const minVideoHeight = Math.round(exportHeight * 0.4);

            // 3. Hauteur idéale demandée pour la bande rythmo (selon le scale choisi par l'utilisateur)
            // L'échelle de base de l'utilisateur est relative à 1920px.
            const userLaneScale = (exportWidth / 1920) * exportConfig.bandScale;
            const idealBandHeight = Math.round(bandStripHeight * userLaneScale);

            let finalVideoRenderHeight: number;
            let finalBandRenderHeight: number;

            // On vérifie s'il y a de l'espace noir en bas (la vidéo ne remplit pas l'écran)
            if (idealVideoHeight + idealBandHeight <= exportHeight) {
                // Il y a "du noir en bas". L'utilisateur veut que ça soit "utilisé par la taille des pistes".
                // Donc la bande rythmo s'étend pour venir remplir PARFAITEMENT TOUT l'espace vide restant en bas !
                finalVideoRenderHeight = idealVideoHeight;
                finalBandRenderHeight = exportHeight - idealVideoHeight;
            } else {
                // Si la taille de bande + la vidéo dépasse la taille de l'écran (ex: bandScale est très large ou vidéo est très haute)
                // On limite la bande rythmo pour ne pas trop réduire la vidéo, mais elle prend l'espace du bas vers le haut.
                finalBandRenderHeight = Math.min(idealBandHeight, exportHeight - minVideoHeight);
                finalVideoRenderHeight = exportHeight - finalBandRenderHeight;
            }

            // On recalcule TOUTE l'échelle des éléments (band_scale final) pour qu'ils 
            // dessinent sur le canvas avec EXACTEMENT finalBandRenderHeight.
            // Cela permet aux pistes d'être physiquement "plus grandes" et d'occuper la place !
            const laneScale = finalBandRenderHeight / bandStripHeight;
            const scaledBandHeight = finalBandRenderHeight;

            if (exportWidth <= 0 || exportHeight <= 0) {
                throw new Error(`Dimensions d'export invalides: ${exportWidth}x${exportHeight}`);
            }

            // Mise à l'échelle horizontale (zoom) spécifique pour l'export pour que 
            // la rythmo reste proportionnelle à la largeur de l'écran et ne paraisse pas "zoomée".
            const exportZoomLevel = zoomLevel * laneScale;
            const exportSyncLineX = Math.round(SYNC_LINE_POSITION_X * laneScale);

            // --- ADAPTIVE BITRATE ---
            let adaptiveBitrate: number;
            if (exportConfig.resolution === '480p') {
                adaptiveBitrate = 2_500_000;
            } else if (exportConfig.resolution === '720p') {
                adaptiveBitrate = 5_000_000;
            } else {
                adaptiveBitrate = 8_000_000;
            }

            console.log(`🎬 Export FFmpeg natif: ${exportConfig.resolution}, ${duration.toFixed(1)}s, ${fps}fps, ${exportWidth}x${exportHeight}, bitrate=${(adaptiveBitrate / 1_000_000).toFixed(1)}Mbps`);

            // --- PRE-RENDER BAND STRIP ---
            setEstimatedTimeRemaining("Pré-rendu de la bande rythmo...");

            const bandStripWidth = Math.ceil(duration * exportZoomLevel);

            // Cap at 32000px wide (canvas limit)
            const MAX_STRIP_WIDTH = 32000;
            const effectiveStripWidth = Math.min(bandStripWidth, MAX_STRIP_WIDTH);

            const stripCanvas = document.createElement('canvas');
            stripCanvas.width = effectiveStripWidth;
            stripCanvas.height = scaledBandHeight; // On utilise la hauteur finale redimensionnée
            const stripCtx = stripCanvas.getContext('2d')!;

            // Background
            stripCtx.fillStyle = '#1a1a2e';
            stripCtx.fillRect(0, 0, effectiveStripWidth, scaledBandHeight);

            // Lane separators (dashed)
            stripCtx.strokeStyle = '#333';
            stripCtx.lineWidth = Math.max(1, Math.round(laneScale));
            stripCtx.setLineDash([5 * laneScale, 5 * laneScale]);
            for (let lane = 1; lane < actualLanes; lane++) {
                // Draw lane horizontal lines scaled
                const y = Math.round(getLaneY(laneHeights, lane, globalLaneHeight) * laneScale);
                stripCtx.beginPath();
                stripCtx.moveTo(0, y);
                stripCtx.lineTo(effectiveStripWidth, y);
                stripCtx.stroke();
            }
            stripCtx.setLineDash([]);

            // Borders
            stripCtx.strokeStyle = '#444';
            stripCtx.lineWidth = 1;
            stripCtx.beginPath();
            stripCtx.moveTo(0, 0); stripCtx.lineTo(effectiveStripWidth, 0);
            stripCtx.moveTo(0, scaledBandHeight); stripCtx.lineTo(effectiveStripWidth, scaledBandHeight);
            stripCtx.stroke();

            // Draw dialogue blocks onto the strip
            for (const block of dialogues) {
                const blockX = block.startTime * exportZoomLevel;
                const blockWidth = block.duration * exportZoomLevel;
                
                // Convert coords with scale
                const blockY = getLaneY(laneHeights, block.lane, globalLaneHeight) * laneScale;
                const laneHeightCurrent = getLaneHeight(laneHeights, block.lane, globalLaneHeight) * laneScale;
                const verticalPadding = 3 * laneScale;
                const blockHeight = laneHeightCurrent - (verticalPadding * 2);

                if (blockX + blockWidth < 0 || blockX > effectiveStripWidth) continue;

                // Background rect with rounded corners
                stripCtx.globalAlpha = 0.3;
                stripCtx.fillStyle = block.color;
                const r = 4 * laneScale;
                const bx = blockX, by = blockY + verticalPadding, bw = blockWidth, bh = blockHeight;
                stripCtx.beginPath();
                stripCtx.moveTo(bx + r, by);
                stripCtx.lineTo(bx + bw - r, by);
                stripCtx.quadraticCurveTo(bx + bw, by, bx + bw, by + r);
                stripCtx.lineTo(bx + bw, by + bh - r);
                stripCtx.quadraticCurveTo(bx + bw, by + bh, bx + bw - r, by + bh);
                stripCtx.lineTo(bx + r, by + bh);
                stripCtx.quadraticCurveTo(bx, by + bh, bx, by + bh - r);
                stripCtx.lineTo(bx, by + r);
                stripCtx.quadraticCurveTo(bx, by, bx + r, by);
                stripCtx.closePath();
                stripCtx.fill();
                stripCtx.globalAlpha = 1.0;

                // Border
                stripCtx.strokeStyle = block.color;
                stripCtx.lineWidth = 1;
                stripCtx.stroke();

                // Text (stretched to fit block width, proportional to height)
                stripCtx.save();
                stripCtx.fillStyle = '#ffffff';
                const scaledFontSize = Math.round(28 * laneScale);
                stripCtx.font = `bold ${scaledFontSize}px monospace`;
                stripCtx.textBaseline = 'middle';
                stripCtx.shadowColor = 'black';
                stripCtx.shadowBlur = 2 * laneScale;

                const textX = blockX + (5 * laneScale); 
                const textY = blockY + laneHeightCurrent / 2;
                const naturalWidth = stripCtx.measureText(block.text).width;
                
                if (naturalWidth > 0 && blockWidth > 0) {
                    stripCtx.save();
                    stripCtx.translate(blockX, textY);
                    // L'échelle horizontale reste identique car blockWidth et naturalWidth 
                    // sont tous deux multipliés par laneScale.
                    // Cela évite toute déformation supplémentaire du texte.
                    stripCtx.scale(blockWidth / naturalWidth, 1);
                    stripCtx.fillText(block.text, 0, 0);
                    stripCtx.restore();
                }
                stripCtx.restore();
            }

            // Convert to PNG and save to disk
            const stripBlob: Blob = await new Promise((resolve, reject) => {
                stripCanvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("Failed to create band strip")), 'image/png');
            });
            const stripBuffer = await stripBlob.arrayBuffer();
            const tempStripPath = videoPath.replace(/\.[^.]+$/, '_band_strip.png');
            await writeFile(tempStripPath, new Uint8Array(stripBuffer));
            console.log(`ðŸŽ¨ Band strip: ${effectiveStripWidth}x${scaledBandHeight}px (Scale: ${exportConfig.bandScale}) â†’ ${tempStripPath}`);
            stripCanvas.remove();

            // --- ASK SAVE PATH ---
            const videoName = videoPath ? videoPath.split(/[/\\]/).pop()?.replace(/\.[^.]+$/, '') || 'Untitled' : 'Untitled';
            const defaultName = `rhythmosync_master_${Date.now()}.mp4`;
            const savePath = await save({
                defaultPath: defaultName,
                filters: [{ name: 'Vidéo MP4', extensions: ['mp4'] }]
            });
            if (!savePath) {
                console.log('Export annulé par l\'utilisateur.');
                setIsExportingVideo(false);
                isExportingRef.current = false;
                return;
            }

            // --- LISTEN FOR PROGRESS ---
            const unlistenProgress = await listen<{ percent: number; fps_effective: number; estimated_remaining: string }>('export-progress', (event) => {
                setExportProgress(event.payload.percent);
                setExportFpsEffective(event.payload.fps_effective);
                setEstimatedTimeRemaining(event.payload.estimated_remaining);
            });

            // --- CALL RUST FFmpeg EXPORT ---
            setEstimatedTimeRemaining("Encodage en cours...");
            const exportDate = new Date().toISOString();

            try {
                const result = await invoke<string>('export_video_native', {
                    request: {
                        video_path: videoPath,
                        output_path: savePath,
                        fps,
                        bitrate: adaptiveBitrate,
                        band_strip_path: tempStripPath,
                        video_width: nativeW,
                        crop_top: cropTop,
                        crop_bottom: cropBottom,
                        export_width: exportWidth,
                        export_height: exportHeight,
                        video_render_height: finalVideoRenderHeight,
                        band_render_height: finalBandRenderHeight,
                        band_strip_height: scaledBandHeight,
                        pps: exportZoomLevel,
                        sync_offset: syncOffset - (SYNC_LINE_POSITION_X / zoomLevel), // Utilise le zoom original pour le temporel UI
                        duration,
                        sync_line_x: exportSyncLineX,
                        title: `${videoName} — RhythmoSync Master`,
                        comment: `Bande rythmo: ${actualLanes} piste(s), ${dialogues.length} bloc(s) — ${exportConfig.resolution} @ ${fps}fps`,
                        description: `Exporté par RhythmoSync Studio le ${exportDate}`,
                    }
                });

                console.log(`âœ… ${result}`);
                setExportProgress(100);
                setEstimatedTimeRemaining("Terminé !");

            } catch (exportErr) {
                console.error("Export FFmpeg error:", exportErr);
                alert("Erreur d'exportation FFmpeg: " + exportErr);
            } finally {
                unlistenProgress();
            }

        } catch (err) {
            console.error(err);
            alert("Erreur critique d'exportation: " + err);
        } finally {
            setIsExportingVideo(false);
            isExportingRef.current = false;
            videoElement.currentTime = 0;
            unlistenEncoder();
        }
    };






    // --- Tools Actions ---
    const openShiftTimeline = () => { setActiveModal('SHIFT'); setShiftAmount("0"); setIsEditMenuOpen(false); };
    const openFindReplace = () => { setActiveModal('FIND'); setFindText(""); setReplaceText(""); setIsEditMenuOpen(false); };
    const openStats = () => { setActiveModal('STATS'); setIsViewMenuOpen(false); };
    const openSettings = () => { setActiveModal('SETTINGS'); };
    const handleResetView = () => { setZoomLevel(DEFAULT_PPS); setCurrentTime(0); setGlobalLaneHeight(50); setIsViewMenuOpen(false); };
    
    const handleClearAllDialogues = () => {
        showConfirm(
            "Supprimer tous les sous-titres ?",
            "Cette action supprimera tous les blocs de dialogue de la timeline. Cette opération peut être annulée (Ctrl+Z).",
            () => {
                snapshotHistory();
                setDialogues([]);
                setIsEditMenuOpen(false);
            }
        );
    };

    const closeModal = () => { setActiveModal(null); };

    const applyShift = () => {
        const offset = parseFloat(shiftAmount);
        if (!isNaN(offset) && offset !== 0) shiftTimeline(offset);
        closeModal();
    };

    const applyFindReplace = () => {
        if (findText) globalFindReplace(findText, replaceText);
        closeModal();
    };

    // â”€â”€ UI Components pour le menu â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    const MenuButton = ({ label, isOpen, onClick, children }: any) => (
        <div className="relative">
            <button
                onClick={onClick}
                className="px-3 py-1.5 text-xs font-medium rounded-md select-none transition-all duration-150"
                style={isOpen
                    ? { background: 'var(--rs-bg-muted)', color: 'var(--rs-text-primary)' }
                    : { color: 'var(--rs-text-secondary)' }
                }
                onMouseEnter={(e) => { if (!isOpen) { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; (e.currentTarget as HTMLElement).style.background = 'var(--rs-bg-muted)'; } }}
                onMouseLeave={(e) => { if (!isOpen) { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; (e.currentTarget as HTMLElement).style.background = ''; } }}
            >
                {label}
            </button>
            {isOpen && (
                <div
                    className="absolute top-full left-0 mt-2 w-56 rounded-xl shadow-2xl py-1.5 z-50 animate-fade-in-down"
                    style={{
                        background: 'rgba(14,20,32,0.95)',
                        backdropFilter: 'blur(16px)',
                        WebkitBackdropFilter: 'blur(16px)',
                        border: '1px solid var(--rs-border)',
                    }}
                >
                    {children}
                </div>
            )}
        </div>
    );

    const MenuSlider = ({ label, icon: Icon, value, min, max, onChange, unit = "" }: any) => (
        <div className="px-3 py-2 text-xs flex flex-col gap-2 group rounded-md mx-1 mb-1 transition-all duration-100"
            style={{ width: 'calc(100% - 8px)', color: 'var(--rs-text-secondary)' }}
        >
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-3">
                    {Icon && <Icon size={14} style={{ opacity: 0.7 }} />}
                    <span className="font-medium text-white/80">{label}</span>
                </div>
                <span className="text-[10px] font-mono opacity-50 px-1.5 py-0.5 rounded bg-white/5 text-white/70">
                    {value}{unit}
                </span>
            </div>
            <div className="flex items-center gap-3 px-1">
                <input 
                    type="range" 
                    min={min} 
                    max={max} 
                    step={2}
                    value={value} 
                    onChange={(e) => onChange(parseInt(e.target.value))}
                    className="flex-1 h-1 bg-white/10 rounded-lg appearance-none cursor-pointer accent-blue-500 hover:accent-blue-400 transition-all"
                />
            </div>
        </div>
    );

    const MenuItem = ({ icon: Icon, label, onClick, shortcut, danger }: any) => (
        <button
            onClick={onClick}
            className="w-full text-left px-3 py-2 text-xs flex items-center justify-between group rounded-md mx-1 transition-all duration-100"
            style={{ width: 'calc(100% - 8px)', color: danger ? '#f87171' : 'var(--rs-text-secondary)' }}
            onMouseEnter={(e) => {
                if (danger) { (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.1)'; (e.currentTarget as HTMLElement).style.color = '#f87171'; }
                else { (e.currentTarget as HTMLElement).style.background = 'var(--rs-accent)'; (e.currentTarget as HTMLElement).style.color = '#fff'; }
            }}
            onMouseLeave={(e) => {
                (e.currentTarget as HTMLElement).style.background = '';
                (e.currentTarget as HTMLElement).style.color = danger ? '#f87171' : 'var(--rs-text-secondary)';
            }}
        >
            <div className="flex items-center gap-3">
                {Icon && <Icon size={13} style={{ opacity: 0.7 }} />}
                <span>{label}</span>
            </div>
            {shortcut && (
                <kbd className="text-[9px] font-mono opacity-50 px-1 py-0.5 rounded" style={{ background: 'var(--rs-bg-muted)', color: 'inherit' }}>
                    {shortcut}
                </kbd>
            )}
        </button>
    );

    const MenuDivider = () => (
        <div className="h-px my-1.5 mx-3" style={{ background: 'var(--rs-border)' }} />
    );

    return (
        <div
            className="flex flex-col h-screen overflow-hidden selection:text-white"
            style={{ background: 'var(--rs-bg-base)', color: 'var(--rs-text-primary)', '--tw-selection-color': 'var(--rs-accent)' } as any}
        >


            {/* â”€â”€ EXPORT OVERLAY â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
            {isExportingVideo && (
                <div
                    className="fixed inset-0 z-[100] flex flex-col items-center justify-center"
                    style={{ background: 'rgba(8,12,20,0.96)', backdropFilter: 'blur(12px)' }}
                >
                    <div
                        className="rounded-2xl p-8 flex flex-col items-center gap-6 w-80"
                        style={{ background: 'var(--rs-bg-elevated)', border: '1px solid var(--rs-border)' }}
                    >
                        {/* Spinning icon */}
                        <div
                            className="w-16 h-16 rounded-2xl flex items-center justify-center"
                            style={{ background: 'linear-gradient(135deg, var(--rs-accent), #8b5cf6)', boxShadow: '0 0 24px rgba(99,102,241,0.4)' }}
                        >
                            <Film size={28} className="text-white" style={{ animation: 'rs-spin-slow 3s linear infinite' }} />
                        </div>
                        <div className="text-center">
                            <h2 className="text-lg font-bold tracking-tight mb-1" style={{ color: 'var(--rs-text-primary)' }}>Exportation Master</h2>
                            {gpuEncoderInfo && (
                                <div className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[10px] font-bold mb-1 ${gpuEncoderInfo.is_gpu ? 'bg-green-500/15 text-green-400 border border-green-500/30' : 'bg-neutral-800 text-neutral-400 border border-neutral-700'}`}>
                                    {gpuEncoderInfo.is_gpu ? <Zap size={10} /> : <Cpu size={10} />}
                                    {gpuEncoderInfo.label}
                                </div>
                            )}
                            {estimatedTimeRemaining && (
                                <p className="text-xs font-mono" style={{ color: 'var(--rs-text-muted)' }}>{estimatedTimeRemaining}</p>
                            )}
                        </div>
                        {/* Progress bar */}
                        <div className="w-full">
                            <div className="flex justify-between text-[10px] mb-2" style={{ color: 'var(--rs-text-muted)' }}>
                                <span>Rendu FFmpeg</span>
                                <span className="font-mono font-bold" style={{ color: 'var(--rs-text-secondary)' }}>{exportProgress}%</span>
                            </div>
                            <div className="w-full h-1.5 rounded-full overflow-hidden" style={{ background: 'var(--rs-bg-muted)' }}>
                                <div
                                    className="h-full rounded-full transition-all duration-300"
                                    style={{
                                        width: `${exportProgress}%`,
                                        background: 'linear-gradient(90deg, var(--rs-accent), #8b5cf6)',
                                        boxShadow: '0 0 8px rgba(99,102,241,0.5)',
                                    }}
                                />
                            </div>
                        </div>
                        <p className="text-xs" style={{ color: 'var(--rs-text-muted)' }}>Ne fermez pas cette fenêtre.</p>
                    </div>
                </div>
            )}



            {/* --- MODALS (Simplified for brevity, same logic) --- */}
            {activeModal && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm" onClick={closeModal}>
                    <div className={`bg-neutral-900 border border-neutral-800 p-6 rounded-xl shadow-2xl animate-in fade-in zoom-in duration-200 ${activeModal === 'SETTINGS' ? 'w-[450px]' : 'w-96'}`} onClick={e => e.stopPropagation()}>
                        {/* Header */}
                        <div className="flex justify-between items-start mb-6">
                            <h3 className="text-base font-semibold flex items-center gap-2 text-white">
                                {activeModal === 'SHIFT' && 'Décaler la Timeline'}
                                {activeModal === 'FIND' && 'Rechercher & Remplacer'}
                                {activeModal === 'STATS' && 'Statistiques du Projet'}
                                {activeModal === 'SETTINGS' && 'Préférences'}
                                {activeModal === 'EXPORT_CONFIG' && 'Configuration Exportation'}
                            </h3>
                            <button onClick={closeModal} className="text-neutral-500 hover:text-white transition-colors"><X size={16} /></button>
                        </div>

                        {/* Modal Contents based on activeModal (Kept logic same, just styled) */}
                        {activeModal === 'SHIFT' && (
                            <>
                                <div className="mb-6">
                                    <label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Décalage (secondes)</label>
                                    <input type="number" value={shiftAmount} onChange={e => setShiftAmount(e.target.value)}
                                        className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2.5 text-sm text-white focus:border-blue-500 focus:outline-none" step="0.1" autoFocus />
                                </div>
                                <div className="flex justify-end gap-2">
                                    <button onClick={applyShift} className="px-4 py-2 bg-white text-black hover:bg-neutral-200 rounded-md text-xs font-bold transition-colors">Appliquer</button>
                                </div>
                            </>
                        )}
                        {/* Find/Replace */}
                        {activeModal === 'FIND' && (
                            <>
                                <div className="space-y-4 mb-6">
                                    <div><label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Rechercher</label>
                                        <input type="text" value={findText} onChange={e => setFindText(e.target.value)} className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2.5 text-sm text-white focus:border-blue-500 focus:outline-none" autoFocus /></div>
                                    <div><label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Remplacer par</label>
                                        <input type="text" value={replaceText} onChange={e => setReplaceText(e.target.value)} className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2.5 text-sm text-white focus:border-blue-500 focus:outline-none" /></div>
                                </div>
                                <div className="flex justify-end gap-2">
                                    <button onClick={applyFindReplace} className="px-4 py-2 bg-white text-black hover:bg-neutral-200 rounded-md text-xs font-bold transition-colors">Remplacer Tout</button>
                                </div>
                            </>
                        )}
                        {/* Stats */}
                        {activeModal === 'STATS' && (
                            <div className="space-y-1 mb-6">
                                <div className="flex justify-between py-2 border-b border-neutral-800"><span className="text-neutral-400 text-sm">Blocs</span><span className="font-mono text-white">{dialogues.length}</span></div>
                                <div className="flex justify-between py-2 border-b border-neutral-800"><span className="text-neutral-400 text-sm">Durée</span><span className="font-mono text-white">{dialogues.reduce((acc, d) => acc + d.duration, 0).toFixed(2)}s</span></div>
                            </div>
                        )}
                        {/* Settings */}
                        {activeModal === 'SETTINGS' && (
                            <div className="flex flex-col">
                                <div className="flex gap-4 border-b border-neutral-800 mb-6">
                                    <button onClick={() => setSettingsTab('GENERAL')} className={`pb-2 text-xs font-medium border-b-2 transition-colors ${settingsTab === 'GENERAL' ? 'text-white border-blue-500' : 'text-neutral-500 border-transparent'}`}>Général</button>
                                    <button onClick={() => setSettingsTab('SHORTCUTS')} className={`pb-2 text-xs font-medium border-b-2 transition-colors ${settingsTab === 'SHORTCUTS' ? 'text-white border-blue-500' : 'text-neutral-500 border-transparent'}`}>Raccourcis</button>
                                </div>
                                {settingsTab === 'GENERAL' && (
                                    <div className="space-y-4">
                                        <div>
                                            <label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Framerate</label>
                                            <select value={fps} onChange={(e) => setFps(Number(e.target.value))} className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2 text-sm text-white focus:border-blue-500 focus:outline-none">
                                                {[24, 25, 30, 60].map(v => <option key={v} value={v}>{v} FPS</option>)}
                                            </select>
                                        </div>
                                        <div>
                                            <label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Durée par défaut (s)</label>
                                            <input type="number" step={0.1} value={defaultBlockDuration} onChange={(e) => setDefaultBlockDuration(Number(e.target.value))} className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2 text-sm text-white focus:border-blue-500 focus:outline-none" />
                                        </div>
                                        <div>
                                            <label className="text-[10px] text-neutral-500 uppercase font-bold mb-2 block">Sync Offset (ms)</label>
                                            <input type="number" step={10} value={syncOffset * 1000} onChange={(e) => setSyncOffset(Number(e.target.value) / 1000)} className="w-full bg-neutral-950 border border-neutral-800 rounded-md p-2 text-sm text-white focus:border-blue-500 focus:outline-none" />
                                        </div>
                                    </div>
                                )}
                                {settingsTab === 'SHORTCUTS' && (
                                    <div className="max-h-60 overflow-y-auto space-y-2 text-xs">
                                        {[
                                            { k: "Ctrl+Z", d: "Annuler" }, { k: "Space", d: "Play/Pause" },
                                            { k: "Arrows", d: "Navigation" }, { k: "Shift+Arr", d: "Navigation Rapide" }
                                        ].map((s, i) => (
                                            <div key={i} className="flex justify-between py-2 border-b border-neutral-800/50">
                                                <span className="text-neutral-400">{s.d}</span>
                                                <kbd className="font-mono bg-neutral-800 px-1.5 py-0.5 rounded text-neutral-300">{s.k}</kbd>
                                            </div>
                                        ))}
                                    </div>
                                )}
                                <div className="flex justify-end gap-3 mt-6 pt-4 border-t border-neutral-800">
                                    <button onClick={closeModal} className="px-4 py-2 bg-blue-600 hover:bg-blue-500 rounded-md text-white text-xs font-bold transition-colors">Enregistrer</button>
                                </div>
                            </div>
                        )}

                        {/* EXPORT CONFIG MODAL */}
                        {activeModal === 'EXPORT_CONFIG' && (
                            <div className="space-y-6">
                                {/* GPU Encoder Info Badge */}
                                {gpuEncoderInfo && (
                                    <div className={`flex items-center gap-2 px-3 py-2.5 rounded-lg border text-xs ${gpuEncoderInfo.is_gpu ? 'bg-green-500/10 border-green-500/30 text-green-300' : 'bg-neutral-800/50 border-neutral-700/50 text-neutral-400'}`}>
                                        {gpuEncoderInfo.is_gpu ? <Zap size={14} className="text-green-400" /> : <Cpu size={14} className="text-neutral-500" />}
                                        <div>
                                            <span className="font-bold">{gpuEncoderInfo.is_gpu ? 'Encodage GPU' : 'Encodage CPU'}</span>
                                            <span className="ml-2 font-mono opacity-70">{gpuEncoderInfo.encoder}</span>
                                        </div>
                                    </div>
                                )}
                                <div className="space-y-5">
                                    <div>
                                        <label className="text-xs font-semibold text-neutral-300 uppercase tracking-widest mb-3 flex items-center gap-2">
                                            <Film size={14} className="text-blue-500" />
                                            Résolution de Sortie
                                        </label>
                                        <div className="grid grid-cols-2 gap-3">
                                            {[
                                                { id: 'SOURCE', label: 'Source', sub: 'Originale' },
                                                { id: '1080p', label: 'Full HD', sub: '1080p' },
                                                { id: '720p', label: 'HD', sub: '720p' },
                                                { id: '480p', label: 'SD', sub: '480p' }
                                            ].map((opt) => (
                                                <button
                                                    key={opt.id}
                                                    onClick={() => setExportConfig({ ...exportConfig, resolution: opt.id as any })}
                                                    className={`relative flex flex-col items-center justify-center py-4 px-2 rounded-xl border transition-all duration-200 overflow-hidden group
                                                    ${exportConfig.resolution === opt.id
                                                            ? 'bg-blue-600/10 border-blue-500 text-white shadow-[0_0_20px_rgba(59,130,246,0.15)] ring-1 ring-blue-500/50'
                                                            : 'bg-neutral-900/50 border-neutral-800/80 text-neutral-400 hover:bg-neutral-800 hover:border-neutral-600 hover:text-neutral-200'}`}
                                                >
                                                    {exportConfig.resolution === opt.id && (
                                                        <div className="absolute inset-0 bg-gradient-to-br from-blue-500/10 to-purple-500/10 pointer-events-none" />
                                                    )}
                                                    <span className="font-bold text-sm mb-1 z-10">{opt.label}</span>
                                                    <span className={`text-[10px] font-mono z-10 ${exportConfig.resolution === opt.id ? 'text-blue-300' : 'text-neutral-500 group-hover:text-neutral-400'}`}>{opt.sub}</span>
                                                </button>
                                            ))}
                                        </div>
                                    </div>

                                    <div>
                                        <div className="flex justify-between items-center mb-3">
                                            <label className="text-xs font-semibold text-neutral-300 uppercase tracking-widest flex items-center gap-2">
                                                <LayoutTemplate size={14} className="text-purple-500" />
                                                Taille de la Bande
                                            </label>
                                            <span className="text-[10px] font-mono text-purple-400 bg-purple-500/10 px-2 py-0.5 rounded-full border border-purple-500/20">
                                                {(exportConfig.bandScale * 100).toFixed(0)}%
                                            </span>
                                        </div>
                                        <input 
                                            type="range" 
                                            min="0.5" 
                                            max="2.0" 
                                            step="0.1" 
                                            value={exportConfig.bandScale} 
                                            onChange={(e) => setExportConfig({ ...exportConfig, bandScale: parseFloat(e.target.value) })}
                                            className="w-full h-1.5 bg-neutral-800 rounded-lg appearance-none cursor-pointer accent-blue-500"
                                        />
                                        <div className="flex justify-between mt-1 px-0.5">
                                            <span className="text-[9px] text-neutral-600">Petit</span>
                                            <span className="text-[9px] text-neutral-600">Standard</span>
                                            <span className="text-[9px] text-neutral-600">Large</span>
                                        </div>
                                    </div>

                                    {/* Preview Window */}
                                    <div className="space-y-3 pt-2">
                                        <label className="text-xs font-semibold text-neutral-300 uppercase tracking-widest flex items-center gap-2">
                                            <Search size={14} className="text-green-500" />
                                            Aperçu du Rendu
                                        </label>
                                        <div 
                                            className="w-full aspect-[16/10] rounded-xl border border-neutral-700 bg-black overflow-hidden relative shadow-2xl group"
                                            style={{ boxShadow: '0 10px 30px -10px rgba(0,0,0,0.5)' }}
                                        >
                                            {/* Video Portion Placeholder */}
                                            <div 
                                                className="absolute inset-0 bg-neutral-900 flex items-center justify-center overflow-hidden"
                                                style={{ height: `${100 / (1 + (exportConfig.bandScale * 0.3))}%` }}
                                            >
                                                <div className="relative w-full h-full flex items-center justify-center bg-neutral-900/50">
                                                    <Film size={32} className="text-white/10" />
                                                    <div className="absolute inset-0 flex items-center justify-center">
                                                        <div className="w-16 h-0.5 bg-white/5 rotate-45 absolute" />
                                                        <div className="w-16 h-0.5 bg-white/5 -rotate-45 absolute" />
                                                    </div>
                                                </div>
                                                <div className="absolute top-2 left-2 px-2 py-0.5 rounded bg-black/60 text-[8px] text-white/50 font-mono">IMAGE SOURCE</div>
                                            </div>

                                            {/* Band Portion */}
                                            <div 
                                                className="absolute bottom-0 left-0 right-0 border-t border-white/5 bg-[#1a1a2e] overflow-hidden flex items-center px-4"
                                                style={{ height: `${100 - (100 / (1 + (exportConfig.bandScale * 0.3)))}%` }}
                                            >
                                                {/* Simulated Dialogue Block */}
                                                <div 
                                                    className="bg-blue-600/30 border border-blue-400/50 rounded flex items-center justify-center relative overflow-hidden"
                                                    style={{ 
                                                        height: '60%', 
                                                        width: '50%',
                                                        marginLeft: '10%'
                                                    }}
                                                >
                                                    <span 
                                                        className="font-mono font-bold text-white whitespace-nowrap"
                                                        style={{ fontSize: `${Math.max(8, 12 * exportConfig.bandScale)}px` }}
                                                    >
                                                        TEXTE RYTHMO
                                                    </span>
                                                    {/* Sync line indicator */}
                                                    <div className="absolute top-0 bottom-0 left-[20%] w-[1px] bg-red-500/80 shadow-[0_0_4px_rgba(239,68,68,0.5)]" />
                                                </div>
                                                
                                                <div className="absolute top-2 left-2 px-2 py-0.5 rounded bg-black/60 text-[8px] text-white/50 font-mono text-center">Bande Rythmo ({(exportConfig.bandScale * 100).toFixed(0)}%)</div>
                                            </div>
                                        </div>
                                    </div>
                                </div>
                                <div className="flex justify-end gap-3 border-t border-neutral-800/80 pt-5">
                                    <button onClick={closeModal} className="px-5 py-2 hover:bg-neutral-800 text-neutral-300 rounded-lg text-xs font-bold transition-colors">
                                        Annuler
                                    </button>
                                    <button onClick={startVideoExport} className="px-6 py-2 bg-gradient-to-r from-blue-600 to-indigo-600 hover:from-blue-500 hover:to-indigo-500 text-white rounded-lg text-xs font-bold transition-all shadow-lg shadow-blue-900/20 flex items-center gap-2 group">
                                        <Download size={14} className="group-hover:-translate-y-0.5 transition-transform" />
                                        Lancer l'Export
                                    </button>
                                </div>
                            </div>
                        )}
                    </div>
                </div>
            )}

            {/* â”€â”€ WHISPER AI PANEL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
            {isWhisperPanelOpen && (
                <WhisperPanel onClose={() => setIsWhisperPanelOpen(false)} />
            )}

            {/* â”€â”€ HISTORY PANEL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
            {isHistoryPanelOpen && (
                <HistoryPanel onClose={() => setIsHistoryPanelOpen(false)} />
            )}

            {/* --- ALERT / CONFIRM MODAL --- */}
            {alertConfig.isOpen && (
                <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 backdrop-blur-sm" onClick={alertConfig.type === 'info' ? closeAlert : undefined}>
                    <div className="bg-neutral-900 border border-neutral-800 p-6 rounded-xl shadow-2xl animate-in fade-in zoom-in duration-200 w-96" onClick={e => e.stopPropagation()}>
                        <div className="mb-4">
                            <h3 className="text-lg font-bold text-white mb-2">{alertConfig.title}</h3>
                            <p className="text-sm text-neutral-400 leading-relaxed">{alertConfig.message}</p>
                        </div>
                        <div className="flex justify-end gap-3">
                            {alertConfig.type === 'confirm' && (
                                <button
                                    onClick={closeAlert}
                                    className="px-4 py-2 bg-neutral-800 hover:bg-neutral-700 text-white rounded-md text-xs font-bold transition-colors"
                                >
                                    Annuler
                                </button>
                            )}
                            <button
                                onClick={handleAlertConfirm}
                                className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-md text-xs font-bold transition-colors"
                            >
                                {alertConfig.type === 'confirm' ? 'Confirmer' : 'OK'}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* â”€â”€ HEADER BAR â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
            <header
                className="h-12 flex items-center px-4 justify-between flex-none z-50 select-none"
                style={{
                    background: 'var(--rs-bg-surface)',
                    borderBottom: '1px solid var(--rs-border)',
                    boxShadow: '0 1px 0 rgba(0,0,0,0.4)',
                }}
            >
                <div className="flex items-center gap-5">
                    {/* Logo */}
                    <div className="flex items-center gap-2.5 cursor-default">
                        <div
                            className="w-7 h-7 rounded-lg flex items-center justify-center text-white flex-none"
                            style={{
                                background: 'linear-gradient(135deg, #6366f1 0%, #8b5cf6 100%)',
                                boxShadow: '0 0 12px rgba(99,102,241,0.4), inset 0 1px 0 rgba(255,255,255,0.15)',
                            }}
                        >
                            <Film size={14} strokeWidth={2.5} />
                        </div>
                        <span className="text-sm font-bold tracking-tight" style={{ color: 'var(--rs-text-primary)' }}>
                            Rhythmo<span style={{ color: 'var(--rs-text-muted)', fontWeight: 400 }}>Sync</span>
                        </span>
                    </div>

                    {/* Nav menu */}
                    <nav
                        className="flex items-center gap-1 pl-4 h-6"
                        style={{ borderLeft: '1px solid var(--rs-border)' }}
                    >
                        <div ref={fileMenuRef}>
                            <MenuButton label="Fichier" isOpen={isFileMenuOpen} onClick={() => setIsFileMenuOpen(!isFileMenuOpen)}>
                                <MenuItem icon={PlusCircle} label="Nouveau Projet" onClick={handleNewProject} />
                                <MenuItem icon={X} label="Fermer / Nettoyer projet" onClick={handleDeleteProject} danger={true} />
                                <MenuItem icon={FolderOpen} label="Ouvrir..." onClick={triggerOpenProject} />
                                <MenuItem icon={Save} label="Enregistrer" onClick={handleSaveProject} shortcut="Ctrl+S" />
                                <MenuDivider />
                                <MenuItem icon={FileVideo} label="Importer Vidéo" onClick={triggerImportVideo} />
                                <MenuItem icon={Captions} label="Importer Sous-titres" onClick={triggerImportSubtitle} />
                                <MenuDivider />
                                <MenuItem icon={Download} label="Export Rapide" onClick={handleExportScene} />
                            </MenuButton>
                        </div>
                        <div ref={editMenuRef}>
                            <MenuButton label="Édition" isOpen={isEditMenuOpen} onClick={() => setIsEditMenuOpen(!isEditMenuOpen)}>
                                <MenuItem icon={RotateCcw} label="Annuler" onClick={undo} shortcut="Ctrl+Z" />
                                <MenuItem icon={RotateCcw} label="Rétablir" onClick={redo} shortcut="Ctrl+Y" />
                                <MenuDivider />
                                <MenuItem icon={Move} label="Décaler Timeline" onClick={openShiftTimeline} />
                                <MenuItem icon={Search} label="Rechercher/Remplacer" onClick={openFindReplace} />
                                <MenuDivider />
                                <MenuItem icon={Wand2} label="Alignement IA (Whisper)" onClick={() => { setIsWhisperPanelOpen(true); setIsEditMenuOpen(false); }} />
                                <MenuItem icon={History} label="Historique Undo/Redo" onClick={() => { setIsHistoryPanelOpen(true); setIsEditMenuOpen(false); }} shortcut="Ctrl+H" />
                                <MenuDivider />
                                <MenuItem icon={X} label="Tout supprimer" onClick={handleClearAllDialogues} danger />
                            </MenuButton>
                        </div>
                        <div ref={viewMenuRef}>
                            <MenuButton label="Affichage" isOpen={isViewMenuOpen} onClick={() => setIsViewMenuOpen(!isViewMenuOpen)}>
                                <MenuItem icon={RotateCcw} label="Réinitialiser Vue" onClick={handleResetView} />
                                <MenuDivider />
                                <div className="px-3 py-1 text-[9px] font-bold uppercase tracking-widest text-white/30">Hauteur des pistes</div>
                                <MenuSlider  
                                    icon={Maximize2}
                                    label="Échelle Verticale" 
                                    min={30} 
                                    max={150} 
                                    value={globalLaneHeight} 
                                    onChange={setGlobalLaneHeight}
                                    unit="px"
                                />
                                <MenuDivider />
                                <MenuItem icon={BarChart} label="Statistiques" onClick={openStats} />
                            </MenuButton>
                        </div>
                        <div ref={exportMenuRef}>
                            <MenuButton label="Export" isOpen={isExportMenuOpen} onClick={() => setIsExportMenuOpen(!isExportMenuOpen)}>
                                <div className="px-3 py-1 text-[9px] font-bold uppercase tracking-widest" style={{ color: 'var(--rs-text-muted)' }}>Sous-titres</div>
                                <MenuItem icon={Captions} label="SubRip (.srt)" onClick={handleExportSRT} />
                                <MenuItem icon={Captions} label="WebVTT (.vtt)" onClick={handleExportVTT} />
                                <div className="px-3 py-1 mt-1 text-[9px] font-bold uppercase tracking-widest" style={{ color: 'var(--rs-text-muted)' }}>Données</div>
                                <MenuItem icon={FileText} label="Transcript (.txt)" onClick={handleExportTXT} />
                                <MenuItem icon={FileSpreadsheet} label="Tableur (.csv)" onClick={handleExportCSV} />
                                <MenuDivider />
                                <MenuItem icon={Film} label="Rendu Vidéo (Master)" onClick={handleExportOffline} danger={true} />
                            </MenuButton>
                        </div>
                    </nav>
                </div>

                {/* Right: Settings + Avatar */}
                <div className="flex items-center gap-2">
                    {/* Mode Présentation */}
                    <button
                        onClick={() => setIsPresentationMode(true)}
                        className="w-8 h-8 flex items-center justify-center rounded-lg transition-all duration-100 active:scale-90"
                        style={{ color: 'var(--rs-text-secondary)' }}
                        title="Mode Présentation/Doublage (F11)"
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-bg-muted)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = ''; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                    >
                        <Maximize2 size={16} />
                    </button>
                    {/* Snap toggle */}
                    <button
                        onClick={() => setSnapEnabled(!snapEnabled)}
                        className="w-8 h-8 flex items-center justify-center rounded-lg transition-all duration-100 active:scale-90"
                        style={{
                            color: snapEnabled ? '#facc15' : 'var(--rs-text-secondary)',
                            background: snapEnabled ? 'rgba(250,204,21,0.1)' : 'transparent',
                            border: snapEnabled ? '1px solid rgba(250,204,21,0.3)' : '1px solid transparent',
                        }}
                        title={snapEnabled ? 'Magnétisme activé (cliquer pour désactiver)' : 'Magnétisme désactivé (cliquer pour activer)'}
                    >
                        <Magnet size={16} />
                    </button>
                    <button
                        onClick={() => setIsHistoryPanelOpen(true)}
                        className="w-8 h-8 flex items-center justify-center rounded-lg transition-all duration-100 active:scale-90"
                        style={{ color: 'var(--rs-text-secondary)' }}
                        title="Historique (Ctrl+H)"
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-bg-muted)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = ''; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                    >
                        <History size={16} />
                    </button>
                    <button
                        onClick={openSettings}
                        className="w-8 h-8 flex items-center justify-center rounded-lg transition-all duration-100 active:scale-90"
                        style={{ color: 'var(--rs-text-secondary)' }}
                        title="Paramètres"
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-bg-muted)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = ''; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                    >
                        <Settings size={16} />
                    </button>
                    <div
                        className="w-7 h-7 rounded-full flex items-center justify-center text-xs font-bold"
                        style={{ background: 'linear-gradient(135deg, var(--rs-bg-muted), var(--rs-border))', border: '1px solid var(--rs-border)', color: 'var(--rs-text-secondary)' }}
                    >
                        U
                    </div>
                </div>
            </header>

            {/* â”€â”€ MAIN CONTENT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
            <div className="flex-1 flex flex-col overflow-hidden">

                {/* Top: Video + Sidebar */}
                <div className="flex-1 flex flex-row min-h-0 overflow-hidden">
                    <div className="flex-1 relative flex flex-col min-w-0" style={{ background: '#000' }}>
                        <VideoPlayer />
                    </div>
                    <EditorSidebar />
                </div>

                {/* Bottom: Waveform + RhythmoBand + Controls */}
                <div
                    className="flex-none flex flex-col z-10 relative"
                    style={{
                        height: workstationHeight,
                        borderTop: '1px solid var(--rs-border)',
                        background: 'var(--rs-bg-surface)'
                    }}
                >
                    {/* Vertical Resize Handle */}
                    <div
                        onMouseDown={startResizingWorkstation}
                        className="absolute top-0 left-0 right-0 h-1.5 -mt-[1px] cursor-ns-resize group z-50 rounded-t"
                        style={{ background: 'transparent' }}
                    >
                        <div
                            className="absolute inset-x-0 top-0 h-[2px] transition-all duration-150"
                            style={{ background: 'transparent' }}
                            onMouseEnter={(e) => ((e.currentTarget as HTMLElement).style.background = 'var(--rs-accent)')}
                            onMouseLeave={(e) => ((e.currentTarget as HTMLElement).style.background = 'transparent')}
                        />
                    </div>

                    <div className="relative flex-none">
                        <Waveform />
                        {/* Sync-line glow overlay on waveform */}
                        <div
                            className="absolute top-0 bottom-0 pointer-events-none z-10 rs-syncline-glow"
                            style={{ left: `${SYNC_LINE_POSITION_X}px` }}
                        />
                    </div>
                    {/* Make RhythmoBand container stretch and scroll vertically if workstation is resized */}
                    <div ref={rhythmoContainerRef} className="flex-1 min-h-0 overflow-y-auto" style={{ background: 'var(--rs-bg-elevated)' }}>
                        <RhythmoBand width={bandDimensions.width} height={bandDimensions.height} ref={rhythmoStageRef} />
                    </div>
                    <Controls />
                </div>
            </div>

            {isPresentationMode && (
                <div
                    className="fixed inset-0 z-[200] flex flex-col bg-black"
                    style={{ animation: 'presentationFadeIn 0.25s ease-out' }}
                >
                    <style>{`
                    @keyframes presentationFadeIn {
                        from { opacity: 0; transform: scale(0.98); }
                        to   { opacity: 1; transform: scale(1); }
                    }
                `}</style>

                    <div
                        className="absolute top-0 left-0 right-0 z-10 flex items-center justify-between px-6 py-3 opacity-0 hover:opacity-100 transition-opacity duration-300"
                        style={{ background: 'linear-gradient(to bottom, rgba(0,0,0,0.85) 0%, transparent 100%)' }}
                    >
                        <div className="flex items-center gap-3">
                            <div className="w-6 h-6 rounded-md flex items-center justify-center" style={{ background: 'linear-gradient(135deg, #6366f1, #8b5cf6)' }}>
                                <Film size={12} className="text-white" />
                            </div>
                            <span className="text-white font-bold text-sm tracking-wide">Mode Présentation</span>
                            <span className="text-white/40 text-xs font-mono bg-white/10 px-2 py-0.5 rounded">F11 / Échap pour quitter</span>
                        </div>
                        <button
                            onClick={() => setIsPresentationMode(false)}
                            className="flex items-center gap-2 px-3 py-1.5 rounded-lg text-white/70 hover:text-white hover:bg-white/10 transition text-xs font-medium"
                        >
                            <Minimize2 size={14} />
                            Quitter
                        </button>
                    </div>

                    <div className="flex-none" style={{ height: '65vh', background: '#000' }}>
                        <VideoPlayer />
                    </div>

                    <div
                        ref={presentationBandContainerRef}
                        className="flex-1 min-h-0 overflow-y-auto"
                        style={{ background: '#0d1117' }}
                    >
                        <RhythmoBand
                            width={presentationBandDims.width}
                            height={presentationBandDims.height}
                            ref={presentationBandRef}
                        />
                    </div>

                    <div
                        className="flex-none flex items-center justify-center gap-6 px-8 py-3"
                        style={{ background: 'rgba(0,0,0,0.85)', borderTop: '1px solid rgba(255,255,255,0.05)' }}
                    >
                        <button
                            onClick={() => {
                                if (!videoElement) return;
                                if (isPlaying) { videoElement.pause(); setIsPlaying(false); }
                                else { videoElement.play(); setIsPlaying(true); }
                            }}
                            className={`w-12 h-12 flex items-center justify-center rounded-full text-white shadow-lg transition-transform hover:scale-105 active:scale-95 ${isPlaying ? 'bg-amber-600 hover:bg-amber-500' : 'bg-green-600 hover:bg-green-500'}`}
                        >
                            {isPlaying ? <Pause size={22} fill="currentColor" /> : <Play size={22} fill="currentColor" className="ml-0.5" />}
                        </button>

                        <div className="flex flex-col items-start">
                            <span className="font-mono text-2xl font-bold text-blue-400 leading-none tabular-nums">
                                {(() => {
                                    const t = videoElement?.currentTime ?? 0;
                                    const m = Math.floor(t / 60);
                                    const s = Math.floor(t % 60);
                                    const ms = Math.floor((t % 1) * 100);
                                    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}:${String(ms).padStart(2, '00')}`;
                                })()}
                            </span>
                            <span className="text-[10px] text-white/30 font-mono mt-0.5">{fps} fps</span>
                        </div>

                        <div className="flex items-center gap-2">
                            {[0.5, 1.0, 1.5, 2.0].map(rate => (
                                <button
                                    key={rate}
                                    onClick={() => { if (videoElement) videoElement.playbackRate = rate; }}
                                    className="px-3 py-1.5 rounded-lg text-xs font-bold transition"
                                    style={{
                                        background: (videoElement?.playbackRate ?? 1) === rate ? 'rgba(99,102,241,0.3)' : 'rgba(255,255,255,0.05)',
                                        color: (videoElement?.playbackRate ?? 1) === rate ? '#a5b4fc' : 'rgba(255,255,255,0.4)',
                                        border: (videoElement?.playbackRate ?? 1) === rate ? '1px solid rgba(99,102,241,0.4)' : '1px solid transparent',
                                    }}
                                >
                                    {rate}x
                                </button>
                            ))}
                        </div>
                    </div>
                </div>

            )}
        </div>
    );
}