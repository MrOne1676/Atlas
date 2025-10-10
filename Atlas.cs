namespace Atlas
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class Atlas : PCore<AtlasSettings>
    {
        private const uint CitadelLineColor = 0xFF0000FF;
        private const uint TowerLineColor = 0xFFC6C10D;
        private const uint SearchLineColor = 0xFFFFFFFF;
        private const uint CompletedNodeDotColor = 0xFF00FF00;
        private const uint DotOutlineColor = 0xFF000000;

        private const int ChannelGrid = 0;
        private const int ChannelLines = 1;
        private const int ChannelDots = 2;
        private const int ChannelLabels = 3;

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string NewGroupName = string.Empty;

        private static readonly Dictionary<string, ContentInfo> MapTags = [];
        private static readonly Dictionary<string, ContentInfo> MapPlain = [];
        private static readonly Dictionary<byte, BiomeInfo> Biomes = [];

        public static IntPtr Handle { get; set; }
        private static int _handlePid;

        public override void OnDisable()
        {
            CloseAndResetHandle();
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var serializerSettings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<AtlasSettings>(content, serializerSettings);
            }

            LoadBiomeMap();
            LoadContentMap();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var settingsData = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingPathname, settingsData);
        }

        public override void DrawSettings()
        {
            #region SettingsUI
            ImGui.Checkbox("Use Controller Mode", ref Settings.ControllerMode);

            ImGui.SeparatorText("Search Maps");
            ImGui.InputTextWithHint("Search Map", "You can search multiple maps at once using a comma separator ','", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                Settings.SearchQuery = string.Empty;
            if (ImGui.TreeNode("Draw Lines Settings"))
            {
                ImGui.Checkbox("Route Lines Through Nodes (Shortest Path)", ref Settings.RouteLinesThroughNodes);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                ImGui.SliderFloat("Path Thickness", ref Settings.PathLineThickness, 1.0f, 8.0f);
                ImGui.Checkbox("Draw Lines to Search in range", ref Settings.DrawLinesSearchQuery);
                ImGui.SameLine();
                ImGui.SliderFloat("##DrawSearchInRange", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
                ImGui.Checkbox("Draw Lines to Citadels", ref Settings.DrawLinesToCitadel);
                ImGui.Checkbox("Draw Lines to Towers in range", ref Settings.DrawLinesToTowers);
                ImGui.SameLine();
                ImGui.SliderFloat("##DrawTowersInRange", ref Settings.DrawTowersInRange, 1.0f, 10.0f);
                ImGui.TreePop();
            }

            ImGui.SeparatorText("Atlas Settings");
            ImGui.Checkbox("Hide Completed Maps", ref Settings.HideCompletedMaps);
            ImGui.Checkbox("Hide Not Accessible Maps", ref Settings.HideNotAccessibleMaps);
            ImGui.Checkbox("Show Atlas Grid", ref Settings.DrawGrid);
            if (Settings.DrawGrid)
                if (ImGui.TreeNode("Atlas Grid Settings"))
                {
                    ColorSwatch("Grid Color", ref Settings.GridLineColor);
                    ImGui.SameLine();
                    ImGui.Text("Grid Color");
                    ImGui.Checkbox("Hide connections to completed maps", ref Settings.GridSkipCompleted);
                    ImGui.TreePop();
                }

            ImGui.Checkbox("Show Map Content Badges", ref Settings.ShowMapBadges);
            if (Settings.ShowMapBadges)
                if (ImGui.TreeNode("Map Content Badge Settings"))
                {
                    if (ImGui.BeginTable("split", 3))
                    {
                        foreach (var kv in MapTags.Concat(MapPlain))
                        {
                            ImGui.TableNextColumn();
                            var key = kv.Key;
                            var info = kv.Value;

                            if (!Settings.ContentOverrides.TryGetValue(key, out var ov))
                            {
                                ov = new ContentOverride();
                                Settings.ContentOverrides[key] = ov;
                            }

                            ImGui.Text(info.Label);

                            bool show = ov.Show ?? info.Show;
                            if (ImGui.Checkbox($"##Show##{key}", ref show))
                            {
                                ov.Show = show;
                                ApplyBiomeOverrides();
                            }

                            var bg = ov.BackgroundColor ?? info.BgColor;
                            ImGui.SameLine();
                            ColorSwatch($"Background Color##{key}", ref bg);
                            if (!ColorsEqual(bg, ov.BackgroundColor ?? info.BgColor))
                            {
                                ov.BackgroundColor = bg;
                                ApplyContentOverrides();
                            }

                            string abbrev = ov.Abbrev ?? info.Abbrev;
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(100);
                            if (ImGui.InputText($"##Abbrev##{key}", ref abbrev, 16))
                            {
                                ov.Abbrev = abbrev;
                                ApplyContentOverrides();
                            }

                            var fg = ov.FontColor ?? info.FtColor;
                            ImGui.SameLine();
                            ColorSwatch($"Font Color##{key}", ref fg);
                            if (!ColorsEqual(fg, ov.FontColor ?? info.FtColor))
                            {
                                ov.FontColor = fg;
                                ApplyContentOverrides();
                            }
                        }
                        ImGui.EndTable();
                    }
                    ImGui.TreePop();
                }

            ImGui.Checkbox("Show Biome Border", ref Settings.ShowBiomeBorder);
            if (Settings.ShowBiomeBorder)
                if (ImGui.TreeNode("Biome Settings"))
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat("Biome Border Thickness", ref Settings.BiomeBorderThickness, 1.0f, 6.0f);

                    if (ImGui.BeginTable("split", 3))
                    {
                        foreach (var biome in Biomes)
                        {
                            ImGui.TableNextColumn();
                            var id = biome.Key;
                            var info = biome.Value;

                            if (!Settings.BiomeOverrides.TryGetValue(id, out var ov))
                            {
                                ov = new ContentOverride();
                                Settings.BiomeOverrides[id] = ov;
                            }

                            bool show = ov.Show ?? info.Show;
                            if (ImGui.Checkbox($"##Show##{id}", ref show))
                            {
                                ov.Show = show;
                                ApplyBiomeOverrides();
                            }

                            var border = ov.BorderColor ?? info.BdColor;
                            ImGui.SameLine();
                            ColorSwatch($"Border Color##Biome{id}", ref border);
                            if (!ColorsEqual(border, ov.BorderColor ?? info.BdColor))
                            {
                                ov.BorderColor = border;
                                ApplyBiomeOverrides();
                            }

                            var label = string.IsNullOrWhiteSpace(info.Label) ? $"Biome {id}" : info.Label;
                            ImGui.SameLine();
                            ImGui.Text(label);
                        }
                        ImGui.EndTable();
                    }

                    ImGui.TreePop();
                }

            ImGui.SeparatorText("Layout Settings");
            var nudge = Settings.AnchorNudge;
            if (ImGui.SliderFloat2("Layout Nudge (px)", ref nudge, -60f, 60f))
                Settings.AnchorNudge = nudge;
            ImGui.SliderFloat("Scale Multiplier", ref Settings.ScaleMultiplier, 0.5f, 3.0f);

            ImGui.SeparatorText("Map Groups");

            if (ImGui.TreeNode("Settings"))
            {
                ImGui.InputTextWithHint("##MapGroupName", "group name", ref Settings.GroupNameInput, 256);
                ImGui.SameLine();
                if (ImGui.Button("Add new map group"))
                {
                    Settings.MapGroups.Add(new MapGroupSettings(Settings.GroupNameInput, Settings.DefaultBackgroundColor, Settings.DefaultFontColor));
                    Settings.GroupNameInput = string.Empty;
                }

                for (int i = 0; i < Settings.MapGroups.Count; i++)
                {
                    var mapGroup = Settings.MapGroups[i];
                    if (ImGui.TreeNode($"{mapGroup.Name}##MapGroup{i}"))
                    {
                        float buttonSize = ImGui.GetFrameHeight();
                        if (TriangleButton($"##Up{i}", buttonSize, new Vector4(1, 1, 1, 1), true))
                        {
                            MoveMapGroup(i, -1);
                        }
                        ImGui.SameLine();
                        if (TriangleButton($"##Down{i}", buttonSize, new Vector4(1, 1, 1, 1), false))
                        {
                            MoveMapGroup(i, 1);
                        }
                        ImGui.SameLine();
                        if (ImGui.Button($"Rename Group##{i}"))
                        {
                            NewGroupName = mapGroup.Name;
                            ImGui.OpenPopup($"RenamePopup##{i}");
                        }
                        ImGui.SameLine();
                        if (ImGui.Button($"Delete Group##{i}"))
                        {
                            DeleteMapGroup(i);
                        }
                        ImGui.SameLine();
                        ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                        ImGui.SameLine();
                        ImGui.Text("Background Color");
                        ImGui.SameLine();
                        ColorSwatch($"##MapGroupFontColor{i}", ref mapGroup.FontColor);
                        ImGui.SameLine(); ImGui.Text("Font Color");

                        for (int j = 0; j < mapGroup.Maps.Count; j++)
                        {
                            var mapName = mapGroup.Maps[j];
                            if (ImGui.InputTextWithHint($"##MapName{i}-{j}", "map name", ref mapName, 256))
                                mapGroup.Maps[j] = mapName;

                            ImGui.SameLine();
                            if (ImGui.Button($"Delete##MapNameDelete{i}-{j}"))
                            {
                                mapGroup.Maps.RemoveAt(j);
                                break;
                            }
                        }

                        if (ImGui.Button($"Add new map##AddNewMap{i}"))
                            mapGroup.Maps.Add(string.Empty);

                        if (ImGui.BeginPopupModal($"RenamePopup##{i}", ImGuiWindowFlags.AlwaysAutoResize))
                        {
                            ImGui.InputText("New Name", ref NewGroupName, 256);
                            if (ImGui.Button("OK"))
                            {
                                mapGroup.Name = NewGroupName;
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.SameLine();
                            if (ImGui.Button("Cancel"))
                            {
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.TreePop();
                    }
                }
                ImGui.TreePop();
            }
            #endregion
        }

        public override void DrawUI()
        {
            var inventoryPanel = InventoryPanel();

            var isGameHelperForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !isGameHelperForeground)
                return;

            EnsureProcessHandle();

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var playerRender))
                return;

            var drawList = ImGui.GetBackgroundDrawList();

            drawList.ChannelsSplit(4);

            var atlasUi = GetAtlasPanelUi();
            if (!atlasUi.IsVisible)
                return;

            var panelAddr = GetAtlasPanelAddress();
            var atlasMap = panelAddr != IntPtr.Zero
                ? Read<AtlasMapOffsets>(panelAddr)
                : default;
            bool useVector = TryVectorCount<AtlasNodeEntry>(atlasMap.AtlasNodes, out int vecCount)
                && vecCount > 0 && vecCount <= 10000;
            var atlasCount = useVector ? vecCount : atlasUi.Length;
            if (atlasCount <= 0 || atlasCount > 10000)
                return;

            var panelTopLeft = GetFinalTopLeft(in atlasUi.UiElementBase);
            var panelScale = ComputeScalePair(in atlasUi.UiElementBase);
            var panelSize = new Vector2(
                atlasUi.UiElementBase.UnscaledSize.X * panelScale.X,
                atlasUi.UiElementBase.UnscaledSize.Y * panelScale.Y);
            var panelRect = new RectangleF(panelTopLeft.X, panelTopLeft.Y, panelSize.X, panelSize.Y);

            Dictionary<StdTuple2D<int>, Vector2> nodeCenters = null;
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph = null;
            Dictionary<StdTuple2D<int>, bool> nodeCompleted = null;
            Dictionary<StdTuple2D<int>, bool> nodeAccessible = null;

            if (useVector && Settings.RouteLinesThroughNodes)
            {
                BuildAtlasGraph(atlasMap, vecCount, panelRect,
                    out nodeCenters, out graph, out nodeCompleted, out nodeAccessible);
            }

            var towers = new HashSet<string>(
                Settings.MapGroups
                    .Where(tower => string.Equals(tower.Name, "Towers", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(tower => tower.Maps)
                    .Select(NormalizeName),
                StringComparer.OrdinalIgnoreCase);
            var boundsTowers = CalculateBounds(Settings.DrawTowersInRange);

            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = [];
            if (doSearch)
            {
                searchList = searchQuery
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            var boundsSearch = CalculateBounds(Settings.DrawSearchInRange);

            var playerLocation = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(playerRender.WorldPosition);

            float resScale = ComputeRelativeUiScale(in atlasUi.UiElementBase, Settings.BaseWidth, Settings.BaseHeight);
            float uiScale = Math.Clamp(Settings.ScaleMultiplier * resScale, 0.5f, 4.0f);
            using (new FontScaleScope(uiScale))
            {
                if (!Settings.ControllerMode)
                    if (inventoryPanel)
                        return;

                if (Settings.DrawGrid && useVector)
                {
                    var centers = new Dictionary<StdTuple2D<int>, Vector2>();
                    drawList.ChannelsSetCurrent(ChannelGrid);
                    var completed = new HashSet<StdTuple2D<int>>();

                    for (int i = 0; i < vecCount; i++)
                    {
                        var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                        if (entry.UiElementPtr == IntPtr.Zero)
                            continue;

                        var node = Read<AtlasNode>(entry.UiElementPtr);

                        var nodeTopLeft = GetFinalTopLeft(in node.UiElementBase);
                        var nodeScale = ComputeScalePair(in node.UiElementBase);
                        var nodeSize = new Vector2(
                            node.UiElementBase.UnscaledSize.X * nodeScale.X,
                            node.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                        var nodeCenter = nodeTopLeft + nodeSize * 0.5f;

                        if (!panelRect.Contains(nodeCenter.X, nodeCenter.Y))
                            continue;

                        centers[entry.GridPosition] = nodeCenter;

                        if (node.IsCompleted)
                            completed.Add(entry.GridPosition);
                    }

                    static (int x, int y) XY(StdTuple2D<int> t) => (t.X, t.Y);
                    static bool IsCanonical(StdTuple2D<int> a, StdTuple2D<int> b)
                    {
                        var (ax, ay) = XY(a);
                        var (bx, by) = XY(b);
                        return (ax < bx) || (ax == bx && ay <= by);
                    }

                    if (TryVectorCount<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, out int connCount)
                        && connCount > 0)
                    {
                        float lineTh = MathF.Max(1f, uiScale * 2.5f);

                        for (int i = 0; i < connCount; i++)
                        {
                            var cn = ReadVectorAt<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, i);
                            var src = cn.GridPosition;

                            if (!centers.TryGetValue(src, out var a))
                                continue;

                            var targets = new[]
                            {
                                cn.Connection1, cn.Connection2, cn.Connection3, cn.Connection4
                            };

                            foreach (var dst in targets)
                            {
                                if (dst.Equals(default(StdTuple2D<int>)) || dst.Equals(src))
                                    continue;

                                if (!IsCanonical(src, dst))
                                    continue;

                                if (!centers.TryGetValue(dst, out var b))
                                    continue;

                                if (Settings.GridSkipCompleted && (completed.Contains(src) || completed.Contains(dst)))
                                    continue;

                                drawList.AddLine(a, b, ImGuiHelper.Color(Settings.GridLineColor), lineTh);
                            }
                        }
                    }
                }

                StdTuple2D<int> startNode = default;
                bool haveStartNode = false;
                if (Settings.RouteLinesThroughNodes && nodeCenters != null && nodeCenters.Count > 0)
                {
                    haveStartNode = TryGetNearestNode(playerLocation, nodeCenters, out startNode);
                }

                var pathCache = new Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>>();

                for (int i = 0; i < atlasCount; i++)
                {
                    AtlasNode atlasNode;
                    UiElement nodeUi;
                    StdTuple2D<int> nodeGrid = default;

                    if (useVector)
                    {
                        var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                        if (entry.UiElementPtr == IntPtr.Zero)
                            continue;

                        atlasNode = Read<AtlasNode>(entry.UiElementPtr);
                        nodeUi = Read<UiElement>(entry.UiElementPtr);
                        nodeGrid = entry.GridPosition;
                    }
                    else
                    {
                        atlasNode = atlasUi.GetAtlasNode(i);
                        nodeUi = atlasUi.GetChild(i);
                    }

                    var mapName = NormalizeName(atlasNode.MapName);
                    if (!IsPrintableUnicode(mapName))
                        continue;

                    if (string.IsNullOrWhiteSpace(mapName))
                        continue;

                    if (doSearch && !searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (Settings.HideCompletedMaps && (atlasNode.IsCompleted || (mapName.EndsWith("Citadel") && AtlasNode.IsFailedAttempt)))
                        continue;

                    if (Settings.HideNotAccessibleMaps && atlasNode.IsNotAccessible)
                        continue;

                    var rawContents = GetContentName(nodeUi);

                    var textSize = ImGui.CalcTextSize(mapName);

                    var nodeTopLeft = GetFinalTopLeft(in atlasNode.UiElementBase);
                    var nodeScale = ComputeScalePair(in atlasNode.UiElementBase);
                    var nodeSize = new Vector2(
                        atlasNode.UiElementBase.UnscaledSize.X * nodeScale.X,
                        atlasNode.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                    var nodeCenter = nodeTopLeft + nodeSize * 0.5f;
                    Vector2 drawPosition = nodeCenter - textSize * 0.5f;

                    drawPosition += Settings.AnchorNudge;

                    var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                        m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                    var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                    var fontColor = group?.FontColor ?? Settings.DefaultFontColor;

                    if (atlasNode.IsCompleted)
                        backgroundColor.W *= 0.4f;

                    var padding = new Vector2(5, 2) * uiScale;
                    var bgPos = drawPosition - padding;
                    var bgSize = textSize + padding * 2;
                    var rectCenter = (bgPos + (bgPos + bgSize)) * 0.5f;
                    var intersectionPoint = GetLineRectangleIntersection(playerLocation, rectCenter, bgPos, bgPos + bgSize);

                    bool shouldDrawCitadel = Settings.DrawLinesToCitadel && mapName.EndsWith("Citadel", StringComparison.OrdinalIgnoreCase);
                    bool shouldDrawTower = Settings.DrawLinesToTowers && towers.Contains(mapName) && !atlasNode.IsCompleted && boundsTowers.Contains(new PointF(drawPosition.X, drawPosition.Y));
                    bool shouldDrawSearch = Settings.DrawLinesSearchQuery && doSearch
                        && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y));
                    if (shouldDrawCitadel || shouldDrawTower || shouldDrawSearch)
                    {
                        uint lineColor = shouldDrawCitadel ? CitadelLineColor : shouldDrawTower ? TowerLineColor : SearchLineColor;
                        float thickness = MathF.Max(1f, uiScale * Settings.PathLineThickness);
                        
                        if (Settings.RouteLinesThroughNodes && useVector && nodeCenters != null && graph != null && haveStartNode && nodeCenters.ContainsKey(nodeGrid))
                        {
                            if (!pathCache.TryGetValue(nodeGrid, out var path))
                            {
                                path = FindShortestPathAStar(startNode, nodeGrid, graph, nodeCenters);
                                pathCache[nodeGrid] = path;
                            }
                            
                            if (path != null && path.Count > 0)
                            {
                                DrawPath(drawList, playerLocation, bgPos, bgSize,
                                    path, nodeCenters, nodeCompleted, nodeAccessible,
                                    lineColor, thickness, ChannelLines, ChannelDots);
                            }
                            else
                            {
                                drawList.ChannelsSetCurrent(ChannelLines);
                                var tip = GetLineRectangleIntersection(playerLocation, rectCenter,
                                    bgPos, bgPos + bgSize);
                                drawList.AddLine(playerLocation, tip, lineColor, thickness);
                                var endDot = OffsetPointOutsideRect(tip, rectCenter, thickness * 0.6f);
                                drawList.ChannelsSetCurrent(ChannelDots);
                                drawList.AddCircleFilled(endDot, thickness, lineColor);
                                drawList.AddCircle(
                                    endDot, thickness, DotOutlineColor, 0, MathF.Max(1f, thickness * 0.35f));


                            }
                        }
                        else
                        {
                            drawList.ChannelsSetCurrent(ChannelLines);
                            drawList.AddLine(playerLocation, intersectionPoint, lineColor, thickness);
                            var endDot = OffsetPointOutsideRect(intersectionPoint, rectCenter, thickness * 0.6f);
                            drawList.ChannelsSetCurrent(ChannelDots);
                            drawList.AddCircleFilled(endDot, thickness, lineColor);
                            drawList.AddCircle(
                                endDot, thickness, DotOutlineColor, 0, MathF.Max(1f, thickness * 0.35f));

                        }
                    }

                    drawList.ChannelsSetCurrent(ChannelLabels);
                    float rounding = 3f * uiScale;

                    if (Settings.ShowBiomeBorder && Biomes.TryGetValue(atlasNode.BiomeId, out var biome)
                        && biome.Show)
                    {
                        var biomeColor = biome.BdColor;
                        if (atlasNode.IsCompleted)
                            biomeColor.W *= 0.4f;

                        float bBorderTh = MathF.Max(1f, uiScale * Settings.BiomeBorderThickness);

                        var half = bBorderTh * 0.5f;
                        var outMin = bgPos - new Vector2(half, half);
                        var outMax = (bgPos + bgSize) + new Vector2(half, half);
                        var outRounding = MathF.Max(0f, rounding + half);

                        drawList.AddRect(outMin, outMax, ImGuiHelper.Color(biomeColor),
                            outRounding, ImDrawFlags.RoundCornersAll, bBorderTh);
                    }

                    drawList.AddRectFilled(bgPos, bgPos + bgSize, ImGuiHelper.Color(backgroundColor), rounding);
                    drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), mapName);

                    float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                    float nextRowTopY = drawPosition.Y + textSize.Y + (4f * uiScale);
                    float rowGap = 4f * uiScale;

                    CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                    if (Settings.ShowMapBadges)
                        DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap, uiScale);
                }

                drawList.ChannelsMerge();
            }
        }

        #region Routing helpers

        private static void BuildAtlasGraph(
            AtlasMapOffsets atlasMap,
            int nodeCount,
            RectangleF panelRect,
            out Dictionary<StdTuple2D<int>, Vector2> centers,
            out Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            out Dictionary<StdTuple2D<int>, bool> completed,
            out Dictionary<StdTuple2D<int>, bool> accessible)
        {
            centers = new Dictionary<StdTuple2D<int>, Vector2>(nodeCount);
            completed = new Dictionary<StdTuple2D<int>, bool>(nodeCount);
            graph = new Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>>(nodeCount);
            accessible = new Dictionary<StdTuple2D<int>, bool>(nodeCount);

            for (int i = 0; i < nodeCount; i++)
            {
                var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                if (entry.UiElementPtr == IntPtr.Zero)
                    continue;

                var node = Read<AtlasNode>(entry.UiElementPtr);

                var nodeTopLeft = GetFinalTopLeft(in node.UiElementBase);
                var nodeScale = ComputeScalePair(in node.UiElementBase);
                var nodeSize = new Vector2(
                    node.UiElementBase.UnscaledSize.X * nodeScale.X,
                    node.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                var nodeCenter = nodeTopLeft + nodeSize * 0.5f;

                centers[entry.GridPosition] = nodeCenter;
                completed[entry.GridPosition] = node.IsCompleted;
                accessible[entry.GridPosition] = !node.IsNotAccessible;
                graph[entry.GridPosition] = new List<StdTuple2D<int>>(4);
            }

            if (TryVectorCount<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, out int connCount) && connCount > 0)
            {
                for (int i = 0; i < connCount; i++)
                {
                    var cn = ReadVectorAt<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, i);
                    var src = cn.GridPosition;
                    if (!centers.ContainsKey(src))
                        continue;

                    var targets = new[] { cn.Connection1, cn.Connection2, cn.Connection3, cn.Connection4 };

                    foreach (var dst in targets)
                    {
                        if (dst.Equals(default(StdTuple2D<int>)) || dst.Equals(src))
                            continue;

                        if (!centers.ContainsKey(dst))
                            continue;

                        graph[src].Add(dst);

                        if (!graph.TryGetValue(dst, out var list))
                        {
                            list = new List<StdTuple2D<int>>(4);
                            graph[dst] = list;
                        }
                        list.Add(src);
                    }
                }
            }
        }

        private static bool TryGetNearestNode(Vector2 point, Dictionary<StdTuple2D<int>, Vector2> centers, out StdTuple2D<int> nearest)
        {
            nearest = default;
            float best = float.MaxValue;
            bool found = false;
            foreach (var kv in centers)
            {
                float d = Vector2.DistanceSquared(point, kv.Value);
                if (d < best)
                {
                    best = d;
                    nearest = kv.Key;
                    found = true;
                }
            }
            return found;
        }

        private static float Heuristic(StdTuple2D<int> a, StdTuple2D<int> b, Dictionary<StdTuple2D<int>, Vector2> centers)
        {
            var pa = centers[a];
            var pb = centers[b];
            return Vector2.Distance(pa, pb);
        }

        private static List<StdTuple2D<int>> FindShortestPathAStar(
            StdTuple2D<int> start,
            StdTuple2D<int> goal,
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            Dictionary<StdTuple2D<int>, Vector2> centers)
        {
            if (!graph.ContainsKey(start) || !graph.ContainsKey(goal))
                return null;

            var cameFrom = new Dictionary<StdTuple2D<int>, StdTuple2D<int>>();
            var gScore = new Dictionary<StdTuple2D<int>, float> { [start] = 0f };

            var open = new PriorityQueue<StdTuple2D<int>, float>();
            var f0 = Heuristic(start, goal, centers);
            open.Enqueue(start, f0);

            var inOpen = new HashSet<StdTuple2D<int>> { start };

            while (open.Count > 0)
            {
                var current = open.Dequeue();
                inOpen.Remove(current);

                if (current.Equals(goal))
                    return ReconstructPath(cameFrom, current);

                if (!graph.TryGetValue(current, out var neighbors))
                    continue;

                foreach (var nb in neighbors)
                {
                    float tentative = gScore[current] + Vector2.Distance(centers[current], centers[nb]);

                    if (!gScore.TryGetValue(nb, out var old) || tentative < old)
                    {
                        cameFrom[nb] = current;
                        gScore[nb] = tentative;
                        float f = tentative + Heuristic(nb, goal, centers);
                        if (!inOpen.Contains(nb))
                        {
                            open.Enqueue(nb, f);
                            inOpen.Add(nb);
                        }
                    }
                }
            }

            return null;
        }

        private static List<StdTuple2D<int>> ReconstructPath(Dictionary<StdTuple2D<int>, StdTuple2D<int>> cameFrom, StdTuple2D<int> current)
        {
            var path = new List<StdTuple2D<int>> { current };
            while (cameFrom.TryGetValue(current, out var prev))
            {
                current = prev;
                path.Add(current);
            }
            path.Reverse();
            return path;
        }

        private static void DrawPath(
            ImDrawListPtr drawList,
            Vector2 playerLocation,
            Vector2 labelBgPos,
            Vector2 labelBgSize,
            List<StdTuple2D<int>> path,
            Dictionary<StdTuple2D<int>, Vector2> centers,
            Dictionary<StdTuple2D<int>, bool> completedMap,
            Dictionary<StdTuple2D<int>, bool> accessibleMap,
            uint color,
            float thickness,
            int lineChannel,
            int dotChannel)
        {
            if (path == null || path.Count == 0)
                return;

            var segments = new List<(Vector2 A, Vector2 B, uint Col)>(path.Count + 1);
            var dots = new List<(Vector2 P, uint Col, float R)>(path.Count + 2);

            var first = centers[path[0]];
            segments.Add((playerLocation, first, color));

            if (completedMap != null && completedMap.TryGetValue(path[0], out var firstCompleted) && firstCompleted)
                dots.Add((first, CompletedNodeDotColor, thickness));

            for (int i = 1; i<path.Count; i++)
            {
                var a = centers[path[i - 1]];
                var b = centers[path[i]];
                bool aC = completedMap != null && completedMap.TryGetValue(path[i - 1], out var ac) && ac;
                bool bC = completedMap != null && completedMap.TryGetValue(path[i], out var bc) && bc;
                bool aA = accessibleMap != null && accessibleMap.TryGetValue(path[i - 1], out var aa) && aa;
                bool bA = accessibleMap != null && accessibleMap.TryGetValue(path[i], out var ba) && ba;
                
                uint segColor = ((aC && bC) || (aA && bA)) ? CompletedNodeDotColor : color;
                segments.Add((a, b, segColor));

                var atNode = path[i];
                if (completedMap != null && completedMap.TryGetValue(atNode, out var isCompleted) && isCompleted)
                    dots.Add((b, CompletedNodeDotColor, thickness));
            }

            var last = centers[path[^1]];
            var rectCenter = (labelBgPos + (labelBgPos + labelBgSize)) * 0.5f;
            var tip = GetLineRectangleIntersection(last, rectCenter, labelBgPos, labelBgPos + labelBgSize);
            segments.Add((last, tip, color));
            var endDot = OffsetPointOutsideRect(tip, rectCenter, thickness * 0.6f);
            dots.Add((endDot, color, thickness));

            drawList.ChannelsSetCurrent(lineChannel);
            for (int i = 0; i < segments.Count; i++)
                drawList.AddLine(segments[i].A, segments[i].B, segments[i].Col, thickness);

            drawList.ChannelsSetCurrent(dotChannel);
            float outlineTh = MathF.Max(1f, thickness * 0.35f);
            for (int i = 0; i < dots.Count; i++)
            {
                drawList.AddCircleFilled(dots[i].P, dots[i].R, dots[i].Col);
                drawList.AddCircle(dots[i].P, dots[i].R, DotOutlineColor, 0, outlineTh);
            }
        }
#endregion

        private void LoadBiomeMap()
        {
            var path = Path.Join(DllDirectory, "json", "biome.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, BiomeInfo>>(json);

            Biomes.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (byte.TryParse(content.Key, out var id))
                    Biomes[id] = content.Value;
            }

            ApplyBiomeOverrides();
        }

        private void LoadContentMap()
        {
            var path = Path.Join(DllDirectory, "json", "content.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, ContentInfo>>(json);

            MapTags.Clear();
            MapPlain.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (content.Key.All(char.IsLetter))
                    MapTags[content.Key] = content.Value;
                else
                    MapPlain[content.Key] = content.Value;
            }

            ApplyContentOverrides();
        }

        private static Vector2 ComputeScalePair(in UiElementBaseOffset uiBase)
        {
            var io = ImGui.GetIO();
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = io.DisplaySize.X / MathF.Max(1f, baseW);
            float sy = io.DisplaySize.Y / MathF.Max(1f, baseH);

            Vector2 pair;
            switch (uiBase.ScaleIndex)
            {
                case 0:
                    pair = new Vector2(sx, sx);
                    break;
                case 1:
                    pair = new Vector2(sy, sy);
                    break;
                case 2:
                    float s = MathF.Min(sx, sy);
                    pair = new Vector2(s, s);
                    break;
                default:
                    pair = new Vector2(sx, sy);
                    break;
            }

            return pair * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeUniformScale(in UiElementBaseOffset uiBase, float dispW, float dispH)
        {
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = dispW / MathF.Max(1f, baseW);
            float sy = dispH / MathF.Max(1f, baseH);

            float s = uiBase.ScaleIndex switch
            {
                0 => sx,
                1 => sy,
                2 => MathF.Min(sx, sy),
                _ => MathF.Min(sx, sy),
            };

            return s * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeRelativeUiScale(in UiElementBaseOffset uiBase, float refW, float refH)
        {
            var io = ImGui.GetIO();
            float cur = ComputeUniformScale(in uiBase, io.DisplaySize.X, io.DisplaySize.Y);
            float pref = ComputeUniformScale(in uiBase, refW, refH);

            return pref > 0 ? cur / pref : 1f;
        }

        private static Vector2 GetFinalTopLeft(in UiElementBaseOffset leaf)
        {
            Vector2 pos = Vector2.Zero;
            UiElementBaseOffset cur = leaf;
            int guard = 0;
            IntPtr last = IntPtr.Zero;
            while (true)
            {
                var scale = ComputeScalePair(in cur);
                pos += new Vector2(cur.RelativePosition.X * scale.X,
                    cur.RelativePosition.Y * scale.Y);

                if (UiElementBaseFuncs.ShouldModifyPos(cur.Flags))
                {
                    pos += new Vector2(cur.PositionModifier.X * scale.X,
                        cur.PositionModifier.Y * scale.Y);
                }

                if (cur.ParentPtr == IntPtr.Zero || cur.ParentPtr == last || ++guard > 64)
                    break;

                last = cur.Self;
                cur = Read<UiElementBaseOffset>(cur.ParentPtr);
            }

            return pos;
        }

        private static void DrawSquares(ImDrawListPtr drawList, List<ContentInfo> infos, float centerX,
            ref float nextRowTopY, float rowGap, float uiScale)
        {
            if (infos.Count == 0)
                return;

            const float fixedHeightBase = 18f;
            const float paddingBase = 6f;
            float fixedHeight = fixedHeightBase * uiScale;
            float padding = paddingBase * uiScale;

            var widths = new List<float>(infos.Count);
            float totalW = 0f;

            foreach (var info in infos)
            {
                var abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? info.Label[..1] : info.Abbrev;
                var textSize = ImGui.CalcTextSize(abbrev);
                float w = MathF.Max(fixedHeight, textSize.X + padding);
                widths.Add(w);
                totalW += w;
            }

            var basePos = new Vector2(centerX - totalW * 0.5f, nextRowTopY);

            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string abbrev;
                if (string.IsNullOrWhiteSpace(info.Abbrev))
                    abbrev = !string.IsNullOrEmpty(info.Label) ? info.Label.Substring(0, 1) : "?";
                else
                    abbrev = info.Abbrev;
                var boxSize = new Vector2(widths[i], fixedHeight);
                var squareMin = basePos;
                var squareMax = squareMin + boxSize;

                drawList.AddRectFilled(squareMin, squareMax, ImGuiHelper.Color(info.BgColor));

                var textSize = ImGui.CalcTextSize(abbrev);
                var textPos = squareMin + (boxSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGuiHelper.Color(info.FtColor), abbrev);

                basePos.X += boxSize.X;
            }

            nextRowTopY += fixedHeight + rowGap;
        }

        private readonly struct FontScaleScope : IDisposable
        {
            private readonly ImFontPtr _font;
            private readonly float _prevScale;
            public FontScaleScope(float scale)
            {
                _font = ImGui.GetFont();
                _prevScale = _font.Scale;
                _font.Scale = _prevScale * scale;
                ImGui.PushFont(_font);
            }
            public void Dispose()
            {
                ImGui.PopFont();
                _font.Scale = _prevScale;
            }
        }

        private static Vector2 GetLineRectangleIntersection(Vector2 lineStart, Vector2 rectCenter, Vector2 rectMin, Vector2 rectMax)
        {
            if (lineStart.X >= rectMin.X && lineStart.X <= rectMax.X &&
                lineStart.Y >= rectMin.Y && lineStart.Y <= rectMax.Y)
                return lineStart;

            Vector2 direction = rectCenter - lineStart;

            float dirX = direction.X == 0 ? 1e-6f : direction.X;
            float dirY = direction.Y == 0 ? 1e-6f : direction.Y;

            float tMinX = (rectMin.X - lineStart.X) / dirX;
            float tMaxX = (rectMax.X - lineStart.X) / dirX;
            float tMinY = (rectMin.Y - lineStart.Y) / dirY;
            float tMaxY = (rectMax.Y - lineStart.Y) / dirY;

            if (tMinX > tMaxX)
                (tMaxX, tMinX) = (tMinX, tMaxX);

            if (tMinY > tMaxY)
                (tMaxY, tMinY) = (tMinY, tMaxY);

            float tEnter = Math.Max(tMinX, tMinY);
            float tExit = Math.Min(tMaxX, tMaxY);

            if (tEnter > tExit || tEnter < 0)
                return rectCenter;

            float t = Math.Min(tEnter, 1.0f);

            return lineStart + direction * t;
        }

        private static Vector2 OffsetPointOutsideRect(Vector2 borderPoint, Vector2 rectCenter, float distance)
        {
            var dir = borderPoint - rectCenter;
            float lenSq = dir.X * dir.X + dir.Y * dir.Y;
            if (lenSq< 1e-6f)
                return borderPoint;
            dir /= MathF.Sqrt(lenSq);

            return borderPoint + dir* distance;
        }

        private void MoveMapGroup(int index, int direction)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            int to = index + direction;
            if (to < 0 || to >= Settings.MapGroups.Count)
                return;

            var item = Settings.MapGroups[index];
            Settings.MapGroups.RemoveAt(index);
            Settings.MapGroups.Insert(to, item);
        }

        private void DeleteMapGroup(int index)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            Settings.MapGroups.RemoveAt(index);
        }

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);

            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }

        private static bool TriangleButton(string id, float buttonSize, Vector4 color, bool isUp)
        {
            var pressed = ImGui.Button(id, new Vector2(buttonSize, buttonSize));
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetItemRectMin();
            var triSize = buttonSize * 0.5f;
            var center = new Vector2(pos.X + buttonSize * 0.5f, pos.Y + buttonSize * 0.5f);

            Vector2 p1, p2, p3;
            if (isUp)
            {
                p1 = new Vector2(center.X, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X - triSize * 0.5f, center.Y + triSize * 0.5f);
                p3 = new Vector2(center.X + triSize * 0.5f, center.Y + triSize * 0.5f);
            }
            else
            {
                p1 = new Vector2(center.X - triSize * 0.5f, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X + triSize * 0.5f, center.Y - triSize * 0.5f);
                p3 = new Vector2(center.X, center.Y + triSize * 0.5f);
            }

            drawList.AddTriangleFilled(p1, p2, p3, ImGuiHelper.Color(color));

            return pressed;
        }

        private static void EnsureProcessHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (Handle == IntPtr.Zero)
            {
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;

                return;
            }

            if (_handlePid != pid)
            {
                CloseAndResetHandle();
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;
            }
        }

        private static void CloseAndResetHandle()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero)
                return default;

            EnsureProcessHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(Handle, address, ref result);

            return result;
        }

        private static bool TryVectorCount<T>(in StdVector vector, out int count)
            where T : unmanaged
        {
            count = 0;
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero)
                return false;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0)
                return false;

            int stride = Marshal.SizeOf<T>();
            if (stride <= 0 || (bytes % stride) != 0)
                return false;

            long c = bytes / stride;
            if (c <= 0 || c > 10000)
                return false;

            count = (int)c;

            return true;
        }

        private static T ReadVectorAt<T>(in StdVector vector, int index)
            where T : unmanaged
        {
            int stride = Marshal.SizeOf<T>();
            var addr = IntPtr.Add(vector.First, index * stride);

            return Read<T>(addr);
        }

        public static string ReadWideString(nint address, int stringLength)
        {
            if (address == IntPtr.Zero || stringLength <= 0)
                return string.Empty;

            EnsureProcessHandle();
            byte[] result = new byte[stringLength * 2];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, address, result);

            return Encoding.Unicode.GetString(result).Split('\0')[0];
        }

        static bool IsPrintableUnicode(string str)
        {
            if (string.IsNullOrEmpty(str))
                return false;

            if (str.All(ch => ch == '?' || char.IsWhiteSpace(ch)))
                return false;

            foreach (var rune in str.EnumerateRunes())
            {
                if (rune.Value == 0xFFFD)
                    return false;

                var cat = Rune.GetUnicodeCategory(rune);
                switch (cat)
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                        return false;
                }
            }

            return true;
        }

        private static string NormalizeName(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? s
                : CollapseWhitespace(s.Replace('\u00A0', ' ').Trim());

        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!prevSpace) sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
                prevSpace = isSpace;
            }

            return sb.ToString();
        }

        private UiElement GetAtlasPanelUi()
        {
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            if (Settings.ControllerMode)
            {
                uiElement = uiElement.GetChild(17);
                uiElement = uiElement.GetChild(2);
                uiElement = uiElement.GetChild(3);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(6);
            }
            else
            {
                uiElement = uiElement.GetChild(25);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(6);
            }

            return uiElement;
        }

        private IntPtr GetAtlasPanelAddress()
        {
            IntPtr address = Core.States.InGameStateObject.GameUi.Address;
            var root = Read<UiElement>(address);
            if (Settings.ControllerMode)
            {
                address = root.GetChildAddress(17);
                var uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(2); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(3); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(6);
            }
            else
            {
                address = root.GetChildAddress(25);
                var uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(6);
            }

            return address;
        }

        private static bool InventoryPanel()
        {
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            var invetoryPanel = uiElement.GetChild(33);

            return invetoryPanel.IsVisible;
        }

        private static void CategorizeContents(IEnumerable<string> raws,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap,
            out List<ContentInfo> flags,
            out List<ContentInfo> contents)
        {
            flags = [];
            contents = [];
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var info = MatchContent(NormalizeName(raw), tagMap, plainMap);
                if (info is null || !info.Show)
                    continue;

                if (info.IsFlag) flags.Add(info);
                else contents.Add(info);
            }
        }

        public static List<string> GetContentName(UiElement nodeUi)
        {
            const int ContentOffset = 0x290;
            var result = new List<string>();

            nodeUi = nodeUi.GetChild(0);
            nodeUi = nodeUi.GetChild(0);

            var len = nodeUi.Length;
            if (len <= 0)
                return result;

            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                var contentPtr = Read<IntPtr>(childAddr + ContentOffset);
                if (contentPtr == IntPtr.Zero)
                    continue;

                var contentName = ReadWideString(contentPtr, 64);
                if (string.IsNullOrWhiteSpace(contentName))
                    continue;

                result.Add(contentName);
            }

            return result;
        }

        private static ContentInfo MatchContent(string contentName,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap)
        {
            if (string.IsNullOrWhiteSpace(contentName))
                return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int lb = normalized.IndexOf('[');
            int rb = lb >= 0 ? normalized.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb + 1)
            {
                var inside = normalized.Substring(lb + 1, rb - lb - 1);
                var pipe = inside.IndexOf('|');
                var tag = (pipe >= 0 ? inside[..pipe] : inside).Trim();

                if (tagMap.TryGetValue(tag, out var tagInfo))
                    return tagInfo;

                if (plainMap.TryGetValue(tag, out var tagAsPlain))
                    return tagAsPlain;
            }

            foreach (var map in plainMap)
            {
                if (normalized.Contains(map.Key, StringComparison.OrdinalIgnoreCase))
                    return map.Value;
            }

            foreach (var tag in tagMap)
            {
                if (normalized.Contains(tag.Key, StringComparison.OrdinalIgnoreCase))
                    return tag.Value;
            }

            return null;
        }

        private void ApplyBiomeOverrides()
        {
            foreach (var entry in Settings.BiomeOverrides)
            {
                if (Biomes.TryGetValue(entry.Key, out var info))
                {
                    var ov = entry.Value;
                    if (ov.BorderColor.HasValue)
                        info.BorderColor = [ov.BorderColor.Value.X, ov.BorderColor.Value.Y, ov.BorderColor.Value.Z, ov.BorderColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;
                }
            }
        }

        private void ApplyContentOverrides()
        {
            foreach (var entry in Settings.ContentOverrides)
            {
                if (MapTags.TryGetValue(entry.Key, out var info) ||
                    MapPlain.TryGetValue(entry.Key, out info))
                {
                    var ov = entry.Value;
                    if (ov.BackgroundColor.HasValue)
                        info.BackgroundColor = [ov.BackgroundColor.Value.X, ov.BackgroundColor.Value.Y, ov.BackgroundColor.Value.Z, ov.BackgroundColor.Value.W];

                    if (ov.FontColor.HasValue)
                        info.FontColor = [ov.FontColor.Value.X, ov.FontColor.Value.Y, ov.FontColor.Value.Z, ov.FontColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;

                    if (!string.IsNullOrEmpty(ov.Abbrev))
                        info.Abbrev = ov.Abbrev;
                }
            }
        }

        private static bool ColorsEqual(Vector4 a, Vector4 b, float eps = 0.001f)
        {
            return Math.Abs(a.X - b.X) < eps &&
                   Math.Abs(a.Y - b.Y) < eps &&
                   Math.Abs(a.Z - b.Z) < eps &&
                   Math.Abs(a.W - b.W) < eps;
        }

        private static RectangleF CalculateBounds(float range)
        {
            var baseBoundsTowers = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);

            return RectangleF.Inflate(baseBoundsTowers, baseBoundsTowers.Width * (range - 1.0f), baseBoundsTowers.Height * (range - 1.0f));
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}