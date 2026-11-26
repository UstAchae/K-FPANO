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
// - window glass (the materials the game's own WindowsManager takes as window glass, by its own test: its window glass
//   shader; single-material windows it switches off and draws itself, for the main and optic cameras only) is drawn by
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
    internal static readonly List<Renderer> Decals = new List<Renderer>();
    internal static readonly List<Part> Glass = new List<Part>();
    internal static System.Action<string>? Note;
    private static readonly List<Renderer> _hidden = new List<Renderer>();
    private static readonly List<Renderer> _found = new List<Renderer>();
    private static readonly List<Material> _materials = new List<Material>();
    private static bool _hooked, _gone;
    private static readonly System.Predicate<Renderer> DecalGone = r => r == null;
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
                        // (the game's own test of a window glass material, WindowsManager.IsMaterialValid, for each
                        // material: a renderer with more than one (the escalators' balustrades) is not the
                        // WindowsManager's, drawn by itself, but its glass is glass all the same)
                        if (r is MeshRenderer && WindowsManager.IsMaterialValid(m))
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
                        Decals.Add(r);
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

    // the decals hidden for the lens camera's render (put back by ShowDecals right after it)
    internal static int HideDecals()
    {
        _hidden.Clear();
        foreach (Renderer r in Decals)
        {
            if (r == null)
            {
                _gone = true;
                continue;
            }
            if (!r.enabled || r.forceRenderingOff || !r.isVisible)
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
