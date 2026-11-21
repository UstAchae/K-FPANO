using System.Linq;
using EFT.Visual;
using UnityEngine;
// The game's item icon (ItemIconCreator.CenterByBounds + WeaponPreview.GetBounds): the model turned by Icon.rotation,
// B = the union of its active renderers' Renderer.bounds put on the camera's axis, orthographicSize = B.extents.y /
// boundsScale (or B.extents.x / aspect / boundsScale when wider than the cell), then Icon.position added (hasOffset).
// Renderer.bounds is each mesh's own box turned and boxed again, far larger than the mesh once turned (the ENVG-B
// battery's 30 cm cable doubled its icon; the goggles looked small and off centre: user, in game). Fit takes the real
// outline (every vertex, turned as in the icon) and sets offset and boundsScale so that outline is centred and fills the
// cell with a small margin, whatever B the game computes.
public static class IconFraming {
 const float Margin=1.06f;
 public static PreviewPivot.IconSettings Fit(GameObject root,PreviewPivot.IconSettings icon,float aspect){
  var keepRot=root.transform.rotation;var keepPos=root.transform.position;
  root.transform.SetPositionAndRotation(Vector3.zero,icon.rotation);
  var rs=root.GetComponentsInChildren<Renderer>(false).Where(r=>!r.name.Contains("linza")&&r.bounds.extents!=Vector3.zero).ToArray();
  Bounds B=rs[0].bounds;foreach(var r in rs)B.Encapsulate(r.bounds);
  Bounds? T=null;
  foreach(var r in rs){
   Mesh m;Matrix4x4 w=r.transform.localToWorldMatrix;
   if(r is SkinnedMeshRenderer s){m=new Mesh();s.BakeMesh(m,true);w=Matrix4x4.TRS(r.transform.position,r.transform.rotation,Vector3.one);}
   else{var f=r.GetComponent<MeshFilter>();if(f==null||f.sharedMesh==null)continue;m=f.sharedMesh;}
   foreach(var v in m.vertices){var p=w.MultiplyPoint3x4(v);if(T==null)T=new Bounds(p,Vector3.zero);else{var t=T.Value;t.Encapsulate(p);T=t;}}
  }
  root.transform.SetPositionAndRotation(keepPos,keepRot);
  var tb=T.Value;
  float baseSize=B.extents.x/B.extents.y>aspect?B.extents.x/aspect:B.extents.y;   // what the game's camera size is made from
  float need=Mathf.Max(tb.extents.y,tb.extents.x/aspect)*Margin;                  // the size that shows the outline
  icon.boundsScale=baseSize/need;
  var off=B.center-tb.center;icon.position=new Vector3(off.x,off.y,0f);icon.hasOffset=1;
  Debug.Log($"ICON {root.name}: renderer boxes {B.extents} outline {tb.extents} boundsScale {icon.boundsScale:F3} offset {icon.position}");
  return icon;
 }
}
