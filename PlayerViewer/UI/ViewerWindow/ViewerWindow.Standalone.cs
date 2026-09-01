using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Left-hand panel shown when viewing a loose (dropped/browsed) BFRES model.
    public partial class ViewerWindow
    {
        void DrawStandalonePanel()
        {
            Widgets.SectionHeader("Standalone Model");

            ImGui.TextColored(Theme.GoldBright, _standalone.Name);
            ImGui.PushTextWrapPos();
            Widgets.DimText(_standalone.SourcePath);
            ImGui.PopTextWrapPos();
            if (_standaloneError != null)
                Widgets.ErrorText(_standaloneError);

            ImGui.Spacing();
            if (ImGui.Button("Back to player", new Vector2(-1, 0)))
            {
                CloseStandalone();
                return;
            }
            if (ImGui.Button("Frame model", new Vector2(-1, 0)))
                _pipeline.FrameSphere(_standalone.GetBounding());
            DrawSaveSection();

            var models = _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();

            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float avail = VisibleHeightBelowCursor();
            //The lighting/view tail below leaves about 120px here, which the material and
            //texture lists cannot use. They ask for more and the panel scrolls instead.
            //The active tab is only known once it has drawn, so the request lands a frame late.
            float bodyHeight = Math.Max(
                avail - _measuredStandaloneTailHeight - spacing,
                _standaloneBodyMin
            );
            float requestedMin = 160;

            ImGui.BeginChild("##standalonebody", new Vector2(0, bodyHeight), false);
            if (ImGui.BeginTabBar("##standalonetabs"))
            {
                if (ImGui.BeginTabItem("Models"))
                {
                    DrawModelsTab(models);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Materials"))
                {
                    requestedMin = 380;
                    DrawMaterialsTab(models);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Textures"))
                {
                    requestedMin = 380;
                    DrawTexturesTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.EndChild();
            _standaloneBodyMin = requestedMin;

            float tailY0 = ImGui.GetCursorPosY();
            DrawLightingSection();
            DrawTeamColorSection();
            DrawViewSection();
            _measuredStandaloneTailHeight = ImGui.GetCursorPosY() - tailY0;
        }

        /// <summary>
        /// Room left below the cursor in the current child, measured so it does not move when
        /// the child is scrolled.
        /// </summary>
        static float VisibleHeightBelowCursor() =>
            ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetCursorPosY();

        void DrawModelsTab(List<BfresEditor.BfresModelAsset> models)
        {
            ImGui.BeginChild("##models", new Vector2(0, 0), true);
            for (int mi = 0; mi < models.Count; mi++)
            {
                var model = models[mi];
                bool visible = model.IsVisible;
                if (ImGui.Checkbox($"##{mi}_vis", ref visible))
                    model.IsVisible = visible;
                ImGui.SameLine();
                if (ImGui.TreeNode($"{model.ModelData.Name}##{mi}"))
                {
                    foreach (var mesh in model.Meshes)
                    {
                        bool meshVis = mesh.Shape.IsVisible;
                        if (ImGui.Checkbox($"{mesh.Name}##{mi}_{mesh.Name}", ref meshVis))
                            mesh.Shape.IsVisible = meshVis;
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.EndChild();
        }

        //Every model of the loaded scene.
        IReadOnlyList<BfresEditor.BfresModelAsset> StandaloneModels() =>
            _standalone?.Render == null
                ? Array.Empty<BfresEditor.BfresModelAsset>()
                : _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();

        //Every material of the loaded model, in model order.
        IEnumerable<BfresEditor.FMAT> StandaloneMaterials()
        {
            foreach (var model in StandaloneModels())
            foreach (var material in model.ResModel.Materials.OfType<BfresEditor.FMAT>())
                yield return material;
        }

        //The weight is part of the key, so a material drawn by shapes with different skin
        //counts needs a variation per count.
        IEnumerable<uint> WeightsFor(BfresEditor.FMAT material)
        {
            foreach (var model in StandaloneModels())
            foreach (var mesh in model.Meshes)
                if (mesh.Shape.Material == material)
                    yield return mesh.Shape.VertexSkinCount;
        }

        //Minimum height the tab body asks for, set by whichever tab drew last frame.
        float _standaloneBodyMin = 120;
    }
}
