# 🎨 Design et Expérience Utilisateur (UI/UX)

L'objectif ici est de rendre l'application plus "premium" et adaptée à un usage professionnel prolongé (studios de doublage).

**Esthétique Globale & Thème :**
- **Palette de couleurs harmonisée :** Améliorer le mode sombre actuel avec une palette plus sophistiquée (ex. nuances de gris bleuté type "Slate" ou "Zinc" de Tailwind) pour réduire la fatigue visuelle.
- **Micro-interactions :** Ajouter des transitions douces sur les boutons (hover, active), les ouvertures de menus, et l'apparition de la barre latérale (`EditorSidebar`).
- **Typographie :** Utiliser une police de caractères haut de gamme (ex. *Inter* pour l'interface, *Roboto Mono* pour les timecodes).

**La Bande Rythmo (Canvas Konva) :**
- **Amélioration visuelle des blocs :** Ajouter des gradients subtils, des coins légèrement arrondis (`cornerRadius`) et des ombres portées douces (`shadowBlur`) pour le bloc actuellement sélectionné afin de bien le distinguer.
- **Indicateur de lecture (Playhead) :** Rendre la ligne de synchronisation rouge plus esthétique (ex. trait plus fin avec un léger effet de lueur/glow).

**Ergonomie du Layout :**
- **Interface modulable :** Permettre le redimensionnement fluide entre le lecteur vidéo et la bande rythmo. À terme, proposer une fonctionnalité pour "détacher" la vidéo sur un deuxième écran (très utile en studio).
- **États vides (Empty States) :** Créer des écrans d'accueil visuellement attrayants lorsqu'aucun projet n'est ouvert (illustrations, boutons d'appel à l'action animés), plutôt qu'un écran vide.
