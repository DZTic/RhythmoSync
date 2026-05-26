# Rapport des Problèmes, Manques et Solutions pour RhythmoSync Studio

Suite à l'analyse du code source et des propositions existantes (`propositions_*.md`), voici un rapport complet de ce qui manque ou pose problème dans le logiciel actuel, accompagné d'un guide étape par étape pour les résoudre.

---

## 1. ⚡ Problèmes de Performance Restants

**Problème 1 : Manque de Virtualisation du Canvas pour la Bande Rythmo**
*   **Description :** Dans `components/RhythmoBand.tsx`, le composant rend *tous* les blocs de dialogue (`dialogues.map(...)`) indépendamment de la position actuelle de la vidéo (`currentTime`). Contrairement à la *Waveform* qui utilise une logique de "Tiling" (tuiles) pour ne rendre que ce qui est visible, la Bande Rythmo surcharge le DOM Konva si le projet contient des milliers de sous-titres, ce qui fera chuter drastiquement les FPS (Frames Per Second).
*   **Comment le régler :**
    1.  Ouvrir `components/RhythmoBand.tsx`.
    2.  Identifier la largeur visible de la timeline (`width`) et le zoom actuel (`zoomLevel`).
    3.  Calculer les bornes temporelles visibles : `visibleStartTime = videoElement.currentTime - (SYNC_LINE_POSITION_X / zoomLevel)` et `visibleEndTime = visibleStartTime + (width / zoomLevel)`.
    4.  Ajouter une marge (ex: +/- 10 secondes) pour éviter les apparitions brutales.
    5.  Filtrer le tableau `dialogues` pour ne passer à Konva que les `DialogueItem` dont le `startTime` (ou `startTime + duration`) se trouve dans cette plage.

**Problème 2 : Absence de Web Workers pour les gros calculs**
*   **Description :** Des calculs lourds, comme la mise à jour massive de l'historique ou les algorithmes de magnétisme (snap) complexes, s'exécutent sur le thread principal UI, causant potentiellement des micro-gels (stutters).
*   **Comment le régler :**
    1.  Créer un dossier `src/workers`.
    2.  Déplacer les fonctions pures complexes (ex: algorithme d'auto-split, traitement de données d'export) dans des fichiers dédiés `.worker.ts`.
    3.  Instancier et communiquer avec ces workers depuis les composants React pour soulager le thread principal.

---

## 2. 🚀 Fonctionnalités Manquantes (Basé sur les propositions)

**Fonctionnalité 1 : Enregistrement vocal intégré et Monitoring**
*   **Description :** RhythmoSync est un outil de doublage, mais il n'est actuellement pas possible d'enregistrer sa voix directement depuis l'application.
*   **Comment le régler :**
    1.  Ajouter un bouton "Record" (🎙️) rouge dans `components/VideoPlayer.tsx` à côté du bouton Play.
    2.  Demander l'accès au microphone via l'API Web `navigator.mediaDevices.getUserMedia`.
    3.  Utiliser `MediaRecorder` pour capturer l'audio pendant la lecture de la vidéo.
    4.  À l'arrêt, sauvegarder le Blob audio, l'ajouter à la piste "Voix" dans le store Zustand (`audioTracks`), et synchroniser sa lecture avec la vidéo principale.

**Fonctionnalité 2 : Détection automatique des silences (Auto-split)**
*   **Description :** Impossible de découper un long bloc de texte automatiquement en se basant sur le son de la vidéo.
*   **Comment le régler :**
    1.  Récupérer les données de la forme d'onde (`WaveformData.peaks`) depuis Rust (déjà fait dans `Waveform.tsx`).
    2.  Créer un bouton "Auto-Split" dans `EditorSidebar.tsx` ou un menu contextuel pour un bloc sélectionné.
    3.  Écrire une fonction qui parcourt les `peaks` correspondant à la durée du bloc. Si le volume descend sous un seuil (ex: -40dB) pendant plus de X millisecondes, diviser le bloc en deux et répartir le texte proportionnellement.

**Fonctionnalité 3 : Verrouillage et Groupes de Blocs**
*   **Description :** La fonction "Grouper" existe dans le store Zustand et s'affiche dans l'UI (`EditorSidebar`), mais la manipulation visuelle (déplacer le groupe entier ensemble) n'est pas complètement implémentée/fiabilisée dans `RhythmoBand.tsx`. Le "Verrouillage" de blocs est totalement absent.
*   **Comment le régler :**
    1.  Ajouter un booléen `locked` dans l'interface `DialogueBlock` (`types.ts`).
    2.  Dans `RhythmoBand.tsx` (`DialogueItem`), si `locked` est vrai, forcer `draggable={false}` et désactiver les poignées de redimensionnement.
    3.  Ajouter un bouton "Verrouiller" (🔒) dans `EditorSidebar.tsx`.

**Fonctionnalité 4 : Options d'Export Avancées**
*   **Description :** L'export vidéo (`handleExportOffline` dans `App.tsx`) fonctionne, mais il manque des options comme l'Export Mixé (vidéo + nouvelles voix) et l'export d'une image haute résolution de la bande rythmo.
*   **Comment le régler :**
    1.  Pour l'export image : Créer une fonction qui utilise `stageRef.current.toDataURL()` avec un `pixelRatio` élevé et déclenche un téléchargement.
    2.  Pour l'export mixé : Mettre à jour la commande Rust `export_video_native` pour accepter en paramètre les chemins des fichiers audio enregistrés (voir Fonctionnalité 1) et utiliser FFmpeg pour mixer ces pistes avec la vidéo source (via `filter_complex`).

---

## 3. 🎨 Design et Améliorations de l'Interface (UI/UX)

**Problème 1 : États vides (Empty States) manquants ou perfectibles**
*   **Description :** Lorsqu'il n'y a pas de dialogues, la Timeline/Bande rythmo affiche juste un fond quadrillé. Il faudrait guider l'utilisateur.
*   **Comment le régler :**
    1.  Dans `RhythmoBand.tsx`, si `dialogues.length === 0`, rendre un composant `Konva.Text` ou un élément HTML superposé au centre de la timeline indiquant "Double-cliquez ou appuyez sur 'Nouveau bloc' pour commencer à écrire".

**Problème 2 : Personnalisation et Typographie**
*   **Description :** Le texte dans les blocs utilise une police "monospace" basique. Les thèmes de couleurs personnalisés ne sont pas implémentés.
*   **Comment le régler :**
    1.  Importer une web font haut de gamme (ex: *Inter* pour l'UI, *JetBrains Mono* ou *Fira Code* pour les sous-titres) via un fichier CSS/Google Fonts.
    2.  Modifier la propriété `fontFamily` dans `DialogueItem` (`RhythmoBand.tsx`) pour utiliser cette nouvelle police.
    3.  Créer une section "Thème" dans la modale Préférences (`App.tsx`) permettant de modifier les variables CSS (ex: `--rs-bg-base`, `--rs-accent`).

---

## Résumé du Plan d'Action (Priorité Suggérée)

Pour rendre ce logiciel professionnel, il est recommandé de suivre cet ordre de résolution :

1.  **Immédiat (Performance) :** Implémenter la Virtualisation dans `RhythmoBand.tsx` (Culling spatial). C'est vital pour les longs projets (films entiers).
2.  **Essentiel (Fonctionnalité) :** Ajouter l'enregistrement vocal (API MediaRecorder) et l'intégrer au Mixeur Audio existant. Un outil de doublage *doit* pouvoir enregistrer des voix.
3.  **Confort (UI/UX & Outils) :** Ajouter le verrouillage des blocs, peaufiner l'auto-split basé sur l'audio, et rajouter une police monospace dédiée pour faciliter la lecture des comédiens.
