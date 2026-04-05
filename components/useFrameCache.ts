import { useEffect, useRef, useCallback } from 'react';

const MAX_CACHED_FRAMES = 120;
const PREFETCH_SECONDS_FORWARD = 3.0;
const PREFETCH_SECONDS_BACKWARD = 1.0;
const FRAME_TOLERANCE_S = 0.005; // 5ms tolerance pour la correspondance de frames

type FrameCache = Map<number, ImageBitmap>;

interface UseFrameCacheResult {
    /** Cherche une frame dans le cache. Retourne null si non trouvée. */
    getCachedFrame: (time: number) => ImageBitmap | null;
    /** Déclenche le prefetch autour d'un temps donné. */
    prefetchAround: (time: number) => void;
    /** Arrête le prefetch en cours sans vider le cache. */
    abortPrefetch: () => void;
    /** Vide entièrement le cache (à appeler quand la source change). */
    clearCache: () => void;
    /** Nombre de frames actuellement en cache (debug). */
    cacheSize: () => number;
}

/**
 * Hook de cache de frames.
 * @param videoElement - L'élément vidéo source
 * @param fps - FPS de la vidéo
 * @param enabled - Active/désactive le cache
 */
export function useFrameCache(
    videoElement: HTMLVideoElement | null,
    fps: number,
    enabled: boolean = true
): UseFrameCacheResult {
    const cacheRef = useRef<FrameCache>(new Map());
    const isPrefetchingRef = useRef(false);
    const prefetchControllerRef = useRef<AbortController | null>(null);
    const offscreenCanvasRef = useRef<OffscreenCanvas | null>(null);
    const hiddenVideoRef = useRef<HTMLVideoElement | null>(null);

    // Initialise/réinitialise l'OffscreenCanvas et la vidéo cachée
    useEffect(() => {
        if (!videoElement || !enabled) return;

        clearCache();

        const initCanvas = () => {
            const w = videoElement.videoWidth || 1280;
            const h = videoElement.videoHeight || 720;
            offscreenCanvasRef.current = new OffscreenCanvas(w, h);
        };

        if (videoElement.readyState >= 1) {
            initCanvas();
        } else {
            videoElement.addEventListener('loadedmetadata', initCanvas, { once: true });
        }

        // Création d'une vidéo fantôme pour extraire les images sans saccader la vue principale
        const hiddenVideo = document.createElement('video');
        hiddenVideo.src = videoElement.src;
        hiddenVideo.crossOrigin = "anonymous";
        hiddenVideo.muted = true;
        hiddenVideo.playsInline = true;
        hiddenVideoRef.current = hiddenVideo;

        return () => {
            prefetchControllerRef.current?.abort();
            hiddenVideo.removeAttribute('src');
            hiddenVideo.load();
            hiddenVideoRef.current = null;
        };
    }, [videoElement?.src, enabled]); // Dépend de la source, pas de l'élément lui-même pour éviter des boucles

    const clearCache = useCallback(() => {
        cacheRef.current.forEach(bitmap => bitmap.close());
        cacheRef.current.clear();
        prefetchControllerRef.current?.abort();
        isPrefetchingRef.current = false;
    }, []);

    const getCachedFrame = useCallback((time: number): ImageBitmap | null => {
        if (!enabled) return null;
        const cache = cacheRef.current;
        for (const [cachedTime, bitmap] of cache) {
            if (Math.abs(cachedTime - time) < FRAME_TOLERANCE_S) {
                return bitmap;
            }
        }
        return null;
    }, [enabled]);

    const captureFrame = useCallback(async (
        video: HTMLVideoElement,
        time: number,
        signal: AbortSignal
    ): Promise<void> => {
        if (signal.aborted) return;

        const canvas = offscreenCanvasRef.current;
        if (!canvas) return;

        const ctx = canvas.getContext('2d', { alpha: false }) as OffscreenCanvasRenderingContext2D | null;
        if (!ctx) return;

        video.currentTime = time;

        await new Promise<void>((resolve) => {
            const onSeeked = () => {
                video.removeEventListener('seeked', onSeeked);
                resolve();
            };
            video.addEventListener('seeked', onSeeked);

            setTimeout(() => {
                video.removeEventListener('seeked', onSeeked);
                resolve();
            }, 300); // 300ms max par frame
        });

        if (signal.aborted) return;

        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);

        const bitmap = await createImageBitmap(canvas);

        if (!signal.aborted) {
            const cache = cacheRef.current;
            if (cache.size >= MAX_CACHED_FRAMES) {
                const oldestKey = cache.keys().next().value;
                if (oldestKey !== undefined) {
                    cache.get(oldestKey)?.close();
                    cache.delete(oldestKey);
                }
            }
            cache.set(time, bitmap);
        } else {
            bitmap.close();
        }
    }, []);

    const prefetchAround = useCallback((time: number) => {
        const hiddenVideo = hiddenVideoRef.current;
        if (!enabled || !hiddenVideo || isPrefetchingRef.current) return;
        if (!offscreenCanvasRef.current) return;

        prefetchControllerRef.current?.abort();
        const controller = new AbortController();
        prefetchControllerRef.current = controller;
        isPrefetchingRef.current = true;

        const fps_safe = fps > 0 ? fps : 25;
        const frameTime = 1 / fps_safe;

        const framesToFetch: number[] = [];
        const startTime = Math.max(0, time - PREFETCH_SECONDS_BACKWARD);
        // Utiliser la durée de la vidéo principale si dispo
        const endTime = Math.min(
            videoElement?.duration || Infinity,
            time + PREFETCH_SECONDS_FORWARD
        );

        for (let t = startTime; t <= endTime; t += frameTime) {
            const roundedT = Math.round(t / frameTime) * frameTime;
            if (!getCachedFrame(roundedT)) {
                framesToFetch.push(roundedT);
            }
        }

        if (framesToFetch.length === 0) {
            isPrefetchingRef.current = false;
            return;
        }

        (async () => {
            for (const frameT of framesToFetch) {
                if (controller.signal.aborted) break;
                try {
                    await captureFrame(hiddenVideo, frameT, controller.signal);
                } catch {
                    // Frame non disponible
                }
            }
            isPrefetchingRef.current = false;
        })();
    }, [enabled, fps, getCachedFrame, captureFrame, videoElement?.duration]);

    const cacheSize = useCallback(() => cacheRef.current.size, []);

    const abortPrefetch = useCallback(() => {
        prefetchControllerRef.current?.abort();
        isPrefetchingRef.current = false;
    }, []);

    return { getCachedFrame, prefetchAround, abortPrefetch, clearCache, cacheSize };
}
