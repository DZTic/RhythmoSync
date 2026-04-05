/**
 * WhisperPanel.tsx
 * Panneau d'alignement temporel par IA (Whisper.cpp via Rust/Tauri).
 *
 * Fonctionnement :
 *  1. Vérifie si whisper-cli.exe + le modèle sont présents
 *  2. Si non → propose le téléchargement (avec progression)
 *  3. Lance la transcription sur la vidéo chargée
 *  4. Affiche les segments détectés (prévisualisation)
 *  5. Permet d'importer tout ou une sélection dans la bande rythmo
 */

import React, { useState, useEffect, useCallback, useRef } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { useAppStore } from '../store';
import type { DialogueBlock } from '../types';
import {
    Wand2, Download, Play, ChevronDown, ChevronUp,
    CheckCircle2, AlertCircle, Loader2, Import, X, Info,
    Mic2, Clock, Globe, Trash2
} from 'lucide-react';

// ─────────────────────────────────────────────────────────────────────────────
// Types internes
// ─────────────────────────────────────────────────────────────────────────────

interface WhisperSegment {
    id: string;
    text: string;
    startTime: number;
    duration: number;
    characterName: string;
    color: string;
    lane: number;
}

interface WhisperResult {
    segments: WhisperSegment[];
    language: string;
    duration: number;
}

interface WhisperProgress {
    stage: string;
    message: string;
    percent: number;
}

interface WhisperStatus {
    whisperReady: boolean;
    models: string[];
}

type PanelPhase = 'idle' | 'downloading' | 'transcribing' | 'results' | 'error';

const MODEL_INFO: Record<string, { label: string; size: string; quality: string; speed: string }> = {
    tiny: { label: 'Tiny', size: '75 MB', quality: '★★☆☆☆', speed: '⚡⚡⚡⚡' },
    base: { label: 'Base', size: '142 MB', quality: '★★★☆☆', speed: '⚡⚡⚡☆' },
    small: { label: 'Small', size: '466 MB', quality: '★★★★☆', speed: '⚡⚡☆☆' },
    medium: { label: 'Medium', size: '1.5 GB', quality: '★★★★★', speed: '⚡☆☆☆' },
};

const LANGUAGE_OPTIONS = [
    { value: 'auto', label: 'Auto-détection' },
    { value: 'fr', label: 'Français' },
    { value: 'en', label: 'English' },
    { value: 'es', label: 'Español' },
    { value: 'de', label: 'Deutsch' },
    { value: 'it', label: 'Italiano' },
    { value: 'pt', label: 'Português' },
    { value: 'ja', label: '日本語' },
    { value: 'zh', label: '中文' },
    { value: 'ko', label: '한국어' },
    { value: 'ru', label: 'Русский' },
    { value: 'ar', label: 'العربية' },
];

// ─────────────────────────────────────────────────────────────────────────────
// Composant principal
// ─────────────────────────────────────────────────────────────────────────────

interface WhisperPanelProps {
    onClose: () => void;
}

export const WhisperPanel: React.FC<WhisperPanelProps> = ({ onClose }) => {
    const videoPath = useAppStore((s) => s.videoPath);
    const addDialogues = useAppStore((s) => s.addDialogues);
    const snapshotHistory = useAppStore((s) => s.snapshotHistory);

    // ── État ──────────────────────────────────────────────────────────────────
    const [phase, setPhase] = useState<PanelPhase>('idle');
    const [status, setStatus] = useState<WhisperStatus | null>(null);
    const [selectedModel, setSelectedModel] = useState<string>('base');
    const [selectedLanguage, setSelectedLanguage] = useState<string>('auto');
    const [progress, setProgress] = useState<WhisperProgress | null>(null);
    const [result, setResult] = useState<WhisperResult | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [selectedSegments, setSelectedSegments] = useState<Set<string>>(new Set());
    const [expandedSegments, setExpandedSegments] = useState(true);
    const [isDeletingModel, setIsDeletingModel] = useState<string | null>(null); // model key being deleted
    const [confirmDeleteModelKey, setConfirmDeleteModelKey] = useState<string | null>(null); // model key pending confirmation

    // Nouveaux états de profils pour répartir sur différentes pistes
    const [profiles, setProfiles] = useState([
        { id: 0, name: 'Personnage A', color: '#8b5cf6', lane: 0 },
        { id: 1, name: 'Personnage B', color: '#10b981', lane: 1 },
    ]);
    const [segmentAssigns, setSegmentAssigns] = useState<Record<string, number>>({});

    const unlistenRef = useRef<UnlistenFn | null>(null);

    // ── Initialisation ────────────────────────────────────────────────────────
    useEffect(() => {
        invoke<WhisperStatus>('check_whisper')
            .then(setStatus)
            .catch(console.error);

        return () => {
            if (unlistenRef.current) unlistenRef.current();
        };
    }, []);

    // ── Abonnement aux events de progression ──────────────────────────────────
    const subscribeToProgress = useCallback(async () => {
        if (unlistenRef.current) unlistenRef.current();
        unlistenRef.current = await listen<WhisperProgress>('whisper-progress', (event) => {
            setProgress(event.payload);
        });
    }, []);

    // ── Téléchargement ────────────────────────────────────────────────────────
    const handleDownload = async () => {
        setPhase('downloading');
        setError(null);
        await subscribeToProgress();

        try {
            await invoke('download_whisper', { model: selectedModel });
            const newStatus = await invoke<WhisperStatus>('check_whisper');
            setStatus(newStatus);
            setPhase('idle');
        } catch (err) {
            setError(String(err));
            setPhase('error');
        }
    };

    // ── Suppression d'un modèle ───────────────────────────────────────────────
    const requestDeleteModel = (modelKey: string) => {
        setConfirmDeleteModelKey(modelKey);
    };

    const confirmDeleteModel = async () => {
        const modelKey = confirmDeleteModelKey;
        if (!modelKey) return;
        setConfirmDeleteModelKey(null);
        setIsDeletingModel(modelKey);
        try {
            await invoke('delete_whisper_model', { model: modelKey });
            const newStatus = await invoke<WhisperStatus>('check_whisper');
            setStatus(newStatus);
            // Si le modèle qu'on supprime était sélectionné, basculer sur un autre
            if (selectedModel === modelKey) {
                const remaining = newStatus.models.filter(m => m !== modelKey);
                if (remaining.length > 0) setSelectedModel(remaining[0]);
            }
        } catch (err) {
            setError(String(err));
        } finally {
            setIsDeletingModel(null);
        }
    };

    // ── Transcription ─────────────────────────────────────────────────────────
    const handleTranscribe = async () => {
        if (!videoPath) {
            setError('Aucune vidéo chargée. Ouvrez d\'abord une vidéo.');
            setPhase('error');
            return;
        }

        setPhase('transcribing');
        setError(null);
        setResult(null);
        await subscribeToProgress();

        try {
            const res = await invoke<WhisperResult>('run_whisper_transcription', {
                videoPath,
                model: selectedModel,
                language: selectedLanguage,
            });
            setResult(res);

            // Auto-assignation intelligente (si silence long -> change de piste)
            const newAssigns: Record<string, number> = {};
            let currentSpeaker = 0;
            let lastEnd = 0;
            res.segments.forEach(seg => {
                // S'il y a un gap de +1.2s, on suppose que c'est l'autre personnage qui parle
                if (seg.startTime - lastEnd > 1.2) {
                    currentSpeaker = (currentSpeaker + 1) % 2;
                }
                newAssigns[seg.id] = currentSpeaker;
                lastEnd = seg.startTime + seg.duration;
            });
            setSegmentAssigns(newAssigns);

            // Sélectionner tous les segments par défaut
            setSelectedSegments(new Set(res.segments.map((s) => s.id)));
            setPhase('results');
        } catch (err) {
            setError(String(err));
            setPhase('error');
        }
    };

    // ── Import des segments dans la bande rythmo ──────────────────────────────
    const handleImport = () => {
        if (!result) return;

        const toImport: DialogueBlock[] = result.segments
            .filter((s) => selectedSegments.has(s.id))
            .map((s, i) => {
                const pId = segmentAssigns[s.id] ?? 0;
                const p = profiles.find(pf => pf.id === pId) || profiles[0];
                return {
                    id: `whisper-import-${Date.now()}-${i}`,
                    text: s.text,
                    startTime: s.startTime,
                    duration: s.duration,
                    characterName: p.name,
                    color: p.color,
                    lane: p.lane, // Dépend du personnage !
                };
            });

        snapshotHistory();
        addDialogues(toImport);
        onClose();
    };

    const toggleSegment = (id: string) => {
        setSelectedSegments((prev) => {
            const next = new Set(prev);
            if (next.has(id)) next.delete(id);
            else next.add(id);
            return next;
        });
    };

    const toggleAll = () => {
        if (!result) return;
        if (selectedSegments.size === result.segments.length) {
            setSelectedSegments(new Set());
        } else {
            setSelectedSegments(new Set(result.segments.map((s) => s.id)));
        }
    };

    const isWhisperReady = status?.whisperReady && status.models.includes(selectedModel);

    // ─────────────────────────────────────────────────────────────────────────
    // Rendu
    // ─────────────────────────────────────────────────────────────────────────

    return (
        <div
            style={{
                position: 'fixed',
                inset: 0,
                zIndex: 1000,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                background: 'rgba(0,0,0,0.65)',
                backdropFilter: 'blur(6px)',
            }}
            onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
        >
            <div
                style={{
                    width: 600,
                    maxWidth: '95vw',
                    maxHeight: '90vh',
                    display: 'flex',
                    flexDirection: 'column',
                    background: 'var(--rs-bg-surface)',
                    border: '1px solid var(--rs-border)',
                    borderRadius: 20,
                    boxShadow: '0 30px 80px rgba(0,0,0,0.6), 0 0 0 1px rgba(139,92,246,0.15)',
                    overflow: 'hidden',
                }}
            >
                {/* ── En-tête ─────────────────────────────────────────────── */}
                <div
                    style={{
                        padding: '20px 24px 18px',
                        borderBottom: '1px solid var(--rs-border)',
                        background: 'var(--rs-bg-elevated)',
                        flexShrink: 0,
                    }}
                >
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                            <div style={{
                                width: 40, height: 40, borderRadius: 12,
                                background: 'rgba(139,92,246,0.15)',
                                border: '1px solid rgba(139,92,246,0.35)',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                                boxShadow: '0 0 20px rgba(139,92,246,0.2)',
                            }}>
                                <Wand2 size={20} style={{ color: '#a78bfa' }} />
                            </div>
                            <div>
                                <p style={{ color: '#ffffff', fontWeight: 700, fontSize: 16, marginBottom: 2 }}>
                                    Alignement IA — Whisper.cpp
                                </p>
                                <p style={{ color: '#a1a1aa', fontSize: 12 }}>
                                    Transcription locale · Sans connexion internet requise après installation
                                </p>
                            </div>
                        </div>
                        <button
                            onClick={onClose}
                            style={{
                                width: 32, height: 32, borderRadius: 8,
                                background: 'transparent', border: '1px solid var(--rs-border)',
                                color: 'var(--rs-text-muted)', cursor: 'pointer',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                                transition: 'all 150ms',
                            }}
                            onMouseEnter={(e) => {
                                (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.1)';
                                (e.currentTarget as HTMLElement).style.color = '#f87171';
                                (e.currentTarget as HTMLElement).style.borderColor = 'rgba(239,68,68,0.4)';
                            }}
                            onMouseLeave={(e) => {
                                (e.currentTarget as HTMLElement).style.background = 'transparent';
                                (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-muted)';
                                (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)';
                            }}
                        >
                            <X size={15} />
                        </button>
                    </div>
                </div>

                {/* ── Corps scrollable ────────────────────────────────────── */}
                <div style={{ flex: 1, overflowY: 'auto', padding: '20px 24px' }}>

                    {/* ── Configuration ─────────────────────────────────── */}
                    {(phase === 'idle' || phase === 'error') && (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>

                            {/* Sélecteur de modèle */}
                            <div>
                                <label style={{
                                    display: 'flex', alignItems: 'center', gap: 6,
                                    fontSize: 10, fontWeight: 700, letterSpacing: '0.1em',
                                    textTransform: 'uppercase', color: 'var(--rs-text-muted)', marginBottom: 8,
                                }}>
                                    <Mic2 size={11} /> Modèle Whisper
                                </label>
                                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                                    {Object.entries(MODEL_INFO).map(([key, info]) => {
                                        const isActive = selectedModel === key;
                                        const isInstalled = status?.models.includes(key);
                                        return (
                                            <div
                                                key={key}
                                                role="button"
                                                tabIndex={0}
                                                onClick={() => setSelectedModel(key)}
                                                onKeyDown={(e) => e.key === 'Enter' && setSelectedModel(key)}
                                                style={{
                                                    padding: isInstalled ? '10px 14px 28px' : '10px 14px',
                                                    borderRadius: 10,
                                                    border: `1px solid ${isActive ? 'rgba(139,92,246,0.6)' : 'var(--rs-border)'}`,
                                                    background: isActive ? 'rgba(139,92,246,0.1)' : 'var(--rs-bg-base)',
                                                    cursor: 'pointer',
                                                    textAlign: 'left',
                                                    transition: 'all 150ms',
                                                    boxShadow: isActive ? '0 0 14px rgba(139,92,246,0.2)' : 'none',
                                                    position: 'relative',
                                                    userSelect: 'none',
                                                }}
                                            >
                                                {/* Indicateur modèle installé */}
                                                {isInstalled && (
                                                    <div style={{
                                                        position: 'absolute', top: 6, right: 8,
                                                        width: 6, height: 6, borderRadius: '50%',
                                                        background: '#10b981',
                                                        boxShadow: '0 0 6px #10b981',
                                                    }} />
                                                )}
                                                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
                                                    <span style={{ fontWeight: 700, fontSize: 13, color: isActive ? '#a78bfa' : 'var(--rs-text-primary)' }}>
                                                        {info.label}
                                                    </span>
                                                    <span style={{ fontSize: 10, color: 'var(--rs-text-muted)', fontFamily: 'monospace' }}>
                                                        {info.size}
                                                    </span>
                                                </div>
                                                <div style={{ fontSize: 10, color: 'var(--rs-text-muted)', display: 'flex', gap: 8 }}>
                                                    <span title="Qualité">🎯 {info.quality}</span>
                                                    <span title="Vitesse">🏎️ {info.speed}</span>
                                                </div>

                                                {/* Bouton supprimer — toujours visible si installé */}
                                                {isInstalled && (
                                                    <button
                                                        onClick={(e) => { e.stopPropagation(); requestDeleteModel(key); }}
                                                        disabled={isDeletingModel === key}
                                                        title={`Supprimer le modèle ${info.label}`}
                                                        style={{
                                                            position: 'absolute', bottom: 6, right: 6,
                                                            display: 'flex', alignItems: 'center', gap: 4,
                                                            padding: '3px 8px',
                                                            borderRadius: 6,
                                                            background: 'rgba(239,68,68,0.08)',
                                                            border: '1px solid rgba(239,68,68,0.25)',
                                                            color: '#f87171',
                                                            cursor: 'pointer',
                                                            fontSize: 10,
                                                            fontWeight: 600,
                                                            transition: 'all 150ms',
                                                            opacity: isDeletingModel === key ? 0.5 : 1,
                                                        }}
                                                        onMouseEnter={(e) => {
                                                            (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.2)';
                                                            (e.currentTarget as HTMLElement).style.borderColor = 'rgba(239,68,68,0.6)';
                                                            (e.currentTarget as HTMLElement).style.color = '#fca5a5';
                                                        }}
                                                        onMouseLeave={(e) => {
                                                            (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.08)';
                                                            (e.currentTarget as HTMLElement).style.borderColor = 'rgba(239,68,68,0.25)';
                                                            (e.currentTarget as HTMLElement).style.color = '#f87171';
                                                        }}
                                                    >
                                                        {isDeletingModel === key
                                                            ? <Loader2 size={11} style={{ animation: 'spin 1s linear infinite' }} />
                                                            : <Trash2 size={11} />}
                                                        {isDeletingModel === key ? 'Suppression…' : 'Supprimer'}
                                                    </button>
                                                )}
                                            </div>
                                        );
                                    })}
                                </div>
                                <p style={{ fontSize: 10, color: 'var(--rs-text-muted)', marginTop: 6, display: 'flex', alignItems: 'center', gap: 4 }}>
                                    <span style={{ width: 6, height: 6, borderRadius: '50%', background: '#10b981', display: 'inline-block', boxShadow: '0 0 6px #10b981' }} />
                                    Déjà installé
                                </p>
                            </div>

                            {/* Sélecteur de langue */}
                            <div>
                                <label style={{
                                    display: 'flex', alignItems: 'center', gap: 6,
                                    fontSize: 10, fontWeight: 700, letterSpacing: '0.1em',
                                    textTransform: 'uppercase', color: 'var(--rs-text-muted)', marginBottom: 8,
                                }}>
                                    <Globe size={11} /> Langue audio
                                </label>
                                <select
                                    value={selectedLanguage}
                                    onChange={(e) => setSelectedLanguage(e.target.value)}
                                    style={{
                                        width: '100%',
                                        background: 'var(--rs-bg-base)',
                                        border: '1px solid var(--rs-border)',
                                        borderRadius: 8,
                                        padding: '8px 10px',
                                        fontSize: 12,
                                        color: 'var(--rs-text-primary)',
                                        fontFamily: 'inherit',
                                        outline: 'none',
                                        cursor: 'pointer',
                                    }}
                                >
                                    {LANGUAGE_OPTIONS.map((l) => (
                                        <option key={l.value} value={l.value}>{l.label}</option>
                                    ))}
                                </select>
                            </div>

                            {/* Statut vidéo */}
                            <div style={{
                                padding: '10px 14px',
                                borderRadius: 10,
                                background: videoPath ? 'rgba(16,185,129,0.08)' : 'rgba(245,158,11,0.08)',
                                border: `1px solid ${videoPath ? 'rgba(16,185,129,0.25)' : 'rgba(245,158,11,0.25)'}`,
                                display: 'flex', alignItems: 'center', gap: 10,
                            }}>
                                {videoPath ? (
                                    <CheckCircle2 size={16} style={{ color: '#10b981', flexShrink: 0 }} />
                                ) : (
                                    <AlertCircle size={16} style={{ color: '#f59e0b', flexShrink: 0 }} />
                                )}
                                <div>
                                    <p style={{ fontSize: 11, fontWeight: 600, color: videoPath ? '#34d399' : '#fbbf24', marginBottom: 2 }}>
                                        {videoPath ? 'Vidéo chargée' : 'Aucune vidéo chargée'}
                                    </p>
                                    <p style={{ fontSize: 10, color: 'var(--rs-text-muted)' }}>
                                        {videoPath
                                            ? videoPath.split(/[/\\]/).pop()
                                            : 'Ouvrez d\'abord une vidéo pour lancer l\'analyse.'}
                                    </p>
                                </div>
                            </div>

                            {/* Info box */}
                            <div style={{
                                padding: '10px 14px',
                                borderRadius: 10,
                                background: 'rgba(99,102,241,0.06)',
                                border: '1px solid rgba(99,102,241,0.2)',
                                display: 'flex', alignItems: 'flex-start', gap: 10,
                            }}>
                                <Info size={14} style={{ color: '#818cf8', flexShrink: 0, marginTop: 1 }} />
                                <p style={{ fontSize: 11, color: '#c7d2fe', lineHeight: 1.6 }}>
                                    Whisper.cpp analyse l'audio de votre vidéo en local, sur votre machine, sans aucun envoi de données à l'extérieur.
                                    La première analyse peut prendre 30 à 90 secondes selon le modèle et la durée vidéo.
                                </p>
                            </div>

                            {/* Message d'erreur */}
                            {error && (
                                <div style={{
                                    padding: '10px 14px',
                                    borderRadius: 10,
                                    background: 'rgba(239,68,68,0.08)',
                                    border: '1px solid rgba(239,68,68,0.3)',
                                    display: 'flex', alignItems: 'flex-start', gap: 10,
                                }}>
                                    <AlertCircle size={14} style={{ color: '#f87171', flexShrink: 0, marginTop: 1 }} />
                                    <p style={{ fontSize: 11, color: '#fca5a5', lineHeight: 1.5 }}>{error}</p>
                                </div>
                            )}
                        </div>
                    )}

                    {/* ── Progression (téléchargement / transcription) ─────── */}
                    {(phase === 'downloading' || phase === 'transcribing') && (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: 20, padding: '12px 0' }}>
                            <div style={{ textAlign: 'center' }}>
                                <div style={{
                                    width: 64, height: 64, borderRadius: '50%',
                                    background: 'rgba(139,92,246,0.1)',
                                    border: '2px solid rgba(139,92,246,0.3)',
                                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                                    margin: '0 auto 16px',
                                    animation: 'spin 2s linear infinite',
                                }}>
                                    <Loader2 size={28} style={{ color: '#a78bfa' }} />
                                </div>
                                <p style={{ fontWeight: 700, fontSize: 16, color: '#ffffff', marginBottom: 6 }}>
                                    {phase === 'downloading' ? 'Installation en cours…' : 'Analyse IA en cours…'}
                                </p>
                                <p style={{ fontSize: 13, color: '#a1a1aa' }}>
                                    {progress?.message ?? 'Préparation…'}
                                </p>
                            </div>

                            {/* Barre de progression */}
                            <div>
                                <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 6 }}>
                                    <span style={{ fontSize: 11, color: '#a1a1aa', fontWeight: 500 }}>{progress?.stage ?? ''}</span>
                                    <span style={{ fontSize: 11, fontWeight: 700, color: '#c4b5fd', fontFamily: 'monospace' }}>
                                        {progress?.percent ?? 0}%
                                    </span>
                                </div>
                                <div style={{
                                    height: 6, borderRadius: 999,
                                    background: 'var(--rs-bg-base)',
                                    border: '1px solid var(--rs-border)',
                                    overflow: 'hidden',
                                }}>
                                    <div style={{
                                        height: '100%',
                                        width: `${progress?.percent ?? 0}%`,
                                        background: 'linear-gradient(90deg, #7c3aed, #a78bfa)',
                                        borderRadius: 999,
                                        transition: 'width 500ms ease',
                                        boxShadow: '0 0 10px rgba(139,92,246,0.5)',
                                    }} />
                                </div>
                            </div>

                            {phase === 'transcribing' && (
                                <div style={{
                                    padding: '10px 14px', borderRadius: 10,
                                    background: 'rgba(139,92,246,0.06)',
                                    border: '1px solid rgba(139,92,246,0.2)',
                                    display: 'flex', alignItems: 'center', gap: 10,
                                }}>
                                    <Info size={13} style={{ color: '#818cf8', flexShrink: 0 }} />
                                    <p style={{ fontSize: 11, color: '#c7d2fe' }}>
                                        L'application reste utilisable pendant la transcription. Le modèle {MODEL_INFO[selectedModel]?.label} est 100% local.
                                    </p>
                                </div>
                            )}
                        </div>
                    )}

                    {/* ── Résultats ────────────────────────────────────────── */}
                    {phase === 'results' && result && (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>

                            {/* Résumé */}
                            <div style={{
                                display: 'grid', gridTemplateColumns: '1fr 1fr 1fr',
                                gap: 8,
                            }}>
                                {[
                                    { label: 'Segments', value: result.segments.length.toString(), icon: '📝' },
                                    { label: 'Durée', value: formatTime(result.duration), icon: '⏱' },
                                    { label: 'Sélectionnés', value: selectedSegments.size.toString(), icon: '✅' },
                                ].map(({ label, value, icon }) => (
                                    <div key={label} style={{
                                        padding: '10px 12px', borderRadius: 10,
                                        background: 'var(--rs-bg-base)',
                                        border: '1px solid var(--rs-border)',
                                        textAlign: 'center',
                                    }}>
                                        <div style={{ fontSize: 18, marginBottom: 4 }}>{icon}</div>
                                        <div style={{ fontWeight: 700, fontSize: 16, color: '#a78bfa', fontFamily: 'monospace' }}>{value}</div>
                                        <div style={{ fontSize: 10, color: 'var(--rs-text-muted)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>{label}</div>
                                    </div>
                                ))}
                            </div>

                            {/* Configuration des personnages */}
                            <div style={{
                                padding: '12px 14px', borderRadius: 10,
                                background: 'var(--rs-bg-base)',
                                border: '1px solid var(--rs-border)',
                                display: 'flex', flexDirection: 'column', gap: 10,
                            }}>
                                <label style={{
                                    fontSize: 10, fontWeight: 700, letterSpacing: '0.1em',
                                    textTransform: 'uppercase', color: 'var(--rs-text-muted)', display: 'block'
                                }}>
                                    Personnages à associer aux pistes
                                </label>
                                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                                    {profiles.map((p) => (
                                        <div key={p.id} style={{
                                            display: 'flex', alignItems: 'center', gap: 8,
                                            padding: '8px 10px', borderRadius: 8,
                                            background: 'var(--rs-bg-elevated)',
                                            border: `1px solid ${p.color}40`,
                                        }}>
                                            <div style={{ width: 12, height: 12, borderRadius: '50%', background: p.color, boxShadow: `0 0 6px ${p.color}80`, flexShrink: 0 }} />
                                            <input
                                                value={p.name}
                                                onChange={(e) => {
                                                    const val = e.target.value;
                                                    setProfiles(prev => prev.map(old => old.id === p.id ? { ...old, name: val } : old));
                                                }}
                                                style={{
                                                    flex: 1, minWidth: 0,
                                                    background: 'transparent', border: 'none', color: 'var(--rs-text-primary)',
                                                    fontSize: 12, fontWeight: 600, outline: 'none'
                                                }}
                                            />
                                            <span style={{ fontSize: 10, color: 'var(--rs-text-muted)', whiteSpace: 'nowrap', userSelect: 'none' }}>
                                                Piste {p.lane + 1}
                                            </span>
                                        </div>
                                    ))}
                                </div>
                                <p style={{ fontSize: 10, color: 'var(--rs-text-muted)', textAlign: 'center', margin: '4px 0 0' }}>
                                    Cliquez sur le badge coloré d'un segment dans la liste ci-dessous pour changer instantanément la piste.
                                </p>
                            </div>

                            {/* Liste des segments */}
                            <div style={{
                                borderRadius: 10,
                                border: '1px solid var(--rs-border)',
                                overflow: 'hidden',
                            }}>
                                {/* Header de la liste */}
                                <div
                                    onClick={() => setExpandedSegments(!expandedSegments)}
                                    style={{
                                        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                                        padding: '10px 14px',
                                        background: 'var(--rs-bg-elevated)',
                                        cursor: 'pointer',
                                        borderBottom: expandedSegments ? '1px solid var(--rs-border)' : 'none',
                                    }}
                                >
                                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                                        <span style={{ fontSize: 11, fontWeight: 700, color: 'var(--rs-text-primary)' }}>
                                            Segments détectés ({result.segments.length})
                                        </span>
                                    </div>
                                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                                        <button
                                            onClick={(e) => { e.stopPropagation(); toggleAll(); }}
                                            style={{
                                                fontSize: 10, padding: '3px 8px', borderRadius: 5,
                                                background: 'rgba(139,92,246,0.1)',
                                                border: '1px solid rgba(139,92,246,0.3)',
                                                color: '#a78bfa', cursor: 'pointer', fontWeight: 600,
                                            }}
                                        >
                                            {selectedSegments.size === result.segments.length ? 'Tout désélect.' : 'Tout sélect.'}
                                        </button>
                                        {expandedSegments ? <ChevronUp size={14} style={{ color: 'var(--rs-text-muted)' }} /> : <ChevronDown size={14} style={{ color: 'var(--rs-text-muted)' }} />}
                                    </div>
                                </div>

                                {/* Segments */}
                                {expandedSegments && (
                                    <div style={{ maxHeight: 260, overflowY: 'auto' }}>
                                        {result.segments.map((seg) => {
                                            const isSelected = selectedSegments.has(seg.id);
                                            return (
                                                <div
                                                    key={seg.id}
                                                    onClick={() => toggleSegment(seg.id)}
                                                    style={{
                                                        display: 'flex', alignItems: 'flex-start', gap: 10,
                                                        padding: '9px 14px',
                                                        borderBottom: '1px solid var(--rs-border-subtle)',
                                                        cursor: 'pointer',
                                                        background: isSelected ? 'rgba(139,92,246,0.04)' : 'transparent',
                                                        transition: 'background 100ms',
                                                    }}
                                                    onMouseEnter={(e) => {
                                                        if (!isSelected) (e.currentTarget as HTMLElement).style.background = 'rgba(255,255,255,0.02)';
                                                    }}
                                                    onMouseLeave={(e) => {
                                                        if (!isSelected) (e.currentTarget as HTMLElement).style.background = 'transparent';
                                                    }}
                                                >
                                                    {/* Checkbox custom */}
                                                    <div style={{
                                                        width: 16, height: 16, borderRadius: 4, flexShrink: 0, marginTop: 1,
                                                        background: isSelected ? '#8b5cf6' : 'transparent',
                                                        border: `1.5px solid ${isSelected ? '#8b5cf6' : 'var(--rs-border)'}`,
                                                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                                                        transition: 'all 120ms',
                                                        boxShadow: isSelected ? '0 0 8px rgba(139,92,246,0.4)' : 'none',
                                                    }}>
                                                        {isSelected && (
                                                            <svg width="9" height="7" viewBox="0 0 9 7" fill="none">
                                                                <path d="M1 3.5L3.5 6L8 1" stroke="white" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
                                                            </svg>
                                                        )}
                                                    </div>

                                                    <div style={{ flex: 1, minWidth: 0 }}>
                                                        <p style={{
                                                            fontSize: 12, color: 'var(--rs-text-primary)',
                                                            lineHeight: 1.4, marginBottom: 4,
                                                            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                                                        }}>
                                                            {seg.text}
                                                        </p>
                                                        <div style={{ display: 'flex', gap: 8, alignItems: 'center', justifyContent: 'space-between' }}>
                                                            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                                                                <span style={{
                                                                    display: 'flex', alignItems: 'center', gap: 3,
                                                                    fontSize: 10, color: 'var(--rs-text-muted)', fontFamily: 'monospace',
                                                                }}>
                                                                    <Clock size={9} />
                                                                    {formatTime(seg.startTime)}
                                                                </span>
                                                                <span style={{
                                                                    fontSize: 10, color: 'var(--rs-text-muted)',
                                                                    padding: '1px 6px', borderRadius: 4,
                                                                    background: 'var(--rs-bg-base)',
                                                                    fontFamily: 'monospace',
                                                                }}>
                                                                    {seg.duration.toFixed(1)}s
                                                                </span>
                                                            </div>

                                                            {/* Bouton de changement de perso/piste */}
                                                            {(() => {
                                                                const pId = segmentAssigns[seg.id] ?? 0;
                                                                const p = profiles.find(pf => pf.id === pId) || profiles[0];
                                                                return (
                                                                    <button
                                                                        onClick={(e) => {
                                                                            e.stopPropagation();
                                                                            setSegmentAssigns(prev => ({
                                                                                ...prev,
                                                                                [seg.id]: (pId + 1) % profiles.length
                                                                            }));
                                                                        }}
                                                                        style={{
                                                                            background: `${p.color}20`,
                                                                            color: p.color,
                                                                            border: `1px solid ${p.color}40`,
                                                                            padding: '2px 6px', borderRadius: 4,
                                                                            fontSize: 9, fontWeight: 700,
                                                                            display: 'flex', alignItems: 'center', gap: 4,
                                                                            cursor: 'pointer', outline: 'none',
                                                                        }}
                                                                    >
                                                                        <div style={{ width: 6, height: 6, borderRadius: '50%', background: p.color }} />
                                                                        {p.name}
                                                                    </button>
                                                                );
                                                            })()}
                                                        </div>
                                                    </div>
                                                </div>
                                            );
                                        })}
                                    </div>
                                )}
                            </div>
                        </div>
                    )}
                </div>

                {/* ── Pied de page : boutons d'action ─────────────────────── */}
                <div style={{
                    padding: '14px 24px',
                    borderTop: '1px solid var(--rs-border)',
                    background: 'var(--rs-bg-elevated)',
                    display: 'flex', gap: 10, alignItems: 'center', flexShrink: 0,
                }}>

                    {/* Bouton Annuler */}
                    <button
                        onClick={onClose}
                        style={{
                            flex: '0 0 auto', padding: '9px 18px',
                            borderRadius: 10, fontSize: 12, fontWeight: 600,
                            background: 'transparent',
                            border: '1px solid var(--rs-border)',
                            color: '#a1a1aa', cursor: 'pointer',
                            transition: 'all 150ms',
                        }}
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.borderColor = '#d4d4d8'; (e.currentTarget as HTMLElement).style.color = '#ffffff'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)'; (e.currentTarget as HTMLElement).style.color = '#a1a1aa'; }}
                    >
                        Annuler
                    </button>

                    {/* Bouton contextuel principal */}
                    {phase === 'idle' || phase === 'error' ? (
                        <>
                            {!isWhisperReady && (
                                <ActionButton
                                    onClick={handleDownload}
                                    icon={<Download size={15} />}
                                    label={`Installer Whisper (modèle ${MODEL_INFO[selectedModel]?.label})`}
                                    color="#7c3aed"
                                    glowColor="rgba(139,92,246,0.3)"

                                />
                            )}
                            <ActionButton
                                onClick={handleTranscribe}
                                icon={<Play size={15} />}
                                label="Lancer l'analyse IA"
                                color="#7c3aed"
                                glowColor="rgba(139,92,246,0.35)"
                                disabled={!isWhisperReady || !videoPath}
                                fullWidth={isWhisperReady}
                            />
                        </>
                    ) : phase === 'results' ? (
                        <>
                            <ActionButton
                                onClick={handleTranscribe}
                                icon={<Play size={14} />}
                                label="Réanalyser"
                                color="#374151"
                                glowColor="transparent"
                            />
                            <ActionButton
                                onClick={handleImport}
                                icon={<Import size={15} />}
                                label={`Importer ${selectedSegments.size} segment${selectedSegments.size > 1 ? 's' : ''}`}
                                color="#7c3aed"
                                glowColor="rgba(139,92,246,0.35)"
                                disabled={selectedSegments.size === 0}
                                fullWidth
                            />
                        </>
                    ) : null}
                </div>
            </div>

            <style>{`
                @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
                @keyframes fadeInData { from { opacity: 0; transform: scale(0.95); } to { opacity: 1; transform: scale(1); } }
            `}</style>

            {/* ── Pop-up de confirmation interne ──────────────────────── */}
            {confirmDeleteModelKey && (
                <div
                    style={{
                        position: 'fixed', inset: 0, zIndex: 2000,
                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                        background: 'rgba(0,0,0,0.5)', backdropFilter: 'blur(4px)',
                        animation: 'fadeInData 150ms ease-out',
                    }}
                    onClick={(e) => { e.stopPropagation(); setConfirmDeleteModelKey(null); }}
                >
                    <div
                        style={{
                            width: 380,
                            background: 'var(--rs-bg-surface)',
                            border: '1px solid var(--rs-border)',
                            borderRadius: 16,
                            padding: 24,
                            boxShadow: '0 20px 40px rgba(0,0,0,0.6), 0 0 0 1px rgba(239,68,68,0.1)',
                            display: 'flex', flexDirection: 'column', gap: 16,
                        }}
                        onClick={(e) => e.stopPropagation()} // empêcher fermeture subite
                    >
                        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 14 }}>
                            <div style={{
                                width: 42, height: 42, borderRadius: 12,
                                background: 'rgba(239,68,68,0.1)', border: '1px solid rgba(239,68,68,0.2)',
                                display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0
                            }}>
                                <Trash2 size={20} style={{ color: '#f87171' }} />
                            </div>
                            <div style={{ paddingTop: 2 }}>
                                <h3 style={{ margin: 0, fontSize: 16, fontWeight: 700, color: '#fff', marginBottom: 6 }}>Supprimer le modèle</h3>
                                <p style={{ margin: 0, fontSize: 13, color: 'var(--rs-text-muted)', lineHeight: 1.5 }}>
                                    Voulez-vous vraiment supprimer le modèle <b style={{ color: '#e2e8f0' }}>{MODEL_INFO[confirmDeleteModelKey]?.label ?? confirmDeleteModelKey}</b> ({MODEL_INFO[confirmDeleteModelKey]?.size ?? '?'}) ?
                                    Il sera effacé de votre disque local.
                                </p>
                            </div>
                        </div>

                        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 10, marginTop: 8 }}>
                            <button
                                onClick={(e) => { e.stopPropagation(); setConfirmDeleteModelKey(null); }}
                                style={{
                                    padding: '9px 16px', borderRadius: 8,
                                    background: 'var(--rs-bg-base)',
                                    border: '1px solid var(--rs-border)',
                                    color: 'var(--rs-text-primary)',
                                    cursor: 'pointer', fontSize: 13, fontWeight: 600,
                                    transition: 'background 150ms',
                                }}
                                onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--rs-bg-elevated)')}
                                onMouseLeave={(e) => (e.currentTarget.style.background = 'var(--rs-bg-base)')}
                            >
                                Annuler
                            </button>
                            <button
                                onClick={(e) => { e.stopPropagation(); confirmDeleteModel(); }}
                                style={{
                                    padding: '9px 16px', borderRadius: 8,
                                    background: '#ef4444',
                                    border: '1px solid transparent',
                                    color: '#fff',
                                    cursor: 'pointer', fontSize: 13, fontWeight: 600,
                                    boxShadow: '0 4px 10px rgba(239,68,68,0.3)',
                                    transition: 'all 150ms',
                                }}
                                onMouseEnter={(e) => {
                                    e.currentTarget.style.background = '#dc2626';
                                    e.currentTarget.style.transform = 'translateY(-1px)';
                                }}
                                onMouseLeave={(e) => {
                                    e.currentTarget.style.background = '#ef4444';
                                    e.currentTarget.style.transform = 'translateY(0)';
                                }}
                            >
                                Supprimer
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
};

// ─────────────────────────────────────────────────────────────────────────────
// Sous-composants
// ─────────────────────────────────────────────────────────────────────────────

interface ActionButtonProps {
    onClick: () => void;
    icon: React.ReactNode;
    label: string;
    color: string;
    glowColor: string;
    disabled?: boolean;
    fullWidth?: boolean;
}

const ActionButton: React.FC<ActionButtonProps> = ({ onClick, icon, label, color, glowColor, disabled, fullWidth }) => (
    <button
        onClick={onClick}
        disabled={disabled}
        style={{
            flex: fullWidth ? '1' : '0 0 auto',
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
            padding: '9px 18px', borderRadius: 10,
            fontSize: 12, fontWeight: 700, color: 'white',
            background: disabled ? 'var(--rs-bg-muted)' : color,
            border: `1px solid ${disabled ? 'var(--rs-border)' : 'rgba(255,255,255,0.1)'}`,
            boxShadow: disabled ? 'none' : `0 0 20px ${glowColor}`,
            cursor: disabled ? 'not-allowed' : 'pointer',
            opacity: disabled ? 0.5 : 1,
            transition: 'all 150ms',
            whiteSpace: 'nowrap',
        }}
        onMouseEnter={(e) => { if (!disabled) (e.currentTarget as HTMLElement).style.filter = 'brightness(1.12)'; }}
        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.filter = ''; }}
    >
        {icon}
        {label}
    </button>
);

// ─────────────────────────────────────────────────────────────────────────────
// Utils
// ─────────────────────────────────────────────────────────────────────────────

function formatTime(seconds: number): string {
    const m = Math.floor(seconds / 60);
    const s = (seconds % 60).toFixed(1);
    return `${m}:${s.padStart(4, '0')}`;
}
