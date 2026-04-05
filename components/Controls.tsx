import React from 'react';
import { useAppStore } from '../store';
import { Plus, ZoomIn, ZoomOut, Layers, Minus, MousePointer2, Settings2 } from 'lucide-react';

export const Controls: React.FC = () => {
    // PERF: Granular selectors
    const zoomLevel = useAppStore((s) => s.zoomLevel);
    const syncOffset = useAppStore((s) => s.syncOffset);
    const totalLanes = useAppStore((s) => s.totalLanes);
    const defaultBlockDuration = useAppStore((s) => s.defaultBlockDuration);
    const setZoomLevel = useAppStore((s) => s.setZoomLevel);
    const setSyncOffset = useAppStore((s) => s.setSyncOffset);
    const setTotalLanes = useAppStore((s) => s.setTotalLanes);
    const addDialogue = useAppStore((s) => s.addDialogue);

    const handleAddBlock = () => {
        const currentTime = useAppStore.getState().currentTime;
        addDialogue({
            id: Date.now().toString(),
            text: 'Nouveau dialogue',
            startTime: currentTime,
            duration: defaultBlockDuration,
            characterName: 'Perso',
            color: '#6366f1',
            lane: 0,
        });
    };

    const zoomMin = 50;
    const zoomMax = 600;
    const zoomStep = 50;
    const zoomPercent = Math.round(((zoomLevel - zoomMin) / (zoomMax - zoomMin)) * 100);

    return (
        <div
            className="h-14 flex items-center justify-between px-5 select-none flex-none z-20 relative"
            style={{ background: 'var(--rs-bg-surface)', borderTop: '1px solid var(--rs-border)' }}
        >
            {/* ── Left: Status hint ─────────────────────────────────── */}
            <div className="flex items-center gap-2 w-1/4">
                <div
                    className="flex items-center gap-2 px-2.5 py-1 rounded-md text-xs"
                    style={{ background: 'var(--rs-bg-muted)', color: 'var(--rs-text-muted)' }}
                >
                    <MousePointer2 size={12} />
                    <span className="font-medium tracking-wide">Timeline</span>
                </div>
            </div>

            {/* ── Center: Main actions ────────────────────────────────── */}
            <div className="flex items-center justify-center gap-4 flex-1">

                {/* Add block button */}
                <button
                    onClick={handleAddBlock}
                    className="group flex items-center gap-2 px-4 h-8 rounded-lg text-xs font-semibold text-white transition-all duration-150 active:scale-95"
                    style={{
                        background: 'var(--rs-accent)',
                        boxShadow: '0 1px 0 rgba(255,255,255,0.08) inset, 0 0 0 1px rgba(99,102,241,0.5)',
                    }}
                    onMouseEnter={(e) => {
                        (e.currentTarget as HTMLButtonElement).style.background = 'var(--rs-accent-light)';
                        (e.currentTarget as HTMLButtonElement).style.boxShadow = '0 0 14px rgba(99,102,241,0.4), 0 0 0 1px rgba(129,140,248,0.5)';
                        (e.currentTarget as HTMLButtonElement).style.transform = 'translateY(-1px)';
                    }}
                    onMouseLeave={(e) => {
                        (e.currentTarget as HTMLButtonElement).style.background = 'var(--rs-accent)';
                        (e.currentTarget as HTMLButtonElement).style.boxShadow = '0 1px 0 rgba(255,255,255,0.08) inset, 0 0 0 1px rgba(99,102,241,0.5)';
                        (e.currentTarget as HTMLButtonElement).style.transform = '';
                    }}
                >
                    <Plus size={14} strokeWidth={2.5} />
                    Ajouter un bloc
                </button>

                {/* Divider */}
                <div className="w-px h-6 opacity-30" style={{ background: 'var(--rs-border)' }} />

                {/* Zoom Controls */}
                <div
                    className="flex items-center rounded-lg overflow-hidden"
                    style={{ background: 'var(--rs-bg-muted)', border: '1px solid var(--rs-border)' }}
                >
                    <button
                        onClick={() => setZoomLevel(Math.max(zoomMin, zoomLevel - zoomStep))}
                        className="flex items-center justify-center w-7 h-8 transition-all duration-100 active:scale-90"
                        style={{ color: 'var(--rs-text-secondary)' }}
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; (e.currentTarget as HTMLElement).style.background = 'var(--rs-border)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; (e.currentTarget as HTMLElement).style.background = ''; }}
                        title="Zoom arrière (−)"
                    >
                        <ZoomOut size={14} />
                    </button>

                    {/* Zoom bar + label */}
                    <div className="flex flex-col items-center justify-center px-3 min-w-[72px]">
                        {/* Mini progress bar */}
                        <div className="w-12 h-1 rounded-full overflow-hidden mb-0.5" style={{ background: 'var(--rs-border)' }}>
                            <div
                                className="h-full rounded-full transition-all duration-200"
                                style={{ width: `${zoomPercent}%`, background: 'var(--rs-accent)' }}
                            />
                        </div>
                        <span className="text-[10px] font-mono font-bold" style={{ color: 'var(--rs-text-secondary)' }}>
                            {zoomLevel} PPS
                        </span>
                    </div>

                    <button
                        onClick={() => setZoomLevel(Math.min(zoomMax, zoomLevel + zoomStep))}
                        className="flex items-center justify-center w-7 h-8 transition-all duration-100 active:scale-90"
                        style={{ color: 'var(--rs-text-secondary)' }}
                        onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; (e.currentTarget as HTMLElement).style.background = 'var(--rs-border)'; }}
                        onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; (e.currentTarget as HTMLElement).style.background = ''; }}
                        title="Zoom avant (+)"
                    >
                        <ZoomIn size={14} />
                    </button>
                </div>
            </div>

            {/* ── Right: Lane & Offset controls ─────────────────────── */}
            <div className="flex items-center justify-end gap-5 w-1/4">

                {/* Lane Control */}
                <div className="flex items-center gap-2">
                    <Layers size={13} style={{ color: 'var(--rs-text-muted)' }} />
                    <div
                        className="flex items-center rounded-lg overflow-hidden"
                        style={{ background: 'var(--rs-bg-muted)', border: '1px solid var(--rs-border)' }}
                    >
                        <button
                            onClick={() => setTotalLanes(Math.max(1, totalLanes - 1))}
                            className="flex items-center justify-center w-6 h-7 transition-all duration-100 active:scale-90"
                            style={{ color: 'var(--rs-text-secondary)', borderRight: '1px solid var(--rs-border)' }}
                            onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-border)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                            onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = ''; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                        >
                            <Minus size={10} />
                        </button>
                        <span
                            className="w-7 text-center text-xs font-mono font-bold"
                            style={{ color: 'var(--rs-text-primary)' }}
                            title="Nombre de pistes"
                        >
                            {totalLanes}
                        </span>
                        <button
                            onClick={() => setTotalLanes(Math.min(10, totalLanes + 1))}
                            className="flex items-center justify-center w-6 h-7 transition-all duration-100 active:scale-90"
                            style={{ color: 'var(--rs-text-secondary)', borderLeft: '1px solid var(--rs-border)' }}
                            onMouseEnter={(e) => { (e.currentTarget as HTMLElement).style.background = 'var(--rs-border)'; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-primary)'; }}
                            onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = ''; (e.currentTarget as HTMLElement).style.color = 'var(--rs-text-secondary)'; }}
                        >
                            <Plus size={10} />
                        </button>
                    </div>
                </div>

                {/* Sync Offset */}
                <div className="flex items-center gap-2">
                    <Settings2 size={13} style={{ color: 'var(--rs-text-muted)' }} />
                    <div className="flex items-center gap-1">
                        <input
                            type="number"
                            value={Math.round(syncOffset * 1000)}
                            onChange={(e) => setSyncOffset(Number(e.target.value) / 1000)}
                            className="w-14 h-7 text-xs text-right font-mono rounded-lg px-2 outline-none transition-all duration-150"
                            style={{
                                background: 'var(--rs-bg-muted)',
                                border: '1px solid var(--rs-border)',
                                color: 'var(--rs-text-primary)',
                            }}
                            onFocus={(e) => { (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-accent)'; (e.currentTarget as HTMLElement).style.boxShadow = '0 0 0 3px var(--rs-accent-glow)'; }}
                            onBlur={(e) => { (e.currentTarget as HTMLElement).style.borderColor = 'var(--rs-border)'; (e.currentTarget as HTMLElement).style.boxShadow = ''; }}
                            title="Décalage global (ms)"
                        />
                        <span className="text-[10px] font-mono" style={{ color: 'var(--rs-text-muted)' }}>ms</span>
                    </div>
                </div>
            </div>
        </div>
    );
};