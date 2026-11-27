using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace EnvgFusion;

// What the thermal lens camera must see differently from the eye (the user's, 2026-11-26: "我只要挡住玻璃，不画墙壁上的涂鸦和
// 字"): long-wave infrared does not pass window glass, and paint on a wall is the wall's own temperature. The game's own
// thermal (the T-7's) gets both from its switch (_ThermalVisionOn), which has every surface draw its material's
// preset "temperature" instead of its colour, and so flattens everything else too; here the colour stays (the picture
// as before) and only these two are changed:
// - decals (the renderers whose every material is drawn by a decal shader: graffiti, signs, stains) are left out of the
//   lens camera's render (hidden for it alone, shown again right after);
// - window glass (materials whose shader the T-7 shows solid, see SolidForThermal; the WindowsManager switches
//   single-material windows off and draws them itself, for the main and optic cameras only) is drawn by
//   its mesh into the lens camera's diffuse colour as an even surface, against its depth, before the colour is copied.
//   (Chosen by any see-through material with a thermal temperature, 0.1.22 took hundreds of puddles and stains as
//   glass, a grey over the picture, and missed the windows, whose shader has none.)
// Which renderers those are is found once per level scene as it loads (not while playing) and kept.
internal static class ThermalScene
{
    internal struct Part
    {
        public Renderer Renderer;
        public int Sub;
        public Mesh Mesh;
        public EFT.Interactive.WindowBreaker? Breaker;
    }
    internal struct Decal
    {
        public Renderer Renderer;
        public Bounds Bounds;   // as its level scene loaded (the levels' decals do not move)
    }
    internal static readonly List<Decal> Decals = new List<Decal>();
    internal static readonly List<Part> Glass = new List<Part>();
    internal static System.Action<string>? Note;
    private static readonly List<Renderer> _hidden = new List<Renderer>();
    private static readonly List<Renderer> _found = new List<Renderer>();
    private static readonly List<Material> _materials = new List<Material>();
    private static bool _hooked, _gone;
    private static readonly System.Predicate<Decal> DecalGone = d => d.Renderer == null;
    private static readonly System.Predicate<Part> GlassGone = p => p.Renderer == null;

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        SceneManager.sceneLoaded += (scene, mode) => Collect(scene);
        for (int i = 0; i < SceneManager.sceneCount; i++)
            Collect(SceneManager.GetSceneAt(i));
    }

    // the see-through shaders the T-7 shows solid: under the game's thermal switch (_ThermalVisionOn) their pixel
    // programs force the alpha to 1 (read from the game's own compiled shaders, 2026-11-26): the window glass (also
    // what the WindowsManager draws instanced), the other reflective glass (black, opaque) and DepthZwrite (its own
    // temperature, opaque). Others only change their colour (Standard, AlphaBumpedFresnel, WireNetting) or do not
    // react at all (Custom/Glass2), so the T-7 sees through them, and so does the thermal lens here.
    private static readonly HashSet<string> SolidForThermal = new HashSet<string>
    {
        "Global Fog/Transparent Reflective Specular",
        "p0/Transparent/Reflective/Specular",
        "Transparent/DepthZwrite",
    };

    private static void Collect(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return;
        Decals.RemoveAll(DecalGone);
        Glass.RemoveAll(GlassGone);
        int decals = 0, glass = 0;
        try
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                _found.Clear();
                root.GetComponentsInChildren(true, _found);
                foreach (Renderer r in _found)
                {
                    if (r == null || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                        continue;
                    _materials.Clear();
                    r.GetSharedMaterials(_materials);
                    if (_materials.Count == 0)
                        continue;
                    bool allDecal = true;
                    for (int sub = 0; sub < _materials.Count; sub++)
                    {
                        Material m = _materials[sub];
                        string shader = m != null && m.shader != null ? m.shader.name : "";
                        if (shader.IndexOf("decal", System.StringComparison.OrdinalIgnoreCase) < 0)
                            allDecal = false;
                        if (r is MeshRenderer && m != null && SolidForThermal.Contains(shader))
                        {
                            // (by its mesh: for the WindowsManager's windows the game has switched the renderer off)
                            Mesh? mesh = r is MeshRenderer && r.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                            int at = r.isPartOfStaticBatch ? ((MeshRenderer)r).subMeshStartIndex + sub : sub;
                            if (mesh == null || at >= mesh.subMeshCount)
                                continue;
                            r.TryGetComponent(out EFT.Interactive.WindowBreaker breaker);
                            Glass.Add(new Part { Renderer = r, Sub = at, Mesh = mesh, Breaker = breaker });
                            glass++;
                        }
                    }
                    if (allDecal)
                    {
                        Decals.Add(new Decal { Renderer = r, Bounds = r.bounds });
                        decals++;
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Note?.Invoke("thermal scene: " + scene.name + " not read: " + e.Message);
        }
        _found.Clear();
        _materials.Clear();
        if (decals > 0 || glass > 0)
            Note?.Invoke("thermal scene " + scene.name + ": " + decals + " decal renderer(s) left out of the thermal lens's view, " + glass + " window glass part(s) drawn solid for it (" + Decals.Count + " and " + Glass.Count + " in all)");
    }

    // The decals hidden for the lens camera's render (put back by ShowDecals right after it): those whose place lies in
    // the lens camera's view. (Not "those drawn last frame", Renderer.isVisible: a decal only the lens camera draws, on a
    // level of detail the main camera does not show at that distance, was drawn one frame, so hidden the next, so not
    // drawn, so shown again: on and off every other frame, 120 changes in 120 frames in the log. Far rubble's paint
    // flickered in the thermal picture from where the two cameras chose different levels, the user's "B" flicker.)
    private static readonly Plane[] _decalPlanes = new Plane[6];
    internal static int HideDecals(Camera lens)
    {
        _hidden.Clear();
        GeometryUtility.CalculateFrustumPlanes(lens, _decalPlanes);
        foreach (Decal d in Decals)
        {
            if (!GeometryUtility.TestPlanesAABB(_decalPlanes, d.Bounds))
                continue;
            Renderer r = d.Renderer;
            if (r == null)
            {
                _gone = true;
                continue;
            }
            if (!r.enabled || r.forceRenderingOff)
                continue;
            r.forceRenderingOff = true;
            _hidden.Add(r);
        }
        return _hidden.Count;
    }

    internal static void ShowDecals()
    {
        foreach (Renderer r in _hidden)
            if (r != null)
                r.forceRenderingOff = false;
        _hidden.Clear();
        if (_gone)
        {
            _gone = false;
            Decals.RemoveAll(DecalGone);
        }
    }

    // the window glass in the camera's view drawn with the given material's pass (an even colour, against the depth
    // bound); of a window the game's WindowBreaker has broken, the pieces still stuck in the frame (each its own mesh,
    // where the game placed it), not the whole pane (the user's: a pane partly broken let the whole of it through)
    private static readonly AccessTools.FieldRef<EFT.Interactive.WindowBreaker, EFT.Interactive.WindowBreaker.Piece[]> PiecesOf =
        AccessTools.FieldRefAccess<EFT.Interactive.WindowBreaker, EFT.Interactive.WindowBreaker.Piece[]>("_pieces");
    private static readonly Plane[] _planes = new Plane[6];
    private const float GlassReach = 400f;
    internal static int DrawGlass(CommandBuffer cb, Material material, int pass, Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, _planes);
        Vector3 at = camera.transform.position;
        int n = 0;
        foreach (Part p in Glass)
        {
            Renderer r = p.Renderer;
            if (r == null || p.Mesh == null || !r.gameObject.activeInHierarchy)
                continue;
            if (p.Breaker != null && p.Breaker.IsDamaged)
            {
                n += DrawPieces(cb, material, pass, p.Breaker, at);
                continue;
            }
            Bounds b = r.bounds;
            if ((b.center - at).sqrMagnitude > GlassReach * GlassReach || !GeometryUtility.TestPlanesAABB(_planes, b))
                continue;
            cb.DrawMesh(p.Mesh, r.isPartOfStaticBatch ? Matrix4x4.identity : r.localToWorldMatrix, material, p.Sub, pass);
            n++;
        }
        return n;
    }

    private static int DrawPieces(CommandBuffer cb, Material material, int pass, EFT.Interactive.WindowBreaker breaker, Vector3 at)
    {
        EFT.Interactive.WindowBreaker.Piece[]? pieces = PiecesOf(breaker);
        if (pieces == null || (breaker.transform.position - at).sqrMagnitude > GlassReach * GlassReach)
            return 0;
        int n = 0;
        foreach (EFT.Interactive.WindowBreaker.Piece piece in pieces)
        {
            if (piece == null || !piece.Stuck)
                continue;
            MeshFilter? filter = piece.Description.MeshFilter;
            GameObject? go = piece.Description.GameObject;
            if (filter == null || go == null || !go.activeInHierarchy || filter.sharedMesh == null)
                continue;
            cb.DrawMesh(filter.sharedMesh, filter.transform.localToWorldMatrix, material, 0, pass);
            n++;
        }
        return n;
    }
}
