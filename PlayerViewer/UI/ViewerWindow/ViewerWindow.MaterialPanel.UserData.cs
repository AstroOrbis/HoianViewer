using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.Materials;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // User tab of the material editor.
    public partial class ViewerWindow
    {
        void DrawUserData(FMAT material)
        {
            var userData = material.Material.UserData;
            var baseline = Baseline(material);

            DrawResetAll(
                "user data entry",
                baseline.ChangedUserData(material.Material).ToList(),
                name => baseline.ResetUserData(material.Material, name),
                material
            );

            if (userData == null || userData.Count == 0)
            {
                Widgets.DimText("none");
                return;
            }

            foreach (var entry in userData.ToArray())
            {
                var data = entry.Value;
                bool changed = baseline.UserDataChanged(material.Material, entry.Key);
                DrawEntryName($"{entry.Key}  ({data.Type})", changed, dim: !changed);
                string key = entry.Key;
                var reset = ResetAction(
                    material,
                    changed,
                    () => baseline.ResetUserData(material.Material, key)
                );
                switch (data.Type)
                {
                    case UserDataType.String:
                    case UserDataType.WString:
                        bool unicode = data.Type == UserDataType.WString;
                        DrawStringArray(
                            $"ud{entry.Key}",
                            data.GetValueStringArray(),
                            v =>
                            {
                                data.SetValue(v, unicode);
                                MaterialEdited(material);
                            },
                            reset
                        );
                        break;
                    case UserDataType.Single:
                        DrawFloatArray(
                            $"ud{entry.Key}",
                            data.GetValueSingleArray(),
                            v =>
                            {
                                data.SetValue(v);
                                MaterialEdited(material);
                            },
                            reset
                        );
                        break;
                    case UserDataType.Int32:
                        DrawIntArray(
                            $"ud{entry.Key}",
                            data.GetValueInt32Array(),
                            v =>
                            {
                                data.SetValue(v);
                                MaterialEdited(material);
                            },
                            reset
                        );
                        break;
                    case UserDataType.Byte:
                        ImGui.AlignTextToFramePadding();
                        Widgets.DimText($"{data.GetValueByteArray().Length} bytes (not editable)");
                        ImGui.SameLine();
                        DrawResetButton($"ud{entry.Key}", reset);
                        break;
                }
            }
        }
    }
}
