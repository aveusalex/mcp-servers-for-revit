using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>A category as the document knows it (built-in enum name when there is one, localized name, id).</summary>
    public sealed class CategoryEntry
    {
        public CategoryEntry(string builtInName, string name, long id)
        {
            BuiltInName = builtInName;
            Name = name;
            Id = id;
        }

        /// <summary>"OST_Walls"; null for categories without a BuiltInCategory.</summary>
        public string BuiltInName { get; }

        /// <summary>Name in the language Revit runs in ("Paredes" in pt-BR, "Walls" in English).</summary>
        public string Name { get; }

        public long Id { get; }
    }

    /// <summary>
    /// Language-neutral category resolution. Accepts the enum name (<c>OST_Walls</c>), the enum name without the
    /// prefix (<c>Walls</c>), the name in the language Revit runs in, and English / pt-BR aliases (so "Paredes" works in
    /// an English Revit and "Walls" in a Portuguese one). Never matches by substring: an unknown name fails with
    /// CATEGORY_NOT_FOUND and suggestions. Pure: the Revit-side wrapper passes the document's categories and an enum
    /// parser. Alias idea adapted from LuDattilo/revit-mcp-server CategoryResolver (MIT).
    /// </summary>
    public static class CategoryCatalog
    {
        // alias (compared by TextMatch.Key) → BuiltInCategory name.
        private static readonly Dictionary<string, string> Aliases = BuildAliases();

        public static IReadOnlyDictionary<string, string> AliasTable => Aliases;

        /// <param name="input">What the caller typed.</param>
        /// <param name="docCategories">The document's categories.</param>
        /// <param name="canonicalBuiltIn">Returns the canonical enum name for a candidate ("ost_walls" → "OST_Walls"), or null.</param>
        public static CategoryEntry Resolve(string input, IEnumerable<CategoryEntry> docCategories,
            Func<string, string> canonicalBuiltIn)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ToolException("CATEGORY_NOT_FOUND", "Category name is empty.");

            var cats = (docCategories ?? Enumerable.Empty<CategoryEntry>()).ToList();
            var text = input.Trim();

            // 1. Enum name, with or without the OST_ prefix (spaces dropped: "Generic Model" → OST_GenericModel).
            var compact = text.Replace(" ", string.Empty);
            var enumName = canonicalBuiltIn(compact.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? compact : "OST_" + compact);
            if (enumName != null) return FromBuiltIn(enumName, cats);

            // 2. The name in the document's language.
            var named = NameMatcher.FindBest(text, cats.Select(c => new NamedCandidate<CategoryEntry>(c.Name, c)).ToList());
            if (named.Count == 1) return named[0].Item;
            if (named.Count > 1)
            {
                // Same localized name on different categories: prefer a unique built-in one, else refuse.
                var builtIn = named.Where(n => n.Item.BuiltInName != null).ToList();
                if (builtIn.Count == 1) return builtIn[0].Item;
                throw new ToolException("AMBIGUOUS_NAME",
                    $"Category '{input}' matches several categories: " +
                    string.Join("; ", named.Select(n => n.Item.BuiltInName ?? n.Item.Name + " (id " + n.Item.Id + ")")) + ".",
                    "Use the OST_ enum name, e.g. OST_Walls.");
            }

            // 3. English / pt-BR alias.
            if (Aliases.TryGetValue(TextMatch.Key(text), out var aliased))
            {
                var canon = canonicalBuiltIn(aliased);
                if (canon != null) return FromBuiltIn(canon, cats);
            }

            var candidates = cats.Select(c => c.Name).Concat(cats.Where(c => c.BuiltInName != null).Select(c => c.BuiltInName));
            var suggestions = TextMatch.Suggest(text, candidates);
            throw new ToolException("CATEGORY_NOT_FOUND", $"Category '{input}' was not found.",
                suggestions.Count > 0
                    ? "Did you mean: " + string.Join(", ", suggestions.Select(s => $"'{s}'")) + "?"
                    : "Use the OST_ enum name (e.g. OST_Walls) or the category name shown in Revit.");
        }

        private static CategoryEntry FromBuiltIn(string enumName, List<CategoryEntry> cats) =>
            cats.FirstOrDefault(c => string.Equals(c.BuiltInName, enumName, StringComparison.Ordinal))
            ?? new CategoryEntry(enumName, null, 0);

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string bic, params string[] names)
            {
                foreach (var n in names) map[TextMatch.Key(n)] = bic;
            }

            // English friendly names and pt-BR names as shown by Revit / used in the office.
            Add("OST_Walls", "Walls", "Wall", "Paredes", "Parede");
            Add("OST_Floors", "Floors", "Floor", "Pisos", "Piso", "Lajes", "Laje");
            Add("OST_Roofs", "Roofs", "Roof", "Telhados", "Telhado", "Coberturas", "Cobertura");
            Add("OST_Ceilings", "Ceilings", "Ceiling", "Forros", "Forro");
            Add("OST_Doors", "Doors", "Door", "Portas", "Porta");
            Add("OST_Windows", "Windows", "Window", "Janelas", "Janela");
            Add("OST_Rooms", "Rooms", "Room", "Ambientes", "Ambiente", "Salas");
            Add("OST_Areas", "Areas", "Area", "Áreas");
            Add("OST_MEPSpaces", "Spaces", "MEP Spaces", "Espaços");
            Add("OST_Columns", "Columns", "Architectural Columns", "Pilares", "Pilar", "Pilares arquitetônicos");
            Add("OST_StructuralColumns", "Structural Columns", "Pilares estruturais", "Pilar estrutural");
            Add("OST_StructuralFraming", "Structural Framing", "Beams", "Framing estrutural", "Vigas", "Viga");
            Add("OST_StructuralFoundation", "Structural Foundations", "Foundations", "Fundação estrutural", "Fundações");
            Add("OST_Stairs", "Stairs", "Escadas", "Escada");
            Add("OST_StairsRailing", "Railings", "Railing", "Guarda-corpos", "Guarda-corpo");
            Add("OST_Ramps", "Ramps", "Rampas", "Rampa");
            Add("OST_Furniture", "Furniture", "Mobiliário", "Mobília", "Móveis");
            Add("OST_FurnitureSystems", "Furniture Systems", "Sistemas de mobiliário");
            Add("OST_Casework", "Casework", "Marcenaria");
            Add("OST_PlumbingFixtures", "Plumbing Fixtures", "Plumbing", "Peças hidrossanitárias", "Louças");
            Add("OST_MechanicalEquipment", "Mechanical Equipment", "Equipamento mecânico", "Equipamentos mecânicos");
            Add("OST_ElectricalEquipment", "Electrical Equipment", "Equipamento elétrico", "Equipamentos elétricos");
            Add("OST_ElectricalFixtures", "Electrical Fixtures", "Dispositivos elétricos", "Luminárias e dispositivos elétricos");
            Add("OST_LightingFixtures", "Lighting Fixtures", "Luminárias", "Luminária");
            Add("OST_SpecialityEquipment", "Specialty Equipment", "Equipamento especializado", "Equipamentos especiais");
            Add("OST_GenericModel", "Generic Models", "Generic Model", "Modelos genéricos", "Modelo genérico");
            Add("OST_Mass", "Mass", "Massa", "Massas");
            Add("OST_Planting", "Planting", "Vegetação", "Plantio");
            Add("OST_Entourage", "Entourage");
            Add("OST_Parking", "Parking", "Estacionamento");
            Add("OST_Site", "Site", "Terreno", "Implantação");
            Add("OST_Topography", "Topography", "Topografia");
            Add("OST_CurtainWallPanels", "Curtain Panels", "Curtain Wall Panels", "Painéis de parede cortina", "Painéis de cortina");
            Add("OST_CurtainWallMullions", "Curtain Wall Mullions", "Mullions", "Montantes de parede cortina", "Montantes");
            Add("OST_Grids", "Grids", "Grid", "Eixos", "Eixo");
            Add("OST_Levels", "Levels", "Level", "Níveis", "Nível");
            Add("OST_Views", "Views", "View", "Vistas", "Vista");
            Add("OST_Sheets", "Sheets", "Sheet", "Folhas", "Folha", "Pranchas", "Prancha");
            Add("OST_TitleBlocks", "Title Blocks", "Title Block", "Carimbos", "Carimbo", "Legendas de folha");
            Add("OST_Dimensions", "Dimensions", "Cotas", "Cota");
            Add("OST_TextNotes", "Text Notes", "Notas de texto", "Textos");
            Add("OST_RoomTags", "Room Tags", "Etiquetas de ambiente", "Identificadores de ambiente");
            Add("OST_DoorTags", "Door Tags", "Etiquetas de porta", "Identificadores de porta");
            Add("OST_WindowTags", "Window Tags", "Etiquetas de janela", "Identificadores de janela");
            Add("OST_PipeCurves", "Pipes", "Pipe", "Tubos", "Tubulações");
            Add("OST_DuctCurves", "Ducts", "Duct", "Dutos", "Duto");
            Add("OST_CableTray", "Cable Trays", "Cable Tray", "Leitos de cabos", "Eletrocalhas");
            Add("OST_Conduit", "Conduits", "Conduit", "Eletrodutos", "Eletroduto");
            Add("OST_Lines", "Lines", "Linhas");
            Add("OST_Materials", "Materials", "Materiais");
            Add("OST_RvtLinks", "Revit Links", "Links", "Vínculos RVT", "Vínculos");
            Add("OST_DetailComponents", "Detail Items", "Itens de detalhe", "Componentes de detalhe");
            Add("OST_Schedules", "Schedules", "Tabelas", "Tabelas de quantitativos");
            return map;
        }
    }
}
