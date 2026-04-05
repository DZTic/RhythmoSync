import React, { useState, useRef, useEffect } from 'react';
import { useAppStore } from '../store';
import { Type, User, Palette, Layers, Clock, Trash2, Plus, RotateCcw, MousePointerClick, Expand, AlertTriangle, VolumeX, Volume2, Sliders } from 'lucide-react';
import { getLaneHeight, LANE_HEIGHT, MAX_CHARS_PER_SECOND } from '../types';

// ── Shared field label component ──────────────────────────────────────────────
const Label: React.FC<{ icon?: React.ReactNode; children: React.ReactNode }> = ({ icon, children }) => (
    <label
        className="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-widest mb-2"
        style={{ color: 'var(--rs-text-muted)' }}
    >
        {icon}
        {children}
    </label>
);

// ── Shared input style helper ─────────────────────────────────────────────────
const inputStyle: React.CSSProperties = {
    width: '100%',
    background: 'var(--rs-bg-base)',
    border: '1px solid var(--rs-border)',
    borderRadius: '8px',
    padding: '8px 10px',
    fontSize: '12px',
    color: 'var(--rs-text-primary)',
    outline: 'none',
    transition: 'border-color 150ms, box-shadow 150ms',
    fontFamily: 'inherit',
};

const useInputFocus = () => ({
    onFocus: (e: React.FocusEvent<HTMLElement>) => {
        (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-accent)';
        (e.currentTarget as HTMLElement).style.boxShadow = '0 0 0 3px rgba(99,102,241,0.2)';
    },
    onBlur: (e: React.FocusEvent<HTMLElement>) => {
        (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)';
        (e.currentTarget as HTMLElement).style.boxShadow = '';
    },
});

// ─────────────────────────────────────────────────────────────────────────────

export const EditorSidebar: React.FC = () => {
    // PERF: Granular selectors
    const selectedBlockIds = useAppStore((s) => s.selectedBlockIds);
    const totalLanes = useAppStore((s) => s.totalLanes);
    const updateDialogue = useAppStore((s) => s.updateDialogue);
    const deleteDialogue = useAppStore((s) => s.deleteDialogue);
    const deleteDialogues = useAppStore((s) => s.deleteDialogues);
    const addDialogue = useAppStore((s) => s.addDialogue);
    const snapshotHistory = useAppStore((s) => s.snapshotHistory);
    const selectedBlock = useAppStore((s) =>
        s.selectedBlockIds.length === 1 ? s.dialogues.find((d) => d.id === s.selectedBlockIds[0]) ?? null : null
    );
    const laneHeights = useAppStore(s => s.laneHeights);
    const setLaneHeight = useAppStore(s => s.setLaneHeight);
    const audioTracks = useAppStore(s => s.audioTracks);
    const updateAudioTrack = useAppStore(s => s.updateAudioTrack);

    const [localDuration, setLocalDuration] = React.useState<string>('');
    const [isEditingDuration, setIsEditingDuration] = React.useState(false);
    const [deleteConfirm, setDeleteConfirm] = React.useState(false);
    const [width, setWidth] = useState(320);
    const isResizing = useRef(false);
    const focusProps = useInputFocus();

    // ── Resizing (rAF-throttled) ──────────────────────────────────────────────
    useEffect(() => {
        let rafId: number | null = null;
        const onMove = (e: MouseEvent) => {
            if (!isResizing.current) return;
            if (rafId !== null) cancelAnimationFrame(rafId);
            rafId = requestAnimationFrame(() => {
                const w = window.innerWidth - e.clientX;
                if (w >= 250 && w <= 600) setWidth(w);
                rafId = null;
            });
        };
        const onUp = () => {
            if (!isResizing.current) return;
            isResizing.current = false;
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            if (rafId !== null) { cancelAnimationFrame(rafId); rafId = null; }
        };
        window.addEventListener('mousemove', onMove);
        window.addEventListener('mouseup', onUp);
        return () => {
            window.removeEventListener('mousemove', onMove);
            window.removeEventListener('mouseup', onUp);
            if (rafId !== null) cancelAnimationFrame(rafId);
        };
    }, []);

    const startResizing = (e: React.MouseEvent) => {
        e.preventDefault();
        isResizing.current = true;
        document.body.style.cursor = 'ew-resize';
        document.body.style.userSelect = 'none';
    };

    // ── Duration sync ─────────────────────────────────────────────────────────
    useEffect(() => {
        if (selectedBlock) { setLocalDuration(selectedBlock.duration.toFixed(2)); setIsEditingDuration(false); }
    }, [selectedBlock?.id]);

    useEffect(() => {
        if (selectedBlock && !isEditingDuration) setLocalDuration(selectedBlock.duration.toFixed(2));
    }, [selectedBlock?.duration, isEditingDuration]);

    const handleDurationChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        const val = e.target.value;
        setLocalDuration(val);
        const num = parseFloat(val);
        if (!isNaN(num) && num >= 0.1 && selectedBlock)
            updateDialogue(selectedBlock.id, { duration: num }, true);
    };

    const handleResetDuration = () => {
        setLocalDuration('2.00');
        if (selectedBlock) { snapshotHistory(); updateDialogue(selectedBlock.id, { duration: 2.0 }); }
    };

    const handleAddBlock = () => {
        const currentTime = useAppStore.getState().currentTime;
        addDialogue({ id: Date.now().toString(), text: 'Nouveau dialogue', startTime: currentTime, duration: 2.0, characterName: 'Perso', color: '#6366f1', lane: 0 });
    };

    const handleDelete = () => {
        if (deleteConfirm && selectedBlock) { deleteDialogue(selectedBlock.id); setDeleteConfirm(false); }
        else if (deleteConfirm && selectedBlockIds.length > 1) { deleteDialogues(selectedBlockIds); setDeleteConfirm(false); }
        else { setDeleteConfirm(true); setTimeout(() => setDeleteConfirm(false), 3000); }
    };

    // ── Empty state ───────────────────────────────────────────────────────────
    const renderEmpty = () => {
        const isMulti = selectedBlockIds.length > 1;
        return (
            <div className="flex flex-col h-full items-center justify-center p-6 text-center gap-6 relative isolate">
                <div className="absolute inset-0 bg-gradient-to-br from-indigo-500/5 via-transparent to-transparent pointer-events-none" />

                {/* Icon pulse */}
                <div className="relative">
                    <div className="absolute inset-0 bg-indigo-500/20 blur-xl rounded-full" />
                    <div
                        className="w-16 h-16 rounded-2xl flex items-center justify-center relative rs-glass"
                        style={{ border: '1px solid rgba(255,255,255,0.05)' }}
                    >
                        {isMulti ? <Layers size={28} className="text-indigo-400 opacity-80" /> : <MousePointerClick size={28} className="text-indigo-400 opacity-80" />}
                    </div>
                </div>

                <div className="z-10">
                    <p className="text-sm font-bold tracking-widest text-white/90 mb-1">
                        {isMulti ? "SÉLECTION MULTIPLE" : "AUCUN BLOC SÉLECTIONNÉ"}
                    </p>
                    <p className="text-xs text-white/50 max-w-[200px] leading-relaxed">
                        {isMulti ? `${selectedBlockIds.length} blocs sélectionnés.` : "Cliquez sur un bloc dans la chronologie pour l'éditer ici."}
                    </p>
                </div>

                {isMulti && (
                    <div className="flex flex-col gap-2 w-full max-w-[250px] z-10">
                        <div className="flex gap-2 w-full justify-center">
                            <button
                                onClick={() => useAppStore.getState().groupDialogues(selectedBlockIds)}
                                className="flex-1 flex justify-center items-center gap-2 px-3 py-2 rounded-xl text-xs font-bold transition-all duration-300"
                                style={{ background: 'rgba(99,102,241,0.15)', border: '1px solid rgba(99,102,241,0.3)', color: '#818cf8' }}
                                title="Grouper les blocs (Ctrl+G)"
                            >
                                Grouper
                            </button>
                            <button
                                onClick={() => useAppStore.getState().ungroupDialogues(selectedBlockIds)}
                                className="flex-1 flex justify-center items-center gap-2 px-3 py-2 rounded-xl text-xs font-bold transition-all duration-300"
                                style={{ background: 'rgba(255,255,255,0.05)', border: '1px solid var(--rs-border)', color: 'var(--rs-text-secondary)' }}
                                title="Dégrouper les blocs (Ctrl+Shift+G)"
                            >
                                Dégrouper
                            </button>
                        </div>
                        <button
                            onClick={handleDelete}
                            className="flex justify-center items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold transition-all duration-300 w-full"
                            style={{ background: deleteConfirm ? 'rgba(239,68,68,0.15)' : 'transparent', border: deleteConfirm ? '1px solid rgba(239,68,68,0.6)' : '1px solid var(--rs-border)', color: deleteConfirm ? '#f87171' : 'var(--rs-text-secondary)' }}
                        >
                            <Trash2 size={14} strokeWidth={2.5} />
                            {deleteConfirm ? 'Confirmer ?' : 'Supprimer'}
                        </button>
                    </div>
                )}

                {!isMulti && (
                    <button
                        onClick={handleAddBlock}
                        className="flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white transition-all duration-300 z-10"
                        style={{ background: 'var(--rs-accent)', boxShadow: '0 0 0 1px rgba(99,102,241,0.5), 0 4px 12px rgba(99,102,241,0.3)' }}
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-accent-light)'; (e.currentTarget as HTMLElement).style.transform = 'translateY(-2px)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-accent)'; (e.currentTarget as HTMLElement).style.transform = 'translateY(0)'; }}
                        onMouseDown={(e) => { (e.currentTarget as HTMLElement).style.transform = 'translateY(1px)'; }}
                        onMouseUp={(e) => { (e.currentTarget as HTMLElement).style.transform = 'translateY(-2px)'; }}
                    >
                        <Plus size={14} strokeWidth={2.5} />
                        Nouveau bloc
                    </button>
                )}
            </div>
        );
    };

    // ── Filled state ──────────────────────────────────────────────────────────
    const renderBlock = () => {
        if (!selectedBlock) return null;
        return (
            <>
                {/* Header */}
                <div
                    className="flex items-center justify-between px-5 py-4 flex-none"
                    style={{ borderBottom: '1px solid var(--rs-border)', background: 'var(--rs-bg-elevated)' }}
                >
                    <div className="flex items-center gap-2.5">
                        <div
                            className="w-8 h-8 rounded-lg flex items-center justify-center flex-none"
                            style={{ background: `${selectedBlock.color}22`, border: `1px solid ${selectedBlock.color}55` }}
                        >
                            <Type size={14} style={{ color: selectedBlock.color }} />
                        </div>
                        <div>
                            <p className="text-xs font-bold" style={{ color: 'var(--rs-text-primary)' }}>Édition du bloc</p>
                            <p className="text-[10px] font-mono" style={{ color: 'var(--rs-text-muted)' }}>#{selectedBlock.id.slice(-6)}</p>
                        </div>
                    </div>
                    {/* Color swatch */}
                    <div
                        className="w-5 h-5 rounded-full border"
                        style={{ background: selectedBlock.color, borderColor: `${selectedBlock.color}88` }}
                    />
                </div>

                {/* Fields */}
                <div className="flex-1 overflow-y-auto p-5 space-y-5">

                    {/* Texte */}
                    <div>
                        <Label icon={<Type size={11} />}>Texte du dialogue</Label>
                        <textarea
                            value={selectedBlock.text}
                            onFocus={(e) => { snapshotHistory(); focusProps.onFocus(e as any); }}
                            onBlur={focusProps.onBlur as any}
                            onChange={(e) => updateDialogue(selectedBlock.id, { text: e.target.value }, true)}
                            placeholder="Entrez le texte du dialogue..."
                            rows={3}
                            style={{ ...inputStyle, resize: 'none', lineHeight: '1.5' }}
                        />
                        {selectedBlock.text.length / selectedBlock.duration > MAX_CHARS_PER_SECOND && (
                            <div className="mt-2 flex items-start gap-1.5 p-2 rounded-md bg-red-500/10 border border-red-500/20 text-red-500 text-[10px] leading-snug">
                                <AlertTriangle size={12} className="flex-none mt-0.5" />
                                <span>
                                    Le texte défilera trop vite ({Math.round(selectedBlock.text.length / selectedBlock.duration)} car/s).
                                </span>
                            </div>
                        )}
                    </div>

                    {/* Personnage */}
                    <div>
                        <Label icon={<User size={11} />}>Personnage</Label>
                        <input
                            type="text"
                            value={selectedBlock.characterName}
                            onFocus={(e) => { snapshotHistory(); focusProps.onFocus(e as any); }}
                            onBlur={focusProps.onBlur as any}
                            onChange={(e) => updateDialogue(selectedBlock.id, { characterName: e.target.value }, true)}
                            style={{ ...inputStyle }}
                        />
                    </div>

                    {/* Couleur */}
                    <div>
                        <Label icon={<Palette size={11} />}>Couleur du bloc</Label>
                        <div className="flex items-center gap-3">
                            {/* Preset swatches */}
                            {['#6366f1', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#06b6d4', '#f97316', '#ec4899'].map((c) => (
                                <button
                                    key={c}
                                    onClick={() => { snapshotHistory(); updateDialogue(selectedBlock.id, { color: c }); }}
                                    className="w-6 h-6 rounded-lg flex-none transition-all duration-100 active:scale-90"
                                    style={{
                                        background: c,
                                        border: selectedBlock.color === c ? '2px solid white' : '2px solid transparent',
                                        boxShadow: selectedBlock.color === c ? `0 0 8px ${c}aa` : 'none',
                                        transform: selectedBlock.color === c ? 'scale(1.15)' : '',
                                    }}
                                    title={c}
                                />
                            ))}
                            {/* Custom picker */}
                            <label className="relative w-6 h-6 rounded-lg overflow-hidden cursor-pointer flex-none transition-all duration-100 hover:scale-110 active:scale-90"
                                style={{ border: '1px dashed var(--rs-text-muted)', background: 'var(--rs-bg-muted)' }}
                                title="Couleur personnalisée"
                            >
                                <Palette size={12} style={{ position: 'absolute', inset: 0, margin: 'auto', color: 'var(--rs-text-muted)' }} />
                                <input
                                    type="color"
                                    value={selectedBlock.color}
                                    onFocus={() => snapshotHistory()}
                                    onChange={(e) => updateDialogue(selectedBlock.id, { color: e.target.value }, true)}
                                    className="opacity-0 absolute inset-0 w-full h-full cursor-pointer"
                                />
                            </label>
                        </div>
                    </div>

                    {/* Piste + Durée */}
                    <div className="grid grid-cols-2 gap-3">
                        <div>
                            <Label icon={<Layers size={11} />}>Piste</Label>
                            <input
                                type="number"
                                min={1}
                                max={totalLanes}
                                value={selectedBlock.lane + 1}
                                onFocus={(e) => { snapshotHistory(); focusProps.onFocus(e as any); }}
                                onBlur={focusProps.onBlur as any}
                                onChange={(e) => {
                                    const v = parseInt(e.target.value);
                                    if (!isNaN(v) && v >= 1 && v <= totalLanes)
                                        updateDialogue(selectedBlock.id, { lane: v - 1 }, true);
                                }}
                                style={{ ...inputStyle, textAlign: 'center', fontFamily: "'Roboto Mono', monospace" }}
                            />
                        </div>
                        <div>
                            <Label icon={<Clock size={11} />}>Durée (s)</Label>
                            <div className="flex gap-1.5">
                                <input
                                    type="number"
                                    step={0.1}
                                    min={0.1}
                                    value={localDuration}
                                    onChange={handleDurationChange}
                                    onFocus={(e) => { setIsEditingDuration(true); snapshotHistory(); focusProps.onFocus(e as any); }}
                                    onBlur={(e) => { setIsEditingDuration(false); focusProps.onBlur(e as any); }}
                                    style={{ ...inputStyle, flex: 1, textAlign: 'right', fontFamily: "'Roboto Mono', monospace" }}
                                />
                                <button
                                    onClick={handleResetDuration}
                                    className="w-8 h-8 flex-none flex items-center justify-center rounded-lg transition-all duration-100 active:scale-90"
                                    style={{ background: 'var(--rs-bg-muted)', border: '1px solid var(--rs-border)', color: 'var(--rs-text-secondary)' }}
                                    title="Réinitialiser (2s)"
                                    onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-border)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                                    onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-bg-muted)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                                >
                                    <RotateCcw size={12} />
                                </button>
                            </div>
                        </div>
                    </div>

                    {/* Hauteur de Piste */}
                    <div style={{ paddingBottom: 10 }}>
                        <div className="flex justify-between items-center mb-1">
                            <Label icon={<Expand size={11} />}>Hauteur de Piste (Zoom)</Label>
                            {selectedBlock && getLaneHeight(laneHeights, selectedBlock.lane) !== LANE_HEIGHT && (
                                <button
                                    onClick={() => setLaneHeight(selectedBlock.lane, LANE_HEIGHT)}
                                    className="text-[9px] font-bold text-indigo-400 hover:text-indigo-300 transition-colors"
                                >
                                    RÉINITIALISER
                                </button>
                            )}
                        </div>
                        <input
                            type="range"
                            min={20}
                            max={200}
                            step={5}
                            value={selectedBlock ? getLaneHeight(laneHeights, selectedBlock.lane) : LANE_HEIGHT}
                            onChange={(e) => {
                                if (selectedBlock) {
                                    setLaneHeight(selectedBlock.lane, parseInt(e.target.value));
                                }
                            }}
                            className="w-full accent-indigo-500"
                            style={{ height: '4px', appearance: 'none', background: 'var(--rs-bg-muted)', borderRadius: '2px', outline: 'none' }}
                        />
                        <div className="flex justify-between mt-1 text-[9px] text-neutral-500 font-mono">
                            <span>20px</span>
                            <span>{selectedBlock ? getLaneHeight(laneHeights, selectedBlock.lane) : LANE_HEIGHT}px</span>
                            <span>200px</span>
                        </div>
                    </div>

                    {/* Timecode info box */}
                    <div
                        className="rounded-xl p-3 space-y-1.5"
                        style={{ background: 'var(--rs-bg-base)', border: '1px solid var(--rs-border-subtle)' }}
                    >
                        <div className="flex justify-between text-[10px]">
                            <span style={{ color: 'var(--rs-text-muted)' }}>Début</span>
                            <span className="font-mono font-bold" style={{ color: 'var(--rs-text-secondary)' }}>
                                {selectedBlock.startTime.toFixed(3)}s
                            </span>
                        </div>
                        <div className="h-px" style={{ background: 'var(--rs-border-subtle)' }} />
                        <div className="flex justify-between text-[10px]">
                            <span style={{ color: 'var(--rs-text-muted)' }}>Fin</span>
                            <span className="font-mono font-bold" style={{ color: 'var(--rs-text-secondary)' }}>
                                {(selectedBlock.startTime + selectedBlock.duration).toFixed(3)}s
                            </span>
                        </div>
                    </div>
                </div>

                {/* Footer: Delete */}
                <div className="flex-none p-4" style={{ borderTop: '1px solid var(--rs-border)', background: 'var(--rs-bg-elevated)' }}>
                    <button
                        onClick={handleDelete}
                        className="w-full flex items-center justify-center gap-2 py-2 rounded-xl text-xs font-bold transition-all duration-150 active:scale-95"
                        style={deleteConfirm ? {
                            background: 'rgba(239,68,68,0.15)',
                            border: '1px solid rgba(239,68,68,0.6)',
                            color: '#f87171',
                        } : {
                            background: 'transparent',
                            border: '1px solid var(--rs-border)',
                            color: 'var(--rs-text-muted)',
                        }}
                        onMouseEnter={(e) => {
                            if (!deleteConfirm) {
                                (e.currentTarget as HTMLElement).style.background = 'rgba(239,68,68,0.08)';
                                (e.currentTarget as HTMLElement).style.borderColor = 'rgba(239,68,68,0.4)';
                                (e.currentTarget as HTMLElement).style.color = '#f87171';
                            }
                        }}
                        onMouseLeave={(e) => {
                            if (!deleteConfirm) {
                                (e.currentTarget as HTMLElement).style.background = 'transparent';
                                (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)';
                                (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-muted)';
                            }
                        }}
                    >
                        <Trash2 size={13} />
                        {deleteConfirm ? 'Confirmer la suppression ?' : 'Supprimer le bloc'}
                    </button>
                </div>
            </>
        );
    };

    // ── Mixer state ───────────────────────────────────────────────────────────
    const renderMixer = () => {
        return (
            <div className="flex-none p-4 w-full" style={{ borderTop: '1px solid var(--rs-border)', background: 'var(--rs-bg-base)' }}>
                <div className="flex items-center gap-2 mb-3">
                    <Sliders size={14} style={{ color: 'var(--rs-text-muted)' }} />
                    <span className="text-xs font-bold uppercase tracking-widest" style={{ color: 'var(--rs-text-secondary)' }}>Mixeur Audio</span>
                </div>
                <div className="space-y-4">
                    {audioTracks.map(track => (
                        <div key={track.id} className="flex flex-col gap-1.5">
                            <div className="flex items-center justify-between">
                                <span className="text-[10px] font-bold" style={{ color: 'var(--rs-text-primary)' }}>{track.name}</span>
                                <div className="flex items-center gap-1.5">
                                    <button
                                        onClick={() => updateAudioTrack(track.id, { muted: !track.muted })}
                                        className="w-5 h-5 flex items-center justify-center rounded transition-colors"
                                        style={{
                                            background: track.muted ? 'rgba(239,68,68,0.15)' : 'transparent',
                                            color: track.muted ? '#ef4444' : 'var(--rs-text-muted)'
                                        }}
                                        title={track.muted ? "Démuter" : "Muter"}
                                    >
                                        <VolumeX size={10} />
                                    </button>
                                    <button
                                        onClick={() => updateAudioTrack(track.id, { solo: !track.solo })}
                                        className="w-5 h-5 flex items-center justify-center rounded text-[9px] font-bold transition-colors uppercase"
                                        style={{
                                            background: track.solo ? 'rgba(234,179,8,0.15)' : 'transparent',
                                            color: track.solo ? '#eab308' : 'var(--rs-text-muted)'
                                        }}
                                        title="Solo"
                                    >
                                        S
                                    </button>
                                </div>
                            </div>
                            <div className="flex items-center gap-2.5">
                                <Volume2 size={12} style={{ color: track.muted ? 'var(--rs-text-muted)' : 'var(--rs-text-secondary)' }} />
                                <input
                                    type="range"
                                    min="0"
                                    max="1"
                                    step="0.01"
                                    value={track.volume}
                                    onChange={(e) => updateAudioTrack(track.id, { volume: parseFloat(e.target.value) })}
                                    className="flex-1 accent-indigo-500"
                                    style={{ height: '4px', appearance: 'none', background: 'var(--rs-bg-muted)', borderRadius: '2px', outline: 'none', opacity: track.muted ? 0.4 : 1 }}
                                />
                            </div>
                        </div>
                    ))}
                </div>
            </div>
        );
    };

    return (
        <div
            style={{ width, background: 'var(--rs-bg-surface)', borderLeft: '1px solid var(--rs-border)' }}
            className="flex flex-col overflow-hidden relative flex-none shadow-2xl"
        >
            {/* Resize Handle */}
            <div
                onMouseDown={startResizing}
                className="absolute left-0 top-0 bottom-0 w-1.5 z-50 group cursor-ew-resize"
            >
                <div
                    className="absolute inset-y-0 left-0 w-[2px] transition-all duration-150"
                    style={{ background: 'transparent' }}
                    onMouseEnter={(e) => ((e.currentTarget as HTMLElement).style.background = 'var(--rs-accent)')}
                    onMouseLeave={(e) => ((e.currentTarget as HTMLElement).style.background = 'transparent')}
                />
            </div>

            <div className="flex-1 overflow-y-auto flex flex-col w-full relative">
                {selectedBlock ? renderBlock() : <div className="flex-1 relative">{renderEmpty()}</div>}
            </div>

            {renderMixer()}
        </div>
    );
};