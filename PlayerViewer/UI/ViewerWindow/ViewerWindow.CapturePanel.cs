using System;
using System.Collections.Generic;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;

namespace PlayerViewer.UI
{
    // Right-hand animation + capture panel: playback, screenshots, video recording,
    // and the deterministic full-animation MP4/WebP export.
    public partial class ViewerWindow
    {
        static readonly (string Label, int W, int H)[] CaptureSizes =
        {
            ("1280 x 1280", 1280, 1280),
            ("1920 x 1080", 1920, 1080),
            ("3840 x 2160 (4K)", 3840, 2160),
            ("2160 x 3840 (4K portrait)", 2160, 3840),
        };

        void DrawAnimationPanel()
        {
            Widgets.SectionHeader("Animation");

            //Both scene types expose the same playback surface; bridge through locals.
            bool standalone = _standalone != null;
            string currentAnim = standalone ? _standalone.CurrentAnimName : _scene.CurrentAnimName;
            bool paused = standalone ? _standalone.AnimPaused : _scene.AnimPaused;
            float speed = standalone ? _standalone.AnimSpeed : _scene.AnimSpeed;
            float rawFrameCount = standalone
                ? _standalone.CurrentSkeletal?.FrameCount ?? 1
                : _scene.CurrentSkeletal?.FrameCount ?? 1;
            List<string> animNames = standalone ? _standalone.AnimNames : _scene.Anims.AnimNames;

            void SetPaused(bool value) { if (standalone) _standalone.AnimPaused = value; else _scene.AnimPaused = value; }
            void SetSpeed(float value) { if (standalone) _standalone.AnimSpeed = value; else _scene.AnimSpeed = value; }
            void SetFrame(float value) { if (standalone) _standalone.SetAnimFrame(value); else _scene.SetAnimFrame(value); }
            void Play(string name) { if (standalone) _standalone.PlayAnim(name); else _scene.PlayAnim(name); }

            ImGui.TextColored(Theme.GoldBright, currentAnim ?? "(none)");

            if (ImGui.Button(paused ? "  Play  " : " Pause "))
                SetPaused(!paused);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##speed", ref speed, 0.1f, 2.0f, "speed %.2fx"))
                SetSpeed(speed);

            float frameCount = Math.Max(rawFrameCount - 1, 1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##frame", ref _uiFrame, 0, frameCount, "frame %.0f"))
            {
                SetFrame(_uiFrame);
                SetPaused(true);
            }

            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, "Search");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##animsearch", ref _animSearch, 64);

            var avail = ImGui.GetContentRegionAvail();
            ImGui.BeginChild("##animlist", new Vector2(0, Math.Max(avail.Y - 205, 120)), true);
            if (animNames.Count == 0)
                ImGui.TextColored(Theme.TextDim, "no skeletal animations");
            if (standalone && animNames.Count > 0)
            {
                if (ImGui.Selectable("<BLANK>", currentAnim == null))
                {
                    Play(null);
                    SetPaused(true);
                }
            }
            foreach (var name in animNames)
            {
                if (!string.IsNullOrEmpty(_animSearch) &&
                    !name.Contains(_animSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isCurrent = name == currentAnim;
                if (ImGui.Selectable(name, isCurrent))
                {
                    Play(name);
                    SetPaused(false);
                }
            }
            ImGui.EndChild();
        }

        void DrawCapturePanel()
        {
            Widgets.SectionHeader("Capture");

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##capres", CaptureSizes[_captureRes].Label))
            {
                for (int i = 0; i < CaptureSizes.Length; i++)
                    if (ImGui.Selectable(CaptureSizes[i].Label, i == _captureRes))
                        _captureRes = i;
                ImGui.EndCombo();
            }
            ImGui.Checkbox("Transparent background", ref _captureTransparent);

            if (ImGui.Button("Screenshot (PNG)", new Vector2(-1, 0)))
                SaveScreenshot();

            ImGui.Spacing();
            bool haveFfmpeg = VideoRecorder.FfmpegAvailable;

            if (_animExporting)
            {
                float progress = _animExportTotal > 0
                    ? Math.Min(_animExportIndex / (float)_animExportTotal, 1f) : 0f;
                ImGui.ProgressBar(progress, new Vector2(-1, 0),
                    $"Exporting {Math.Min(_animExportIndex + 1, _animExportTotal)}/{_animExportTotal}");
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.12f, 0.10f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.16f, 0.13f, 1));
                if (ImGui.Button("Cancel export", new Vector2(-1, 0)))
                    FinishAnimExport();
                ImGui.PopStyleColor(2);
            }
            else if (_recorder.IsRecording)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.12f, 0.10f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.16f, 0.13f, 1));
                //"###" keeps the widget ID stable while the timer in the label changes,
                //otherwise the click never registers (ID differs between press/release).
                if (ImGui.Button($"Stop recording ({_recorder.FrameCount / 60.0f:F1}s)###stoprec", new Vector2(-1, 0)))
                    StopRecording();
                ImGui.PopStyleColor(2);
            }
            else
            {
                ImGui.Checkbox("Video greenscreen", ref _recordGreenscreen);
                Widgets.DisabledButton("Record video (real-time)", haveFfmpeg, StartRecording);

                //Frame-exact export of the whole selected animation, at the chosen fps.
                ImGui.Spacing();
                ImGui.TextColored(Theme.TextDim, "Export full animation");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.TextDim, "FPS");
                ImGui.SameLine();
                if (ImGui.RadioButton("30", _exportFps == 30)) _exportFps = 30;
                ImGui.SameLine();
                if (ImGui.RadioButton("60", _exportFps == 60)) _exportFps = 60;

                bool canExport = haveFfmpeg && PlaybackHasAnim;
                Widgets.DisabledButton("MP4 (greenscreen)", canExport,
                    () => StartAnimExport(VideoRecorder.OutputFormat.Mp4, transparent: false));
                Widgets.DisabledButton("WebP (transparent)", canExport,
                    () => StartAnimExport(VideoRecorder.OutputFormat.WebpTransparent, transparent: true));

                if (!haveFfmpeg)
                    ImGui.TextColored(Theme.TextDim, "ffmpeg not found (exe folder or PATH)");
                else if (!PlaybackHasAnim)
                    ImGui.TextColored(Theme.TextDim, "select an animation to export");
            }
        }

        void SaveScreenshot()
        {
            string path = NativeFolderPicker.SaveFile("Save Screenshot", "player.png", "PNG image (*.png)", "*.png");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";

            var (_, w, h) = CaptureSizes[_captureRes];
            using var bmp = _pipeline.Capture(ActiveScene, w, h, _pipeline.BackgroundColor, _captureTransparent);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[UI] Saved {path}");
        }

        bool _recordGreenscreen = true;
        System.Numerics.Vector3 _backgroundBeforeRecord;

        void StartRecording()
        {
            string path = NativeFolderPicker.SaveFile("Save Video", "player.mp4", "MP4 video (*.mp4)", "*.mp4");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                path += ".mp4";
            StartRecording(path);
        }

        void StartRecording(string path)
        {
            _backgroundBeforeRecord = _pipeline.BackgroundColor;
            if (_recordGreenscreen)
                _pipeline.BackgroundColor = new System.Numerics.Vector3(0, 1, 0);
            if (!_recorder.Start(_pipeline.Width, _pipeline.Height, path))
                _pipeline.BackgroundColor = _backgroundBeforeRecord;
        }

        void StopRecording()
        {
            //Flush the last PBO frame that the async readback is holding.
            var last = _pipeline.ReadFinalPixelsAsync(out _);
            if (last != null)
                _recorder.PushFrame(last, _pipeline.Width, _pipeline.Height);
            _recorder.Stop();
            if (_recordGreenscreen)
                _pipeline.BackgroundColor = _backgroundBeforeRecord;
        }

        //--- Playback bridge: both scene types expose the same animation surface but
        //share no interface for it, so route through the active one.
        bool PlaybackHasAnim => (_standalone != null ? _standalone.CurrentSkeletal : _scene?.CurrentSkeletal) != null;
        int PlaybackFrameCount => (int)Math.Round(_standalone != null
            ? (_standalone.CurrentSkeletal?.FrameCount ?? 0f)
            : (_scene?.CurrentSkeletal?.FrameCount ?? 0f));
        float PlaybackAnimFrame => _standalone != null ? _standalone.AnimFrame : (_scene?.AnimFrame ?? 0f);
        bool PlaybackPaused => _standalone != null ? _standalone.AnimPaused : (_scene?.AnimPaused ?? true);
        void PlaybackSetPaused(bool v) { if (_standalone != null) _standalone.AnimPaused = v; else if (_scene != null) _scene.AnimPaused = v; }
        void PlaybackSetFrame(float f) { if (_standalone != null) _standalone.SetAnimFrame(f); else _scene?.SetAnimFrame(f); }
        void PlaybackUpdate(float dt) { if (_standalone != null) _standalone.Update(dt); else _scene?.Update(dt); }

        void StartAnimExport(VideoRecorder.OutputFormat format, bool transparent)
        {
            if (_animExporting || _recorder.IsRecording || !PlaybackHasAnim)
                return;
            int total = PlaybackFrameCount;
            if (total < 1)
                return;

            string ext = transparent ? ".webp" : ".mp4";
            string path = transparent
                ? NativeFolderPicker.SaveFile("Export Animation", "animation.webp", "WebP image (*.webp)", "*.webp")
                : NativeFolderPicker.SaveFile("Export Animation", "animation.mp4", "MP4 video (*.mp4)", "*.mp4");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                path += ext;

            //Force even dimensions so the raw RGBA stride matches ffmpeg's -video_size.
            //Resize is frozen for the duration because the recorder is "recording".
            int w = _pipeline.Width & ~1, h = _pipeline.Height & ~1;
            _pipeline.Resize(w, h);

            _animExportPrevBg = _pipeline.BackgroundColor;
            _animExportPrevPaused = PlaybackPaused;
            _animExportPrevFrame = PlaybackAnimFrame;

            if (!transparent)
                _pipeline.BackgroundColor = new System.Numerics.Vector3(0, 1, 0);

            if (!_recorder.Start(w, h, path, _exportFps, lockstep: true, format))
            {
                _pipeline.BackgroundColor = _animExportPrevBg;
                return;
            }

            _animExportTransparent = transparent;
            _animExportStep = Math.Max(1, 60 / _exportFps);
            _animExportIndex = 0;
            _animExportTotal = total;
            _animExporting = true;
            PlaybackSetPaused(true);
            //Restart cloth from rest so the first exported frame is reproducible.
            if (_standalone == null)
                _scene?.ResetHairPhysics();
        }

        void CaptureAnimExportFrame()
        {
            var bytes = _pipeline.CaptureFrameBytes(ActiveScene, _pipeline.BackgroundColor,
                _animExportTransparent, out int w, out int h);
            _recorder.PushFrame(bytes, w, h);

            _animExportIndex += _animExportStep;
            if (_animExportIndex >= _animExportTotal)
                FinishAnimExport();
        }

        void FinishAnimExport()
        {
            _recorder.Stop();
            _pipeline.BackgroundColor = _animExportPrevBg;
            PlaybackSetPaused(_animExportPrevPaused);
            PlaybackSetFrame(_animExportPrevFrame);
            _animExporting = false;
            Console.WriteLine($"[UI] Exported {_recorder.OutputPath}");
        }
    }
}
