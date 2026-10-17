using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Millwork.Support
{
    /// <summary>One rectangular panel of a module, in the module's local frame (mm).</summary>
    public sealed class MillworkPart
    {
        /// <summary>Stable role code, e.g. side_left, shelf_1_2, door_1, drawer_2_side_left.</summary>
        [JsonProperty("role")] public string Role { get; set; }

        /// <summary>Generic piece name used to group the cutting list (pt-BR), e.g. "Lateral".</summary>
        [JsonProperty("piece")] public string Piece { get; set; }

        /// <summary>Specific label (pt-BR), e.g. "Lateral esquerda".</summary>
        [JsonProperty("label")] public string Label { get; set; }

        /// <summary>carcass, front, back or drawer.</summary>
        [JsonProperty("materialKey")] public string MaterialKey { get; set; }

        /// <summary>x0, y0, z0, x1, y1, z1 in mm. X = width (left to right), Y = depth (front to back), Z = up.</summary>
        [JsonProperty("box")] public double[] Box { get; set; }

        [JsonProperty("note")] public string Note { get; set; }

        [JsonIgnore] public double SizeX => Box[3] - Box[0];
        [JsonIgnore] public double SizeY => Box[4] - Box[1];
        [JsonIgnore] public double SizeZ => Box[5] - Box[2];

        /// <summary>Panel dimensions: length (largest), width, thickness (smallest), mm.</summary>
        public double[] PanelDims()
        {
            var d = new[] { SizeX, SizeY, SizeZ }.OrderByDescending(v => v).ToArray();
            return new[] { Math.Round(d[0], 1), Math.Round(d[1], 1), Math.Round(d[2], 1) };
        }
    }

    /// <summary>Computes the parts of a module from its design. Pure (no Revit): unit-testable.</summary>
    public static class MillworkLayout
    {
        public const double DefaultWallElevation = 1500;

        /// <summary>Fills the defaults of a design (create) or of an existing design with changes (configure).</summary>
        public static ResolvedDesign Resolve(MillworkDesign d, ResolvedDesign baseline = null)
        {
            var b = baseline;
            string kind = Lower(d.Kind) ?? b?.Kind;
            if (kind == null) throw Invalid("kind is required (base, wall or tall).");
            bool kindChanged = b != null && kind != b.Kind;
            var r = new ResolvedDesign
            {
                Kind = kind,
                Width = d.Width ?? b?.Width ?? throw Invalid("width is required."),
                Height = d.Height ?? b?.Height ?? throw Invalid("height is required."),
                Depth = d.Depth ?? b?.Depth ?? throw Invalid("depth is required."),
                Thickness = d.Thickness ?? b?.Thickness ?? 18,
                BackPanel = d.BackPanel ?? b?.BackPanel ?? true,
                BackThickness = d.BackThickness ?? b?.BackThickness ?? 6,
                TopPanel = Lower(d.TopPanel) ?? b?.TopPanel ?? "auto",
                RailWidth = d.RailWidth ?? b?.RailWidth ?? 100,
                Shelves = d.Shelves ?? b?.Shelves ?? 0,
                Dividers = d.Dividers ?? b?.Dividers ?? 0,
                Doors = d.Doors ?? b?.Doors ?? 0,
                HingeSide = Lower(d.HingeSide) ?? b?.HingeSide ?? "left",
                DoorOpening = Lower(d.DoorOpening) ?? b?.DoorOpening ?? "swing",
                FrontStyle = Lower(d.FrontStyle) ?? b?.FrontStyle ?? "overlay",
                Gap = d.Gap ?? b?.Gap ?? 3,
                Drawers = d.Drawers ?? b?.Drawers ?? 0,
                DrawerPosition = Lower(d.DrawerPosition) ?? b?.DrawerPosition ?? "top",
                DrawerBoxThickness = d.DrawerBoxThickness ?? b?.DrawerBoxThickness ?? 15,
                ToeKickRecess = d.ToeKickRecess ?? b?.ToeKickRecess ?? 50,
                CarcassMaterial = Text(d.CarcassMaterial, b?.CarcassMaterial),
                FrontMaterial = Text(d.FrontMaterial, b?.FrontMaterial),
                BackMaterial = Text(d.BackMaterial, b?.BackMaterial),
                Mark = Text(d.Mark, b?.Mark),
                Name = Text(d.Name, b?.Name),
                Level = Text(d.Level, b?.Level),
                Rotation = d.Rotation ?? b?.Rotation ?? 0
            };
            // Thickness-dependent defaults follow the new thickness unless they were set explicitly before.
            r.FrontThickness = d.FrontThickness ?? (b != null && b.FrontThickness != b.Thickness ? b.FrontThickness : r.Thickness);
            // Kind-dependent defaults follow a kind change unless given.
            r.ToeKickHeight = d.ToeKickHeight ?? (b != null && !kindChanged ? b.ToeKickHeight : (kind == "wall" ? 0 : 100));
            r.Elevation = d.Elevation ?? (b != null && !kindChanged ? b.Elevation : (kind == "wall" ? DefaultWallElevation : 0));
            if (d.Point != null) { r.X = d.Point.X; r.Y = d.Point.Y; }
            else if (b != null) { r.X = b.X; r.Y = b.Y; }
            else throw Invalid("point is required.");
            if (r.Level == null) throw Invalid("level is required.");

            if (d.DrawerHeights != null) r.DrawerHeights = d.DrawerHeights.Count == 0 ? null : d.DrawerHeights.ToList();
            else if (b?.DrawerHeights != null && (d.Drawers == null || d.Drawers == b.Drawers)) r.DrawerHeights = b.DrawerHeights.ToList();
            if (r.TopPanel == "auto") r.TopPanel = kind == "base" ? "rails" : "full";
            if (d.TopPanel == null && kindChanged && b != null) r.TopPanel = kind == "base" ? "rails" : "full";
            return r;
        }

        /// <summary>Checks the resolved design and returns its parts in the local frame.</summary>
        public static List<MillworkPart> Build(ResolvedDesign r, List<string> notes)
        {
            double W = r.Width, H = r.Height, D = r.Depth, t = r.Thickness, ft = r.FrontThickness, g = r.Gap;
            double bt = r.BackPanel ? r.BackThickness : 0;
            bool inset = r.FrontStyle == "inset";

            if (r.Kind == "wall" && r.ToeKickHeight > 0)
                throw Invalid("Wall (aéreo) modules have no toe kick: set toeKickHeight to 0 or omit it.");
            if (W < 2 * t + 50) throw Invalid($"width {F(W)} mm is too small for two {F(t)} mm sides.");
            double tk = r.ToeKickHeight;
            double z0 = tk;
            double hc = H - tk;
            if (hc < 2 * t + 50) throw Invalid($"height {F(H)} mm leaves only {F(hc)} mm of carcass above the toe kick.");
            if (D < bt + t + 50) throw Invalid($"depth {F(D)} mm is too small.");
            if (r.TopPanel == "rails" && 2 * r.RailWidth > D - bt) throw Invalid("railWidth is too large for the depth.");
            if (r.DrawerHeights != null && r.DrawerHeights.Count != r.Drawers)
                throw Invalid($"drawerHeights has {r.DrawerHeights.Count} value(s) but drawers is {r.Drawers}.");
            if (inset && r.Gap <= 0) notes.Add("Inset fronts with gap 0 will touch the carcass.");

            var parts = new List<MillworkPart>();
            void Add(string role, string piece, string label, string mat, double x0, double y0, double z0_, double x1, double y1, double z1, string note = null)
            {
                if (x1 - x0 < 0.5 || y1 - y0 < 0.5 || z1 - z0_ < 0.5)
                    throw Invalid($"The {label} would be {F(x1 - x0)} x {F(y1 - y0)} x {F(z1 - z0_)} mm: the module is too small for this configuration.");
                parts.Add(new MillworkPart
                {
                    Role = role, Piece = piece, Label = label, MaterialKey = mat, Note = note,
                    Box = new[] { R(x0), R(y0), R(z0_), R(x1), R(y1), R(z1) }
                });
            }

            // ---- carcass (caixa) ---------------------------------------------------------------------------------
            Add("side_left", "Lateral", "Lateral esquerda", "carcass", 0, 0, z0, t, D, H);
            Add("side_right", "Lateral", "Lateral direita", "carcass", W - t, 0, z0, W, D, H);
            Add("bottom", "Base", "Base", "carcass", t, 0, z0, W - t, D - bt, z0 + t);
            double zInTop = H;
            switch (r.TopPanel)
            {
                case "full":
                    Add("top", "Tampo", "Tampo", "carcass", t, 0, H - t, W - t, D - bt, H);
                    zInTop = H - t;
                    break;
                case "rails":
                    Add("rail_front", "Travessa", "Travessa frontal", "carcass", t, 0, H - t, W - t, r.RailWidth, H);
                    Add("rail_back", "Travessa", "Travessa traseira", "carcass", t, D - bt - r.RailWidth, H - t, W - t, D - bt, H);
                    zInTop = H - t;
                    break;
            }
            if (r.BackPanel)
                Add("back", "Fundo", "Fundo", "back", t, D - bt, z0, W - t, D, H);
            if (tk > 0)
            {
                if (r.ToeKickRecess + t > D - bt) throw Invalid("toeKickRecess is too deep for the module depth.");
                Add("toe_kick_front", "Rodapé", "Rodapé frontal", "carcass", 0, r.ToeKickRecess, 0, W, r.ToeKickRecess + t, tk);
                Add("toe_kick_back", "Rodapé", "Rodapé traseiro", "carcass", 0, D - bt - t, 0, W, D - bt, tk);
            }

            // ---- interior and fronts -------------------------------------------------------------------------------
            double xi0 = t, xi1 = W - t;                // interior width
            double yf = inset ? ft : 0, yb = D - bt;    // interior depth
            double zi0 = z0 + t, zi1 = zInTop;          // interior height
            if (yb - yf < 50) throw Invalid("The interior depth is under 50 mm (depth minus back and inset fronts).");

            // Front zone: overlay covers the carcass outline, inset sits in the opening.
            double fx0 = inset ? xi0 + g : g / 2, fx1 = inset ? xi1 - g : W - g / 2;
            double fz0 = inset ? zi0 + g : z0 + g / 2, fz1 = inset ? zi1 - g : H - g / 2;
            double fy0 = inset ? 0 : -ft, fy1 = inset ? ft : 0;
            double frontH = fz1 - fz0;

            int nd = r.Doors, nw = r.Drawers;
            var drawerHeights = new List<double>();
            double openZ0 = zi0, openZ1 = zi1;   // interior zone for shelves/dividers (door or open zone)
            double doorZ0 = fz0, doorZ1 = fz1;   // door fronts zone
            bool hasOpenZone = true;

            if (nw > 0)
            {
                double sep = inset ? 2 * g + t : g; // between the drawer zone and the door zone
                if (nd == 0)
                {
                    double avail = frontH - (nw - 1) * g;
                    if (r.DrawerHeights != null)
                    {
                        double sum = r.DrawerHeights.Sum();
                        if (sum > avail + 0.5)
                            throw Invalid($"drawerHeights add up to {F(sum)} mm but only {F(avail)} mm are available for drawer fronts.");
                        drawerHeights.AddRange(r.DrawerHeights);
                        if (avail - sum > 0.5)
                        {
                            drawerHeights[drawerHeights.Count - 1] += avail - sum;
                            notes.Add($"The bottom drawer front was enlarged by {F(avail - sum)} mm to fill the module.");
                        }
                    }
                    else drawerHeights.AddRange(Enumerable.Repeat(avail / nw, nw));
                    hasOpenZone = false;
                }
                else
                {
                    drawerHeights.AddRange(r.DrawerHeights ?? Enumerable.Repeat(180.0, nw).ToList());
                    double drawerZone = drawerHeights.Sum() + (nw - 1) * g;
                    double doorH = frontH - drawerZone - sep;
                    if (doorH < 150)
                        throw Invalid($"Only {F(doorH)} mm would be left for the doors under/over {nw} drawer(s): reduce drawerHeights or the drawer count.");
                    // Horizontal panel between the zones.
                    double hz0, hz1;
                    if (r.DrawerPosition == "bottom")
                    {
                        doorZ0 = fz1 - doorH;
                        if (inset) { hz0 = doorZ0 - g - t; hz1 = doorZ0 - g; }
                        else { double c = doorZ0 - g / 2; hz0 = c - t / 2; hz1 = c + t / 2; }
                        openZ0 = hz1;
                    }
                    else
                    {
                        doorZ1 = fz0 + doorH;
                        if (inset) { hz0 = doorZ1 + g; hz1 = doorZ1 + g + t; }
                        else { double c = doorZ1 + g / 2; hz0 = c - t / 2; hz1 = c + t / 2; }
                        openZ1 = hz0;
                    }
                    Add("shelf_fixed", "Divisória horizontal", "Divisória horizontal (gavetas)", "carcass", xi0, yf, hz0, xi1, yb, hz1);
                }

                // Drawer fronts and boxes, top drawer first.
                double zTopDrawers = (nd > 0 && r.DrawerPosition == "bottom") ? doorZ0 - sep : fz1;
                double bx0 = xi0 + 13, bx1 = xi1 - 13; // telescopic slides: ~13 mm each side
                double boxDepth = Math.Floor((yb - yf - 20) / 50) * 50;
                if (boxDepth < 200) throw Invalid($"The interior depth ({F(yb - yf)} mm) is too small for drawers (box under 200 mm).");
                if (boxDepth > 600) boxDepth = 600;
                double dbt = r.DrawerBoxThickness, bot = Math.Max(3, r.BackThickness);
                if (bx1 - bx0 < 2 * dbt + 50) throw Invalid("The module is too narrow for drawers.");
                double z = zTopDrawers;
                for (int i = 0; i < nw; i++)
                {
                    int n = i + 1;
                    double h = drawerHeights[i];
                    if (h < 60) throw Invalid($"Drawer {n} front is {F(h)} mm high; the minimum is 60 mm.");
                    double dz1 = z, dz0 = z - h;
                    Add($"drawer_front_{n}", "Frente de gaveta", $"Frente gaveta {n}", "front", fx0, fy0, dz0, fx1, fy1, dz1);
                    // Box: bottom 10 mm above the front's bottom, height front - 40 (min 60), behind the front.
                    double bh = Math.Max(60, h - 40);
                    double bz0 = dz0 + 10, bz1 = bz0 + bh;
                    double by0 = yf, by1 = yf + boxDepth;
                    if (bz0 < zi0) { bz1 += zi0 - bz0; bz0 = zi0; }
                    Add($"drawer_{n}_side_left", "Gaveta - lateral", $"Gaveta {n} - lateral esquerda", "carcass", bx0, by0, bz0, bx0 + dbt, by1, bz1);
                    Add($"drawer_{n}_side_right", "Gaveta - lateral", $"Gaveta {n} - lateral direita", "carcass", bx1 - dbt, by0, bz0, bx1, by1, bz1);
                    Add($"drawer_{n}_front", "Gaveta - frente/traseira", $"Gaveta {n} - contra-frente", "carcass", bx0 + dbt, by0, bz0 + bot, bx1 - dbt, by0 + dbt, bz1);
                    Add($"drawer_{n}_back", "Gaveta - frente/traseira", $"Gaveta {n} - traseira", "carcass", bx0 + dbt, by1 - dbt, bz0 + bot, bx1 - dbt, by1, bz1);
                    Add($"drawer_{n}_bottom", "Gaveta - fundo", $"Gaveta {n} - fundo", "back", bx0 + dbt, by0 + dbt, bz0, bx1 - dbt, by1 - dbt, bz0 + bot);
                    z = dz0 - g;
                }
            }
            r.DrawerHeightsUsed = drawerHeights.Select(R).ToList();

            if (!hasOpenZone && (r.Shelves > 0 || r.Dividers > 0))
                throw Invalid("The drawers fill the whole module: there is no zone left for shelves or dividers (add doors, or set shelves/dividers to 0).");

            // ---- dividers and shelves in the door/open zone --------------------------------------------------------
            int bays = r.Dividers + 1;
            double wi = xi1 - xi0;
            double bayW = (wi - r.Dividers * t) / bays;
            if (bayW < 50) throw Invalid($"{r.Dividers} divider(s) leave bays of only {F(bayW)} mm.");
            var bayX = new List<(double x0, double x1)>();
            for (int k = 0; k < bays; k++)
            {
                double bx = xi0 + k * (bayW + t);
                bayX.Add((bx, bx + bayW));
                if (k < bays - 1)
                    Add($"divider_{k + 1}", "Divisória", $"Divisória {k + 1}", "carcass", bx + bayW, yf, openZ0, bx + bayW + t, yb, openZ1);
            }
            if (hasOpenZone && r.Shelves > 0)
            {
                double free = openZ1 - openZ0 - r.Shelves * t;
                double step = free / (r.Shelves + 1);
                if (step < 80) throw Invalid($"{r.Shelves} shelves leave only {F(step)} mm between them.");
                for (int k = 0; k < bays; k++)
                    for (int s = 0; s < r.Shelves; s++)
                    {
                        double sz = openZ0 + (s + 1) * step + s * t;
                        string label = bays > 1 ? $"Prateleira {s + 1} (vão {k + 1})" : $"Prateleira {s + 1}";
                        Add(bays > 1 ? $"shelf_{k + 1}_{s + 1}" : $"shelf_{s + 1}", "Prateleira", label, "carcass",
                            bayX[k].x0 + 1, yf + 10, sz, bayX[k].x1 - 1, yb, sz + t);
                    }
            }

            // ---- doors -------------------------------------------------------------------------------------------
            if (nd > 0)
            {
                var columns = new List<(double x0, double x1)>();
                if (inset && r.Dividers > 0)
                {
                    if (nd % bays != 0)
                        throw Invalid($"Inset doors must split evenly over the {bays} bays: use a multiple of {bays} doors (or overlay fronts).");
                    int per = nd / bays;
                    foreach (var bay in bayX)
                        columns.AddRange(Split(bay.x0 + g, bay.x1 - g, per, g));
                }
                else columns.AddRange(Split(fx0, fx1, nd, g));
                if (columns.Any(c => c.x1 - c.x0 < 100))
                    throw Invalid($"{nd} doors would be under 100 mm wide.");
                for (int i = 0; i < columns.Count; i++)
                {
                    string hinge = HingeOf(r, i, columns.Count);
                    string open = r.DoorOpening == "swing" ? "giro" : r.DoorOpening == "lift_up" ? "basculante" : "tombar";
                    Add($"door_{i + 1}", "Porta", $"Porta {i + 1} ({open}, dobradiça {HingePt(hinge)})", "front",
                        columns[i].x0, fy0, doorZ0, columns[i].x1, fy1, doorZ1, "hinge:" + hinge);
                }
            }
            return parts;
        }

        public static string HingeOf(ResolvedDesign r, int index, int count)
        {
            if (r.DoorOpening == "lift_up") return "top";
            if (r.DoorOpening == "flap_down") return "bottom";
            if (count == 1) return r.HingeSide;
            bool pairsComplete = count % 2 == 0 || index < count - 1;
            if (!pairsComplete) return r.HingeSide;
            return index % 2 == 0 ? "left" : "right";
        }

        private static string HingePt(string h) =>
            h == "left" ? "esquerda" : h == "right" ? "direita" : h == "top" ? "superior" : "inferior";

        private static IEnumerable<(double x0, double x1)> Split(double x0, double x1, int n, double gap)
        {
            double w = (x1 - x0 - (n - 1) * gap) / n;
            for (int i = 0; i < n; i++)
            {
                double a = x0 + i * (w + gap);
                yield return (a, a + w);
            }
        }

        private static string Lower(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
        private static string Text(string given, string previous) =>
            given == null ? previous : (string.IsNullOrWhiteSpace(given) || given.Trim() == "-" ? null : given.Trim());
        private static double R(double v) => Math.Round(v, 2);
        internal static string F(double mm) => mm.ToString("0.#", CultureInfo.InvariantCulture);
        private static ToolException Invalid(string message) => new ToolException("INVALID_ARGUMENTS", message);
    }
}
