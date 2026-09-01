using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BfresEditor;
using ShaderBundler;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// Draws an edited material with the programs the editor generated for it, instead of
    /// leaving the mesh blank because the shipped archive has none.
    /// </summary>
    public sealed class LivePreview
    {
        enum ShownShaderKind
        {
            Own,
            Preview,
            Full,
        }

        sealed class Binding
        {
            public string Hash;
            public ShownShaderKind ShownShaderKind;
        }

        //A build that is assembled but not yet drawable. It goes through three phases before
        //the swap, so that the frame the archive changes has no work left in it:
        //
        //  1. the bytecode decompiles, on worker threads
        //  2. the GL programs, one per frame on the render thread: the ~1MB source read, the
        //     shader objects and the link
        //  3. polling those links to completion, which is also where the driver's uniform and
        //     attribute reflection happens
        //
        sealed class Pending
        {
            public FMAT Material;
            public string Hash;
            public ShownShaderKind ShownShaderKind;
            public BfshaLibrary.BfshaFile File;
            public BfshaLibrary.ShaderModel Model;
            public Task[] Prepare;
            public BfshaRenderer Warmer;
            public int Warmed;
            public bool Decompiled;
            public readonly List<BfresEditor.ShaderInfo> Programs = new();
        }

        readonly Dictionary<FMAT, Binding> _bound = new();
        Pending _pending;

        public string Error { get; private set; }

        public bool IsPreviewing(FMAT material) =>
            material != null
            && _bound.TryGetValue(material, out var b)
            && b.ShownShaderKind != ShownShaderKind.Own;

        /// <summary>Whether what is on screen for this material is the unguarded splice.</summary>
        public bool IsQuick(FMAT material) =>
            material != null
            && _bound.TryGetValue(material, out var b)
            && b.ShownShaderKind == ShownShaderKind.Preview;

        /// <summary>A better program is assembled and is being decompiled, built and linked.</summary>
        public bool Upgrading => _pending != null;

        /// <summary>Moves the build in flight along by one step. Called every frame, whether
        /// or not anything is selected, or a build would only finish while a material is.</summary>
        public void Advance(IReadOnlyList<BfresModelAsset> models) => FinishPending(models);

        /// <summary>
        /// Brings one material's binding up to date. Safe to call every frame: it returns
        /// immediately unless the key moved or a better shown became available. One build is
        /// in flight at a time, so a caller syncing several materials stops at the first that
        /// starts one.
        /// </summary>
        public void Sync(
            FMAT material,
            MaterialVariations variations,
            UberContext uber,
            string modelName,
            IReadOnlyList<BfresModelAsset> models
        )
        {
            FinishPending(models);

            if (material == null || variations == null || !variations.Ok || uber == null)
                return;

            var cells = variations.Cells.Where(c => c.Pass == PassPolicy.Material).ToList();
            if (cells.Count == 0)
                return;

            //Every shape drawn with this material shares an archive, so the material pass of
            //the first weight identifies the binding.
            string want = cells.All(c => c.Exists) ? null : cells[0].Vector.Hash;
            var drawn = variations.Cells.Where(c => DrawnPasses.IsDrawn(c.Pass)).ToList();

            //A build in flight for a key this material no longer has is abandoned.
            if (_pending != null && _pending.Material == material && _pending.Hash != want)
                _pending = null;

            //Each drawn cell takes its guarded splice when it has one and its quick one
            //otherwise; the whole is quick if any cell is.
            ShownShaderKind shown;
            if (want == null)
                shown = ShownShaderKind.Own;
            else if (drawn.All(c => c.Exists || c.State == CellState.Ready || c.PreviewReady))
                shown = drawn.Any(c => !c.Exists && c.State != CellState.Ready)
                    ? ShownShaderKind.Preview
                    : ShownShaderKind.Full;
            else
                return;

            _bound.TryGetValue(material, out var have);
            if (have != null && have.Hash == want && have.ShownShaderKind == shown)
                return;
            //A material that has always drawn from its own archive has nothing to put back.
            if (have == null && shown == ShownShaderKind.Own)
            {
                _bound[material] = new Binding { ShownShaderKind = ShownShaderKind.Own };
                return;
            }
            if (
                _pending != null
                && _pending.Material == material
                && _pending.Hash == want
                && _pending.ShownShaderKind == shown
            )
                return;

            if (shown == ShownShaderKind.Own)
            {
                Rebind(material, null, null, models);
                _bound[material] = new Binding
                {
                    Hash = null,
                    ShownShaderKind = ShownShaderKind.Own,
                };
                if (_pending?.Material == material)
                    _pending = null;
                Error = null;
                return;
            }

            var bytes = ModelBundle.BuildPreview(
                material,
                variations,
                uber,
                modelName,
                allowQuick: shown == ShownShaderKind.Preview,
                out _,
                out string error
            );
            if (bytes == null)
            {
                Error = error;
                return;
            }

            try
            {
                var file = new BfshaLibrary.BfshaFile(new MemoryStream(bytes));
                var model = file.ShaderModels.Values.FirstOrDefault(x =>
                    x.Name == material.ShaderModel
                );
                if (model == null)
                {
                    Error = $"the generated archive has no '{material.ShaderModel}'";
                    return;
                }

                //Decompile off the render thread. FinishPending then builds and links the GL
                //programs before the swap, so the rebind is a cache read and the mesh neither
                //goes blank nor hitches between the two.
                var prepare = new Task[model.ProgramCount];
                for (int i = 0; i < model.ProgramCount; i++)
                    prepare[i] = TegraShaderDecoder.PrepareShaderAsync(
                        model.GetShaderVariation(model.GetShaderProgram(i))
                    );

                _pending = new Pending
                {
                    Material = material,
                    Hash = want,
                    ShownShaderKind = shown,
                    File = file,
                    Model = model,
                    Prepare = prepare,
                    Warmer = FindRenderer(material, models),
                };
                SpliceTrace.Log($"assembled {material.Name} ({shown})");
                SpliceTrace.Note(
                    $"{material.Name} assembled a {(shown == ShownShaderKind.Preview ? "quick" : "guarded")} "
                        + $"archive ({bytes.Length:N0} bytes, {model.ProgramCount} program(s))"
                );
                Error = null;
                FinishPending(models);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Console.WriteLine($"[Preview] {material.Name} failed: {ex}");
            }
        }

        void FinishPending(IReadOnlyList<BfresModelAsset> models)
        {
            if (_pending == null)
                return;
            foreach (var t in _pending.Prepare)
                if (t != null && !t.IsCompleted)
                    return;
            if (!_pending.Decompiled)
            {
                _pending.Decompiled = true;
                SpliceTrace.Log($"decompiled {_pending.Material.Name}");
            }

            //One program per call, so building them is spread over frames
            if (_pending.Warmer != null && _pending.Warmed < _pending.Model.ProgramCount)
            {
                try
                {
                    _pending.Programs.Add(
                        _pending.Warmer.PrewarmProgram(
                            _pending.Model,
                            _pending.Model.GetShaderProgram(_pending.Warmed)
                        )
                    );
                }
                catch (Exception ex)
                {
                    //A program that will not build is not worth blocking the swap on: the
                    //rebind falls back to compiling it the old way, hitch and all.
                    Console.WriteLine($"[Preview] prewarm failed: {ex.Message}");
                }
                _pending.Warmed++;
                SpliceTrace.Log($"prewarmed program {_pending.Warmed}");
                return;
            }

            foreach (var info in _pending.Programs)
                if (info?.Program != null && info.Program.IsPending && !info.Program.PollReady())
                    return;

            var p = _pending;
            _pending = null;
            Rebind(p.Material, p.File, p.Model, models);
            _bound[p.Material] = new Binding { Hash = p.Hash, ShownShaderKind = p.ShownShaderKind };
            SpliceTrace.Log($"drawn {p.Material.Name} ({p.ShownShaderKind})");
            SpliceTrace.Note(
                $"{p.Material.Name} drawn from the "
                    + (p.ShownShaderKind == ShownShaderKind.Preview ? "quick" : "guarded")
                    + " archive"
            );
        }

        /// <summary>Hands every material back to the archive it was probed onto.</summary>
        public void Reset(IReadOnlyList<BfresModelAsset> models)
        {
            if (models != null)
                foreach (var entry in _bound)
                    if (entry.Value.ShownShaderKind != ShownShaderKind.Own)
                        Rebind(entry.Key, null, null, models);
            _bound.Clear();
            _pending = null;
            Error = null;
        }

        static BfshaRenderer FindRenderer(FMAT material, IReadOnlyList<BfresModelAsset> models)
        {
            foreach (var asset in models)
            foreach (var mesh in asset.Meshes)
                if (mesh.Shape.Material == material && mesh.MaterialAsset is BfshaRenderer r)
                    return r;
            return null;
        }

        static void Rebind(
            FMAT material,
            BfshaLibrary.BfshaFile file,
            BfshaLibrary.ShaderModel model,
            IReadOnlyList<BfresModelAsset> models
        )
        {
            foreach (var asset in models)
            foreach (var mesh in asset.Meshes)
            {
                if (mesh.Shape.Material != material)
                    continue;
                if (mesh.MaterialAsset is BfshaRenderer renderer)
                    renderer.RebindArchive(file, model, mesh);
            }
        }
    }
}
