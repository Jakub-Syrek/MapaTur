using System.Numerics;

using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A rigged, animated glTF model loaded for CPU skinning — the engine behind the ridden dragon. Loads a
/// glTF/GLB with SharpGLTF, keeps each primitive's bind-pose vertices + per-vertex skin weights, and on
/// <see cref="Pose"/> evaluates the requested animation at a time and writes the skinned world positions/normals
/// back into each primitive's reusable buffers. The renderer then uploads those to a dynamic VBO and draws the
/// model with a simple textured/lit shader — no GPU skinning shader needed, which keeps the custom GL pipeline
/// simple. The base-colour texture is exposed as its raw encoded bytes so the GL layer decodes it (this
/// Application-layer type stays free of SkiaSharp/GL).
///
/// Coordinates are the model's own local space (glTF: +Y up, +Z forward, metres by convention). The caller
/// orients/scales the posed vertices into the world when drawing. Not thread-safe: pose it from one place.
/// </summary>
public sealed class SkinnedModel
{
    /// <summary>One drawable primitive: immutable bind-pose geometry + reusable posed output buffers.</summary>
    public sealed class Primitive
    {
        public required Vector3[] BindPositions { get; init; }
        public required Vector3[] BindNormals { get; init; }
        public required Vector2[] TexCoords { get; init; }
        public required SparseWeight8[] SkinWeights { get; init; }
        public required uint[] Indices { get; init; }
        public required int MeshIndex { get; init; }

        /// <summary>Skinned positions written by the last <see cref="Pose"/> (same length as <see cref="BindPositions"/>).</summary>
        public Vector3[] PosedPositions { get; init; } = Array.Empty<Vector3>();

        /// <summary>Skinned normals written by the last <see cref="Pose"/>.</summary>
        public Vector3[] PosedNormals { get; init; } = Array.Empty<Vector3>();
    }

    private readonly SceneInstance instance;
    private readonly ArmatureInstance armature;
    private readonly Matrix4x4[] bindLocals;             // each armature node's bind-pose local matrix
    private readonly Dictionary<string, int> nodeByName; // bone name → armature node index
    private Matrix4x4[]? blendLocals;                    // scratch: clip-A locals captured during a PoseBlend

    private SkinnedModel(
        IReadOnlyList<Primitive> primitives,
        SceneInstance instance,
        IReadOnlyList<(string Name, float Duration)> animations,
        byte[]? baseColorImageBytes)
    {
        Primitives = primitives;
        this.instance = instance;
        Animations = animations;
        BaseColorImageBytes = baseColorImageBytes;

        this.armature = instance.Armature;
        int count = this.armature.LogicalNodes.Count;
        this.bindLocals = new Matrix4x4[count];
        this.nodeByName = new Dictionary<string, int>(count);
        for (int i = 0; i < count; i++)
        {
            NodeInstance node = this.armature.LogicalNodes[i];
            this.bindLocals[i] = node.LocalMatrix;
            if (!string.IsNullOrEmpty(node.Name))
            {
                this.nodeByName[node.Name] = i;
            }
        }
    }

    /// <summary>Bone (armature node) names present in the rig — for wiring procedural animation to them.</summary>
    public IReadOnlyCollection<string> BoneNames => this.nodeByName.Keys;

    /// <summary>
    /// Axis-aligned bounds of the CURRENT posed vertices (not the bind pose). A baked clip can translate the
    /// whole mesh (root motion), so the bind-pose centre no longer marks the visible model — centring on this
    /// keeps the drawn dragon on its logical world position instead of drifting by the animation's offset.
    /// </summary>
    public (Vector3 Min, Vector3 Max) GetPosedBounds()
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (Primitive primitive in Primitives)
        {
            foreach (Vector3 vertex in primitive.PosedPositions)
            {
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
        }

        return float.IsFinite(min.X) ? (min, max) : (BoundsMin, BoundsMax);
    }

    /// <summary>Bind-pose axis-aligned bounds (model local space) — for scaling the model into world metres.</summary>
    public Vector3 BoundsMin { get; private set; }

    /// <summary>Bind-pose axis-aligned bounds (model local space).</summary>
    public Vector3 BoundsMax { get; private set; }

    /// <summary>Largest local extent (max bounds dimension) in the model's own units — a scale reference.</summary>
    public float LocalExtent
    {
        get
        {
            Vector3 size = BoundsMax - BoundsMin;
            return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        }
    }

    /// <summary>The drawable primitives (bind geometry + posed output).</summary>
    public IReadOnlyList<Primitive> Primitives { get; }

    /// <summary>Animation tracks: name + duration in seconds. Index into this list is the track for <see cref="Pose"/>.</summary>
    public IReadOnlyList<(string Name, float Duration)> Animations { get; }

    /// <summary>Raw encoded (PNG/JPEG) bytes of the first material's base-colour texture, or null if none.</summary>
    public byte[]? BaseColorImageBytes { get; }

    // Lenient loading: real-world Sketchfab-era exports fail SharpGLTF's STRICT validation on harmless issues
    // (e.g. the animated dragon's skin weights don't sum exactly to 1). TryFix normalizes what it can instead
    // of throwing, so those models load; genuinely broken files still throw.
    private static readonly SharpGLTF.Schema2.ReadSettings LenientRead = new()
    {
        Validation = SharpGLTF.Validation.ValidationMode.TryFix,
    };

    /// <summary>Loads a model from a glTF/GLB file path.</summary>
    public static SkinnedModel Load(string path) => FromModel(ModelRoot.Load(path, LenientRead));

    /// <summary>Loads a model from GLB bytes.</summary>
    public static SkinnedModel LoadGlb(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);
        return FromModel(ModelRoot.ParseGLB(new ArraySegment<byte>(glb), LenientRead));
    }

    private static SkinnedModel FromModel(ModelRoot model)
    {
        var primitives = new List<Primitive>();
        byte[]? baseColor = null;

        foreach (Mesh mesh in model.LogicalMeshes)
        {
            foreach (MeshPrimitive prim in mesh.Primitives)
            {
                IList<Vector3>? positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions is null || positions.Count == 0)
                {
                    continue;
                }

                int n = positions.Count;
                IList<Vector3>? normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                IList<Vector2>? uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                IList<Vector4>? joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                IList<Vector4>? weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

                var bindPos = new Vector3[n];
                var bindNrm = new Vector3[n];
                var tex = new Vector2[n];
                var skin = new SparseWeight8[n];
                for (int i = 0; i < n; i++)
                {
                    bindPos[i] = positions[i];
                    bindNrm[i] = normals is not null ? normals[i] : Vector3.UnitY;
                    tex[i] = uvs is not null ? uvs[i] : Vector2.Zero;
                    skin[i] = joints is not null && weights is not null
                        ? SparseWeight8.Create(joints[i], weights[i])
                        : default;
                }

                uint[] indices = prim.GetIndices() is { Count: > 0 } idx
                    ? idx.ToArray()
                    : SequentialIndices(n);

                primitives.Add(new Primitive
                {
                    BindPositions = bindPos,
                    BindNormals = bindNrm,
                    TexCoords = tex,
                    SkinWeights = skin,
                    Indices = indices,
                    MeshIndex = mesh.LogicalIndex,
                    PosedPositions = new Vector3[n],
                    PosedNormals = new Vector3[n],
                });

                baseColor ??= TryReadBaseColor(prim.Material);
            }
        }

        var animations = model.LogicalAnimations
            .Select(a => (a.Name ?? $"anim{a.LogicalIndex}", a.Duration))
            .ToList();

        Scene scene = model.DefaultScene ?? model.LogicalScenes[0];
        SceneInstance instance = SceneTemplate.Create(scene).CreateInstance();

        var result = new SkinnedModel(primitives, instance, animations, baseColor);

        // Bounds must be measured from the SKINNED bind pose, not the raw POSITION accessors: on a rigged mesh
        // the joint matrices carry the real scale/placement (bones sit metres from origin while the raw vertices
        // are a fraction of a unit), so the raw bounds would give a wildly wrong size + centre for the rendered
        // model. Pose to bind, skin, then measure what actually draws.
        result.ResetPose();
        result.Skin();

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (Primitive p in primitives)
        {
            foreach (Vector3 v in p.PosedPositions)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        result.BoundsMin = primitives.Count > 0 ? min : Vector3.Zero;
        result.BoundsMax = primitives.Count > 0 ? max : Vector3.Zero;
        return result;
    }

    /// <summary>
    /// Poses the model at animation <paramref name="animationIndex"/> and time <paramref name="seconds"/>, writing
    /// the skinned positions/normals into every primitive's <see cref="Primitive.PosedPositions"/> /
    /// <see cref="Primitive.PosedNormals"/>. With no animations, or an out-of-range index, it holds the bind pose.
    /// </summary>
    public void Pose(int animationIndex, float seconds)
    {
        SetFrame(animationIndex, seconds);
        Skin();
    }

    /// <summary>
    /// Poses the model as a CROSSFADE between two animation frames: animation <paramref name="animA"/> at
    /// <paramref name="secondsA"/> and animation <paramref name="animB"/> at <paramref name="secondsB"/>, blended
    /// per-bone by <paramref name="weight"/> (0 = fully A, 1 = fully B) with a TRS-decomposed, slerped bone lerp so
    /// the blend doesn't shear, then skins the result like <see cref="Pose"/>. The walk animation state machine
    /// drives this to fade Idle↔Walk↔Run / Jump / Land smoothly instead of snapping between clips.
    /// </summary>
    public void PoseBlend(int animA, float secondsA, int animB, float secondsB, float weight)
    {
        weight = Math.Clamp(weight, 0f, 1f);
        if (weight <= 0f)
        {
            Pose(animA, secondsA);
            return;
        }

        if (weight >= 1f)
        {
            Pose(animB, secondsB);
            return;
        }

        int count = this.armature.LogicalNodes.Count;
        this.blendLocals ??= new Matrix4x4[count];

        SetFrame(animA, secondsA);
        for (int i = 0; i < count; i++)
        {
            this.blendLocals[i] = this.armature.LogicalNodes[i].LocalMatrix;
        }

        SetFrame(animB, secondsB);
        for (int i = 0; i < count; i++)
        {
            NodeInstance node = this.armature.LogicalNodes[i];
            node.LocalMatrix = LerpTransform(this.blendLocals[i], node.LocalMatrix, weight);
        }

        Skin();
    }

    /// <summary>
    /// Sets the armature to animation <paramref name="animationIndex"/> at <paramref name="seconds"/> WITHOUT
    /// skinning — so the caller can layer procedural bone deltas on top (<see cref="RotateBoneOverlay"/>) and
    /// then <see cref="Skin"/> once. <see cref="Pose"/> = SetFrame + Skin.
    /// </summary>
    public void SetFrame(int animationIndex, float seconds)
    {
        if (Animations.Count > 0 && animationIndex >= 0 && animationIndex < Animations.Count)
        {
            this.armature.SetAnimationFrame(animationIndex, seconds);
        }
    }

    /// <summary>
    /// Composes a rotation onto a bone's CURRENT local transform — i.e. on top of whatever the animation frame
    /// (or a prior overlay) put there — unlike <see cref="RotateBone"/>, which composes onto the BIND pose.
    /// This is the hook for layering procedural motion (e.g. a turn wing-kick) over a baked clip.
    /// No-op if the bone name isn't in the rig. Returns true if applied.
    /// </summary>
    public bool RotateBoneOverlay(string boneName, Quaternion localDelta)
    {
        if (!this.nodeByName.TryGetValue(boneName, out int idx))
        {
            return false;
        }

        NodeInstance node = this.armature.LogicalNodes[idx];
        node.LocalMatrix = Matrix4x4.CreateFromQuaternion(localDelta) * node.LocalMatrix;
        return true;
    }

    /// <summary>
    /// Model-space position of a bone in the CURRENT pose (after <see cref="SetFrame"/>/<see cref="ResetPose"/>
    /// and any overlays), or null when the rig has no such bone. Used e.g. to seat the perched dragon's FEET on
    /// the ground (the bind bounds' lowest point may be its tail, not its feet).
    /// </summary>
    public Vector3? GetBonePosedPosition(string boneName)
    {
        if (!this.nodeByName.TryGetValue(boneName, out int idx))
        {
            return null;
        }

        return this.armature.LogicalNodes[idx].ModelMatrix.Translation;
    }

    /// <summary>
    /// Lowest POSED-vertex height (model-space Y) within <paramref name="radiusXZ"/> (in the ground plane) of
    /// any anchor point — i.e. the actual SOLE under a foot, where the foot bone's origin sits at joint height
    /// (seating on the bone floated the feet). Null when no vertex is in range. Call after <see cref="Skin"/>.
    /// </summary>
    public float? GetLowestVertexYNear(ReadOnlySpan<Vector3> anchors, float radiusXZ)
    {
        float best = float.PositiveInfinity;
        float radiusSquared = radiusXZ * radiusXZ;
        foreach (Primitive primitive in Primitives)
        {
            foreach (Vector3 vertex in primitive.PosedPositions)
            {
                if (vertex.Y >= best)
                {
                    continue;
                }

                foreach (Vector3 anchor in anchors)
                {
                    float dx = vertex.X - anchor.X;
                    float dz = vertex.Z - anchor.Z;
                    if ((dx * dx) + (dz * dz) <= radiusSquared)
                    {
                        best = vertex.Y;
                        break;
                    }
                }
            }
        }

        return float.IsFinite(best) ? best : null;
    }

    /// <summary>
    /// Blends a bone's CURRENT local transform toward its BIND pose (weight 0 = leave the animation frame,
    /// 1 = fully bind). This SUPPRESSES a baked clip's motion on that bone — e.g. freezing the inner wing
    /// spread-still during a turn while the clip keeps beating the outer one. Call after <see cref="SetFrame"/>,
    /// before <see cref="Skin"/>. No-op if the bone name isn't in the rig. Returns true if applied.
    /// </summary>
    public bool BlendBoneTowardBind(string boneName, float weight)
    {
        if (!this.nodeByName.TryGetValue(boneName, out int idx))
        {
            return false;
        }

        NodeInstance node = this.armature.LogicalNodes[idx];
        node.LocalMatrix = LerpTransform(node.LocalMatrix, this.bindLocals[idx], Math.Clamp(weight, 0f, 1f));
        return true;
    }

    // TRS-decomposed lerp (slerped rotation) — a raw component-wise matrix lerp would shear mid-blend.
    private static Matrix4x4 LerpTransform(Matrix4x4 a, Matrix4x4 b, float t)
    {
        if (t <= 0f)
        {
            return a;
        }

        if (t >= 1f)
        {
            return b;
        }

        if (Matrix4x4.Decompose(a, out Vector3 scaleA, out Quaternion rotA, out Vector3 posA)
            && Matrix4x4.Decompose(b, out Vector3 scaleB, out Quaternion rotB, out Vector3 posB))
        {
            return Matrix4x4.CreateScale(Vector3.Lerp(scaleA, scaleB, t))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(rotA, rotB, t))
                * Matrix4x4.CreateTranslation(Vector3.Lerp(posA, posB, t));
        }

        return t < 0.5f ? a : b;
    }

    /// <summary>Resets every bone to its bind-pose local transform — the start of a PROCEDURAL pose (no baked
    /// animation clip). Call this, then <see cref="RotateBone"/> the bones you want, then <see cref="Skin"/>.</summary>
    public void ResetPose()
    {
        for (int i = 0; i < this.bindLocals.Length; i++)
        {
            this.armature.LogicalNodes[i].LocalMatrix = this.bindLocals[i];
        }
    }

    /// <summary>Applies a rotation to a named bone in its local frame, composed onto its bind pose, for procedural
    /// animation (e.g. flapping a wing). No-op if the bone name isn't in the rig. Returns true if applied.</summary>
    public bool RotateBone(string boneName, Quaternion localDelta)
    {
        if (!this.nodeByName.TryGetValue(boneName, out int idx))
        {
            return false;
        }

        // Row-vector convention (System.Numerics): pre-multiplying the delta rotates the bone in its own local
        // frame about the joint origin before the bind transform places it under the parent.
        this.armature.LogicalNodes[idx].LocalMatrix =
            Matrix4x4.CreateFromQuaternion(localDelta) * this.bindLocals[idx];
        return true;
    }

    /// <summary>Recomputes the skinned positions/normals from the armature's CURRENT pose into each primitive's
    /// posed buffers. Called by <see cref="Pose"/>; call it directly after a procedural <see cref="ResetPose"/> +
    /// <see cref="RotateBone"/> sequence.</summary>
    public void Skin()
    {
        // Map each mesh's current geometry transform (rigid or skinned) from the posed armature.
        var transforms = new Dictionary<int, IGeometryTransform>();
        foreach (DrawableInstance d in this.instance)
        {
            transforms[d.Template.LogicalMeshIndex] = d.Transform;
        }

        foreach (Primitive p in Primitives)
        {
            if (!transforms.TryGetValue(p.MeshIndex, out IGeometryTransform? xf))
            {
                Array.Copy(p.BindPositions, p.PosedPositions, p.BindPositions.Length);
                Array.Copy(p.BindNormals, p.PosedNormals, p.BindNormals.Length);
                continue;
            }

            for (int i = 0; i < p.BindPositions.Length; i++)
            {
                p.PosedPositions[i] = xf.TransformPosition(p.BindPositions[i], null, p.SkinWeights[i]);
                p.PosedNormals[i] = Vector3.Normalize(xf.TransformNormal(p.BindNormals[i], null, p.SkinWeights[i]));
            }
        }
    }

    private static byte[]? TryReadBaseColor(Material? material)
    {
        try
        {
            // Metallic-roughness carries the albedo as "BaseColor"; KHR_materials_pbrSpecularGlossiness
            // (Sketchfab-era exports, e.g. the animated dragon) exposes it as "Diffuse" instead. NOTE: the
            // "BaseColor" CHANNEL exists even on a spec-gloss material (just with no texture), so a
            // FindChannel(...) ?? FindChannel(...) chain never falls through — probe each channel for an
            // actual texture instead.
            foreach (string channelName in new[] { "BaseColor", "Diffuse" })
            {
                SharpGLTF.Memory.MemoryImage? image = material?.FindChannel(channelName)?.Texture?.PrimaryImage?.Content;
                if (image is { Content.Length: > 0 } found)
                {
                    return found.Content.ToArray();
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint[] SequentialIndices(int count)
    {
        var idx = new uint[count];
        for (int i = 0; i < count; i++)
        {
            idx[i] = (uint)i;
        }

        return idx;
    }
}