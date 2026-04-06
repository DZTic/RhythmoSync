import React, { useEffect, useRef, forwardRef, useImperativeHandle, useMemo, useCallback } from 'react';
import { Stage, Layer, Rect, Text, Group, Line, Circle } from 'react-konva';
import Konva from 'konva';
import { useAppStore } from '../store';
import { SYNC_LINE_POSITION_X, DialogueBlock, LANE_HEIGHT, getLaneY, getLaneHeight, getTotalBandHeight, getLaneFromY, MAX_CHARS_PER_SECOND } from '../types';

// ─── GLOBAL KONVA SETTINGS ───────────────────────────────────────────────────
// pixelRatio = 1: renders at 1:1 resolution instead of 2x on HiDPI screens.
// On a 4K screen, 2x ratio means 4× the number of canvas pixels to process.
// This single setting saves ~75% of GPU fill-rate during animation.
Konva.pixelRatio = 1;

// ─── TYPES ───────────────────────────────────────────────────────────────────

interface RhythmoBandProps {
  width: number;
  height: number;
}

interface DialogueItemProps {
  block: DialogueBlock;
  zoomLevel: number;
  isSelected: boolean;
  isPlaying: boolean;
  totalLanes: number;
  snapEnabled: boolean;
  syncTime: number;        // video.currentTime
  syncOffset: number;      // latency offset
  allDialogues: DialogueBlock[]; // pour snap sur les autres blocs
  onSelect: (id: string, multi?: boolean) => void;
  onEditRequest: (id: string) => void;
  onUpdate: (id: string, updates: Partial<DialogueBlock>, skipHistory?: boolean) => void;
  updateDialogues: (updatesArray: { id: string, changes: Partial<DialogueBlock> }[], skipHistory?: boolean) => void;
  snapshotHistory: () => void;
  laneHeights: Record<number, number>;
  globalLaneHeight: number;
}

// ─── DIALOGUE BLOCK COMPONENT ─────────────────────────────────────────────────

const DialogueItemInner: React.FC<DialogueItemProps> = ({
  block, zoomLevel, isSelected, isPlaying, totalLanes,
  snapEnabled, syncTime, syncOffset, allDialogues,
  onSelect, onEditRequest, onUpdate, updateDialogues, snapshotHistory, laneHeights, globalLaneHeight
}) => {
  const textRef = useRef<Konva.Text>(null);
  const groupRef = useRef<Konva.Group>(null);
  const bodyGroupRef = useRef<Konva.Group>(null);
  const dragStartPositions = React.useRef<Record<string, { x: number, y: number, node: Konva.Node }>>({});

  // Snap: time en secondes qui attire (null = pas de snap actif)
  const [snapIndicator, setSnapIndicator] = React.useState<{ x: number; type: 'sync' | 'block' } | null>(null);

  // Distance seuil en secondes pour déclencher le snap (adaptative à ~15 pixels à l'écran)
  const SNAP_THRESHOLD_S = 15 / zoomLevel;

  const blockWidth = block.duration * zoomLevel;
  const blockX = block.startTime * zoomLevel;
  const laneHeightCurrent = getLaneHeight(laneHeights, block.lane, globalLaneHeight);
  const blockY = getLaneY(laneHeights, block.lane, globalLaneHeight);
  const vPad = 3;
  const blockHeight = laneHeightCurrent - vPad * 2;

  const isTooFast = block.duration > 0 && (block.text.length / block.duration > MAX_CHARS_PER_SECOND);
  const strokeColor = isSelected ? '#ffffff' : (isTooFast ? '#ef4444' : block.color);

  // ── TEXT STRETCH ────────────────────────────────────────────────────────────
  // Scale the text horizontally to always fill the block width exactly.
  // Because we cache the group after this effect runs, the cache captures the
  // already-stretched text, so Konva never has to measure it again.
  useEffect(() => {
    if (!textRef.current) return;
    const node = textRef.current;
    node.scaleX(1);
    const natural = node.width();
    if (natural > 0) node.scaleX(blockWidth / natural);
  }, [block.text, blockWidth]);

  // ── NODE CACHE ──────────────────────────────────────────────────────────────
  // cache() rasterises the group into an offscreen bitmap canvas.
  // Konva then blits that single bitmap instead of re-issuing all the
  // individual canvas draw calls (fillRect, fillText, shadow, etc.) on every
  // frame. Large performance win when many blocks are visible simultaneously.
  //
  // We clear the cache first to force a fresh raster whenever the block's
  // visual appearance changes (text, color, width, selection state).
  useEffect(() => {
    const node = bodyGroupRef.current;
    if (!node) return;

    // Give the DOM one tick to apply the text scale before capturing
    const id = setTimeout(() => {
      node.clearCache();
      node.cache({
        // Add a small pixel buffer around the bounding box so shadows / strokes
        // on the border of the block are not clipped by the cache rect.
        offset: 4,
        pixelRatio: 1, // Override global setting locally — cache at 1:1
      });
    }, 0);
    return () => clearTimeout(id);
  }, [block.text, block.color, blockWidth, blockHeight, isSelected, vPad, isTooFast]);

  // ── SNAP HELPER ──────────────────────────────────────────────────────────────
  // Returns the snapped startTime or null if no snap applies.
  const computeSnap = useCallback((rawStartTime: number): { snappedTime: number; snapX: number; type: 'sync' | 'block' } | null => {
    if (!snapEnabled) return null;

    const endTime = rawStartTime + block.duration;
    let best: { delta: number; snappedTime: number; snapX: number; type: 'sync' | 'block' } | null = null;
    const adaptiveThreshold = 15 / zoomLevel;

    const trySnap = (candidateStartTime: number, type: 'sync' | 'block') => {
      const delta = Math.abs(rawStartTime - candidateStartTime);
      if (delta < adaptiveThreshold && (!best || delta < best.delta)) {
        best = { delta, snappedTime: candidateStartTime, snapX: candidateStartTime * zoomLevel, type };
      }
    };

    const trySnapEnd = (candidateStartTime: number, type: 'sync' | 'block') => {
      // Snap the END of this block to the candidate
      const newStart = candidateStartTime - block.duration;
      const delta = Math.abs(endTime - candidateStartTime);
      if (delta < adaptiveThreshold && newStart >= 0 && (!best || delta < best.delta)) {
        best = { delta, snappedTime: newStart, snapX: candidateStartTime * zoomLevel, type };
      }
    };

    // Temps visuel de la ligne rouge (point de synchro réel)
    const targetSyncTime = syncTime + syncOffset;

    // 1. Snap sur la barre rouge (targetSyncTime)
    trySnap(targetSyncTime, 'sync');     // startTime → syncTime
    trySnapEnd(targetSyncTime, 'sync');  // endTime → syncTime

    // 2. Snap sur les bords des autres blocs
    allDialogues.forEach(other => {
      if (other.id === block.id) return;
      trySnap(other.startTime, 'block');
      trySnap(other.startTime + other.duration, 'block');
      trySnapEnd(other.startTime, 'block');
      trySnapEnd(other.startTime + other.duration, 'block');
    });

    return best;
  }, [snapEnabled, syncTime, syncOffset, allDialogues, block.id, block.duration, zoomLevel]);

  // ── INTERACTION CALLBACKS (stable refs via useCallback) ─────────────────────
  const handleDragMove = useCallback((e: Konva.KonvaEventObject<DragEvent>) => {
    if (e.target !== groupRef.current || !snapEnabled) { setSnapIndicator(null); }

    if (isSelected && Object.keys(dragStartPositions.current).length > 1) {
      const myStart = dragStartPositions.current[block.id];
      if (myStart) {
        const dx = e.target.x() - myStart.x;
        const dyLane = getLaneFromY(laneHeights, totalLanes, e.target.y() + laneHeightCurrent / 2, globalLaneHeight) - block.lane;

        Object.entries(dragStartPositions.current).forEach(([id, start]) => {
          if (id === block.id) return;
          start.node.x(Math.max(0, start.x + dx));

          const baseLane = allDialogues.find(d => d.id === id)?.lane || 0;
          const newLane = Math.max(0, Math.min(totalLanes - 1, baseLane + dyLane));
          start.node.y(getLaneY(laneHeights, newLane, globalLaneHeight));
        });
      }
    }

    if (!snapEnabled) return;
    const rawStartTime = Math.max(0, e.target.x() / zoomLevel);
    const snap = computeSnap(rawStartTime);
    setSnapIndicator(snap ? { x: snap.snapX, type: snap.type } : null);
  }, [snapEnabled, zoomLevel, computeSnap, isSelected, block.id, laneHeights, totalLanes, laneHeightCurrent, allDialogues]);

  const handleDragStart = useCallback((e: Konva.KonvaEventObject<DragEvent>) => {
    snapshotHistory();
    const currentSelectedIds = useAppStore.getState().selectedBlockIds;
    if (isSelected && currentSelectedIds.length > 1) {
      const stage = e.target.getStage();
      if (!stage) return;
      const nodes = stage.find('.dialogue-block');
      const starts: Record<string, { x: number, y: number, node: Konva.Node }> = {};
      nodes.forEach(node => {
        if (currentSelectedIds.includes(node.id())) {
          starts[node.id()] = { x: node.x(), y: node.y(), node };
        }
      });
      dragStartPositions.current = starts;
    } else {
      dragStartPositions.current = {};
    }
  }, [snapshotHistory, isSelected]);

  const handleDragEnd = useCallback((e: Konva.KonvaEventObject<DragEvent>) => {
    setSnapIndicator(null);
    if (e.target !== groupRef.current) return;

    // Use the node's current X/Y because dragBoundFunc already handled snapping
    const finalLocalX = e.target.x();
    let newStartTime = Math.max(0, finalLocalX / zoomLevel);
    
    const yCenter = e.target.y() + laneHeightCurrent / 2;
    const rawLane = getLaneFromY(laneHeights, totalLanes, yCenter, globalLaneHeight);
    const newLane = Math.max(0, Math.min(rawLane, totalLanes - 1));

    if (isSelected && Object.keys(dragStartPositions.current).length > 1) {
      const myStart = dragStartPositions.current[block.id];
      const appliedDx = myStart ? (newStartTime * zoomLevel) - myStart.x : 0;
      const dyLane = newLane - block.lane;

      const updatesArray: { id: string, changes: Partial<DialogueBlock> }[] = [];
      Object.entries(dragStartPositions.current).forEach(([id, start]) => {
        const d = allDialogues.find(b => b.id === id);
        if (d) {
          const uLane = Math.max(0, Math.min(totalLanes - 1, d.lane + dyLane));
          const uStart = Math.max(0, (start.x + appliedDx) / zoomLevel);
          updatesArray.push({ id, changes: { startTime: uStart, lane: uLane } });
        }
      });
      updateDialogues(updatesArray, false); // Normal update (snapshot was taken in Start)
    } else {
      onUpdate(block.id, { startTime: newStartTime, lane: newLane }, false);
      if (!e.evt.shiftKey && !e.evt.ctrlKey && !e.evt.metaKey) {
        onSelect(block.id, false);
      }
    }
    dragStartPositions.current = {};
  }, [block.id, zoomLevel, totalLanes, onUpdate, updateDialogues, isSelected, laneHeights, laneHeightCurrent, allDialogues, onSelect]);

  const handleRightResizeDragMove = useCallback((e: Konva.KonvaEventObject<DragEvent>) => {
    e.cancelBubble = true;
    if (!groupRef.current) return;

    let rawNewWidth = e.target.x();
    const myLane = block.lane;

    // Empêcher de superposer le bloc suivant sur la même ligne
    let maxWidth = Infinity;
    for (const other of allDialogues) {
      if (other.id !== block.id && other.lane === myLane) {
        if (other.startTime >= block.startTime) {
          const maxW = (other.startTime - block.startTime) * zoomLevel;
          if (maxW < maxWidth) maxWidth = maxW;
        }
      }
    }

    if (rawNewWidth > maxWidth) {
      rawNewWidth = maxWidth;
      e.target.x(rawNewWidth); // Bloquer la poignée
    }
    if (rawNewWidth < 10) {
      rawNewWidth = 10;
      e.target.x(rawNewWidth);
    }

    let newDuration = rawNewWidth / zoomLevel;

    if (snapEnabled) {
      const rawEndTime = block.startTime + newDuration;
      let bestDelta = 20 / zoomLevel;
      let bestEndTime = rawEndTime;
      let snapTargetX = -1;
      let snapType: 'sync' | 'block' | null = null;

      const trySnap = (candidate: number, type: 'sync' | 'block') => {
        const delta = Math.abs(rawEndTime - candidate);
        if (delta < bestDelta) {
          bestDelta = delta;
          bestEndTime = candidate;
          snapTargetX = candidate * zoomLevel;
          snapType = type;
        }
      };

      trySnap(syncTime, 'sync');
      allDialogues.forEach(other => {
        if (other.id === block.id) return;
        trySnap(other.startTime, 'block');
        trySnap(other.startTime + other.duration, 'block');
      });

      if (snapType !== null) {
        const potentialDuration = Math.max(0, bestEndTime - block.startTime);
        // Ne pas snapper si le snap nous fait dépasser un bloc
        if (potentialDuration * zoomLevel <= maxWidth) {
          newDuration = potentialDuration;
          setSnapIndicator({ x: snapTargetX, type: snapType });
        } else {
          setSnapIndicator(null);
        }
      } else {
        setSnapIndicator(null);
      }
    } else {
      setSnapIndicator(null);
    }

    if (newDuration * zoomLevel >= 10) onUpdate(block.id, { duration: newDuration }, true);
  }, [block.id, block.startTime, zoomLevel, snapEnabled, syncTime, allDialogues, onUpdate]);

  const handleLeftResizeDragMove = useCallback((e: Konva.KonvaEventObject<DragEvent>) => {
    e.cancelBubble = true;
    let localX = e.target.x();

    const myLane = block.lane;
    let minAbsoluteX = 0;
    for (const other of allDialogues) {
      if (other.id !== block.id && other.lane === myLane) {
        if (other.startTime <= block.startTime) {
          const endAbsoluteX = (other.startTime + other.duration) * zoomLevel;
          if (endAbsoluteX > minAbsoluteX) minAbsoluteX = endAbsoluteX;
        }
      }
    }

    const currentAbsoluteX = block.startTime * zoomLevel;
    const minLocalX = minAbsoluteX - currentAbsoluteX;

    if (localX < minLocalX) {
      localX = minLocalX;
      e.target.x(localX); // Bloquer la poignée
    }

    const maxLocalX = (block.duration * zoomLevel) - 10;
    if (localX > maxLocalX) {
      localX = maxLocalX;
      e.target.x(localX);
    }

    let newStartTime = block.startTime + localX / zoomLevel;
    let newDuration = block.duration - localX / zoomLevel;

    if (snapEnabled) {
      let bestDelta = 20 / zoomLevel;
      let snappedStartTime = newStartTime;
      let snapTargetX = -1;
      let snapType: 'sync' | 'block' | null = null;

      const trySnap = (candidate: number, type: 'sync' | 'block') => {
        const delta = Math.abs(newStartTime - candidate);
        if (delta < bestDelta) {
          bestDelta = delta;
          snappedStartTime = candidate;
          snapTargetX = candidate * zoomLevel;
          snapType = type;
        }
      };

      trySnap(syncTime, 'sync');
      allDialogues.forEach(other => {
        if (other.id === block.id) return;
        trySnap(other.startTime, 'block');
        trySnap(other.startTime + other.duration, 'block');
      });

      if (snapType !== null) {
        const potentialDiff = snappedStartTime - newStartTime;
        const potentialNewStartTime = snappedStartTime;

        // Empêcher de snapper en dessous du minAbsoluteX (le bloc derrière)
        if (potentialNewStartTime * zoomLevel >= minAbsoluteX) {
          newStartTime = potentialNewStartTime;
          newDuration -= potentialDiff;
          setSnapIndicator({ x: snapTargetX, type: snapType });
        } else {
          setSnapIndicator(null);
        }
      } else {
        setSnapIndicator(null);
      }
    } else {
      setSnapIndicator(null);
    }

    if (newDuration * zoomLevel >= 10 && newStartTime >= 0) {
      onUpdate(block.id, {
        startTime: newStartTime,
        duration: newDuration
      }, true);
    }
  }, [block.startTime, block.duration, zoomLevel, block.id, snapEnabled, syncTime, allDialogues, onUpdate]);

  const handleClick = useCallback((e: Konva.KonvaEventObject<MouseEvent>) => {
    const isMulti = e.evt.shiftKey || e.evt.ctrlKey || e.evt.metaKey;
    onSelect(block.id, isMulti);
  }, [block.id, onSelect]);

  const dragBoundFunc = useCallback((pos: { x: number; y: number }) => {
    const yCenter = pos.y + laneHeightCurrent / 2;
    const clampedLane = getLaneFromY(laneHeights, totalLanes, yCenter, globalLaneHeight);
    const newY = getLaneY(laneHeights, clampedLane, globalLaneHeight);
    
    let targetX = pos.x;
    const currentSelectedIds = useAppStore.getState().selectedBlockIds;
    const isMultiDragging = isSelected && currentSelectedIds.length > 1;

    // --- MAGNETISM (SNAP) ---
    // On convertit pos.x (absolu) en temps (local) pour computeSnap
    // Note: Layer.x() est négatif car il défile vers la gauche
    const layerX = groupRef.current?.getLayer()?.x() || 0;
    const localX = pos.x - layerX;
    const rawStartTime = localX / zoomLevel;
    
    const snap = computeSnap(rawStartTime);
    if (snap) {
      targetX = snap.snappedTime * zoomLevel + layerX;
    }

    // --- SIBLING COLLISIONS (only if not multi-dragging) ---
    if (!isMultiDragging) {
      const siblings = allDialogues.filter(d => d.id !== block.id && d.lane === clampedLane);
      const blockPixelW = block.duration * zoomLevel;
      const localTargetX = targetX - layerX;

      for (const other of siblings) {
        const otherX = other.startTime * zoomLevel;
        const otherW = other.duration * zoomLevel;

        if (localTargetX < otherX + otherW && localTargetX + blockPixelW > otherX) {
          // Push out of collision
          if (localTargetX + blockPixelW/2 < otherX + otherW/2) {
            targetX = (otherX - blockPixelW) + layerX;
          } else {
            targetX = (otherX + otherW) + layerX;
          }
        }
      }
    }

    return { x: Math.max(layerX, targetX), y: newY };
  }, [totalLanes, allDialogues, block.id, block.duration, zoomLevel, laneHeightCurrent, laneHeights, globalLaneHeight, isSelected, computeSnap]);

  return (
    <Group
      ref={groupRef}
      id={block.id}
      name="dialogue-block"
      x={blockX}
      y={blockY}
      draggable={!isPlaying}
      listening={!isPlaying}
      dragBoundFunc={dragBoundFunc}
      onDragMove={handleDragMove}
      onDragStart={handleDragStart}
      onDragEnd={handleDragEnd}
      onClick={handleClick}
      onTap={handleClick}
      onDblClick={(e) => { e.cancelBubble = true; onEditRequest(block.id); }}
      onDblTap={(e) => { e.cancelBubble = true; onEditRequest(block.id); }}
    >
      {/*
        CACHE TARGET: This inner Group is the node we rasterise with .cache().
        It contains only the static visuals (background rect + text).
        Interactive elements (resize handle) are kept OUTSIDE the cache so they
        don't need to invalidate it when they move.
      */}
      <Group ref={bodyGroupRef}>
        {/* Background rect */}
        <Rect
          y={vPad}
          width={blockWidth}
          height={blockHeight}
          fill={isTooFast ? '#ef4444' : block.color}
          opacity={isTooFast ? 0.6 : 0.35}
          stroke={strokeColor}
          strokeWidth={isSelected || isTooFast ? 2 : 1}
          cornerRadius={4}
          perfectDrawEnabled={false}
          shadowForStrokeEnabled={false}
          dash={isTooFast ? [4, 4] : undefined}
        />
        {/* Warning Icon (Text) if Too Fast */}
        {isTooFast && blockWidth > 20 && (
          <Text
            text="⚠️"
            fontSize={12}
            x={4}
            y={laneHeightCurrent / 2 - 6} // Centered vertically in lane roughly
            fill="#ffffff"
            listening={false}
          />
        )}
        {/* Stretched text */}
        <Text
          ref={textRef}
          text={block.text}
          y={vPad}
          height={blockHeight}
          fontSize={Math.max(12, blockHeight * 0.6)}
          fontFamily="monospace"
          fontStyle="bold"
          fill="#ffffff"
          align="center"
          verticalAlign="middle"
          listening={false}
          shadowColor="black"
          shadowBlur={3}
          shadowOpacity={0.9}
          perfectDrawEnabled={false}
        />
      </Group>

      {/* Resize handle — outside the cache group so dragging it doesn't bust the cache */}
      {isSelected && !isPlaying && (
        <>
          {/* Poignée Gauche */}
          <Line
            points={[0, vPad, 0, blockHeight + vPad]}
            stroke="rgba(255,255,255,0.8)"
            strokeWidth={1}
            dash={[2, 2]}
            listening={false}
          />
          <Circle
            x={0}
            y={laneHeightCurrent / 2}
            radius={8}
            fill="#ffffff"
            stroke="#000000"
            strokeWidth={1}
            draggable
            dragBoundFunc={(pos) => {
              const groupY = groupRef.current?.getAbsolutePosition().y || 0;
              return { x: pos.x, y: groupY + laneHeightCurrent / 2 };
            }}
            onMouseEnter={(e) => { const stage = e.target.getStage(); if (stage) stage.container().style.cursor = 'ew-resize'; }}
            onMouseLeave={(e) => { const stage = e.target.getStage(); if (stage) stage.container().style.cursor = 'default'; }}
            onDragStart={(e) => { e.cancelBubble = true; snapshotHistory(); }}
            onDragMove={handleLeftResizeDragMove}
            onDragEnd={(e) => { e.cancelBubble = true; setSnapIndicator(null); }}
          />

          {/* Poignée Droite */}
          <Line
            points={[blockWidth, vPad, blockWidth, blockHeight + vPad]}
            stroke="rgba(255,255,255,0.8)"
            strokeWidth={1}
            dash={[2, 2]}
            listening={false}
          />
          <Circle
            x={blockWidth}
            y={laneHeightCurrent / 2}
            radius={8}
            fill="#ffffff"
            stroke="#000000"
            strokeWidth={1}
            draggable
            dragBoundFunc={(pos) => {
              const groupY = groupRef.current?.getAbsolutePosition().y || 0;
              return { x: pos.x, y: groupY + laneHeightCurrent / 2 };
            }}
            onMouseEnter={(e) => { const stage = e.target.getStage(); if (stage) stage.container().style.cursor = 'ew-resize'; }}
            onMouseLeave={(e) => { const stage = e.target.getStage(); if (stage) stage.container().style.cursor = 'default'; }}
            onDragStart={(e) => { e.cancelBubble = true; snapshotHistory(); }}
            onDragMove={handleRightResizeDragMove}
            onDragEnd={(e) => { e.cancelBubble = true; setSnapIndicator(null); }}
          />
          <Text
            text={`${block.startTime.toFixed(2)}s`}
            y={-5}
            fontSize={10}
            fill="#aaa"
            listening={false}
            perfectDrawEnabled={false}
          />
          <Text
            text={`(${block.duration.toFixed(2)}s)`}
            x={blockWidth + 10}
            y={laneHeightCurrent / 2 - 5}
            fontSize={10}
            fill="#aaa"
            listening={false}
            perfectDrawEnabled={false}
          />
        </>
      )}

      {/* Indicateur de snap — ligne jaune qui s'affiche pendant le drag */}
      {snapIndicator && (
        <Line
          x={snapIndicator.x - (groupRef.current?.x() ?? blockX)}
          y={-(groupRef.current?.y() ?? blockY)}
          points={[0, 0, 0, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight)]}
          stroke={snapIndicator.type === 'sync' ? '#ef4444' : '#facc15'}
          strokeWidth={2}
          dash={[5, 5]}
          listening={false}
          opacity={0.8}
        />
      )}
    </Group>
  );
};

// ─── MEMO COMPARISON ─────────────────────────────────────────────────────────
// React.memo prevents the component function from being called when parent
// re-renders but none of the block's OWN props changed.
// Combined with node.cache() this creates a two-level performance shield:
//   Level 1 (React): function call skipped entirely when memo returns true.
//   Level 2 (Konva):  even if the function IS called, Konva blits the cached
//                     bitmap instead of re-issuing all draw commands.
const DialogueItem = React.memo(DialogueItemInner, (prev, next) => {
  return (
    prev.block === next.block &&
    prev.zoomLevel === next.zoomLevel &&
    prev.isSelected === next.isSelected &&
    prev.isPlaying === next.isPlaying &&
    prev.totalLanes === next.totalLanes &&
    prev.snapEnabled === next.snapEnabled &&
    prev.syncTime === next.syncTime &&
    prev.allDialogues === next.allDialogues &&
    prev.globalLaneHeight === next.globalLaneHeight &&
    prev.laneHeights === next.laneHeights
  );
});

// ─── RHYTHMOBAND ─────────────────────────────────────────────────────────────

export const RhythmoBand = forwardRef<Konva.Stage, RhythmoBandProps>(({ width, height }, ref) => {

  // PERF: Granular selectors (from previous optimisation step)
  const zoomLevel = useAppStore((s) => s.zoomLevel);
  const syncOffset = useAppStore((s) => s.syncOffset);
  const totalLanes = useAppStore((s) => s.totalLanes);
  const isPlaying = useAppStore((s) => s.isPlaying);
  const dialogues = useAppStore((s) => s.dialogues);
  const selectedBlockIds = useAppStore((s) => s.selectedBlockIds);
  const videoElement = useAppStore((s) => s.videoElement);
  const selectBlock = useAppStore((s) => s.selectBlock);
  const { updateDialogue, updateDialogues, snapshotHistory, laneHeights, globalLaneHeight } = useAppStore();
  const snapEnabled = useAppStore((s) => s.snapEnabled);
  // currentTime via ref to avoid re-renders at 60fps
  const syncTimeRef = React.useRef(0);
  React.useEffect(() => {
    if (!videoElement) return;
    const onTimeUpdate = () => { syncTimeRef.current = videoElement.currentTime; };
    videoElement.addEventListener('timeupdate', onTimeUpdate);
    return () => videoElement.removeEventListener('timeupdate', onTimeUpdate);
  }, [videoElement]);
  // Snapshot stable pour les DialogueItem (stable ref each render but value always fresh)
  const syncTimeSnap = React.useRef(0);
  React.useEffect(() => {
    syncTimeSnap.current = 0;
  }, [videoElement]);

  const stageRef = useRef<Konva.Stage>(null);
  const movingLayerRef = useRef<Konva.Layer>(null); // Layer 1: scrolling content
  const staticLayerRef = useRef<Konva.Layer>(null); // Layer 0: background grid
  const animationRef = useRef<number>(0);

  const [editingBlockId, setEditingBlockId] = React.useState<string | null>(null);
  const editingInputRef = React.useRef<HTMLTextAreaElement>(null);
  const currentEditingBlockIdRef = React.useRef<string | null>(null);
  currentEditingBlockIdRef.current = editingBlockId;

  const isDraggingTimeline = useRef(false);
  const lastPointerX = useRef(0);
  const mouseDownPos = useRef({ x: 0, y: 0 });

  // Expose stageRef to parent (used for video export screenshot)
  useImperativeHandle(ref, () => stageRef.current as Konva.Stage);

  // ── STATIC LAYER SETUP ────────────────────────────────────────────────────
  // After the static layer mounts, tell Konva it must NEVER clear itself before
  // drawing. This turns it into the equivalent of Konva's old "FastLayer":
  // the content is drawn exactly once (or when explicitly invalidated), and
  // subsequent batchDraw() calls on OTHER layers don't touch it at all.
  useEffect(() => {
    const layer = staticLayerRef.current;
    if (!layer) return;
    // clearBeforeDraw=false means the canvas backing this layer is never wiped.
    // The grid pixels stay in GPU memory, eliminating the fill + redraw cost
    // that would otherwise happen on every single animation frame.
    layer.clearBeforeDraw(false);
    layer.batchDraw(); // Initial draw
  }, []);

  // Explicitly redraw the static layer only when its content actually changes.
  useEffect(() => {
    staticLayerRef.current?.clearCache();
    staticLayerRef.current?.batchDraw();
  }, [totalLanes, width, height, zoomLevel, globalLaneHeight, laneHeights]);

  // ── rAF SYNC LOOP ────────────────────────────────────────────────────────
  // Runs at 60/120fps and touches movingLayerRef.
  // We extrapolate `video.currentTime` using `performance.now()` because the
  // HTML5 video `currentTime` property only updates a few times per second.
  // Without extrapolation, the rhythm band scrolls in jerky steps.
  useEffect(() => {
    let lastX = -Infinity;
    let lastVideoTime = -1;
    let lastPerfTime = performance.now();

    const loop = () => {
      if (movingLayerRef.current && videoElement) {
        const perfNow = performance.now();
        const currentVideoTime = videoElement.currentTime;

        // If the video browser clock advanced, resync our anchor
        if (currentVideoTime !== lastVideoTime) {
          lastVideoTime = currentVideoTime;
          lastPerfTime = perfNow;
        }

        let extrapolatedTime = currentVideoTime;
        if (!videoElement.paused && videoElement.playbackRate > 0) {
          extrapolatedTime += ((perfNow - lastPerfTime) / 1000) * videoElement.playbackRate;
        }

        const exactX = SYNC_LINE_POSITION_X - (extrapolatedTime + syncOffset) * zoomLevel;
        // Arrondir à l'entier le plus proche pour éviter le flou de rendu sous-pixel (smearing)
        const newX = Math.round(exactX);

        if (Math.abs(newX - lastX) >= 1) {
          movingLayerRef.current.x(newX);
          movingLayerRef.current.batchDraw();
          lastX = newX;
        }

        if (editingInputRef.current && currentEditingBlockIdRef.current) {
          const state = useAppStore.getState();
          const block = state.dialogues.find(d => d.id === currentEditingBlockIdRef.current);
          if (block) {
            const blockWidth = block.duration * zoomLevel;
            const actualWidth = Math.max(50, blockWidth);
            const offsetX = (actualWidth - blockWidth) / 2;

            let absoluteX = newX + (block.startTime * zoomLevel) - offsetX;
            let absoluteY = getLaneY(state.laneHeights, block.lane);

            if (stageRef.current && movingLayerRef.current) {
              const node = movingLayerRef.current.findOne('#' + block.id);
              if (node) {
                const pos = node.absolutePosition();
                absoluteX = pos.x - offsetX;
                absoluteY = pos.y;
              }
            }

            editingInputRef.current.style.transform = `translate(${absoluteX}px, ${absoluteY + 3}px)`;
            editingInputRef.current.style.width = `${actualWidth}px`;
            editingInputRef.current.style.height = `${getLaneHeight(state.laneHeights, block.lane) - 6}px`;
          }
        }
      }
      animationRef.current = requestAnimationFrame(loop);
    };
    animationRef.current = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(animationRef.current);
  }, [videoElement, zoomLevel, syncOffset]);

  // ── STATIC GRID CONTENT (memoised) ───────────────────────────────────────
  // This is the content for the static background layer.
  // It is recomputed only when totalLanes, width, height, or zoomLevel change.
  // Each element has listening={false} so Konva never builds a hit-graph for them.
  const staticLayerContent = useMemo(() => {
    const nodes: React.ReactNode[] = [];

    // ① Dark background fill
    nodes.push(
      <Rect
        key="bg"
        x={0} y={0}
        width={width} height={height}
        fill="#111827"
        listening={false}
        perfectDrawEnabled={false}
      />
    );

    // ② Horizontal lane dividers
    for (let i = 1; i < totalLanes; i++) {
      const y = getLaneY(laneHeights, i, globalLaneHeight);
      nodes.push(
        <Line
          key={`lane-${i}`}
          points={[0, y, width, y]}
          stroke="#2d3748"
          strokeWidth={1}
          dash={[6, 4]}
          listening={false}
          perfectDrawEnabled={false}
        />
      );
    }

    // ③ Top & bottom borders
    nodes.push(
      <Line key="border-top" points={[0, 0, width, 0]} stroke="#4a5568" strokeWidth={1} listening={false} perfectDrawEnabled={false} />,
      <Line key="border-bottom" points={[0, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight), width, getTotalBandHeight(laneHeights, totalLanes, globalLaneHeight)]} stroke="#4a5568" strokeWidth={1} listening={false} perfectDrawEnabled={false} />
    );

    // ④ Time ruler: draw a tick mark + label every N pixels (adapted to zoom).
    //    We use a fixed ruler over the full visible width.
    //    The ticks are drawn at absolute canvas positions so they don't scroll.
    //    Spacing: aim for a tick roughly every 80-120px regardless of zoom.
    const targetTickSpacingPx = 100; // target pixels between ticks
    // Round the seconds-per-tick to a "nice" value (0.5, 1, 2, 5, 10, 30 …)
    const rawSecsPerTick = targetTickSpacingPx / zoomLevel;
    const niceIntervals = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
    const secsPerTick = niceIntervals.find(v => v >= rawSecsPerTick) ?? 300;
    const pxPerTick = secsPerTick * zoomLevel;
    const numTicks = Math.ceil(width / pxPerTick) + 2;

    for (let t = 0; t < numTicks; t++) {
      const x = t * pxPerTick;
      const sec = t * secsPerTick;
      const mm = Math.floor(sec / 60);
      const ss = (sec % 60).toFixed(secsPerTick < 1 ? 1 : 0);
      const label = `${mm}:${String(ss).padStart(secsPerTick < 1 ? 4 : 2, '0')}`;

      nodes.push(
        <Line
          key={`tick-${t}`}
          points={[x, 0, x, 8]}
          stroke="#4a5568"
          strokeWidth={1}
          listening={false}
          perfectDrawEnabled={false}
        />,
        <Text
          key={`tick-label-${t}`}
          text={label}
          x={x + 3}
          y={1}
          fontSize={9}
          fontFamily="monospace"
          fill="#4a5568"
          listening={false}
          perfectDrawEnabled={false}
        />
      );
    }

    return nodes;
  }, [totalLanes, width, height, zoomLevel, laneHeights, globalLaneHeight]);

  // ── INTERACTION HANDLERS ─────────────────────────────────────────────────

  const handleStageMouseDown = (e: Konva.KonvaEventObject<MouseEvent>) => {
    if (e.target.findAncestor('.dialogue-block')) return;
    
    // Désélection si clic sur le fond vide
    if (e.target === e.target.getStage()) {
      selectBlock(null);
    }

    isDraggingTimeline.current = true;
    const pos = stageRef.current?.getPointerPosition();
    if (pos) {
      lastPointerX.current = pos.x;
      mouseDownPos.current = { x: pos.x, y: pos.y };
    }
    stageRef.current?.container().style.setProperty('cursor', 'grabbing');
  };

  const handleStageMouseMove = (e: Konva.KonvaEventObject<MouseEvent>) => {
    if (!isDraggingTimeline.current || !videoElement) return;
    e.evt.preventDefault();
    const pos = stageRef.current?.getPointerPosition();
    if (!pos) return;
    const deltaX = pos.x - lastPointerX.current;
    lastPointerX.current = pos.x;
    const newTime = Math.max(0, Math.min(
      videoElement.currentTime - deltaX / zoomLevel,
      videoElement.duration || 1000
    ));
    videoElement.currentTime = newTime;
  };

  const handleStageMouseUp = (e: Konva.KonvaEventObject<MouseEvent>) => {
    if (isDraggingTimeline.current && videoElement) {
      const pos = stageRef.current?.getPointerPosition();
      if (pos && Math.abs(pos.x - mouseDownPos.current.x) < 5) {
        const timeDiff = (pos.x - SYNC_LINE_POSITION_X) / zoomLevel;
        videoElement.currentTime = Math.max(0, videoElement.currentTime + timeDiff);
      }
    }
    isDraggingTimeline.current = false;
    stageRef.current?.container().style.setProperty('cursor', 'default');
  };

  // ── RENDER ──────────────────────────────────────────────────────────────

  return (
    <div 
      className="relative overflow-hidden bg-gray-900 border-b border-gray-700 cursor-default"
      onClick={() => {
        if (document.activeElement instanceof HTMLElement) {
          document.activeElement.blur();
        }
      }}
    >
      <Stage
        width={width}
        height={height}
        ref={stageRef}
        onMouseDown={handleStageMouseDown}
        onMouseMove={handleStageMouseMove}
        onMouseUp={handleStageMouseUp}
        onMouseLeave={handleStageMouseUp}
        listening={true}
      >
        {/*
          ┌─────────────────────────────────────────────────────────────────┐
          │  LAYER 0 — Static background (FastLayer equivalent)             │
          │                                                                 │
          │  • clearBeforeDraw(false) applied in useEffect above.          │
          │  • listening={false} → no hit graph at all for these nodes.    │
          │  • Content never scrolls — ruler labels are in "screen space". │
          │  • Redrawn ONLY when totalLanes / width / zoomLevel change.    │
          └─────────────────────────────────────────────────────────────────┘
        */}
        <Layer ref={staticLayerRef} listening={false}>
          {staticLayerContent}
        </Layer>

        {/*
          ┌─────────────────────────────────────────────────────────────────┐
          │  LAYER 1 — Moving content (dialogue blocks)                     │
          │                                                                 │
          │  • Translated on X every rAF frame via layerRef.x(newX).      │
          │  • perfectDrawEnabled={false} → raw canvas API, no AA cost.    │
          │  • listening={!isPlaying} → hit graph suspended during playback│
          │  • Each DialogueItem caches itself with node.cache() internally │
          └─────────────────────────────────────────────────────────────────┘
        */}
        <Layer
          ref={movingLayerRef}
          listening={!isPlaying}
          perfectDrawEnabled={false}
        >
          {dialogues.map((block) => (
            <DialogueItem
              key={block.id}
              block={block}
              zoomLevel={zoomLevel}
              isSelected={selectedBlockIds.includes(block.id)}
              isPlaying={isPlaying}
              totalLanes={totalLanes}
              snapEnabled={snapEnabled}
              syncTime={syncTimeRef.current}
              syncOffset={syncOffset}
              allDialogues={dialogues}
              onSelect={selectBlock}
              onEditRequest={setEditingBlockId}
              onUpdate={updateDialogue}
              updateDialogues={updateDialogues}
              snapshotHistory={snapshotHistory}
              laneHeights={laneHeights}
              globalLaneHeight={globalLaneHeight}
            />
          ))}
        </Layer>

        {/*
          ┌─────────────────────────────────────────────────────────────────┐
          │  LAYER 2 — Static UI overlay (sync line)                        │
          │                                                                 │
          │  • Always on top, never scrolls, never cleared.                │
          │  • clearBeforeDraw(false) could be applied here too but the    │
          │    layer is so cheap (2 primitives) that it's irrelevant.       │
          └─────────────────────────────────────────────────────────────────┘
        */}
        <Layer listening={false}>
          {/* Main sync line */}
          <Line
            points={[SYNC_LINE_POSITION_X, 0, SYNC_LINE_POSITION_X, height]}
            stroke="#ef4444"
            strokeWidth={2}
            perfectDrawEnabled={false}
          />
          {/* Arrow head at top */}
          <Line
            points={[
              SYNC_LINE_POSITION_X - 6, 0,
              SYNC_LINE_POSITION_X + 6, 0,
              SYNC_LINE_POSITION_X, 10
            ]}
            closed
            fill="#ef4444"
            perfectDrawEnabled={false}
          />
        </Layer>
      </Stage>

      {editingBlockId && (
        <textarea
          ref={editingInputRef}
          className="absolute z-50 bg-[#1e293b] text-white font-mono text-[28px] overflow-hidden leading-none font-bold border border-blue-500 rounded p-0 outline-none resize-none text-center shadow-[0_0_15px_rgba(59,130,246,0.6)] selection:bg-blue-500 selection:text-white"
          style={{
            top: 0, left: 0,
            transformOrigin: 'top left',
            willChange: 'transform'
          }}
          autoFocus
          onFocus={(e) => {
            e.target.select();
            if (useAppStore.getState().isPlaying && videoElement) {
              videoElement.pause();
              useAppStore.getState().setIsPlaying(false);
            }
          }}
          defaultValue={dialogues.find(d => d.id === editingBlockId)?.text || ""}
          onMouseDown={(e) => e.stopPropagation()}
          onMouseMove={(e) => e.stopPropagation()}
          onMouseUp={(e) => e.stopPropagation()}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              updateDialogue(editingBlockId, { text: e.currentTarget.value });
              setEditingBlockId(null);
            } else if (e.key === 'Escape') {
              setEditingBlockId(null);
            }
            e.stopPropagation();
          }}
          onBlur={(e) => {
            updateDialogue(editingBlockId, { text: e.target.value });
            setEditingBlockId(null);
            // On redonne le focus à la fenêtre principale pour que les raccourcis clavier (Space, etc.) fonctionnent
            window.focus();
          }}
        />
      )}

      {/* HTML debug overlay — reads from store selector, zero canvas cost */}
      <div className="absolute top-0 right-0 p-2 text-[9px] text-gray-600 pointer-events-none font-mono">
        PERF: cache+FastLayer | {zoomLevel} PPS
      </div>
    </div>
  );
});

RhythmoBand.displayName = 'RhythmoBand';