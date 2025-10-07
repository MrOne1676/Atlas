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
            ImGui.Separator();

            ImGui.Text("You can search for multiple maps at once. To do this, separate them with a comma ','");
            ImGui.InputText("Search Map", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            ImGui.Checkbox("Draw Lines", ref Settings.DrawLinesSearchQuery);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                Settings.SearchQuery = string.Empty;
            ImGui.SliderFloat("Draw Lines to Search in range", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
            ImGui.Separator();

            ImGui.Checkbox("Draw Lines to Citadels", ref Settings.DrawLinesToCitadel);
            ImGui.Checkbox("Draw Lines to Towers in range", ref Settings.DrawLinesToTowers);
            ImGui.SameLine();
            ImGui.SliderFloat("##DrawTowersInRange", ref Settings.DrawTowersInRange, 1.0f, 10.0f);
            ImGui.Separator();

            ImGui.Checkbox("Hide Completed Maps", ref Settings.HideCompletedMaps);
            ImGui.Checkbox("Hide Not Accessible Maps", ref Settings.HideNotAccessibleMaps);
            ImGui.Checkbox("Draw Atlas Grid", ref Settings.DrawGrid);
            if (Settings.DrawGrid)
                if (ImGui.CollapsingHeader("Atlas Grid Settings"))
                {
                    ImGui.Checkbox("Hide connections to completed maps", ref Settings.GridSkipCompleted);
                    ColorSwatch("Grid Color", ref Settings.GridLineColor);
                    ImGui.SameLine();
                    ImGui.Text("Grid Color");
                    ImGui.Separator();
                }

            ImGui.Checkbox("Show Map Content Badges", ref Settings.ShowMapBadges);
            if (Settings.ShowMapBadges)
                if (ImGui.CollapsingHeader("Map Content Badge Settings"))
                {
                    foreach (var kv in MapTags.Concat(MapPlain))
                    {
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
                            ApplyContentOverrides();
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
                    ImGui.Separator();
                }

            ImGui.Checkbox("Show Biome Border", ref Settings.ShowBiomeBorder);
            if (Settings.ShowBiomeBorder)
                if (ImGui.CollapsingHeader("Biome Settings"))
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat("Biome Border Thickness", ref Settings.BiomeBorderThickness, 1.0f, 6.0f);

                    if (ImGui.CollapsingHeader("Biomes Borders Colors"))
                    {
                        foreach (var biome in Biomes)
                        {
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
                                ApplyContentOverrides();
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
                    }
                }
            ImGui.Separator();

            ImGui.Text("Layout Settings");
            var nudge = Settings.AnchorNudge;
            if (ImGui.SliderFloat2("Layout Nudge (px)", ref nudge, -60f, 60f))
                Settings.AnchorNudge = nudge;
            ImGui.SliderFloat("Scale Multiplier", ref Settings.ScaleMultiplier, 0.5f, 3.0f);
            ImGui.Separator();

            if (ImGui.CollapsingHeader("Map Groups Settings"))
            {
                ImGui.InputText("##MapGroupName", ref Settings.GroupNameInput, 256);
                ImGui.SameLine();
                if (ImGui.Button("Add new map group"))
                {
                    Settings.MapGroups.Add(new MapGroupSettings(Settings.GroupNameInput, Settings.DefaultBackgroundColor, Settings.DefaultFontColor));
                    Settings.GroupNameInput = string.Empty;
                }

                for (int i = 0; i < Settings.MapGroups.Count; i++)
                {
                    var mapGroup = Settings.MapGroups[i];
                    if (ImGui.CollapsingHeader($"{mapGroup.Name}##MapGroup{i}"))
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
                            if (ImGui.InputText($"##MapName{i}-{j}", ref mapName, 256))
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
                    }
                }
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
                    var panelTopLeft = GetFinalTopLeft(in atlasUi.UiElementBase);
                    var panelScale = ComputeScalePair(in atlasUi.UiElementBase);
                    var panelSize = new Vector2(
                        atlasUi.UiElementBase.UnscaledSize.X * panelScale.X,
                        atlasUi.UiElementBase.UnscaledSize.Y * panelScale.Y);
                    var panelRect = new RectangleF(panelTopLeft.X, panelTopLeft.Y, panelSize.X, panelSize.Y);

                    var centers = new Dictionary<StdTuple2D<int>, Vector2>();
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

                for (int i = 0; i < atlasCount; i++)
                {
                    AtlasNode atlasNode;
                    UiElement nodeUi;
                    if (useVector)
                    {
                        var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                        if (entry.UiElementPtr == IntPtr.Zero)
                            continue;
                        atlasNode = Read<AtlasNode>(entry.UiElementPtr);
                        nodeUi = Read<UiElement>(entry.UiElementPtr);
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

                    float rounding = 3f * uiScale;
                    float borderTh = MathF.Max(1f, 1f * uiScale);

                    if (Settings.ShowBiomeBorder && Biomes.TryGetValue(atlasNode.BiomeId, out var biome))
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

                    if (Settings.DrawLinesToCitadel && mapName.EndsWith("Citadel", StringComparison.OrdinalIgnoreCase))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, CitadelLineColor);
                    }

                    if (Settings.DrawLinesToTowers
                        && towers.Contains(mapName)
                        && !atlasNode.IsCompleted
                        && boundsTowers.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, TowerLineColor);
                    }

                    if (Settings.DrawLinesSearchQuery
                        && doSearch && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, SearchLineColor);
                    }

                    float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                    float nextRowTopY = drawPosition.Y + textSize.Y + (4f * uiScale);
                    float rowGap = 4f * uiScale;

                    CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                    if (Settings.ShowMapBadges)
                        DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap, uiScale);
                }
            }
        }

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