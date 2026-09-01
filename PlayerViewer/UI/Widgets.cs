using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Reusable themed widgets: searchable gear combos, section headers, labeled rows,
    /// and bound controls.
    /// </summary>
    public static class Widgets
    {
        static readonly Dictionary<string, string> _searches = new();

        public static void SectionHeader(string text)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, text.ToUpperInvariant());
            ImGui.PushStyleColor(ImGuiCol.Separator, Theme.GoldDim);
            ImGui.Separator();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        /// <summary>Label + combo on one line with fixed label column.</summary>
        public static void LabeledRow(string label, Action drawControl)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, label);
            ImGui.SameLine(92);
            drawControl();
        }

        /// <summary>
        /// Searchable combo for a gear list. Returns true when the selection changed
        /// (selected receives the new entry, null = none).
        /// </summary>
        public static bool GearCombo(
            string label,
            List<GearEntry> entries,
            GearEntry current,
            out GearEntry selected,
            bool allowNone = true,
            string noneLabel = "Blank"
        )
        {
            var ordered = new List<GearEntry>(entries.Count);
            foreach (var group in new[] { true, false })
            foreach (var entry in entries)
                if (entry.IsCustom == group)
                    ordered.Add(entry);

            return FilterCombo(
                "##" + label,
                label,
                "gear" + label,
                current?.DisplayName ?? noneLabel,
                ordered,
                (entry, _) => $"{entry.DisplayName}##{entries.IndexOf(entry)}",
                (entry, search) =>
                    MatchesSearch(entry.DisplayName, search)
                    || MatchesSearch(entry.Label ?? "", search),
                entry => entry.IsCustom,
                entry => entry.Label,
                current,
                allowNone,
                noneLabel,
                300,
                false,
                out selected
            );
        }

        /// <summary>
        /// Full-width combo over a plain string list with a filter box, for lists too long
        /// to scroll comfortably. Returns true when the selection changed.
        /// </summary>
        public static bool StringCombo(
            string id,
            string current,
            IReadOnlyList<string> items,
            out string selected
        )
        {
            return FilterCombo(
                id,
                id,
                id,
                current ?? "",
                items,
                (item, i) => $"{item}##{i}",
                MatchesSearch,
                null,
                null,
                current,
                false,
                null,
                260,
                true,
                out selected
            );
        }

        /// <summary>
        /// The one filtered combo: a search box that takes the focus on open, a fixed height
        /// list of the rows that match, the selection scrolled into view on open and kept in
        /// view by the arrows, and a click as the only thing that closes it. An arrow step
        /// applies the pick and leaves the list up so it can be walked through and looked at.
        /// </summary>
        static bool FilterCombo<T>(
            string comboId,
            string searchKey,
            string navId,
            string preview,
            IReadOnlyList<T> items,
            Func<T, int, string> label,
            Func<T, string, bool> matches,
            Func<T, bool> gold,
            Func<T, string> tooltip,
            T current,
            bool allowNone,
            string noneLabel,
            float listHeight,
            bool resetSearchOnOpen,
            out T selected
        )
            where T : class
        {
            selected = current;
            bool clicked = false;

            ImGui.SetNextItemWidth(-1);
            if (!ImGui.BeginCombo(comboId, preview, ImGuiComboFlags.HeightLarge))
                return false;

            bool justOpened = ImGui.IsWindowAppearing();
            if (justOpened)
            {
                if (resetSearchOnOpen)
                    _searches[searchKey] = "";
                ImGui.SetKeyboardFocusHere();
            }
            string search = _searches.GetValueOrDefault(searchKey, "");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##search" + searchKey, ref search, 64))
                _searches[searchKey] = search;

            ImGui.Separator();
            //The rows in the order they are drawn, so an arrow step lands where the eye
            //expects it to rather than somewhere in the unfiltered list.
            var rows = new List<T>();
            if (ImGui.BeginChild("##list" + searchKey, new Vector2(0, listHeight)))
            {
                if (allowNone && MatchesSearch(noneLabel, search))
                {
                    rows.Add(null);
                    if (ImGui.Selectable(noneLabel, current == null))
                    {
                        selected = null;
                        clicked = true;
                    }
                    KeepRowVisible(navId, current == null);
                }

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (!matches(item, search))
                        continue;
                    bool isGold = gold != null && gold(item);
                    if (isGold)
                        ImGui.PushStyleColor(ImGuiCol.Text, Theme.GoldBright);
                    bool isSelected = EqualityComparer<T>.Default.Equals(item, current);
                    rows.Add(item);
                    if (ImGui.Selectable(label(item, i), isSelected))
                    {
                        selected = item;
                        clicked = true;
                    }
                    KeepRowVisible(navId, isSelected);
                    if (isGold)
                        ImGui.PopStyleColor();
                    if (isSelected && justOpened)
                        ImGui.SetScrollHereY();
                    string tip = tooltip?.Invoke(item);
                    if (!string.IsNullOrEmpty(tip) && ImGui.IsItemHovered())
                        ImGui.SetTooltip(tip);
                }
            }
            ImGui.EndChild();

            int move = PopupListNav(navId, rows.Count, rows.IndexOf(current));
            if (move >= 0)
                selected = rows[move];
            if (clicked)
                ImGui.CloseCurrentPopup();
            ImGui.EndCombo();
            return clicked || move >= 0;
        }

        /// <summary>
        /// The rows of a plain combo popup with the arrow protocol done for them: drawRow draws
        /// row i and returns true when it was clicked, and pick receives the row chosen by a
        /// click or by an arrow step.
        /// </summary>
        public static void PopupRows(
            string id,
            int count,
            int current,
            Func<int, bool, bool> drawRow,
            Action<int> pick
        )
        {
            for (int i = 0; i < count; i++)
            {
                if (drawRow(i, i == current))
                    pick(i);
                KeepRowVisible(id, i == current);
            }
            int move = PopupListNav(id, count, current);
            if (move >= 0)
                pick(move);
        }

        static readonly HashSet<string> _navScroll = new(StringComparer.Ordinal);

        /// <summary>
        /// Up and down over a list of selectables. Returns the row to move to, or -1 when
        /// nothing moves. Call it inside the list's own window once its rows are drawn, with
        /// the count and the current row taken from the rows as they were drawn.
        /// </summary>
        public static int ListNav(string id, int count, int current) =>
            Navigate(id, count, current, ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive());

        /// <summary>
        /// The same for a list inside a combo popup, where the focus sits on the popup or on
        /// its filter box rather than on the list, and the filter box is active the whole time
        /// the popup is up.
        /// </summary>
        public static int PopupListNav(string id, int count, int current) =>
            Navigate(
                id,
                count,
                current,
                ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            );

        static int Navigate(string id, int count, int current, bool active)
        {
            _navScroll.Remove(id);
            if (!active || count == 0)
                return -1;

            int step = 0;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.DownArrow)))
                step++;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.UpArrow)))
                step--;
            if (step == 0)
                return -1;

            int next =
                current < 0 || current >= count
                    ? (step > 0 ? 0 : count - 1)
                    : Math.Clamp(current + step, 0, count - 1);
            if (next == current)
                return -1;
            _navScroll.Add(id);
            return next;
        }

        /// <summary>
        /// Follows an arrowed selection that has gone off screen.
        /// </summary>
        public static void KeepRowVisible(string id, bool isSelected)
        {
            if (isSelected && _navScroll.Contains(id) && !ImGui.IsItemVisible())
                ImGui.SetScrollHereY(0.5f);
        }

        /// <summary>Case insensitive filter test, empty filter matches everything.</summary>
        public static bool Matches(string text, string filter) =>
            string.IsNullOrEmpty(filter)
            || (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

        static bool MatchesSearch(string text, string search) => Matches(text, search);

        /// <summary>Full-width button that dims and no-ops when disabled.</summary>
        public static void DisabledButton(string label, bool enabled, Action onClick) =>
            DisabledButton(label, enabled, new Vector2(-1, 0), onClick);

        /// <summary>At a given size, for one that shares its line with something else. The
        /// full width default runs to the edge of the content region, so anything after it on
        /// the same line is drawn outside and clipped away without a trace.</summary>
        public static void DisabledButton(string label, bool enabled, Vector2 size, Action onClick)
        {
            if (!enabled)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            if (ImGui.Button(label, size) && enabled)
                onClick();
            if (!enabled)
                ImGui.PopStyleVar();
        }

        /// <summary>Full-width red (destructive/cancel) button.</summary>
        public static void RedButton(string label, Action onClick) =>
            RedButton(label, new Vector2(-1, 0), onClick);

        /// <summary>Red button at a given size, for a confirm that sits beside a cancel. A
        /// full-width one leaves no room for anything after it on the same line.</summary>
        public static void RedButton(string label, Vector2 size, Action onClick)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedButtonBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedButtonHover);
            if (ImGui.Button(label, size))
                onClick();
            ImGui.PopStyleColor(2);
        }

        /// <summary>Muted caption/status text.</summary>
        public static void DimText(string text) => ImGui.TextColored(Theme.TextDim, text);

        /// <summary>Red error/warning text.</summary>
        public static void ErrorText(string text) => ImGui.TextColored(Theme.Error, text);

        /// <summary>Green success/active text.</summary>
        public static void SuccessText(string text) => ImGui.TextColored(Theme.Success, text);

        /// <summary>Tooltip shown when the last-drawn item is hovered.</summary>
        public static void ItemTooltip(string text)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        //Read a value, draw the control, and on edit push the new value through <paramref name="set"/> then
        //run <paramref name="onChanged"/> (persist/side effects). Each returns true when the value changed.

        public static bool Checkbox(
            string label,
            bool value,
            Action<bool> set,
            Action onChanged = null
        )
        {
            bool v = value;
            if (!ImGui.Checkbox(label, ref v))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool SliderInt(
            string label,
            int value,
            int min,
            int max,
            Action<int> set,
            Action onChanged = null,
            string format = "%d"
        )
        {
            int v = value;
            if (!ImGui.SliderInt(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool SliderFloat(
            string label,
            float value,
            float min,
            float max,
            Action<float> set,
            Action onChanged = null,
            string format = "%.2f"
        )
        {
            float v = value;
            if (!ImGui.SliderFloat(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool InputInt(
            string label,
            int value,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            if (!ImGui.InputInt(label, ref v))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool Combo(
            string label,
            int value,
            string[] items,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            if (!ImGui.Combo(label, ref v, items, items.Length))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool ColorEdit3(
            string label,
            Vector3 value,
            Action<Vector3> set,
            ImGuiColorEditFlags flags = ImGuiColorEditFlags.None,
            Action onChanged = null
        )
        {
            Vector3 v = value;
            if (!ImGui.ColorEdit3(label, ref v, flags))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }
    }
}
