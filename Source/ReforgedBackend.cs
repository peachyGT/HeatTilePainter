
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using KSP.UI.Screens;

namespace HeatTileOverlayPrototype.ReforgedCore
{

    [KSPModule("Heat Tile Reforged Core")]
    public class ModuleHeatTileReforgedCore : PartModule
    {

        // Heat Tile Painter owns persistence. These are deliberately runtime-only derived fields,
        // so the rendering core can never race the layer stack during craft loading.
        [KSPField] public bool  rdxFinish = false;
        [KSPField] public float rdxMetal = 0f;
        [KSPField] public float rdxSmooth = 0f;
        [KSPField] public bool  rdxColored = false;
        [KSPField] public float rdxR = 1f;
        [KSPField] public float rdxG = 1f;
        [KSPField] public float rdxB = 1f;
        [KSPField] public bool  rdxKeepShiny = false;
        [KSPField] public float rdxScale = 1f;
        [KSPField] public int   rdxShineSpot = 0;
        [KSPField] public string rdxRegion = "whole";

        [KSPField] public string rdxStack = "";

        [KSPField] public string rdxZones = "";

        public bool Active { get { return rdxFinish || rdxColored || WeatherOn || !string.IsNullOrEmpty(rdxZones); } }
        public bool WeatherOn { get { return !string.IsNullOrEmpty(rdxStack); } }

        readonly Dictionary<Renderer, Material[]> pristine = new Dictionary<Renderer, Material[]>();

        readonly Dictionary<Renderer, Material[]> origShared = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Renderer, Mesh> origMesh = new Dictionary<Renderer, Mesh>();
        readonly List<Mesh> splitMeshes = new List<Mesh>();
        const string SplitMeshPrefix = "__HeatTilePainterSplit";
        readonly List<Material> created = new List<Material>();

        static Shader tuShader;
        static bool tuTried;
        static readonly int IdMainTex  = Shader.PropertyToID("_MainTex");
        static readonly int IdBumpMap  = Shader.PropertyToID("_BumpMap");
        static readonly int IdColor    = Shader.PropertyToID("_Color");
        static readonly int IdMetal    = Shader.PropertyToID("_Metal");
        static readonly int IdSmooth   = Shader.PropertyToID("_Smoothness");
        static readonly int IdMetalGloss = Shader.PropertyToID("_MetallicGlossMap");

        static readonly HashSet<ModuleHeatTileReforgedCore> liveModules = new HashSet<ModuleHeatTileReforgedCore>();
        const int BakeCap = 48;

        public static Shader TuShader()
        {
            if (tuTried) return tuShader;
            tuTried = true;

            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.assembly == null) continue;
                    Type t = la.assembly.GetType("KSPShaderTools.TexturesUnlimitedLoader", false);
                    if (t == null) continue;
                    MethodInfo mi = t.GetMethod("getShader", BindingFlags.Public | BindingFlags.Static);
                    if (mi != null) tuShader = mi.Invoke(null, new object[] { "TU/Metallic" }) as Shader;
                    if (tuShader != null)
                    {
                        Debug.Log("[ReforgedRedux] TU/Metallic via TexturesUnlimitedLoader (" + la.assembly.GetName().Name + ")");
                        return tuShader;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] TU loader lookup failed: " + e.Message); }

            try
            {
                Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == "TU/Metallic")
                    {
                        tuShader = all[i];
                        Debug.Log("[ReforgedRedux] TU/Metallic via loaded-shader scan");
                        return tuShader;
                    }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] shader scan failed: " + e.Message); }

            tuShader = Shader.Find("TU/Metallic");
            if (tuShader == null)
                Debug.LogError("[ReforgedRedux] TU/Metallic shader NOT FOUND - finishes disabled. Is TexturesUnlimited installed?");
            return tuShader;
        }

        static Shader deferredTu;
        static bool deferredTried;
        static Shader ActiveTuShader()
        {
            Shader baseTu = TuShader();
            if (baseTu == null) return null;
            if (deferredTried) return (deferredTu != null) ? deferredTu : baseTu;
            deferredTried = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.assembly == null || la.assembly.GetName().Name != "Deferred") continue;
                    Type[] types = la.assembly.GetTypes();
                    for (int i = 0; i < types.Length; i++)
                    {
                        if (types[i].Name != "ShaderLoader") continue;
                        PropertyInfo pi = types[i].GetProperty("ReplacementShaders", BindingFlags.Public | BindingFlags.Static);
                        if (pi == null) continue;
                        System.Collections.IDictionary dict = pi.GetValue(null, null) as System.Collections.IDictionary;
                        if (dict != null && dict.Contains("TU/Metallic"))
                        {
                            deferredTu = dict["TU/Metallic"] as Shader;
                            if (deferredTu != null)
                                Debug.Log("[ReforgedRedux] using Deferred's replacement TU/Metallic shader");
                        }
                        break;
                    }
                    break;
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] Deferred lookup failed: " + e.Message); }
            return (deferredTu != null) ? deferredTu : baseTu;
        }

        static readonly Dictionary<string, Texture2D> fillTex = new Dictionary<string, Texture2D>();
        static Texture2D FillTexture(byte r, byte g, byte b, byte a)
        {
            string key = r + "," + g + "," + b + "," + a;
            Texture2D t;
            if (fillTex.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Color32 c = new Color32(r, g, b, a);
            t.SetPixels32(new Color32[] { c, c, c, c });
            t.Apply(false, false);
            fillTex[key] = t;
            return t;
        }
        static void FillSlot(Material m, string slot, byte r, byte g, byte b, byte a)
        {
            if (m.HasProperty(slot) && m.GetTexture(slot) == null)
                m.SetTexture(slot, FillTexture(r, g, b, a));
        }
        static void FillEmptyTuSlots(Material m)
        {
            FillSlot(m, "_MainTex", 0, 0, 0, 255);
            FillSlot(m, "_BumpMap", 128, 128, 128, 128);
            FillSlot(m, "_Emissive", 0, 0, 0, 0);
            FillSlot(m, "_MetallicGlossMap", 255, 255, 255, 255);
            FillSlot(m, "_SpecGlossMap", 255, 255, 255, 255);
            FillSlot(m, "_MaskTex", 255, 0, 0, 255);
        }

        static readonly string[] AlienShaders = {
            "waterfall", "particle", "/additive", "smoke", "kerbalfx", "sootstreak",
            "scatterer", "eve/", "eve-", "eve ", "atmosphere", "deferred/",
            "sunflare", "sunspot", "kopernicus", "parallax", "/rings", "flare",
            "planetshine", "singularity", "firefly" };

        static readonly string[] SkipNames = { "flag", "window", "glass", "canopy", "overlay" };

        internal static bool SkipRenderer(Renderer r)
        {

            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) return true;

            float sz = r.bounds.size.magnitude;
            if (float.IsNaN(sz) || float.IsInfinity(sz) || sz > 2000f) return true;

            if (r.GetComponentInParent<Camera>() != null) return true;

            if (r.GetComponent<Light>() != null) return true;

            Material sm = r.sharedMaterial;
            if (sm != null && sm.shader != null)
            {
                string shn = sm.shader.name.ToLowerInvariant();
                if (shn.Contains("depthmask")) return true;
                for (int i = 0; i < AlienShaders.Length; i++)
                    if (shn.Contains(AlienShaders[i])) return true;
            }

            string n = r.name.ToLowerInvariant();
            for (int i = 0; i < SkipNames.Length; i++)
                if (n.Contains(SkipNames[i])) return true;
            return false;
        }

        static MethodInfo tuIsTransparent;
        static bool tuIsTransparentTried;
        static bool IsGlass(Material m)
        {
            if (!tuIsTransparentTried)
            {
                tuIsTransparentTried = true;
                try
                {
                    foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                    {
                        if (la.assembly == null) continue;
                        Type t = la.assembly.GetType("KSPShaderTools.TexturesUnlimitedLoader", false);
                        if (t == null) continue;
                        tuIsTransparent = t.GetMethod("isTransparentMaterial", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }
                catch { }
            }
            if (tuIsTransparent != null)
            {
                try { if ((bool)tuIsTransparent.Invoke(null, new object[] { m })) return true; }
                catch { tuIsTransparent = null; }
            }
            if (m.renderQueue >= 2900) return true;
            Shader sh = m.shader;
            if (sh != null)
            {
                string sn = sh.name.ToLowerInvariant();
                if (sn.Contains("transparent") || sn.Contains("translucent") || sn.Contains("glass")
                    || sn.Contains("/alpha/") || sn.Contains("cutoff") || sn.Contains("unlit"))
                    return true;
            }
            return false;
        }

        bool excludedPart;
        bool isFairing;
        int lastRenderCount = -1;
        bool ExcludedPart()
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                string mn = part.Modules[i].moduleName;
                if (mn == "FSengineBladed" || mn.StartsWith("FSpropeller")) return true;
            }
            bool engine = false;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i].moduleName.StartsWith("ModuleEngines") ||
                    part.Modules[i].moduleName == "ModuleRoboticServoRotor") { engine = true; break; }
            if (!engine) return false;
            Renderer[] rs = part.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != null && rs[i].name.ToLowerInvariant().Contains("blur")) return true;
            return false;
        }

        internal static string RelPath(Transform t, Transform root)
        {
            string path = t.name;
            Transform pn = t.parent;
            while (pn != null && pn != root) { path = pn.name + "/" + path; pn = pn.parent; }
            return path;
        }

        Material[] PrefabMats(Renderer r)
        {
            try
            {
                if (part == null || part.partInfo == null || part.partInfo.partPrefab == null || r == null) return null;
                Transform pref = part.partInfo.partPrefab.transform;

                Transform pt = pref.Find(RelPath(r.transform, part.transform));
                Renderer pr = (pt != null) ? pt.GetComponent<Renderer>() : null;

                if (pr == null)
                {
                    Renderer[] all = part.partInfo.partPrefab.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].name == r.name) { pr = all[i]; break; }
                }
                return (pr != null) ? pr.sharedMaterials : null;
            }
            catch { return null; }
        }

        bool UsesDynamicMeshSource()
        {
            if (part == null) return false;
            string pn = part.partInfo != null ? part.partInfo.name : part.name;
            if (!string.IsNullOrEmpty(pn)
                && pn.IndexOf("procedural", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule pm = part.Modules[i];
                if (pm == null || ReferenceEquals(pm, this)) continue;
                string tn = pm.GetType().Name;
                string mn = pm.moduleName ?? "";
                if (tn.IndexOf("Procedural", StringComparison.OrdinalIgnoreCase) >= 0
                    || mn.IndexOf("Procedural", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        Mesh PrefabMesh(Renderer r)
        {
            try
            {
                if (part == null || part.partInfo == null || part.partInfo.partPrefab == null || r == null) return null;
                // A procedural part's prefab is only a seed shape. The settled live mesh is its
                // authoritative unpainted source; substituting the prefab here changes its shape.
                if (UsesDynamicMeshSource()) return null;
                // The settled live mesh is also authoritative on stock parts. KSP and visual
                // mods may clone or edit it. A prefab is only a recovery source for a copied
                // painted renderer whose original ownership dictionary was not cloned.
                bool copiedPaint = false;
                foreach (Material material in r.sharedMaterials)
                    if (material != null && material.HasProperty(IdMainTex)
                        && WeatherCache.IsBaked(material.GetTexture(IdMainTex))) copiedPaint = true;
                if (!copiedPaint) return null;
                Transform pref = part.partInfo.partPrefab.transform;
                Transform pt = pref.Find(RelPath(r.transform, part.transform));
                MeshFilter pmf = (pt != null) ? pt.GetComponent<MeshFilter>() : null;
                if (pmf == null)
                {
                    MeshFilter[] all = part.partInfo.partPrefab.GetComponentsInChildren<MeshFilter>(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].name == r.name) { pmf = all[i]; break; }
                }
                return (pmf != null) ? pmf.sharedMesh : null;
            }
            catch { return null; }
        }

        List<Renderer> OwnRenderers()
        {
            List<Renderer> mine = new List<Renderer>();
            if (part == null) return mine;
            Transform iva = (part.internalModel != null) ? part.internalModel.transform : null;
            Transform root = part.transform.Find("model");
            if (root == null) root = part.transform;
            Renderer[] all = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || SkipRenderer(all[i])
                    || !HeatTileOverlayPrototype.ExteriorPaintPolicy.AllowsRenderer(part, all[i])) continue;
                if (iva != null && all[i].transform.IsChildOf(iva)) continue;
                if (all[i].GetComponentInParent<Camera>() != null) continue;
                Part owner = all[i].GetComponentInParent<Part>();
                if (owner == null || owner == part) mine.Add(all[i]);
            }
            return mine;
        }

        public override void OnStart(StartState state)
        {
            liveModules.Add(this);
            excludedPart = ExcludedPart();
            isFairing = false;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i].moduleName.IndexOf("Fairing", StringComparison.OrdinalIgnoreCase) >= 0) { isFairing = true; break; }
            if (isFairing && HighLogic.LoadedSceneIsEditor) StartCoroutine(FairingWatch());
        }

        int ModelRenderCount()
        {
            Transform m = (part != null) ? part.transform.Find("model") : null;
            return (m != null) ? m.GetComponentsInChildren<Renderer>(true).Length : 0;
        }
        System.Collections.IEnumerator FairingWatch()
        {
            WaitForSeconds wait = new WaitForSeconds(0.3f);
            while (part != null)
            {
                yield return wait;
                if (Active && ModelRenderCount() != lastRenderCount) Rebuild();
            }
        }

        public override void OnStartFinished(StartState state)
        {

            if (Active) { if (DeferLoadBakes) EnqueueLoadBake(this); else Rebuild(); }
            UpdateHeatBuff();
        }

        void OnDestroy()
        {
            liveModules.Remove(this);
            RestoreOriginals();
        }

        internal static bool DeferLoadBakes = true;
        static readonly Queue<ModuleHeatTileReforgedCore> loadQueue = new Queue<ModuleHeatTileReforgedCore>();
        static void EnqueueLoadBake(ModuleHeatTileReforgedCore m) { if (m != null) loadQueue.Enqueue(m); }
        public static void ClearLoadQueue() { loadQueue.Clear(); }

        public static bool PumpLoadQueue()
        {
            while (loadQueue.Count > 0)
            {
                ModuleHeatTileReforgedCore m = loadQueue.Dequeue();
                if (m == null || m.part == null || !m.Active) continue;
                try { m.Rebuild(); }
                catch (Exception e) { Debug.LogError("[ReforgedRedux] deferred paint failed: " + e.Message); }
                return true;
            }
            return false;
        }

        static void SweepCaches()
        {
            liveModules.RemoveWhere(delegate(ModuleHeatTileReforgedCore mm) { return mm == null; });
            HashSet<int> live = new HashSet<int>();
            foreach (ModuleHeatTileReforgedCore m in liveModules)
            {
                if (m == null) continue;
                foreach (KeyValuePair<Renderer, Material[]> kv in m.pristine)
                {
                    Renderer r = kv.Key;
                    if (r == null) continue;
                    Material[] mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        Material mat = mats[i];
                        if (mat == null) continue;
                        Texture a = mat.HasProperty(IdMainTex) ? mat.GetTexture(IdMainTex) : null; if (a != null) live.Add(a.GetInstanceID());
                        Texture b = mat.HasProperty(IdBumpMap) ? mat.GetTexture(IdBumpMap) : null; if (b != null) live.Add(b.GetInstanceID());
                        Texture g = mat.HasProperty(IdMetalGloss) ? mat.GetTexture(IdMetalGloss) : null; if (g != null) live.Add(g.GetInstanceID());
                    }
                }
            }
            WeatherCache.EvictExcept(live);
        }

        void RestoreOriginals()
        {
            RestoreOriginals(false);
        }

        void RestoreOriginals(bool reapplyVariant)
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in origShared)
                if (kv.Key != null && kv.Value != null) kv.Key.sharedMaterials = kv.Value;
            for (int i = 0; i < created.Count; i++)
                if (created[i] != null) Destroy(created[i]);
            created.Clear();
            foreach (KeyValuePair<Renderer, Material[]> kv in pristine)
                for (int j = 0; j < kv.Value.Length; j++)
                    if (kv.Value[j] != null) Destroy(kv.Value[j]);
            pristine.Clear();
            origShared.Clear();
            RestoreMeshesToOriginal();
            origMesh.Clear();
            // Cycling a ModulePartVariants selection is a state-changing operation, not a generic
            // cleanup step. It can expose alternate/internal model transforms when Remove All is
            // called across a craft. Only the explicit variant-change path is allowed to do this.
            if (reapplyVariant) ReapplyVariant();
        }

        void ReapplyVariant()
        {
            try
            {
                ModulePartVariants mpv = part.FindModuleImplementing<ModulePartVariants>();
                if (mpv == null || mpv.SelectedVariant == null || string.IsNullOrEmpty(mpv.SelectedVariant.Name)) return;
                string cur = mpv.SelectedVariant.Name;

                List<PartVariant> vl = mpv.variantList;
                if (vl != null && vl.Count > 1)
                    for (int i = 0; i < vl.Count; i++)
                        if (vl[i] != null && vl[i].Name != cur) { mpv.SetVariant(vl[i].Name); break; }
                mpv.SetVariant(cur);
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] variant reapply failed: " + e.Message); }
        }

        static string StripPlaces(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) es[i] = f[0] + ":whole:" + f[2];
            }
            return string.Join(";", es);
        }

        void RestoreMeshesToOriginal()
        {
            foreach (KeyValuePair<Renderer, Mesh> kv in origMesh)
            {
                if (kv.Key == null || kv.Value == null) continue;
                MeshFilter mf = kv.Key.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = kv.Value;
            }
            for (int i = 0; i < splitMeshes.Count; i++)
                if (splitMeshes[i] != null) Destroy(splitMeshes[i]);
            splitMeshes.Clear();
        }

        void RegionExtent(List<Renderer> rs, Vector3 wdir, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            for (int i = 0; i < rs.Count; i++)
            {
                MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Vector3[] vs = mf.sharedMesh.vertices;
                Transform t = rs[i].transform;
                for (int v = 0; v < vs.Length; v++)
                {
                    float c = Vector3.Dot(t.TransformPoint(vs[v]), wdir);
                    if (c < lo) lo = c;
                    if (c > hi) hi = c;
                }
            }
        }

        internal static void MeshToLists(Mesh m, out List<Vector3> V, out List<Vector3> N,
            out List<Vector4> T, out List<Vector2> U, out List<int> tris)
        {
            Vector3[] v = m.vertices; V = new List<Vector3>(v);
            Vector3[] n = m.normals; N = (n != null && n.Length == v.Length) ? new List<Vector3>(n) : null;
            Vector4[] t4 = m.tangents; T = (t4 != null && t4.Length == v.Length) ? new List<Vector4>(t4) : null;
            Vector2[] uv = m.uv; U = (uv != null && uv.Length == v.Length) ? new List<Vector2>(uv) : null;
            tris = new List<int>(m.triangles);
        }

        internal static void ClipSoup(List<Vector3> V, List<Vector3> N, List<Vector4> T, List<Vector2> U,
            Transform rt, Vector3 wdir, float cw, List<int> inTris, List<int> keep, List<int> drop,
            bool normalPlane = false)
        {
            keep.Clear(); drop.Clear();
            List<int> rp = new List<int>(4), op = new List<int>(4);
            int[] idx = new int[3]; float[] dd = new float[3];
            for (int t = 0; t + 2 < inTris.Count; t += 3)
            {
                idx[0] = inTris[t]; idx[1] = inTris[t + 1]; idx[2] = inTris[t + 2];
                for (int k = 0; k < 3; k++) dd[k] = Vector3.Dot(normalPlane
                    ? rt.TransformDirection(N[idx[k]]).normalized : rt.TransformPoint(V[idx[k]]), wdir) - cw;
                rp.Clear(); op.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int cur = idx[k], nxt = idx[(k + 1) % 3];
                    float dc = dd[k], dn = dd[(k + 1) % 3];
                    if (dc >= 0f) rp.Add(cur); else op.Add(cur);
                    if ((dc >= 0f) != (dn >= 0f))
                    {
                        float tt = dc / (dc - dn);
                        int vi = V.Count;
                        V.Add(Vector3.Lerp(V[cur], V[nxt], tt));
                        if (N != null) N.Add(Vector3.Lerp(N[cur], N[nxt], tt).normalized);
                        if (T != null) T.Add(Vector4.Lerp(T[cur], T[nxt], tt));
                        if (U != null) U.Add(Vector2.Lerp(U[cur], U[nxt], tt));
                        rp.Add(vi); op.Add(vi);
                    }
                }
                for (int i = 1; i + 1 < rp.Count; i++) { keep.Add(rp[0]); keep.Add(rp[i]); keep.Add(rp[i + 1]); }
                for (int i = 1; i + 1 < op.Count; i++) { drop.Add(op[0]); drop.Add(op[i]); drop.Add(op[i + 1]); }
            }
        }
        void ClipRegionFacing(List<Vector3> v,List<Vector3> n,List<Vector4> t,List<Vector2> uv,
            Renderer renderer,string region,ref List<int> painted,List<int> rest)
        {
            Vector3 direction;float threshold;
            if(n==null || !WeatherCache.RegionFacingPlane(region,out direction,out threshold))return;
            List<int> keep=new List<int>(),drop=new List<int>();
            ClipSoup(v,n,t,uv,renderer.transform,part.transform.TransformDirection(direction),threshold,
                painted,keep,drop,true);
            painted=keep;rest.AddRange(drop);
        }
        bool AssignRegionSplit(Renderer r, Material[] work, Material[] pris, bool geom,
            Vector3 wdir, float cw, Vector3 upDir, float cwHeight, bool useHeight, int target)
        {
            if (!geom || target == 2 || work.Length != 1 || pris.Length != 1 || pris[0] == null) return false;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            if (!origMesh.ContainsKey(r)) { Mesh pm = PrefabMesh(r); origMesh[r] = (pm != null) ? pm : mf.sharedMesh; }
            Mesh src = origMesh[r];
            if (src == null || !src.isReadable || src.subMeshCount != 1) return false;
            try
            {
                List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> tris;
                MeshToLists(src, out V, out N, out T, out U, out tris);
                List<int> keep1 = new List<int>(), drop1 = new List<int>();
                ClipSoup(V, N, T, U, r.transform, wdir, cw, tris, keep1, drop1);
                List<int> painted, stock;
                if (useHeight)
                {
                    List<int> keep2 = new List<int>(), drop2 = new List<int>();
                    ClipSoup(V, N, T, U, r.transform, -upDir, -cwHeight, keep1, keep2, drop2);
                    painted = keep2; stock = drop1; stock.AddRange(drop2);
                }
                else { painted = keep1; stock = drop1; }
                if (painted.Count == 0 || stock.Count == 0) return false;
                Mesh nm = new Mesh(); nm.name = SplitMeshPrefix;
                if (V.Count > 65000) nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                nm.vertices = V.ToArray();
                if (N != null) nm.normals = N.ToArray();
                if (T != null) nm.tangents = T.ToArray();
                if (U != null) nm.uv = U.ToArray();
                nm.subMeshCount = 2;
                nm.SetTriangles(painted, 0);
                nm.SetTriangles(stock, 1);
                nm.RecalculateBounds();
                mf.sharedMesh = nm; splitMeshes.Add(nm);
                Material stockMat = new Material(pris[0]); created.Add(stockMat);

                r.materials = (target == 1) ? new Material[] { stockMat, work[0] } : new Material[] { work[0], stockMat };
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] region split failed: " + e.Message); return false; }
        }

        void ApplyFinish(Material m, Shader tu) { ApplyFinish(m, tu, rdxMetal, rdxSmooth); }
        void ApplyFinish(Material m, Shader tu, float metal, float smooth)
        {
            Texture mt = m.HasProperty(IdMainTex) ? m.GetTexture(IdMainTex) : null;
            Texture bt = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
            int origQ = m.renderQueue;
            m.shader = tu;
            m.renderQueue = origQ;
            if (mt != null) m.SetTexture(IdMainTex, mt);
            if (bt != null) m.SetTexture(IdBumpMap, bt);
            FillEmptyTuSlots(m);
            m.SetFloat(IdMetal, metal);
            m.SetFloat(IdSmooth, smooth);
        }

        public void Rebuild()
        {
            if (part == null || excludedPart) return;

            if (!Active) { RestoreOriginals(); return; }
            RebuildInner();
            if (WeatherCache.NeedSweep()) SweepCaches();
        }
        void RebuildInner()
        {
            {
                List<Zone> _ex = ParseZones(rdxZones); bool _fd = false;
                if (_ex.Count == 0) { Vector3 _d; float _c, _f, _h; if (WeatherCache.RegionAxisDir(rdxRegion, out _d, out _c, out _f, out _h)) _fd = _f > 0.001f; }
                if (_ex.Count > 0 || _fd) { RebuildMulti(_ex); return; }
            }
            List<Renderer> rs = OwnRenderers();
            Shader tu = rdxFinish ? ActiveTuShader() : null;
            Color col = new Color(rdxR, rdxG, rdxB);

            string regionPlace = rdxRegion;
            bool pickMode = regionPlace != null && regionPlace.StartsWith("pick|");
            string pickTarget = pickMode ? regionPlace.Substring(5) : null;
            bool regionMode = !string.IsNullOrEmpty(regionPlace) && regionPlace != "whole";
            bool geomRegion = regionMode && !pickMode;
            string bakeStack = StripPlaces(rdxStack);
            RestoreMeshesToOriginal();
            Vector3 wdir = Vector3.up; float cw = 0f;
            Vector3 upDir = part.transform.up; float cwHeight = 0f; bool useHeight = false;
            if (geomRegion)
            {
                Vector3 ldir; float lcut, lfade, lheight;
                if (WeatherCache.RegionAxisDir(regionPlace, out ldir, out lcut, out lfade, out lheight))
                {
                    float rlo, rhi;
                    wdir = part.transform.TransformDirection(ldir).normalized;
                    RegionExtent(rs, wdir, out rlo, out rhi);
                    cw = rlo + lcut * (rhi - rlo);
                    if (lheight < 0.999f)
                    {
                        float vlo, vhi;
                        RegionExtent(rs, upDir, out vlo, out vhi);
                        cwHeight = vlo + lheight * (vhi - vlo);
                        useHeight = true;
                    }
                }
                else geomRegion = false;
            }

            List<Material> prev = new List<Material>(created);
            created.Clear();

            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                Material[] pris = EnsurePristine(r);

                Material[] work = new Material[pris.Length];
                for (int j = 0; j < pris.Length; j++)
                {
                    if (pris[j] == null) { work[j] = null; continue; }
                    MeshFilter bakeFilter = r.GetComponent<MeshFilter>();
                    Mesh bakeMesh = bakeFilter != null ? bakeFilter.sharedMesh : null;
                    WeatherCache.BakeProjection projection = MeshProjection(r, bakeMesh, j, pris[j]);
                    Material m = new Material(pris[j]);
                    created.Add(m);
                    bool glass = IsGlass(m) && !isFairing;
                    bool pickPaint = true;
                    if (pickMode)
                    {
                        bool mtc = RelPath(r.transform, part.transform) + "#" + j == pickTarget;
                        pickPaint = (rdxShineSpot == 2) || (rdxShineSpot == 1 ? !mtc : mtc);
                    }
                    if (!glass && pickPaint)
                    {
                        if (rdxFinish && tu != null)
                            ApplyFinish(m, tu);
                        if (rdxKeepShiny && (rdxColored || WeatherOn) && m.HasProperty(IdMainTex)
                            && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
                        {

                            Texture orig = m.GetTexture(IdMainTex);
                            if (orig != null)
                            {
                                Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                                Texture dirt = orig;
                                if (WeatherOn)
                                {
                                    Texture wantB;
                                    Texture want = WeatherCache.Baked(orig, ob, bakeStack, part,
                                        rdxScale, out wantB, projection);
                                    if (want != null) dirt = want;
                                    if (wantB != null && m.HasProperty(IdBumpMap))
                                    {

                                        Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                                        m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                                    }
                                }
                                Texture masked = WeatherCache.MaskWindows(dirt, orig, rdxColored, col);
                                if (masked != null) m.SetTexture(IdMainTex, masked);
                            }
                        }
                        else
                        {
                            if (rdxColored && m.HasProperty(IdColor)) m.SetColor(IdColor, col);
                            if (WeatherOn && m.HasProperty(IdMainTex))
                            {
                                Texture orig = m.GetTexture(IdMainTex);
                                if (orig != null)
                                {
                                    Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                                    Texture wantB;
                                    Texture want = WeatherCache.Baked(orig, ob, bakeStack, part,
                                        rdxScale, out wantB, projection);
                                    if (want != null) m.SetTexture(IdMainTex, want);
                                    if (wantB != null && m.HasProperty(IdBumpMap)) m.SetTexture(IdBumpMap, wantB);
                                }
                            }
                        }
                    }
                    work[j] = m;
                }
                if (!AssignRegionSplit(r, work, pris, geomRegion, wdir, cw, upDir, cwHeight, useHeight, rdxShineSpot))
                    r.materials = work;
            }

            for (int i = 0; i < prev.Count; i++)
                if (prev[i] != null) Destroy(prev[i]);
            if (isFairing) lastRenderCount = ModelRenderCount();
        }

        public class Zone
        {
            public string region = "whole";
            public bool finish; public float metal, smooth;
            public bool colored; public float r = 1f, g = 1f, b = 1f;
            public string stack = ""; public int shineSpot; public float scale = 1f;
            public bool HasContent { get { return finish || colored || !string.IsNullOrEmpty(stack); } }
            public Color Col { get { return new Color(r, g, b); } }
        }
        static string FS(float f) { return f.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        static float FP(string x) { float f; float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f); return f; }
        static string SerZones(List<Zone> zs)
        {
            if (zs == null || zs.Count == 0) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < zs.Count; i++)
            {
                if (i > 0) sb.Append('^');
                Zone z = zs[i];
                sb.Append(z.region).Append('~').Append(z.finish ? '1' : '0').Append('~').Append(FS(z.metal)).Append('~').Append(FS(z.smooth))
                  .Append('~').Append(z.colored ? '1' : '0').Append('~').Append(FS(z.r)).Append('~').Append(FS(z.g)).Append('~').Append(FS(z.b))
                  .Append('~').Append(z.shineSpot).Append('~').Append(FS(z.scale)).Append('~').Append(z.stack == null ? "" : z.stack);
            }
            return sb.ToString();
        }
        static List<Zone> ParseZones(string str)
        {
            List<Zone> list = new List<Zone>();
            if (string.IsNullOrEmpty(str)) return list;
            string[] ps = str.Split('^');
            for (int i = 0; i < ps.Length; i++)
            {
                string[] f = ps[i].Split('~');
                if (f.Length < 11) continue;
                Zone z = new Zone();
                z.region = f[0]; z.finish = f[1] == "1"; z.metal = FP(f[2]); z.smooth = FP(f[3]);
                z.colored = f[4] == "1"; z.r = FP(f[5]); z.g = FP(f[6]); z.b = FP(f[7]);
                int.TryParse(f[8], out z.shineSpot); z.scale = FP(f[9]); z.stack = f[10];
                list.Add(z);
            }
            return list;
        }
        Zone ActiveZone()
        {
            Zone z = new Zone();
            z.region = string.IsNullOrEmpty(rdxRegion) ? "whole" : rdxRegion;
            z.finish = rdxFinish; z.metal = rdxMetal; z.smooth = rdxSmooth;
            z.colored = rdxColored; z.r = rdxR; z.g = rdxG; z.b = rdxB;
            z.stack = rdxStack == null ? "" : rdxStack; z.shineSpot = rdxShineSpot; z.scale = rdxScale;
            return z;
        }
        void LoadZoneIntoFields(Zone z)
        {
            rdxRegion = string.IsNullOrEmpty(z.region) ? "whole" : z.region;
            rdxFinish = z.finish; rdxMetal = z.metal; rdxSmooth = z.smooth;
            rdxColored = z.colored; rdxR = z.r; rdxG = z.g; rdxB = z.b;
            rdxStack = z.stack == null ? "" : z.stack; rdxShineSpot = z.shineSpot; rdxScale = z.scale;
        }

        public void SetRegion(string reg)
        {
            if (string.IsNullOrEmpty(reg)) reg = "whole";
            if (reg == rdxRegion) return;
            List<Zone> ex = ParseZones(rdxZones);
            Zone act = ActiveZone();
            ex.RemoveAll(delegate(Zone zz) { return zz.region == act.region; });
            if (act.HasContent) ex.Add(act);
            Zone tgt = ex.Find(delegate(Zone zz) { return zz.region == reg; });
            if (tgt != null) ex.Remove(tgt); else tgt = new Zone();
            tgt.region = reg;
            LoadZoneIntoFields(tgt);
            rdxZones = SerZones(ex);
        }

        Material[] EnsurePristine(Renderer r)
        {
            Material[] pris;
            if (pristine.TryGetValue(r, out pris)) return pris;
            // Capture the real live shared array before asking Unity for any instantiated material.
            // Renderer.materials creates and assigns clones, which would otherwise make the
            // supposed "original" restoration target another generated object.
            Material[] live = r.sharedMaterials;
            Material[] cur = live;
            Material[] stock = PrefabMats(r);
            bool curPainted = false;
            for (int cj = 0; cj < cur.Length; cj++)
                if (cur[cj] != null && cur[cj].HasProperty(IdMainTex) && WeatherCache.IsBaked(cur[cj].GetTexture(IdMainTex))) { curPainted = true; break; }
            bool useStock = curPainted && stock != null && stock.Length == cur.Length;
            Material[] os = new Material[cur.Length];
            pris = new Material[cur.Length];
            for (int j = 0; j < cur.Length; j++)
            {
                Material sm = useStock ? stock[j] : cur[j];
                pris[j] = (sm != null) ? new Material(sm) : null;
                int liveQ = (cur[j] != null) ? cur[j].renderQueue : -1;
                if (pris[j] != null && liveQ != -1) pris[j].renderQueue = liveQ;
                Material o = useStock ? stock[j] : ((j < live.Length) ? live[j] : null);
                if (useStock && o != null && liveQ != -1 && o.renderQueue != liveQ) { o = new Material(o); o.renderQueue = liveQ; }
                os[j] = o;
            }
            origShared[r] = os;
            pristine[r] = pris;
            return pris;
        }

        Zone MergeBase(Zone baseZ, Zone z)
        {
            if (baseZ == null || !baseZ.HasContent) return z;
            Zone m = new Zone();
            m.region = z.region;
            m.finish = z.finish || baseZ.finish;
            if (z.finish) { m.metal = z.metal; m.smooth = z.smooth; }
            else { m.metal = baseZ.metal; m.smooth = baseZ.smooth; }
            if (z.colored) { m.colored = true; m.r = z.r; m.g = z.g; m.b = z.b; }
            else if (baseZ.colored) { m.colored = true; m.r = baseZ.r; m.g = baseZ.g; m.b = baseZ.b; }
            string bs = baseZ.stack == null ? "" : baseZ.stack;
            string zs = z.stack == null ? "" : z.stack;
            m.stack = (bs == "") ? zs : (zs == "" ? bs : bs + ";" + zs);
            m.shineSpot = z.shineSpot;
            m.scale = (z.scale != 1f) ? z.scale : baseZ.scale;
            return m;
        }

        public void ClearWeatherAll()
        {
            rdxStack = "";
            List<Zone> ex = ParseZones(rdxZones);
            for (int i = 0; i < ex.Count; i++) ex[i].stack = "";
            ex.RemoveAll(delegate(Zone zz) { return !zz.HasContent; });
            rdxZones = SerZones(ex);
            UpdateHeatBuff();
            Rebuild();
        }
        Material StockCopy(Material pristine)
        {
            Material m = new Material(pristine); created.Add(m); return m;
        }

        WeatherCache.BakeProjection MeshProjection(Renderer renderer, Mesh mesh, int materialSlot,
            Material material)
        {
            if (renderer == null || mesh == null || !mesh.isReadable || mesh.subMeshCount <= 0)
                return null;
            return WeatherCache.CreateProjection(part, renderer, mesh,
                Mathf.Clamp(materialSlot, 0, mesh.subMeshCount - 1), material);
        }

        WeatherCache.BakeProjection SoupProjection(Renderer renderer, List<Vector3> vertices,
            List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Material material)
        {
            if (renderer == null || vertices == null || uvs == null || triangles == null
                || vertices.Count == 0 || uvs.Count != vertices.Count || triangles.Count < 3)
                return null;
            return WeatherCache.CreateProjection(part, renderer, vertices, normals, uvs, triangles,
                material);
        }

        Material BlitStock(Material pristine)
        {
            Material m = new Material(pristine); created.Add(m);
            if (m.HasProperty(IdMainTex))
            {
                Texture bc = WeatherCache.BlitCopy(m.GetTexture(IdMainTex));
                if (bc != null) m.SetTexture(IdMainTex, bc);
            }
            return m;
        }

        static string ScaleStack(string stack, float s)
        {
            if (string.IsNullOrEmpty(stack) || s >= 0.999f) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length < 3) continue;
                int ol; if (!int.TryParse(f[2], out ol)) continue;
                float mul = WeatherCache.StackStrength(ol);
                int nl = Mathf.Clamp(Mathf.RoundToInt(s * mul * 100f), 1, 250);
                f[2] = (1000 + nl).ToString(System.Globalization.CultureInfo.InvariantCulture);
                es[i] = string.Join(":", f);
            }
            return string.Join(";", es);
        }

        Material BuildZoneMaterial(Material pristineMat, Zone z) { return BuildZoneMaterial(pristineMat, z, 1f, false, null); }
        Material BuildZoneMaterial(Material pristineMat, Zone z, WeatherCache.BakeProjection projection)
        { return BuildZoneMaterial(pristineMat, z, 1f, false, projection); }
        Material BuildZoneMaterial(Material pristineMat, Zone z, float strength) { return BuildZoneMaterial(pristineMat, z, strength, false, null); }
        Material BuildZoneMaterial(Material pristineMat, Zone z, float strength, bool exactRegion)
        { return BuildZoneMaterial(pristineMat, z, strength, exactRegion, null); }
        Material BuildZoneMaterial(Material pristineMat, Zone z, float strength, bool exactRegion,
            WeatherCache.BakeProjection projection)
        {
            projection = WeatherCache.ForRegion(projection, z != null ? z.region : null);
            Material m = new Material(pristineMat); created.Add(m);
            if (IsGlass(m) && !isFairing) return m;
            bool weatherOn = !string.IsNullOrEmpty(z.stack);
            string bakeStack = ScaleStack(StripPlaces(z.stack), strength);
            Shader tu = z.finish ? ActiveTuShader() : null;
            if (z.finish && tu != null) ApplyFinish(m, tu, z.metal * strength, z.smooth * strength);
            Color col = z.Col;
            if (rdxKeepShiny && (z.colored || weatherOn) && m.HasProperty(IdMainTex)
                && WeatherCache.HasShiny(m.GetTexture(IdMainTex)))
            {
                Texture orig = m.GetTexture(IdMainTex);
                if (orig != null)
                {
                    Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                    Texture dirt = orig;
                    if (weatherOn)
                    {
                        Texture wantB;
                        Texture want = WeatherCache.Baked(orig, ob, bakeStack, part, z.scale, out wantB, projection);
                        if (want != null) dirt = want;
                        if (wantB != null && m.HasProperty(IdBumpMap))
                        {
                            Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                            m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                        }
                    }
                    Texture masked = WeatherCache.MaskWindows(dirt, orig, z.colored, Color.Lerp(Color.white, col, strength));
                    if (masked != null) m.SetTexture(IdMainTex, masked);
                }
            }
            else
            {
                if (z.colored && m.HasProperty(IdColor)) m.SetColor(IdColor, Color.Lerp(m.GetColor(IdColor), col, strength));
                if (weatherOn && m.HasProperty(IdMainTex))
                {
                    Texture orig = m.GetTexture(IdMainTex);
                    if (orig != null)
                    {
                        Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                        Texture wantB;
                        Texture want = WeatherCache.Baked(orig, ob, bakeStack, part, z.scale, out wantB, projection);
                        if (want != null) m.SetTexture(IdMainTex, want);
                        if (wantB != null && m.HasProperty(IdBumpMap)) m.SetTexture(IdBumpMap, wantB);
                    }
                }
            }
            if (exactRegion) ApplyPaintSurfaceProperties(m, z);
            return m;
        }

        static string TextureLeaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path;
        }

        static HeatTileOverlayPrototype.HeatTileTextureDefinition PaintDefinition(Zone z)
        {
            if (z == null || string.IsNullOrEmpty(z.stack)) return null;
            string[] entries = z.stack.Split(';');
            for (int e = entries.Length - 1; e >= 0; e--)
            {
                string[] fields = entries[e].Split(':');
                if (fields.Length < 1 || string.IsNullOrEmpty(fields[0])) continue;
                if (fields[0].StartsWith("tint#", StringComparison.OrdinalIgnoreCase))
                    return HeatTileOverlayPrototype.HeatTileTextureLibrary.Find("porkjet_matte");
                HeatTileOverlayPrototype.HeatTileTextureDefinition definition;
                if (HeatTileOverlayPrototype.HeatTileTextureLibrary.FindExact(fields[0], out definition)
                    || HeatTileOverlayPrototype.HeatTileTextureLibrary.FindByTextureLeaf(fields[0], out definition))
                    return definition;
            }
            return null;
        }

        static void ApplyPaintSurfaceProperties(Material material, Zone z)
        {
            HeatTileOverlayPrototype.HeatTileTextureDefinition definition = PaintDefinition(z);
            if (material == null || definition == null) return;
            float specularity = Mathf.Clamp01(definition.Specularity);
            float shininess = Mathf.Clamp01(definition.Shininess);
            if (definition.IsFlatColor)
            {
                string[] mapProperties = {
                    "_SpecMap", "_SpecGlossMap", "_MetallicGlossMap", "_MetalMap",
                    "_MetallicMap", "_GlossMap"
                };
                for (int i = 0; i < mapProperties.Length; i++)
                    if (material.HasProperty(mapProperties[i]))
                        material.SetTexture(mapProperties[i], Texture2D.blackTexture);
                material.DisableKeyword("_SPECGLOSSMAP");
                material.DisableKeyword("_METALLICGLOSSMAP");
                material.DisableKeyword("_METALLIC_MAP");
            }
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_SpecColor"))
                material.SetColor("_SpecColor", new Color(specularity, specularity, specularity, 1f));
            if (material.HasProperty("_ReflectColor"))
                material.SetColor("_ReflectColor", new Color(specularity, specularity, specularity, 1f));
            if (material.HasProperty("_Shininess")) material.SetFloat("_Shininess", shininess);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", shininess);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", shininess);
            if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", shininess);
        }
        static string HexRGB(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}",
                Mathf.Clamp((int)(c.r * 255f), 0, 255),
                Mathf.Clamp((int)(c.g * 255f), 0, 255),
                Mathf.Clamp((int)(c.b * 255f), 0, 255));
        }

        static string PlaceStack(string stack, string place)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) { f[1] = place; es[i] = string.Join(":", f); }
            }
            return string.Join(";", es);
        }

        static bool MaskUsable(float[] m)
        {
            if (m == null) return false;
            for (int i = 0; i < m.Length; i++) if (m[i] > 0.02f) return true;
            return false;
        }
        Material BuildSmoothFadeMaterial(Material baseMat, Zone z, float[] customMask)
        {
            return BuildSmoothFadeMaterial(baseMat, z, customMask, false, null);
        }

        Material BuildSmoothFadeMaterial(Material baseMat, Zone z, float[] customMask,
            bool exactRegion)
        { return BuildSmoothFadeMaterial(baseMat, z, customMask, exactRegion, null); }

        Material BuildSmoothFadeMaterial(Material baseMat, Zone z, float[] customMask,
            bool exactRegion, WeatherCache.BakeProjection projection)
        {
            projection = WeatherCache.ForRegion(projection, z != null ? z.region : null);
            Material m = new Material(baseMat); created.Add(m);
            if (IsGlass(m) && !isFairing) return m;
            if (!m.HasProperty(IdMainTex)) return m;
            Texture orig = m.GetTexture(IdMainTex);
            if (orig == null) return m;
            string stack = "";
            if (z.colored) stack = "tint#" + HexRGB(z.Col) + ":" + z.region + ":2";
            string dirt = PlaceStack(z.stack, z.region);
            if (!string.IsNullOrEmpty(dirt)) stack = (stack == "") ? dirt : stack + ";" + dirt;
            if (stack != "")
            {
                Texture ob = m.HasProperty(IdBumpMap) ? m.GetTexture(IdBumpMap) : null;
                Texture wantB;
                Texture want = WeatherCache.Baked(orig, ob, stack, part, z.scale, out wantB,
                    customMask, projection);
                Texture result = (want != null) ? want : orig;
                if (rdxKeepShiny && WeatherCache.HasShiny(orig))
                {
                    Texture masked = WeatherCache.MaskWindows(result, orig, false, Color.white);
                    if (masked != null) result = masked;
                }
                m.SetTexture(IdMainTex, result);
                if (wantB != null && m.HasProperty(IdBumpMap))
                {
                    if (rdxKeepShiny && WeatherCache.HasShiny(orig))
                    {
                        Texture mb = WeatherCache.MaskWindowsBump(wantB, ob, orig);
                        m.SetTexture(IdBumpMap, mb != null ? mb : wantB);
                    }
                    else m.SetTexture(IdBumpMap, wantB);
                }
            }

            if (z.finish)
            {
                Shader tu = ActiveTuShader();
                if (tu != null)
                {
                    ApplyFinish(m, tu, 1f, 1f);
                    Texture2D mm = WeatherCache.MetallicMask(part, z.region, z.metal, z.smooth, customMask);
                    if (mm != null && m.HasProperty(IdMetalGloss)) m.SetTexture(IdMetalGloss, mm);
                }
            }
            if (exactRegion) ApplyPaintSurfaceProperties(m, z);
            return m;
        }

        bool ZoneGeom(string region, List<Renderer> rs, out Vector3 wdir, out float cw,
            Vector3 upDir, out float cwHeight, out bool useHeight, out float fadeAxisW, out float fadeHeightW)
        {
            wdir = Vector3.up; cw = 0f; cwHeight = 0f; useHeight = false; fadeAxisW = 0f; fadeHeightW = 0f;
            Vector3 ldir; float lcut, lfade, lheight;
            if (!WeatherCache.RegionAxisDir(region, out ldir, out lcut, out lfade, out lheight)) return false;
            float rlo, rhi;
            wdir = part.transform.TransformDirection(ldir).normalized;
            RegionExtent(rs, wdir, out rlo, out rhi);
            cw = rlo + lcut * (rhi - rlo);
            fadeAxisW = lfade * Mathf.Max(1e-4f, rhi - cw);
            if (lheight < 0.999f)
            {
                float vlo, vhi;
                RegionExtent(rs, upDir, out vlo, out vhi);
                cwHeight = vlo + lheight * (vhi - vlo);
                useHeight = true;
                fadeHeightW = lfade * Mathf.Max(1e-4f, cwHeight - vlo);
            }
            return true;
        }

        internal Mesh PreviewSourceMesh(Renderer renderer, Mesh fallback)
        {
            Mesh source;
            return origMesh.TryGetValue(renderer, out source) && source != null ? source : fallback;
        }

        // Extend the existing geometric partition to reserve unpaintable triangles in their
        // original material. This keeps an interior original even when it reuses cover UVs.
        void RebuildExterior(Renderer renderer, Mesh source, Material[] pristineMaterials,
            List<Zone> zones, List<Renderer> renderers)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || source == null || !source.isReadable) return;
            try
            {
                List<Vector3> vertices, normals; List<Vector4> tangents; List<Vector2> uvs; List<int> ignored;
                MeshToLists(source, out vertices, out normals, out tangents, out uvs, out ignored);
                List<List<int>> output = new List<List<int>>();
                List<Material> materials = new List<Material>();
                for (int slot=0; slot<source.subMeshCount; slot++)
                {
                    Material original = pristineMaterials[Math.Min(slot, pristineMaterials.Length-1)];
                    int[] triangles=source.GetTriangles(slot);
                    bool[] exterior=HeatTileOverlayPrototype.ExteriorPaintPolicy.Triangles(part,renderer,source,slot);
                    List<int> pool=new List<int>(), untouched=new List<int>();
                    for(int i=0;i+2<triangles.Length;i+=3)
                    {
                        bool paint=original!=null && !IsGlass(original) && exterior!=null && i/3<exterior.Length && exterior[i/3];
                        List<int> target=paint?pool:untouched;
                        target.Add(triangles[i]); target.Add(triangles[i+1]); target.Add(triangles[i+2]);
                    }
                    for(int z=zones.Count-1;z>=0 && pool.Count>0;z--)
                    {
                        Zone zone=zones[z];
                        Vector3 direction; float cut,cutHeight,fade,fadeHeight; bool useHeight;
                        Vector3 up=part.transform.up;
                        if(!ZoneGeom(zone.region,renderers,out direction,out cut,up,out cutHeight,out useHeight,out fade,out fadeHeight)) continue;
                        List<int> painted=new List<int>(), rest=new List<int>();
                        ClipSoup(vertices,normals,tangents,uvs,renderer.transform,direction,cut,pool,painted,rest);
                        if(useHeight)
                        {
                            List<int> kept=new List<int>(), rejected=new List<int>();
                            ClipSoup(vertices,normals,tangents,uvs,renderer.transform,-up,-cutHeight,painted,kept,rejected);
                            painted=kept; rest.AddRange(rejected);
                        }
                        ClipRegionFacing(vertices,normals,tangents,uvs,renderer,zone.region,ref painted,rest);
                        pool=rest;
                        if(painted.Count==0) continue;
                        List<List<int>> uvBatches = WeatherCache.HasWorldScaledLayer(zone.stack) || fade>1e-4f
                            ? HeatTileOverlayPrototype.UvPaintPartition.Split(uvs, painted,
                                original.mainTextureScale, original.mainTextureOffset)
                            : new List<List<int>> { painted };
                        foreach (List<int> uvBatch in uvBatches)
                        {
                        painted = uvBatch;
                        WeatherCache.BakeProjection projection=SoupProjection(renderer,vertices,normals,uvs,painted,original);
                        Material material;
                        if(fade>1e-4f && uvs!=null && uvs.Count==vertices.Count)
                        {
                            List<float> weights=new List<float>(vertices.Count);
                            for(int v=0;v<vertices.Count;v++)
                            {
                                Vector3 world=renderer.transform.TransformPoint(vertices[v]);
                                float weight=Mathf.Clamp01((Vector3.Dot(world,direction)-cut)/fade);
                                if(useHeight) weight*=Mathf.Clamp01((cutHeight-Vector3.Dot(world,up))/fadeHeight);
                                if(normals!=null && v<normals.Count) weight*=WeatherCache.RegionFacingWeight(zone.region,part.transform.InverseTransformDirection(renderer.transform.TransformDirection(normals[v])).normalized);
                                weights.Add(weight);
                            }
                            float[] mask=WeatherCache.RasterMask(uvs,weights,painted);
                            material=BuildSmoothFadeMaterial(original,zone,mask,true,projection);
                        }
                        else material=BuildZoneMaterial(original,zone,1f,true,projection);
                        output.Add(painted); materials.Add(material);
                        }
                    }
                    untouched.AddRange(pool);
                    if(untouched.Count>0) { output.Add(untouched); materials.Add(original); }
                }
                if(output.Count==0) return;
                Mesh result=new Mesh(); result.name=SplitMeshPrefix + "-Exterior";
                if(vertices.Count>65000) result.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
                result.SetVertices(vertices); if(normals!=null) result.SetNormals(normals);
                if(tangents!=null) result.SetTangents(tangents); if(uvs!=null) result.SetUVs(0,uvs);
                result.subMeshCount=output.Count;
                for(int i=0;i<output.Count;i++) result.SetTriangles(output[i],i,false);
                result.RecalculateBounds(); filter.sharedMesh=result; splitMeshes.Add(result);
                renderer.sharedMaterials=materials.ToArray();
            }
            catch(Exception exception)
            {
                filter.sharedMesh=source; renderer.sharedMaterials=pristineMaterials;
                Debug.LogWarning("[HeatTilePainter] Exterior paint safely declined: "+exception.Message);
            }
        }

        void RebuildMulti(List<Zone> extras)
        {
            List<Renderer> rs = OwnRenderers();
            RestoreMeshesToOriginal();
            // Resolve every original before any renderer acquires a disposable split.
            foreach (Renderer sourceRenderer in rs)
                if (!origMesh.ContainsKey(sourceRenderer))
                {
                    MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
                    Mesh prefabSource = PrefabMesh(sourceRenderer);
                    origMesh[sourceRenderer] = prefabSource != null ? prefabSource
                        : (sourceFilter == null ? null : sourceFilter.sharedMesh);
                }
            List<Zone> zones = new List<Zone>(extras);
            Zone act = ActiveZone();
            if (act.HasContent) { zones.RemoveAll(delegate(Zone zz) { return zz.region == act.region; }); zones.Add(act); }
            Zone whole = zones.Find(delegate(Zone z) { return z.region == "whole" || string.IsNullOrEmpty(z.region); });
            Zone baseZone = zones.Find(delegate(Zone z) { return z.region == "base"; });

            List<Material> prev = new List<Material>(created); created.Clear();
            for (int i = 0; i < rs.Count; i++)
            {
                Renderer r = rs[i];
                Material[] pris = EnsurePristine(r);
                if (pris.Length == 0) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!origMesh.ContainsKey(r)) { Mesh pm = PrefabMesh(r); origMesh[r] = (pm != null) ? pm : (mf != null ? mf.sharedMesh : null); }
                Mesh src = origMesh[r];
                if (HeatTileOverlayPrototype.ExteriorPaintPolicy.IsDeployable(part))
                {
                    RebuildExterior(r, src, pris, zones, rs);
                    continue;
                }
                bool singleSub = mf != null && src != null && src.isReadable && src.subMeshCount == 1 && pris.Length == 1 && pris[0] != null;

                List<Zone> geo = zones.FindAll(delegate(Zone z)
                {
                    return z.region != null && (z.region.StartsWith("g") || z.region.StartsWith("v"));
                });
                List<Zone> pick = zones.FindAll(delegate(Zone z) { return z.region != null && z.region.StartsWith("pick|"); });

                bool hasPickHere = false;
                for (int j = 0; j < pris.Length && !hasPickHere; j++)
                {
                    string pk = "pick|" + RelPath(r.transform, part.transform) + "#" + j;
                    if (pick.Exists(delegate(Zone z) { return z.region == pk; })) hasPickHere = true;
                }

                if (geo.Count > 0 && singleSub && !hasPickHere)
                {
                    try
                    {
                        List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> tris;
                        MeshToLists(src, out V, out N, out T, out U, out tris);
                        WeatherCache.BakeProjection fullProjection = SoupProjection(r, V, N, U,
                            tris, pris[0]);

                        Vector3 upDir0 = part.transform.up;
                        // Do not collapse placed regions into one UV-masked material. Many KSP
                        // models intentionally overlap top/bottom UV islands; a texture-space mask
                        // would then paint both physical surfaces. Keep Reforged's disposable
                        // geometric partition even for soft edges, and apply the fade only inside
                        // the painted partition.
                        bool allFaded = false;
                        for (int z = 0; z < geo.Count && allFaded; z++)
                        {
                            Vector3 gw; float gc, gcH, gfa, gfh; bool guH;
                            if (!ZoneGeom(geo[z].region, rs, out gw, out gc, upDir0, out gcH, out guH, out gfa, out gfh) || gfa <= 1e-4f)
                                allFaded = false;
                        }
                        if (allFaded)
                        {
                            Material combined = (whole != null && whole.HasContent)
                                ? BuildZoneMaterial(pris[0], whole, fullProjection) : pris[0];
                            string fmBase = "fm|" + src.GetInstanceID() + "|g" +
                                WeatherCache.GeometrySignature(part) + "|";
                            for (int z = 0; z < geo.Count; z++)
                            {
                                float[] cm;
                                if (!WeatherCache.TryFadeMask(fmBase + geo[z].region, out cm))
                                {
                                    Vector3 wdir; float cw; float cwH; bool useH; float faW, fhW;
                                    if (!ZoneGeom(geo[z].region, rs, out wdir, out cw, upDir0, out cwH, out useH, out faW, out fhW)) continue;
                                    List<int> zk = new List<int>(), zd = new List<int>();
                                    ClipSoup(V, N, T, U, r.transform, wdir, cw, tris, zk, zd);
                                    List<int> zreg = zk;
                                    if (useH) { List<int> k2 = new List<int>(), d2 = new List<int>(); ClipSoup(V, N, T, U, r.transform, -upDir0, -cwH, zk, k2, d2); zreg = k2; }
                                    if (zreg.Count == 0) continue;
                                    List<float> wv = new List<float>(V.Count);
                                    for (int v = 0; v < V.Count; v++)
                                    {
                                        Vector3 wp = r.transform.TransformPoint(V[v]);
                                        float ww = Mathf.Clamp01((Vector3.Dot(wp, wdir) - cw) / faW);
                                        if (useH) ww *= Mathf.Clamp01((cwH - Vector3.Dot(wp, upDir0)) / fhW);
                                        if (N != null && v < N.Count)
                                        {
                                            Vector3 ln = part.transform.InverseTransformDirection(
                                                r.transform.TransformDirection(N[v])).normalized;
                                            ww *= WeatherCache.RegionFacingWeight(geo[z].region, ln);
                                        }
                                        wv.Add(ww);
                                    }
                                    cm = WeatherCache.RasterMask(U, wv, zreg);
                                    WeatherCache.PutFadeMask(fmBase + geo[z].region, cm);
                                }
                                combined = BuildSmoothFadeMaterial(combined, geo[z],
                                    MaskUsable(cm) ? cm : null, false, fullProjection);
                            }
                            r.materials = new Material[] { combined };
                            continue;
                        }
                        List<int> pool = tris;
                        List<List<int>> subTris = new List<List<int>>();
                        List<Material> subMats = new List<Material>();
                        bool fadedHere = false;
                        geo.Reverse();
                        for (int z = 0; z < geo.Count; z++)
                        {
                            Vector3 wdir; float cw; Vector3 upDir = part.transform.up; float cwH; bool useH; float fadeAxisW, fadeHeightW;
                            if (!ZoneGeom(geo[z].region, rs, out wdir, out cw, upDir, out cwH, out useH, out fadeAxisW, out fadeHeightW)) continue;
                            List<int> keep = new List<int>(), drop = new List<int>();
                            ClipSoup(V, N, T, U, r.transform, wdir, cw, pool, keep, drop);
                            List<int> painted = keep;
                            if (useH)
                            {
                                List<int> k2 = new List<int>(), d2 = new List<int>();
                                ClipSoup(V, N, T, U, r.transform, -upDir, -cwH, keep, k2, d2);
                                painted = k2; drop.AddRange(d2);
                            }
                            ClipRegionFacing(V,N,T,U,r,geo[z].region,ref painted,drop);
                            pool = drop;
                            if (painted.Count == 0) continue;
                            Zone mz = MergeBase(whole, geo[z]);
                            List<List<int>> uvBatches = WeatherCache.HasWorldScaledLayer(mz.stack) || fadeAxisW>1e-4f
                                ? HeatTileOverlayPrototype.UvPaintPartition.Split(U, painted,
                                    pris[0].mainTextureScale, pris[0].mainTextureOffset)
                                : new List<List<int>> { painted };
                            foreach (List<int> uvBatch in uvBatches)
                            {
                            painted = uvBatch;
                            WeatherCache.BakeProjection paintedProjection = SoupProjection(r, V, N,
                                U, painted, pris[0]);
                            if (fadeAxisW > 1e-4f && U != null && U.Count == V.Count)
                            {

                                float[] cm;
                                string fsKey = "fs|" + src.GetInstanceID() + "|g" +
                                    WeatherCache.GeometrySignature(part) + "|" + geo[z].region + "|p" + paintedProjection.Signature;
                                if (!WeatherCache.TryFadeMask(fsKey, out cm))
                                {
                                    List<float> wv = new List<float>(V.Count);
                                    for (int v = 0; v < V.Count; v++)
                                    {
                                        Vector3 wp = r.transform.TransformPoint(V[v]);
                                        float ww = Mathf.Clamp01((Vector3.Dot(wp, wdir) - cw) / fadeAxisW);
                                        if (useH) ww *= Mathf.Clamp01((cwH - Vector3.Dot(wp, upDir)) / fadeHeightW);
                                        if (N != null && v < N.Count)
                                        {
                                            Vector3 ln = part.transform.InverseTransformDirection(
                                                r.transform.TransformDirection(N[v])).normalized;
                                            ww *= WeatherCache.RegionFacingWeight(geo[z].region, ln);
                                        }
                                        wv.Add(ww);
                                    }
                                    cm = WeatherCache.RasterMask(U, wv, painted);
                                    WeatherCache.PutFadeMask(fsKey, cm);
                                }
                                Material baseMat = (whole != null && whole.HasContent)
                                    ? BuildZoneMaterial(pris[0], whole, paintedProjection) : pris[0];
                                subTris.Add(painted); subMats.Add(BuildSmoothFadeMaterial(baseMat,
                                    geo[z], MaskUsable(cm) ? cm : null, true, paintedProjection));
                                fadedHere = true;
                            }
                            else { subTris.Add(painted); subMats.Add(BuildZoneMaterial(pris[0], mz,
                                1f, true, paintedProjection)); }
                            }
                        }
                        if (pool.Count > 0)
                        {
                            Zone lz = (baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole;
                            WeatherCache.BakeProjection restProjection = SoupProjection(r, V, N, U,
                                pool, pris[0]);
                            Material rest = (lz != null && lz.HasContent)
                                          ? BuildZoneMaterial(pris[0], lz, restProjection)
                                          : (fadedHere ? BlitStock(pris[0]) : StockCopy(pris[0]));
                            subTris.Add(pool);
                            subMats.Add(rest);
                        }
                        if (subTris.Count == 0) { r.materials = new Material[] { StockCopy(pris[0]) }; continue; }
                        Mesh nm = new Mesh(); nm.name = SplitMeshPrefix;
                        if (V.Count > 65000) nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                        nm.vertices = V.ToArray();
                        if (N != null) nm.normals = N.ToArray();
                        if (T != null) nm.tangents = T.ToArray();
                        if (U != null) nm.uv = U.ToArray();
                        nm.subMeshCount = subTris.Count;
                        for (int k = 0; k < subTris.Count; k++) nm.SetTriangles(subTris[k], k);
                        nm.RecalculateBounds();
                        mf.sharedMesh = nm; splitMeshes.Add(nm);
                        r.materials = subMats.ToArray();
                    }
                    catch (Exception e) { Debug.LogWarning("[ReforgedRedux] multi-zone split failed: " + e.Message); }
                }
                else if (geo.Count > 0 && !hasPickHere)
                {
                    // Multi-material renderers cannot use the upstream one-submesh partitioner.
                    // Keep their topology entirely untouched and compose every zone into cloned
                    // stock materials using Reforged's cached UV mask/bake path instead.
                    Material[] work = new Material[pris.Length];
                    for (int j = 0; j < pris.Length; j++)
                    {
                        if (pris[j] == null) { work[j] = null; continue; }
                        WeatherCache.BakeProjection slotProjection = MeshProjection(r, src, j,
                            pris[j]);
                        Material combined = (whole != null && whole.HasContent)
                            ? BuildZoneMaterial(pris[j], whole, slotProjection) : StockCopy(pris[j]);
                        for (int z = 0; z < geo.Count; z++)
                            combined = BuildSmoothFadeMaterial(combined, geo[z], null, false,
                                slotProjection);
                        work[j] = combined;
                    }
                    r.materials = work;
                }
                else if (pick.Count > 0)
                {
                    Material[] work = new Material[pris.Length];
                    for (int j = 0; j < pris.Length; j++)
                    {
                        if (pris[j] == null) { work[j] = null; continue; }
                        WeatherCache.BakeProjection slotProjection = MeshProjection(r, src, j,
                            pris[j]);
                        string key = "pick|" + RelPath(r.transform, part.transform) + "#" + j;
                        Zone pz = pick.Find(delegate(Zone z) { return z.region == key; });
                        if (pz != null) work[j] = BuildZoneMaterial(pris[j],
                            MergeBase(whole, pz), slotProjection);
                        else { Zone lz = (baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole; work[j] = (lz != null && lz.HasContent) ? BuildZoneMaterial(pris[j], lz, slotProjection) : StockCopy(pris[j]); }
                    }
                    r.materials = work;
                }
                else
                {
                    Zone lz = (baseZone != null && baseZone.HasContent) ? MergeBase(whole, baseZone) : whole;
                    Material[] work = new Material[pris.Length];
                    for (int j = 0; j < pris.Length; j++)
                    {
                        if (pris[j] == null) { work[j] = null; continue; }
                        WeatherCache.BakeProjection slotProjection = MeshProjection(r, src, j,
                            pris[j]);
                        work[j] = (lz != null && lz.HasContent)
                            ? BuildZoneMaterial(pris[j], lz, slotProjection) : StockCopy(pris[j]);
                    }
                    r.materials = work;
                }
            }
            for (int i = 0; i < prev.Count; i++) if (prev[i] != null) Destroy(prev[i]);
            if (isFairing) lastRenderCount = ModelRenderCount();
        }

        public void SetFinish(float metal, float smooth)
        {
            if (ActiveTuShader() == null)
            {
                ScreenMessages.PostScreenMessage("Reforged: TU/Metallic shader not found (TexturesUnlimited missing?)",
                    5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth;
            Rebuild();
        }
        public void SetColour(Color c)
        {
            rdxColored = true; rdxR = c.r; rdxG = c.g; rdxB = c.b;
            Rebuild();
        }
        public void SetBoth(float metal, float smooth, Color c)
        {
            rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth;
            rdxColored = true; rdxR = c.r; rdxG = c.g; rdxB = c.b;
            Rebuild();
        }
        public void ClearFinish() { rdxFinish = false; Rebuild(); }

        public void ClearColourAndTints()
        {
            rdxColored = false;
            if (WeatherOn)
            {
                List<string> keep = new List<string>();
                string[] es = rdxStack.Split(';');
                for (int i = 0; i < es.Length; i++)
                    if (!es[i].StartsWith("tint#")) keep.Add(es[i]);
                rdxStack = string.Join(";", keep.ToArray());
            }
            Rebuild();
        }

        public void AddWeather(int level, string tex, string place)
        {
            if (string.IsNullOrEmpty(tex)) tex = "soot";
            if (string.IsNullOrEmpty(place)) place = "whole";
            string prefix = tex + ":" + place + ":";
            List<string> es = new List<string>();
            if (WeatherOn) es.AddRange(rdxStack.Split(';'));
            for (int i = es.Count - 1; i >= 0; i--)
            {
                string[] pf = es[i].Split(':');
                if (pf.Length >= 2 && pf[1] != place) es.RemoveAt(i);
            }
            int same = 0;
            for (int i = 0; i < es.Count; i++) if (es[i].StartsWith(prefix)) same++;
            if (same >= 3)
            {
                for (int i = es.Count - 1; i >= 0; i--)
                    if (es[i].StartsWith(prefix)) { es[i] = prefix + level; break; }
            }
            else es.Add(prefix + level);
            while (es.Count > 8) es.RemoveAt(0);
            rdxStack = string.Join(";", es.ToArray());
            UpdateHeatBuff();
            Rebuild();
        }

        public void AppendStack(string add)
        {
            if (!string.IsNullOrEmpty(add))
                rdxStack = string.IsNullOrEmpty(rdxStack) ? add : (rdxStack + ";" + add);
            UpdateHeatBuff();
            Rebuild();
        }
        public void ClearAll()
        {
            ClearAll(false);
        }

        public void ClearAll(bool reapplyVariant)
        {
            RefreshDynamicMeshSources();
            rdxFinish = false; rdxColored = false; rdxStack = ""; rdxZones = "";
            UpdateHeatBuff();
            RestoreOriginals(reapplyVariant);
        }

        // Heat Tile Painter owns the saved layer model and sends a complete derived zone set.
        // Replacing the whole state in one call prevents partially-updated renderer state.
        public void ApplyExternalZones(string zones)
        {
            RefreshDynamicMeshSources();
            rdxFinish = false;
            rdxMetal = 0f;
            rdxSmooth = 0f;
            rdxColored = false;
            rdxR = rdxG = rdxB = 1f;
            rdxKeepShiny = false;
            rdxScale = 1f;
            rdxShineSpot = 0;
            rdxRegion = "whole";
            rdxStack = "";
            rdxZones = zones ?? "";
            UpdateHeatBuff();
            Rebuild();
        }

        void RefreshDynamicMeshSources()
        {
            if (!UsesDynamicMeshSource()) return;
            List<Renderer> rs = OwnRenderers();
            for (int i = 0; i < rs.Count; i++)
            {
                MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || splitMeshes.Contains(mf.sharedMesh)) continue;
                // MeshFilter.mesh clones our shared partition when another mod reads it.
                // Unity preserves its name: never promote that clone to authoritative source.
                if (mf.sharedMesh.name.StartsWith(SplitMeshPrefix, StringComparison.Ordinal))
                {
                    splitMeshes.Add(mf.sharedMesh);
                    continue;
                }
                // A procedural generator has supplied (or retained) the live authoritative mesh.
                // Capture it before Rebuild restores any previous disposable partition.
                origMesh[rs[i]] = mf.sharedMesh;
            }
        }

        List<Zone> AllZones()
        {
            List<Zone> zs = ParseZones(rdxZones);
            Zone a = ActiveZone();
            if (a.HasContent) { zs.RemoveAll(delegate(Zone z) { return z.region == a.region; }); zs.Add(a); }
            return zs;
        }

        public string ComboRecipe()
        {
            List<Zone> zs = AllZones();
            List<string> names = new List<string>();
            for (int zi = 0; zi < zs.Count; zi++)
            {
                if (string.IsNullOrEmpty(zs[zi].stack)) continue;
                string[] es = zs[zi].stack.Split(';');
                for (int ei = 0; ei < es.Length; ei++)
                {
                    int c = es[ei].IndexOf(':');
                    string nm = c > 0 ? es[ei].Substring(0, c) : es[ei];
                    if (string.IsNullOrEmpty(nm)) continue;
                    names.Add(nm);
                }
            }
            return string.Join(";", names.ToArray());
        }
        public bool ComboFinish(out float m, out float sm)
        {
            List<Zone> zs = AllZones();
            for (int i = 0; i < zs.Count; i++) if (zs[i].finish) { m = zs[i].metal; sm = zs[i].smooth; return true; }
            m = 0f; sm = 0f; return false;
        }
        public bool ComboColor(out float cr, out float cg, out float cb)
        {
            List<Zone> zs = AllZones();
            for (int i = 0; i < zs.Count; i++) if (zs[i].colored) { cr = zs[i].r; cg = zs[i].g; cb = zs[i].b; return true; }
            cr = 0f; cg = 0f; cb = 0f; return false;
        }

        public void ApplyComboLayers(string region, int level, string[] layers, bool hasColor, Color col, bool hasFinish, float metal, float smooth, float scale)
        {
            rdxScale = scale;
            if (hasColor) { rdxColored = true; rdxR = col.r; rdxG = col.g; rdxB = col.b; }
            if (layers != null)
                for (int i = 0; i < layers.Length; i++)
                {
                    if (string.IsNullOrEmpty(layers[i])) continue;
                    string entry = layers[i] + ":" + region + ":" + level;
                    rdxStack = string.IsNullOrEmpty(rdxStack) ? entry : (rdxStack + ";" + entry);
                }
            if (hasFinish) { rdxFinish = true; rdxMetal = metal; rdxSmooth = smooth; }
            UpdateHeatBuff();
            Rebuild();
        }

        public void RestoreState(bool fin, float m, float s, bool col, float r, float g, float b, string stack,
            string zones, string region, int spot, float scale)
        {
            rdxFinish = fin; rdxMetal = m; rdxSmooth = s;
            rdxColored = col; rdxR = r; rdxG = g; rdxB = b;
            rdxStack = stack ?? "";
            rdxZones = zones ?? "";
            rdxRegion = string.IsNullOrEmpty(region) ? "whole" : region;
            rdxShineSpot = spot; rdxScale = scale;
            UpdateHeatBuff();
            Rebuild();
        }

        void ForPainted(System.Action<Zone> act)
        {
            List<Zone> zs = ParseZones(rdxZones);
            Zone a = ActiveZone();
            if (a.HasContent) { zs.RemoveAll(delegate(Zone z) { return z.region == a.region; }); zs.Add(a); }
            if (zs.Count == 0) return;
            for (int i = 0; i < zs.Count; i++) act(zs[i]);
            Zone last = zs[zs.Count - 1]; zs.RemoveAt(zs.Count - 1);
            rdxZones = SerZones(zs); LoadZoneIntoFields(last);
            UpdateHeatBuff(); Rebuild();
        }
        public void PaintedFinish(float metal, float smooth, bool alsoCol, Color c)
        {
            ForPainted(delegate(Zone z)
            {
                if (metal < 0f) z.finish = false;
                else { z.finish = true; z.metal = metal; z.smooth = smooth; if (alsoCol) { z.colored = true; z.r = c.r; z.g = c.g; z.b = c.b; } }
            });
        }
        public void PaintedColour(Color c) { ForPainted(delegate(Zone z) { z.colored = true; z.r = c.r; z.g = c.g; z.b = c.b; }); }
        public void PaintedClearColour() { ForPainted(delegate(Zone z) { z.colored = false; }); }
        public void PaintedWeather(int level, string tex, float scale)
        {
            ForPainted(delegate(Zone z)
            {
                if (!string.IsNullOrEmpty(tex) && !tex.StartsWith("tint#")) z.scale = scale;
                string entry = (string.IsNullOrEmpty(tex) ? "soot" : tex) + ":whole:" + level;
                z.stack = string.IsNullOrEmpty(z.stack) ? entry : z.stack + ";" + entry;
            });
        }
        public void UpdateHeatBuff()
        {
            // The upstream weather-stack 2.5x temperature buff is intentionally disabled. Heat
            // Tile Painter owns a persisted, capped, mass-balanced part-level protection option in
            // ModuleHeatTileOverlay; the rendering core must not fight it during every repaint.
        }
    }

    // The upstream painter is intentionally excluded. Heat Tile Painter supplies the only UI and
    // persists the only editable layer stack; this file contributes rendering/cache code only.
#if HEATTILE_REFORGED_UPSTREAM_UI
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class ReforgedPainterEditor : ReforgedPainter { }
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ReforgedPainterFlight : ReforgedPainter { }

    public class ReforgedPainter : MonoBehaviour
    {

        static readonly string[] FinNames  = { "Matte", "SemiGloss", "Satin", "MetallicPaint", "BrushedMetal", "Chrome", "LightChrome" };
        static readonly string[] FinLabels = { "Matte", "Semi-Gloss", "Satin", "Metallic Paint", "Brushed Metal", "Chrome (Mirror)", "Light Chrome" };
        static readonly float[][] FinVals = {
            new float[] { 0.10f, 0.00f }, new float[] { 0.15f, 0.55f }, new float[] { 0.40f, 0.72f },
            new float[] { 0.55f, 0.90f }, new float[] { 0.85f, 0.65f }, new float[] { 0.90f, 1.00f },
            new float[] { 0.45f, 1.00f } };

        static readonly string[] MetalNames = { "Gold", "Silver", "Steel", "Bronze", "Brass", "Platinum" };

        static readonly string[] HeatFiles  = { "heatd1", "heatd2", "heatd3", "heatd4", "heatd5", "heatd6", "heatd7", "heatd8" };
        static readonly string[] HeatNames  = { "Temper", "Oxide Green", "Rose Heat", "Copper Fire", "Rainbow", "Overheated Blue", "Heat Mottle", "Ember" };
        const float HeatSmooth = 0.90f;
        static readonly float[][] MetalRGB = {
            new float[] { 212f, 175f, 55f }, new float[] { 183f, 183f, 183f }, new float[] { 128f, 128f, 128f },
            new float[] { 191f, 149f, 99f }, new float[] { 250f, 230f, 150f }, new float[] { 213f, 208f, 200f } };
        const float MetalSmooth = 176f / 255f;

        static readonly string[] DirtNames = { "Dusty", "Dirty", "Muddy", "Rusty", "Oily", "Scorched", "Burned", "Worn Metal" };
        static readonly float[][] DirtRGB = {
            new float[] { 209f, 199f, 179f }, new float[] { 148f, 128f, 102f },
            new float[] { 115f,  94f,  71f }, new float[] { 148f,  84f,  51f },
            new float[] {  89f,  87f,  82f }, new float[] {  77f,  66f,  59f },
            new float[] {  41f,  38f,  36f }, new float[] { 158f, 158f, 153f } };

        static readonly List<string> WeatherFiles = new List<string> { "soot", "burn", "scorch", "grime", "peel", "scratch", "grunge", "mli", "grain", "tiles",
                                                   "tilesw", "diamond", "brushed", "fabric",
                                                   "paving1", "paving2", "tiles138", "metal3", "plates6", "plates14", "ornament",
                                                   "tactile1", "tactile2", "walkway1", "walkway2", "chip", "chipc",
                                                   "concrete3", "leather", "carpet", "net", "styro", "paving3", "paving4", "ornament2",
                                                   "roadlines", "roadlines2", "roadlines3", "leak",
                                                   "blockwall", "facade", "cladding", "intiles", "planksb", "msheet", "carbon",
                                                   "heatd1", "heatd2", "heatd3", "heatd4", "heatd5", "heatd6", "heatd7", "heatd8",
                                                   "camo1", "camo10", "camo13", "camo26", "camo54", "camo22", "camo44", "camo55",
                                                   "ventsteel", "hessian",
                                                   "wornsteel", "metalgrid", "grimymetal" };
        static readonly List<string> WeatherLabels = new List<string> { "Soot smudge", "Burn", "Scorch", "Grime", "Rust", "Scratches", "Grunge", "MLI foil", "Grainy metal", "Heat tiles",
                                                   "Heat tiles W", "Diamond plate", "Brushed steel", "Blanket",
                                                   "Paving A", "Paving B", "Panel tiles", "Cast metal", "Plates B", "Thermal Foil", "Ornament",
                                                   "Tactile A", "Tactile B", "Walkway A", "Walkway B", "Motherboard", "Motherboard (colour)",
                                                   "Concrete C", "Leather", "Carpet", "Net", "Styrofoam", "Paving C", "Paving D", "Ornament B",
                                                   "Yellow Lines", "Yellow Lines B", "Yellow Lines C", "Paint Peel",
                                                   "Block Wall", "Tile Facade", "Cladding", "Interior Tiles", "Planks B", "Foil Sheet", "Carbon Fiber",
                                                   "Temper", "Oxide Green", "Rose Heat", "Copper Fire", "Rainbow", "Overheated Blue", "Heat Mottle", "Ember",
                                                   "Camo Tiger Olive", "Camo Naval Green", "Camo Forest Fleck", "Camo Urban Red", "Camo Tiger Grey", "Camo Winter Blue", "Camo Desert Digital", "Camo Woodland DPM",
                                                   "Vented Steel", "Hessian",
                                                   "Worn Steel", "Metal Grid", "Grimy Metal" };

        static bool customScanned;
        void EnsureCustomScan()
        {
            if (customScanned || GameDatabase.Instance == null) return;
            customScanned = true;
            try
            {
                const string pfx = "ReforgedRedux/Weather/";
                var all = GameDatabase.Instance.databaseTexture;
                for (int i = 0; i < all.Count; i++)
                {
                    string url = all[i].name;
                    if (url == null || !url.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;
                    string bn = url.Substring(pfx.Length);
                    if (bn.Length == 0 || bn.IndexOf('/') >= 0) continue;
                    string low = bn.ToLowerInvariant();
                    if (low.EndsWith("_n") || low.EndsWith("_b")) continue;
                    bool known = false;
                    for (int k = 0; k < WeatherFiles.Count; k++)
                        if (string.Equals(WeatherFiles[k], bn, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    if (known) continue;
                    WeatherFiles.Add(bn);
                    WeatherLabels.Add(PrettyLabel(bn));
                }
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] custom weather scan failed: " + e.Message); }
        }

        static string PrettyLabel(string fn)
        {
            char[] c = fn.Replace('_', ' ').ToCharArray();
            bool up = true;
            for (int i = 0; i < c.Length; i++) { if (up && c[i] != ' ') c[i] = char.ToUpperInvariant(c[i]); up = (c[i] == ' '); }
            return new string(c);
        }

        static readonly List<string> palNames = new List<string>();
        static readonly List<Color> palCols = new List<Color>();
        static bool palLoaded;

        class Preset { public string name; public bool isColor; public float metal, smooth, r, g, b; }
        class Combo
        {
            public string name; public string stack = "";
            public string recipe = "";
            public bool hasFinish; public float metal, smooth;
            public bool hasColor; public float r, g, b;
            public float scale = 1f;
        }
        static readonly List<Preset> userFinish = new List<Preset>();
        static readonly List<Preset> userColor = new List<Preset>();
        static readonly List<Combo> userCombo = new List<Combo>();
        static readonly string[] DefaultPins = { "camo1", "leak", "msheet", "carbon", "grimymetal", "tiles", "grain", "metalgrid" };
        static readonly List<string> pinned = new List<string>(DefaultPins);
        static bool presetsLoaded;

        string brushLabel = "(none yet)";
        int brushKind;
        float brMetal, brSmooth;
        Color brColor = Color.white;
        bool brAlsoColor;
        int brLevel = 1; string brTex = "soot"; string brPlace = "whole";
        float brScale = 1f;
        Combo brCombo; string brComboStack;
        bool brForceNew;

        ApplicationLauncherButton button;
        bool show;
        int tab;
        int mode = 1;
        bool includeChildren;
        bool paintSym;
        Part lastPaintedPart;
        Rect win = new Rect(300f, 100f, 360f, 10f);
        int lastWinTab = -1;
        Vector2 dirtScroll, colScroll;

        bool regWhole = true;
        int regAxis = 1;
        int regShineSpot = 0;
        bool coversOpen;
        float regCut = 0.4f;
        float regHeight = 1f;
        float regFade = 0f;
        float regStrength = 1f;
        float regPatScale = 1f;
        string dirtSearch = "";
        string regCutBuf, regHeightBuf, regFadeBuf, regStrBuf, regScaleBuf;
        int heatVariant;
        bool showOverlay = true;
        static bool showHints = true;
        Texture2D brIcon; Color brIconTint = Color.white;
        string comboName = "My combo";
        string newName = "My preset";
        bool buildingFinish, buildingColor;
        float bMetal, bSmooth;
        float pH, pS, pV = 1f, pBright = 1f;
        string bufMetal, bufShine, bufR, bufG, bufB, bufBright, hexBuf;
        Texture2D svTex, hueTex, markTex;
        int svHueKey = -1;
        bool eyedrop;
        bool meshPick;
        string pickId;
        Part pickPart;
        bool overlayApplied, ovApplied;
        Part lastHovered;
        GUIStyle headerStyle, hintStyle, builderLabel, toggleBold, brushStyle, pressStyle;

        class UndoEntry
        {
            public Part part;
            public bool fin; public float m, s;
            public bool col; public float r, g, b;
            public string stack;
            public string zones, region; public int spot; public float scale;
        }
        readonly List<List<UndoEntry>> undoStack = new List<List<UndoEntry>>();
        readonly List<List<UndoEntry>> redoStack = new List<List<UndoEntry>>();
        const int UndoCap = 12;

        public const string Version = "1.0.1";
        static bool versionLogged;

        void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
            if (!palLoaded) { LoadPalette(); palLoaded = true; }
            if (!presetsLoaded) { LoadPresets(); presetsLoaded = true; }
            if (!versionLogged) { versionLogged = true; Debug.Log("[ReforgedRedux] Reforged Materials Redux v" + Version + " loaded"); }
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            InputLockManager.RemoveControlLock(LockId);
            ClearOverlay();
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            Texture2D icon = (GameDatabase.Instance != null)
                ? GameDatabase.Instance.GetTexture("ReforgedRedux/icon", false) : null;
            if (icon == null) { icon = new Texture2D(38, 38); }
            button = ApplicationLauncher.Instance.AddModApplication(
                delegate { show = true; },
                delegate { show = false; },
                null, null, null, null,
                ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.FLIGHT,
                icon);
        }

        static void LoadPalette()
        {
            palNames.Clear(); palCols.Clear();
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REDUX_COLOR");
            for (int i = 0; i < nodes.Length; i++)
            {
                string nm = nodes[i].GetValue("name");
                string cv = nodes[i].GetValue("color");
                if (string.IsNullOrEmpty(nm) || string.IsNullOrEmpty(cv)) continue;
                string[] p = cv.Split(',');
                if (p.Length < 3) continue;
                float r, g, b;
                if (!float.TryParse(p[0], out r) || !float.TryParse(p[1], out g) || !float.TryParse(p[2], out b)) continue;
                palNames.Add(nm);
                palCols.Add(new Color(r / 255f, g / 255f, b / 255f));
            }
            if (palNames.Count == 0)
            {

                string[] fn = { "White", "Grey", "Black", "Red", "Orange", "Yellow", "Green", "Blue" };
                float[][] fc = { new float[]{245,245,245}, new float[]{140,140,140}, new float[]{25,25,25},
                                 new float[]{205,35,35}, new float[]{235,130,30}, new float[]{240,205,50},
                                 new float[]{55,150,65}, new float[]{45,105,210} };
                for (int i = 0; i < fn.Length; i++)
                {
                    palNames.Add(fn[i]);
                    palCols.Add(new Color(fc[i][0] / 255f, fc[i][1] / 255f, fc[i][2] / 255f));
                }
            }
            Debug.Log("[ReforgedRedux] palette: " + palNames.Count + " colours");
        }

        void Update()
        {
            if (Mouse.HoveredPart != null) lastHovered = Mouse.HoveredPart;

            if (show && !regWhole && Input.GetMouseButtonDown(1))
            {
                Part hp = Mouse.HoveredPart;
                if (hp != null) ovLocked = (ovLocked == hp) ? null : hp;
            }
            if (regWhole) ovLocked = null;
            DrawRegionOverlay();
            if (eyedrop && Input.GetMouseButtonDown(0))
            {
                eyedrop = false;
                StartCoroutine(SamplePixel(Input.mousePosition));
                return;
            }
            if (meshPick && Input.GetMouseButtonDown(2))
            {
                meshPick = false;
                DoMeshPick(Input.mousePosition);
                return;
            }
            if (show && Input.GetKeyDown(KeyCode.P) && brushKind != 0)
            {
                overlayApplied = true;
                if (mode == 0)
                {
                    ApplyBrush(GetParts(), false);
                }
                else
                {
                    Part target = (pickId != null && pickPart != null) ? pickPart : Mouse.HoveredPart;
                    if (target != null)
                    {
                        List<Part> group = new List<Part>();
                        group.Add(target);
                        if (includeChildren) CollectChildren(target, group);
                        ApplyBrush(group, true);
                    }
                }
            }
        }

        static void CollectChildren(Part p, List<Part> into)
        {
            for (int i = 0; i < p.children.Count; i++)
            {
                into.Add(p.children[i]);
                CollectChildren(p.children[i], into);
            }
        }

        static Material regOverlayMat;
        readonly List<Mesh> ovMeshes = new List<Mesh>();
        readonly List<Transform> ovTrs = new List<Transform>();
        Part ovPart, ovLocked; string ovToken = "~"; float ovStr = -1f;

        static Material RegOverlayMat()
        {
            if (regOverlayMat != null) return regOverlayMat;
            Shader sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            regOverlayMat = new Material(sh);
            regOverlayMat.mainTexture = Texture2D.whiteTexture;
            if (regOverlayMat.HasProperty("_TintColor")) regOverlayMat.SetColor("_TintColor", Color.white);
            if (regOverlayMat.HasProperty("_Color")) regOverlayMat.SetColor("_Color", Color.white);
            regOverlayMat.renderQueue = 4000;
            return regOverlayMat;
        }

        void ClearOverlay()
        {
            for (int i = 0; i < ovMeshes.Count; i++) if (ovMeshes[i] != null) Destroy(ovMeshes[i]);
            ovMeshes.Clear(); ovTrs.Clear(); ovPart = null; ovToken = "~"; ovStr = -1f;
        }

        static void EdgeInc(Dictionary<long, int> d, int a, int b)
        {
            long key = (a < b) ? ((long)a << 22) | (long)b : ((long)b << 22) | (long)a;
            int c; d[key] = d.TryGetValue(key, out c) ? c + 1 : 1;
        }

        void BuildOverlay(Part part, string token)
        {
            ClearOverlay();
            ovPart = part; ovToken = token; ovStr = regStrength;
            if (part == null || token == "whole") return;
            if (token == "pickscan") { BuildPickPreview(part, null); return; }
            if (token.StartsWith("pick|")) { BuildPickPreview(part, token.Substring(5)); return; }
            Vector3 ldir; float lcut, lfade, lheight;
            if (!WeatherCache.RegionAxisDir(token, out ldir, out lcut, out lfade, out lheight)) return;
            Vector3 wdir = part.transform.TransformDirection(ldir).normalized;
            Vector3 upDir = part.transform.up;
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            float lo = float.MaxValue, hi = float.MinValue, vlo = float.MaxValue, vhi = float.MinValue;
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || ModuleReforged.SkipRenderer(rs[i]) || !rs[i].enabled || !rs[i].gameObject.activeInHierarchy) continue;
                Part o = rs[i].GetComponentInParent<Part>(); if (o != null && o != part) continue;
                MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Vector3[] vs = mf.sharedMesh.vertices; Transform t = rs[i].transform;
                for (int v = 0; v < vs.Length; v++)
                {
                    Vector3 wp = t.TransformPoint(vs[v]);
                    float c = Vector3.Dot(wp, wdir); if (c < lo) lo = c; if (c > hi) hi = c;
                    float cy = Vector3.Dot(wp, upDir); if (cy < vlo) vlo = cy; if (cy > vhi) vhi = cy;
                }
            }
            if (hi - lo < 1e-5f) return;
            float cw = lo + lcut * (hi - lo);
            bool useHeight = lheight < 0.999f && vhi - vlo > 1e-5f;
            float cwHeight = vlo + lheight * (vhi - vlo);
            float alpha = 0.05f + 0.09f * Mathf.Clamp01(regStrength);
            float fadeW = lfade * Mathf.Max(0.05f, 1f - lcut) * (hi - lo);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || ModuleReforged.SkipRenderer(rs[i]) || !rs[i].enabled || !rs[i].gameObject.activeInHierarchy) continue;
                Part owner = rs[i].GetComponentInParent<Part>();
                if (owner != null && owner != part) continue;
                MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                List<Vector3> V, N; List<Vector4> T; List<Vector2> U; List<int> tris;
                ModuleReforged.MeshToLists(mf.sharedMesh, out V, out N, out T, out U, out tris);
                List<int> keep1 = new List<int>(), drop1 = new List<int>();
                ModuleReforged.ClipSoup(V, N, T, U, rs[i].transform, wdir, cw, tris, keep1, drop1);
                List<int> region = keep1;
                if (useHeight)
                {
                    List<int> keep2 = new List<int>(), d2 = new List<int>();
                    ModuleReforged.ClipSoup(V, N, T, U, rs[i].transform, -upDir, -cwHeight, keep1, keep2, d2);
                    region = keep2;
                }
                if (region.Count == 0) continue;
                Color[] cols = new Color[V.Count];
                for (int v = 0; v < cols.Length; v++)
                {
                    float a = alpha;
                    if (fadeW > 1e-4f)
                    {
                        float d = Vector3.Dot(rs[i].transform.TransformPoint(V[v]), wdir) - cw;
                        a = alpha * (0.15f + 0.85f * Mathf.Clamp01(d / fadeW));
                    }
                    cols[v] = new Color(1f, 0.12f, 0.12f, a);
                }
                Vector3[] ov = new Vector3[V.Count];
                bool lift = N != null && N.Count == V.Count;
                for (int v = 0; v < V.Count; v++) ov[v] = lift ? V[v] + N[v] * 0.004f : V[v];
                if (!overlayApplied)
                {
                    Mesh om = new Mesh();
                    if (V.Count > 65000) om.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    om.vertices = ov;
                    om.triangles = region.ToArray();
                    om.colors = cols;
                    ovMeshes.Add(om); ovTrs.Add(rs[i].transform);
                }

                Dictionary<long, int> ec = new Dictionary<long, int>();
                for (int e = 0; e + 2 < region.Count; e += 3)
                {
                    EdgeInc(ec, region[e], region[e + 1]);
                    EdgeInc(ec, region[e + 1], region[e + 2]);
                    EdgeInc(ec, region[e + 2], region[e]);
                }
                List<int> li = new List<int>();
                foreach (KeyValuePair<long, int> kv in ec)
                    if (kv.Value == 1) { li.Add((int)(kv.Key >> 22)); li.Add((int)(kv.Key & 0x3FFFFF)); }
                if (li.Count > 0)
                {
                    Color[] lc = new Color[V.Count];
                    Color bright = new Color(1f, 0.2f, 0.2f, 0.85f);
                    for (int v = 0; v < lc.Length; v++) lc[v] = bright;
                    Mesh lm = new Mesh();
                    if (V.Count > 65000) lm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    lm.vertices = ov;
                    lm.colors = lc;
                    lm.SetIndices(li.ToArray(), MeshTopology.Lines, 0);
                    ovMeshes.Add(lm); ovTrs.Add(rs[i].transform);
                }
            }
        }

        void DoMeshPick(Vector3 mousePos)
        {
            Part part = Mouse.HoveredPart;
            if (part == null) { ScreenMessages.PostScreenMessage("Point at a part first, then click.", 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            Camera cam = Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(mousePos);
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            float bestT = float.MaxValue; Renderer bestR = null; int bestSub = -1;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || ModuleReforged.SkipRenderer(r) || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Part owner = r.GetComponentInParent<Part>(); if (owner != null && owner != part) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Mesh msh = mf.sharedMesh; Vector3[] vs = msh.vertices; Transform t = r.transform;
                for (int sub = 0; sub < msh.subMeshCount; sub++)
                {
                    int[] tr = msh.GetTriangles(sub);
                    for (int k = 0; k + 2 < tr.Length; k += 3)
                    {
                        float tt;
                        if (RayTri(ray, t.TransformPoint(vs[tr[k]]), t.TransformPoint(vs[tr[k + 1]]), t.TransformPoint(vs[tr[k + 2]]), out tt) && tt < bestT)
                        { bestT = tt; bestR = r; bestSub = sub; }
                    }
                }
            }
            if (bestR != null)
            {
                pickId = ModuleReforged.RelPath(bestR.transform, part.transform) + "#" + bestSub;
                pickPart = part; ovLocked = part;
                ReArm();
                ScreenMessages.PostScreenMessage("Picked piece: " + bestR.name, 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            else ScreenMessages.PostScreenMessage("No mesh under the cursor - this part may not be splittable.", 4f, ScreenMessageStyle.UPPER_CENTER);
        }
        static bool RayTri(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a, pv = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, pv);
            if (det > -1e-7f && det < 1e-7f) return false;
            float inv = 1f / det; Vector3 tv = ray.origin - a;
            float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(ray.direction, qv) * inv; if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, qv) * inv; return t > 1e-4f;
        }

        static Color PieceColor(int i)
        {
            Color c = Color.HSVToRGB((i * 0.61803f) % 1f, 0.7f, 1f);
            c.a = 0.34f; return c;
        }
        void AddPieceOutline(Renderer r, Mesh msh, int sub)
        {
            int[] tris = msh.GetTriangles(sub);
            if (tris.Length == 0) return;
            Vector3[] vs = msh.vertices; Vector3[] nrm = msh.normals;
            bool lift = nrm != null && nrm.Length == vs.Length;
            Vector3[] ov = new Vector3[vs.Length];
            for (int v = 0; v < vs.Length; v++) ov[v] = lift ? vs[v] + nrm[v] * 0.006f : vs[v];
            Dictionary<long, int> ec = new Dictionary<long, int>();
            for (int e = 0; e + 2 < tris.Length; e += 3) { EdgeInc(ec, tris[e], tris[e + 1]); EdgeInc(ec, tris[e + 1], tris[e + 2]); EdgeInc(ec, tris[e + 2], tris[e]); }
            List<int> li = new List<int>();
            foreach (KeyValuePair<long, int> kv in ec) if (kv.Value == 1) { li.Add((int)(kv.Key >> 22)); li.Add((int)(kv.Key & 0x3FFFFF)); }
            if (li.Count == 0) return;
            Color[] lc = new Color[vs.Length]; Color bright = new Color(1f, 0.25f, 0.25f, 0.9f);
            for (int v = 0; v < lc.Length; v++) lc[v] = bright;
            Mesh lm = new Mesh();
            if (vs.Length > 65000) lm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            lm.vertices = ov; lm.colors = lc; lm.SetIndices(li.ToArray(), MeshTopology.Lines, 0);
            ovMeshes.Add(lm); ovTrs.Add(r.transform);
        }
        void AddPieceFill(Renderer r, Mesh msh, int sub, Color col)
        {
            int[] tris = msh.GetTriangles(sub);
            if (tris.Length == 0) return;
            Vector3[] vs = msh.vertices; Vector3[] nrm = msh.normals;
            bool lift = nrm != null && nrm.Length == vs.Length;
            Vector3[] ov = new Vector3[vs.Length];
            for (int v = 0; v < vs.Length; v++) ov[v] = lift ? vs[v] + nrm[v] * 0.006f : vs[v];
            Color[] cols = new Color[vs.Length];
            for (int v = 0; v < cols.Length; v++) cols[v] = col;
            Mesh om = new Mesh();
            if (vs.Length > 65000) om.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            om.vertices = ov; om.triangles = tris; om.colors = cols;
            ovMeshes.Add(om); ovTrs.Add(r.transform);
        }

        void BuildPickPreview(Part part, string pickTarget)
        {
            Transform mroot = part.transform.Find("model");
            Renderer[] rs = (mroot != null ? mroot : part.transform).GetComponentsInChildren<Renderer>(true);
            int idx = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || ModuleReforged.SkipRenderer(r) || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Part owner = r.GetComponentInParent<Part>(); if (owner != null && owner != part) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Mesh msh = mf.sharedMesh;
                for (int sub = 0; sub < msh.subMeshCount; sub++, idx++)
                {
                    if (pickTarget == null) { AddPieceFill(r, msh, sub, PieceColor(idx)); continue; }
                    bool mtc = ModuleReforged.RelPath(r.transform, part.transform) + "#" + sub == pickTarget;
                    bool show = (regShineSpot == 2) || (regShineSpot == 1 ? !mtc : mtc);
                    if (!show) continue;
                    if (overlayApplied) AddPieceOutline(r, msh, sub);
                    else AddPieceFill(r, msh, sub, new Color(1f, 0.15f, 0.15f, 0.34f));
                }
            }
        }

        void DrawRegionOverlay()
        {
            Part pv; string tok;
            if (pickId != null && pickPart != null) { pv = pickPart; tok = "pick|" + pickId; }
            else if (meshPick) { pv = (ovLocked != null) ? ovLocked : lastHovered; tok = "pickscan"; }
            else { pv = (ovLocked != null) ? ovLocked : lastHovered; tok = CurRegion(); }
            bool want = show && showOverlay && pv != null && (pickId != null || meshPick || !regWhole);
            if (!want) { if (ovPart != null) ClearOverlay(); return; }
            if (pv != ovPart || tok != ovToken) overlayApplied = false;
            if (pv != ovPart || tok != ovToken || regStrength != ovStr || overlayApplied != ovApplied) { BuildOverlay(pv, tok); ovApplied = overlayApplied; }
            Material m = RegOverlayMat();
            for (int i = 0; i < ovMeshes.Count; i++)
                if (ovTrs[i] != null && ovMeshes[i] != null)
                    Graphics.DrawMesh(ovMeshes[i], ovTrs[i].localToWorldMatrix, m, ovTrs[i].gameObject.layer);
        }

        List<Part> GetParts()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                return new List<Part>(EditorLogic.fetch.ship.parts);
            if (FlightGlobals.ActiveVessel != null)
                return new List<Part>(FlightGlobals.ActiveVessel.parts);
            return new List<Part>();
        }

        void ApplyBrush(List<Part> parts, bool spot)
        {
            if (parts.Count > 0) lastPaintedPart = parts[0];
            if (spot && paintSym)
            {
                List<Part> extra = new List<Part>();
                for (int i = 0; i < parts.Count; i++)
                {
                    Part pt = parts[i];
                    if (pt == null || pt.symmetryCounterparts == null) continue;
                    for (int k = 0; k < pt.symmetryCounterparts.Count; k++)
                    {
                        Part sc = pt.symmetryCounterparts[k];
                        if (sc != null && !parts.Contains(sc) && !extra.Contains(sc)) extra.Add(sc);
                    }
                }
                parts.AddRange(extra);
            }
            Snapshot(parts);
            int n = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                ModuleReforged mod = parts[i].Modules.GetModule<ModuleReforged>();
                if (mod == null) continue;
                mod.rdxKeepShiny = uiKeepShiny;
                bool wholeFilter = regWhole && pickId == null && !meshPick && mod.Active;
                if (wholeFilter && regShineSpot == 0)
                {
                    bool did = true;
                    switch (brushKind)
                    {
                        case 1: mod.PaintedFinish(brMetal, brSmooth, brAlsoColor, brColor); break;
                        case 2: mod.PaintedColour(brColor); break;
                        case 3: mod.PaintedWeather(brLevel, brTex, brScale); break;
                        case 7: mod.PaintedClearColour(); break;
                        default: did = false; break;
                    }
                    if (did) { n++; continue; }
                }
                if (brushKind != 4 && brushKind != 6)
                    mod.SetRegion(wholeFilter ? (regShineSpot == 1 ? "base" : "whole") : CurRegion());
                mod.rdxShineSpot = (pickId != null && !brForceNew) ? regShineSpot : 0;
                switch (brushKind)
                {
                    case 1:
                        if (brMetal < 0f) mod.ClearFinish();
                        else if (brAlsoColor) mod.SetBoth(brMetal, brSmooth, brColor);
                        else mod.SetFinish(brMetal, brSmooth);
                        n++;
                        break;
                    case 2:
                        mod.SetColour(brColor);
                        n++;
                        break;
                    case 3:
                        if (!brTex.StartsWith("tint#")) mod.rdxScale = brScale;
                        mod.AddWeather(brLevel, brTex, brPlace);
                        n++;
                        break;
                    case 4:
                        mod.ClearWeatherAll();
                        n++;
                        break;
                    case 5:
                        if (brCombo != null && !string.IsNullOrEmpty(brCombo.recipe))
                        {

                            mod.ApplyComboLayers(CurRegion(), StrengthCode(), brCombo.recipe.Split(';'),
                                brCombo.hasColor, new Color(brCombo.r, brCombo.g, brCombo.b),
                                brCombo.hasFinish, brCombo.metal, brCombo.smooth, regPatScale);
                        }
                        else
                        {

                            mod.rdxScale = regPatScale;
                            mod.AppendStack(brComboStack);
                            if (brCombo != null)
                            {
                                if (brCombo.hasFinish && brCombo.hasColor)
                                    mod.SetBoth(brCombo.metal, brCombo.smooth, new Color(brCombo.r, brCombo.g, brCombo.b));
                                else if (brCombo.hasFinish) mod.SetFinish(brCombo.metal, brCombo.smooth);
                                else if (brCombo.hasColor) mod.SetColour(new Color(brCombo.r, brCombo.g, brCombo.b));
                            }
                        }
                        n++;
                        break;
                    case 6:
                        mod.ClearAll();
                        n++;
                        break;
                    case 7:
                        mod.ClearColourAndTints();
                        n++;
                        break;
                }
            }
            ScreenMessages.PostScreenMessage((spot ? "Spot: '" : "Vessel: '") + brushLabel + "' -> " + n + " parts",
                3f, ScreenMessageStyle.UPPER_CENTER);
        }

        void Arm(int kind, string label)
        {

            brushKind = kind; brushLabel = label; brForceNew = false;
        }

        System.Action lastArm;
        void ReArm() { if (lastArm != null) lastArm(); }

        void ArmFinish(string label, float metal, float smooth)
        {
            lastArm = delegate { ArmFinish(label, metal, smooth); };
            brMetal = metal; brSmooth = smooth; brAlsoColor = false;
            brIcon = (metal >= 0f) ? ShineIcon(metal, smooth) : null; brIconTint = Color.white;
            if (buildingFinish && metal >= 0f)
            { bMetal = Mathf.Clamp01(metal); bSmooth = Mathf.Clamp01(smooth); bufMetal = bufShine = null; }
            Arm(1, label);
        }

        static string Hex(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}",
                Mathf.Clamp((int)(c.r * 255f), 0, 255),
                Mathf.Clamp((int)(c.g * 255f), 0, 255),
                Mathf.Clamp((int)(c.b * 255f), 0, 255));
        }

        void ArmMetal(int i)
        {
            lastArm = delegate { ArmMetal(i); };
            Color col = new Color(MetalRGB[i][0] / 255f, MetalRGB[i][1] / 255f, MetalRGB[i][2] / 255f);
            brIcon = MetalIcon(col); brIconTint = Color.white;
            if (regWhole)
            {
                brMetal = 1f; brSmooth = MetalSmooth; brAlsoColor = true; brColor = col;
                Arm(1, MetalNames[i]);
            }
            else
            {

                Combo c = new Combo();
                c.name = MetalNames[i];
                c.hasFinish = true; c.metal = 1f; c.smooth = MetalSmooth;
                c.stack = "tint#" + Hex(col) + ":" + CurRegion() + ":2";
                brCombo = c; brComboStack = c.stack;
                Arm(5, MetalNames[i] + " / region");
                brForceNew = true;
            }
        }

        void ArmHeatDiscolor(int i)
        {

            lastArm = delegate { ArmHeatDiscolor(i); };
            Combo c = new Combo();
            c.name = HeatNames[i];
            c.stack = HeatFiles[i] + (heatVariant == 1 ? "b" : "") + ":" + CurRegion() + ":" + StrengthCode();
            brIcon = HeatIcon(i, heatVariant == 1); brIconTint = Color.white;
            c.hasFinish = true; c.metal = 1f; c.smooth = HeatSmooth;
            brCombo = c; brComboStack = c.stack;
            Arm(5, "Heat: " + HeatNames[i] + (regWhole ? "" : " / region"));
            brForceNew = true;
        }

        void ArmColour(Color c, string label)
        {
            lastArm = delegate { ArmColour(c, label); };
            LoadPickerColour(c);
            brIcon = Texture2D.whiteTexture; brIconTint = c;
            if (regWhole && pickId == null)
            {
                brColor = c;
                Arm(2, label);
            }
            else
            {

                brLevel = 2;
                brTex = "tint#" + Hex(c);
                brPlace = CurRegion();
                Arm(3, label + " / region");
            }
        }

        void ArmWeather(int idx)
        {
            lastArm = delegate { ArmWeather(idx); };
            brIcon = null; brIconTint = Color.white;
            if (idx < 0) { Arm(4, "Clean weathering"); return; }
            float dm, ds;
            if (DefaultShine(WeatherFiles[idx], out dm, out ds))
            {

                Combo c = new Combo();
                c.name = WeatherLabels[idx];
                c.stack = WeatherFiles[idx] + ":" + CurRegion() + ":" + StrengthCode();
                c.hasFinish = true; c.metal = dm; c.smooth = ds;
                brCombo = c; brComboStack = c.stack;
                Arm(5, WeatherLabels[idx] + (regWhole ? "" : " / region"));
                brForceNew = true;
                return;
            }
            brLevel = StrengthCode();
            brScale = regPatScale;
            brTex = WeatherFiles[idx];
            brPlace = CurRegion();
            Arm(3, WeatherLabels[idx] + (regWhole ? "" : " / region"));
            brForceNew = true;
        }

        static bool DefaultShine(string name, out float metal, out float smooth)
        {
            metal = 0f; smooth = 0f;
            switch (name)
            {
                case "carbon": metal = 0.75f; smooth = 0.82f; return true;
                case "brushed": case "diamond": case "metal3": case "plates14": case "mli":
                case "metalgrid":
                    metal = 0.75f; smooth = 0.82f; return true;
                case "tiles138": metal = 0.40f; smooth = 0.72f; return true;
                case "tiles": case "tilesw": metal = 0.40f; smooth = 0.72f; return true;
                case "plates6": metal = 0.85f; smooth = 0.65f; return true;
                case "msheet": case "tactile1": case "tactile2": case "chip":
                    metal = 0.55f; smooth = 0.90f; return true;
                case "heatd1": case "heatd2": case "heatd3": case "heatd4":
                case "heatd5": case "heatd6": case "heatd7": case "heatd8":
                    metal = 1f; smooth = HeatSmooth; return true;
                default: return false;
            }
        }

        void ArmDirtTint(int i)
        {
            lastArm = delegate { ArmDirtTint(i); };
            brLevel = StrengthCode();
            brTex = "tint#" + string.Format("{0:X2}{1:X2}{2:X2}",
                (int)DirtRGB[i][0], (int)DirtRGB[i][1], (int)DirtRGB[i][2]);
            brPlace = CurRegion();
            Arm(3, DirtNames[i] + " tint" + (regWhole ? "" : " / region"));
        }

        void ArmCombo(Combo c) { ArmCombo(c, false); }
        void ArmCombo(Combo c, bool snapScale)
        {
            lastArm = delegate { ArmCombo(c, false); };
            brIcon = null; brIconTint = Color.white;
            brCombo = c;
            if (snapScale && c.scale > 0f) { regPatScale = c.scale; regScaleBuf = null; }
            brComboStack = string.IsNullOrEmpty(c.recipe) ? RemapPlace(c.stack, CurRegion(), StrengthCode()) : "";
            Arm(5, "Combo: " + c.name + (regWhole ? "" : " / region"));
        }

        string CurRegion()
        {
            if (pickId != null) return "pick|" + pickId;
            if (regWhole)
            {
                if (regShineSpot == 2) return "whole";
                if (regShineSpot == 1) return "base";
                return "whole";
            }
            int c = Mathf.Clamp(Mathf.RoundToInt(regCut * 100f), 0, 100);
            int f = Mathf.Clamp(Mathf.RoundToInt(regFade * 100f), 0, 100);
            int h = Mathf.Clamp(Mathf.RoundToInt(regHeight * 100f), 0, 100);
            return "g" + regAxis + "_" + c.ToString("00") + "_" + f.ToString("00") + "_" + h.ToString("00");
        }

        bool DrawRegion()
        {
            bool ch = false;
            int rmode = !regWhole ? 1 : ((meshPick || pickId != null) ? 2 : 0);
            int nmode = GUILayout.Toolbar(rmode, new string[] { "Whole part", "Region", "Pick" });
            if (nmode != rmode)
            {
                pickId = null; pickPart = null; meshPick = false; regShineSpot = (nmode == 0) ? 2 : 0;
                regWhole = (nmode != 1);
                if (nmode == 1) { regCut = 0.4f; regHeight = 1f; regFade = 0f; regCutBuf = null; regHeightBuf = null; regFadeBuf = null; }
                else if (nmode == 2) meshPick = true;
                ch = true;
            }
            if (nmode != 2 && (pickId != null || meshPick)) { pickId = null; pickPart = null; meshPick = false; ch = true; }
            if (nmode == 1)
            {
                int ns = GUILayout.Toolbar(regAxis, new string[] { "Top", "Btm", "Front", "Back", "Left", "Right" });
                if (ns != regAxis) { regAxis = ns; ch = true; }
                float nc = SliderNum("Cutoff", regCut, 0f, 1f, ref regCutBuf);
                if (nc != regCut) { regCut = nc; ch = true; }
                float nh = SliderNum("Height", regHeight, 0f, 1f, ref regHeightBuf);
                if (nh != regHeight) { regHeight = nh; ch = true; }
                float nf = SliderNum("Fade", regFade, 0f, 1f, ref regFadeBuf);
                if (nf != regFade) { regFade = nf; ch = true; }
            }
            else if (nmode == 2)
            {
                if (pickId == null)
                    Hint("Middle-click a coloured piece to select just that piece, then paint it.\nEach separate piece has its own colour. A part drawn as one piece can't be split.");
                else if (GUILayout.Button("Pick a different piece", GUILayout.Height(20f)))
                { pickId = null; pickPart = null; meshPick = true; ch = true; }
            }
            if (nmode != 0)
            {
                showOverlay = GUILayout.Toggle(showOverlay, "<b>Show preview</b>", toggleBold);
                if (showOverlay) Hint((ovLocked != null ? "Preview PINNED. " : "") + "Right-click a part to pin/unpin the preview.");
            }
            return ch;
        }

        bool TargetPainted()
        {
            Part tp = ovLocked;
            if (tp == null) tp = lastPaintedPart;
            if (tp == null) tp = Mouse.HoveredPart;
            if (tp == null) return false;
            ModuleReforged m = tp.Modules.GetModule<ModuleReforged>();
            return m != null && m.Active;
        }
        void DrawRegionTarget()
        {
            if (pickId != null || meshPick) { regShineSpot = 0; return; }
            if (!regWhole) { regShineSpot = 0; return; }
            if (!TargetPainted()) { regShineSpot = 2; return; }
            string[] nm = { "New", "Original", "Whole" };
            if (GUILayout.Button("Extra filters  (" + nm[regShineSpot].ToLower() + ")", GUILayout.Height(20f)))
                coversOpen = !coversOpen;
            if (coversOpen)
            {
                regShineSpot = GUILayout.Toolbar(regShineSpot, nm);
                Hint("New = only paints over the new textures youve applied.  Original = only paints over the original texture, not the new ones.  Whole = the whole part.");
            }
        }

        bool DrawStrength()
        {
            bool ch = false;
            float ns = SliderNum("Strength", regStrength, 0.1f, 1f, ref regStrBuf);
            if (ns != regStrength) { regStrength = ns; ch = true; }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Low", GUILayout.Height(20f))) { regStrength = 0.25f; regStrBuf = null; ch = true; }
            if (GUILayout.Button("Med", GUILayout.Height(20f))) { regStrength = 0.55f; regStrBuf = null; ch = true; }
            if (GUILayout.Button("Max", GUILayout.Height(20f))) { regStrength = 1f; regStrBuf = null; ch = true; }
            GUILayout.EndHorizontal();
            return ch;
        }

        bool DrawPatternSize()
        {
            bool ch = false;
            float ns = SliderNum("Pattern size", regPatScale, 0.25f, 3f, ref regScaleBuf);
            if (ns != regPatScale) { regPatScale = ns; ch = true; }
            if (regPatScale != 1f && GUILayout.Button("Reset pattern size", GUILayout.Height(18f)))
            { regPatScale = 1f; regScaleBuf = null; ch = true; }
            return ch;
        }
        int StrengthCode() { return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(12f, 90f, (regStrength - 0.1f) / 0.9f)), 12, 90); }

        static string RemapPlace(string stack, string place, int lvl)
        {
            if (string.IsNullOrEmpty(stack)) return stack;
            string[] es = stack.Split(';');
            for (int i = 0; i < es.Length; i++)
            {
                string[] f = es[i].Split(':');
                if (f.Length >= 3) es[i] = f[0] + ":" + place + ":" + lvl;
            }
            return string.Join(";", es);
        }

        List<UndoEntry> Capture(List<Part> parts)
        {
            List<UndoEntry> set = new List<UndoEntry>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                ModuleReforged mod = (parts[i] != null) ? parts[i].Modules.GetModule<ModuleReforged>() : null;
                if (mod == null) continue;
                UndoEntry u = new UndoEntry();
                u.part = parts[i];
                u.fin = mod.rdxFinish; u.m = mod.rdxMetal; u.s = mod.rdxSmooth;
                u.col = mod.rdxColored; u.r = mod.rdxR; u.g = mod.rdxG; u.b = mod.rdxB;
                u.stack = mod.rdxStack;
                u.zones = mod.rdxZones; u.region = mod.rdxRegion; u.spot = mod.rdxShineSpot; u.scale = mod.rdxScale;
                set.Add(u);
            }
            return set;
        }

        void Snapshot(List<Part> parts)
        {
            undoStack.Add(Capture(parts));
            while (undoStack.Count > UndoCap) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        static List<Part> PartsOf(List<UndoEntry> set)
        {
            List<Part> ps = new List<Part>(set.Count);
            for (int i = 0; i < set.Count; i++) if (set[i].part != null) ps.Add(set[i].part);
            return ps;
        }

        int RestoreSet(List<UndoEntry> set)
        {
            int n = 0;
            for (int i = 0; i < set.Count; i++)
            {
                UndoEntry u = set[i];
                if (u.part == null) continue;
                ModuleReforged mod = u.part.Modules.GetModule<ModuleReforged>();
                if (mod == null) continue;
                mod.RestoreState(u.fin, u.m, u.s, u.col, u.r, u.g, u.b, u.stack, u.zones, u.region, u.spot, u.scale);
                n++;
            }
            return n;
        }

        void UndoLast()
        {
            if (undoStack.Count == 0) { ScreenMessages.PostScreenMessage("Nothing to undo", 2.5f, ScreenMessageStyle.UPPER_CENTER); return; }
            List<UndoEntry> prev = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(Capture(PartsOf(prev)));
            ScreenMessages.PostScreenMessage("Undo (" + RestoreSet(prev) + " parts)", 2.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        void RedoLast()
        {
            if (redoStack.Count == 0) { ScreenMessages.PostScreenMessage("Nothing to redo", 2.5f, ScreenMessageStyle.UPPER_CENTER); return; }
            List<UndoEntry> nxt = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(Capture(PartsOf(nxt)));
            while (undoStack.Count > UndoCap) undoStack.RemoveAt(0);
            ScreenMessages.PostScreenMessage("Redo (" + RestoreSet(nxt) + " parts)", 2.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        const string LockId = "ReforgedReduxWindow";
        bool inputLocked, lockedEyedrop;

        void ManageInputLock()
        {
            bool over = show && (eyedrop || win.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)));

            if (over && (!inputLocked || lockedEyedrop != eyedrop))
            {
                if (eyedrop)
                    InputLockManager.SetControlLock(ControlTypes.All, LockId);
                else if (HighLogic.LoadedSceneIsEditor)
                    InputLockManager.SetControlLock(
                        ControlTypes.EDITOR_PAD_PICK_PLACE | ControlTypes.EDITOR_ICON_PICK |
                        ControlTypes.EDITOR_ICON_HOVER | ControlTypes.EDITOR_GIZMO_TOOLS, LockId);
                else
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, LockId);
                inputLocked = true; lockedEyedrop = eyedrop;
            }
            else if (!over && inputLocked)
            {
                InputLockManager.RemoveControlLock(LockId);
                inputLocked = false; lockedEyedrop = false;
            }

            bool regBlock = show && !regWhole && showOverlay && Mouse.HoveredPart != null;
            if (regBlock && !regLocked)
            {
                InputLockManager.SetControlLock(
                    HighLogic.LoadedSceneIsEditor ? ControlTypes.EDITOR_UI : (ControlTypes.All & ~ControlTypes.CAMERACONTROLS),
                    RegLockId);
                regLocked = true;
            }
            else if (!regBlock && regLocked)
            {
                InputLockManager.RemoveControlLock(RegLockId);
                regLocked = false;
            }
        }
        bool regLocked;
        const string RegLockId = "ReforgedReduxRegion";

        void OnGUI()
        {
            if (!show) { if (inputLocked) { InputLockManager.RemoveControlLock(LockId); inputLocked = false; } return; }
            GUI.skin = HighLogic.Skin;
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label);
                headerStyle.fontStyle = FontStyle.Bold;
                headerStyle.normal.textColor = Color.white;
                hintStyle = new GUIStyle(GUI.skin.label);
                hintStyle.fontSize = 13;
                hintStyle.richText = true;
                builderLabel = new GUIStyle(GUI.skin.label);
                builderLabel.fontSize = 13;
                builderLabel.richText = true;
                brushStyle = new GUIStyle(GUI.skin.label);
                brushStyle.fontSize = 19;
                brushStyle.fontStyle = FontStyle.Bold;
                brushStyle.richText = true;
                brushStyle.wordWrap = true;
                brushStyle.normal.textColor = Color.white;
                pressStyle = new GUIStyle(GUI.skin.label);
                pressStyle.fontSize = 15;
                pressStyle.fontStyle = FontStyle.Bold;
                pressStyle.richText = true;
                toggleBold = new GUIStyle(GUI.skin.toggle);
                toggleBold.richText = true;
            }

            if (tab != lastWinTab && Event.current.type == EventType.Layout) { win.height = 0f; lastWinTab = tab; }
            win = GUILayout.Window(GetInstanceID(), win, DrawWindow, "Reforged Redux v" + Version, GUILayout.Width(360f));
            ManageInputLock();
        }

        void Header(string t) { GUILayout.Space(3f); GUILayout.Label(t, headerStyle); }

        const float ColW = 166f;
        const float ColWs = 156f;

        static void Swatch(Color c)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill);
            GUI.color = old;
        }

        float SliderNum(string label, float val, float min, float max, ref string buf)
        {
            if (buf == null) buf = val.ToString("0.00");
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, builderLabel, GUILayout.Width(76f));
            float sv = GUILayout.HorizontalSlider(val, min, max, GUILayout.Width(118f), GUILayout.Height(20f));
            if (sv != val) buf = sv.ToString("0.00");
            buf = GUILayout.TextField(buf, GUILayout.Width(48f));
            float pv2;
            float result = float.TryParse(buf, out pv2) ? Mathf.Clamp(pv2, min, max) : sv;
            GUILayout.EndHorizontal();
            return result;
        }

        void Hint(string s) { if (showHints) GUILayout.Label(s, hintStyle); }

        void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            int newTab = GUILayout.Toolbar(tab, new string[] { "Finish", "Colour", "Metals", "Dirt", "Combo" });
            if (newTab != tab) tab = newTab;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<---", GUILayout.Height(22f))) UndoLast();
            if (GUILayout.Button("--->", GUILayout.Height(22f))) RedoLast();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            if (tab == 0) DrawFinishTab();
            else if (tab == 1) DrawColourTab();
            else if (tab == 2) DrawMetalsTab();
            else if (tab == 3) DrawDirtTab();
            else DrawComboTab();

            GUILayout.Space(6f);
            if (mode == 0) GUILayout.Label("Press P to paint the WHOLE vessel.", pressStyle);
            else
            {
                GUILayout.Label("Press P to paint the hovered part.", pressStyle);
                includeChildren = GUILayout.Toggle(includeChildren, "<b>Include attached parts</b>", toggleBold);
                paintSym = GUILayout.Toggle(paintSym, "<b>Symmetry paint</b>", toggleBold);
            }
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (brIcon != null)
            {
                Rect ir = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(40f), GUILayout.Height(40f));
                Color oc = GUI.color; GUI.color = brIconTint;
                GUI.DrawTexture(ir, brIcon, ScaleMode.StretchToFill, true);
                GUI.color = oc;
            }
            GUILayout.Label(brushLabel, brushStyle);
            GUILayout.EndHorizontal();
            if (tab == 0)
            {
                if (brushKind == 1 && brMetal >= 0f)
                    GUILayout.Label("Metal: " + brMetal.ToString("0.00") + "    Shine: " + brSmooth.ToString("0.00"), hintStyle);
            }
            else if (tab != 1)
                GUILayout.Label("Strength: " + regStrength.ToString("0.00"), hintStyle);
            GUILayout.Space(2f);
            bool nShow = GUILayout.Toggle(showHints, "Show Text");
            if (nShow != showHints) { showHints = nShow; SavePresets(); }
            GUILayout.Space(8f);
            {
                Color _oc = GUI.backgroundColor;
                GUILayout.BeginHorizontal();
                GUI.backgroundColor = (mode == 0) ? new Color(0.90f, 0.25f, 0.25f) : _oc;
                if (GUILayout.Button(mode == 0 ? "Whole vessel (ON)" : "Whole vessel", GUILayout.Height(22f))) mode = 0;
                GUI.backgroundColor = (mode == 1) ? new Color(0.25f, 0.70f, 0.35f) : _oc;
                if (GUILayout.Button(mode == 1 ? "Spot brush (ON)" : "Spot brush", GUILayout.Height(22f))) mode = 1;
                GUI.backgroundColor = _oc;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        void DrawResetRow()
        {
            GUILayout.Space(4f);
            if (GUILayout.Button("Reset All to stock", GUILayout.Height(24f)))
            {
                Snapshot(GetParts());
                List<Part> ps = GetParts();
                int n = 0;
                for (int i = 0; i < ps.Count; i++)
                {
                    ModuleReforged mod = ps[i].Modules.GetModule<ModuleReforged>();
                    if (mod != null) { mod.ClearAll(); n++; }
                }
                ScreenMessages.PostScreenMessage("Reset " + n + " parts to stock", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (GUILayout.Button("Reset brush to stock", GUILayout.Height(22f)))
            { brushKind = 6; brushLabel = "Reset to stock"; }
        }

        static readonly Dictionary<int, Texture2D> shineIcons = new Dictionary<int, Texture2D>();

        static Texture2D ShineIcon(float metal, float smooth)
        {
            int key = (int)(metal * 100) * 1000 + (int)(smooth * 100);
            Texture2D t;
            if (shineIcons.TryGetValue(key, out t)) return t;
            int n = 26;
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            float baseGrey = Mathf.Lerp(0.62f, 0.38f, metal);
            float power = Mathf.Lerp(2f, 24f, smooth);
            float specAmp = Mathf.Lerp(0.15f, 1.0f, smooth);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float spec = Mathf.Pow(ndl, power) * specAmp;
                    float g = baseGrey * (0.25f + 0.75f * ndl) + spec;
                    px[y * n + x] = new Color(Mathf.Clamp01(g), Mathf.Clamp01(g), Mathf.Clamp01(g), 1f);
                }
            }
            t.SetPixels(px); t.Apply();
            shineIcons[key] = t;
            return t;
        }

        static void ShineSwatch(float metal, float smooth)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, ShineIcon(metal, smooth), ScaleMode.StretchToFill, true);
        }

        static readonly Dictionary<int, Texture2D> metalIcons = new Dictionary<int, Texture2D>();
        static Texture2D MetalIcon(Color c)
        {
            int key = ((int)(c.r * 255f) << 16) | ((int)(c.g * 255f) << 8) | (int)(c.b * 255f);
            Texture2D t;
            if (metalIcons.TryGetValue(key, out t)) return t;
            int n = 28;
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float env = 0.55f + 0.6f * (v * 0.5f + 0.5f);
                    float spec = Mathf.Pow(ndl, 24f) * 0.95f;
                    float br = (0.22f + 0.72f * ndl) * env;
                    px[y * n + x] = new Color(
                        Mathf.Clamp01(c.r * br + spec),
                        Mathf.Clamp01(c.g * br + spec),
                        Mathf.Clamp01(c.b * br + spec), 1f);
                }
            t.SetPixels(px); t.Apply();
            metalIcons[key] = t;
            return t;
        }
        static void MetalSwatch(int i)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, MetalIcon(new Color(MetalRGB[i][0] / 255f, MetalRGB[i][1] / 255f, MetalRGB[i][2] / 255f)), ScaleMode.StretchToFill, true);
        }

        static readonly Color[] TemperStops = {
            new Color(206f/255f, 206f/255f, 210f/255f), new Color(236f/255f, 216f/255f, 158f/255f),
            new Color(226f/255f, 182f/255f,  96f/255f), new Color(182f/255f, 122f/255f,  72f/255f),
            new Color(152f/255f,  96f/255f, 142f/255f), new Color( 72f/255f,  92f/255f, 176f/255f),
            new Color( 86f/255f, 132f/255f, 202f/255f), new Color(112f/255f, 176f/255f, 186f/255f),
            new Color(168f/255f, 192f/255f, 182f/255f) };
        static readonly Color[] GreenStops = {
            new Color(210f/255f, 206f/255f, 150f/255f), new Color(190f/255f, 200f/255f, 116f/255f),
            new Color(150f/255f, 190f/255f, 108f/255f), new Color(108f/255f, 170f/255f, 118f/255f),
            new Color( 92f/255f, 150f/255f, 140f/255f), new Color(128f/255f, 184f/255f, 150f/255f),
            new Color(168f/255f, 200f/255f, 150f/255f) };
        static readonly Color[] RoseStops = {
            new Color(222f/255f, 190f/255f, 182f/255f), new Color(216f/255f, 150f/255f, 172f/255f),
            new Color(204f/255f, 110f/255f, 152f/255f), new Color(176f/255f,  92f/255f, 152f/255f),
            new Color(150f/255f,  92f/255f, 172f/255f), new Color(188f/255f, 122f/255f, 182f/255f),
            new Color(214f/255f, 156f/255f, 192f/255f) };
        static readonly Color[] CopperStops = {
            new Color(120f/255f, 78f/255f, 58f/255f), new Color(168f/255f, 104f/255f, 66f/255f),
            new Color(214f/255f, 132f/255f, 70f/255f), new Color(232f/255f, 150f/255f, 86f/255f),
            new Color(206f/255f, 104f/255f, 78f/255f), new Color(168f/255f, 74f/255f, 92f/255f),
            new Color(128f/255f, 74f/255f, 120f/255f) };
        static readonly Color[] RainbowStops = {
            new Color(196f/255f, 176f/255f, 210f/255f), new Color(150f/255f, 118f/255f, 208f/255f),
            new Color( 92f/255f, 110f/255f, 220f/255f), new Color( 74f/255f, 168f/255f, 210f/255f),
            new Color( 96f/255f, 200f/255f, 150f/255f), new Color(216f/255f, 200f/255f, 110f/255f),
            new Color(226f/255f, 138f/255f,  96f/255f), new Color(206f/255f,  96f/255f, 150f/255f),
            new Color(150f/255f,  92f/255f, 200f/255f) };
        static readonly Color[] OverblueStops = {
            new Color(150f/255f, 170f/255f, 205f/255f), new Color( 92f/255f, 130f/255f, 202f/255f),
            new Color( 68f/255f,  92f/255f, 188f/255f), new Color(112f/255f,  86f/255f, 182f/255f),
            new Color(162f/255f,  92f/255f, 174f/255f), new Color(198f/255f, 112f/255f, 162f/255f),
            new Color(128f/255f, 108f/255f, 200f/255f), new Color( 74f/255f, 140f/255f, 196f/255f),
            new Color( 98f/255f, 178f/255f, 190f/255f) };
        static readonly Color[] WarmStops = {
            new Color(238f/255f, 224f/255f, 176f/255f), new Color(232f/255f, 200f/255f, 130f/255f),
            new Color(226f/255f, 168f/255f,  86f/255f), new Color(216f/255f, 128f/255f,  56f/255f),
            new Color(198f/255f,  92f/255f,  44f/255f), new Color(168f/255f,  60f/255f,  40f/255f),
            new Color(132f/255f,  44f/255f,  38f/255f) };
        static Color[] HeatPalette(int variant)
        {
            if (variant == 1) return GreenStops;
            if (variant == 2) return RoseStops;
            if (variant == 3) return CopperStops;
            if (variant == 4) return RainbowStops;
            if (variant == 5) return OverblueStops;
            if (variant == 7) return WarmStops;
            return TemperStops;
        }
        static Color Temper(float t, Color[] stops)
        {
            t = Mathf.Clamp01(t) * (stops.Length - 1);
            int i = (int)t;
            if (i >= stops.Length - 1) return stops[stops.Length - 1];
            return Color.Lerp(stops[i], stops[i + 1], t - i);
        }
        static readonly Dictionary<int, Texture2D> heatIcons = new Dictionary<int, Texture2D>();
        static Texture2D HeatIcon(int variant, bool linear)
        {
            Texture2D t;
            int key = variant * 2 + (linear ? 1 : 0);
            if (heatIcons.TryGetValue(key, out t)) return t;
            int n = 28;
            float off = variant * 0.9f;
            Color[] stops = HeatPalette(variant);
            t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f;
                    float v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f) { px[y * n + x] = Color.clear; continue; }
                    float z = Mathf.Sqrt(1f - r2);
                    float ndl = Mathf.Clamp01(u * -0.4f + v * 0.55f + z * 0.73f);
                    float temp;
                    if (linear) temp = Mathf.Clamp01(1f - (v * 0.5f + 0.5f));
                    else
                    {
                        temp = 0.5f + 0.5f * Mathf.Sin((u + off) * 3.1f + Mathf.Cos(v * 2.6f + off) * 1.7f + z * 1.9f);
                        temp = Mathf.Clamp01(temp * 0.9f + v * 0.08f + 0.05f);
                    }
                    Color b = Temper(temp, stops) * (0.3f + 0.75f * ndl);
                    float spec = Mathf.Pow(ndl, 22f) * 0.85f;
                    px[y * n + x] = new Color(
                        Mathf.Clamp01(b.r + spec), Mathf.Clamp01(b.g + spec), Mathf.Clamp01(b.b + spec), 1f);
                }
            t.SetPixels(px); t.Apply();
            heatIcons[key] = t;
            return t;
        }
        static void HeatSwatch(int i, bool linear)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Width(28f), GUILayout.Height(28f));
            GUI.DrawTexture(r, HeatIcon(i, linear), ScaleMode.StretchToFill, true);
        }

        void DrawFinishTab()
        {
            Header("Finishes");
            if (GUILayout.Button("Stock", GUILayout.Height(26f))) ArmFinish("Stock", -1f, 0f);
            for (int i = 0; i < FinNames.Length; i++)
            {
                int idx = i;
                GUILayout.BeginHorizontal();
                ShineSwatch(FinVals[i][0], FinVals[i][1]);
                if (GUILayout.Button(FinLabels[i], GUILayout.Height(28f)))
                    ArmFinish(FinLabels[idx], FinVals[idx][0], FinVals[idx][1]);
                GUILayout.EndHorizontal();
            }
            Header("My finishes");
            DrawUserPresets(userFinish, false);
            if (GUILayout.Button(buildingFinish ? "Close builder" : "Create finish preset +", GUILayout.Height(26f)))
                buildingFinish = !buildingFinish;
            if (buildingFinish) DrawFinishBuilder();
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawResetRow();
        }

        void DrawFinishBuilder()
        {
            float m = SliderNum("Metal", bMetal, 0f, 1f, ref bufMetal);
            float s = SliderNum("Shine", bSmooth, 0f, 1f, ref bufShine);
            if (m != bMetal || s != bSmooth)
            {
                bMetal = m; bSmooth = s;
                PreviewFinishLive();
            }
            newName = GUILayout.TextField(newName);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply")) ArmFinish("Custom finish", bMetal, bSmooth);
            if (GUILayout.Button("Save"))
            {
                Preset pr = new Preset();
                pr.name = string.IsNullOrEmpty(newName) ? "My preset" : newName;
                pr.isColor = false; pr.metal = bMetal; pr.smooth = bSmooth;
                for (int i = userFinish.Count - 1; i >= 0; i--) if (userFinish[i].name == pr.name) userFinish.RemoveAt(i);
                userFinish.Add(pr); SavePresets();
            }
            if (GUILayout.Button("Reset")) { bMetal = 0f; bSmooth = 0f; bufMetal = bufShine = null; PreviewFinishLive(); }
            GUILayout.EndHorizontal();
            Hint(mode == 0 ? "Drag to preview on the whole vessel." : "Point at a part, drag to preview.");
        }

        void PreviewFinishLive()
        {
            List<Part> targets = (mode == 0) ? GetParts() : ((lastHovered != null) ? new List<Part> { lastHovered } : new List<Part>());
            for (int i = 0; i < targets.Count; i++)
            {
                ModuleReforged mod = targets[i].Modules.GetModule<ModuleReforged>();
                if (mod != null) mod.SetFinish(bMetal, bSmooth);
            }
        }

        void DrawUserPresets(List<Preset> list, bool isColor)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Preset pr = list[i];
                GUILayout.BeginHorizontal();
                if (isColor) Swatch(new Color(pr.r, pr.g, pr.b));
                if (GUILayout.Button(pr.name, GUILayout.Height(26f)))
                {
                    if (isColor) ArmColour(new Color(pr.r, pr.g, pr.b), pr.name);
                    else ArmFinish(pr.name, pr.metal, pr.smooth);
                }
                if (GUILayout.Button("X", GUILayout.Width(26f), GUILayout.Height(26f)))
                { list.RemoveAt(i); SavePresets(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
        }

        bool uiKeepShiny = false;

        void DrawKeepShiny()
        {
            uiKeepShiny = GUILayout.Toggle(uiKeepShiny, "<b>Keep shiny areas original</b>", toggleBold);
            if (uiKeepShiny) Hint("Shiny spots (windows/chrome) stay unpainted;\ncolour & dirt fill around them.");
        }

        void DrawColourTab()
        {
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawKeepShiny();
            Header("Colours");
            if (GUILayout.Button("Original (reset colour)", GUILayout.Height(24f)))
            {
                if (mode == 0)
                {
                    Snapshot(GetParts());
                    List<Part> ps = GetParts();
                    for (int i = 0; i < ps.Count; i++)
                    {
                        ModuleReforged mod = ps[i].Modules.GetModule<ModuleReforged>();
                        if (mod != null) mod.ClearColourAndTints();
                    }
                }
                else
                {
                    brIcon = null; brIconTint = Color.white;
                    Arm(7, "Original colour");
                }
            }
            colScroll = GUILayout.BeginScrollView(colScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(200f));
            for (int i = 0; i < palNames.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= palNames.Count) { GUILayout.Space(ColWs); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColWs));
                    Swatch(palCols[idx]);
                    if (GUILayout.Button(palNames[idx], GUILayout.Height(28f))) ArmColour(palCols[idx], palNames[idx]);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            Header("My colours");
            DrawUserPresets(userColor, true);
            if (GUILayout.Button(buildingColor ? "Close builder" : "Create colour preset +", GUILayout.Height(26f)))
                buildingColor = !buildingColor;
            if (buildingColor) DrawColorBuilder();
            DrawResetRow();
        }

        Color BuilderColor()
        {
            Color c = Color.HSVToRGB(pH, pS, pV);
            return new Color(Mathf.Min(c.r * pBright, 3f), Mathf.Min(c.g * pBright, 3f), Mathf.Min(c.b * pBright, 3f));
        }

        void LoadPickerColour(Color c)
        {
            Color.RGBToHSV(new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b)), out pH, out pS, out pV);
            pBright = Mathf.Max(1f, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            bufR = bufG = bufB = bufBright = hexBuf = null;
            svHueKey = -1;
        }

        static string RgbToHex(Color c)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}",
                (int)(Mathf.Clamp01(c.r) * 255), (int)(Mathf.Clamp01(c.g) * 255), (int)(Mathf.Clamp01(c.b) * 255));
        }

        static bool TryHex(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.TrimStart('#');
            if (s.Length != 6) return false;
            try
            {
                int r = Convert.ToInt32(s.Substring(0, 2), 16);
                int g = Convert.ToInt32(s.Substring(2, 2), 16);
                int b = Convert.ToInt32(s.Substring(4, 2), 16);
                c = new Color(r / 255f, g / 255f, b / 255f);
                return true;
            }
            catch { return false; }
        }

        void EnsurePickerTex()
        {
            if (hueTex == null)
            {
                int w = 120, h = 12;
                hueTex = new Texture2D(w, h);
                Color[] px = new Color[w * h];
                for (int x = 0; x < w; x++)
                {
                    Color hc = Color.HSVToRGB(x / (float)(w - 1), 1f, 1f);
                    for (int y = 0; y < h; y++) px[y * w + x] = hc;
                }
                hueTex.SetPixels(px); hueTex.Apply();
            }
            if (markTex == null) { markTex = new Texture2D(1, 1); markTex.SetPixel(0, 0, Color.white); markTex.Apply(); }
            int key = Mathf.RoundToInt(pH * 360f);
            if (svTex == null || key != svHueKey)
            {
                svHueKey = key;
                int nx = 190, ny = 130;
                if (svTex == null) svTex = new Texture2D(nx, ny);
                Color[] px = new Color[nx * ny];
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        px[y * nx + x] = Color.HSVToRGB(pH, x / (float)(nx - 1), y / (float)(ny - 1));
                svTex.SetPixels(px); svTex.Apply();
            }
        }

        void PreviewColourLive()
        {
            List<Part> targets = (mode == 0) ? GetParts()
                : ((lastHovered != null) ? new List<Part> { lastHovered } : new List<Part>());
            Color c = BuilderColor();
            for (int i = 0; i < targets.Count; i++)
            {
                ModuleReforged mod = targets[i].Modules.GetModule<ModuleReforged>();
                if (mod != null) mod.SetColour(c);
            }
        }

        void DrawColorBuilder()
        {
            bool pickerChanged = false;
            EnsurePickerTex();
            GUILayout.BeginHorizontal();
            Swatch(BuilderColor());
            if (GUILayout.Button(eyedrop ? "click!" : "pick", GUILayout.Width(48f)))
            {
                eyedrop = true;
                ScreenMessages.PostScreenMessage("Eyedropper: click anything on screen", 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            GUILayout.Label("#", builderLabel, GUILayout.Width(14f));
            string curHex = RgbToHex(Color.HSVToRGB(pH, pS, pV));
            string shown = (hexBuf != null) ? hexBuf : curHex;
            string hx = GUILayout.TextField(shown, GUILayout.Width(84f));
            if (hx != shown)
            {
                hexBuf = hx;
                Color pc;
                if (TryHex(hx, out pc)) { Color.RGBToHSV(pc, out pH, out pS, out pV); svHueKey = -1; pickerChanged = true; }
            }
            else if (hexBuf != null && shown == curHex) hexBuf = null;
            GUILayout.EndHorizontal();

            Rect sq = GUILayoutUtility.GetRect(330f, 200f, GUILayout.Width(330f), GUILayout.Height(200f));
            GUI.DrawTexture(sq, svTex, ScaleMode.StretchToFill);
            Rect mark = new Rect(sq.x + pS * sq.width - 3, sq.y + (1f - pV) * sq.height - 3, 6, 6);
            GUI.DrawTexture(mark, markTex);
            if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag)
                && sq.Contains(Event.current.mousePosition))
            {
                pS = Mathf.Clamp01((Event.current.mousePosition.x - sq.x) / sq.width);
                pV = Mathf.Clamp01(1f - (Event.current.mousePosition.y - sq.y) / sq.height);
                bufR = bufG = bufB = hexBuf = null;
                pickerChanged = true;
                Event.current.Use();
            }
            GUILayout.Space(4f);

            Rect hb = GUILayoutUtility.GetRect(330f, 18f, GUILayout.Width(330f), GUILayout.Height(18f));
            GUI.DrawTexture(hb, hueTex, ScaleMode.StretchToFill);
            Rect hm = new Rect(hb.x + pH * hb.width - 1, hb.y, 3, hb.height);
            GUI.DrawTexture(hm, markTex);
            if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag)
                && hb.Contains(Event.current.mousePosition))
            {
                pH = Mathf.Clamp01((Event.current.mousePosition.x - hb.x) / hb.width);
                svHueKey = -1;
                bufR = bufG = bufB = hexBuf = null;
                pickerChanged = true;
                Event.current.Use();
            }

            Color c2 = Color.HSVToRGB(pH, pS, pV);
            float R = SliderNum("R", c2.r * 255f, 0f, 255f, ref bufR);
            float G = SliderNum("G", c2.g * 255f, 0f, 255f, ref bufG);
            float B = SliderNum("B", c2.b * 255f, 0f, 255f, ref bufB);
            if (Mathf.RoundToInt(R) != Mathf.RoundToInt(c2.r * 255f) ||
                Mathf.RoundToInt(G) != Mathf.RoundToInt(c2.g * 255f) ||
                Mathf.RoundToInt(B) != Mathf.RoundToInt(c2.b * 255f))
            {
                Color.RGBToHSV(new Color(R / 255f, G / 255f, B / 255f), out pH, out pS, out pV);
                hexBuf = null; svHueKey = -1;
                pickerChanged = true;
            }
            float br = SliderNum("Brightness", pBright, 0f, 3f, ref bufBright);
            if (br != pBright) { pBright = br; pickerChanged = true; }
            if (pickerChanged) PreviewColourLive();

            newName = GUILayout.TextField(newName);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply")) ArmColour(BuilderColor(), "Custom colour");
            if (GUILayout.Button("Save"))
            {
                Preset pr = new Preset();
                pr.name = string.IsNullOrEmpty(newName) ? "My preset" : newName;
                pr.isColor = true;
                Color cc = BuilderColor();
                pr.r = cc.r; pr.g = cc.g; pr.b = cc.b;
                for (int i = userColor.Count - 1; i >= 0; i--) if (userColor[i].name == pr.name) userColor.RemoveAt(i);
                userColor.Add(pr); SavePresets();
            }
            if (GUILayout.Button("Reset")) { pH = 0f; pS = 0f; pV = 1f; pBright = 1f; bufR = bufG = bufB = bufBright = hexBuf = null; svHueKey = -1; PreviewColourLive(); }
            GUILayout.EndHorizontal();
            Hint("Brightness above 1 pushes past the texture.");
        }

        System.Collections.IEnumerator SamplePixel(Vector3 mousePos)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Texture2D px = new Texture2D(1, 1, TextureFormat.RGB24, false);
                px.ReadPixels(new Rect(mousePos.x, mousePos.y, 1, 1), 0, 0);
                px.Apply(false, false);
                Color c = px.GetPixel(0, 0);
                Destroy(px);
                LoadPickerColour(c);
                ScreenMessages.PostScreenMessage("Picked #" + RgbToHex(c), 3f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception e) { Debug.LogWarning("[ReforgedRedux] eyedropper failed: " + e.Message); }
        }

        void DrawMetalsTab()
        {
            Header("Metals");
            Hint("Full metal finish + colour in one click.");
            if (DrawRegion()) ReArm();
            DrawKeepShiny();
            for (int i = 0; i < MetalNames.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= MetalNames.Length) { GUILayout.Space(ColW); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColW));
                    MetalSwatch(idx);
                    if (GUILayout.Button(MetalNames[idx], GUILayout.Height(28f))) ArmMetal(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            Header("Heat Discoloration");
            Hint("Real temper colours plus a polished finish.");
            int nhv = GUILayout.Toolbar(heatVariant, new string[] { "Mixed", "Linear" });
            if (nhv != heatVariant)
            {
                heatVariant = nhv;
                ReArm();
            }
            if (DrawStrength()) ReArm();
            {
                float nhs = SliderNum("Pattern size", regPatScale, 0.25f, 3f, ref regScaleBuf);
                if (nhs != regPatScale) { regPatScale = nhs; ReArm(); }
            }
            for (int i = 0; i < HeatNames.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= HeatNames.Length) { GUILayout.Space(ColW); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColW));
                    HeatSwatch(idx, heatVariant == 1);
                    if (GUILayout.Button(HeatNames[idx], GUILayout.Height(28f))) ArmHeatDiscolor(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            DrawResetRow();
        }

        void DrawDirtTab()
        {
            EnsureCustomScan();
            Header("Weathering");
            if (DrawStrength()) ReArm();
            if (DrawPatternSize()) ReArm();
            if (DrawRegion()) ReArm();
            DrawKeepShiny();
            if (pinned.Count > 0)
            {
                GUILayout.Label("<b>Favorites</b>", builderLabel);
                for (int pi = 0; pi < pinned.Count; pi += 2)
                {
                    GUILayout.BeginHorizontal();
                    for (int k = 0; k < 2; k++)
                    {
                        int idx = pi + k;
                        if (idx >= pinned.Count) { GUILayout.Space(ColW); continue; }
                        int a = WeatherFiles.IndexOf(pinned[idx]);
                        if (a < 0) { GUILayout.Space(ColW); continue; }
                        if (GUILayout.Button(WeatherLabels[a], GUILayout.Width(ColW), GUILayout.Height(26f))) ArmWeather(a);
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(4f);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", hintStyle, GUILayout.Width(48f));
            dirtSearch = GUILayout.TextField(dirtSearch, GUILayout.Height(20f));
            if (dirtSearch.Length > 0 && GUILayout.Button("x", GUILayout.Width(22f), GUILayout.Height(20f))) dirtSearch = "";
            GUILayout.EndHorizontal();
            GUILayout.Label("All patterns (tick = pin a favorite):", hintStyle);
            dirtScroll = GUILayout.BeginScrollView(dirtScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(210f));
            string dq = dirtSearch.ToLowerInvariant();
            for (int i = 0; i < WeatherLabels.Count; i++)
            {
                if (dq.Length > 0 && WeatherLabels[i].ToLowerInvariant().IndexOf(dq) < 0) continue;
                GUILayout.BeginHorizontal();
                bool wasPin = pinned.Contains(WeatherFiles[i]);
                bool nowPin = GUILayout.Toggle(wasPin, "", GUILayout.Width(20f));
                if (nowPin != wasPin)
                {
                    if (nowPin) pinned.Add(WeatherFiles[i]); else pinned.Remove(WeatherFiles[i]);
                    SavePresets();
                }
                if (GUILayout.Button(WeatherLabels[i], GUILayout.Height(26f))) ArmWeather(i);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Clean weathering off", GUILayout.Height(24f))) ArmWeather(-1);
            Hint("Effects stack. Colourless textures take any\ntint below or a colour from the Colour tab.");
            GUILayout.Space(4f);
            Header("Tinted worn looks");
            for (int i = 0; i < DirtNames.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = 0; k < 2; k++)
                {
                    int idx = i + k;
                    if (idx >= DirtNames.Length) { GUILayout.Space(ColW); continue; }
                    GUILayout.BeginHorizontal(GUILayout.Width(ColW));
                    Swatch(new Color(DirtRGB[idx][0] / 255f, DirtRGB[idx][1] / 255f, DirtRGB[idx][2] / 255f));
                    if (GUILayout.Button(DirtNames[idx], GUILayout.Height(28f))) ArmDirtTint(idx);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndHorizontal();
            }
            DrawResetRow();
        }

        void DrawComboTab()
        {
            Header("Saved combos");
            Hint("Set a part up on the other tabs, then save it here.");
            if (DrawRegion()) ReArm();
            DrawRegionTarget();
            DrawKeepShiny();
            if (DrawStrength()) ReArm();
            if (DrawPatternSize()) ReArm();
            if (userCombo.Count == 0) GUILayout.Label("No saved combos yet.", hintStyle);
            for (int ci = 0; ci < userCombo.Count; ci++)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(userCombo[ci].name, GUILayout.Height(26f))) ArmCombo(userCombo[ci], true);
                if (GUILayout.Button("X", GUILayout.Width(26f), GUILayout.Height(26f)))
                { userCombo.RemoveAt(ci); SavePresets(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6f);
            Header("Save a new combo");
            comboName = GUILayout.TextField(comboName);
            if (GUILayout.Button("Save hovered part's combo +", GUILayout.Height(26f))) SaveComboFromHovered();
            Hint("Point at a painted part first, then click.");
            DrawResetRow();
        }

        static string StripToRecipe(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            string[] es = stack.Split(';');
            List<string> names = new List<string>();
            for (int i = 0; i < es.Length; i++)
            {
                int c = es[i].IndexOf(':');
                string nm = c > 0 ? es[i].Substring(0, c) : es[i];
                if (string.IsNullOrEmpty(nm)) continue;
                names.Add(nm);
            }
            return string.Join(";", names.ToArray());
        }

        void SaveComboFromHovered()
        {
            ModuleReforged mod = (lastHovered != null) ? lastHovered.Modules.GetModule<ModuleReforged>() : null;
            if (mod == null || !mod.Active)
            { ScreenMessages.PostScreenMessage("Point at a painted part first", 3f, ScreenMessageStyle.UPPER_CENTER); return; }
            Combo c = new Combo();
            c.name = string.IsNullOrEmpty(comboName) ? "My combo" : comboName;
            c.recipe = mod.ComboRecipe();
            float cm, csm; c.hasFinish = mod.ComboFinish(out cm, out csm); c.metal = cm; c.smooth = csm;
            float rr, gg, bb; c.hasColor = mod.ComboColor(out rr, out gg, out bb); c.r = rr; c.g = gg; c.b = bb;
            c.scale = mod.rdxScale;
            for (int i = userCombo.Count - 1; i >= 0; i--) if (userCombo[i].name == c.name) userCombo.RemoveAt(i);
            userCombo.Add(c); SavePresets();
            comboName = ""; GUIUtility.keyboardControl = 0;
            ScreenMessages.PostScreenMessage("Saved combo: " + c.name, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        static string PresetsPath()
        {
            return KSPUtil.ApplicationRootPath + "GameData/ReforgedRedux/PluginData/Presets.cfg";
        }

        static void AddDefaultFinish()
        {
            if (userFinish.Count > 0) return;
            Preset f = new Preset();
            f.name = "J4COB's most used";
            f.isColor = false; f.metal = 0.75f; f.smooth = 0.82f;
            userFinish.Add(f);
        }
        static void AddDefaultCombos()
        {
            AddDefaultFinish();
            if (userCombo.Count == 0)
            {
                userCombo.Add(new Combo { name = "J4COB's Soot Marks", recipe = "grunge;soot;leak" });
                userCombo.Add(new Combo { name = "J4COB's Reentry Burn", recipe = "grimymetal;burn;scorch;leak;soot" });
            }
        }

        static void LoadPresets()
        {
            try
            {
                string path = PresetsPath();
                if (!System.IO.File.Exists(path)) { AddDefaultCombos(); return; }
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) { AddDefaultCombos(); return; }
                { string sv = root.GetValue("showText"); if (sv != null) showHints = sv != "False"; }
                userFinish.Clear(); userColor.Clear(); userCombo.Clear(); pinned.Clear();
                ConfigNode[] cs = root.GetNodes("REDUX_COMBO");
                for (int i = 0; i < cs.Length; i++)
                {
                    Combo cb = new Combo();
                    cb.name = cs[i].GetValue("name");
                    cb.recipe = cs[i].GetValue("recipe");
                    if (string.IsNullOrEmpty(cb.recipe)) cb.recipe = StripToRecipe(cs[i].GetValue("stack"));
                    cb.scale = 1f; { float sc; if (float.TryParse(cs[i].GetValue("scale"), out sc) && sc > 0f) cb.scale = sc; }
                    cb.hasFinish = cs[i].GetValue("hasFinish") == "True";
                    float.TryParse(cs[i].GetValue("metal"), out cb.metal);
                    float.TryParse(cs[i].GetValue("smooth"), out cb.smooth);
                    cb.hasColor = cs[i].GetValue("hasColor") == "True";
                    float.TryParse(cs[i].GetValue("r"), out cb.r);
                    float.TryParse(cs[i].GetValue("g"), out cb.g);
                    float.TryParse(cs[i].GetValue("b"), out cb.b);
                    if (!string.IsNullOrEmpty(cb.name) && (!string.IsNullOrEmpty(cb.recipe) || cb.hasFinish || cb.hasColor))
                        userCombo.Add(cb);
                }
                if (cs.Length == 0) AddDefaultCombos();
                ConfigNode[] ns = root.GetNodes("REDUX_PRESET");
                for (int i = 0; i < ns.Length; i++)
                {
                    Preset pr = new Preset();
                    pr.name = ns[i].GetValue("name");
                    pr.isColor = ns[i].GetValue("type") == "color";
                    float.TryParse(ns[i].GetValue("metal"), out pr.metal);
                    float.TryParse(ns[i].GetValue("smooth"), out pr.smooth);
                    float.TryParse(ns[i].GetValue("r"), out pr.r);
                    float.TryParse(ns[i].GetValue("g"), out pr.g);
                    float.TryParse(ns[i].GetValue("b"), out pr.b);
                    if (string.IsNullOrEmpty(pr.name)) continue;
                    if (pr.isColor) userColor.Add(pr); else userFinish.Add(pr);
                }
                AddDefaultFinish();
                ConfigNode[] ps = root.GetNodes("REDUX_PIN");
                for (int i = 0; i < ps.Length; i++)
                {
                    string pn = ps[i].GetValue("name");
                    if (!string.IsNullOrEmpty(pn) && !pinned.Contains(pn)) pinned.Add(pn);
                }
                if (ps.Length == 0) pinned.AddRange(DefaultPins);
            }
            catch (Exception e) { Debug.LogError("[ReforgedRedux] load presets failed: " + e.Message); }
        }

        static void SavePresets()
        {
            try
            {
                ConfigNode root = new ConfigNode();
                for (int i = 0; i < userFinish.Count; i++) AddPresetNode(root, userFinish[i]);
                for (int i = 0; i < userColor.Count; i++) AddPresetNode(root, userColor[i]);
                for (int i = 0; i < userCombo.Count; i++)
                {
                    ConfigNode cn = root.AddNode("REDUX_COMBO");
                    cn.AddValue("name", userCombo[i].name);
                    cn.AddValue("recipe", userCombo[i].recipe);
                    cn.AddValue("scale", userCombo[i].scale);
                    if (userCombo[i].hasFinish)
                    {
                        cn.AddValue("hasFinish", "True");
                        cn.AddValue("metal", userCombo[i].metal);
                        cn.AddValue("smooth", userCombo[i].smooth);
                    }
                    if (userCombo[i].hasColor)
                    {
                        cn.AddValue("hasColor", "True");
                        cn.AddValue("r", userCombo[i].r);
                        cn.AddValue("g", userCombo[i].g);
                        cn.AddValue("b", userCombo[i].b);
                    }
                }
                for (int i = 0; i < pinned.Count; i++)
                    root.AddNode("REDUX_PIN").AddValue("name", pinned[i]);
                root.AddValue("showText", showHints);
                string path = PresetsPath();
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                root.Save(path);
            }
            catch (Exception e) { Debug.LogError("[ReforgedRedux] save presets failed: " + e.Message); }
        }

        static void AddPresetNode(ConfigNode root, Preset pr)
        {
            ConfigNode n = root.AddNode("REDUX_PRESET");
            n.AddValue("name", pr.name);
            n.AddValue("type", pr.isColor ? "color" : "finish");
            n.AddValue("metal", pr.metal);
            n.AddValue("smooth", pr.smooth);
            n.AddValue("r", pr.r);
            n.AddValue("g", pr.g);
            n.AddValue("b", pr.b);
        }
    }

#endif

    internal static class WeatherCache
    {
        internal static float StackStrength(int level)
        {
            return level >= 1000 ? Math.Max(0, Math.Min(250, level - 1000)) / 100f
                : (level <= 5 ? (level >= 2 ? 2.5f : 0.6f) : level / 100f);
        }
        const string OverlayPath = "HeatTileOverlayPrototype/Assets/";
        const string Tag = "[HeatTilePainter/ReforgedCore] ";

        // Describes the settled physical surface that owns one baked material. Stock UVs are used
        // only to locate output texels; Heat Tile Painter pattern coordinates come from the
        // interpolated part-local position. Exact region splits provide their clipped triangle
        // subset so overlapping top/bottom UV islands remain independent.
        internal sealed class BakeProjection
        {
            internal Part Part;
            internal Renderer Renderer;
            internal Mesh Mesh;
            internal IList<Vector3> Vertices;
            internal IList<Vector3> Normals;
            internal IList<Vector2> Uvs;
            internal IList<int> Triangles;
            internal int Submesh = -1;
            internal Matrix4x4 ToPart;
            internal Matrix4x4 NormalToPart;
            internal Vector2 TextureScale = Vector2.one;
            internal Vector2 TextureOffset = Vector2.zero;
            internal TextureWrapMode WrapMode = TextureWrapMode.Repeat;
            internal bool HasBasis;
            internal Vector3 BasisU;
            internal Vector3 BasisV;
            internal int Signature;

            internal bool Usable
            {
                get
                {
                    if (Part == null || Renderer == null) return false;
                    int vertexCount = Vertices != null ? Vertices.Count : (Mesh != null ? Mesh.vertexCount : 0);
                    bool hasUvs = Uvs != null ? Uvs.Count == vertexCount : Mesh != null && Mesh.isReadable;
                    return vertexCount > 0 && hasUvs
                        && (Triangles != null ? Triangles.Count >= 3 : Mesh != null && Mesh.isReadable);
                }
            }
        }

        internal static BakeProjection CreateProjection(Part part, Renderer renderer, Mesh mesh,
            int submesh, Material material)
        {
            BakeProjection projection = new BakeProjection();
            projection.Part = part;
            projection.Renderer = renderer;
            projection.Mesh = mesh;
            projection.Submesh = submesh;
            InitializeProjection(projection, material);
            return projection;
        }

        internal static BakeProjection CreateProjection(Part part, Renderer renderer,
            IList<Vector3> vertices, IList<Vector3> normals, IList<Vector2> uvs,
            IList<int> triangles, Material material)
        {
            BakeProjection projection = new BakeProjection();
            projection.Part = part;
            projection.Renderer = renderer;
            projection.Vertices = vertices;
            projection.Normals = normals;
            projection.Uvs = uvs;
            projection.Triangles = triangles;
            InitializeProjection(projection, material);
            return projection;
        }

        internal static BakeProjection ForRegion(BakeProjection source, string region)
        {
            if (source == null) return null;
            Vector3 direction; float cut, fade, height;
            if (!RegionAxisDir(region, out direction, out cut, out fade, out height)) return source;
            // Opposite sides share the same mapping plane and phase.
            if (Mathf.Abs(direction.x) > 0.0001f ? direction.x < 0f
                : (Mathf.Abs(direction.y) > 0.0001f ? direction.y < 0f : direction.z < 0f))
                direction = -direction;
            Vector3 reference = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.95f
                ? Vector3.right : Vector3.up;
            Vector3 basisU = Vector3.Cross(reference, direction).normalized;
            Vector3 basisV = Vector3.Cross(direction, basisU).normalized;
            BakeProjection projection = new BakeProjection();
            projection.Part = source.Part;
            projection.Renderer = source.Renderer;
            projection.Mesh = source.Mesh;
            projection.Vertices = source.Vertices;
            projection.Normals = source.Normals;
            projection.Uvs = source.Uvs;
            projection.Triangles = source.Triangles;
            projection.Submesh = source.Submesh;
            projection.ToPart = source.ToPart;
            projection.NormalToPart = source.NormalToPart;
            projection.TextureScale = source.TextureScale;
            projection.TextureOffset = source.TextureOffset;
            projection.WrapMode = source.WrapMode;
            projection.HasBasis = true;
            projection.BasisU = basisU;
            projection.BasisV = basisV;
            unchecked
            {
                projection.Signature = source.Signature;
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisU.x * 10000f);
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisU.y * 10000f);
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisU.z * 10000f);
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisV.x * 10000f);
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisV.y * 10000f);
                projection.Signature = projection.Signature * 31 + Mathf.RoundToInt(basisV.z * 10000f);
            }
            return projection;
        }

        static void InitializeProjection(BakeProjection projection, Material material)
        {
            projection.ToPart = RelativeMatrix(projection.Renderer != null ? projection.Renderer.transform : null,
                projection.Part != null ? projection.Part.transform : null);
            projection.NormalToPart = projection.ToPart.inverse.transpose;
            if (material != null && material.HasProperty("_MainTex"))
            {
                projection.TextureScale = material.GetTextureScale("_MainTex");
                projection.TextureOffset = material.GetTextureOffset("_MainTex");
                Texture source = Original(material.GetTexture("_MainTex"));
                if (source != null) projection.WrapMode = source.wrapMode;
            }
            projection.Signature = ProjectionSignature(projection);
        }

        static Matrix4x4 RelativeMatrix(Transform from, Transform to)
        {
            if (from == null || to == null || from == to) return Matrix4x4.identity;
            Matrix4x4 result = Matrix4x4.identity;
            Transform current = from;
            while (current != null && current != to)
            {
                result = Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale) * result;
                current = current.parent;
            }
            return current == to ? result : to.worldToLocalMatrix * from.localToWorldMatrix;
        }

        static int StableTextHash(string value)
        {
            unchecked
            {
                int hash = 17;
                if (value != null) for (int i = 0; i < value.Length; i++) hash = hash * 31 + value[i];
                return hash;
            }
        }

        static int ProjectionSignature(BakeProjection projection)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + StableTextHash(projection.Renderer != null && projection.Part != null
                    ? ModuleHeatTileReforgedCore.RelPath(projection.Renderer.transform, projection.Part.transform) : "");
                int vertexCount = projection.Vertices != null ? projection.Vertices.Count
                    : (projection.Mesh != null ? projection.Mesh.vertexCount : 0);
                hash = hash * 31 + vertexCount;
                hash = hash * 31 + projection.Submesh;
                for (int i = 0; i < 16; i++) hash = hash * 31
                    + Mathf.RoundToInt(projection.ToPart[i] * 10000f);
                hash = hash * 31 + Mathf.RoundToInt(projection.TextureScale.x * 10000f);
                hash = hash * 31 + Mathf.RoundToInt(projection.TextureScale.y * 10000f);
                hash = hash * 31 + Mathf.RoundToInt(projection.TextureOffset.x * 10000f);
                hash = hash * 31 + Mathf.RoundToInt(projection.TextureOffset.y * 10000f);
                IList<int> triangles = projection.Triangles;
                if (triangles != null)
                {
                    hash = hash * 31 + triangles.Count;
                    for (int i = 0; i < triangles.Count; i++)
                    {
                        int index = triangles[i];
                        hash = hash * 31 + index;
                        if (projection.Vertices != null && index >= 0
                            && index < projection.Vertices.Count)
                        {
                            Vector3 position = projection.Vertices[index];
                            hash = hash * 31 + Mathf.RoundToInt(position.x * 10000f);
                            hash = hash * 31 + Mathf.RoundToInt(position.y * 10000f);
                            hash = hash * 31 + Mathf.RoundToInt(position.z * 10000f);
                        }
                        if (projection.Uvs != null && index >= 0 && index < projection.Uvs.Count)
                        {
                            Vector2 uv = projection.Uvs[index];
                            hash = hash * 31 + Mathf.RoundToInt(uv.x * 10000f);
                            hash = hash * 31 + Mathf.RoundToInt(uv.y * 10000f);
                        }
                    }
                }
                else if (projection.Mesh != null)
                {
                    hash = hash * 31 + projection.Mesh.GetInstanceID();
                    hash = hash * 31 + projection.Mesh.subMeshCount;
                    Bounds bounds = projection.Mesh.bounds;
                    hash = hash * 31 + Mathf.RoundToInt(bounds.center.x * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(bounds.center.y * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(bounds.center.z * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(bounds.extents.x * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(bounds.extents.y * 1000f);
                    hash = hash * 31 + Mathf.RoundToInt(bounds.extents.z * 1000f);
                }
                return hash;
            }
        }

        static readonly Dictionary<int, Texture> bakedToOrig = new Dictionary<int, Texture>();
        static readonly Dictionary<string, Texture2D> baked = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Texture2D> bakedBump = new Dictionary<string, Texture2D>();
        class BakeMem
        {
            public string spec;
            public Color32[] main;
            public Color32[] bump;
            public int w, h;
        }
        static readonly Dictionary<string, BakeMem> lastBake = new Dictionary<string, BakeMem>();
        static readonly List<string> lastOrder = new List<string>();
        const int BakeMemCap = 2;

        static float PatternStrength(string name)
        {
            if (name.StartsWith("camo")) return 1.6f;
            if (name == "soot" || name == "burn" || name == "grime" || name == "grunge") return 1.7f;
            if (name.StartsWith("scorch")) return 1.4f;
            return 1f;
        }
        static readonly Dictionary<int, Texture> bumpToOrig = new Dictionary<int, Texture>();
        static readonly HashSet<int> bumpSynth = new HashSet<int>();
        static readonly Dictionary<string, Color32[]> overlayPix = new Dictionary<string, Color32[]>();
        static readonly Dictionary<string, int> overlayW = new Dictionary<string, int>();
        static readonly Dictionary<string, int> overlayH = new Dictionary<string, int>();
        internal static void InvalidateOverlay(string id)
        {
            foreach (string suffix in new[] { "", "_n", "_b", "_b_n" })
            {
                string key = id + suffix;
                overlayPix.Remove(key); overlayW.Remove(key); overlayH.Remove(key);
            }
        }
        static readonly Dictionary<string, float[]> masks = new Dictionary<string, float[]>();
        static readonly HashSet<string> maskWarned = new HashSet<string>();
        const int MR = 128;
        internal static bool Verbose = false;

        internal static bool ParallelBake = true;
        const int ParMinPixels = 262144;
        static void ForRows(int h, bool parallel, Action<int> row)
        {
            if (parallel && ParallelBake) Parallel.For(0, h, row);
            else for (int y = 0; y < h; y++) row(y);
        }

        public static Texture Original(Texture t)
        {
            if (t == null) return null;
            Texture o;
            return bakedToOrig.TryGetValue(t.GetInstanceID(), out o) ? o : t;
        }

        static float TileMeters(string name)
        {
            string definitionName = name != null && name.EndsWith("_b", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 2) : name;
            HeatTileOverlayPrototype.HeatTileTextureDefinition definition;
            if (HeatTileOverlayPrototype.HeatTileTextureLibrary.FindExact(definitionName, out definition))
                return Mathf.Max(0.05f, definition.PatternSpan);
            switch (definitionName)
            {
                case "hrsi_legacy": return 1.28f;
                case "hrsi_b9_exact": return 4.0f;
                case "hrsi_b9_hd": return 4.0f;
                case "hrsi_brick": return 5.12f;
                case "edge_gasket": return 0.64f;
                case "edge_reinforced": return 0.64f;
                case "fabric":  return 1.2f;
                case "diamond": return 1.2f;
                case "grain":   return 1.5f;
                case "scratch": return 1.6f;
                case "tiles":   return 1.8f;
                case "tilesw":  return 1.8f;
                case "mli":     return 2.5f;
                case "brushed": return 3.0f;
                case "paving1": return 2.5f;
                case "paving2": return 2.0f;
                case "tiles138": return 2.0f;
                case "metal3":  return 3.0f;
                case "plates6": return 2.0f;
                case "plates14": return 3.0f;
                case "ornament": return 1.0f;
                case "tactile1": case "tactile2": return 2.0f;
                case "walkway1": case "walkway2": return 2.5f;
                case "chip":    case "chipc": return 2.0f;
                case "concrete3": return 3.0f;
                case "leather": return 2.0f;
                case "carpet":  return 2.0f;
                case "net":     return 1.5f;
                case "styro":   return 2.5f;
                case "paving3": return 2.5f;
                case "paving4": return 2.5f;
                case "blockwall": return 2.5f;
                case "facade":   return 3.0f;
                case "cladding": return 3.0f;
                case "intiles":  return 2.0f;
                case "planksb":  return 3.0f;
                case "msheet":   return 3.0f;
                case "carbon":   return 2.5f;
                case "ornament2": return 1.0f;
                case "roadlines": case "roadlines2": case "roadlines3": return 4.0f;
                case "camo44":  return 1.5f;
                case "camo1": case "camo54": return 3.0f;
                case "camo10": case "camo13": case "camo26": case "camo22": case "camo55": return 2.5f;
                case "ventsteel": return 1.0f;
                case "wornsteel": return 2.5f;
                case "metalgrid": return 1.5f;
                case "grimymetal": return 2.5f;
                case "hessian": return 1.5f;
                default:        return 5.0f;
            }
        }

        static bool WorldScaled(string name)
        {
            string definitionName = name != null && name.EndsWith("_b", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 2) : name;
            HeatTileOverlayPrototype.HeatTileTextureDefinition definition;
            if (HeatTileOverlayPrototype.HeatTileTextureLibrary.FindExact(definitionName, out definition))
                return !definition.IsFlatColor;
            switch (definitionName)
            {
                case "hrsi_legacy": case "hrsi_b9_exact": case "hrsi_b9_hd": case "hrsi_brick":
                case "edge_gasket": case "edge_reinforced":
                case "tiles": case "tilesw": case "scratch": case "mli": case "grain":
                case "diamond":
                case "brushed": case "fabric":
                case "paving1": case "paving2":
                case "tiles138": case "metal3":
                case "plates6": case "plates14": case "ornament":
                case "tactile1": case "tactile2":
                case "walkway1": case "walkway2": case "chip": case "chipc":
                case "concrete3": case "leather":
                case "carpet": case "net": case "styro":
                case "paving3": case "paving4": case "ornament2":
                case "roadlines": case "roadlines2": case "roadlines3":
                case "blockwall": case "facade": case "cladding": case "intiles":
                case "planksb": case "msheet": case "carbon":
                case "camo1": case "camo10": case "camo13": case "camo26":
                case "camo54": case "camo22": case "camo44": case "camo55":
                case "ventsteel":
                case "hessian":
                case "wornsteel": case "metalgrid": case "grimymetal":
                case "heatd1": case "heatd2": case "heatd3": case "heatd4":
                case "heatd5": case "heatd6": case "heatd7": case "heatd8":
                    return true;
                default:
                    return false;
            }
        }

        static readonly HashSet<string> DeepRelief = new HashSet<string> {
            "diamond", "metal3", "plates6", "chip", "concrete3", "paving3", "paving4",
            "leak", "grimymetal", "wornsteel", "ventsteel" };

        static Color32 ParseHex(string hx)
        {
            try
            {
                return new Color32(
                    (byte)Convert.ToInt32(hx.Substring(0, 2), 16),
                    (byte)Convert.ToInt32(hx.Substring(2, 2), 16),
                    (byte)Convert.ToInt32(hx.Substring(4, 2), 16), 255);
            }
            catch { return new Color32(255, 255, 255, 255); }
        }

        static bool GetOverlayPixels(string name, out Color32[] pix, out int w, out int h)
        {
            pix = null; w = h = 0;
            if (overlayPix.TryGetValue(name, out pix))
            {
                if (pix == null) return false;
                w = overlayW[name]; h = overlayH[name];
                return true;
            }
            Texture2D tex = null;
            bool definitionNormal = false;
            string definitionId = name;
            HeatTileOverlayPrototype.HeatTileTextureDefinition definition;
            if (!HeatTileOverlayPrototype.HeatTileTextureLibrary.FindExact(definitionId, out definition)
                && name.EndsWith("_n", StringComparison.OrdinalIgnoreCase))
            {
                definitionId = name.Substring(0, name.Length - 2);
                definitionNormal = true;
            }
            if (!HeatTileOverlayPrototype.HeatTileTextureLibrary.TryResolveTexture(
                    definitionId, definitionNormal, out tex))
            {
                tex = (GameDatabase.Instance != null)
                    ? GameDatabase.Instance.GetTexture(OverlayPath + name, false) : null;
            }
            if (tex == null)
            {
                Debug.LogWarning(Tag + "weathering overlay missing: " + name);
                overlayPix[name] = null;
                return false;
            }
            try { pix = tex.GetPixels32(); w = tex.width; h = tex.height; }
            catch
            {

                try
                {
                    RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(tex, rt);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    Texture2D cp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                    cp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                    cp.Apply(false, false);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    pix = cp.GetPixels32(); w = cp.width; h = cp.height;
                    UnityEngine.Object.Destroy(cp);
                }
                catch (Exception e)
                {
                    Debug.LogError(Tag + "cannot read overlay " + name + ": " + e.Message);
                    overlayPix[name] = null;
                    return false;
                }
            }
            if ((definitionNormal || name.EndsWith("_n")) && pix != null)
                for (int i = 0; i < pix.Length; i++) pix[i].a = pix[i].r;
            overlayPix[name] = pix; overlayW[name] = w; overlayH[name] = h;
            return pix != null;
        }

        static float Smooth(float lo, float hi, float v)
        {
            float t = Mathf.Clamp01((v - lo) / (hi - lo));
            return t * t * (3f - 2f * t);
        }

        static List<Renderer> PartRenderers(Part part)
        {
            List<Renderer> mine = new List<Renderer>();
            if (part == null) return mine;
            Transform root = part.transform.Find("model");
            if (root == null) root = part.transform;
            Renderer[] all = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (!all[i].enabled || !all[i].gameObject.activeInHierarchy) continue;
                if (all[i].GetComponentInParent<Camera>() != null) continue;
                Part owner = all[i].GetComponentInParent<Part>();
                if (owner == null || owner == part) mine.Add(all[i]);
            }
            return mine;
        }

        internal static int GeometrySignature(Part part)
        {
            unchecked
            {
                int h = 17;
                List<Renderer> rs = PartRenderers(part);
                h = h * 31 + rs.Count;
                for (int i = 0; i < rs.Count; i++)
                {
                    Renderer r = rs[i];
                    MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    ModuleHeatTileReforgedCore core = part.FindModuleImplementing<ModuleHeatTileReforgedCore>();
                    if (core != null) mesh = core.PreviewSourceMesh(r, mesh);
                    if (mesh == null) { h = h * 31; continue; }
                    h = h * 31 + mesh.GetInstanceID();
                    h = h * 31 + mesh.vertexCount;
                    Bounds b = mesh.bounds;
                    h = h * 31 + Mathf.RoundToInt(b.center.x * 1000f);
                    h = h * 31 + Mathf.RoundToInt(b.center.y * 1000f);
                    h = h * 31 + Mathf.RoundToInt(b.center.z * 1000f);
                    h = h * 31 + Mathf.RoundToInt(b.extents.x * 1000f);
                    h = h * 31 + Mathf.RoundToInt(b.extents.y * 1000f);
                    h = h * 31 + Mathf.RoundToInt(b.extents.z * 1000f);
                    Vector3 s = r.transform.lossyScale;
                    h = h * 31 + Mathf.RoundToInt(s.x * 1000f);
                    h = h * 31 + Mathf.RoundToInt(s.y * 1000f);
                    h = h * 31 + Mathf.RoundToInt(s.z * 1000f);
                }
                return h;
            }
        }

        static bool HasPlacedLayer(string stackSpec)
        {
            if (string.IsNullOrEmpty(stackSpec)) return false;
            string[] entries = stackSpec.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string[] f = entries[i].Split(':');
                if (f.Length >= 3 && f[1] != "whole") return true;
            }
            return false;
        }

        internal static bool HasWorldScaledLayer(string stackSpec)
        {
            if (string.IsNullOrEmpty(stackSpec)) return false;
            string[] entries = stackSpec.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string[] fields = entries[i].Split(':');
                if (fields.Length > 0 && !fields[0].StartsWith("tint#") && WorldScaled(fields[0]))
                    return true;
            }
            return false;
        }

        static readonly Dictionary<string, Vector2> uvScaleCache = new Dictionary<string, Vector2>();

        // A bounded logarithmic histogram gives the world-area-weighted median without retaining
        // every triangle on large procedural meshes. The median ignores tiny, heavily stretched
        // atlas islands that used to skew the single whole-part area ratio.
        sealed class UvScaleHistogram
        {
            const int BinCount = 192;
            const double MinimumLog2 = -12.0;
            const double MaximumLog2 = 16.0;
            readonly double[] weights = new double[BinCount];
            double totalWeight;

            public void Add(double metresPerUv, double worldArea)
            {
                if (metresPerUv <= 0.0 || worldArea <= 1e-12 || double.IsNaN(metresPerUv)
                    || double.IsInfinity(metresPerUv)) return;
                double log = System.Math.Log(metresPerUv, 2.0);
                double normalized = (log - MinimumLog2) / (MaximumLog2 - MinimumLog2);
                int bin = Mathf.Clamp((int)System.Math.Floor(normalized * BinCount), 0, BinCount - 1);
                weights[bin] += worldArea;
                totalWeight += worldArea;
            }

            public float Median()
            {
                if (totalWeight <= 1e-12) return -1f;
                double target = totalWeight * 0.5;
                double cumulative = 0.0;
                for (int i = 0; i < weights.Length; i++)
                {
                    cumulative += weights[i];
                    if (cumulative < target) continue;
                    double log = MinimumLog2 + (i + 0.5) / BinCount * (MaximumLog2 - MinimumLog2);
                    return (float)System.Math.Pow(2.0, log);
                }
                return -1f;
            }
        }

        static Vector2 UvWorldScale(Part part, Texture sourceTexture)
        {
            if (part == null) return new Vector2(-1f, -1f);
            Texture source = Original(sourceTexture);
            int sourceId = source != null ? source.GetInstanceID() : 0;
            int materialSignature = MaterialUvSignature(part, source);
            string key = part.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "|"
                + GeometrySignature(part).ToString(CultureInfo.InvariantCulture) + "|"
                + sourceId.ToString(CultureInfo.InvariantCulture) + "|"
                + materialSignature.ToString(CultureInfo.InvariantCulture);
            Vector2 cached;
            if (uvScaleCache.TryGetValue(key, out cached)) return cached;

            Vector2 result = new Vector2(-1f, -1f);
            try
            {
                UvScaleHistogram uHistogram = new UvScaleHistogram();
                UvScaleHistogram vHistogram = new UvScaleHistogram();
                List<Renderer> renderers = PartRenderers(part);
                for (int i = 0; i < renderers.Count; i++)
                    AddMaterialScaleSamples(renderers[i], source, uHistogram, vHistogram);
                result = new Vector2(uHistogram.Median(), vHistogram.Median());
                if (result.x <= 0f || result.y <= 0f)
                {
                    float fallback = WholePartUvWorldScale(renderers);
                    result = new Vector2(fallback, fallback);
                }
            }
            catch { result = new Vector2(-1f, -1f); }
            uvScaleCache[key] = result;
            return result;
        }

        static int MaterialUvSignature(Part part, Texture source)
        {
            unchecked
            {
                int hash = 17;
                List<Renderer> renderers = PartRenderers(part);
                for (int i = 0; i < renderers.Count; i++)
                {
                    Material[] materials = renderers[i] != null ? renderers[i].sharedMaterials : null;
                    if (materials == null) continue;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        Material material = materials[slot];
                        if (!MaterialUsesTexture(material, source)) continue;
                        Vector2 scale = material.GetTextureScale("_MainTex");
                        // Renderer order is stable within one part definition. Do not use Unity
                        // instance IDs here or identical symmetry parts would lose bake sharing.
                        hash = hash * 31 + i;
                        hash = hash * 31 + slot;
                        hash = hash * 31 + Mathf.RoundToInt(scale.x * 10000f);
                        hash = hash * 31 + Mathf.RoundToInt(scale.y * 10000f);
                    }
                }
                return hash;
            }
        }

        static void AddMaterialScaleSamples(Renderer renderer, Texture source,
            UvScaleHistogram uHistogram, UvScaleHistogram vHistogram)
        {
            if (renderer == null || source == null) return;
            Mesh mesh = RendererMesh(renderer);
            if (mesh == null || !mesh.isReadable) return;
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            if (vertices == null || uvs == null || vertices.Length == 0 || uvs.Length != vertices.Length) return;
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return;
            Matrix4x4 localToWorld = renderer.localToWorldMatrix;
            int submeshCount = mesh.subMeshCount;
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                Material material = materials[Mathf.Min(submesh, materials.Length - 1)];
                if (!MaterialUsesTexture(material, source)) continue;
                Vector2 textureScale = material.GetTextureScale("_MainTex");
                if (System.Math.Abs(textureScale.x) <= 1e-9 || System.Math.Abs(textureScale.y) <= 1e-9) continue;
                int[] triangles = mesh.GetTriangles(submesh);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Length
                        || b >= vertices.Length || c >= vertices.Length) continue;
                    Vector3 wa = localToWorld.MultiplyPoint3x4(vertices[a]);
                    Vector3 wb = localToWorld.MultiplyPoint3x4(vertices[b]);
                    Vector3 wc = localToWorld.MultiplyPoint3x4(vertices[c]);
                    double worldArea = Vector3.Cross(wb - wa, wc - wa).magnitude * 0.5;
                    Vector2 ua = uvs[a], ub = uvs[b], uc = uvs[c];
                    double du1 = (ub.x - ua.x) * textureScale.x;
                    double dv1 = (ub.y - ua.y) * textureScale.y;
                    double du2 = (uc.x - ua.x) * textureScale.x;
                    double dv2 = (uc.y - ua.y) * textureScale.y;
                    double determinant = du1 * dv2 - du2 * dv1;
                    if (worldArea <= 1e-10 || System.Math.Abs(determinant) <= 1e-10) continue;
                    Vector3 edge1 = wb - wa;
                    Vector3 edge2 = wc - wa;
                    Vector3 worldPerU = (edge1 * (float)dv2 - edge2 * (float)dv1) / (float)determinant;
                    Vector3 worldPerV = (-edge1 * (float)du2 + edge2 * (float)du1) / (float)determinant;
                    uHistogram.Add(worldPerU.magnitude, worldArea);
                    vHistogram.Add(worldPerV.magnitude, worldArea);
                }
            }
        }

        static Mesh RendererMesh(Renderer renderer)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) return filter.sharedMesh;
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            return skinned != null ? skinned.sharedMesh : null;
        }

        static bool MaterialUsesTexture(Material material, Texture source)
        {
            if (material == null || source == null || !material.HasProperty("_MainTex")) return false;
            Texture candidate = Original(material.GetTexture("_MainTex"));
            return candidate != null && candidate.GetInstanceID() == source.GetInstanceID();
        }

        static float WholePartUvWorldScale(List<Renderer> renderers)
        {
            double sumWorld = 0.0, sumUv = 0.0;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                Mesh mesh = RendererMesh(renderer);
                if (mesh == null || !mesh.isReadable) continue;
                Vector3[] vertices = mesh.vertices;
                Vector2[] uvs = mesh.uv;
                if (vertices == null || uvs == null || uvs.Length != vertices.Length) continue;
                Matrix4x4 localToWorld = renderer.localToWorldMatrix;
                int[] triangles = mesh.triangles;
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    Vector3 wa = localToWorld.MultiplyPoint3x4(vertices[a]);
                    Vector3 wb = localToWorld.MultiplyPoint3x4(vertices[b]);
                    Vector3 wc = localToWorld.MultiplyPoint3x4(vertices[c]);
                    sumWorld += Vector3.Cross(wb - wa, wc - wa).magnitude * 0.5;
                    Vector2 ua = uvs[a], ub = uvs[b], uc = uvs[c];
                    sumUv += System.Math.Abs((ub.x - ua.x) * (uc.y - ua.y)
                        - (uc.x - ua.x) * (ub.y - ua.y)) * 0.5;
                }
            }
            return sumUv > 1e-6 && sumWorld > 1e-9
                ? (float)System.Math.Sqrt(sumWorld / sumUv) : -1f;
        }

        static float PartMaxDimension(Part part)
        {
            try
            {
                List<Renderer> rs = PartRenderers(part);
                bool got = false; Bounds b = new Bounds();
                for (int i = 0; i < rs.Count; i++)
                {
                    if (!got) { b = rs[i].bounds; got = true; }
                    else b.Encapsulate(rs[i].bounds);
                }
                if (!got) return 2.5f;
                return Mathf.Max(0.3f, Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)));
            }
            catch { return 2.5f; }
        }

        internal static bool RegionAxisDir(string tok, out Vector3 dir, out float cut, out float fade, out float height)
        {
            dir = Vector3.up; cut = 0.4f; fade = 0.2f; height = 1f;
            if (string.IsNullOrEmpty(tok) || tok == "whole") return false;
            // Heat Tile Painter vector region: vX_Y_Z_cutPercent_fadePercent_heightPercent.
            // Vector components are signed thousandths in the part's local frame.
            if (tok[0] == 'v' && tok.IndexOf('_') > 0)
            {
                string[] vf = tok.Substring(1).Split('_');
                int x, y, z, c, fd;
                if (vf.Length >= 5 && int.TryParse(vf[0], out x) && int.TryParse(vf[1], out y)
                    && int.TryParse(vf[2], out z) && int.TryParse(vf[3], out c)
                    && int.TryParse(vf[4], out fd))
                {
                    dir = new Vector3(x / 1000f, y / 1000f, z / 1000f);
                    if (dir.sqrMagnitude < 0.000001f) return false;
                    dir.Normalize();
                    cut = c >= 1000
                        ? Mathf.Clamp01((c - 1000) / 1000f)
                        : Mathf.Clamp01(c / 100f);
                    fade = Mathf.Clamp01(fd / 100f);
                    int hd;
                    if (vf.Length >= 6 && int.TryParse(vf[5], out hd)) height = Mathf.Clamp01(hd / 100f);
                    return true;
                }
                return false;
            }
            if (tok[0] == 'g' && tok.IndexOf('_') > 0)
            {
                string[] f = tok.Substring(1).Split('_');
                int ax, c, fd;
                if (f.Length >= 3 && int.TryParse(f[0], out ax) && int.TryParse(f[1], out c) && int.TryParse(f[2], out fd))
                {
                    switch (ax)
                    {
                        case 0: dir = new Vector3(0f, 1f, 0f); break;
                        case 1: dir = new Vector3(0f, -1f, 0f); break;
                        case 2: dir = new Vector3(0f, 0f, 1f); break;
                        case 3: dir = new Vector3(0f, 0f, -1f); break;
                        case 4: dir = new Vector3(-1f, 0f, 0f); break;
                        default: dir = new Vector3(1f, 0f, 0f); break;
                    }
                    cut = c / 100f; fade = fd / 100f;
                    int hd; if (f.Length >= 4 && int.TryParse(f[3], out hd)) height = hd / 100f;
                    return true;
                }
                return false;
            }
            float ang, lc, lf;
            if (ParseRegion(tok, out ang, out lc, out lf))
            {
                dir = new Vector3(Mathf.Sin(ang), Mathf.Cos(ang), 0f); cut = lc; fade = lf; return true;
            }
            return false;
        }

        internal static float RegionFacingWeight(string tok, Vector3 localNormal)
        {
            if (string.IsNullOrEmpty(tok) || tok[0] != 'v') return 1f;
            string[] f = tok.Substring(1).Split('_');
            int enabled;
            if (f.Length < 8 || !int.TryParse(f[6], out enabled) || enabled == 0) return 1f;
            int wrap;
            if (!int.TryParse(f[7], out wrap)) wrap = 82;
            Vector3 dir; float cut, fade, height;
            if (!RegionAxisDir(tok, out dir, out cut, out fade, out height)) return 1f;
            if (localNormal.sqrMagnitude < 0.000001f) return 0f;
            float threshold = Mathf.Cos(Mathf.Clamp(wrap, 0, 180) * Mathf.Deg2Rad);
            float dot = Vector3.Dot(localNormal.normalized, dir);
            return Smooth(threshold - 0.04f, threshold + 0.04f, dot);
        }
        internal static bool RegionFacingPlane(string tok,out Vector3 direction,out float threshold)
        {
            direction=Vector3.zero;threshold=0;
            if(string.IsNullOrEmpty(tok) || tok[0]!='v')return false;
            string[] fields=tok.Substring(1).Split('_');int enabled,wrap;
            if(fields.Length<8 || !int.TryParse(fields[6],out enabled) || enabled==0)return false;
            if(!int.TryParse(fields[7],out wrap))wrap=82;
            float cut,fade,height;
            if(!RegionAxisDir(tok,out direction,out cut,out fade,out height))return false;
            threshold=Mathf.Cos(Mathf.Clamp(wrap,0,180)*Mathf.Deg2Rad);return true;
        }

        static bool ParseRegion(string tok, out float ang, out float cut, out float fade)
        {
            ang = 0f; cut = 0.5f; fade = 0.3f;
            if (string.IsNullOrEmpty(tok)) return false;
            if (tok == "bottom") { cut = 0.45f; fade = 0.18f; return true; }
            if (tok == "top") { ang = Mathf.PI; cut = 0.45f; fade = 0.18f; return true; }
            if (tok[0] != 'r' || tok.IndexOf('_') < 0) return false;
            string[] f = tok.Substring(1).Split('_');
            if (f.Length != 3) return false;
            int a, c, fd;
            if (!int.TryParse(f[0], out a) || !int.TryParse(f[1], out c) || !int.TryParse(f[2], out fd)) return false;
            ang = a * Mathf.Deg2Rad; cut = c / 100f; fade = fd / 100f; return true;
        }

        static float RegionWeight(float cut, float fade, float c)
        {
            float hw = Mathf.Max(0.005f, fade * 0.6f);
            return Smooth(cut - hw, cut + hw, c);
        }

        static float RegionWeightUV(string tok, float u, float v)
        {
            float ang, cut, fade;
            if (!ParseRegion(tok, out ang, out cut, out fade)) return 1f;
            float c = 0.5f + (u - 0.5f) * Mathf.Sin(ang) + (v - 0.5f) * Mathf.Cos(ang);
            return RegionWeight(cut, fade, Mathf.Clamp01(c));
        }

        internal static float[] GetMask(Part part, string place)
        {
            if (part == null) return null;
            string pname = (part.partInfo != null) ? part.partInfo.name : part.name;
            List<Renderer> rs = PartRenderers(part);

            int gsig = GeometrySignature(part);
            string key = pname + "|" + place + "|" + gsig;
            float[] cached;
            if (masks.TryGetValue(key, out cached)) return cached;

            float[] m = null;
            try
            {
                float rcut, rfade, rheight;
                Vector3 dir;
                if (!RegionAxisDir(place, out dir, out rcut, out rfade, out rheight)) { dir = Vector3.up; rcut = 0.4f; }
                float lo = float.MaxValue, hi = float.MinValue;
                float vlo = float.MaxValue, vhi = float.MinValue;
                List<Mesh> meshes = new List<Mesh>(); List<Transform> trs = new List<Transform>();
                for (int i = 0; i < rs.Count; i++)
                {
                    MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    Mesh mesh = mf.sharedMesh;
                    Vector3[] vs = mesh.vertices;
                    for (int v = 0; v < vs.Length; v++)
                    {
                        Vector3 pl = part.transform.InverseTransformPoint(rs[i].transform.TransformPoint(vs[v]));
                        float c = Vector3.Dot(pl, dir);
                        if (c < lo) lo = c;
                        if (c > hi) hi = c;
                        if (pl.y < vlo) vlo = pl.y;
                        if (pl.y > vhi) vhi = pl.y;
                    }
                    meshes.Add(mesh); trs.Add(rs[i].transform);
                }
                if (meshes.Count == 0 || hi - lo < 1e-4f)
                {
                    if (maskWarned.Add(pname))
                        Debug.LogWarning(Tag + "no readable mesh on " + pname + " - coverage falls back to whole part");
                    masks[key] = null;
                    return null;
                }
                m = new float[MR * MR];
                int[] cnt = new int[MR * MR];
                for (int mi = 0; mi < meshes.Count; mi++)
                {
                    Mesh mesh = meshes[mi];
                    Vector3[] vs = mesh.vertices;
                    Vector3[] ns = mesh.normals;
                    Vector2[] uv = mesh.uv;
                    int[] tris = mesh.triangles;
                    if (uv == null || uv.Length != vs.Length) continue;
                    bool useH = rheight < 0.999f && (vhi - vlo) > 1e-5f;

                    float bandC = Mathf.Max(0.02f, rfade * Mathf.Max(0.05f, 1f - rcut));
                    float bandH = Mathf.Max(0.02f, rfade * Mathf.Max(0.05f, rheight));
                    float[] wv = new float[vs.Length];
                    for (int v = 0; v < vs.Length; v++)
                    {
                        Vector3 pl = part.transform.InverseTransformPoint(trs[mi].TransformPoint(vs[v]));
                        float cA = (Vector3.Dot(pl, dir) - lo) / (hi - lo);
                        float w = Smooth(rcut, rcut + bandC, cA);
                        if (useH)
                        {
                            float hN = (pl.y - vlo) / (vhi - vlo);
                            w *= 1f - Smooth(rheight - bandH, rheight, hN);
                        }
                        if (ns != null && ns.Length == vs.Length)
                        {
                            Vector3 ln = part.transform.InverseTransformDirection(
                                trs[mi].TransformDirection(ns[v])).normalized;
                            w *= RegionFacingWeight(place, ln);
                        }
                        wv[v] = w;
                    }
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                        RasterTri(m, cnt, uv[tris[t]], uv[tris[t + 1]], uv[tris[t + 2]], wv[tris[t]], wv[tris[t + 1]], wv[tris[t + 2]]);
                }

                for (int i2 = 0; i2 < m.Length; i2++)
                    if (cnt[i2] > 0) m[i2] /= cnt[i2];
                SmoothMask(m, cnt);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Tag + "coverage mask failed on " + pname + ": " + e.Message);
                m = null;
            }
            masks[key] = m;
            return m;
        }

        static readonly Dictionary<string, Texture2D> metalMasks = new Dictionary<string, Texture2D>();
        public static Texture2D MetallicMask(Part part, string place, float metal, float smooth) { return MetallicMask(part, place, metal, smooth, null); }
        public static Texture2D MetallicMask(Part part, string place, float metal, float smooth, float[] maskOverride)
        {
            float[] mask; string key = null;
            if (maskOverride != null) mask = maskOverride;
            else
            {
                if (part == null) return null;
                string pname = (part.partInfo != null) ? part.partInfo.name : part.name;
                key = pname + "|" + place + "|m" + metal.ToString("0.00") + "|g" + smooth.ToString("0.00");
                Texture2D ct;
                if (metalMasks.TryGetValue(key, out ct) && ct != null) return ct;
                mask = GetMask(part, place);
            }
            if (mask == null) return null;
            Texture2D t = new Texture2D(MR, MR, TextureFormat.RGBA32, true);
            Color32[] px = new Color32[MR * MR];
            for (int i = 0; i < px.Length; i++)
            {
                float w = Mathf.Clamp01(mask[i]);
                byte met = (byte)(Mathf.Clamp01(metal * w) * 255f);
                byte gl = (byte)(Mathf.Clamp01(smooth * w) * 255f);
                px[i] = new Color32(met, met, met, gl);
            }
            t.SetPixels32(px); t.Apply(true, false);
            if (key != null) metalMasks[key] = t;
            return t;
        }

        internal static float[] RasterMask(List<Vector2> uv, List<float> wv, List<int> tris)
        {
            float[] m = new float[MR * MR];
            int[] cnt = new int[MR * MR];
            for (int t = 0; t + 2 < tris.Count; t += 3)
                RasterTri(m, cnt, uv[tris[t]], uv[tris[t + 1]], uv[tris[t + 2]], wv[tris[t]], wv[tris[t + 1]], wv[tris[t + 2]]);
            for (int i = 0; i < m.Length; i++) if (cnt[i] > 0) m[i] /= cnt[i];
            SmoothMask(m, cnt);
            return m;
        }

        static void SmoothMask(float[] m, int[] cnt)
        {
            bool[] has = new bool[m.Length];
            for (int i = 0; i < m.Length; i++) has[i] = cnt[i] > 0;
            for (int pass = 0; pass < 4; pass++)
            {
                bool[] nhas = (bool[])has.Clone();
                for (int y = 0; y < MR; y++)
                {
                    for (int x = 0; x < MR; x++)
                    {
                        int idx = y * MR + x;
                        if (has[idx]) continue;
                        float sum = 0f; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = ((y + dy) % MR + MR) % MR;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = ((x + dx) % MR + MR) % MR;
                                int j = yy * MR + xx;
                                if (has[j]) { sum += m[j]; n++; }
                            }
                        }
                        if (n > 0) { m[idx] = sum / n; nhas[idx] = true; }
                    }
                }
                has = nhas;
            }
            float[] tmp = new float[m.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < MR; y++)
                {
                    for (int x = 0; x < MR; x++)
                    {
                        float sum = 0f;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = ((y + dy) % MR + MR) % MR;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = ((x + dx) % MR + MR) % MR;
                                sum += m[yy * MR + xx];
                            }
                        }
                        tmp[y * MR + x] = sum / 9f;
                    }
                }
                Array.Copy(tmp, m, m.Length);
            }
        }

        static void RasterTri(float[] m, int[] cnt, Vector2 a, Vector2 b, Vector2 c, float wa, float wb, float wc)
        {
            float ax = a.x * MR, ay = a.y * MR, bx = b.x * MR, by = b.y * MR, cx = c.x * MR, cy = c.y * MR;
            float minx = Mathf.Min(ax, Mathf.Min(bx, cx)), maxx = Mathf.Max(ax, Mathf.Max(bx, cx));
            float miny = Mathf.Min(ay, Mathf.Min(by, cy)), maxy = Mathf.Max(ay, Mathf.Max(by, cy));
            if ((maxx - minx) * (maxy - miny) > MR * MR * 9f) return;
            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Mathf.Abs(den) < 1e-6f) return;
            int x0 = Mathf.FloorToInt(minx), x1 = Mathf.CeilToInt(maxx);
            int y0 = Mathf.FloorToInt(miny), y1 = Mathf.CeilToInt(maxy);
            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    float fx = px + 0.5f, fy = py + 0.5f;
                    float l1 = ((by - cy) * (fx - cx) + (cx - bx) * (fy - cy)) / den;
                    float l2 = ((cy - ay) * (fx - cx) + (ax - cx) * (fy - cy)) / den;
                    float l3 = 1f - l1 - l2;
                    if (l1 < -0.02f || l2 < -0.02f || l3 < -0.02f) continue;
                    float w = l1 * wa + l2 * wb + l3 * wc;
                    int ix = ((px % MR) + MR) % MR, iy = ((py % MR) + MR) % MR;
                    int idx = iy * MR + ix;
                    m[idx] += w; cnt[idx]++;
                }
            }
        }

        internal static float SampleMask(float[] m, float u, float v)
        {
            float fx = u * MR - 0.5f, fy = v * MR - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = ((x0 % MR) + MR) % MR; x1 = ((x1 % MR) + MR) % MR;
            y0 = ((y0 % MR) + MR) % MR; y1 = ((y1 % MR) + MR) % MR;
            float a = m[y0 * MR + x0] * (1 - tx) + m[y0 * MR + x1] * tx;
            float b = m[y1 * MR + x0] * (1 - tx) + m[y1 * MR + x1] * tx;
            return a * (1 - ty) + b * ty;
        }

        static readonly Dictionary<string, Texture2D> winBaked = new Dictionary<string, Texture2D>();
        static readonly Dictionary<int, Color32[]> origPixC = new Dictionary<int, Color32[]>();
        static readonly Dictionary<int, int> origWC = new Dictionary<int, int>();
        static readonly Dictionary<int, int> origHC = new Dictionary<int, int>();
        static readonly List<int> origOrder = new List<int>();

        static readonly Dictionary<int, bool> hasShinyC = new Dictionary<int, bool>();
        public static bool HasShiny(Texture orig)
        {
            if (orig == null) return false;
            int id = orig.GetInstanceID();
            bool v;
            if (hasShinyC.TryGetValue(id, out v)) return v;
            int w, h; Color32[] px = OrigPix(orig, out w, out h);
            v = false;
            if (px != null)
            {
                int need = Mathf.Max(24, px.Length / 900), cnt = 0;
                for (int i = 0; i < px.Length; i += 5)
                    if (px[i].a > 180 && ++cnt >= need) { v = true; break; }
            }
            hasShinyC[id] = v;
            return v;
        }

        static Color32[] OrigPix(Texture src, out int w, out int h)
        {
            w = 0; h = 0;
            if (src == null) return null;
            int id = src.GetInstanceID();
            Color32[] px;
            if (origPixC.TryGetValue(id, out px) && px != null) { w = origWC[id]; h = origHC[id]; return px; }

            int sw = src.width, sh = src.height;
            if (sw > 1024 || sh > 1024) { float sc = 1024f / Mathf.Max(sw, sh); sw = Mathf.Max(1, (int)(sw * sc)); sh = Mathf.Max(1, (int)(sh * sc)); }
            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
                Texture2D tmp = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tmp.Apply(false, false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                px = tmp.GetPixels32(); UnityEngine.Object.Destroy(tmp);
                w = sw; h = sh;
            }
            catch { return null; }
            origPixC[id] = px; origWC[id] = w; origHC[id] = h; origOrder.Add(id);
            while (origOrder.Count > 12)
            {
                int o = origOrder[0]; origOrder.RemoveAt(0);
                origPixC.Remove(o); origWC.Remove(o); origHC.Remove(o);
            }
            return px;
        }

        static Color32[] ReadTexWC(Texture src, out int w, out int h)
        {
            w = 0; h = 0;
            if (src == null) return null;
            Texture2D t2 = src as Texture2D;
            if (t2 != null && t2.isReadable) { w = t2.width; h = t2.height; return t2.GetPixels32(); }
            try
            {
                int mw = src.width, mh = src.height;
                RenderTexture rt = RenderTexture.GetTemporary(mw, mh, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D tmp = new Texture2D(mw, mh, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, mw, mh), 0, 0);
                tmp.Apply(false, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] px = tmp.GetPixels32();
                UnityEngine.Object.Destroy(tmp);
                w = mw; h = mh;
                return px;
            }
            catch { return null; }
        }

        static readonly Dictionary<int, byte[]> winMaskC = new Dictionary<int, byte[]>();
        static readonly Dictionary<int, int> winMaskWC = new Dictionary<int, int>();
        static readonly Dictionary<int, int> winMaskHC = new Dictionary<int, int>();

        static byte[] WindowMask(Texture orig, out int w, out int h)
        {
            w = 0; h = 0;
            if (orig == null) return null;
            int id = orig.GetInstanceID();
            byte[] mb;
            if (winMaskC.TryGetValue(id, out mb) && mb != null) { w = winMaskWC[id]; h = winMaskHC[id]; return mb; }
            int ow, oh; Color32[] op = OrigPix(orig, out ow, out oh);
            if (op == null) return null;
            const float low = 0.50f, high = 0.80f;
            float[] mk = new float[op.Length];
            for (int i = 0; i < op.Length; i++)
            {
                float t = (op[i].a / 255f - low) / (high - low); t = t < 0f ? 0f : (t > 1f ? 1f : t);
                mk[i] = t * t * (3f - 2f * t);
            }
            const int r = 3;
            float[] tmp = new float[mk.Length];
            for (int y = 0; y < oh; y++) { int row = y * ow; for (int x = 0; x < ow; x++) { float sum = 0f; int c = 0; for (int dx = -r; dx <= r; dx++) { int xx = x + dx; if (xx < 0 || xx >= ow) continue; sum += mk[row + xx]; c++; } tmp[row + x] = sum / c; } }
            for (int x = 0; x < ow; x++) { for (int y = 0; y < oh; y++) { float sum = 0f; int c = 0; for (int dy = -r; dy <= r; dy++) { int yy = y + dy; if (yy < 0 || yy >= oh) continue; sum += tmp[yy * ow + x]; c++; } mk[y * ow + x] = sum / c; } }
            mb = new byte[mk.Length];
            for (int i = 0; i < mk.Length; i++) mb[i] = (byte)(mk[i] * 255f);
            winMaskC[id] = mb; winMaskWC[id] = ow; winMaskHC[id] = oh; w = ow; h = oh;
            return mb;
        }

        public static Texture MaskWindows(Texture baked, Texture origAlbedo, bool applyColor, Color tint)
        {
            if (baked == null || origAlbedo == null) return baked;
            string key = "win|" + baked.GetInstanceID() + "|" + origAlbedo.GetInstanceID() + "|" + (applyColor ? 1 : 0)
                + "|" + tint.r.ToString("0.00") + tint.g.ToString("0.00") + tint.b.ToString("0.00");
            Texture2D cached;
            if (winBaked.TryGetValue(key, out cached)) { if (cached != null) return cached; winBaked.Remove(key); }
            int w, h; Color32[] bp = ReadTexWC(baked, out w, out h);
            if (bp == null) return baked;
            int ow, oh; Color32[] op = OrigPix(origAlbedo, out ow, out oh);
            if (op == null) return baked;
            int mkw, mkh; byte[] mk = WindowMask(origAlbedo, out mkw, out mkh);
            for (int y = 0; y < h; y++)
            {
                int oy = (oh == h) ? y : Mathf.Min(oh - 1, (int)((y + 0.5f) / h * oh));
                int row = y * w, orow = oy * ow;
                for (int x = 0; x < w; x++)
                {
                    int ox = (ow == w) ? x : Mathf.Min(ow - 1, (int)((x + 0.5f) / w * ow));
                    int oi = orow + ox;
                    Color32 o = op[oi];
                    float mask = (mk != null) ? mk[oi] / 255f : 0f;
                    Color32 b = bp[row + x];
                    float br = applyColor ? b.r * tint.r : b.r;
                    float bg = applyColor ? b.g * tint.g : b.g;
                    float bb = applyColor ? b.b * tint.b : b.b;
                    float fr = br + (o.r - br) * mask, fg = bg + (o.g - bg) * mask, fb = bb + (o.b - bb) * mask;
                    bp[row + x] = new Color32(
                        (byte)(fr < 0f ? 0f : (fr > 255f ? 255f : fr)),
                        (byte)(fg < 0f ? 0f : (fg > 255f ? 255f : fg)),
                        (byte)(fb < 0f ? 0f : (fb > 255f ? 255f : fb)), b.a);
                }
            }
            Texture2D outT = new Texture2D(w, h, TextureFormat.RGBA32, true);
            outT.SetPixels32(bp); outT.Apply(true, false);
            try { if (w % 4 == 0 && h % 4 == 0) outT.Compress(false); } catch { }
            winBaked[key] = outT;
            return outT;
        }

        public static Texture MaskWindowsBump(Texture bakedBump, Texture origBump, Texture maskAlbedo)
        {
            if (bakedBump == null || maskAlbedo == null) return bakedBump;
            string key = "winB|" + bakedBump.GetInstanceID() + "|" + ((origBump != null) ? origBump.GetInstanceID() : 0) + "|" + maskAlbedo.GetInstanceID();
            Texture2D cached;
            if (winBaked.TryGetValue(key, out cached)) { if (cached != null) return cached; winBaked.Remove(key); }
            int w, h; Color32[] bp = ReadTexWC(bakedBump, out w, out h);
            if (bp == null) return bakedBump;
            int obw = 0, obh = 0; Color32[] obp = (origBump != null) ? OrigPix(origBump, out obw, out obh) : null;
            int aw, ah; byte[] mk = WindowMask(maskAlbedo, out aw, out ah);
            if (mk == null) return bakedBump;
            Color32 flat = new Color32(128, 128, 255, 255);
            for (int y = 0; y < h; y++)
            {
                int ay = (ah == h) ? y : Mathf.Min(ah - 1, (int)((y + 0.5f) / h * ah));
                int oby = (obp != null && obh != h) ? Mathf.Min(obh - 1, (int)((y + 0.5f) / h * obh)) : y;
                int row = y * w, arow = ay * aw, obrow = oby * obw;
                for (int x = 0; x < w; x++)
                {
                    int ax = (aw == w) ? x : Mathf.Min(aw - 1, (int)((x + 0.5f) / w * aw));
                    float mask = mk[arow + ax] / 255f;
                    if (mask <= 0.004f) continue;
                    Color32 o = flat;
                    if (obp != null) { int obx = (obw == w) ? x : Mathf.Min(obw - 1, (int)((x + 0.5f) / w * obw)); o = obp[obrow + obx]; }
                    Color32 b = bp[row + x];
                    bp[row + x] = new Color32(
                        (byte)(b.r + (o.r - b.r) * mask),
                        (byte)(b.g + (o.g - b.g) * mask),
                        (byte)(b.b + (o.b - b.b) * mask),
                        (byte)(b.a + (o.a - b.a) * mask));
                }
            }
            Texture2D outT = new Texture2D(w, h, TextureFormat.RGBA32, true);
            outT.SetPixels32(bp); outT.Apply(true, false);
            try { if (w % 4 == 0 && h % 4 == 0) outT.Compress(false); } catch { }
            winBaked[key] = outT;
            return outT;
        }

        static void BilinTab(float[] uv, int n, int dim, out int[] i0, out int[] i1, out float[] frac)
        {
            i0 = new int[n]; i1 = new int[n]; frac = new float[n];
            for (int i = 0; i < n; i++)
            {
                float f = uv[i] * dim - 0.5f;
                int a = (int)Mathf.Floor(f);
                float fr = f - a;
                if (a < 0) { a = 0; fr = 0f; }
                if (a > dim - 1) a = dim - 1;
                int b = a + 1; if (b > dim - 1) b = dim - 1;
                i0[i] = a; i1[i] = b; frac[i] = fr;
            }
        }
        static readonly Dictionary<int, Texture2D> blitCopies = new Dictionary<int, Texture2D>();

        static int lastSweepCount;
        public static int BakedCount { get { return baked.Count; } }
        public static bool IsBaked(Texture t) { return t != null && bakedToOrig.ContainsKey(t.GetInstanceID()); }
        public static bool TryFadeMask(string key, out float[] m) { return masks.TryGetValue(key, out m); }
        public static void PutFadeMask(string key, float[] m) { masks[key] = m; }

        public static bool NeedSweep() { return baked.Count > 48 && baked.Count > lastSweepCount + 16; }

        public static void EvictExcept(HashSet<int> live)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Texture2D> kv in baked)
                if (kv.Value == null || !live.Contains(kv.Value.GetInstanceID())) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++)
            {
                string k = dead[i];
                Texture2D t; if (baked.TryGetValue(k, out t) && t != null) { bakedToOrig.Remove(t.GetInstanceID()); UnityEngine.Object.Destroy(t); }
                baked.Remove(k);
                Texture2D bt; if (bakedBump.TryGetValue(k, out bt)) { if (bt != null) { bumpToOrig.Remove(bt.GetInstanceID()); bumpSynth.Remove(bt.GetInstanceID()); UnityEngine.Object.Destroy(bt); } bakedBump.Remove(k); }
            }
            List<string> deadM = new List<string>();
            foreach (KeyValuePair<string, Texture2D> kv in metalMasks)
                if (kv.Value == null || !live.Contains(kv.Value.GetInstanceID())) deadM.Add(kv.Key);
            for (int i = 0; i < deadM.Count; i++)
            {
                string k = deadM[i]; Texture2D mt;
                if (metalMasks.TryGetValue(k, out mt) && mt != null) UnityEngine.Object.Destroy(mt);
                metalMasks.Remove(k);
            }
            lastSweepCount = baked.Count;
            if (Verbose && (dead.Count > 0 || deadM.Count > 0))
                Debug.Log(Tag + "cache sweep freed " + dead.Count + " bakes + " + deadM.Count + " shine masks (" + baked.Count + " kept)");
        }

        public static void ClearAllBakes()
        {
            foreach (KeyValuePair<string, Texture2D> kv in baked) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in bakedBump) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in metalMasks) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<int, Texture2D> kv in blitCopies) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            foreach (KeyValuePair<string, Texture2D> kv in winBaked) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            baked.Clear(); bakedBump.Clear(); metalMasks.Clear(); blitCopies.Clear(); winBaked.Clear();
            bakedToOrig.Clear(); bumpToOrig.Clear(); bumpSynth.Clear();
            lastBake.Clear(); lastOrder.Clear(); masks.Clear();
            origPixC.Clear(); origWC.Clear(); origHC.Clear(); origOrder.Clear();
            winMaskC.Clear(); winMaskWC.Clear(); winMaskHC.Clear();
            uvScaleCache.Clear();
            lastSweepCount = 0;

        }

        public static Texture BlitCopy(Texture orig)
        {
            if (orig == null) return null;
            Texture2D t;
            if (blitCopies.TryGetValue(orig.GetInstanceID(), out t) && t != null) return t;
            int scale = 1;
            while (Mathf.Max(orig.width, orig.height) * scale < 2048 && scale < 4) scale *= 2;
            int w = Mathf.Max(4, orig.width * scale), h = Mathf.Max(4, orig.height * scale);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(orig, rt);
            RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
            t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t.Apply(true, false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            blitCopies[orig.GetInstanceID()] = t;
            return t;
        }
        static ulong MaskHash(float[] m)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL ^ (uint)m.Length;
                for (int i = 0; i < m.Length; i++)
                {
                    uint bits = (uint)m[i].GetHashCode();
                    for (int b = 0; b < 4; b++)
                    {
                        h = (h ^ (byte)bits) * 1099511628211UL;
                        bits >>= 8;
                    }
                }
                return h;
            }
        }

        static float FoldPattern(float coordinate, bool flip)
        {
            int tile = Mathf.FloorToInt(coordinate);
            float fraction = coordinate - tile;
            if ((tile & 1) != 0) fraction = 1f - fraction;
            return flip ? 1f - fraction : fraction;
        }

        static Color32 SampleProjected(Color32[] pixels, int width, int height,
            float u, float v, int entry)
        {
            float fu = FoldPattern(u + entry * 0.41f, (entry & 1) != 0);
            float fv = FoldPattern(v + entry * 0.29f, (entry & 2) != 0);
            int x = Mathf.Clamp(Mathf.RoundToInt(fu * (width - 1)), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(fv * (height - 1)), 0, height - 1);
            return pixels[y * width + x];
        }

        static Vector2 ProjectedCoordinate(BakeProjection projection, Vector3 position, int axis,
            float spanU, float spanV)
        {
            if (projection != null && projection.HasBasis)
                return new Vector2(Vector3.Dot(position, projection.BasisU) / spanU,
                    Vector3.Dot(position, projection.BasisV) / spanV);
            if (axis == 1) return new Vector2(position.x / spanU, position.z / spanV);
            if (axis == 0) return new Vector2(position.z / spanU, position.y / spanV);
            return new Vector2(position.x / spanU, position.y / spanV);
        }

        static bool ProjectionData(BakeProjection projection, out IList<Vector3> vertices,
            out IList<Vector2> uvs, out IList<int> triangles)
        {
            vertices = projection != null ? projection.Vertices : null;
            uvs = projection != null ? projection.Uvs : null;
            triangles = projection != null ? projection.Triangles : null;
            try
            {
                if (projection == null || !projection.Usable) return false;
                if (vertices == null) vertices = projection.Mesh.vertices;
                if (uvs == null) uvs = projection.Mesh.uv;
                if (triangles == null)
                {
                    int submesh = Mathf.Clamp(projection.Submesh, 0, projection.Mesh.subMeshCount - 1);
                    triangles = projection.Mesh.GetTriangles(submesh);
                }
                return vertices != null && uvs != null && triangles != null
                    && vertices.Count > 0 && uvs.Count == vertices.Count && triangles.Count >= 3;
            }
            catch { return false; }
        }

        static bool RasterProjectedTriangle(BakeProjection projection, IList<Vector3> vertices,
            IList<Vector2> uvs, int ia, int ib, int ic, Vector2 a, Vector2 b, Vector2 c,
            int width, int height, Color32[] main, Color32[] bump, Color32[] overlay,
            int overlayWidth, int overlayHeight, Color32[] overlayNormal, int normalWidth,
            int normalHeight, byte[] weights, byte[] touched, float strength, float relief,
            float spanU, float spanV, int entry, bool emboss)
        {
            float denominator = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denominator) <= 1e-10f) return false;
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * width), 0, width - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * width) - 1, 0, width - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * height), 0, height - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * height) - 1, 0, height - 1);
            if (maxX < minX || maxY < minY) return false;

            Vector3 pa = projection.ToPart.MultiplyPoint3x4(vertices[ia]);
            Vector3 pb = projection.ToPart.MultiplyPoint3x4(vertices[ib]);
            Vector3 pc = projection.ToPart.MultiplyPoint3x4(vertices[ic]);
            Vector3 faceNormal = Vector3.Cross(pb - pa, pc - pa).normalized;
            float nx = Mathf.Abs(faceNormal.x), ny = Mathf.Abs(faceNormal.y), nz = Mathf.Abs(faceNormal.z);
            int axis = ny >= nx && ny >= nz ? 1 : (nx >= nz ? 0 : 2);
            float inv = 1f / denominator;
            float s255 = strength / 255f;
            const float inv255 = 1f / 255f;
            bool wrote = false;
            for (int y = minY; y <= maxY; y++)
            {
                float py = (y + 0.5f) / height;
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    int index = row + x;
                    if (touched[index] != 0) continue;
                    float px = (x + 0.5f) / width;
                    float wa = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) * inv;
                    float wb = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) * inv;
                    float wc = 1f - wa - wb;
                    if (wa < -0.0001f || wb < -0.0001f || wc < -0.0001f) continue;
                    Vector3 position = pa * wa + pb * wb + pc * wc;
                    Vector2 pattern = ProjectedCoordinate(projection, position, axis, spanU, spanV);
                    Color32 over = SampleProjected(overlay, overlayWidth, overlayHeight,
                        pattern.x, pattern.y, entry);
                    if (over.a == 0 && overlayNormal == null) continue;
                    float alpha = over.a * s255;
                    if (alpha > 0.95f) alpha = 0.95f;
                    if (weights != null) alpha *= weights[index] * inv255;
                    if (alpha <= 0.004f) continue;
                    touched[index] = 1;
                    if (overlayNormal != null && bump != null)
                    {
                        Color32 on = SampleProjected(overlayNormal, normalWidth, normalHeight,
                            pattern.x, pattern.y, entry);
                        Color32 nb = bump[index];
                        float normalAlpha = alpha * relief;
                        bump[index] = new Color32(
                            (byte)(nb.r + (on.r - nb.r) * normalAlpha),
                            (byte)(nb.g + (on.g - nb.g) * normalAlpha),
                            (byte)(nb.b + (on.b - nb.b) * normalAlpha),
                            (byte)(nb.a + (on.a - nb.a) * normalAlpha));
                    }
                    Color32 original = main[index];
                    if (emboss)
                    {
                        int nr = original.r + (int)((original.r * over.r / 128 - original.r) * alpha);
                        int ng = original.g + (int)((original.g * over.g / 128 - original.g) * alpha);
                        int nb = original.b + (int)((original.b * over.b / 128 - original.b) * alpha);
                        main[index] = new Color32(
                            (byte)Mathf.Clamp(nr, 0, 255), (byte)Mathf.Clamp(ng, 0, 255),
                            (byte)Mathf.Clamp(nb, 0, 255), original.a);
                    }
                    else
                    {
                        main[index] = new Color32(
                            (byte)(original.r + (over.r - original.r) * alpha),
                            (byte)(original.g + (over.g - original.g) * alpha),
                            (byte)(original.b + (over.b - original.b) * alpha), original.a);
                    }
                    wrote = true;
                }
            }
            return wrote;
        }

        static bool ApplyProjectedLayer(BakeProjection projection, int width, int height,
            Color32[] main, Color32[] bump, Color32[] overlay, int overlayWidth, int overlayHeight,
            Color32[] overlayNormal, int normalWidth, int normalHeight, byte[] weights,
            float strength, float relief, float spanU, float spanV, int entry, bool emboss,
            byte[] touched)
        {
            IList<Vector3> vertices; IList<Vector2> uvs; IList<int> triangles;
            if (!ProjectionData(projection, out vertices, out uvs, out triangles)) return false;
            if (touched == null || touched.Length != width * height) return false;
            bool wrote = false;
            bool repeat = projection.WrapMode != TextureWrapMode.Clamp;
            Vector2 scale = projection.TextureScale;
            Vector2 offset = projection.TextureOffset;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Count || ib >= vertices.Count
                    || ic >= vertices.Count) continue;
                Vector2 a = Vector2.Scale(uvs[ia], scale) + offset;
                Vector2 b = Vector2.Scale(uvs[ib], scale) + offset;
                Vector2 c = Vector2.Scale(uvs[ic], scale) + offset;
                bool outsideUnit = a.x < 0f || a.x > 1f || a.y < 0f || a.y > 1f
                    || b.x < 0f || b.x > 1f || b.y < 0f || b.y > 1f
                    || c.x < 0f || c.x > 1f || c.y < 0f || c.y > 1f;
                if (repeat && outsideUnit)
                {
                    int shiftMinX = Mathf.CeilToInt(-Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                    int shiftMaxX = Mathf.FloorToInt(1f - Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
                    int shiftMinY = Mathf.CeilToInt(-Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
                    int shiftMaxY = Mathf.FloorToInt(1f - Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
                    if (shiftMaxX - shiftMinX > 16 || shiftMaxY - shiftMinY > 16) continue;
                    for (int sy = shiftMinY; sy <= shiftMaxY; sy++)
                        for (int sx = shiftMinX; sx <= shiftMaxX; sx++)
                        {
                            Vector2 shift = new Vector2(sx, sy);
                            wrote |= RasterProjectedTriangle(projection, vertices, uvs, ia, ib, ic,
                                a + shift, b + shift, c + shift, width, height, main, bump, overlay,
                                overlayWidth, overlayHeight, overlayNormal, normalWidth, normalHeight,
                                weights, touched, strength, relief, spanU, spanV, entry, emboss);
                        }
                }
                else if (!repeat)
                {
                    a = new Vector2(Mathf.Clamp01(a.x), Mathf.Clamp01(a.y));
                    b = new Vector2(Mathf.Clamp01(b.x), Mathf.Clamp01(b.y));
                    c = new Vector2(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y));
                    wrote |= RasterProjectedTriangle(projection, vertices, uvs, ia, ib, ic, a, b, c,
                        width, height, main, bump, overlay, overlayWidth, overlayHeight, overlayNormal,
                        normalWidth, normalHeight, weights, touched, strength, relief, spanU, spanV,
                        entry, emboss);
                }
                else
                {
                    wrote |= RasterProjectedTriangle(projection, vertices, uvs, ia, ib, ic, a, b, c,
                        width, height, main, bump, overlay, overlayWidth, overlayHeight, overlayNormal,
                        normalWidth, normalHeight, weights, touched, strength, relief, spanU, spanV,
                        entry, emboss);
                }
            }
            return wrote;
        }

        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut)
        { return Baked(orig, origBump, stackSpec, part, patScale, out bumpOut, null, null); }
        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut, float[] maskOverride)
        { return Baked(orig, origBump, stackSpec, part, patScale, out bumpOut, maskOverride, null); }
        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut, BakeProjection projection)
        { return Baked(orig, origBump, stackSpec, part, patScale, out bumpOut, null, projection); }
        public static Texture Baked(Texture orig, Texture origBump, string stackSpec, Part part, float patScale, out Texture bumpOut, float[] maskOverride, BakeProjection projection)
        {
            bumpOut = null;
            if (patScale <= 0.01f || float.IsNaN(patScale) || float.IsInfinity(patScale)) patScale = 1f;
            if (orig == null) return null;
            if (string.IsNullOrEmpty(stackSpec)) return orig;
            string pname = (part != null) ? ((part.partInfo != null) ? part.partInfo.name : part.name) : "?";
            string inputKey = "|n" + (origBump == null ? 0 : origBump.GetInstanceID())
                + "|s" + patScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string key = orig.GetInstanceID() + "|" + stackSpec + "|" + pname + inputKey;
            if (part != null && HasPlacedLayer(stackSpec)) key += "|g" + GeometrySignature(part);
            if (part != null && HasWorldScaledLayer(stackSpec)
                && (projection == null || !projection.Usable))
                key += "|u" + GeometrySignature(part) + "_" + MaterialUvSignature(part, Original(orig));
            if (projection != null && projection.Usable) key += "|p" + projection.Signature
                + (part != null ? "_g" + GeometrySignature(part) : "");
            if (maskOverride != null) key += "|k" + MaskHash(maskOverride);
            Texture2D t;
            if (baked.TryGetValue(key, out t))
            {
                if (t != null)
                {
                    Texture2D btc;
                    bool haveBump = bakedBump.TryGetValue(key, out btc);

                    if (!haveBump || ReferenceEquals(btc, null)) return t;
                    if (btc != null) { bumpOut = btc; return t; }
                }

                baked.Remove(key); bakedBump.Remove(key);
                lastBake.Remove(orig.GetInstanceID() + "|" + pname);
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int scale = 1;
                while (Mathf.Max(orig.width, orig.height) * scale < 2048 && scale < 4) scale *= 2;
                int w = Mathf.Max(4, orig.width * scale), h = Mathf.Max(4, orig.height * scale);

                string[] entries = stackSpec.Split(';');

                bool wantBump = false;
                for (int e2 = 0; e2 < entries.Length; e2++)
                {
                    string[] f2 = entries[e2].Split(':');
                    if (f2.Length < 3 || f2[0].StartsWith("tint#")) continue;
                    Color32[] tst; int tw, th;
                    if (GetOverlayPixels(f2[0] + "_n", out tst, out tw, out th)) { wantBump = true; break; }
                }

                string memKey = orig.GetInstanceID() + "|" + pname + inputKey;
                if (maskOverride != null) memKey += "|k" + MaskHash(maskOverride);
                if (part != null && HasPlacedLayer(stackSpec))
                    memKey += "|g" + GeometrySignature(part);
                if (part != null && HasWorldScaledLayer(stackSpec)
                    && (projection == null || !projection.Usable))
                    memKey += "|u" + GeometrySignature(part) + "_" + MaterialUvSignature(part, Original(orig));
                if (projection != null && projection.Usable) memKey += "|p" + projection.Signature
                    + (part != null ? "_g" + GeometrySignature(part) : "");
                Color32[] bp = null, nbase = null;
                int firstEntry = 0;
                BakeMem bm;
                if (lastBake.TryGetValue(memKey, out bm) && bm.w == w && bm.h == h
                    && stackSpec.StartsWith(bm.spec)
                    && (stackSpec.Length == bm.spec.Length || stackSpec[bm.spec.Length] == ';'))
                {
                    bp = (Color32[])bm.main.Clone();
                    if (wantBump && bm.bump != null) nbase = (Color32[])bm.bump.Clone();
                    firstEntry = bm.spec.Split(';').Length;
                }

                if (bp == null)
                {
                    RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(orig, rt);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    Texture2D baseT = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    baseT.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    baseT.Apply(false, false);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    bp = baseT.GetPixels32();
                    UnityEngine.Object.Destroy(baseT);
                    firstEntry = 0;
                }
                if (wantBump && nbase == null)
                {
                    nbase = new Color32[w * h];
                    if (origBump != null)
                    {
                        RenderTexture rb = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                        Graphics.Blit(origBump, rb);
                        RenderTexture pv = RenderTexture.active;
                        RenderTexture.active = rb;
                        Texture2D bT = new Texture2D(w, h, TextureFormat.RGBA32, false);
                        bT.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        bT.Apply(false, false);
                        RenderTexture.active = pv;
                        RenderTexture.ReleaseTemporary(rb);
                        nbase = bT.GetPixels32();
                        UnityEngine.Object.Destroy(bT);
                    }
                    else
                    {
                        Color32 flat = new Color32(128, 128, 255, 128);
                        for (int i2 = 0; i2 < nbase.Length; i2++) nbase[i2] = flat;
                    }
                }

                float dim = PartMaxDimension(part);
                Vector2 worldPerUv = projection != null && projection.Usable
                    ? new Vector2(-1f, -1f) : UvWorldScale(part, orig);
                float tileBasisU = worldPerUv.x > 0f ? worldPerUv.x : dim;
                float tileBasisV = worldPerUv.y > 0f ? worldPerUv.y : dim;
                int hsh = pname.GetHashCode();
                float partU = ((hsh >> 2) & 0x3FF) / 1024f;
                float partV = ((hsh >> 12) & 0x3FF) / 1024f;
                bool flipX = ((hsh >> 22) & 1) == 1;
                bool flipY = ((hsh >> 23) & 1) == 1;
                float[] colF = new float[w];
                float[] rowF = new float[h];
                byte[] wgtBuf = null;
                byte[] projectedTouched = null;

                for (int e = firstEntry; e < entries.Length; e++)
                {
                    string[] f = entries[e].Split(':');
                    if (f.Length < 3) continue;
                    string name = f[0], place = f[1];
                    int lvl; if (!int.TryParse(f[2], out lvl)) lvl = 1;
                    bool isTint = name.StartsWith("tint#");

                    if (!isTint && ((hsh >> 24) & 1) == 1)
                    {
                        Color32[] tvb; int tvw, tvh;
                        if (GetOverlayPixels(name + "_b", out tvb, out tvw, out tvh)) name = name + "_b";
                    }
                    Color32 tintC = new Color32(255, 255, 255, 255);
                    Color32[] ov = null; int ow = 0, oh = 0;
                    if (isTint) tintC = ParseHex(name.Substring(5));
                    else if (!GetOverlayPixels(name, out ov, out ow, out oh)) continue;
                    Color32[] ovn = null; int ownw = 0, ownh = 0;
                    if (!isTint && nbase != null) GetOverlayPixels(name + "_n", out ovn, out ownw, out ownh);
                    bool isEmboss = name.StartsWith("grain");
                    float[] mask = (place == "whole") ? null : (maskOverride != null ? maskOverride : GetMask(part, place));
                    bool uvFB = (mask == null && place != "whole");
                    float strengthMul = StackStrength(lvl);
                    float strength = strengthMul * PatternStrength(name);
                    int tilesU = 1, tilesV = 1;
                    float projectedSpanU = TileMeters(name) * patScale;
                    float projectedSpanV = projectedSpanU * (ow > 0 ? oh / (float)ow : 1f);
                    if (WorldScaled(name))
                    {
                        tilesU = Mathf.Clamp(Mathf.RoundToInt(tileBasisU / Mathf.Max(0.05f, projectedSpanU)), 1, 24);
                        tilesV = Mathf.Clamp(Mathf.RoundToInt(tileBasisV / Mathf.Max(0.05f, projectedSpanV)), 1, 24);
                        while (tilesU > 1 && w / tilesU < 160) tilesU--;
                        while (tilesV > 1 && h / tilesV < 160) tilesV--;
                    }

                    bool eFlipX = flipX ^ ((e & 1) == 1);
                    bool eFlipY = flipY ^ ((e & 2) == 2);
                    float uShift = partU * 3f + e * 0.41f;
                    for (int x = 0; x < w; x++)
                    {
                        float su = (x + 0.5f) / w * tilesU + uShift;
                        int txi = Mathf.FloorToInt(su);
                        float fu = su - txi;
                        if ((txi & 1) == 1) fu = 1f - fu;
                        if (eFlipX) fu = 1f - fu;
                        colF[x] = fu;
                    }
                    for (int y = 0; y < h; y++)
                    {
                        float sv = (y + 0.5f) / h * tilesV + partV * 3f + e * 0.29f;
                        int tyi = Mathf.FloorToInt(sv);
                        float fv = sv - tyi;
                        if ((tyi & 1) == 1) fv = 1f - fv;
                        if (eFlipY) fv = 1f - fv;
                        rowF[y] = fv;
                    }

                    int[] ax0 = null, ax1 = null, ay0 = null, ay1 = null;
                    int[] bx0 = null, bx1 = null, by0 = null, by1 = null;
                    float[] axf = null, ayf = null, bxf = null, byf = null;
                    if (!isTint)
                    {
                        BilinTab(colF, w, ow, out ax0, out ax1, out axf);
                        BilinTab(rowF, h, oh, out ay0, out ay1, out ayf);
                        if (ovn != null)
                        {
                            BilinTab(colF, w, ownw, out bx0, out bx1, out bxf);
                            BilinTab(rowF, h, ownh, out by0, out by1, out byf);
                        }
                    }

                    byte[] wgt = null;
                    if (mask != null || uvFB)
                    {
                        if (wgtBuf == null) wgtBuf = new byte[w * h];
                        wgt = wgtBuf;
                        ForRows(h, w * h >= ParMinPixels, delegate(int y)
                        {
                            float v01 = (y + 0.5f) / h;
                            int off = y * w;
                            if (uvFB)
                            {
                                for (int x = 0; x < w; x++)
                                    wgt[off + x] = (byte)(RegionWeightUV(place, (x + 0.5f) / w, v01) * 255f);
                            }
                            else
                            {
                                for (int x = 0; x < w; x++)
                                    wgt[off + x] = (byte)(SampleMask(mask, (x + 0.5f) / w, v01) * 255f);
                            }
                        });
                    }

                    float s255 = strength / 255f;
                    const float inv255 = 1f / 255f;
                    string rbn = name.EndsWith("_b") ? name.Substring(0, name.Length - 2) : name;
                    float reliefMul;
                    if (name.StartsWith("heatd")) reliefMul = 0.14f;
                    else if (name.StartsWith("tiles")) reliefMul = 0.85f;
                    else if (DeepRelief.Contains(rbn)) reliefMul = 0.74f;
                    else reliefMul = 0.52f;
                    if (!isTint && WorldScaled(name) && projection != null && projection.Usable)
                    {
                        if (projectedTouched == null) projectedTouched = new byte[w * h];
                        else Array.Clear(projectedTouched, 0, projectedTouched.Length);
                        if (ApplyProjectedLayer(projection, w, h, bp, nbase, ov, ow, oh, ovn,
                            ownw, ownh, wgt, strength, reliefMul,
                            Mathf.Max(0.05f, projectedSpanU), Mathf.Max(0.05f, projectedSpanV), e,
                            isEmboss, projectedTouched))
                            continue;
                    }
                    ForRows(h, w * h >= ParMinPixels, delegate(int y)
                    {
                        int rowOff = y * w;
                        int ayO0 = !isTint ? ay0[y] * ow : 0, ayO1 = !isTint ? ay1[y] * ow : 0;
                        int byO0 = (ovn != null) ? by0[y] * ownw : 0, byO1 = (ovn != null) ? by1[y] * ownw : 0;
                        float ayfy = !isTint ? ayf[y] : 0f, byfy = (ovn != null) ? byf[y] : 0f;
                        for (int x = 0; x < w; x++)
                        {
                            int idx = rowOff + x;
                            float a;
                            Color32 o;
                            if (isTint) { o = tintC; a = 0.85f * strength; }
                            else
                            {
                                o = ov[(ayfy >= 0.5f ? ayO1 : ayO0) + (axf[x] >= 0.5f ? ax1[x] : ax0[x])];
                                if (o.a == 0 && ovn == null) continue;
                                a = o.a * s255;
                            }
                            if (a > 0.95f) a = 0.95f;
                            if (wgt != null) a *= wgt[idx] * inv255;
                            if (a <= 0.004f) continue;
                            if (ovn != null)
                            {
                                Color32 on = ovn[(byfy >= 0.5f ? byO1 : byO0) + (bxf[x] >= 0.5f ? bx1[x] : bx0[x])];
                                Color32 nB = nbase[idx];
                                float na = a * reliefMul;
                                nbase[idx] = new Color32(
                                    (byte)(nB.r + (on.r - nB.r) * na),
                                    (byte)(nB.g + (on.g - nB.g) * na),
                                    (byte)(nB.b + (on.b - nB.b) * na),
                                    (byte)(nB.a + (on.a - nB.a) * na));
                            }
                            Color32 b = bp[idx];
                            if (isEmboss)
                            {
                                int nr = b.r + (int)((b.r * o.r / 128 - b.r) * a);
                                int ng = b.g + (int)((b.g * o.g / 128 - b.g) * a);
                                int nb2 = b.b + (int)((b.b * o.b / 128 - b.b) * a);
                                bp[idx] = new Color32(
                                    (byte)(nr < 0 ? 0 : (nr > 255 ? 255 : nr)),
                                    (byte)(ng < 0 ? 0 : (ng > 255 ? 255 : ng)),
                                    (byte)(nb2 < 0 ? 0 : (nb2 > 255 ? 255 : nb2)),
                                    b.a);
                            }
                            else if (isTint)
                                bp[idx] = new Color32(
                                    (byte)(b.r + (b.r * o.r / 255 - b.r) * a),
                                    (byte)(b.g + (b.g * o.g / 255 - b.g) * a),
                                    (byte)(b.b + (b.b * o.b / 255 - b.b) * a),
                                    b.a);
                            else
                                bp[idx] = new Color32(
                                    (byte)(b.r + (o.r - b.r) * a),
                                    (byte)(b.g + (o.g - b.g) * a),
                                    (byte)(b.b + (o.b - b.b) * a),
                                    b.a);
                        }
                    });
                }

                t = new Texture2D(w, h, TextureFormat.RGBA32, true);
                t.SetPixels32(bp);
                t.Apply(true, false);
                t.name = orig.name + "_RDX_weathered";
                try { if (w % 4 == 0 && h % 4 == 0) t.Compress(false); } catch { }
                baked[key] = t;
                bakedToOrig[t.GetInstanceID()] = orig;
                if (nbase != null)
                {
                    Texture2D bt = new Texture2D(w, h, TextureFormat.RGBA32, true);
                    bt.SetPixels32(nbase);
                    bt.Apply(true, false);
                    bt.name = orig.name + "_RDX_weathered_n";
                    try { if (w % 4 == 0 && h % 4 == 0) bt.Compress(false); } catch { }
                    bakedBump[key] = bt;
                    if (origBump != null) bumpToOrig[bt.GetInstanceID()] = origBump;
                    else bumpSynth.Add(bt.GetInstanceID());
                    bumpOut = bt;
                }
                else bakedBump[key] = null;

                if (!lastBake.ContainsKey(memKey)) lastOrder.Add(memKey);
                BakeMem nm = new BakeMem();
                nm.spec = stackSpec; nm.main = bp; nm.bump = nbase; nm.w = w; nm.h = h;
                lastBake[memKey] = nm;
                while (lastOrder.Count > BakeMemCap)
                {
                    lastBake.Remove(lastOrder[0]);
                    lastOrder.RemoveAt(0);
                }
                if (Verbose)
                    Debug.Log(Tag + "baked " + orig.name + " " + w + "x" + h + " for " + pname
                        + " [" + stackSpec + "] in " + sw.ElapsedMilliseconds + " ms"
                        + (firstEntry > 0 ? " (incremental)" : ""));
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "weathering bake failed on " + orig.name + ": " + e.Message);
                return orig;
            }
            return t;
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class HeatTileReforgedCacheJanitor : MonoBehaviour
    {
        void Awake() { DontDestroyOnLoad(gameObject); GameEvents.onGameSceneLoadRequested.Add(OnScene); }
        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnScene); }
        void OnScene(GameScenes s)
        {
            ModuleHeatTileReforgedCore.ClearLoadQueue();
            if (s != GameScenes.EDITOR && s != GameScenes.FLIGHT) WeatherCache.ClearAllBakes();
        }

        void Update()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            do { if (!ModuleHeatTileReforgedCore.PumpLoadQueue()) break; } while (sw.ElapsedMilliseconds < 8);
        }
    }
}
