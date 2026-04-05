import React, { useEffect, useRef, useState, useCallback } from 'react';
import { useAppStore } from '../store';
import { Play, Pause, SkipBack, ChevronLeft, ChevronRight, Gauge, Zap, Film, Database } from 'lucide-react';
import { useFrameCache } from './useFrameCache';

// Interface definition for TypeScript compatibility with requestVideoFrameCallback
interface VideoFrameCallbackMetadata {
  presentationTime: number;
  expectedDisplayTime: number;
  width: number;
  height: number;
  mediaTime: number;
  presentedFrames: number;
  processingDuration?: number;
}

export const VideoPlayer: React.FC = () => {
  const videoRef = useRef<HTMLVideoElement>(null);
  // PERF: Granular selectors — VideoPlayer only re-renders when its own
  // relevant state changes. Actions (set*) are stable references in Zustand
  // so subscribing to them is effectively free, but we still list them
  // individually for clarity.
  const videoSource = useAppStore((s) => s.videoSource);
  const isPlaying = useAppStore((s) => s.isPlaying);
  const playbackRate = useAppStore((s) => s.playbackRate);
  const fps = useAppStore((s) => s.fps);
  const currentTime = useAppStore((s) => s.currentTime);
  const proxyVideoSource = useAppStore((s) => s.proxyVideoSource);
  const proxyStatus = useAppStore((s) => s.proxyStatus);
  const useProxy = useAppStore((s) => s.useProxy);
  const setVideoElement = useAppStore((s) => s.setVideoElement);
  const setCurrentTime = useAppStore((s) => s.setCurrentTime);
  const setDuration = useAppStore((s) => s.setDuration);
  const setIsPlaying = useAppStore((s) => s.setIsPlaying);
  const setPlaybackRate = useAppStore((s) => s.setPlaybackRate);
  const setFps = useAppStore((s) => s.setFps);
  const setUseProxy = useAppStore((s) => s.setUseProxy);
  const audioTracks = useAppStore((s) => s.audioTracks);

  // Cache de décodage — précharge les frames autour du point courant
  // On passe videoElement depuis le store (stable ref mise à jour via setVideoElement)
  const videoElementStore = useAppStore((s) => s.videoElement);
  const [isDetectingFps, setIsDetectingFps] = useState(false);
  const processedSourceRef = useRef<string | null>(null);
  const [cacheInfo, setCacheInfo] = useState<{ size: number; active: boolean }>({ size: 0, active: false });
  const overlayCanvasRef = useRef<HTMLCanvasElement>(null);

  const { getCachedFrame, prefetchAround, clearCache, cacheSize, abortPrefetch } = useFrameCache(
    videoElementStore,
    fps,
    !!videoSource && !isDetectingFps
  );

  useEffect(() => {
    if (videoRef.current) {
      setVideoElement(videoRef.current);
    }
  }, [videoSource, setVideoElement]);

  // Vider le cache quand la source vidéo change
  useEffect(() => {
    clearCache();
    setCacheInfo({ size: 0, active: false });
  }, [videoSource, clearCache]);

  // Reload video when source changes
  useEffect(() => {
    if (videoRef.current) {
      videoRef.current.load();
      // Reset processed source tracker when source prop changes
      if (videoSource !== processedSourceRef.current) {
        processedSourceRef.current = null;
      }
    }
  }, [videoSource]);

  // Sync playback rate
  useEffect(() => {
    if (videoRef.current) {
      videoRef.current.playbackRate = playbackRate;
    }
  }, [playbackRate]);

  // Sync volume and mute from audioTracks mixer
  useEffect(() => {
    if (videoRef.current && !isDetectingFps) {
      const originalTrack = audioTracks.find(t => t.isOriginal);
      if (originalTrack) {
        const anySolo = audioTracks.some(t => t.solo);
        const shouldMute = originalTrack.muted || (anySolo && !originalTrack.solo);
        videoRef.current.volume = originalTrack.volume;
        videoRef.current.muted = shouldMute;
      }
    }
  }, [audioTracks, isDetectingFps]);

  // PERFORMANCE: Throttle store updates to ~20Hz.
  // The RhythmoBand and Waveform components sync at 60fps via their own rAF loops
  // reading videoElement.currentTime directly. This store update is only for the timecode display.
  const lastTimeUpdateRef = useRef(0);
  const handleTimeUpdate = useCallback(() => {
    if (isDetectingFps) return;
    const now = performance.now();
    if (now - lastTimeUpdateRef.current < 50) return; // Max 20 updates/sec
    lastTimeUpdateRef.current = now;
    if (videoRef.current) {
      setCurrentTime(videoRef.current.currentTime);
    }
  }, [isDetectingFps, setCurrentTime]);

  /**
   * ALGORITHME DE DÉTECTION FPS (High Performance)
   * Utilise requestVideoFrameCallback pour mesurer le delta exact entre les frames rendues par le GPU.
   */
  const detectFrameRate = async () => {
    const video = videoRef.current;
    if (!video || processedSourceRef.current === videoSource) return;

    // Mark as processing to prevent loops
    processedSourceRef.current = videoSource;
    setIsDetectingFps(true);

    // Backup state
    const previousMuted = video.muted;

    // Setup for analysis: Mute and Play to get frame data
    video.muted = true;

    // We need the video to actually play to get callbacks
    try {
      await video.play().catch((err: Error) => {
        if (err.name !== 'AbortError') throw err;
      });
    } catch (e) {
      console.warn("Autoplay blocked, cannot auto-detect FPS without user interaction first.");
      setIsDetectingFps(false);
      video.muted = previousMuted;
      return;
    }

    let lastMediaTime = 0;
    let framesCollected = 0;
    const frameDiffs: number[] = [];
    const MAX_SAMPLES = 10; // 10 frames is enough for statistical significance

    // Recursive callback loop
    const callback = (now: number, metadata: VideoFrameCallbackMetadata) => {
      if (lastMediaTime > 0) {
        const diff = metadata.mediaTime - lastMediaTime;
        // Filter out invalid diffs (too small or paused)
        if (diff > 0.01) {
          frameDiffs.push(diff);
        }
      }
      lastMediaTime = metadata.mediaTime;
      framesCollected++;

      if (framesCollected < MAX_SAMPLES) {
        // Continue sampling
        if (videoRef.current) { // Check ref existence
          // @ts-ignore - TS definition might be missing in some envs
          videoRef.current.requestVideoFrameCallback(callback);
        }
      } else {
        // FINALIZE ANALYSIS
        if (frameDiffs.length > 0) {
          // Calculate Average Frame Duration
          const avgDuration = frameDiffs.reduce((a, b) => a + b, 0) / frameDiffs.length;
          const rawFps = 1 / avgDuration;

          // Standard Broadcast/Cinema Frame Rates
          const standards = [23.976, 24, 25, 29.97, 30, 48, 50, 59.94, 60];

          // Find closest standard
          const closest = standards.reduce((prev, curr) =>
            Math.abs(curr - rawFps) < Math.abs(prev - rawFps) ? curr : prev
          );

          // Use closest standard if within reasonable deviation (0.5), otherwise use raw rounded
          const detectedFps = Math.abs(closest - rawFps) < 0.5 ? closest : Math.round(rawFps);

          console.log(`[FPS Analyzer] Raw: ${rawFps.toFixed(4)} | Snapped: ${detectedFps}`);
          setFps(detectedFps);
        }

        // Cleanup
        video.pause();
        video.currentTime = 0;
        video.muted = previousMuted;
        setIsPlaying(false);
        setIsDetectingFps(false);
      }
    };

    // Start the loop
    if ('requestVideoFrameCallback' in (video as any)) {
      // @ts-ignore
      video.requestVideoFrameCallback(callback);
    } else {
      // Fallback for Safari/Older browsers: Default to 25 or use metadata if available
      console.warn("requestVideoFrameCallback not supported. Fallback to default.");
      setIsDetectingFps(false);
      video.pause();
      video.currentTime = 0;
      video.muted = previousMuted;
      setIsPlaying(false);
    }
  };

  const handleLoadedMetadata = () => {
    if (videoRef.current) {
      setDuration(videoRef.current.duration);
      // Trigger detection when metadata loads
      detectFrameRate();
    }
  };

  const handlePlay = () => {
    if (!isDetectingFps) {
      setIsPlaying(true);
      // STOP background caching which hogs the video decoder and causes stutter!
      abortPrefetch();
      if (videoRef.current) syncExternalAudio(videoRef.current);
      // Masquer le canvas cache overlay quand la lecture reprend
      const overlay = overlayCanvasRef.current;
      if (overlay) overlay.style.display = 'none';
    }
  };

  const handlePause = () => {
    if (!isDetectingFps) {
      setIsPlaying(false);
      // Déclencher le prefetch autour du point de pause
      if (videoRef.current) {
        syncExternalAudio(videoRef.current);
        const time = videoRef.current.currentTime;
        setCacheInfo({ size: cacheSize(), active: true });
        prefetchAround(time);
        setTimeout(() => setCacheInfo({ size: cacheSize(), active: false }), 2000);
      }
    }
  };

  // --- Controls Handlers ---

  const togglePlay = (e?: React.MouseEvent) => {
    e?.stopPropagation();
    if (!videoRef.current || isDetectingFps) return;
    // Lire l'état RÉEL de la vidéo (pas isPlaying du store qui peut être en retard)
    if (videoRef.current.paused) {
      // play() est une Promise — on attrape AbortError si pause() l'interrompt
      videoRef.current.play().catch((err: Error) => {
        if (err.name !== 'AbortError') console.error('Play error:', err);
      });
    } else {
      videoRef.current.pause();
    }
    syncExternalAudio(videoRef.current);
  };

  const handleStop = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!videoRef.current) return;
    videoRef.current.pause();
    videoRef.current.currentTime = 0;
    setIsPlaying(false);
  };

  const handleStep = (e: React.MouseEvent, frames: number) => {
    e.stopPropagation();
    if (!videoRef.current) return;
    videoRef.current.pause();
    setIsPlaying(false);
    const frameTime = 1 / fps;
    const targetTime = Math.max(0, videoRef.current.currentTime + (frames * frameTime));

    // Vérifier si la frame est dans le cache
    const cached = getCachedFrame(targetTime);
    if (cached && overlayCanvasRef.current) {
      const overlay = overlayCanvasRef.current;
      const ctx = overlay.getContext('2d');
      if (ctx) {
        overlay.width = cached.width;
        overlay.height = cached.height;
        ctx.drawImage(cached, 0, 0, overlay.width, overlay.height);
        overlay.style.display = 'block';
        // Laisser quand même la vidéo seek en arrière-plan
        videoRef.current.currentTime = targetTime;
        return;
      }
    }

    if (overlayCanvasRef.current) overlayCanvasRef.current.style.display = 'none';
    videoRef.current.currentTime = targetTime;
  };

  const cyclePlaybackRate = (e: React.MouseEvent) => {
    e.stopPropagation();
    const rates = [0.5, 1.0, 1.5, 2.0];
    const currentIndex = rates.indexOf(playbackRate);
    const nextRate = rates[(currentIndex + 1) % rates.length];
    setPlaybackRate(nextRate);
  };

  const formatTime = (time: number) => {
    const mins = Math.floor(time / 60);
    const secs = Math.floor(time % 60);
    const ms = Math.floor((time % 1) * 100);
    return `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}:${ms.toString().padStart(2, '0')}`;
  };

  // Synchronize external audio tracks (if any URLs are provided later)
  const syncExternalAudio = useCallback((videoEl: HTMLVideoElement) => {
    // A more robust sync would use requestAnimationFrame, but for MVP, when we seek or play/pause:
    const audioNodes = document.querySelectorAll<HTMLAudioElement>('.rs-external-audio');
    const anySolo = audioTracks.some(t => t.solo);

    audioNodes.forEach(audio => {
      const trackId = audio.dataset.trackId;
      const track = audioTracks.find(t => t.id === trackId);
      if (!track) return;

      const shouldMute = track.muted || (anySolo && !track.solo);
      audio.muted = shouldMute;
      audio.volume = track.volume;
      audio.playbackRate = videoEl.playbackRate;

      if (Math.abs(audio.currentTime - videoEl.currentTime) > 0.1) {
        audio.currentTime = videoEl.currentTime;
      }

      if (!videoEl.paused && audio.paused) {
        audio.play().catch(() => { });
      } else if (videoEl.paused && !audio.paused) {
        audio.pause();
      }
    });
  }, [audioTracks]);

  return (
    <div className="flex flex-col bg-gray-900 w-full h-full select-none" onClick={() => window.focus()}>

      {/* 1. Video Area (Flexible Height, Aspect Ratio Preserved) */}
      <div className="flex-1 relative bg-black flex items-center justify-center overflow-hidden">
        {isDetectingFps && (
          <div className="absolute inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm">
            <div className="flex flex-col items-center gap-3 text-blue-400 animate-pulse">
              <Zap size={32} />
              <span className="text-sm font-mono font-bold tracking-widest uppercase">Analyse FPS...</span>
            </div>
          </div>
        )}
        {videoSource ? (
          <>
            <video
              ref={videoRef}
              src={useProxy && proxyVideoSource ? proxyVideoSource : videoSource}
              className="max-w-full max-h-full object-contain"
              onTimeUpdate={handleTimeUpdate}
              onLoadedMetadata={handleLoadedMetadata}
              onPlay={handlePlay}
              onPause={handlePause}
              controls={false}
              playsInline
              crossOrigin="anonymous"
              onClick={togglePlay}
              onSeeked={(e) => syncExternalAudio(e.currentTarget)}
            />
            {/* External Audio Tracks */}
            {audioTracks.filter(t => !t.isOriginal && t.url).map(track => (
              <audio
                key={track.id}
                src={track.url!}
                className="rs-external-audio hidden"
                data-track-id={track.id}
                onLoadedMetadata={(e) => {
                  if (videoRef.current) e.currentTarget.currentTime = videoRef.current.currentTime;
                }}
              />
            ))}
            {/* Canvas overlay pour affichage instantané depuis le cache de décodage */}
            <canvas
              ref={overlayCanvasRef}
              className="absolute max-w-full max-h-full object-contain pointer-events-none"
              style={{ display: 'none' }}
            />
          </>
        ) : (
          <div className="flex flex-col items-center justify-center w-full h-full relative isolate">
            <div className="absolute inset-0 bg-gradient-to-br from-indigo-500/5 via-transparent to-purple-500/5 animate-pulse" />
            <div
              className="flex flex-col items-center justify-center p-8 rounded-2xl rs-glass animate-in fade-in slide-in-from-bottom-4 duration-500 z-10"
              style={{ boxShadow: '0 8px 32px 0 rgba(0, 0, 0, 0.4)' }}
            >
              <div className="w-16 h-16 rounded-2xl flex items-center justify-center mb-5" style={{ background: 'linear-gradient(135deg, rgba(99,102,241,0.1), rgba(139,92,246,0.1))', border: '1px solid rgba(255,255,255,0.05)' }}>
                <Film size={32} className="text-indigo-400 opacity-80" />
              </div>
              <p className="text-base font-bold tracking-widest text-white/90 mb-2">AUCUNE VIDÉO CHARGÉE</p>
              <p className="text-xs text-white/40 font-mono">Sélectionnez <span className="text-indigo-400">Fichier &gt; Importer Vidéo</span> pour commencer</p>
            </div>
          </div>
        )}
        {/* Top Label + Proxy Badge + Cache Badge */}
        <div className="absolute top-3 left-3 flex items-center gap-2 pointer-events-none">
          <div className="px-2 py-1 bg-black/60 backdrop-blur-sm text-[10px] text-white/70 rounded border border-white/10">
            Master Video
          </div>
          {/* Badge cache décodage */}
          {videoSource && cacheInfo.size > 0 && (
            <div className={`flex items-center gap-1 px-2 py-1 backdrop-blur-sm text-[10px] rounded border transition-all ${cacheInfo.active ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/30 animate-pulse' : 'bg-black/40 text-white/40 border-white/10'}`}>
              <Database size={8} />
              {cacheInfo.size} fr. en cache
            </div>
          )}
          {/* Badge proxy : visible seulement si un proxy existe ou est en cours */}
          {videoSource && proxyStatus !== 'idle' && (
            proxyStatus === 'encoding' ? (
              <div className="flex items-center gap-1 px-2 py-1 bg-amber-500/20 backdrop-blur-sm text-[10px] text-amber-300 rounded border border-amber-500/30 pointer-events-auto animate-pulse">
                <svg className="animate-spin" width={8} height={8} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={3}>
                  <path d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" strokeOpacity={0.3} />
                  <path d="M3 12a9 9 0 019-9" />
                </svg>
                Proxy en cours…
              </div>
            ) : proxyStatus === 'ready' ? (
              <button
                className="flex items-center gap-1 px-2 py-1 backdrop-blur-sm text-[10px] rounded border transition pointer-events-auto"
                style={{
                  background: useProxy ? 'rgba(99,102,241,0.25)' : 'rgba(0,0,0,0.5)',
                  borderColor: useProxy ? 'rgba(99,102,241,0.5)' : 'rgba(255,255,255,0.1)',
                  color: useProxy ? '#a5b4fc' : 'rgba(255,255,255,0.5)',
                }}
                title={useProxy ? 'Cliquez pour utiliser la vidéo source originale' : 'Cliquez pour activer le proxy léger (scrubbing rapide)'}
                onClick={() => setUseProxy(!useProxy)}
              >
                {useProxy ? '⚡ Proxy' : '🎬 Source'}
              </button>
            ) : proxyStatus === 'error' ? (
              <div className="px-2 py-1 bg-red-500/10 text-[10px] text-red-400/60 rounded border border-red-500/20 pointer-events-none">
                ⚠ Proxy indisponible
              </div>
            ) : null
          )}
        </div>
      </div>

      {/* 2. Control Bar (Fixed Height, Below Video, Full Width) */}
      <div className="h-14 flex items-center justify-center gap-6 bg-gray-900 border-t border-gray-800 px-4 w-full z-10">

        {/* 1. Reset */}
        <button onClick={handleStop} className="text-gray-400 hover:text-white transition p-2 hover:bg-gray-800 rounded-full" title="Début (Origine)">
          <SkipBack size={18} />
        </button>

        {/* 2. Step Back */}
        <button onClick={(e) => handleStep(e, -1)} className="text-gray-400 hover:text-white transition p-2 hover:bg-gray-800 rounded-full" title={`-1 Image (1/${fps}s)`}>
          <ChevronLeft size={22} />
        </button>

        {/* 3. Play/Pause (Centerpiece) */}
        <button
          onClick={togglePlay}
          className={`w-10 h-10 flex items-center justify-center rounded-full text-white shadow-md transition-transform hover:scale-105 active:scale-95 ${isPlaying ? 'bg-amber-600 hover:bg-amber-500' : 'bg-green-600 hover:bg-green-500'}`}
        >
          {isPlaying ? <Pause size={20} fill="currentColor" /> : <Play size={20} fill="currentColor" className="ml-0.5" />}
        </button>

        {/* 4. Step Forward */}
        <button onClick={(e) => handleStep(e, 1)} className="text-gray-400 hover:text-white transition p-2 hover:bg-gray-800 rounded-full" title={`+1 Image (1/${fps}s)`}>
          <ChevronRight size={22} />
        </button>

        {/* Divider */}
        <div className="w-px h-6 bg-gray-700 mx-1"></div>

        {/* 5. Timecode */}
        <div className="flex flex-col items-start min-w-[85px]">
          <span className="font-mono text-xl font-bold text-blue-400 leading-none">{formatTime(currentTime)}</span>
          <span className="text-[10px] text-gray-500 font-mono mt-0.5 ml-0.5 flex items-center gap-1">
            {fps} FPS
            {isDetectingFps && <Zap size={8} className="text-yellow-400 animate-spin" />}
          </span>
        </div>

        {/* Divider */}
        <div className="w-px h-6 bg-gray-700 mx-1"></div>

        {/* 6. Speed */}
        <button onClick={cyclePlaybackRate} className="flex flex-col items-center justify-center text-gray-400 hover:text-white group w-8">
          <Gauge size={18} className={playbackRate !== 1 ? "text-blue-400" : ""} />
          <span className={`text-[10px] font-bold mt-0.5 ${playbackRate !== 1 ? "text-blue-400" : ""}`}>{playbackRate}x</span>
        </button>

      </div>
    </div>
  );
};