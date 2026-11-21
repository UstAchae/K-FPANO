using System;
using System.Linq;
using EFT.Visual;
using UnityEngine;
// The game's item icon (ItemIconCreator.CenterByBounds): the model turned by Icon.rotation, the bounds of all its active
// renderers centred on the camera, orthographicSize = extents.y / boundsScale (or extents.x / aspect / boundsScale when
// wider than the cell). Our goggles carry parts far from the goggle (mount base, battery and cable on the ENVG-B), which
// moved and shrank the goggle in its icon. Frame sets Icon.position (hasOffset) so the goggle's own bounds are centred,
// and boundsScale so the goggle fills the cell as the GPNVG-18's does (the GPNVG-18's goggle half-width over the
// icon's visible half-width: 0.139 / 0.184).
public static class IconFraming {
 const float Fill=0.139f/0.184f;
 public static PreviewPivot.IconSettings Frame(GameObject root,PreviewPivot.IconSettings icon,Func<Renderer,bool> goggle,float aspect){
  var keepRot=root.transform.rotation;var keepPos=root.transform.position;
  root.transform.SetPositionAndRotation(Vector3.zero,icon.rotation);
  var all=root.GetComponentsInChildren<Renderer>(false).Where(r=>r.bounds.extents!=Vector3.zero).ToArray();
  Bounds B=all[0].bounds;foreach(var r in all)B.Encapsulate(r.bounds);
  var dev=all.Where(goggle).ToArray();Bounds D=dev[0].bounds;foreach(var r in dev)D.Encapsulate(r.bounds);
  root.transform.SetPositionAndRotation(keepPos,keepRot);
  float need=Mathf.Max(D.extents.y,D.extents.x/aspect)/Fill;            // the orthographic size that shows the goggle so
  float baseSize=B.extents.x/B.extents.y>aspect?B.extents.x/aspect:B.extents.y;
  icon.boundsScale=baseSize/need;
  var off=B.center-D.center;icon.position=new Vector3(off.x,off.y,0f);icon.hasOffset=1;
  Debug.Log($"ICON {root.name}: all {B.extents} goggle {D.extents} boundsScale {icon.boundsScale:F3} offset {icon.position}");
  return icon;
 }
}
