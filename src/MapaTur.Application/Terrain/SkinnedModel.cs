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

    /// <summary>Loads a model from a glTF/GLB file path.</summary>
    public static SkinnedModel Load(string path) => FromModel(ModelRoot.Load(path));

    /// <summary>Loads a model from GLB bytes.</summary>
    public static SkinnedModel LoadGlb(byte[] glb)
    {
        ArgumentNullException.ThrowIfNull(glb);
        return FromModel(ModelRoot.ParseGLB(new ArraySegment<byte>(glb)));
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
        if (Animations.Count > 0 && animationIndex >= 0 && animationIndex < Animations.Count)
        {
            this.armature.SetAnimationFrame(animationIndex, seconds);
        }

        Skin();
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
            MaterialChannel? channel = material?.FindChannel("BaseColor");
            SharpGLTF.Memory.MemoryImage? image = channel?.Texture?.PrimaryImage?.Content;
            return image?.Content.ToArray();
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