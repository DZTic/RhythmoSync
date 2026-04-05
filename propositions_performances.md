# ⚡ Propositions d'Optimisations (Performance & Fluidité)

Pour un outil de synchronisation comme RhythmoSync, la réactivité lors de la lecture vidéo et la manipulation de la ligne de temps est cruciale (objectif constant de 60 FPS). 

Voici les propositions d'optimisations techniques avancées pour améliorer la fluidité, en tenant compte de ce qui a déjà été accompli (comme la génération externe de la waveform en Rust).

### 1. Virtualisation du Canvas (Culling spatial et temporel)
* **Contexte :** Actuellement, si une vidéo dure 2 heures et contient 2000 sous-titres, les 2000 nœuds React-Konva risquent d'être présents dans l'arbre de rendu.
* **Proposition :** Implémenter une "Virtualisation" (windowing). Ne fournir à Konva que les `DialogueItem` qui sont mathématiquement visibles sur l'écran à l'instant T (par exemple entre `currentTime - 5s` et `currentTime + 15s`). Tous les dialogues hors de ce champ visuel sont démontés. Le gain de FPS sur les gros projets sera massif.

### 2. ~~Flux de "Proxy Vidéo" pour un Scrubbing fluide~~ ✅ DÉJÀ FAIT
* **Contexte :** Les vidéos modernes (4K, H.265, HEVC avec des "Long GOP") sont très difficiles à décoder en temps réel lors de déplacements rapides dans le temps. Le lecteur HTML5 natif subit des micro-congélations.
* ~~**Proposition :** Lors de l'import d'une vidéo, générer une copie "Proxy" : 720p encodée en **Intra-Frame** (H.264 All-I). Utiliser cette vidéo proxy dans l'éditeur pour un seeking instantané.~~
* **Implémentation :**
  * **Rust (`export.rs`)** : Nouvelle commande Tauri `generate_proxy_video`. Lance FFmpeg avec `-g 1 -keyint_min 1` (All-Intra) + `-preset ultrafast -crf 28 -vf scale=-2:720`. Parse la progression depuis stderr et l'émet via l'event `proxy-progress`. Détecte si le proxy existe déjà pour ne pas le régénérer.
  * **Store (`store.ts` + `types.ts`)** : 3 nouveaux champs : `proxyVideoSource` (Blob URL du proxy), `proxyStatus` (`idle | encoding | ready | error`), `useProxy` (bascule active/inactive). `setVideoSource` réinitialise ces champs à `idle` à chaque nouvelle vidéo.
  * **Flow (`App.tsx`)** : Après un import vidéo, si FFmpeg est disponible, `generate_proxy_video` est appelé en arrière-plan. Une fois terminé, `proxyVideoSource` et `useProxy=true` sont définis automatiquement.
  * **Lecteur (`VideoPlayer.tsx`)** : L'attribut `src` du `<video>` utilise `proxyVideoSource` si `useProxy=true`, sinon la source originale. Un badge `⚡ Proxy` (cliquable) en haut à gauche du lecteur indique l'état et permet de basculer entre les deux sources. État `encoding` → badge ambre pulsant ; `ready` → bouton indigo cliquable ; `error` → badge rouge discret.

### 3. Découplage strict de la boucle 60 FPS (Zustand -> Vanilla Ref)
* **Contexte :** Bien que Zustand soit léger, mettre à jour `currentTime` 60 fois par seconde lors de la lecture génère un trafic d'événements React qui peut peser sur de vieilles machines.
* **Proposition :** Créer un "Event Bus" vanilla pur ou utiliser exclusivement des `useRef` pour la coordination temporelle entre le `<video>` et le `<Stage>` de Konva (pour déplacer le fond ou les calques). L'idée est de court-circuiter complètement le cycle de vie React lors de la simple lecture, ne sollicitant Zustand que lors d'un réel changement de données (fin de déplacement d'un bloc, modif de texte).

### 4. ~~Pagination / Tiling de la Forme d'Onde (Waveform)~~ ✅ DÉJÀ FAIT
* **Contexte :** Les données audio peuvent produire un canvas de plusieurs centaines de milliers de pixels (ex: 2h × 100px/s = 720 000px). Ce canvas unique occupe en permanence de la RAM et VRAM même si l'utilisateur n'en voit que 1 200px.
* ~~**Proposition :** Découper l'affichage de la Track Audio en "tuiles" (Tiling) de 30 secondes. Seules les tuiles actuellement à l'écran (et les 2 adjacentes) sont dessinées par Konva. Les autres sont détruites pour libérer la VRAM.~~
* **Implémentation :** Refactoring de `Waveform.tsx`. Le composant `WaveformCanvas` (canvas unique) est remplacé par un `WaveformTile` par tranche de 30s (`TILE_DURATION = 30`). La boucle RAF 60 FPS calcule quelles tuiles sont dans la fenêtre visible (`[-scrollX, -scrollX + viewportWidth]`) et seulement ces tuiles + `TILE_BUFFER = 2` de chaque côté sont montées dans le DOM React. Les tuiles hors-champ sont **démontées** (canvas libéré). Chaque tuile est mémoïsée via `React.memo` avec comparaison de référence sur les peaks.

### 5. ~~Rasterization Dynamique (Node Cache Konva)~~ ✅ DÉJÀ FAIT
* **Contexte :** Le texte enrichi, ses bordures, ombres portées et calculs de polices coûtent cher au moteur de rendu 2D HTML Canvas sous le capot.
* ~~**Proposition :** Utiliser de façon agressive l'API `node.cache()` de Konva. Lorsqu'un bloc de dialogue n'est pas ciblé/édité, le forcer sous forme d'image statique pré-calculée (rasterization). L'invalider de ce cache (le refaire redevenir vecteur) uniquement au moment du "onDragStart" ou pour de l'édition texte.~~
* **Implémentation :** `bodyGroupRef.current.cache({ offset: 4, pixelRatio: 1 })` déclenché via `useEffect` sur `[block.text, block.color, blockWidth, blockHeight, isSelected]` dans `RhythmoBand.tsx`. Les poignées de resize et l'indicateur de snap sont volontairement **exclus** du groupe caché pour ne pas invalider le bitmap lors d'un drag. Couplé à `React.memo` pour un double bouclier React + Konva.

### 6. Threads Séparés (Web Workers) pour les lourds calculs d'interface
* **Contexte :** Opérations comme la détection globale de collisions (le Snap-to-Grid sur 5000 segments) ou le tri de l'historique massif.
* **Proposition :** Déplacer toute logique d'algorithme lourde dans un processeur Web Worker dédié (en dehors du Main Thread de l'UI). Laisser le Thread UI s'occuper exclusivement de dessiner des pixels.
