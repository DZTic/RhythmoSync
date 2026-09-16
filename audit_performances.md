# 📊 Audit Global de Performance & Rapport d'Optimisation — RhythmoSync Studio

Ce document constitue le rapport officiel d'audit de performance de l'application **RhythmoSync Studio (.NET 8 / WPF)**, réalisé suite à la migration native Windows.

---

## 📑 Sommaire

1. [Synthèse Exécutive](#1-synthèse-exécutive)
2. [Analyse Spécifique du Bug : Saccade de la Timeline](#2-analyse-spécifique-du-bug--saccade-de-la-timeline)
   - 2.1 [Cas n°1 : Saccade de la bande rythmo dans la vidéo exportée](#21-cas-n1--saccade-de-la-bande-rythmo-dans-la-vidéo-exportée)
   - 2.2 [Cas n°2 : Saccade / Gel au démarrage de la lecture dans l'application](#22-cas-n2--saccade--gel-au-démarrage-de-la-lecture-dans-lapplication)
3. [Audit : Chargement & Importation](#3-audit--chargement--importation)
4. [Audit : Déplacement & Scrubbing sur la Timeline](#4-audit--déplacement--scrubbing-sur-la-timeline)
5. [Audit : Pipeline d'Exportation Vidéo](#5-audit--pipeline-dexportation-vidéo)
6. [Matrice des Problèmes & Index des Issues GitHub](#6-matrice-des-problèmes--index-des-issues-github)

---

## 1. Synthèse Exécutive

L'architecture actuelle de RhythmoSync Studio en C# / WPF tire un excellent parti du mode « retained » DirectX (`DrawingVisual`, `TranslateTransform` composée sur GPU, découplage transport). 
Cependant, l'audit approfondi a mis en lumière **trois goulots d'étranglement critiques** et **plusieurs opportunités majeures d'optimisation** :

1. **Bug d'affichage de la bande à l'export (P0)** : Une discordance fondamentale entre le framerate réel de la vidéo source et la fréquence d'export (bloquée à 25 FPS), combinée à une troncature entière subpixel (`(int)`) et un défaut d'alignement PTS FFmpeg (`-ss` avec filtre `fps`), crée un effet de micro-saccade/judder visuel très marqué sur le texte contrasté qui défile.
2. **Scrubbing et Navigation (P1)** : Les tuiles de la waveform sont détruites dès qu'elles sortent du champ de vision (zéro cache LRU), forçant une reconstruction vectorielle (`StreamGeometry`) coûteuse lors des va-et-vient ; la virtualisation des dialogues et le moteur de magnétisme exécutent des parcours exhaustifs `O(N)` à chaque frame ou à chaque événement de souris.
3. **Chargement et Exportation (P1/P2)** : Absence de cache disque pour la waveform audio (recalculée à chaque ouverture), sur-échantillonnage audio inutile (48 kHz mono), et exécution strictement séquentielle (sans pipelining producteur-consommateur) du flux d'export décodeur → composition → encodeur.

---

## 2. Analyse Spécifique du Bug : Saccade de la Timeline

> **Question utilisateur :** *« Pourquoi quand on exporte une vidéo avec la timeline, quand on lance la vidéo, la timeline saccade un peu alors que la vidéo non. »*

Deux contextes complémentaires ont été identifiés et résolus :

### 2.1 Cas n°1 : Saccade de la bande rythmo dans la vidéo exportée

Lorsqu'on visionne le fichier MP4 généré, la vidéo en haut est fluide, mais le bandeau défilant en bas donne l'impression de « saccader » ou d'accrocher. **Quatre facteurs techniques cumulatifs** provoquent ce phénomène :

#### A. Incohérence de Framerate (Source vs Export) et Cadence Pulldown
- **Code concerné :** [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L1318), [`ProjectState.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/ProjectState.cs#L134), [`ExportDialog.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/ExportDialog.xaml.cs#L206).
- **Constat :** Lors de l'import, [`VideoProber`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoProber.cs#L78) extrait le FPS natif de la vidéo (ex. 23.976, 24, 29.97, 30 ou 60 FPS). **Cependant, `MainWindow` n'affecte jamais `_state.Fps = _videoInfo.Fps` !** Le projet reste figé à 25 FPS par défaut.
- **Conséquence :** À l'export, FFmpeg applique le filtre `-vf ...,fps=25`. Si la vidéo source fait 30 FPS ou 24 FPS, FFmpeg supprime ou duplique des images. Mais surtout, sur les écrans d'ordinateur (qui rafraîchissent presque tous à 60 Hz), une vidéo 25 FPS subit un **judder de pulldown 2:3**. L'œil humain tolère ce judder sur une image filmée grâce au flou de mouvement de la caméra (motion blur), mais le rejette immédiatement sur du **texte graphique net à fort contraste** qui défile horizontalement à vitesse constante.
- **Solution recommandée :**
  1. Synchroniser automatiquement `_state.Fps` sur le FPS détecté lors du chargement de la vidéo.
  2. Ajouter une option d'export à 50 ou 60 FPS (avec interpolation ou simple duplication de la vidéo mais rendu de la bande à 60 FPS purs).

#### B. Troncature entière `(int)` de `stripX` & Micro-pas irréguliers
- **Code concerné :** [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L430) :
  ```csharp
  var time = s.StartTime + frameCount / s.Fps;
  var stripX = (int)((time + s.SyncOffsetEffective) * s.Pps);
  ```
- **Constat :** L'avancement spatial par frame vaut $\Delta x = \frac{\text{Pps}}{\text{Fps}}$. Par exemple, avec un zoom de 100 px/s et une échelle de piste de 1.35x, $\text{Pps} = 135 \text{ px/s}$.
  $\Delta x = 135 / 25 = 5.4 \text{ px/frame}$.
- **Conséquence :** Le cast brut `(int)` tronque les décimales. La suite des déplacements par frame devient : `5 px`, `5 px`, `6 px`, `5 px`, `6 px`... Cette modulation de pas à haute fréquence génère un phénomène de crénelage temporel (jitter spatial) perçu comme une micro-saccade constante.
- **Biais supplémentaire autour de zéro :** En C#, `(int)(-0.99)` vaut `0` et `(int)(+0.99)` vaut également `0`. Cela crée une zone morte (plateau) de presque 2 pixels au passage de $t=0$, où la bande reste figée avant de sauter brusquement !
- **Solution recommandée :**
  1. Remplacer `(int)` par `(int)Math.Floor(...)` ou `Math.Round(...)`.
  2. Ajuster l'échelle d'exportation pour cibler un ratio vitesse/framerate régulier ou implémenter un filtrage subpixel.

#### C. Décalage de Timestamp PTS initial via FFmpeg (`-ss` + filtre `fps`)
- **Code concerné :** [`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L234-L240).
- **Constat :** L'argument `-ss` placé avant `-i` effectue un seek rapide sur keyframe. Les conteneurs MP4 possèdent régulièrement un délai initial (delay frames, edit lists ou audio padding) de quelques dizaines de millisecondes (ex: premier timestamp à 0.080 s).
- **Conséquence :** Le filtre `-vf ...,fps=25` de FFmpeg détecte un écart d'horloge et injecte des images dupliquées ou insère un délai au tout début de la vidéo pour recaler le flux vidéo sur son horloge interne. Pendant ce temps, le compositeur C# avance linéairement selon `frameCount / s.Fps`. Dès les premières secondes (« quand on lance la vidéo »), un décalage ou une irrégularité temporelle survient.
- **Solution recommandée :** Insérer `setpts=PTS-STARTPTS` immédiatement avant le filtre `fps` dans la chaîne vidéo du décodeur pour forcer le premier paquet à débuter exactement à $t=0$.

---

### 2.2 Cas n°2 : Saccade / Gel au démarrage de la lecture dans l'application

Si le problème se manifeste lorsque l'utilisateur clique sur le bouton **« Lecture »** dans RhythmoSync :

- **Code concerné :** [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L328-L336) et [`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L472-L476).
- **Constat :**
  Dans `TogglePlay()`, lors de la reprise de lecture :
  ```csharp
  _lastMediaPos = -1;
  _playKickPos = Media.Position.TotalSeconds;
  _playKickTicks = Stopwatch.GetTimestamp();
  ```
  Et dans `GetClockTime()` :
  ```csharp
  if (_isPlaying && _playKickTicks == 0)
  {
      var extrapolated = (now - _anchorTicks) / (double)Stopwatch.Frequency * _playbackRate;
      time += Math.Min(extrapolated, MaxExtrapolationSeconds);
  }
  ```
- **Mécanisme de la saccade :**
  Tant que `_playKickTicks != 0`, **l'extrapolation temporelle est totalement désarmée**. Or, le composant WPF `MediaElement` (basé sur DirectShow/Media Foundation) met typiquement **150 à 300 ms** avant que sa propriété `Position` n'évolue de manière mesurable.
  Pendant ce tiers de seconde, la vidéo commence à afficher ses premières images, mais la bande rythmo reste **complètement gelée sur place**. Dès que `Media.Position` franchit le seuil, `_playKickTicks` repasse à 0 et la timeline fait un bond soudain de 150-300 ms vers l'avant !
- **Solution recommandée :**
  Activer immédiatement une extrapolation extrapolée douce depuis l'instant du clic (ou interpoler la position) plutôt que de bloquer l'horloge pendant la latence d'initialisation du décodeur.

---

## 3. Audit : Chargement & Importation

| Composant | Comportement Actuel | Impact Performance | Recommandation |
| :--- | :--- | :--- | :--- |
| **Génération Waveform** ([`WaveformGenerator.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/WaveformGenerator.cs#L17)) | Décodage à la fréquence native (ex: 48 kHz mono PCM 16-bit). Pour 2h de vidéo = ~690 Mo de flux brut transmis via stdout pipe et traité échantillon par échantillon en C#. | Lenteur au chargement initial (5 à 15s selon le disque et CPU). | Forcer `-ar 8000` dans FFmpeg. Réduit le volume de données transitant par le pipe de **83 %** sans altérer la précision visuelle des pics. |
| **Persistance Waveform** ([`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L940)) | Aucun cache disque pour la forme d'onde (contrairement aux proxys vidéo). | À chaque ouverture d'un projet, FFmpeg recalcule intégralement la forme d'onde. | Sauvegarder un fichier cache binaire (`.wavecache`) dans `%APPDATA%\RhythmoSync Studio\waveforms` indexé par SHA256 (chemin + taille + date). |
| **Sonde Vidéo & FPS** ([`MainWindow.xaml.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/MainWindow.xaml.cs#L1318)) | `VideoProber` extrait le FPS mais il n'est pas assigné au projet. | Risque de désynchronisation et saccades à l'export. | Mettre à jour `_state.Fps` et `FpsCombo` dès la fin de `VideoProber.ProbeAsync`. |
| **Import Sous-titres** ([`SubtitleIo.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SubtitleIo.cs#L133)) | `content.Replace("\r\n", "\n").Split("\n\n")` alloue d'immenses tableaux de chaînes. | Pression sur le GC (Large Object Heap) sur les fichiers contenant des milliers de sous-titres. | Utiliser un lecteur en streaming (`StringReader` ou parsing par `ReadOnlySpan<char>`). |

---

## 4. Audit : Déplacement & Scrubbing sur la Timeline

| Composant | Comportement Actuel | Impact Performance | Recommandation |
| :--- | :--- | :--- | :--- |
| **Waveform Tiling** ([`WaveformControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/WaveformControl.cs#L97)) | `_tiles.Remove(i)` détruit immédiatement tout `DrawingVisual` et sa `StreamGeometry` dès qu'une tuile sort du viewport. | Lors d'un va-et-vient autour d'une frontière de tuile (30s), la géométrie vectorielle est recréée et compilée continuellement sur le thread UI. | Conserver un cache LRU en mémoire vive des 10 à 20 dernières tuiles générées. |
| **Virtualisation Bande** ([`RhythmoBandControl.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Controls/RhythmoBandControl.cs#L176)) | `for (var i = 0; i < dialogues.Count; i++)` exécuté à chaque frame (60 FPS) pour tous les blocs du projet. | Sur un projet de 3000 répliques : 180 000 itérations par seconde sur le thread UI. | Exploiter le tri chronologique via recherche dichotomique (`BinarySearch`) pour isoler l'intervalle visible en $O(\log N + K)$. |
| **Snap Magnetism** ([`SnapEngine.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Core/SnapEngine.cs#L55)) | Vérification de l'intégralité des blocs du projet sur chaque événement `MouseMove` lors d'un déplacement. | Micro-latences lors de la manipulation de blocs sur de gros projets. | Filtrer les blocs candidats dans la fenêtre temporelle locale ($t \pm \Delta t$). |

---

## 5. Audit : Pipeline d'Exportation Vidéo

| Composant | Comportement Actuel | Impact Performance | Recommandation |
| :--- | :--- | :--- | :--- |
| **Synchronisation Pipeline** ([`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L414-L436)) | Exécution séquentielle : `ReadAsync` (décodeur) $\rightarrow$ `ComposeBandRows` (C#) $\rightarrow$ `WriteAsync` (encodeur). | Le décodeur et l'encodeur FFmpeg attendent mutuellement, empêchant la pleine saturation GPU/CPU. | Mettre en place un tampon d'échange asynchrone (Producer-Consumer via `Channel<byte[]>`) avec 2 ou 3 frames en transit. |
| **Composition Ligne à Ligne** ([`VideoExporter.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.Media/VideoExporter.cs#L487-L517)) | La boucle sur les lignes recalcule la tranche de tuile et appelle le délégué `tile()` 400+ fois par frame. | Surcharge de calcul arithmétique et d'appels de méthode inutile dans le chemin critique d'export. | Pré-calculer les coordonnées horizontales des tranches une fois par frame, puis copier chaque ligne. |
| **Rendu des Tuiles d'Export** ([`BandStripRenderer.cs`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/RhythmoSync.Desktop/src/RhythmoSync.App/Export/BandStripRenderer.cs#L48)) | `app.Dispatcher.Invoke(() => RenderTile(tileIndex))` bloque le thread UI de l'application pendant la rasterisation. | Micro-gels de l'interface graphique (barre de progression, dialogue) pendant l'export. | Pré-générer les tuiles nécessaires au démarrage de l'export ou en tâche de fond anticipée. |

---

## 6. Matrice des Problèmes & Index des Issues GitHub

Les 13 problèmes identifiés ont été rédigés sous forme d'issues détaillées prêtes à l'emploi dans le dossier [`issues/`](file:///c:/Users/gener/Downloads/rhythmosync-mvp%20-%20FullRecr%C3%A9er/issues/) :

| ID | GitHub | Priorité | Domaine | Titre de l'Issue | Fichier local |
| :--- | :---: | :---: | :--- | :--- | :--- |
| **#01** | [#60](https://github.com/DZTic/RhythmoSync/issues/60) | **P0** | Export / Vidéo | Saccade de la bande rythmo à l'export : Désynchronisation de cadence & FPS source ignoré | [`issues/01_...`](issues/01_saccade_timeline_export_fps_cadence.md) |
| **#02** | [#61](https://github.com/DZTic/RhythmoSync/issues/61) | **P0** | Export / Rendu | Troncature entière (int) de stripX et absence d'anti-aliasing subpixel à l'export | [`issues/02_...`](issues/02_saccade_timeline_export_subpixel_truncation.md) |
| **#03** | [#62](https://github.com/DZTic/RhythmoSync/issues/62) | **P1** | Export / Vidéo | Gigue de synchronisation PTS au démarrage FFmpeg (-ss avec filtre fps) | [`issues/03_...`](issues/03_saccade_timeline_export_pts_offset_ffmpeg.md) |
| **#04** | [#63](https://github.com/DZTic/RhythmoSync/issues/63) | **P1** | App / Transport | Gel et saut de la timeline au démarrage de la lecture dans l'application (_playKickTicks) | [`issues/04_...`](issues/04_saccade_timeline_app_play_startup_jitter.md) |
| **#05** | [#64](https://github.com/DZTic/RhythmoSync/issues/64) | **P1** | Timeline / UI | Absence de cache mémoire (LRU) des tuiles de waveform lors du scrubbing | [`issues/05_...`](issues/05_timeline_waveform_tiling_lru_cache.md) |
| **#06** | [#65](https://github.com/DZTic/RhythmoSync/issues/65) | **P1** | Timeline / UI | Parcours linéaire O(N) à 60 FPS dans RhythmoBandControl.VirtualizePass | [`issues/06_...`](issues/06_timeline_band_virtualization_binary_search.md) |
| **#07** | [#66](https://github.com/DZTic/RhythmoSync/issues/66) | **P2** | Timeline / Snap | Optimisation du magnétisme SnapEngine en O(N) sur chaque mouvement souris | [`issues/07_...`](issues/07_timeline_snap_engine_spatial_filtering.md) |
| **#08** | [#67](https://github.com/DZTic/RhythmoSync/issues/67) | **P1** | Chargement | Absence de cache disque persistant pour la forme d'onde (WaveformData) | [`issues/08_...`](issues/08_chargement_waveform_persistent_disk_cache.md) |
| **#09** | [#68](https://github.com/DZTic/RhythmoSync/issues/68) | **P2** | Chargement | Sur-échantillonnage audio inutile (48 kHz) lors de la génération de la waveform | [`issues/09_...`](issues/09_chargement_waveform_audio_resampling_optimization.md) |
| **#10** | [#69](https://github.com/DZTic/RhythmoSync/issues/69) | **P2** | Chargement | Allocations massives de chaînes dans les parseurs de sous-titres (SRT / VTT) | [`issues/10_...`](issues/10_chargement_subtitle_parser_memory_allocation.md) |
| **#11** | [#70](https://github.com/DZTic/RhythmoSync/issues/70) | **P1** | Export / Perf | Absence de parallélisme (pipeline séquentiel) entre décodage, composition et encodage | [`issues/11_...`](issues/11_export_pipelined_producer_consumer_concurrency.md) |
| **#12** | [#71](https://github.com/DZTic/RhythmoSync/issues/71) | **P2** | Export / Perf | Répétition arithmétique et appels redondants dans ComposeBandRows | [`issues/12_...`](issues/12_export_compose_band_rows_arithmetic_optimization.md) |
| **#13** | [#72](https://github.com/DZTic/RhythmoSync/issues/72) | **P2** | Export / UI | Appels bloquants Dispatcher.Invoke lors du rendu des tuiles dans BandStripRenderer | [`issues/13_...`](issues/13_export_band_strip_renderer_pregeneration_ui_unblock.md) |
