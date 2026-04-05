/**
 * HistoryPanel.tsx
 * Panneau d'historique avancé (Undo/Redo).
 *
 * Affiche l'arborescence de l'historique des actions :
 *  - Toutes les entrées passées (past[])
 *  - L'état actuel (mis en évidence)
 *  - Les états futurs re-jouables (future[])
 *
 * Cliquer sur une entrée navigue directement à cet état via des undo/redo successifs.
 */

import React, { useCallback } from 'react';
import { useAppStore } from '../store';
import { RotateCcw, RotateCw, Clock, ChevronRight, X } from 'lucide-react';

interface HistoryPanelProps {
    onClose: () => void;
}

function describeState(dialogues: { text: string; startTime: number }[], index: number, total: number): string {
    if (index === 0) return 'État initial';
    const count = dialogues.length;
    return `${count} bloc${count !== 1 ? 's' : ''} — action #${index}`;
}

export const HistoryPanel: React.FC<HistoryPanelProps> = ({ onClose }) => {
    const past = useAppStore((s) => s.past);
    const future = useAppStore((s) => s.future);
    const dialogues = useAppStore((s) => s.dialogues);
    const undo = useAppStore((s) => s.undo);
    const redo = useAppStore((s) => s.redo);

    // L'index dans la timeline globale (0 = plus vieux, past.length = état actuel)
    const currentIndex = past.length;
    const totalSteps = past.length + 1 + future.length;

    // Naviguer vers un index précis par enchaînement d'undo/redo
    const navigateTo = useCallback((targetIndex: number) => {
        const diff = targetIndex - currentIndex;
        if (diff === 0) return;
        if (diff < 0) {
            // Reculer (undo)
            for (let i = 0; i < Math.abs(diff); i++) undo();
        } else {
            // Avancer (redo)
            for (let i = 0; i < diff; i++) redo();
        }
    }, [currentIndex, undo, redo]);

    // Construire la liste complète des états pour l'affichage
    // past[0] est le plus ancien, past[past.length-1] est le plus récent avant l'état actuel
    const allStates = [
        ...past.map((d, i) => ({ dialogues: d, index: i, isCurrent: false, isFuture: false })),
        { dialogues, index: past.length, isCurrent: true, isFuture: false },
        ...future.map((d, i) => ({ dialogues: d, index: past.length + 1 + i, isCurrent: false, isFuture: true })),
    ];
    // Afficher dans l'ordre inverse (le plus récent en haut)
    const reversed = [...allStates].reverse();

    return (
        <div
            style={{
                position: 'fixed',
                inset: 0,
                zIndex: 1000,
                display: 'flex',
                alignItems: 'flex-start',
                justifyContent: 'flex-end',
                background: 'rgba(0,0,0,0.45)',
                backdropFilter: 'blur(4px)',
            }}
            onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
        >
            <div
                style={{
                    width: 320,
                    height: '100vh',
                    display: 'flex',
                    flexDirection: 'column',
                    background: 'var(--rs-bg-surface)',
                    borderLeft: '1px solid var(--rs-border)',
                    boxShadow: '-24px 0 60px rgba(0,0,0,0.5)',
                    animation: 'slideInRight 180ms cubic-bezier(0.16,1,0.3,1)',
                }}
            >
                {/* ── En-tête ── */}
                <div
                    style={{
                        padding: '18px 20px 16px',
                        borderBottom: '1px solid var(--rs-border)',
                        background: 'var(--rs-bg-elevated)',
                        flexShrink: 0,
                    }}
                >
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                            <div style={{
                                width: 32, height: 32, borderRadius: 8,
                                background: 'rgba(99,102,241,0.15)',
                                border: '1px solid rgba(99,102,241,0.3)',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                            }}>
                                <Clock size={16} style={{ color: '#818cf8' }} />
                            </div>
                            <div>
                                <p style={{ color: '#ffffff', fontWeight: 700, fontSize: 14, marginBottom: 1 }}>
                                    Historique
                                </p>
                                <p style={{ color: '#71717a', fontSize: 11 }}>
                                    {past.length} actions · {future.length} refaisables
                                </p>
                            </div>
                        </div>
                        <button
                            onClick={onClose}
                            style={{
                                width: 28, height: 28, borderRadius: 6,
                                background: 'transparent', border: '1px solid var(--rs-border)',
                                color: '#71717a', cursor: 'pointer',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                                transition: 'all 150ms',
                            }}
                            onMouseEnter={(e) => {
                                (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.1)';
                                (e.currentTarget as HTMLElement).style.color = '#f87171';
                                (e.currentTarget as HTMLElement).style.borderColor = 'rgba(239,68,68,0.35)';
                            }}
                            onMouseLeave={(e) => {
                                (e.currentTarget as HTMLElement).style.background = 'transparent';
                                (e.currentTarget as HTMLElement).style.color = '#71717a';
                                (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)';
                            }}
                        >
                            <X size={14} />
                        </button>
                    </div>

                    {/* Boutons Undo / Redo rapides */}
                    <div style={{ display: 'flex', gap: 8 }}>
                        <button
                            onClick={() => undo()}
                            disabled={past.length === 0}
                            style={{
                                flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
                                padding: '7px 0', borderRadius: 8, fontSize: 11, fontWeight: 600,
                                background: past.length > 0 ? 'rgba(99,102,241,0.1)' : 'var(--rs-bg-base)',
                                border: `1px solid ${past.length > 0 ? 'rgba(99,102,241,0.3)' : 'var(--rs-border)'}`,
                                color: past.length > 0 ? '#a5b4fc' : '#52525b',
                                cursor: past.length > 0 ? 'pointer' : 'not-allowed',
                                transition: 'all 150ms',
                            }}
                        >
                            <RotateCcw size={12} />
                            Annuler
                        </button>
                        <button
                            onClick={() => redo()}
                            disabled={future.length === 0}
                            style={{
                                flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
                                padding: '7px 0', borderRadius: 8, fontSize: 11, fontWeight: 600,
                                background: future.length > 0 ? 'rgba(16,185,129,0.08)' : 'var(--rs-bg-base)',
                                border: `1px solid ${future.length > 0 ? 'rgba(16,185,129,0.25)' : 'var(--rs-border)'}`,
                                color: future.length > 0 ? '#34d399' : '#52525b',
                                cursor: future.length > 0 ? 'pointer' : 'not-allowed',
                                transition: 'all 150ms',
                            }}
                        >
                            <RotateCw size={12} />
                            Rétablir
                        </button>
                    </div>
                </div>

                {/* ── Liste des états ── */}
                <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
                    {reversed.map((entry) => {
                        const { isCurrent, isFuture, index } = entry;
                        const count = entry.dialogues.length;

                        return (
                            <button
                                key={index}
                                onClick={() => navigateTo(index)}
                                style={{
                                    width: '100%', textAlign: 'left',
                                    padding: '0 16px',
                                    background: 'transparent',
                                    border: 'none',
                                    cursor: isCurrent ? 'default' : 'pointer',
                                    display: 'flex', alignItems: 'center', gap: 0,
                                }}
                            >
                                {/* Timeline verticale */}
                                <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', width: 20, flexShrink: 0, marginRight: 12 }}>
                                    {/* Ligne haute */}
                                    <div style={{
                                        width: 2,
                                        height: 12,
                                        background: isFuture ? 'rgba(52,211,153,0.2)' : isCurrent ? 'rgba(99,102,241,0.5)' : 'rgba(99,102,241,0.3)',
                                        flexShrink: 0,
                                    }} />
                                    {/* Point */}
                                    <div style={{
                                        width: isCurrent ? 12 : 8,
                                        height: isCurrent ? 12 : 8,
                                        borderRadius: '50%',
                                        flexShrink: 0,
                                        background: isCurrent
                                            ? '#6366f1'
                                            : isFuture
                                                ? 'rgba(52,211,153,0.4)'
                                                : 'rgba(99,102,241,0.35)',
                                        border: isCurrent ? '2px solid #a5b4fc' : '2px solid transparent',
                                        boxShadow: isCurrent ? '0 0 10px rgba(99,102,241,0.6)' : 'none',
                                        transition: 'all 150ms',
                                    }} />
                                    {/* Ligne basse */}
                                    <div style={{
                                        width: 2,
                                        height: 12,
                                        background: isFuture ? 'rgba(52,211,153,0.2)' : 'rgba(99,102,241,0.3)',
                                        flexShrink: 0,
                                    }} />
                                </div>

                                {/* Contenu de l'entrée */}
                                <div
                                    style={{
                                        flex: 1, minWidth: 0,
                                        padding: '6px 10px 6px 0',
                                        borderRadius: 8,
                                        transition: 'background 100ms',
                                    }}
                                    onMouseEnter={(e) => {
                                        if (!isCurrent)
                                            (e.currentTarget as HTMLElement).style.background = isFuture ? 'rgba(52,211,153,0.05)' : 'rgba(99,102,241,0.06)';
                                    }}
                                    onMouseLeave={(e) => {
                                        (e.currentTarget as HTMLElement).style.background = 'transparent';
                                    }}
                                >
                                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                                        <span style={{
                                            fontSize: 12,
                                            fontWeight: isCurrent ? 700 : 500,
                                            color: isCurrent ? '#ffffff' : isFuture ? '#6ee7b7' : '#a1a1aa',
                                            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                                        }}>
                                            {index === 0 ? 'État initial' : isFuture ? `Rétablir #${index}` : `Action #${index}`}
                                        </span>
                                        {isCurrent && (
                                            <span style={{
                                                fontSize: 9, fontWeight: 700,
                                                color: '#6366f1',
                                                background: 'rgba(99,102,241,0.15)',
                                                border: '1px solid rgba(99,102,241,0.3)',
                                                borderRadius: 4,
                                                padding: '1px 5px',
                                                letterSpacing: '0.05em',
                                                flexShrink: 0,
                                            }}>
                                                ACTUEL
                                            </span>
                                        )}
                                    </div>
                                    <span style={{
                                        fontSize: 10,
                                        color: isCurrent ? '#71717a' : isFuture ? '#34d399' : '#52525b',
                                        fontFamily: 'monospace',
                                    }}>
                                        {count} dialogue{count !== 1 ? 's' : ''}
                                    </span>
                                </div>
                            </button>
                        );
                    })}

                    {/* Message si historique vide */}
                    {past.length === 0 && future.length === 0 && (
                        <div style={{ padding: '32px 20px', textAlign: 'center' }}>
                            <div style={{
                                width: 48, height: 48, borderRadius: 12, margin: '0 auto 12px',
                                background: 'rgba(99,102,241,0.08)',
                                border: '1px solid rgba(99,102,241,0.15)',
                                display: 'flex', alignItems: 'center', justifyContent: 'center',
                            }}>
                                <Clock size={22} style={{ color: '#4f4f6e' }} />
                            </div>
                            <p style={{ fontSize: 12, color: '#52525b', fontWeight: 500 }}>
                                Aucune action enregistrée
                            </p>
                            <p style={{ fontSize: 10, color: '#3f3f4e', marginTop: 4 }}>
                                Créez ou modifiez des blocs pour voir l'historique.
                            </p>
                        </div>
                    )}
                </div>

                {/* ── Pied de page ── */}
                <div style={{
                    padding: '12px 16px',
                    borderTop: '1px solid var(--rs-border)',
                    background: 'var(--rs-bg-elevated)',
                    flexShrink: 0,
                    display: 'flex', alignItems: 'center', gap: 6,
                }}>
                    <div style={{
                        width: 6, height: 6, borderRadius: '50%',
                        background: '#6366f1', boxShadow: '0 0 6px #6366f1',
                        flexShrink: 0,
                    }} />
                    <p style={{ fontSize: 10, color: '#52525b' }}>
                        Raccourcis : <kbd style={{ fontFamily: 'monospace', fontSize: 9, color: '#71717a', background: 'var(--rs-bg-base)', padding: '1px 4px', borderRadius: 3 }}>Ctrl+Z</kbd> annuler · <kbd style={{ fontFamily: 'monospace', fontSize: 9, color: '#71717a', background: 'var(--rs-bg-base)', padding: '1px 4px', borderRadius: 3 }}>Ctrl+Y</kbd> rétablir
                    </p>
                </div>
            </div>
        </div>
    );
};
