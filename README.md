# 🎙️ RhythmoSync Studio

[![CI](https://github.com/DZTic/RhythmoSync/actions/workflows/ci.yml/badge.svg)](https://github.com/DZTic/RhythmoSync/actions/workflows/ci.yml)

**RhythmoSync Studio** est un outil professionnel de doublage et de création de bande rythmo, conçu pour offrir une expérience fluide et précise aux comédiens, directeurs artistiques et ingénieurs du son.

Application **Windows native** (C# / WPF, .NET 8) — sans Tauri ni WebView, pour de bien meilleures performances (rendu GPU « retained mode », virtualisation, empreinte mémoire constante).

> Le code de l'application vit dans **[`RhythmoSync.Desktop/`](RhythmoSync.Desktop/README.md)**. Consultez son README pour compiler, lancer et connaître le détail des fonctionnalités.
>
> *L'ancienne version React / Tauri a été retirée du dépôt une fois la parité atteinte ; elle reste consultable dans l'historique git.*

## ✨ Fonctionnalités

- **🎬 Bande rythmo native** : défilement GPU fluide, magnétisme (snap), multi-sélection, groupes, alerte de vitesse de lecture, double-clic pour créer/éditer.
- **🔊 Mixeur audio multi-pistes** : pistes Original / Voix / Bruitages, volume / mute / solo.
- **⚡ Transcription Whisper locale** : génère des blocs de dialogue à partir de l'audio (whisper.cpp, 100 % hors-ligne), avec répartition personnage A/B.
- **🛠️ Édition** : forme d'onde + scrub, transport, undo/redo (50 niveaux), copier/coller, panneau d'édition du bloc, décalage de timeline, rechercher/remplacer.
- **🎞️ Export** : MP4 avec bande rythmo incrustée (+ audio source et mix des pistes), proxy All-Intra pour les formats illisibles (MKV / HEVC / AV1…) avec cache.
- **📄 Texte** : import / export SRT, VTT, TXT, CSV ; import de sous-titres.
- **🖥️ Mode Présentation / Doublage** : plein écran (F11) pour la session d'enregistrement.
- **📦 Projets** : format `.rsp` (JSON), compatible avec l'ancienne version web.

## 🚀 Compilation & lancement

Prérequis : **.NET 8 SDK** sous Windows. Détails dans **[`RhythmoSync.Desktop/README.md`](RhythmoSync.Desktop/README.md)**.

```bash
dotnet build RhythmoSync.Desktop/RhythmoSync.Desktop.sln -c Release
```

## 📚 Notes & propositions

- Pistes d'évolution : [`propositions_fonctionnalites.md`](propositions_fonctionnalites.md), [`propositions_ameliorations.md`](propositions_ameliorations.md), [`propositions_design_ui_ux.md`](propositions_design_ui_ux.md), [`propositions_performances.md`](propositions_performances.md)
- Journal de bord : [`rapport_erreurs_et_solutions.md`](rapport_erreurs_et_solutions.md)

---

*Développé pour l'excellence dans le doublage professionnel.*
