using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Composited export/viewport background. Part of <see cref="PlayerConfig"/> so it travels
    /// with a preset (a preset captures the whole look: gear, colors, and background).
    /// </summary>
    public class BackgroundConfig
    {
        public int Mode; //0 Transparent, 1 Color, 2 Image
        public float[] Color = { 0f, 1f, 0f }; //Color mode; green reproduces the old greenscreen
        public string ImagePath = "";
        public int ScaleMode; //0 Fill, 1 Fit, 2 Stretch
        public float Zoom = 1f;
        public float OffsetX;
        public float OffsetY;
        public bool Tile;
        public int TileX = 1;
        public int TileY = 1;

        //Clamp user-supplied (preset/settings) values into valid ranges.
        public void Normalize()
        {
            Mode = System.Math.Clamp(Mode, 0, 2);
            ScaleMode = System.Math.Clamp(ScaleMode, 0, 2);
            var color = new[] { 0f, 1f, 0f };
            if (Color != null)
                for (int i = 0; i < 3 && i < Color.Length; i++)
                    if (float.IsFinite(Color[i]))
                        color[i] = System.Math.Clamp(Color[i], 0f, 1f);
            Color = color;
            if (!float.IsFinite(Zoom) || Zoom <= 0f)
                Zoom = 1f;
            if (!float.IsFinite(OffsetX))
                OffsetX = 0f;
            if (!float.IsFinite(OffsetY))
                OffsetY = 0f;
            ImagePath ??= "";
            TileX = System.Math.Max(1, TileX);
            TileY = System.Math.Max(1, TileY);
        }
    }

    public class PlayerConfig
    {
        public int PlayerType;
        public int EyeColor;
        public int SkinTone;
        public string Hair;
        public int HairVariation;
        public string Eyebrow;
        public int EyebrowVariation;
        public string Head;
        public int HeadVariation;
        public string Clothes;
        public int ClothesVariation;
        public string Bottom;
        public int BottomVariation;
        public string Shoes;
        public int ShoesVariation;
        public string Tank;
        public int TankVariation;
        public string Weapon;
        public int WeaponVariation;
        public int TeamColorIndex;
        public int TeamIndex;
        public bool UseCustomTeamColor = true;
        public float[] CustomAlpha = { 0.925f, 0.243f, 0.549f };
        public float[] CustomBravo = { 0.196f, 0.855f, 0.302f };
        public float[] CustomCharlie = { 0.980f, 0.769f, 0.196f };

        //Composited export/viewport background, saved and loaded with the preset.
        public BackgroundConfig Background = new();

        public void Normalize()
        {
            Background ??= new BackgroundConfig();
            Background.Normalize();
            CustomAlpha = NormalizeTeamColor(CustomAlpha, 0.925f, 0.243f, 0.549f);
            CustomBravo = NormalizeTeamColor(CustomBravo, 0.196f, 0.855f, 0.302f);
            CustomCharlie = NormalizeTeamColor(CustomCharlie, 0.980f, 0.769f, 0.196f);
        }

        static float[] NormalizeTeamColor(float[] value, float r, float g, float b)
        {
            var color = new[] { r, g, b };
            if (value != null)
                for (int i = 0; i < 3 && i < value.Length; i++)
                    if (float.IsFinite(value[i]))
                        color[i] = System.Math.Clamp(value[i], 0f, 1f);
            return color;
        }
    }

    /// <summary>
    /// Persisted app configuration (romfs paths etc). Stored in the per-user data folder.
    /// </summary>
    public class AppConfig
    {
        public string RomfsPath = "";
        public string SdodrRomfsPath = "";
        public string LayeredFsPath = "";
        public bool UseLayeredFs = false;
        public int WindowWidth = 1600;
        public int WindowHeight = 900;

        //--- Export/capture settings (configured in the Settings window)
        //Trim fully-transparent deadspace off exported frames. Uses the transparent
        //render as an alpha oracle, so it also crops greenscreen MP4s.
        public bool TrimDeadspace = false;

        //Extra pixels of transparent margin kept around the content bounding box.
        public int TrimMarginPx = 0;

        //WebP encode quality: 100 = lossless (bit-exact), below = lossy (smaller/faster).
        public int WebpQuality = 100;

        //Export supersample factor (1-8). Exports render internally at this multiple of the
        //capture size; with trim on, the crop keeps that internal resolution so a loosely
        //framed subject still exports sharp. VRAM and temp-disk use scale with the square.
        public int ExportSupersample = 1;

        //Physics warm-up: plays the animation (/ first animation in the sequence) through
        //this many extra times before recording starts without capturing. Physics reset
        //whenever an animation loads, so frame 0 has a twitch each time the exported
        //WebP/WebM loops. A warm-up lets the sim settle first. 0 = disabled.
        public int PrerollLoops = 1;

        //Physics convergence: an animation export records the hair cloth pose at its first
        //frame and blends back to it over the last min(0.25s, clip length / 4), so a looping
        //clip does not jump when it wraps. Independent of the warm-up above.
        public bool PhysicsConverge = true;

        //--- Material editor
        //Whether the editor may specialise the ubershader.
        public bool UseSplicer = false;

        //--- Capture-panel selections (persisted so they stick between runs)
        public int CaptureResIndex = 2; //index into the resolution dropdown
        public int ExportFormat = 0; //0 PNG, 1 MP4, 2 WebP, 3 WebM
        public int ExportFps = 60;
        public int AnimMode = 0; //0 Single, 1 Sequence

        public PlayerConfig Player = new();

        static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

        //Pre-AppData location, next to the exe
        static string LegacyFilePath =>
            Path.Combine(AppContext.BaseDirectory, "playerviewer_config.json");

        //Writes are coalesced: Save() only marks the config dirty, and the actual file write
        //happens at most this often. Slider callbacks call Save() every frame while dragging.
        static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

        bool _dirty;
        readonly Stopwatch _sinceWrite = Stopwatch.StartNew();

        public static AppConfig Load()
        {
            var config = ReadFrom(FilePath);
            bool migrated = false;
            if (config == null && !File.Exists(FilePath))
            {
                config = ReadFrom(LegacyFilePath);
                migrated = config != null;
            }
            config ??= new AppConfig();
            config.Normalize();
            if (migrated)
            {
                config.WriteToDisk();
                Console.WriteLine($"[Config] Migrated settings from {LegacyFilePath}");
            }
            return config;
        }

        static AppConfig ReadFrom(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to load {path}: {ex.Message}");
            }
            return null;
        }

        //Clamps loaded values and replaces anything a hand-edited file left null.
        public void Normalize()
        {
            //Guard against corrupt/zero sizes (e.g. saved while minimized).
            if (WindowWidth < 200)
                WindowWidth = 1600;
            if (WindowHeight < 200)
                WindowHeight = 900;
            //Multiplies the render target, so a hand-edited value has to stay in range.
            ExportSupersample = System.Math.Clamp(ExportSupersample, 1, 8);
            Player ??= new PlayerConfig();
            Player.Normalize();
        }

        /// <summary>
        /// Marks the config as needing a write. Cheap enough to call from a per-frame ImGui
        /// change callback; <see cref="FlushPending"/> does the write.
        /// </summary>
        public void Save() => _dirty = true;

        /// <summary>Writes a pending change once the coalescing interval has elapsed.</summary>
        public void FlushPending()
        {
            if (_dirty && _sinceWrite.Elapsed >= FlushInterval)
                Flush();
        }

        /// <summary>Writes a pending change now. Called on shutdown so nothing is lost.</summary>
        public void Flush()
        {
            if (!_dirty)
                return;
            _dirty = false;
            _sinceWrite.Restart();
            WriteToDisk();
        }

        void WriteToDisk()
        {
            string temp = FilePath + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, null);
                else
                    File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to save: {ex.Message}");
                try
                {
                    File.Delete(temp);
                }
                catch { }
            }
        }
    }
}
