using BfresLibrary.Core;
using BfresLibrary.Switch.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BfresLibrary.Switch
{
    public class MaterialParserV10
    {
        /* Order the used (non-default) slots by the choice position they had in
           the originally loaded encoding. Slots without a hint (newly assigned
           values) keep their dict order and go last. */
        private static List<int> OrderByHint(List<int> usedSlots, Func<int, int> hint)
        {
            return usedSlots
                .Select((slot, seq) => (slot, seq, h: hint(slot)))
                .OrderBy(x => x.h >= 0 ? 0 : 1)
                .ThenBy(x => x.h >= 0 ? x.h : x.seq)
                .Select(x => x.slot)
                .ToList();
        }

        /* Option name to whether the loaded table encoded it as a toggle. An option the
           source left unassigned has no class. */
        static Dictionary<string, bool> HintClasses(ShaderInfo hints)
        {
            var options = hints?.ShaderAssign?.Options;
            if (options == null)
                return null;
            var classes = new Dictionary<string, bool>();
            for (int i = 0; i < options.Count; i++)
            {
                int idx = HintIndex(hints, i);
                if (idx != -1)
                    classes[options.GetKey(i)] = idx < hints.RawBooleanCount;
            }
            return classes;
        }

        /* Option name to its position in the loaded choice tables, which keeps a resave
           laying the tables out as the file had them. */
        static Dictionary<string, int> HintOrder(ShaderInfo hints)
        {
            var options = hints?.ShaderAssign?.Options;
            if (options == null)
                return null;
            var order = new Dictionary<string, int>();
            for (int i = 0; i < options.Count; i++)
            {
                int idx = HintIndex(hints, i);
                if (idx != -1)
                    order[options.GetKey(i)] = idx;
            }
            return order;
        }

        //A file with no index table maps every option to its own position.
        static int HintIndex(ShaderInfo hints, int i)
        {
            var table = hints.OptionIndices;
            if (table == null || table.Length == 0)
                return i;
            return i < table.Length ? table[i] : -1;
        }

        public static void PrepareSave(Material mat)
        {
            var hints = mat.ShaderInfoV10;

            var info = new ShaderInfo();

            info.ShaderAssign = new ShaderAssignV10();
            info.ShaderAssign.ParentMaterial = mat;
            info.ShaderAssign.ShaderArchiveName = mat.ShaderAssign.ShaderArchiveName;
            info.ShaderAssign.ShadingModelName = mat.ShaderAssign.ShadingModelName;
            info.ShaderAssign.ParamCount = (ushort)mat.ShaderParams.Count;
            info.ShaderAssign.RenderInfoCount = (ushort)mat.RenderInfos.Count;
            info.SamplerAssigns = new List<string>();
            info.AttribAssigns = new List<string>();
            info.OptionValues = new List<string>();

            if (mat.ShaderAssign != null)
            {
                // ---- sampler assigns ----
                {
                    var values = mat.ShaderAssign.SamplerAssigns.Values.Select(v => (string)v).ToList();
                    sbyte[] rawHints = hints?.SamplerAssignIndices;
                    if (rawHints != null && rawHints.Length != values.Count) rawHints = null;

                    var used = Enumerable.Range(0, values.Count).Where(i => values[i] != "<Default Value>").ToList();
                    var order = OrderByHint(used, i => rawHints != null ? rawHints[i] : -1);

                    var indices = Enumerable.Repeat((sbyte)-1, values.Count).ToArray();
                    for (int j = 0; j < order.Count; j++)
                    {
                        info.SamplerAssigns.Add(values[order[j]]);
                        indices[order[j]] = (sbyte)j;
                    }
                    /* files omit the index table when it would be the identity map;
                       mirror that (a loaded hint table means the file had one) */
                    if (rawHints != null || indices.Any(x => x == -1))
                        info.SamplerAssignIndices = indices;
                }

                // ---- attribute assigns ----
                {
                    var values = mat.ShaderAssign.AttribAssigns.Values.Select(v => (string)v).ToList();
                    sbyte[] rawHints = hints?.AttributeAssignIndices;
                    if (rawHints != null && rawHints.Length != values.Count) rawHints = null;

                    var used = Enumerable.Range(0, values.Count).Where(i => values[i] != "<Default Value>").ToList();
                    var order = OrderByHint(used, i => rawHints != null ? rawHints[i] : -1);

                    var indices = Enumerable.Repeat((sbyte)-1, values.Count).ToArray();
                    for (int j = 0; j < order.Count; j++)
                    {
                        info.AttribAssigns.Add(values[order[j]]);
                        indices[order[j]] = (sbyte)j;
                    }
                    if (rawHints != null || indices.Any(x => x == -1))
                        info.AttributeAssignIndices = indices;
                }

                // ---- shader options ----
                {
                    var keys = mat.ShaderAssign.ShaderOptions.Keys.ToList();
                    var values = mat.ShaderAssign.ShaderOptions.Values.Select(v => (string)v).ToList();

                    var hintClass = HintClasses(hints);
                    var hintOrder = HintOrder(hints);

                    bool BoolLike(string v) => v == "True" || v == "1" || v == "False" || v == "0";
                    bool IsToggle(int i)
                    {
                        bool boolLike = BoolLike(values[i]);
                        if (hintClass != null && hintClass.TryGetValue(keys[i], out bool toggle))
                            return toggle && boolLike;
                        return boolLike;
                    }
                    int Hint(int i) => hintOrder != null && hintOrder.TryGetValue(keys[i], out int idx) ? idx : -1;

                    var used = Enumerable.Range(0, values.Count).Where(i => values[i] != "<Default Value>").ToList();
                    var toggleSlots = OrderByHint(used.Where(IsToggle).ToList(), Hint);
                    var stringSlots = OrderByHint(used.Where(i => !IsToggle(i)).ToList(), Hint);

                    var toggles = new List<bool>();
                    var strings = new List<string>();
                    var indices = Enumerable.Repeat((short)-1, values.Count).ToArray();
                    for (int j = 0; j < toggleSlots.Count; j++)
                    {
                        toggles.Add(values[toggleSlots[j]] == "1" || values[toggleSlots[j]] == "True");
                        indices[toggleSlots[j]] = (short)j;
                    }
                    for (int j = 0; j < stringSlots.Count; j++)
                    {
                        strings.Add(values[stringSlots[j]]);
                        indices[stringSlots[j]] = (short)(toggleSlots.Count + j);
                    }

                    info.OptionToggles = toggles.ToArray();
                    info.OptionValues = strings;
                    info.OptionIndices = indices;
                    /* keep the classifier valid if this regenerated info is used as
                       the hint source for a later save/clone */
                    info.RawBooleanCount = toggles.Count;
                }

                //Dicts
                foreach (var sampler in mat.ShaderAssign.SamplerAssigns)
                    info.ShaderAssign.SamplerAssign.Add(sampler.Key, sampler.Value);
                foreach (var att in mat.ShaderAssign.AttribAssigns)
                    info.ShaderAssign.AttributeAssign.Add(att.Key, att.Value);
                foreach (var op in mat.ShaderAssign.ShaderOptions)
                    info.ShaderAssign.Options.Add(op.Key, op.Value);
            }

            /* NOTE: unlike older versions of this code we keep the render info
               dict order exactly as authored; only the raw data table is written
               grouped by type (strings first), which is handled in Save(). */

            mat.ShaderInfoV10 = info;
        }

        public static void Load(ResFileSwitchLoader loader, Material mat)
        {
            //V10 changes quite alot....

            //First change is a new struct with shader assign + tables for shader assign data
            var info = loader.Load<ShaderInfo>();
            long TextureArrayOffset = loader.ReadInt64();
            long TextureNameArray = loader.ReadInt64();
            long SamplerArrayOffset = loader.ReadInt64();
            mat.Samplers = loader.LoadDictValues<Sampler>();
            //Next is table data
            long renderInfoDataTable = loader.ReadInt64();
            long renderInfoCounterTable = loader.ReadInt64();
            long renderInfoDataOffsets = loader.ReadInt64(); //offsets as shorts
            long SourceParamOffset = loader.ReadInt64();
            long SourceParamIndices = loader.ReadInt64(); //0xFFFF a bunch per param. Set at runtime??
            loader.ReadUInt64(); //reserved
            mat.UserData = loader.LoadDictValues<UserData>();
            long VolatileFlagsOffset = loader.ReadInt64();
            long userPointer = loader.ReadInt64();
            long SamplerSlotArrayOffset = loader.ReadInt64();
            long TexSlotArrayOffset = loader.ReadInt64();
            ushort idx = loader.ReadUInt16();
            byte numSampler = loader.ReadByte();
            byte numTextureRef = loader.ReadByte();
            loader.ReadUInt16(); //reserved
            ushort numUserData = loader.ReadUInt16();
            ushort renderInfoDataSize = loader.ReadUInt16();
            ushort user_shading_model_option_ubo_size = loader.ReadUInt16(); //Set at runtime?
            loader.ReadUInt32(); //padding

            mat.RenderInfoSize = renderInfoDataSize;

            long pos = loader.Position;

            var textures = loader.LoadCustom(() => loader.LoadStrings(numTextureRef), (uint)TextureNameArray);

            mat.TextureRefs = new List<TextureRef>();
            if (textures != null)
            {
                foreach (var tex in textures)
                    mat.TextureRefs.Add(new TextureRef() { Name = tex });
            }

            //Add names to the value as switch does not store any
            foreach (var sampler in mat.Samplers)
                sampler.Value.Name = sampler.Key;

            mat.TextureSlotArray = loader.LoadCustom(() => loader.ReadInt64s(numTextureRef), (uint)SamplerSlotArrayOffset);
            mat.SamplerSlotArray = loader.LoadCustom(() => loader.ReadInt64s(numSampler), (uint)TexSlotArrayOffset);

            if (info != null && info.ShaderAssign != null)
            {
                mat.ShaderAssign = new ShaderAssign()
                {
                    ShaderArchiveName = info.ShaderAssign.ShaderArchiveName,
                    ShadingModelName = info.ShaderAssign.ShadingModelName,
                };
                mat.ShaderParamData = loader.LoadCustom(() => loader.ReadBytes(info.ShaderAssign.ShaderParamSize), (uint)SourceParamOffset);
                mat.ParamIndices = loader.LoadCustom(() => loader.ReadInt32s(info.ShaderAssign.ShaderParameters.Count), (uint)SourceParamIndices);

                ReadRenderInfo(loader, info, mat, renderInfoCounterTable, renderInfoDataOffsets, renderInfoDataTable);
                ReadShaderParams(loader, info, mat);

                LoadAttributeAssign(info, mat);
                LoadSamplerAssign(info, mat);
                LoadShaderOptions(info, mat);

                mat.ShaderInfoV10 = info;
            }

            loader.Seek(pos, SeekOrigin.Begin);
        }

        static void ReadRenderInfo(ResFileLoader loader, ShaderInfo info, Material mat,
            long renderInfoCounterTable, long renderInfoDataOffsets, long renderInfoDataTable)
        {
            for (int i = 0; i < info.ShaderAssign.RenderInfos.Count; i++)
            {
                RenderInfo renderInfo = new RenderInfo();

                //Info table
                loader.Seek((int)info.ShaderAssign.renderInfoListOffset + i * 16, SeekOrigin.Begin);
                renderInfo.Name = loader.LoadString(); //name offset
                renderInfo.Type = (RenderInfoType)loader.ReadByte();

                //Count table
                loader.Seek((int)renderInfoCounterTable + i * 2, SeekOrigin.Begin);
                ushort count = loader.ReadUInt16();

                //Offset table
                loader.Seek((int)renderInfoDataOffsets + i * 2, SeekOrigin.Begin);
                ushort dataOffset = loader.ReadUInt16();

                //Raw data table
                loader.Seek((int)renderInfoDataTable + dataOffset, SeekOrigin.Begin);
                renderInfo.ReadData(loader, renderInfo.Type, count);

                mat.RenderInfos.Add(renderInfo.Name, renderInfo);
            }
        }

        static void ReadShaderParams(ResFileLoader loader, ShaderInfo info, Material mat)
        {
            for (int i = 0; i < info.ShaderAssign.ShaderParameters.Count; i++)
            {
                ShaderParam param = new ShaderParam();

                loader.Seek((int)info.ShaderAssign.shaderParamOffset + i * 24, SeekOrigin.Begin);
                var pad0 = loader.ReadUInt64(); //padding
                param.Name = loader.LoadString(); //name offset
                param.DataOffset = loader.ReadUInt16(); //padding
                param.Type = (ShaderParamType)loader.ReadUInt16(); //type
                var pad2 = loader.ReadUInt32(); //padding

                mat.ShaderParams.Add(param.Name, param);
            }
        }

        static void LoadAttributeAssign(ShaderInfo info, Material mat)
        {
            for (int i = 0; i < info.ShaderAssign.AttributeAssign.Count; i++)
            {
                int idx = info.AttributeAssignIndices?.Length > 0 ? info.AttributeAssignIndices[i] : i;
                var value = idx == -1 ? "<Default Value>" : info.AttribAssigns[idx];
                var key = info.ShaderAssign.AttributeAssign.GetKey(i);

                mat.ShaderAssign.AttribAssigns.Add(key, value);
            }
        }

        static void LoadSamplerAssign(ShaderInfo info, Material mat)
        {
            for (int i = 0; i < info.ShaderAssign.SamplerAssign.Count; i++)
            {
                int idx = info.SamplerAssignIndices?.Length > 0 ? info.SamplerAssignIndices[i] : i;
                var value = idx == -1 ? "<Default Value>" : info.SamplerAssigns[idx];
                var key = info.ShaderAssign.SamplerAssign.GetKey(i);

                mat.ShaderAssign.SamplerAssigns.Add(key, value);
            }
        }

        static void LoadShaderOptions(ShaderInfo info, Material mat)
        {
            List<string> choices = new List<string>();
            for (int i = 0; i < info.OptionToggles.Length; i++)
                choices.Add(info.OptionToggles[i] ? "1" : "0");
            if (info.OptionValues != null)
                choices.AddRange(info.OptionValues);

            for (int i = 0; i < info.ShaderAssign.Options.Count; i++)
            {
                int idx = info.OptionIndices?.Length > 0 ? info.OptionIndices[i] : i;
                var value = idx == -1 ? "<Default Value>" : choices[idx];
                var key = info.ShaderAssign.Options.GetKey(i);

                mat.ShaderAssign.ShaderOptions.Add(key, value);
            }
        }

        public static void Save(ResFileSwitchSaver saver, Material mat)
        {
            ShaderInfo info = mat.ShaderInfoV10;

            //Calculate total buffer sizes and offsets
            int renderInfoDataSize = 0;
            foreach (var renderInfo in mat.RenderInfos.Values)
            {
                renderInfo.DataOffset = renderInfoDataSize;
                switch (renderInfo.Type)
                {
                    case RenderInfoType.String:
                        renderInfoDataSize += 8 * renderInfo.GetValueStrings().Length;
                        break;
                    case RenderInfoType.Single:
                        renderInfoDataSize += 4 * renderInfo.GetValueSingles().Length;
                        break;
                    default:
                        renderInfoDataSize += 4 * renderInfo.GetValueInt32s().Length;
                        break;
                }
            }
            //Adds alignment
            var alignment = 128;
            renderInfoDataSize += (-renderInfoDataSize % alignment + alignment) % alignment;
            renderInfoDataSize = Math.Max(renderInfoDataSize, mat.RenderInfoSize);

            saver.SaveRelocateEntryToSection(saver.Position, 12, 1, 0, ResFileSwitchSaver.Section1, "FMAT");

            var textureList = mat.TextureRefs.Select(x => x.Name).ToList();

            saver.SaveString(mat.Name);
            saver.Save(info);
            saver.SaveCustom(new long[mat.TextureRefs.Count], () => saver.Write(new long[mat.TextureRefs.Count]));
            saver.SaveCustom(textureList, () => saver.SaveStringsRelocated(textureList));
            saver.SaveCustom(new long[mat.Samplers.Count], () => saver.Write(new long[mat.Samplers.Count * 15
                ]));
            saver.SaveList(mat.Samplers.Values);
            saver.SaveDict(mat.Samplers);
            //Render info data. The dict keeps its authored order; the raw data
            //table is laid out grouped by type ([strings][floats][ints]) like
            //stock files, so the string pointers form one contiguous run for the
            //relocation entry. Per-entry offsets are recorded in DataOffset and
            //written to the offset table below.
            saver.SaveCustom(mat.RenderInfos, () =>
            {
                long pos = saver.Position;

                saver.Write(new byte[renderInfoDataSize]);
                saver.Seek(pos, SeekOrigin.Begin);

                var infoStrings = mat.RenderInfos.Values.Where(x => x.Type == RenderInfoType.String).ToList();
                if (infoStrings.Count > 0)
                {
                    int numStrings = infoStrings.Sum(x => x.GetValueStrings().Length);

                    saver.SaveRelocateEntryToSection(saver.Position, (uint)numStrings, 1, 0, ResFileSwitchSaver.Section1, "Render Info Strings V10");
                }
                long startpos = saver.Position;
                var dataOrdered = mat.RenderInfos.Values.Where(x => x.Type == RenderInfoType.String)
                    .Concat(mat.RenderInfos.Values.Where(x => x.Type == RenderInfoType.Single))
                    .Concat(mat.RenderInfos.Values.Where(x => x.Type == RenderInfoType.Int32));
                foreach (var renderInfo in dataOrdered)
                {
                    renderInfo.DataOffset = saver.Position - startpos;

                    switch (renderInfo.Type)
                    {
                        case RenderInfoType.String: renderInfo.SaveStrings(saver); break;
                        case RenderInfoType.Single: renderInfo.SaveFloats(saver); break;
                        default: renderInfo.SaveInts(saver); break;
                    }
                }
                saver.Seek(pos + renderInfoDataSize, SeekOrigin.Begin);
            });
            //Render info count
            saver.SaveCustom(new byte[renderInfoDataSize], () =>
            {
                foreach (var renderInfo in mat.RenderInfos.Values)
                {
                    switch (renderInfo.Type)
                    {
                        case RenderInfoType.String: saver.Write((ushort)renderInfo.GetValueStrings()?.Length); break;
                        case RenderInfoType.Single: saver.Write((ushort)renderInfo.GetValueSingles()?.Length); break;
                        default: saver.Write((ushort)renderInfo.GetValueInt32s()?.Length); break;
                    }
                }
            });
            //Render info offsets
            saver.SaveCustom(new uint[mat.RenderInfos.Count], () =>
            {
                foreach (var renderInfo in mat.RenderInfos.Values)
                    saver.Write((ushort)renderInfo.DataOffset);
            });
            //Shader params
            saver.SaveCustom(mat.ShaderParamData, () =>
            {
                saver.Write(mat.ShaderParamData);
                saver.Align(128);
            });
            saver.SaveCustom(mat.ParamIndices, () => saver.Write(mat.ParamIndices));
            saver.Write(0UL); //0

            saver.SaveRelocateEntryToSection(saver.Position, 3, 1, 0, ResFileSwitchSaver.Section1, "FMAT User Data");

            mat.PosUserDataMaterialOffset = saver.SaveOffset();
            mat.PosUserDataDictMaterialOffset = saver.SaveOffset();

            saver.SaveCustom(new byte[32], () => saver.Write(new byte[32])); //Volatile Flags?
            saver.Write(0UL); //userPointer?

            saver.SaveRelocateEntryToSection(saver.Position, 2, 1, 0, ResFileSwitchSaver.Section1, "Material texture slots");

            saver.SaveCustom(mat.SamplerSlotArray, () => saver.Write(mat.SamplerSlotArray));
            saver.SaveCustom(mat.TextureSlotArray, () => saver.Write(mat.TextureSlotArray));
            saver.Write((ushort)saver.CurrentIndex);
            saver.Write((byte)mat.TextureRefs.Count);
            saver.Write((byte)mat.Samplers.Count);
            saver.Write((ushort)0); //numShaderParamVolatile?
            saver.Write((ushort)mat.UserData.Count);
            saver.Write((ushort)renderInfoDataSize);

            saver.Write((ushort)0);
            saver.Write((ushort)0);
            saver.Write((ushort)0);
        }

        public class ShaderInfo : IResData
        {
            public ShaderAssignV10 ShaderAssign;

            public IList<string> AttribAssigns;
            public IList<string> SamplerAssigns;

            public bool[] OptionToggles;
            public IList<string> OptionValues;

            public short[] OptionIndices;
            public sbyte[] AttributeAssignIndices;
            public sbyte[] SamplerAssignIndices;

            private long[] _optionBitFlags;

            // Raw file offsets for binary patching
            public long RawToggleOffset;
            public long RawIndicesOffset;
            public int RawBooleanCount;
            public int RawChoiceCount;
            public int RawNumBitFlags;
            public long RawHeaderCountsOffset; // file offset of the 4 bytes: numAttrAssign, numSampAssign
            public long RawBoolCountOffset;   // file offset of shaderOptionBooleanCount (2 bytes)
            public long RawChoiceCountOffset; // file offset of shaderOptionChoiceCount (2 bytes)
            public int RawTotalOptionsCount;  // total options in dict

            void IResData.Load(ResFileLoader loader)
            {
                ShaderAssign = loader.Load<ShaderAssignV10>();
                long attribAssignOffset = loader.ReadInt64();
                long attribAssignIndicesOffset = loader.ReadInt64();
                long samplerAssignOffset = loader.ReadInt64();
                long samplerAssignIndicesOffset = loader.ReadInt64();
                ulong optionChoiceToggleOffset = loader.ReadUInt64();
                ulong optionChoiceStringsOffset = loader.ReadUInt64();
                long optionChoiceIndicesOffset = loader.ReadInt64();
                loader.ReadUInt32(); //padding
                RawHeaderCountsOffset = loader.Position;
                byte numAttributeAssign = loader.ReadByte();
                byte numSamplerAssign = loader.ReadByte();
                RawBoolCountOffset = loader.Position;
                ushort shaderOptionBooleanCount = loader.ReadUInt16();
                RawChoiceCountOffset = loader.Position;
                ushort shaderOptionChoiceCount = loader.ReadUInt16();
                loader.ReadUInt16(); //padding
                loader.ReadUInt32(); //padding

                RawToggleOffset = (long)optionChoiceToggleOffset;
                RawIndicesOffset = optionChoiceIndicesOffset;
                RawBooleanCount = shaderOptionBooleanCount;
                RawChoiceCount = shaderOptionChoiceCount;
                RawTotalOptionsCount = ShaderAssign?.Options?.Count ?? 0;

                var numBitflags = 1 + shaderOptionBooleanCount / 64;
                RawNumBitFlags = numBitflags;

                AttribAssigns = loader.LoadCustom(() => loader.LoadStrings(numAttributeAssign), (uint)attribAssignOffset);
                SamplerAssigns = loader.LoadCustom(() => loader.LoadStrings(numSamplerAssign), (uint)samplerAssignOffset);
                _optionBitFlags = loader.LoadCustom(() => loader.ReadInt64s(numBitflags), (uint)optionChoiceToggleOffset);

                if (ShaderAssign  != null)
                {
                    OptionIndices = ReadShortIndices(loader, optionChoiceIndicesOffset, shaderOptionChoiceCount, ShaderAssign.Options.Count);
                    AttributeAssignIndices = ReadByteIndices(loader, attribAssignIndicesOffset, numAttributeAssign, ShaderAssign.AttributeAssign.Count);
                    SamplerAssignIndices = ReadByteIndices(loader, samplerAssignIndicesOffset, numSamplerAssign, ShaderAssign.SamplerAssign.Count);

                    var numChoiceValues = shaderOptionChoiceCount - shaderOptionBooleanCount;
                    OptionValues = loader.LoadCustom(() => loader.LoadStrings((int)numChoiceValues), (uint)optionChoiceStringsOffset);

                    SetupOptionBooleans(shaderOptionBooleanCount);
                }
            }

            void IResData.Save(ResFileSaver saver)
            {
                CreateOptionFlag();

                ((ResFileSwitchSaver)saver).SaveRelocateEntryToSection(saver.Position, 8, 1, 0, ResFileSwitchSaver.Section1, "ShaderInfo");

                saver.Save(ShaderAssign);
                saver.SaveCustom(AttribAssigns, () => ((ResFileSwitchSaver)saver).SaveStringsRelocated(AttribAssigns));
                saver.SaveCustom(AttributeAssignIndices, () => WriteIndices(saver, AttributeAssignIndices));
                saver.SaveCustom(SamplerAssigns, () => ((ResFileSwitchSaver)saver).SaveStringsRelocated(SamplerAssigns));
                saver.SaveCustom(SamplerAssignIndices, () => WriteIndices(saver, SamplerAssignIndices));
                saver.SaveCustom(OptionToggles, () => saver.Write(_optionBitFlags));
                saver.SaveCustom(OptionValues, () => ((ResFileSwitchSaver)saver).SaveStringsRelocated(OptionValues));
                saver.SaveCustom(OptionIndices, () => WriteIndices(saver, OptionIndices));
                saver.Write(0); //padding
                saver.Write((byte)AttribAssigns?.Count);
                saver.Write((byte)SamplerAssigns?.Count);
                saver.Write((ushort)OptionToggles?.Length);
                saver.Write((ushort)(OptionToggles?.Length + OptionValues?.Count));
                saver.Write(new byte[6]); //padding
            }

            private void CreateOptionFlag()
            {
                var numBitflags = 1 + OptionToggles.Length / 64;
                _optionBitFlags = new long[numBitflags];

                int idx = 0;
                for (int i = 0; i < OptionToggles.Length; i++)
                {
                    if (i != 0 && i % 64 == 0)
                        idx++;

                    if (OptionToggles[i])
                        _optionBitFlags[idx] |= ((long)1 << i);
                }
            }

            private sbyte[] ReadByteIndices(ResFileLoader loader, long offset, int usedCount, int totalCount)
            {
                if (offset == 0)
                    return null;

                using (loader.TemporarySeek((int)offset, SeekOrigin.Begin))
                {
                    var usedIndices = loader.ReadSBytes(usedCount);
                    return loader.ReadSBytes(totalCount);
                }
            }

            private short[] ReadShortIndices(ResFileLoader loader, long offset, int usedCount, int totalCount)
            {
                if (offset == 0)
                    return null;

                using (loader.TemporarySeek((int)offset, SeekOrigin.Begin))
                {
                    var usedIndices = loader.ReadInt16s(usedCount);
                    return loader.ReadInt16s(totalCount);
                }
            }

            private void SetupOptionBooleans(int count)
            {
                OptionToggles = new bool[count];

                if (count == 0) return;

                var flags = _optionBitFlags.ToArray();
                int idx = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i != 0 && i % 64 == 0)
                        idx++;

                    OptionToggles[i] = (_optionBitFlags[idx] & ((long)1 << i)) != 0;
                }
                for (int i = 0; i < _optionBitFlags.Length; i++)
                    if (_optionBitFlags[i] != flags[i])
                        throw new Exception();
            }

            /* Layout: [usedCount entries: choice -> option slot][totalCount
               entries: option slot -> choice]. The prefix is the inverse of the
               per-slot table, NOT simply ascending slot numbers (choice order can
               differ from slot order in stock files). */
            private void WriteIndices(ResFileSaver saver, short[] indices)
            {
                int usedCount = indices.Count(x => x != -1);
                short[] prefix = new short[usedCount];
                for (short i = 0; i < indices.Length; i++)
                {
                    if (indices[i] != -1)
                        prefix[indices[i]] = i;
                }
                saver.Write(prefix);
                saver.Write(indices);
                saver.Align(8);
            }

            private void WriteIndices(ResFileSaver saver, sbyte[] indices)
            {
                int usedCount = indices.Count(x => x != -1);
                sbyte[] prefix = new sbyte[usedCount];
                for (sbyte i = 0; i < indices.Length; i++)
                {
                    if (indices[i] != -1)
                        prefix[indices[i]] = i;
                }
                saver.Write(prefix);
                saver.Write(indices);
                saver.Align(8);
            }
        }

        public class ShaderAssignV10 : IResData
        {
            public ResDict<ResString> RenderInfos = new ResDict<ResString>();
            public ResDict<ResString> ShaderParameters = new ResDict<ResString>();
            public ResDict<ResString> AttributeAssign = new ResDict<ResString>();
            public ResDict<ResString> SamplerAssign = new ResDict<ResString>();
            public ResDict<ResString> Options = new ResDict<ResString>();

            public string ShaderArchiveName;
            public string ShadingModelName;

            internal ulong shaderParamOffset;
            internal ulong renderInfoListOffset;

            public ushort ShaderParamSize;

            public ushort RenderInfoCount;
            public ushort ParamCount;

            public Material ParentMaterial;

            void IResData.Load(ResFileLoader loader)
            {
                ShaderArchiveName = loader.LoadString();
                ShadingModelName = loader.LoadString();

                //List of names + type. Data in material section
                renderInfoListOffset = loader.ReadUInt64();
                RenderInfos = loader.LoadDict<ResString>();
                //List of names + type. Data in material section
                shaderParamOffset = loader.ReadUInt64();
                ShaderParameters = loader.LoadDict<ResString>();
                AttributeAssign = loader.LoadDict<ResString>();
                SamplerAssign = loader.LoadDict<ResString>();
                Options = loader.LoadDict<ResString>();
                RenderInfoCount = loader.ReadUInt16(); //render info count
                ParamCount = loader.ReadUInt16(); //param count
                ShaderParamSize = loader.ReadUInt16();
                loader.ReadUInt16(); //padding
                loader.ReadUInt64(); //padding
            }

            void IResData.Save(ResFileSaver saver)
            {
                ((ResFileSwitchSaver)saver).SaveRelocateEntryToSection(saver.Position, 9, 1, 0, ResFileSwitchSaver.Section1, "ShaderAssignV10");

                saver.SaveString(ShaderArchiveName);
                saver.SaveString(ShadingModelName);
                saver.SaveCustom(new long[ParentMaterial.RenderInfos.Count], () =>
                {
                    ((ResFileSwitchSaver)saver).SaveRelocateEntryToSection(saver.Position, 1, (uint)ParentMaterial.RenderInfos.Count, 1, ResFileSwitchSaver.Section1, "Render Param Info V10");

                    foreach (var renderInfo in ParentMaterial.RenderInfos.Values)
                    {
                        saver.SaveString(renderInfo.Name);
                        saver.Write((byte)renderInfo.Type);
                        saver.Write(new byte[7]);
                    }
                });
                saver.SaveDict(ParentMaterial.RenderInfos);
                saver.SaveCustom(new long[ParentMaterial.ShaderParams.Count], () =>
                {
                    ((ResFileSwitchSaver)saver).SaveRelocateEntryToSection(saver.Position, 2, (uint)ParentMaterial.ShaderParams.Count, 1, ResFileSwitchSaver.Section1, "Shader Param Info V10");

                    foreach (var param in ParentMaterial.ShaderParams.Values)
                    {
                        saver.Write(new byte[8]);
                        saver.SaveString(param.Name);
                        saver.Write((ushort)param.DataOffset);
                        saver.Write((ushort)param.Type);
                        saver.Write(new byte[4]);
                    }
                });
                saver.SaveDict(ParentMaterial.ShaderParams);
                saver.SaveDict(AttributeAssign);
                saver.SaveDict(SamplerAssign);
                saver.SaveDict(Options);
                saver.Write((ushort)ParentMaterial.RenderInfos.Count);
                saver.Write((ushort)ParentMaterial.ShaderParams.Count);
                saver.Write((ushort)ParentMaterial.ShaderParamData.Length);
                saver.Write((ushort)0);//padding
                saver.Write(0UL);//padding
            }

            public override int GetHashCode()
            {
                int hash = 17;
                Mix(ref hash, ShaderArchiveName.GetHashCode());
                Mix(ref hash, ShadingModelName.GetHashCode());

                foreach (var renderInfo in ParentMaterial.RenderInfos.Values)
                {
                    Mix(ref hash, renderInfo.Name.GetHashCode());
                    Mix(ref hash, renderInfo.Type.GetHashCode());
                }
                foreach (var p in ParentMaterial.ShaderParams.Values)
                {
                    Mix(ref hash, p.Name.GetHashCode());
                    Mix(ref hash, p.DataOffset.GetHashCode());
                    Mix(ref hash, p.Type.GetHashCode());
                }
                foreach (var name in Options.Keys)
                    Mix(ref hash, name.GetHashCode());
                foreach (var name in AttributeAssign.Keys)
                    Mix(ref hash, name.GetHashCode());
                foreach (var name in SamplerAssign.Keys)
                    Mix(ref hash, name.GetHashCode());

                return hash;
            }

            static void Mix(ref int hash, int value)
            {
                unchecked { hash = hash * 31 + value; }
            }

            /// <summary>
            /// Whether another block can stand in for this one: same names in the same order on
            /// every dict a material indexes positionally.
            /// </summary>
            public bool Matches(ShaderAssignV10 other)
            {
                if (other == null) return false;
                if (ReferenceEquals(this, other)) return true;

                return ShaderArchiveName == other.ShaderArchiveName
                    && ShadingModelName == other.ShadingModelName
                    && SameOrder(Options, other.Options)
                    && SameOrder(AttributeAssign, other.AttributeAssign)
                    && SameOrder(SamplerAssign, other.SamplerAssign)
                    && SameRenderInfos(other)
                    && SameShaderParams(other);
            }

            static bool SameOrder(ResDict<ResString> a, ResDict<ResString> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (a.GetKey(i) != b.GetKey(i)) return false;
                return true;
            }

            bool SameRenderInfos(ShaderAssignV10 other)
            {
                var a = ParentMaterial.RenderInfos;
                var b = other.ParentMaterial.RenderInfos;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (a[i].Name != b[i].Name || a[i].Type != b[i].Type) return false;
                return true;
            }

            bool SameShaderParams(ShaderAssignV10 other)
            {
                var a = ParentMaterial.ShaderParams;
                var b = other.ParentMaterial.ShaderParams;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (a[i].Name != b[i].Name || a[i].DataOffset != b[i].DataOffset
                        || a[i].Type != b[i].Type) return false;
                return true;
            }
        }
    }
}
