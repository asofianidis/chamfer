using System.Globalization;
using Avalonia.Data.Converters;

namespace Chamfer.App;

/// <summary>
/// Translations of command and tab names. Commands keep their English names as identifiers
/// (shortcuts and macros use them); only what's shown changes.
/// </summary>
public static class Localizer
{
    /// <summary>Languages with translations, by code.</summary>
    public static IReadOnlyDictionary<string, string> Languages { get; } = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
    };

    /// <summary>The language in use; from <c>CHAMFER_LANG</c> when set, else English.</summary>
    public static string Language { get; set; } = Environment.GetEnvironmentVariable("CHAMFER_LANG") is { } env && Languages.ContainsKey(env) ? env : "en";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["de"] = new()
        {
            ["Features"] = "Features", ["Surfaces"] = "Flächen", ["Sheet Metal"] = "Blech", ["Weldments"] = "Schweißkonstruktionen",
            ["Mold Tools"] = "Formwerkzeuge", ["Bodies"] = "Körper", ["Simulation"] = "Simulation",
            ["Extruded Boss"] = "Linear austragen", ["Extruded Cut"] = "Linear ausschneiden", ["Revolved Boss"] = "Rotieren",
            ["Revolved Cut"] = "Rotationsschnitt", ["Swept Boss"] = "Austragung", ["Swept Cut"] = "Ausgetragener Schnitt",
            ["Lofted Boss"] = "Ausformung", ["Lofted Cut"] = "Ausgeformter Schnitt", ["Fillet"] = "Verrundung", ["Chamfer"] = "Fase",
            ["Shell"] = "Wandung", ["Draft"] = "Formschräge", ["Hole Wizard"] = "Bohrungsassistent", ["Rib"] = "Rippe",
            ["Linear Pattern"] = "Lineares Muster", ["Circular Pattern"] = "Kreismuster", ["Mirror"] = "Spiegeln",
            ["Plane"] = "Ebene", ["Helix"] = "Helix", ["Thread"] = "Gewinde", ["Dome"] = "Kuppel", ["Mate"] = "Verknüpfung",
            ["Insert Component"] = "Komponente einfügen", ["Interference"] = "Kollisionsprüfung", ["Bill of Materials"] = "Stückliste",
            ["Explode"] = "Explosionsansicht", ["Note"] = "Notiz", ["Section View"] = "Schnittansicht", ["Detail View"] = "Detailansicht",
            ["Fixture"] = "Einspannung", ["Force"] = "Kraft", ["Pressure"] = "Druck", ["Run Study"] = "Studie ausführen",
            ["Combine"] = "Kombinieren", ["Split"] = "Teilen", ["Base Flange"] = "Basislasche", ["Edge Flange"] = "Kantenlasche",
            ["Hem"] = "Saum", ["Structural Member"] = "Strukturbauteil", ["Cut List"] = "Zuschnittsliste",
        },
        ["fr"] = new()
        {
            ["Features"] = "Fonctions", ["Surfaces"] = "Surfaces", ["Sheet Metal"] = "Tôlerie", ["Weldments"] = "Constructions soudées",
            ["Mold Tools"] = "Outils de moule", ["Bodies"] = "Corps", ["Simulation"] = "Simulation",
            ["Extruded Boss"] = "Bossage extrudé", ["Extruded Cut"] = "Enlèvement extrudé", ["Revolved Boss"] = "Bossage avec révolution",
            ["Revolved Cut"] = "Enlèvement avec révolution", ["Swept Boss"] = "Bossage balayé", ["Swept Cut"] = "Enlèvement balayé",
            ["Lofted Boss"] = "Bossage lissé", ["Lofted Cut"] = "Enlèvement lissé", ["Fillet"] = "Congé", ["Chamfer"] = "Chanfrein",
            ["Shell"] = "Coque", ["Draft"] = "Dépouille", ["Hole Wizard"] = "Assistance pour le perçage", ["Rib"] = "Nervure",
            ["Linear Pattern"] = "Répétition linéaire", ["Circular Pattern"] = "Répétition circulaire", ["Mirror"] = "Symétrie",
            ["Plane"] = "Plan", ["Helix"] = "Hélice", ["Thread"] = "Filetage", ["Dome"] = "Dôme", ["Mate"] = "Contrainte",
            ["Insert Component"] = "Insérer un composant", ["Interference"] = "Détection d'interférences", ["Bill of Materials"] = "Nomenclature",
            ["Explode"] = "Vue éclatée", ["Note"] = "Note", ["Section View"] = "Vue en coupe", ["Detail View"] = "Vue de détail",
            ["Fixture"] = "Déplacement imposé", ["Force"] = "Force", ["Pressure"] = "Pression", ["Run Study"] = "Exécuter l'étude",
            ["Combine"] = "Combiner", ["Split"] = "Scinder", ["Base Flange"] = "Tôle de base", ["Edge Flange"] = "Tôle sur arête",
            ["Hem"] = "Ourlet", ["Structural Member"] = "Élément mécano-soudé", ["Cut List"] = "Liste des pièces soudées",
        },
        ["es"] = new()
        {
            ["Features"] = "Operaciones", ["Surfaces"] = "Superficies", ["Sheet Metal"] = "Chapa metálica", ["Weldments"] = "Piezas soldadas",
            ["Mold Tools"] = "Moldes", ["Bodies"] = "Sólidos", ["Simulation"] = "Simulación",
            ["Extruded Boss"] = "Extruir saliente", ["Extruded Cut"] = "Extruir corte", ["Revolved Boss"] = "Revolución de saliente",
            ["Revolved Cut"] = "Revolución de corte", ["Swept Boss"] = "Barrer saliente", ["Swept Cut"] = "Barrer corte",
            ["Lofted Boss"] = "Recubrir saliente", ["Lofted Cut"] = "Recubrir corte", ["Fillet"] = "Redondeo", ["Chamfer"] = "Chaflán",
            ["Shell"] = "Vaciado", ["Draft"] = "Ángulo de salida", ["Hole Wizard"] = "Asistente para taladro", ["Rib"] = "Nervio",
            ["Linear Pattern"] = "Matriz lineal", ["Circular Pattern"] = "Matriz circular", ["Mirror"] = "Simetría",
            ["Plane"] = "Plano", ["Helix"] = "Hélice", ["Thread"] = "Rosca", ["Dome"] = "Domo", ["Mate"] = "Relación de posición",
            ["Insert Component"] = "Insertar componente", ["Interference"] = "Detección de interferencias", ["Bill of Materials"] = "Lista de materiales",
            ["Explode"] = "Vista explosionada", ["Note"] = "Nota", ["Section View"] = "Vista de sección", ["Detail View"] = "Vista de detalle",
            ["Fixture"] = "Sujeción", ["Force"] = "Fuerza", ["Pressure"] = "Presión", ["Run Study"] = "Ejecutar estudio",
            ["Combine"] = "Combinar", ["Split"] = "Dividir", ["Base Flange"] = "Brida base", ["Edge Flange"] = "Brida de arista",
            ["Hem"] = "Dobladillo", ["Structural Member"] = "Miembro estructural", ["Cut List"] = "Lista de cortes",
        },
    };

    /// <summary>The text to show for an English name (the name itself when there's no translation).</summary>
    public static string T(string english) =>
        Language != "en" && Tables.TryGetValue(Language, out var table) && table.TryGetValue(english, out var text) ? text : english;

    /// <summary>For bindings: shows a translated name.</summary>
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, string>(s => s is null ? string.Empty : T(s));

    /// <summary>How many names a language translates (for the language panel).</summary>
    public static int Coverage(string language) => Tables.TryGetValue(language, out var t) ? t.Count : 0;

    internal static string Name(string code) => Languages.TryGetValue(code, out var n) ? n : code.ToUpper(CultureInfo.InvariantCulture);
}
