using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Tools.QA.Support
{
    /// <summary>Suggested fixes for the most common warnings, keyed by FailureDefinitionId (language-neutral).</summary>
    public static class WarningFixes
    {
        private static Dictionary<Guid, string> _map;

        public static string For(Guid definitionId)
        {
            if (_map == null) _map = Build();
            return _map.TryGetValue(definitionId, out var s) ? s : null;
        }

        private static Dictionary<Guid, string> Build()
        {
            var m = new Dictionary<Guid, string>();

            void Add(Func<FailureDefinitionId> id, string fix)
            {
                try
                {
                    var g = id().Guid;
                    if (!m.ContainsKey(g)) m[g] = fix;
                }
                catch
                {
                    // definition not present in this Revit version
                }
            }

            Add(() => BuiltInFailures.OverlapFailures.WallsOverlap,
                "Overlapping walls: delete or shorten one of them (or use a wall join); they double-count quantities. / Paredes sobrepostas: apague ou encurte uma delas.");
            Add(() => BuiltInFailures.OverlapFailures.DuplicateInstances,
                "Identical instances in the same place: delete the duplicates (keep one). / Instâncias idênticas no mesmo lugar: apague as duplicadas.");
            Add(() => BuiltInFailures.RoomFailures.RoomNotEnclosed,
                "Room not enclosed: close its boundary with walls or room separation lines, or delete the room if unplaced/unneeded. / Ambiente não delimitado: feche o contorno ou apague.");
            Add(() => BuiltInFailures.RoomFailures.RoomsInSameRegionRooms,
                "Several rooms in the same enclosed region: delete the redundant room or split the region with a room separation line. / Ambientes redundantes na mesma região.");
            Add(() => BuiltInFailures.OverlapFailures.CurvesOverlap,
                "Overlapping lines: delete one of the overlapping model/detail lines. / Linhas sobrepostas: apague uma delas.");
            Add(() => BuiltInFailures.OverlapFailures.RoomSeparationLinesOverlap,
                "Room separation lines overlap: delete the duplicate separation line. / Linhas de separação de ambientes sobrepostas.");
            Add(() => BuiltInFailures.OverlapFailures.WallRoomSeparationOverlap,
                "Room separation line on top of a wall: delete the separation line (the wall already bounds the room). / Linha de separação sobre parede: apague a linha.");
            Add(() => BuiltInFailures.InaccurateFailures.InaccurateWall,
                "Wall is slightly off axis: realign it (Align, or redraw with snaps). / Parede ligeiramente fora de eixo: realinhe.");
            Add(() => BuiltInFailures.JoinElementsFailures.JoiningDisjointWarn,
                "Elements are joined but don't touch: unjoin them (Modify > Unjoin Geometry). / Elementos unidos que não se tocam: desfaça a união.");
            return m;
        }
    }
}
