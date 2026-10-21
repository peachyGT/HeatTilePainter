using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeatTileOverlayPrototype
{
    // Opt-in model profiles, shared by preview and persistent geometry. Unknown deployables
    // are left original. Reference geometry is the prefab, never the live deployment pose.
    internal static class ExteriorPaintPolicy
    {
        sealed class Profile
        {
            internal string Model;
            internal HashSet<string> Renderers = new HashSet<string>(StringComparer.Ordinal);
            internal Vector3? InteriorPoint;
        }
        struct Triangle { internal Vector3 A, B, C; }
        static List<Profile> profiles;
        static readonly Dictionary<string, bool[]> eligibility = new Dictionary<string, bool[]>();
        static readonly HashSet<string> warned = new HashSet<string>();

        internal static bool IsDeployable(Part part)
        {
            if (part == null) return false;
            bool dock = false, animation = false;
            foreach (PartModule m in part.Modules)
            {
                if (m == null) continue;
                string name = m.moduleName ?? "";
                if (name == "ModuleWheelDeployment" || name == "ModuleDeployableSolarPanel") return true;
                if (name == "ModuleDockingNode") dock = true;
                if (name == "ModuleAnimateGeneric") animation = true;
            }
            return dock && animation;
        }

        static Profile FindProfile(Part part, Renderer renderer)
        {
            if (profiles == null)
            {
                profiles = new List<Profile>();
                if (GameDatabase.Instance != null)
                    foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("HEAT_TILE_EXTERIOR"))
                    {
                        string model = node.GetValue("model");
                        if (string.IsNullOrEmpty(model)) continue;
                        Profile p = new Profile(); p.Model = model;
                        if (node.HasValue("interiorPoint"))
                        {
                            Vector3 point;
                            try { point = ConfigNode.ParseVector3(node.GetValue("interiorPoint")); }
                            catch (Exception) { Debug.LogWarning("[HeatTilePainter] Invalid exterior interiorPoint for " + model); continue; }
                            if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude)) continue;
                            p.InteriorPoint = point;
                        }
                        foreach (string name in node.GetValues("renderer")) p.Renderers.Add(name);
                        profiles.Add(p);
                    }
            }
            string path = ReforgedCore.ModuleHeatTileReforgedCore.RelPath(renderer.transform, part.transform);
            for (int i = profiles.Count - 1; i >= 0; i--)
                if (path.IndexOf(profiles[i].Model, StringComparison.Ordinal) >= 0) return profiles[i];
            return null;
        }

        internal static bool AllowsRenderer(Part part, Renderer renderer)
        {
            if (!IsDeployable(part)) return true;
            Profile profile = FindProfile(part, renderer);
            if (profile != null) return profile.Renderers.Contains(renderer.name) && renderer is MeshRenderer;
            string name = part.partInfo == null ? part.name : part.partInfo.name;
            if (warned.Add(name)) Debug.LogWarning("[HeatTilePainter] No exterior profile for deployable "
                + name + "; unsupported surfaces remain unpainted.");
            return false;
        }

        internal static bool[] Triangles(Part part, Renderer renderer, Mesh mesh, int submesh)
        {
            if (!IsDeployable(part)) return null;
            int[] indices = mesh.GetTriangles(submesh);
            bool[] denied = new bool[indices.Length / 3];
            if (!AllowsRenderer(part, renderer) || part.partInfo == null || part.partInfo.partPrefab == null) return denied;
            Part prefab = part.partInfo.partPrefab;
            string path = ReforgedCore.ModuleHeatTileReforgedCore.RelPath(renderer.transform, part.transform);
            Transform reference = prefab.transform.Find(path);
            // KSP model object names contain literal '/' characters. Transform.Find treats
            // them as separators, so match the enumerated relative path when lookup fails.
            if (reference == null)
                foreach (MeshRenderer candidate in prefab.GetComponentsInChildren<MeshRenderer>(true))
                    if (ReforgedCore.ModuleHeatTileReforgedCore.RelPath(candidate.transform, prefab.transform) == path)
                    { reference = candidate.transform; break; }
            MeshFilter filter = reference == null ? null : reference.GetComponent<MeshFilter>();
            Mesh source = filter == null ? null : filter.sharedMesh;
            if (source == null || !source.isReadable || source.vertexCount != mesh.vertexCount || submesh >= source.subMeshCount) return denied;
            int[] referenceIndices = source.GetTriangles(submesh);
            if (referenceIndices.Length != indices.Length) return denied;
            for (int i = 0; i < indices.Length; i++) if (indices[i] != referenceIndices[i]) return denied;
            string key = prefab.GetInstanceID() + "|" + source.GetInstanceID() + "|" + path + "|" + submesh;
            bool[] cached;
            if (eligibility.TryGetValue(key, out cached)) return cached;

            Profile profile = FindProfile(part, renderer);
            List<Triangle> shell = new List<Triangle>();
            foreach (MeshRenderer r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!profile.Renderers.Contains(r.name) || FindProfile(prefab, r) != profile) continue;
                MeshFilter f = r.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null || !f.sharedMesh.isReadable) continue;
                Vector3[] vertices = f.sharedMesh.vertices;
                Matrix4x4 matrix = ToPart(r.transform, prefab.transform);
                int[] ts = f.sharedMesh.triangles;
                for (int j=0; j+2<ts.Length; j+=3)
                    shell.Add(new Triangle { A=matrix.MultiplyPoint3x4(vertices[ts[j]]), B=matrix.MultiplyPoint3x4(vertices[ts[j+1]]), C=matrix.MultiplyPoint3x4(vertices[ts[j+2]]) });
                if (shell.Count > 10000) return denied;
            }
            Vector3[] local = source.vertices;
            Matrix4x4 toPart = ToPart(reference, prefab.transform);
            int accepted = 0;
            for (int i=0; i+2<indices.Length; i+=3)
            {
                Vector3 a=toPart.MultiplyPoint3x4(local[indices[i]]), b=toPart.MultiplyPoint3x4(local[indices[i+1]]), c=toPart.MultiplyPoint3x4(local[indices[i+2]]);
                Vector3 normal=Vector3.Cross(b-a,c-a).normalized;
                if (normal.sqrMagnitude < .5f) continue;
                // Some stock covers surround an open docking aperture: an inward ray can
                // escape through that aperture. Validated convex profiles supply a point
                // inside the closed cover to reject those inward faces before ray testing.
                if (profile.InteriorPoint.HasValue && Vector3.Dot(normal,
                    (a+b+c)/3-profile.InteriorPoint.Value) <= .000001f) continue;
                // Centroid plus near-vertex samples reject cavity-facing faces without camera
                // heuristics. A closed shell blocks inward rays, including shared-UV interiors.
                bool exterior = Visible((a+b+c)/3, normal, shell)
                    && Visible(a*.6f+b*.2f+c*.2f, normal, shell)
                    && Visible(b*.6f+a*.2f+c*.2f, normal, shell)
                    && Visible(c*.6f+a*.2f+b*.2f, normal, shell);
                denied[i/3]=exterior; if (exterior) accepted++;
            }
            eligibility[key]=denied;
            Debug.Log("[HeatTilePainter] Exterior profile " + renderer.name + ": " + accepted + "/" + denied.Length + " triangles.");
            return denied;
        }

        static Matrix4x4 ToPart(Transform transform, Transform root)
        {
            Matrix4x4 result = Matrix4x4.identity;
            while (transform != null && transform != root)
            { result = Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale) * result; transform = transform.parent; }
            return result;
        }

        static bool Visible(Vector3 point, Vector3 direction, List<Triangle> shell)
        {
            Vector3 origin=point+direction*.0001f;
            foreach (Triangle t in shell)
            {
                Vector3 e1=t.B-t.A, e2=t.C-t.A, p=Vector3.Cross(direction,e2);
                float det=Vector3.Dot(e1,p); if (Mathf.Abs(det)<1e-8f) continue;
                float inverse=1f/det; Vector3 s=origin-t.A;
                float u=Vector3.Dot(s,p)*inverse; if(u<0 || u>1) continue;
                Vector3 q=Vector3.Cross(s,e1); float v=Vector3.Dot(direction,q)*inverse;
                if(v<0 || u+v>1) continue;
                if(Vector3.Dot(e2,q)*inverse > .0002f) return false;
            }
            return true;
        }
    }
}
