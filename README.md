# 🎙️ RhythmoSync Studio

**RhythmoSync Studio** est un outil professionnel de doublage et de création de bande rythmo, conçu pour offrir une expérience fluide et précise aux comédiens, directeurs artistiques et ingénieurs du son.

> ## ⚡ Nouvelle version : application Windows **native** (C# / WPF, .NET 8)
>
> RhythmoSync est en cours de réécriture en **véritable application Windows native**, sans Tauri ni WebView,
> pour de bien meilleures performances (rendu GPU « retained mode », virtualisation, RAM constante).
> Le nouveau code vit dans **[`RhythmoSync.Desktop/`](RhythmoSync.Desktop/README.md)** — voir son README
> pour compiler, lancer et connaître les fonctionnalités déjà portées (lecture, bande rythmo, forme d'onde,
> undo/redo, projets `.rsp`, **export vidéo MP4 avec bande incrustée**).
>
> La version React / Tauri décrite ci-dessous reste disponible **comme référence** pendant la migration.

## ✨ Fonctionnalités Clés *(version React / Tauri — référence)*

- **🎬 Synchronisation Vidéo-Rythmo** : Lecture fluide avec une bande rythmo interactive basée sur Canvas (Konva) pour une précision extrême.
- **🔊 Mixeur Audio Multi-pistes** : Gérez indépendamment les pistes originales, les voix enregistrées et les bruitages avec fonctions Mute/Solo.
- **⚡ Génération IA (Whisper)** : Créez automatiquement des blocs de texte à partir de l'audio de la vidéo avec des timecodes précis au mot près.
- **🛠️ Édition Avancée** :
  - Timeline avec zoom dynamique.
  - Outils de recherche et remplacement globaux.
  - Décalage temporel (Shift timeline).
  - Magnétisme (Snap) des blocs.
  - Limitation de vitesse de lecture (indicateurs visuels pour garantir la lisibilité).
- **📦 Gestion de Projet** : Importez et exportez des sessions complètes au format `.rsp`.
- **🚀 Performance** : Support des vidéos proxy (All-Intra) pour un défilement (scrubbing) instantané et sans saccades.

## 🛠️ Installation

**Prérequis :**
- Node.js (v18+)
- Rust (pour le mode Desktop via Tauri)

1. **Cloner le projet** :
   ```bash
   git clone [url-du-repo]
   cd rhythmosync-studio
   ```

2. **Installer les dépendances** :
   ```bash
   npm install
   ```



## 🚀 Utilisation

### Mode Développement (Web)
Pour lancer l'aperçu web dans votre navigateur :
```bash
npm run dev
```

### Mode Desktop (Tauri)
Pour lancer l'application native (Windows/macOS/Linux) :
```bash
npm run tauri:dev
```

### Build Production
```bash
npm run tauri:build
```

## 🏗️ Stack Technique

- **Frontend** : React, TypeScript, Tailwind CSS
- **Timeline** : Konva / React-Konva
- **Desktop** : Tauri (Backend Rust)
- **State Management** : Zustand
- **Icons** : Lucide React

---

*Développé pour l'excellence dans le doublage professionnel.*

