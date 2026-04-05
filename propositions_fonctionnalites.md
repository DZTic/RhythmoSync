# 🚀 Nouvelles Fonctionnalités à Implémenter

Pour faire de RhythmoSync un outil complet et incontournable.

---

## 🎙️ A. Audio & Enregistrement

- **Enregistrement vocal intégré :** Ajouter la possibilité d'enregistrer l'audio du comédien directement via le microphone dans l'application, synchronisé avec la vidéo et la bande rythmo. Inclure un pré-count (compte à rebours visuel/sonore) avant l'enregistrement.
- ~~**Multipistes audio :** Gérer plusieurs pistes audio (Piste originale, Piste voix, Bruitages) avec la possibilité de les muter (Mute) ou de les mettre en solo. Afficher un mixeur minimaliste dans la sidebar.~~ ✅ **Implémenté**
- **Monitoring en temps réel :** Afficher un VU-mètre pendant l'enregistrement pour que le comédien puisse contrôler son niveau audio sans sortir de l'application.

---

## 🎬 B. Bande Rythmo — Édition avancée

- **Détection des silences (Auto-split) :** Un outil qui analyse la forme d'onde audio et découpe automatiquement de longs blocs de sous-titres en plusieurs segments en fonction des silences détectés dans la vidéo.
- **Verrouillage de blocs :** Permettre de verrouiller un ou plusieurs blocs pour éviter de les déplacer accidentellement lors d'une session de doublage.
- ~~**Zoom indépendant par piste :** Permettre de "zoomer" verticalement une piste spécifique pour afficher plus d'informations textuelles sur les blocs les plus courts.~~ ✅ **Implémenté**
- **Groupes de blocs :** Sélectionner plusieurs blocs et les regrouper pour les déplacer/supprimer ensemble.
- **Aperçu au survol :** Au survol d'un bloc, afficher une petite infobulle avec le texte complet, le nom du personnage et les timecodes précis.
- ~~**Copier / Coller des blocs :** `Ctrl+C` / `Ctrl+V` pour dupliquer un ou plusieurs blocs sélectionnés.~~ ✅ **Implémenté**
- **Fusion de blocs (Merge) :** Depuis la sélection de 2 blocs adjacents, les fusionner en un seul bloc dont le texte est concaténé.
- ~~**Ajustement auto selon la longueur du texte :** Quand un texte est tapé ou modifié (Que ce soit In-Place ou via le menu), la durée du bloc d'agrandit automatiquement pour garantir une vitesse de lecture confortable (max 20 caractères par seconde).~~ ✅ **Implémenté**
- ~~**Création "À la volée" (Tap to Add) :** Maintenir une touche pavé numérique ou la rangée du haut (1 à 9) enfoncée pendant la lecture vidéo pour créer dynamiquement et en temps réel un bloc s'étalant sur cette durée.~~ ✅ **Implémenté**
- ~~**Auto-génération avec Timecodes Précis (Word-level timestamps) :** Whisper génère un bloc Rhythmo strictement découpé par mot pour un ajustement ultra-précis à la lèvre près.~~ ✅ **Implémenté**
- ~~**Indicateur de vitesse de lecture (Lisibilité) :** Avertissement visuel (bordure rouge + ⚠️) si le texte défile trop vite pour être lu par le comédien (plus de 20 caractères par seconde).~~ ✅ **Implémenté**

---

## 📤 C. Export & Workflow

- **Export Mixé :** Permettre l'export de la vidéo avec le mixage audio final (voix enregistrées + audio original réduit ou coupé).
- **Présets d'export :** Sauvegarder des configurations d'export personnalisées (résolution, bitrate, fps) sous forme de présets réutilisables.
- **Export d'une plage :** Exporter uniquement la portion de la vidéo entre deux marqueurs (In/Out), au lieu de la vidéo complète.
- **Export sélectif des pistes :** Choisir quelles pistes de la bande rythmo inclure dans l'export vidéo.
- **Export image de la bande rythmo :** Générer une image PNG haute résolution de toute la bande rythmo pour l'imprimer ou l'envoyer par email.

---

## 🖥️ D. Interface & Expérience Utilisateur

- **Thèmes personnalisables :** Proposer plusieurs thèmes de couleurs (Sombre, Clair, Contraste élevé) et permettre de modifier les couleurs principales via les Préférences.
- **Raccourcis matériels (Pédales USB) :** Améliorer la compatibilité avec les pédales USB (souvent utilisées par les comédiens de doublage pour Play/Pause/Record) via un gestionnaire de raccourcis entièrement personnalisables dans les Préférences.
- **Panneau de script flottant :** Afficher le texte des blocs dans une fenêtre séparée (toujours visible), optimisée pour que le comédien lise son texte à l'écran.
- ~~**Mode Présentation/Doublage :** Un mode plein écran épuré qui n'affiche que la vidéo + la bande rythmo défilante, sans les menus et panneaux d'édition.~~ ✅ **Implémenté** (F11 pour activer, Échap pour quitter)
- **Timeline de marqueurs (In/Out) :** Ajouter des marqueurs visuels sur la timeline pour délimiter les scènes, takes, ou sections importantes, avec possibilité de les nommer et de s'y téléporter en cliquant.
- **Prévisualisation de l'export en temps réel :** Avant de lancer l'export final, afficher une fenêtre « preview » qui montre un aperçu de la composition (vidéo + bande incrustée) avec possibilité de faire défiler la timeline.

---

## ☁️ E. Projet & Collaboration

- ~~**Export de session (.rsp) partageable :** Permettre d'exporter un fichier `.rsp` auto-contenu qui embarque les métadonnées du projet, les dialogues et les références aux fichiers médias (ou une version proxy légère de la vidéo).~~ ✅ **Implémenté**
- **Backups automatiques :** Activer des sauvegardes automatiques toutes les X minutes et stocker les N dernières versions du projet, accessibles depuis un menu « Versions ».
- **Projets récents :** Afficher dans le menu `Fichier` la liste des derniers projets ouverts, avec un aperçu de la miniature vidéo.

---

## ⚙️ F. Technique & Performances

- ~~**Support nativement des formats vidéo supplémentaires :** S'assurer via FFmpeg que l'import prend en charge `.mkv`, `.avi`, `.mov`, `.webm`, en plus de `.mp4`.~~ ✅ **Implémenté**
- ~~**Encodage GPU (NVENC / AMF / VideoToolbox) :** Détecter automatiquement si un GPU compatible est disponible et utiliser l'encodage matériel pour accélérer drastiquement les exports vidéo.~~ ✅ **Implémenté**
- ~~**Cache de décodage :** Mettre en mémoire tampon les frames autour du point de lecture actuel pour un seek quasi instantané même sur les vidéos lourdes.~~ ✅ **Implémenté**
