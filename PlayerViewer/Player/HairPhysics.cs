using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using Toolbox.Core;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Runtime hair cloth simulation driven by .bphcl data (one instance per cloth
    /// piece), based on decomp of havok clothes code in the game: particles
    /// are verlet-integrated in model space, fixed particles are pinned to the skinned
    /// animation pose, the constraint sets and the collision pass run in the authored
    /// execution order, and the driven bones take an orthonormal frame from their
    /// triangle. The long form of the kernels is in the MarinaHair research notes.
    /// </summary>
    public class HairPhysics
    {
        public bool Enabled = true;

        readonly HairClothPiece _piece;
        readonly STBone[] _bones; //cloth skeleton index -> scene bone
        readonly STBone[] _driven; //bones written by the deform (subset)

        readonly Vector3[] _pos;
        readonly Vector3[] _prev;
        readonly Vector3[] _before; //positions before the latest step, for the render blend
        readonly Vector3[] _render; //what the bones are written from
        readonly Vector3[] _skinned; //animation-pose skinned vertices
        readonly Matrix4[] _boneWorld; //current per-frame transform set
        readonly int[] _refVertex; //particle -> reference buffer vertex (-1: none)
        readonly bool[] _fixed;
        bool _primed;
        float _accumulator;
        float _lastDt; //the particle time step of the previous step
        float _transitionTime; //seconds since the release from the animation pose
        readonly Matrix4[] _colWorld; //collidable poses for this step
        readonly Matrix4[] _colPrevWorld; //and for the previous one, for their velocity
        bool _colPrevValid;

        //Snapshot of the sim at the first exported frame, and the target an export
        //converges back to so a looping clip does not jump when it wraps.
        Vector3[] _convergePos;
        Vector3[] _convergePrev;

        const float StepTime = 1.0f / 60.0f;
        const int MaxSteps = 4;
        //After a reset the game holds the hair at the animation pose and runs one second
        //at 30 calc per sec while the transition set releases it.
        const float WarmUpStep = 1.0f / 30.0f;
        const int WarmUpSteps = 30;

        //The game's bone record setup, per driven bone: the cloth bone it aims at (the
        //first child in skeleton order that the cloth knows), the quaternion that takes
        //that child's rest local direction onto the x axis, the cloth bone whose
        //position the segment length is kept from, and that length as the skeleton had
        //it this frame.
        readonly int[] _writeOrder; //driven bone indices in cloth bone order
        readonly int[] _aimChild; //cloth bone index or -1
        readonly Quaternion[] _aimQuat;
        readonly bool[] _aimAligned; //child sits on the x axis: aim x at it directly
        readonly bool[] _aimNegate;
        readonly int[] _lengthParent; //cloth bone index or -1
        readonly float[] _segmentLength;
        readonly Matrix4[] _rawFrame; //this frame's deform output per driven bone

        HairPhysics(HairClothPiece piece, STBone[] bones)
        {
            _piece = piece;
            _bones = bones;
            _driven = piece.BoneDeforms.Select(d => bones[d.BoneIndex]).ToArray();

            int nd = piece.BoneDeforms.Count;
            _writeOrder = Enumerable
                .Range(0, nd)
                .OrderBy(i => piece.BoneDeforms[i].BoneIndex)
                .ToArray();
            _aimChild = new int[nd];
            _aimQuat = new Quaternion[nd];
            _aimAligned = new bool[nd];
            _aimNegate = new bool[nd];
            _lengthParent = new int[nd];
            _segmentLength = new float[nd];
            _rawFrame = new Matrix4[nd];
            for (int i = 0; i < nd; i++)
                SetUpAim(i);

            int n = piece.Particles.Length;
            _pos = new Vector3[n];
            _prev = new Vector3[n];
            _before = new Vector3[n];
            _render = new Vector3[n];
            _skinned = new Vector3[piece.SkinVertices.Length];
            _boneWorld = new Matrix4[piece.BoneRefPose.Length];
            _fixed = new bool[n];
            _colWorld = new Matrix4[piece.Collidables.Count];
            _colPrevWorld = new Matrix4[piece.Collidables.Count];
            foreach (int f in piece.FixedParticles)
                if (f >= 0 && f < n)
                    _fixed[f] = true;

            //Particle -> reference-buffer vertex: identity where in range, then the
            //transition and local-range entries, then the MoveParticles pairs (fixed).
            _refVertex = new int[n];
            for (int p = 0; p < n; p++)
                _refVertex[p] = p < _skinned.Length ? p : -1;
            foreach (var t in piece.Transitions)
                if (t.Particle >= 0 && t.Particle < n)
                    _refVertex[t.Particle] = t.ReferenceVertex;
            foreach (var range in piece.LocalRanges)
                if (range.Particle >= 0 && range.Particle < n)
                    _refVertex[range.Particle] = range.ReferenceVertex;
            foreach (var (vertex, particle) in piece.VertexParticlePairs)
                if (particle >= 0 && particle < n)
                    _refVertex[particle] = vertex;
        }

        /// <summary>
        /// Binds a cloth piece to scene skeletons. Bones are resolved by name against
        /// the hair part skeleton first, then the human skeleton (Spine_3 etc. live
        /// there). Returns null when required bones are missing.
        /// </summary>
        public static HairPhysics Create(
            HairClothPiece piece,
            STSkeleton hairSkeleton,
            STSkeleton humanSkeleton
        )
        {
            if (piece.SkinVertices == null || piece.BoneDeforms.Count == 0)
                return null;

            var bones = new STBone[piece.BoneNames.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                string name = piece.BoneNames[i];
                //The hair model carries its own Spine_3 under Head_Root, unanimated. The
                //game overwrites its world matrix with the body's Spine_3 every frame, so
                //the chest capsule follows the spine, not the head.
                bones[i] =
                    name == "Spine_3"
                        ? humanSkeleton?.SearchBone(name) ?? hairSkeleton.SearchBone(name)
                        : hairSkeleton.SearchBone(name) ?? humanSkeleton?.SearchBone(name);
                if (bones[i] == null)
                    return null;
            }
            return new HairPhysics(piece, bones);
        }

        /// <summary>Restarts the sim from the current animation pose next update.</summary>
        public void Reset()
        {
            _primed = false;
            _colPrevValid = false;
            _lastDt = 0;
            _convergePos = null;
            _convergePrev = null;
        }

        /// <summary>
        /// Records the current particle state as the pose an export converges back to.
        /// Verlet carries velocity in the gap between the two buffers, so both are kept.
        /// </summary>
        public void CaptureConvergeState()
        {
            if (!_primed)
                return;
            _convergePos = (Vector3[])_pos.Clone();
            _convergePrev = (Vector3[])_prev.Clone();
        }

        /// <summary>Debug: dumps sim state (skinned targets vs particles, collidables).</summary>
        public void DebugDump()
        {
            Console.WriteLine($"[HairPhys] {_piece.Name}");
            Matrix4 headInv = Matrix4.Identity;
            HairCollidable head = _piece.Collidables.Count > 0 ? _piece.Collidables[0] : null;
            Vector3 ha = Vector3.Zero,
                hb = Vector3.Zero;
            if (head != null)
            {
                var hw = head.BoneOffset * _boneWorld[head.BoneIndex];
                headInv = Matrix4.Invert(hw);
                ha = Vector3.TransformPosition(head.Start, hw);
                hb = Vector3.TransformPosition(head.End, hw);
            }
            float AxisDist(Vector3 p)
            {
                Vector3 ab = hb - ha;
                float t =
                    ab.LengthSquared > 1e-9f
                        ? MathHelper.Clamp(Vector3.Dot(p - ha, ab) / ab.LengthSquared, 0, 1)
                        : 0;
                return (p - (ha + ab * t)).Length;
            }
            for (int p = 0; p < _pos.Length; p++)
            {
                Vector3 target = SkinnedForParticle(p);
                Vector3 l = Vector3.TransformPosition(_pos[p], headInv);
                Vector3 ls = Vector3.TransformPosition(target, headInv);
                Console.WriteLine(
                    $"  p{p}{(_fixed[p] ? " FIX" : "    ")} pos=({_pos[p].X:F3},{_pos[p].Y:F3},{_pos[p].Z:F3}) "
                        + $"skin=({target.X:F3},{target.Y:F3},{target.Z:F3}) drift={(_pos[p] - target).Length:F3} "
                        + $"headLocal=({l.X:F3},{l.Y:F3},{l.Z:F3}) skinLocal=({ls.X:F3},{ls.Y:F3},{ls.Z:F3}) "
                        + $"axis={AxisDist(_pos[p]):F3} skinAxis={AxisDist(target):F3}"
                );
            }
            for (int b = 0; b < _bones.Length; b++)
            {
                Vector3 t = _boneWorld[b].Row3.Xyz;
                Vector3 r = _boneWorld[0].Row3.Xyz;
                Console.WriteLine(
                    $"  clothBone {_piece.BoneNames[b]} fed=({t.X:F3},{t.Y:F3},{t.Z:F3}) fromRoot={(t - r).Length:F3}"
                );
            }
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
            {
                var bd = _piece.BoneDeforms[i];
                int bi = bd.BoneIndex;
                Matrix4 restFrame = DeformFrame(_piece.RestPositions, bd);
                Matrix4 restRef = _piece.BoneRefPose[bi];
                Matrix4 bind = Matrix4.Invert(_driven[i].Inverse);
                Matrix4 now = _driven[i].Transform;
                Matrix4 skel = _skeletonBefore != null ? _skeletonBefore[i] : now;
                Console.WriteLine(
                    $"  bone {_piece.BoneNames[bi]} tri=({_piece.TriangleIndices[bd.TriangleStart]},{_piece.TriangleIndices[bd.TriangleStart + 1]},{_piece.TriangleIndices[bd.TriangleStart + 2]})"
                );
                Console.WriteLine($"    restDeform {Fmt(restFrame)}");
                Console.WriteLine($"    clothRef   {Fmt(restRef)}");
                Console.WriteLine($"    sceneBind  {Fmt(bind)}");
                Console.WriteLine($"    skeleton   {Fmt(skel)}");
                Console.WriteLine($"    written    {Fmt(now)}");
            }
            foreach (var range in _piece.LocalRanges)
            {
                Vector3 c = _skinned[range.ReferenceVertex];
                float d = (_pos[range.Particle] - c).Length;
                Console.WriteLine(
                    $"  range p{range.Particle} ref=v{range.ReferenceVertex} r={range.Radius:F3} k={range.Stiffness:F3} dist={d:F3}"
                );
            }
            foreach (var col in _piece.Collidables)
            {
                var world = col.BoneOffset * _boneWorld[col.BoneIndex];
                Vector3 a = Vector3.TransformPosition(col.Start, world);
                Vector3 b = Vector3.TransformPosition(col.End, world);
                Console.WriteLine(
                    $"  {col.Shape} '{col.Name}' bone={_piece.BoneNames[col.BoneIndex]} r={col.Radius:F3} "
                        + $"a=({a.X:F3},{a.Y:F3},{a.Z:F3}) b=({b.X:F3},{b.Y:F3},{b.Z:F3})"
                );
            }
        }

        /// <summary>
        /// Runs after the hair part weld (bones hold the pure animation pose) and
        /// overwrites the driven bones with the simulated pose. Bones collapsed by
        /// the active hair-arrange (scale ~0 = hidden under headgear) are left at
        /// their welded transform.
        /// </summary>
        public void Update(
            float dt,
            Dictionary<string, ArrangeBoneParam> arrange = null,
            float convergeWeight = 0
        )
        {
            if (!Enabled)
                return;

            for (int i = 0; i < _bones.Length; i++)
                _boneWorld[i] = UnitAxes(_bones[i].Transform);
            //The segment lengths the write back keeps come from the skeleton pose of
            //this frame, before anything is written.
            for (int i = 0; i < _driven.Length; i++)
                _segmentLength[i] =
                    _lengthParent[i] >= 0
                        ? (
                            _driven[i].Transform.Row3.Xyz
                            - _bones[_lengthParent[i]].Transform.Row3.Xyz
                        ).Length
                        : 0;

            SkinVertices();

            if (!_primed)
            {
                //The game starts a cloth pinned to its animation pose, then lets the
                //transition set release it over one second of 30 Hz steps.
                for (int p = 0; p < _pos.Length; p++)
                    _pos[p] = _prev[p] = SkinnedForParticle(p);
                _transitionTime = 0;
                _lastDt = 0;
                _colPrevValid = false;
                _primed = true;
                for (int i = 0; i < WarmUpSteps; i++)
                    Step(WarmUpStep);
                Array.Copy(_pos, _before, _pos.Length);
                _accumulator = 0;
            }

            //The animation advances every rendered frame, so above 60 fps the bones are written
            //from the particles blended between the last two steps by the backlog fraction;
            //otherwise, the hair moves in jittery 60 Hz jumps against the head.
            _accumulator = Math.Min(_accumulator + dt, StepTime * MaxSteps);
            while (_accumulator >= StepTime)
            {
                Array.Copy(_pos, _before, _pos.Length);
                Step(StepTime);
                _accumulator -= StepTime;
            }
            float alpha = MathHelper.Clamp(_accumulator / StepTime, 0, 1);
            for (int p = 0; p < _pos.Length; p++)
                _render[p] = _before[p] + (_pos[p] - _before[p]) * alpha;

            //Blend toward the captured pose before the bones are written, so the frame
            //that gets rendered is the blended one.
            if (convergeWeight > 0 && _convergePos != null)
            {
                float w = MathHelper.Clamp(convergeWeight, 0, 1);
                for (int p = 0; p < _pos.Length; p++)
                {
                    _pos[p] += (_convergePos[p] - _pos[p]) * w;
                    _prev[p] += (_convergePrev[p] - _prev[p]) * w;
                    _render[p] = _pos[p];
                }
            }

            WriteBones(arrange);
        }

        /// <summary>
        /// The pose with its scale removed
        /// </summary>
        static Matrix4 UnitAxes(Matrix4 m)
        {
            Vector3 x = m.Row0.Xyz;
            if (x.LengthSquared < 1e-20f)
                return m;
            x.Normalize();
            Vector3 z = Vector3.Cross(x, m.Row1.Xyz);
            if (z.LengthSquared < 1e-20f)
                return m;
            z.Normalize();
            Vector3 y = Vector3.Cross(z, x);
            return new Matrix4(
                new Vector4(x, 0),
                new Vector4(y, 0),
                new Vector4(z, 0),
                m.Row3
            );
        }

        Vector3 SkinnedForParticle(int particle)
        {
            int vertex = _refVertex[particle];
            return vertex >= 0 && vertex < _skinned.Length ? _skinned[vertex] : _pos[particle];
        }

        void SkinVertices()
        {
            for (int vi = 0; vi < _piece.SkinVertices.Length; vi++)
            {
                var sv = _piece.SkinVertices[vi];
                if (sv == null)
                    continue;
                Vector3 result = Vector3.Zero;
                for (int b = 0; b < sv.Bones.Length; b++)
                {
                    if (sv.Weights[b] <= 0)
                        continue;
                    int subset = sv.Bones[b];
                    int bone =
                        subset < _piece.TransformSubset.Length
                            ? _piece.TransformSubset[subset]
                            : subset;
                    //Bone-space deformer: position is authored per blend slot in bone
                    //space. Object-space: shared position through boneFromSkinMesh.
                    Vector3 p = sv.LocalPosPerBone != null ? sv.LocalPosPerBone[b] : sv.LocalPos;
                    var mat =
                        sv.LocalPosPerBone != null
                            ? _boneWorld[bone]
                            : _piece.BoneFromSkinMesh[subset] * _boneWorld[bone];
                    result += sv.Weights[b] * Vector3.TransformPosition(p, mat);
                }
                _skinned[vi] = result;
            }
        }

        void Step(float dt)
        {
            var piece = _piece;
            int subSteps = Math.Clamp(piece.SubSteps, 1, 8);
            float subDt = dt / subSteps;

            //A changed step keeps the velocity, not the per step displacement.
            if (_lastDt > 0 && _lastDt != subDt)
            {
                float keep = 1.0f - subDt / _lastDt;
                for (int p = 0; p < _pos.Length; p++)
                    _prev[p] += (_pos[p] - _prev[p]) * keep;
            }
            _lastDt = subDt;

            for (int c = 0; c < piece.Collidables.Count; c++)
                _colWorld[c] =
                    piece.Collidables[c].BoneOffset * _boneWorld[piece.Collidables[c].BoneIndex];
            if (!_colPrevValid)
            {
                Array.Copy(_colWorld, _colPrevWorld, _colWorld.Length);
                _colPrevValid = true;
            }

            for (int s = 0; s < subSteps; s++)
            {
                //Fixed particles sit at their skinned positions with no velocity.
                foreach (int f in piece.FixedParticles)
                    _prev[f] = _pos[f] = SkinnedForParticle(f);

                //Verlet integration for dynamic particles.
                float d = piece.DampingPerSecond;
                float damping = d >= 1 ? 0 : d == 0 ? 1 : MathF.Pow(1.0f - d, subDt);
                Vector3 gravityStep = piece.Gravity * subDt * subDt;
                for (int p = 0; p < _pos.Length; p++)
                {
                    if (piece.Particles[p].InvMass <= 0 || _fixed[p])
                        continue;
                    Vector3 velocity = (_pos[p] - _prev[p]) * damping;
                    _prev[p] = _pos[p];
                    _pos[p] += velocity + gravityStep;
                }

                //The authored execution order, the authored number of times; -1 is the
                //collision pass in its place.
                for (int iter = 0; iter < Math.Clamp(piece.SolveIterations, 1, 8); iter++)
                    foreach (int setIndex in piece.ConstraintExecution)
                    {
                        if (setIndex < 0)
                            SolveCollisions(s, subSteps);
                        else
                            SolveConstraintSet(
                                setIndex < piece.ConstraintSetKinds.Count
                                    ? piece.ConstraintSetKinds[setIndex]
                                    : HairConstraintKind.Unknown
                            );
                    }

                _transitionTime += subDt;
            }
            Array.Copy(_colWorld, _colPrevWorld, _colWorld.Length);
        }

        void SolveConstraintSet(HairConstraintKind kind)
        {
            var piece = _piece;
            switch (kind)
            {
                case HairConstraintKind.Standard:
                    foreach (var link in piece.StandardLinks)
                        SolveStandard(link);
                    break;

                case HairConstraintKind.Stretch:
                    foreach (var link in piece.StretchLinks)
                        SolveStretch(link);
                    break;

                case HairConstraintKind.Bend:
                    foreach (var link in piece.BendLinks)
                        SolveBendLink(link);
                    break;

                case HairConstraintKind.LocalRange:
                    foreach (var range in piece.LocalRanges)
                        SolveLocalRange(range);
                    break;

                case HairConstraintKind.Transition:
                    SolveTransition();
                    break;

                case HairConstraintKind.BendStiffness:
                    foreach (var link in piece.BendStiffnessLinks)
                        SolveBendStiffness(link);
                    break;
            }
        }

        float InvMass(int p) => _fixed[p] ? 0 : _piece.Particles[p].InvMass;

        /// <summary>Two sided spring to the rest length: each end takes stiffness times its own inverse mass of the error.</summary>
        void SolveStandard(HairLink link)
        {
            Vector3 d = _pos[link.B] - _pos[link.A];
            float len = d.Length;
            if (len <= 0)
                return;
            Vector3 corr = d * (link.Stiffness * (len - link.RestLength) / len);
            _pos[link.A] += corr * InvMass(link.A);
            _pos[link.B] -= corr * InvMass(link.B);
        }

        /// <summary>One sided maximum length: only particle B moves, by stiffness times the excess, whatever the masses.</summary>
        void SolveStretch(HairLink link)
        {
            Vector3 d = _pos[link.B] - _pos[link.A];
            float len = d.Length;
            if (len <= 0)
                return;
            float s = link.Stiffness * Math.Min(link.RestLength - len, 0);
            _pos[link.B] += d * (s / len);
        }

        /// <summary>A [min, max] band on the chord, each side with its own stiffness.</summary>
        void SolveBendLink(HairLink link)
        {
            Vector3 d = _pos[link.B] - _pos[link.A];
            float len = d.Length;
            if (len <= 0)
                return;
            float s =
                Math.Max(0, len - link.MaxLength) * link.StretchStiffness
                - Math.Max(0, link.MinLength - len) * link.BendStiffness;
            Vector3 corr = d * (s / len);
            _pos[link.A] += corr * InvMass(link.A);
            _pos[link.B] -= corr * InvMass(link.B);
        }

        /// <summary>Pull toward the skinned reference by stiffness times the distance outside the sphere.</summary>
        void SolveLocalRange(HairLocalRange range)
        {
            if (_fixed[range.Particle])
                return;
            Vector3 center = _skinned[range.ReferenceVertex];
            Vector3 d = _pos[range.Particle] - center + new Vector3(float.Epsilon);
            float len = d.Length;
            if (len <= 0)
                return;
            float s = Math.Min(range.Stiffness * (range.Radius - len), 0);
            _pos[range.Particle] += d * (s / len);
        }

        /// <summary>
        /// The release from the animation pose after a reset: for the transition period
        /// each particle may sit at most (t - delay) / period of its maximum distance
        /// from its reference. Steady state does nothing.
        /// </summary>
        void SolveTransition()
        {
            float period = _piece.TransitionToSimPeriod;
            if (period <= 0)
                return;
            foreach (var t in _piece.Transitions)
            {
                if (_fixed[t.Particle] || t.ReferenceVertex >= _skinned.Length)
                    continue;
                float u = _transitionTime - t.ToSimDelay;
                if (u >= period)
                    break;
                Vector3 reference = _skinned[t.ReferenceVertex];
                if (u <= 0)
                {
                    _pos[t.Particle] = reference;
                    continue;
                }
                float maxDist = u / period * t.ToSimMaxDistance;
                Vector3 d = _pos[t.Particle] - reference;
                float len = d.Length;
                if (len > maxDist && len > 0)
                    _pos[t.Particle] = reference + d * (maxDist / len);
            }
        }

        /// <summary>
        /// Linear bending element over the four particles around a shared edge: the
        /// weighted sum of the positions (with the rest curvature added along the average
        /// normal in the rest pose variant) is applied back to each by its weight, inverse
        /// mass and the authored, possibly negative, stiffness.
        /// </summary>
        void SolveBendStiffness(HairBendStiffnessLink link)
        {
            var piece = _piece;
            int[] ps = link.Particles;
            float[] w = link.Weights;
            Vector3 v = Vector3.Zero;
            for (int i = 0; i < 4; i++)
                v += _pos[ps[i]] * w[i];
            float k = link.BendStiffness;
            if (piece.BendStiffnessUseRestPose)
            {
                Vector3 c = _pos[ps[2]];
                Vector3 dc = _pos[ps[3]] - c;
                Vector3 n1 = Vector3.Cross(dc, _pos[ps[0]] - c);
                Vector3 n2 = Vector3.Cross(_pos[ps[1]] - c, dc);
                float e2 = dc.LengthSquared;
                float l1 = n1.Length,
                    l2 = n2.Length;
                if (e2 <= 0 || l1 <= 0 || l2 <= 0)
                    return;
                float h = l1 * l2 / e2 * link.RestCurvature;
                if (piece.BendStiffnessClamp && h * h > piece.BendStiffnessMaxRestHeightSq)
                    k = 0;
                Vector3 avg = n1 / l1 + n2 / l2;
                if (avg.LengthSquared > 0)
                    v += avg.Normalized() * h;
            }
            for (int i = 0; i < 4; i++)
                _pos[ps[i]] += v * (InvMass(ps[i]) * w[i] * k);
        }

        /// <summary>
        /// The collision pass: every free particle is pushed to the surface of each
        /// enabled shape plus its own radius, and its previous position is moved so the
        /// contact is inelastic along the normal with the tangential velocity scaled by
        /// the particle's friction. Fixed particles never collide.
        /// </summary>
        void SolveCollisions(int subStep, int subSteps)
        {
            var piece = _piece;
            float fraction = (subStep + 1) / (float)subSteps;
            for (int pass = 0; pass < 2; pass++)
                for (int c = 0; c < piece.Collidables.Count; c++)
                {
                    if (
                        !piece.UseAllInstanceCollidables
                        && Array.IndexOf(piece.InstanceCollidablesUsed, c) < 0
                    )
                        continue;
                    var col = piece.Collidables[c];
                    if (pass == 1 && (!col.VirtualPoints || piece.VirtualPoints.Count == 0))
                        continue;
                    Matrix4 world = _colWorld[c];
                    Matrix4 old = _colPrevWorld[c];
                    Vector3 linDt = (world.Row3.Xyz - old.Row3.Xyz) / subSteps;
                    Vector3 angDt = RotationDelta(old, world) / subSteps;
                    world.Row3.Xyz = old.Row3.Xyz + linDt * subSteps * fraction;
                    var shape = new ShapePose(col, world);
                    if (!shape.Valid)
                        continue;
                    uint bit = c < 32 ? 1u << c : 0;

                    if (pass == 0)
                    {
                        for (int p = 0; p < _pos.Length; p++)
                        {
                            if (_fixed[p] || piece.Particles[p].InvMass <= 0)
                                continue;
                            if ((piece.Particles[p].CollisionMask & bit) == 0)
                                continue;
                            if (
                                shape.PushOut(
                                    _pos[p],
                                    piece.Particles[p].Radius,
                                    out Vector3 pushed,
                                    out Vector3 n,
                                    out Vector3 surface
                                )
                            )
                            {
                                _pos[p] = pushed;
                                Contact(p, n, surface, shape.Centre, linDt, angDt);
                            }
                        }
                    }
                    else
                    {
                        foreach (var vp in piece.VirtualPoints)
                        {
                            int o = vp.Owner;
                            if (
                                o < 0
                                || o >= _pos.Length
                                || vp.Opposite < 0
                                || vp.Opposite >= _pos.Length
                            )
                                continue;
                            if (_fixed[o] || piece.Particles[o].InvMass <= 0)
                                continue;
                            if ((piece.Particles[o].CollisionMask & bit) == 0)
                                continue;
                            Vector3 m = _pos[o] + (_pos[vp.Opposite] - _pos[o]) * vp.Barycentric;
                            if (
                                shape.PushOut(
                                    m,
                                    piece.Particles[o].Radius,
                                    out Vector3 pushed,
                                    out _,
                                    out _
                                )
                            )
                                _pos[o] += pushed - m;
                        }
                    }
                }
        }

        /// <summary>One collidable at one pose: closest point and push out for its shape.</summary>
        readonly struct ShapePose
        {
            readonly HairCollidableShape _shape;
            readonly Vector3 _a,
                _b,
                _normal;
            readonly float _radius,
                _planeOffset;
            public readonly Vector3 Centre;
            public readonly bool Valid;

            public ShapePose(HairCollidable col, Matrix4 world)
            {
                _shape = col.Shape;
                _radius = col.Radius;
                Centre = world.Row3.Xyz;
                _a = Vector3.TransformPosition(col.Start, world);
                _b = Vector3.TransformPosition(col.End, world);
                _normal = Vector3.Zero;
                _planeOffset = 0;
                Valid = true;
                if (col.Shape == HairCollidableShape.Plane)
                {
                    //The plane is authored as (normal, offset); carry it through the
                    //transform as a point on it and its normal.
                    Vector3 point = Vector3.TransformPosition(col.Start * -col.Radius, world);
                    _normal = Vector3.TransformNormal(col.Start, world);
                    Valid = _normal.LengthSquared > 0;
                    if (Valid)
                    {
                        _normal.Normalize();
                        _planeOffset = -Vector3.Dot(_normal, point);
                    }
                }
            }

            public bool PushOut(
                Vector3 p,
                float particleRadius,
                out Vector3 pushed,
                out Vector3 n,
                out Vector3 surface
            )
            {
                pushed = p;
                n = Vector3.Zero;
                surface = p;
                if (_shape == HairCollidableShape.Plane)
                {
                    float depth = Vector3.Dot(_normal, p) + _planeOffset - particleRadius;
                    if (depth >= 0)
                        return false;
                    n = _normal;
                    pushed = p - _normal * depth;
                    surface = pushed - _normal * particleRadius;
                    return true;
                }
                Vector3 closest = _a;
                if (_shape == HairCollidableShape.Capsule)
                {
                    Vector3 ab = _b - _a;
                    float t =
                        ab.LengthSquared > 1e-9f
                            ? MathHelper.Clamp(Vector3.Dot(p - _a, ab) / ab.LengthSquared, 0, 1)
                            : 0;
                    closest = _a + ab * t;
                }
                Vector3 delta = p - closest;
                float dist = delta.Length;
                if (dist >= _radius + particleRadius || dist <= 1e-7f)
                    return false;
                n = delta / dist;
                surface = closest + n * _radius;
                pushed = surface + n * particleRadius;
                return true;
            }
        }

        /// <summary>
        /// Rotation from one pose to the next as an angle times axis vector, for the
        /// row vector matrices the scene uses.
        /// </summary>
        static Vector3 RotationDelta(Matrix4 from, Matrix4 to)
        {
            Matrix3 a = new Matrix3(from);
            Matrix3 b = new Matrix3(to);
            a.Row0.Normalize();
            a.Row1.Normalize();
            a.Row2.Normalize();
            b.Row0.Normalize();
            b.Row1.Normalize();
            b.Row2.Normalize();
            a.Transpose();
            Matrix3 d = a * b;
            var axis = new Vector3(d.M23 - d.M32, d.M31 - d.M13, d.M12 - d.M21);
            float len = axis.Length;
            if (len < 1e-7f)
                return Vector3.Zero;
            float cos = MathHelper.Clamp((d.Trace - 1.0f) * 0.5f, -1.0f, 1.0f);
            return axis * (MathF.Acos(cos) / len);
        }

        /// <summary>
        /// Contact response on the previous position: the displacement relative to the
        /// collidable's own motion, less its normal part, is added back scaled by the
        /// particle's friction. The push out velocity itself is kept.
        /// </summary>
        void Contact(
            int p,
            Vector3 n,
            Vector3 surface,
            Vector3 centre,
            Vector3 linDt,
            Vector3 angDt
        )
        {
            Vector3 colDt = linDt + Vector3.Cross(angDt, surface - centre);
            Vector3 rel = _pos[p] - _prev[p] - colDt;
            rel -= n * Vector3.Dot(n, rel);
            _prev[p] += rel * _piece.Particles[p].Friction;
        }

        /// <summary>
        /// The orthonormal cloth frame of one driven bone from a set of particle
        /// positions: rows [p0 - c, p1 - c, cross, c] of its triangle through the local
        /// bone transform, translation raw, rotation rebuilt around the boneAxis column.
        /// </summary>
        Matrix4 DeformFrame(Vector3[] positions, HairBoneDeform bd)
        {
            Vector3 p0 = positions[_piece.TriangleIndices[bd.TriangleStart]];
            Vector3 p1 = positions[_piece.TriangleIndices[bd.TriangleStart + 1]];
            Vector3 p2 = positions[_piece.TriangleIndices[bd.TriangleStart + 2]];

            Vector3 c = (p0 + p1 + p2) * (1.0f / 3.0f);
            Vector3 e0 = p0 - c,
                e1 = p1 - c;
            Vector3 n = Vector3.Cross(e0, e1);

            var frame = new Matrix4(
                new Vector4(e0, 0),
                new Vector4(e1, 0),
                new Vector4(n, 0),
                new Vector4(c, 1)
            );
            Matrix4 raw = bd.LocalBoneTransform * frame;
            Vector3 r0 = raw.Row0.Xyz,
                r2 = raw.Row2.Xyz;
            Vector3 x,
                y,
                z;
            if (_piece.BoneAxis == 0)
            {
                x = r0.Normalized();
                y = Vector3.Cross(r2, x).Normalized();
                z = Vector3.Cross(x, y).Normalized();
            }
            else
            {
                z = r2.Normalized();
                y = Vector3.Cross(z, r0).Normalized();
                x = Vector3.Cross(y, z).Normalized();
            }
            return new Matrix4(
                new Vector4(x, 0),
                new Vector4(y, 0),
                new Vector4(z, 0),
                new Vector4(raw.Row3.Xyz, 1)
            );
        }

        Matrix4[] _skeletonBefore; //debug: the skeleton pose each driven bone had before the write

        /// <summary>
        /// What the game records for a driven bone when it binds the cloth: the first
        /// child of the bone (skeleton order) that is a cloth bone, the rotation that
        /// takes that child's rest local direction onto the x axis (or, when the child
        /// already lies on x, a flag to aim x at it directly), and the parent cloth bone
        /// the segment length is measured from.
        /// </summary>
        void SetUpAim(int i)
        {
            var piece = _piece;
            STBone bone = _driven[i];
            _aimChild[i] = -1;
            _lengthParent[i] = -1;
            _aimQuat[i] = Quaternion.Identity;

            int ClothIndex(STBone b) =>
                b == null ? -1 : Array.IndexOf(_bones, b);

            if (bone.Parent != null)
                _lengthParent[i] = ClothIndex(bone.Parent);

            STBone child = bone.Children.FirstOrDefault(c => ClothIndex(c) >= 0);
            if (child == null)
                return;
            _aimChild[i] = ClothIndex(child);

            Vector3 d = child.Position;
            if (d.LengthSquared <= 0)
            {
                _aimChild[i] = -1;
                return;
            }
            d.Normalize();
            Vector3 a = Vector3.UnitX;
            float dot = Vector3.Dot(d, a);
            if (Math.Abs(1.0f - Math.Abs(dot)) <= 1.19e-7f)
            {
                _aimAligned[i] = true;
                _aimNegate[i] = dot < 0;
                return;
            }
            if (dot < 0)
            {
                _aimNegate[i] = true;
                d = -d;
                dot = -dot;
            }
            float w = MathF.Sqrt((1.0f + dot) * 0.5f);
            if (w <= 1e-6f)
            {
                //Opposite the axis: a half turn about any perpendicular.
                Vector3 axis = Vector3.Cross(d, Vector3.UnitY);
                if (axis.LengthSquared <= 1e-12f)
                    axis = Vector3.Cross(d, Vector3.UnitZ);
                axis.Normalize();
                _aimQuat[i] = new Quaternion(axis, 0);
                return;
            }
            Vector3 v = Vector3.Cross(d, a) / (2.0f * w);
            _aimQuat[i] = new Quaternion(v, w);
        }

        /// <summary>The transform set position of a cloth bone: the deform output for a driven one, the fed pose otherwise.</summary>
        Vector3 RawPosition(int clothIndex)
        {
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
                if (_piece.BoneDeforms[i].BoneIndex == clothIndex)
                    return _rawFrame[i].Row3.Xyz;
            return _boneWorld[clothIndex].Row3.Xyz;
        }

        static Vector3 Rotate(Quaternion q, Vector3 p)
        {
            Vector3 v = q.Xyz;
            return p + 2.0f * Vector3.Cross(v, Vector3.Cross(v, p) + q.W * p);
        }

        static string Fmt(Matrix4 m) =>
            $"x=({m.M11:F3},{m.M12:F3},{m.M13:F3}) y=({m.M21:F3},{m.M22:F3},{m.M23:F3}) z=({m.M31:F3},{m.M32:F3},{m.M33:F3}) t=({m.M41:F3},{m.M42:F3},{m.M43:F3})";

        /// <summary>
        /// Simple mesh bone deform: for each driven bone the frame rows are
        /// [p0 - c, p1 - c, cross(p0 - c, p1 - c), c] of its source triangle, the raw
        /// product Local * Frame gives the translation, and the rotation is rebuilt
        /// orthonormal from the raw axes with the boneAxis column kept exact, so no
        /// scale or shear from the triangle reaches the bone. Havok then blends the
        /// cloth frame with the skeleton's world matrix by the bone's AnimReduceRt (1 pure
        /// cloth, 0 the arranged pose) and re-orthonormalises; the skeleton's scale is
        /// kept on the axes so a collapsed bone stays collapsed.
        /// </summary>
        void WriteBones(Dictionary<string, ArrangeBoneParam> arrange)
        {
            //Every deform output first: a bone aims at its child's raw origin, not at
            //the child after its own write.
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
            {
                var bd = _piece.BoneDeforms[i];
                _rawFrame[i] =
                    bd.TriangleStart + 2 < _piece.TriangleIndices.Length
                        ? DeformFrame(_render, bd)
                        : _boneWorld[bd.BoneIndex];
            }

            foreach (int i in _writeOrder)
            {
                var bd = _piece.BoneDeforms[i];
                if (bd.TriangleStart + 2 >= _piece.TriangleIndices.Length)
                    continue;

                float weight = 1.0f;
                if (arrange != null && arrange.TryGetValue(_driven[i].Name, out var arr))
                    weight = MathHelper.Clamp(arr.AnimReduce, 0, 1);
                if (weight <= 0)
                    continue;

                Matrix4 cloth = _rawFrame[i];
                Vector3 x = cloth.Row0.Xyz,
                    y = cloth.Row1.Xyz,
                    z = cloth.Row2.Xyz;
                Vector3 t = cloth.Row3.Xyz;

                //Aim: the frame is turned so the child's rest local direction points at
                //the child's raw origin, x carried through the bind quaternion and the
                //other two axes rebuilt from the raw z.
                if (_aimChild[i] >= 0)
                {
                    Vector3 dir = RawPosition(_aimChild[i]) - t;
                    if (dir.LengthSquared > 1e-12f)
                    {
                        dir.Normalize();
                        if (_aimAligned[i])
                            x = _aimNegate[i] ? -dir : dir;
                        else
                        {
                            Vector3 local = new Vector3(
                                Vector3.Dot(x, dir),
                                Vector3.Dot(y, dir),
                                Vector3.Dot(z, dir)
                            );
                            if (_aimNegate[i])
                                local = -local;
                            Vector3 v = Rotate(_aimQuat[i], local);
                            x = v.X * x + v.Y * y + v.Z * z;
                        }
                        y = Vector3.Cross(z, x).Normalized();
                        z = Vector3.Cross(x, y);
                    }
                }

                //The origin sits at the skeleton's segment length from the parent's
                //current position, along the raw direction from it.
                if (_lengthParent[i] >= 0 && _segmentLength[i] > 0)
                {
                    Vector3 parent = _bones[_lengthParent[i]].Transform.Row3.Xyz;
                    Vector3 d = t - parent;
                    if (d.LengthSquared > 1e-12f)
                        t = parent + d.Normalized() * _segmentLength[i];
                }

                Matrix4 skeleton = _driven[i].Transform;
                _skeletonBefore ??= new Matrix4[_piece.BoneDeforms.Count];
                _skeletonBefore[i] = skeleton;
                Vector3 sx = skeleton.Row0.Xyz,
                    sy = skeleton.Row1.Xyz,
                    sz = skeleton.Row2.Xyz;
                if (weight < 1)
                {
                    //Blend the axes toward the skeleton's directions, then rebuild an
                    //orthonormal frame around the blended bone axis.
                    x = Vector3.Lerp(sx.Normalized(), x, weight);
                    y = Vector3.Lerp(sy.Normalized(), y, weight);
                    z = Vector3.Lerp(sz.Normalized(), z, weight);
                    if (_piece.BoneAxis == 0)
                    {
                        x.Normalize();
                        y = Vector3.Cross(z, x).Normalized();
                        z = Vector3.Cross(x, y).Normalized();
                    }
                    else
                    {
                        z.Normalize();
                        y = Vector3.Cross(z, x).Normalized();
                        x = Vector3.Cross(y, z).Normalized();
                    }
                    t = Vector3.Lerp(skeleton.Row3.Xyz, t, weight);
                }
                _driven[i].Transform = new Matrix4(
                    new Vector4(x * sx.Length, 0),
                    new Vector4(y * sy.Length, 0),
                    new Vector4(z * sz.Length, 0),
                    new Vector4(t, 1)
                );
            }
        }
    }
}
