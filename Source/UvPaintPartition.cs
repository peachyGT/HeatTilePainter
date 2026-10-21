using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeatTileOverlayPrototype
{
    // A projected bake cannot store two different physical positions in one atlas texel.
    // Reuse the existing material partition to separate overlapping atlas triangles.
    internal static class UvPaintPartition
    {
        struct Face
        {
            internal int Offset;
            internal Vector2 A, B, C, Min, Max;
            internal float Area;
        }
        internal static List<List<int>> Split(IList<Vector2> uv, List<int> triangles,
            Vector2 scale, Vector2 offset)
        {
            List<List<int>> fallback = new List<List<int>> { triangles };
            if (uv == null || triangles.Count > 12000) return fallback;
            List<Face> faces = new List<Face>();
            for (int i=0;i+2<triangles.Count;i+=3)
            {
                Vector2 a=Vector2.Scale(uv[triangles[i]],scale)+offset;
                Vector2 b=Vector2.Scale(uv[triangles[i+1]],scale)+offset;
                Vector2 c=Vector2.Scale(uv[triangles[i+2]],scale)+offset;
                Vector2 min=Vector2.Min(a,Vector2.Min(b,c)), max=Vector2.Max(a,Vector2.Max(b,c));
                // Repeating UVs need tessellation/remapping, not extra material slots.
                if(min.x < -.00001f || min.y < -.00001f || max.x > 1.00001f || max.y > 1.00001f) return fallback;
                faces.Add(new Face { Offset=i,A=a,B=b,C=c,Min=min,Max=max,
                    Area=Mathf.Abs(Cross(b-a,c-a)) });
            }
            // Place large faces first to avoid fragmenting an otherwise continuous atlas.
            faces.Sort(delegate(Face a,Face b) { int cmp=b.Area.CompareTo(a.Area);return cmp!=0?cmp:a.Offset.CompareTo(b.Offset); });
            List<List<Face>> batches=new List<List<Face>>();
            foreach(Face face in faces)
            {
                List<Face> chosen=null;
                foreach(List<Face> batch in batches)
                {
                    bool conflict=false;
                    foreach(Face other in batch) if(Overlaps(face,other)) { conflict=true;break; }
                    if(!conflict) { chosen=batch;break; }
                }
                if(chosen==null)
                {
                    if(batches.Count>=16) return fallback;
                    chosen=new List<Face>();batches.Add(chosen);
                }
                chosen.Add(face);
            }
            if(batches.Count<=1) return fallback;
            List<List<int>> result=new List<List<int>>();
            foreach(List<Face> batch in batches)
            {
                batch.Sort(delegate(Face a,Face b) { return a.Offset.CompareTo(b.Offset); });
                List<int> indices=new List<int>(batch.Count*3);
                foreach(Face face in batch) for(int i=0;i<3;i++) indices.Add(triangles[face.Offset+i]);
                result.Add(indices);
            }
            return result;
        }
        static float Cross(Vector2 a,Vector2 b) { return a.x*b.y-a.y*b.x; }
        static bool Overlaps(Face a,Face b)
        {
            if(a.Area<1e-10f || b.Area<1e-10f || a.Max.x<=b.Min.x || b.Max.x<=a.Min.x
                || a.Max.y<=b.Min.y || b.Max.y<=a.Min.y) return false;
            return !Separated(a.A,a.B,a,b) && !Separated(a.B,a.C,a,b) && !Separated(a.C,a.A,a,b)
                && !Separated(b.A,b.B,a,b) && !Separated(b.B,b.C,a,b) && !Separated(b.C,b.A,a,b);
        }
        static bool Separated(Vector2 from,Vector2 to,Face a,Face b)
        {
            Vector2 edge=to-from;
            float a0=Cross(edge,a.A-from),a1=Cross(edge,a.B-from),a2=Cross(edge,a.C-from);
            float b0=Cross(edge,b.A-from),b1=Cross(edge,b.B-from),b2=Cross(edge,b.C-from);
            float overlap=Mathf.Min(Mathf.Max(a0,Mathf.Max(a1,a2)),Mathf.Max(b0,Mathf.Max(b1,b2)))
                -Mathf.Max(Mathf.Min(a0,Mathf.Min(a1,a2)),Mathf.Min(b0,Mathf.Min(b1,b2)));
            return overlap<=1e-8f;
        }
    }
}
