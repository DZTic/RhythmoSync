# 🎙️ RhythmoSync Studio — Application Windows native (.NET 8 / WPF)

Réécriture native Windows de RhythmoSync Studio, sans Tauri ni WebView : C# / WPF,
rendu GPU « retained mode », zéro dépendance NuGet externe.

## Architecture

```
RhythmoSync.Desktop/
├── RhythmoSync.Desktop.sln
└── src/
    ├── RhythmoSync.Core/      # Modèle de données, état + undo/redo, snap, format .rsp
    │   ├── Models/            #   DialogueBlock (record immuable), ProjectFile (JSON .rsp)
    │   ├── ProjectState.cs    #   Portage du store Zustand (historique 50 niveaux)
    │   └── SnapEngine.cs      #   Magnétisme (ligne de synchro + bords de blocs)
    ├── RhythmoSync.Media/     # Intégration FFmpeg (aucune dépendance UI)
    │   ├── FfmpegLocator.cs   #   Localisation (exe, ancienne app Tauri, PATH)
    │   ├── FfmpegDownloader.cs#   Téléchargement auto (gyan.dev essentials)
    │   ├── WaveformGenerator.cs # PCM s16le streamé → pics min/max (jamais tout en RAM)
    │   ├── VideoProber.cs     #   Sonde conteneur/codec → ce format exige-t-il un proxy ?
    │   ├── ProxyGenerator.cs  #   Proxy All-Intra H.264 ≤ 1080p + cache durable
    │   ├── VideoExporter.cs   #   Export MP4 avec bande incrustée (+ ExportLayout.cs)
    │   └── WhisperService.cs  #   Transcription locale (whisper-cli.exe en sous-processus)
    └── RhythmoSync.App/       # Application WPF
        ├── MainWindow.*       #   Transport, horloge extrapolée, raccourcis, fichiers
        ├── Audio/
        │   └── AudioMixer.cs  #   Pistes externes : MediaPlayer asservis au transport
        └── Controls/
            ├── RhythmoBandControl.cs  # Bande rythmo native
            ├── WaveformControl.cs     # Forme d'onde en tuiles de 30 s
            └── AudioMixerPanel.cs     # Panneau du mixeur (volume/mute/solo/fichier)
```

## Pourquoi c'est plus rapide que la version web/Tauri

| Optimisation web (Konva)            | Équivalent natif WPF                                        |
|-------------------------------------|-------------------------------------------------------------|
| `node.cache()` (rasterisation)      | Comportement par défaut des `DrawingVisual` (retained mode) |
| Défilement par `layer.x()` + redraw | `TranslateTransform` composée sur GPU — **zéro redraw**     |
| Virtualisation (proposition n°1)    | Implémentée : seuls les blocs visibles ± 3 s existent       |
| Tiling waveform 30 s                | Implémenté : tuiles `DrawingVisual` figées, démontées hors champ |
| Extrapolation rAF + performance.now | `CompositionTarget.Rendering` + `Stopwatch`                 |
| Décodage waveform en Rust           | FFmpeg → flux PCM bucketé au fil de l'eau (RAM constante)   |

## Compiler et lancer

```powershell
dotnet build RhythmoSync.Desktop.sln          # build debug
dotnet run --project src/RhythmoSync.App      # lancer
dotnet publish src/RhythmoSync.App -c Release -r win-x64 --self-contained false -o publish
```

Prérequis : .NET 8 SDK (runtime .NET 8 Desktop suffit pour exécuter le publish).

## Fonctionnalités (V1 « cœur »)

- Import vidéo (MP4/H.264, WMV, AVI… via le décodeur Windows), lecture/pause/vitesse/volume.
- Bande rythmo : défilement fluide, ligne de synchro à x=300, création par double-clic,
  déplacement, redimensionnement par poignées, snap (ligne + bords), multi-sélection (Ctrl),
  groupes (Ctrl+G), alerte « trop rapide » (> 20 car/s) avec étirement auto, édition par double-clic.
- Forme d'onde sous la bande, scrub à la souris (glisser) et saut (clic) partout.
- Undo/redo (50 niveaux), copier/coller à la tête de lecture.
- Projets `.rsp` 100 % compatibles avec ceux de la version web.
- FFmpeg : détection automatique (réutilise celui de l'ancienne app Tauri) ou téléchargement intégré
  (gyan.dev, avec repli GitHub si la chaîne de certificats échoue).
- **Export vidéo MP4 avec bande incrustée** (port d'export.rs, amélioré) :
  - Layout 1920×1080 fixe (vidéo en haut, bande qui remplit le bas) — les présets 480p/720p/1080p
    ne changent que le bitrate, personnalisable.
  - Bande rendue **en tuiles paresseuses** avec le même style que l'écran (l'ancien strip PNG était
    plafonné à 32 000 px → bande tronquée après quelques minutes ; corrigé).
  - **Audio de la source inclus** (l'ancien export produisait des vidéos muettes) — désactivable.
  - **Les pistes du mixeur sont mixées à l'export** : Voix/Bruitages chargées sont
    combinées à l'audio source (`filter_complex` volume + `amix`) en respectant les
    volumes, Mute et Solo du mixeur ; une source sans audio est détectée et exclue.
  - Letterbox auto (désactivable), encodeur GPU détecté (NVENC/AMF/QuickSync) ou CPU forcé,
    plage d'export optionnelle, **annulation** en cours d'encodage, progression %/fps/ETA.
- **Proxy All-Intra pour les formats illisibles** (MKV, WebM, HEVC, AV1… — port de
  generate_proxy_video, amélioré) :
  - Flux hybride : sonde FFmpeg à l'import (formats connus illisibles → proxy direct),
    sinon lecture native avec repli proxy proposé si MediaElement échoue.
  - H.264 All-Intra (chaque image est un keyframe → seeking instantané), ≤ 1080p sans
    agrandissement, encodeur GPU détecté ou libx264, sortie yuv420p (les sources 10 bits
    donnaient sinon un proxy lui-même illisible).
  - **Non bloquant** : progression et annulation dans le panneau vidéo, bande et forme
    d'onde utilisables pendant l'encodage.
  - Cache durable (`%APPDATA%\RhythmoSync Studio\proxies`), invalidé si le fichier source
    change (taille/date — l'ancien hash du seul chemin rejouait un proxy périmé),
    bouton de purge avec taille dans la barre d'état.
  - Le `.rsp`, l'export, le letterbox et la waveform utilisent **toujours l'original** ;
    seule la lecture passe par le proxy.
- **Mixeur audio multi-pistes** (port du mixeur de l'EditorSidebar web) :
  - Trois pistes : Original (l'audio de la vidéo), Voix et Bruitages, chacune avec
    volume, Mute et Solo (un solo coupe les autres pistes, comme dans la version web).
  - Chargement d'un fichier audio (WAV/MP3/M4A…) dans les pistes Voix/Bruitages :
    un `MediaPlayer` WPF par piste, asservi au transport vidéo (play/pause/seek/vitesse)
    avec recalage automatique de la dérive (seuil 150 ms, contrôle 2×/s).
  - Panneau repliable à droite de la vidéo (bouton « 🎚 Mixeur » du transport).
  - Pistes sauvegardées dans le `.rsp` (champ `audioTracks`, compatible web) ; un chemin
    introuvable (ex. URL blob d'un vieux projet web) restaure les réglages sans lecture.
- Ouverture par ligne de commande : `RhythmoSyncStudio.exe fichier.rsp` (ou une vidéo).

### Raccourcis

| Touche | Action |
|---|---|
| `Espace` | Lecture / pause |
| `Ctrl+Z` / `Ctrl+Y` | Annuler / rétablir |
| `Suppr` | Supprimer la sélection |
| `← / →` | ± 1 image (`Shift` : ± 1 s, `Ctrl` : décale les blocs sélectionnés) |
| `Ctrl+C` / `Ctrl+V` | Copier / coller à la tête de lecture |
| `Ctrl+G` / `Ctrl+Shift+G` | Grouper / dégrouper |
| `Ctrl+S` / `Ctrl+O` / `Ctrl+I` | Enregistrer / ouvrir / importer une vidéo |
| `Ctrl+ + / −` | Zoom |

## Étapes suivantes (non incluses dans la V1)

- Recherche/remplacement global et décalage de timeline (déjà portés dans `ProjectState`,
  il ne manque que les boîtes de dialogue).
