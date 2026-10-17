using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Architecture.Support;

namespace RevitMCPCommandSet.Tools.Millwork.Support
{
    /// <summary>
    /// Everything that defines a millwork module (marcenaria). Every property is optional so the same class serves
    /// create_millwork_module (where kind/width/height/depth/level/point are checked as required) and the
    /// <c>changes</c> of configure_millwork_module. <see cref="MillworkLayout.Resolve"/> fills the defaults.
    /// </summary>
    public class MillworkDesign
    {
        [JsonProperty("kind")]
        [EnumValues("base", "wall", "tall")]
        [Description("base = inferior (on the floor, toe kick), wall = aéreo (hung, no toe kick), tall = torre/paneleiro (floor to high). Required to create.")]
        public string Kind { get; set; }

        [JsonProperty("width")]
        [Unit("mm")]
        [Range(100, 6000)]
        [Description("Overall width (largura) in mm, along the module's X. Required to create.")]
        public double? Width { get; set; }

        [JsonProperty("height")]
        [Unit("mm")]
        [Range(100, 4000)]
        [Description("Overall height (altura) in mm including the toe kick. Required to create.")]
        public double? Height { get; set; }

        [JsonProperty("depth")]
        [Unit("mm")]
        [Range(100, 1500)]
        [Description("Overall depth (profundidade) of the carcass in mm, without overlay fronts. Required to create.")]
        public double? Depth { get; set; }

        [JsonProperty("thickness")]
        [Unit("mm")]
        [Range(6, 50)]
        [Description("Carcass panel thickness (MDF/MDP) in mm. Default 18.")]
        public double? Thickness { get; set; }

        [JsonProperty("frontThickness")]
        [Unit("mm")]
        [Range(6, 50)]
        [Description("Door and drawer front thickness in mm. Default = thickness.")]
        public double? FrontThickness { get; set; }

        [JsonProperty("backPanel")]
        [Description("Back panel (fundo) behind the bottom and top, between the sides. Default true.")]
        public bool? BackPanel { get; set; }

        [JsonProperty("backThickness")]
        [Unit("mm")]
        [Range(3, 30)]
        [Description("Back panel thickness in mm. Default 6.")]
        public double? BackThickness { get; set; }

        [JsonProperty("topPanel")]
        [EnumValues("auto", "full", "rails", "none")]
        [Description("Top of the carcass: full panel, rails (two travessas, front and back, for base modules under a stone top) or none. Default auto = rails for base, full otherwise.")]
        public string TopPanel { get; set; }

        [JsonProperty("railWidth")]
        [Unit("mm")]
        [Range(40, 300)]
        [Description("Width of each top rail (travessa) in mm when topPanel is rails. Default 100.")]
        public double? RailWidth { get; set; }

        [JsonProperty("shelves")]
        [Range(0, 20)]
        [Description("Adjustable shelves (prateleiras) per bay, evenly spaced in the door/open zone. Default 0.")]
        public int? Shelves { get; set; }

        [JsonProperty("dividers")]
        [Range(0, 10)]
        [Description("Internal vertical dividers (divisórias) splitting the door/open zone into equal bays. Default 0.")]
        public int? Dividers { get; set; }

        [JsonProperty("doors")]
        [Range(0, 12)]
        [Description("Number of doors side by side in the door zone. Default 0 (open module or drawers only).")]
        public int? Doors { get; set; }

        [JsonProperty("hingeSide")]
        [EnumValues("left", "right")]
        [Description("Hinge side of a single door, and of the last door when the count is odd (pairs open left/right). Default left.")]
        public string HingeSide { get; set; }

        [JsonProperty("doorOpening")]
        [EnumValues("swing", "lift_up", "flap_down")]
        [Description("Door opening: swing (giro), lift_up (basculante, hinges at the top) or flap_down (tombar, hinges at the bottom). Recorded in the parts list. Default swing.")]
        public string DoorOpening { get; set; }

        [JsonProperty("frontStyle")]
        [EnumValues("overlay", "inset")]
        [Description("overlay (sobreposta: fronts in front of the carcass) or inset (embutida: fronts inside the opening, internals set back). Default overlay.")]
        public string FrontStyle { get; set; }

        [JsonProperty("gap")]
        [Unit("mm")]
        [Range(0, 20)]
        [Description("Gap (folga) between fronts and around them in mm. Default 3.")]
        public double? Gap { get; set; }

        [JsonProperty("drawers")]
        [Range(0, 12)]
        [Description("Number of drawers stacked in one column over the full width. Default 0.")]
        public int? Drawers { get; set; }

        [JsonProperty("drawerHeights")]
        [MaxLength(12)]
        [Unit("mm")]
        [Description("Drawer front heights in mm, from the top drawer down (one per drawer). Default: 180 each with doors, equal split without doors.")]
        public List<double> DrawerHeights { get; set; }

        [JsonProperty("drawerPosition")]
        [EnumValues("top", "bottom")]
        [Description("With doors and drawers: drawers above (top, default) or below (bottom) the doors.")]
        public string DrawerPosition { get; set; }

        [JsonProperty("drawerBoxThickness")]
        [Unit("mm")]
        [Range(6, 30)]
        [Description("Drawer box side/front/back thickness in mm. Default 15.")]
        public double? DrawerBoxThickness { get; set; }

        [JsonProperty("toeKickHeight")]
        [Unit("mm")]
        [Range(0, 300)]
        [Description("Toe kick (rodapé) height in mm for base and tall modules. Default 100; wall modules have none.")]
        public double? ToeKickHeight { get; set; }

        [JsonProperty("toeKickRecess")]
        [Unit("mm")]
        [Range(0, 200)]
        [Description("How far the front toe-kick board is set back from the carcass front, mm. Default 50.")]
        public double? ToeKickRecess { get; set; }

        [JsonProperty("carcassMaterial")]
        [Description("Material (name or id) of the carcass: sides, bottom, top, rails, shelves, dividers, toe kick, drawer boxes. Default: category material.")]
        public string CarcassMaterial { get; set; }

        [JsonProperty("frontMaterial")]
        [Description("Material (name or id) of doors and drawer fronts. Default: carcassMaterial.")]
        public string FrontMaterial { get; set; }

        [JsonProperty("backMaterial")]
        [Description("Material (name or id) of the back panel and drawer bottoms. Default: carcassMaterial.")]
        public string BackMaterial { get; set; }

        [JsonProperty("mark")]
        [MaxLength(60)]
        [Description("Module identifier (e.g. \"M01\"): prefixes every part's Comments and names the group.")]
        public string Mark { get; set; }

        [JsonProperty("name")]
        [MaxLength(120)]
        [Description("Free description (e.g. \"Cozinha - pia\"); stored with the module and reported in the parts list.")]
        public string Name { get; set; }

        [JsonProperty("level")]
        [Description("Level by name or id. Required to create.")]
        public string Level { get; set; }

        [JsonProperty("point")]
        [Description("Insertion point in plan, mm: the FRONT-LEFT corner of the carcass. The module extends +X (width) and +Y (depth, towards the back) before rotation, so at rotation 0 it faces -Y. Required to create.")]
        public PointMm Point { get; set; }

        [JsonProperty("elevation")]
        [Unit("mm")]
        [Range(-5000, 10000)]
        [Description("Bottom of the module above the level, mm. Default 0 (base/tall) or 1500 (wall).")]
        public double? Elevation { get; set; }

        [JsonProperty("rotation")]
        [Unit("deg")]
        [Description("Counter-clockwise rotation in plan about the insertion point, degrees. Default 0.")]
        public double? Rotation { get; set; }
    }

    /// <summary>The design with every default filled in; what is stored on each part.</summary>
    public sealed class ResolvedDesign
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
        [JsonProperty("depth")] public double Depth { get; set; }
        [JsonProperty("thickness")] public double Thickness { get; set; }
        [JsonProperty("frontThickness")] public double FrontThickness { get; set; }
        [JsonProperty("backPanel")] public bool BackPanel { get; set; }
        [JsonProperty("backThickness")] public double BackThickness { get; set; }
        [JsonProperty("topPanel")] public string TopPanel { get; set; }
        [JsonProperty("railWidth")] public double RailWidth { get; set; }
        [JsonProperty("shelves")] public int Shelves { get; set; }
        [JsonProperty("dividers")] public int Dividers { get; set; }
        [JsonProperty("doors")] public int Doors { get; set; }
        [JsonProperty("hingeSide")] public string HingeSide { get; set; }
        [JsonProperty("doorOpening")] public string DoorOpening { get; set; }
        [JsonProperty("frontStyle")] public string FrontStyle { get; set; }
        [JsonProperty("gap")] public double Gap { get; set; }
        [JsonProperty("drawers")] public int Drawers { get; set; }
        [JsonProperty("drawerHeights")] public List<double> DrawerHeights { get; set; }
        [JsonProperty("drawerPosition")] public string DrawerPosition { get; set; }
        [JsonProperty("drawerBoxThickness")] public double DrawerBoxThickness { get; set; }
        [JsonProperty("toeKickHeight")] public double ToeKickHeight { get; set; }
        [JsonProperty("toeKickRecess")] public double ToeKickRecess { get; set; }
        [JsonProperty("carcassMaterial")] public string CarcassMaterial { get; set; }
        [JsonProperty("frontMaterial")] public string FrontMaterial { get; set; }
        [JsonProperty("backMaterial")] public string BackMaterial { get; set; }
        [JsonProperty("mark")] public string Mark { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("level")] public string Level { get; set; }
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("elevation")] public double Elevation { get; set; }
        [JsonProperty("rotation")] public double Rotation { get; set; }

        /// <summary>Drawer front heights actually used (filled in by the layout).</summary>
        [JsonProperty("drawerHeightsUsed")] public List<double> DrawerHeightsUsed { get; set; }
    }
}
