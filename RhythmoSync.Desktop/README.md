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
    │   └── WaveformGenerator.cs # PCM s16le streamé → pics min/max (jamais tout en RAM)
    └── RhythmoSync.App/       # Application WPF
        ├── MainWindow.*       #   Transport, horloge extrapolée, raccourcis, fichiers
        └── Controls/
            ├── RhythmoBandControl.cs  # Bande rythmo native
            └── WaveformControl.cs     # Forme d'onde en tuiles de 30 s
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
- FFmpeg : détection automatique (réutilise celui de l'ancienne app Tauri) ou téléchargement intégré.

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

- Transcription Whisper (port de `diarization.rs` : lancer whisper.cpp en sous-processus).
- Export vidéo avec incrustation de la bande (port de `export.rs`).
- Vidéos proxy All-Intra pour les codecs exotiques (MKV/HEVC) que le décodeur Windows refuse.
- Mixeur audio multi-pistes (NAudio ou MediaPlayer multiples).
- Recherche/remplacement global et décalage de timeline (déjà portés dans `ProjectState`,
  il ne manque que les boîtes de dialogue).
