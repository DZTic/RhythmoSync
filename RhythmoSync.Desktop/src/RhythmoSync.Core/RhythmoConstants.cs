namespace RhythmoSync.Core;

/// <summary>Constantes partagées (mêmes valeurs que l'ancienne version web, types.ts).</summary>
public static class RhythmoConstants
{
    /// <summary>Position X fixe de la ligne rouge de synchronisation, en pixels.</summary>
    public const double SyncLinePositionX = 300;

    /// <summary>Zoom par défaut, en pixels par seconde.</summary>
    public const double DefaultPps = 200;

    /// <summary>Hauteur par défaut d'une piste (lane).</summary>
    public const double LaneHeight = 80;

    /// <summary>Vitesse de lecture maximale recommandée (caractères par seconde).</summary>
    public const double MaxCharsPerSecond = 20;

    /// <summary>Largeur minimale d'un bloc en pixels (empêche les blocs invisibles).</summary>
    public const double MinBlockWidthPx = 10;

    public static readonly string[] CharacterColors =
    [
        "#6366f1", // indigo
        "#ec4899", // rose
        "#f59e0b", // ambre
        "#10b981", // émeraude
        "#3b82f6", // bleu
        "#8b5cf6", // violet
        "#ef4444", // rouge
        "#14b8a6", // sarcelle
    ];
}
