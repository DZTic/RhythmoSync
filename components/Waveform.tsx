import React, { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { useAppStore } from '../store';
import { SYNC_LINE_POSITION_X } from '../types';

// The data shape returned by the Rust `generate_waveform` command
interface WaveformData {
  peaks: number[]; // Interleaved [min, max] per bucket: [min0, max0, min1, max1, ...]
  duration: number;
  sample_rate: number;
  num_channels: number;
}

// How many peak buckets we ask Rust to generate.
const NUM_WAVEFORM_SAMPLES = 4000;

// ─── TILING CONFIG ────────────────────────────────────────────────────────────
// Each tile covers TILE_DURATION seconds of audio.
// Only the tiles within TILE_BUFFER tiles of the viewport are rendered.
const TILE_DURATION = 30; // seconds per tile
const TILE_BUFFER = 2;  // extra tiles rendered on each side of the visible range

const WAVEFORM_HEIGHT = 60;


// ============================================================
// Sub-component: A SINGLE TILE — renders only its own peaks
// slice onto its own small canvas.
// ============================================================
interface TileProps {
  peaks: number[];          // Full peaks array
  tileIndex: number;        // Which tile this is
  totalTiles: number;       // Total number of tiles
  tileWidthPx: number;      // Width in pixels of one tile
  totalWidthPx: number;     // Total waveform width in pixels (for last-tile clipping)
  height: number;
}

const WaveformTile = React.memo(({
  peaks, tileIndex, totalTiles, tileWidthPx, totalWidthPx, height,
}: TileProps) => {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || peaks.length < 2) return;

    // Last tile might be narrower than tileWidthPx
    const isLast = tileIndex === totalTiles - 1;
    const tileStartPx = tileIndex * tileWidthPx;
    const actualWidth = isLast ? totalWidthPx - tileStartPx : tileWidthPx;

    canvas.width = actualWidth;
    canvas.height = height;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    ctx.fillStyle = '#111827';
    ctx.fillRect(0, 0, actualWidth, height);

    const numBuckets = peaks.length / 2;
    const bucketWidth = totalWidthPx / numBuckets;

    // Determine which peak buckets belong to this tile
    const startBucket = Math.floor(tileStartPx / bucketWidth);
    const endBucket = Math.min(numBuckets, Math.ceil((tileStartPx + actualWidth) / bucketWidth));

    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, 'rgba(99, 102, 241, 0.9)');
    gradient.addColorStop(0.5, 'rgba(139, 92, 246, 0.6)');
    gradient.addColorStop(1, 'rgba(99, 102, 241, 0.9)');

    ctx.fillStyle = gradient;

    const midY = height / 2;

    for (let i = startBucket; i < endBucket; i++) {
      const minVal = peaks[i * 2];
      const maxVal = peaks[i * 2 + 1];

      // X position relative to this tile's canvas (not the global canvas)
      const globalX = i * bucketWidth;
      const localX = globalX - tileStartPx;
      const topY = midY + minVal * midY;
      const bottomY = midY + maxVal * midY;
      const barHeight = Math.max(1, bottomY - topY);

      ctx.fillRect(localX, topY, Math.max(1, bucketWidth - 0.5), barHeight);
    }

    // Center line
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.06)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, midY);
    ctx.lineTo(actualWidth, midY);
    ctx.stroke();

  }, [peaks, tileIndex, totalTiles, tileWidthPx, totalWidthPx, height]);

  const isLast = tileIndex === totalTiles - 1;
  const tileStartPx = tileIndex * tileWidthPx;
  const actualWidth = isLast ? Math.max(1, totalWidthPx - tileStartPx) : tileWidthPx;

  return (
    <canvas
      ref={canvasRef}
      style={{
        position: 'absolute',
        left: tileStartPx,
        top: 0,
        width: actualWidth,
        height,
        display: 'block',
        imageRendering: 'pixelated',
      }}
    />
  );
}, (prev, next) =>
  prev.peaks === next.peaks &&
  prev.tileIndex === next.tileIndex &&
  prev.tileWidthPx === next.tileWidthPx &&
  prev.totalWidthPx === next.totalWidthPx &&
  prev.height === next.height
);


// ============================================================
// Main Waveform component
// ============================================================
export const Waveform: React.FC = () => {
  const containerRef = useRef<HTMLDivElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const animationRef = useRef<number>(0);

  // Track the current scroll offset so we can compute visible tiles
  const scrollXRef = useRef<number>(0);
  const [visibleTileRange, setVisibleTileRange] = useState<{ first: number; last: number }>({ first: 0, last: 0 });
  const viewportWidthRef = useRef<number>(800); // updated by ResizeObserver

  const { videoElement, videoPath, zoomLevel, syncOffset, duration } = useAppStore((s) => ({
    videoElement: s.videoElement,
    videoPath: s.videoPath,
    zoomLevel: s.zoomLevel,
    syncOffset: s.syncOffset,
    duration: s.duration,
  }));

  const [waveformData, setWaveformData] = useState<WaveformData | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Interaction State
  const isDragging = useRef(false);
  const dragStart = useRef<{ x: number; time: number } | null>(null);

  // ─── WAVEFORM GENERATION ────────────────────────────────────────────────────
  useEffect(() => {
    if (!videoPath) {
      setWaveformData(null);
      setError(null);
      return;
    }

    let isCancelled = false;
    setIsLoading(true);
    setError(null);
    setWaveformData(null);

    invoke<WaveformData>('generate_waveform', {
      videoPath,
      numSamples: NUM_WAVEFORM_SAMPLES,
    })
      .then((data) => {
        if (!isCancelled) {
          setWaveformData(data);
          setIsLoading(false);
        }
      })
      .catch((err) => {
        if (!isCancelled) {
          console.error('[Waveform] Rust generation failed:', err);
          setError(String(err));
          setIsLoading(false);
        }
      });

    return () => { isCancelled = true; };
  }, [videoPath]);

  // ─── VIEWPORT SIZE TRACKING ─────────────────────────────────────────────────
  useEffect(() => {
    if (!containerRef.current) return;
    const ro = new ResizeObserver((entries) => {
      for (const entry of entries) {
        viewportWidthRef.current = entry.contentRect.width;
      }
    });
    ro.observe(containerRef.current);
    return () => ro.disconnect();
  }, []);

  // ─── TILING MATH ────────────────────────────────────────────────────────────
  const totalCanvasWidth = Math.max(
    (waveformData?.duration ?? duration) * zoomLevel,
    100
  );
  const tileWidthPx = TILE_DURATION * zoomLevel;
  const totalTiles = Math.ceil(totalCanvasWidth / tileWidthPx);

  // ─── SCROLL SYNC LOOP (60 FPS) ─────────────────────────────────────────────
  // Writes CSS transform directly to the DOM (no React state → no re-renders).
  // Also recomputes which tiles should be active based on the new scroll offset.
  useEffect(() => {
    let lastX = -Infinity;
    let lastFirst = -1;
    let lastLast = -1;

    const loop = () => {
      animationRef.current = requestAnimationFrame(loop);

      if (!wrapperRef.current || !videoElement) return;

      const currentTime = videoElement.currentTime;
      const newX = SYNC_LINE_POSITION_X - (currentTime + syncOffset) * zoomLevel;

      // Apply transform
      if (Math.abs(newX - lastX) > 0.5) {
        wrapperRef.current.style.transform = `translateX(${newX}px) translateZ(0)`;
        lastX = newX;
      }

      scrollXRef.current = newX;

      // Compute which tiles are visible in the viewport
      // Viewport in "canvas space" = [-newX, -newX + viewportWidth]
      const canvasLeft = -newX;
      const canvasRight = canvasLeft + viewportWidthRef.current;

      const rawFirst = Math.floor(canvasLeft / tileWidthPx) - TILE_BUFFER;
      const rawLast = Math.ceil(canvasRight / tileWidthPx) + TILE_BUFFER;

      const clampedFirst = Math.max(0, Math.min(rawFirst, totalTiles - 1));
      const clampedLast = Math.max(0, Math.min(rawLast, totalTiles - 1));

      if (clampedFirst !== lastFirst || clampedLast !== lastLast) {
        lastFirst = clampedFirst;
        lastLast = clampedLast;
        setVisibleTileRange({ first: clampedFirst, last: clampedLast });
      }
    };

    animationRef.current = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(animationRef.current);
  }, [videoElement, zoomLevel, syncOffset, tileWidthPx, totalTiles]);

  // ─── VISIBLE TILE INDICES ───────────────────────────────────────────────────
  const visibleTiles = useMemo(() => {
    const indices: number[] = [];
    for (let i = visibleTileRange.first; i <= visibleTileRange.last; i++) {
      indices.push(i);
    }
    return indices;
  }, [visibleTileRange.first, visibleTileRange.last]);

  // ─── INTERACTION HANDLERS ──────────────────────────────────────────────────
  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    if (!videoElement) return;
    e.preventDefault();
    isDragging.current = true;
    dragStart.current = { x: e.clientX, time: videoElement.currentTime };
    document.body.style.cursor = 'grabbing';
  }, [videoElement]);

  const handleMouseMove = useCallback((e: React.MouseEvent) => {
    if (!isDragging.current || !dragStart.current || !videoElement) return;
    e.preventDefault();
    const deltaX = e.clientX - dragStart.current.x;
    const timeShift = deltaX / zoomLevel;
    const newTime = dragStart.current.time - timeShift;
    videoElement.currentTime = Math.max(0, Math.min(newTime, videoElement.duration || 0));
  }, [videoElement, zoomLevel]);

  const handleMouseUp = useCallback((e: React.MouseEvent) => {
    if (isDragging.current && dragStart.current && videoElement) {
      const deltaX = Math.abs(e.clientX - dragStart.current.x);
      if (deltaX < 5 && containerRef.current) {
        const rect = containerRef.current.getBoundingClientRect();
        const relativeX = e.clientX - rect.left;
        const timeOffset = (relativeX - SYNC_LINE_POSITION_X) / zoomLevel;
        const targetTime = videoElement.currentTime + timeOffset;
        videoElement.currentTime = Math.max(0, Math.min(targetTime, videoElement.duration || 0));
      }
    }
    isDragging.current = false;
    dragStart.current = null;
    document.body.style.cursor = 'default';
  }, [videoElement, zoomLevel]);

  // ─── RENDER ───────────────────────────────────────────────────────────────
  return (
    <div
      ref={containerRef}
      className="w-full bg-gray-900 relative overflow-hidden border-b border-gray-800 select-none"
      style={{ height: WAVEFORM_HEIGHT }}
      onMouseDown={handleMouseDown}
      onMouseMove={handleMouseMove}
      onMouseUp={handleMouseUp}
      onMouseLeave={handleMouseUp}
    >
      {/* Moving wrapper — translated by the sync loop */}
      <div
        ref={wrapperRef}
        className="absolute top-0 left-0 will-change-transform"
        style={{ height: WAVEFORM_HEIGHT, width: totalCanvasWidth, position: 'relative' }}
      >
        {/* Tiled canvas rendering — only visible tiles are mounted */}
        {waveformData && visibleTiles.map((tileIndex) => (
          <WaveformTile
            key={tileIndex}
            peaks={waveformData.peaks}
            tileIndex={tileIndex}
            totalTiles={totalTiles}
            tileWidthPx={tileWidthPx}
            totalWidthPx={totalCanvasWidth}
            height={WAVEFORM_HEIGHT}
          />
        ))}
      </div>

      {/* Loading overlay */}
      {isLoading && (
        <div className="absolute inset-0 flex items-center justify-center gap-2 z-10 pointer-events-none">
          <div className="flex gap-1">
            {[0, 1, 2, 3, 4].map((i) => (
              <div
                key={i}
                className="w-0.5 bg-indigo-400 rounded-full animate-pulse"
                style={{
                  height: 10 + Math.random() * 30,
                  animationDelay: `${i * 0.1}s`,
                }}
              />
            ))}
          </div>
          <span className="text-[10px] text-indigo-400 font-mono tracking-widest uppercase ml-1">
            Analyse audio...
          </span>
        </div>
      )}

      {/* Error state */}
      {error && !isLoading && (
        <div className="absolute inset-0 flex items-center justify-center z-10 pointer-events-none">
          <span className="text-[10px] text-red-400/60 font-mono">
            ⚠ Pas de piste audio détectée
          </span>
        </div>
      )}

      {/* Empty state (no video) */}
      {!videoPath && !isLoading && (
        <div className="absolute inset-0 flex items-center justify-center z-10 pointer-events-none">
          <span className="text-[10px] text-gray-600 font-mono tracking-widest">
            - AUDIO WAVEFORM -
          </span>
        </div>
      )}
    </div>
  );
};