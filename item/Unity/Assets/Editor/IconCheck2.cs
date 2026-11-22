using System.IO;
using System.Linq;
using EFT.Visual;
using UnityEditor;
using UnityEngine;
// As IconCheck, for the states the game also draws: the F-PANO flipped the other way, and the rails with the F-PANO on
// their mod_nvg (the root's PreviewPivot frames every active renderer of the whole model).
public static class IconCheck2 {
 static Camera cam;static Material mat;static string outDir;
 public static void Run(){
  outDir=Path.GetFullPath(Path.Combine(Application.dataPath,"../../icon_check"));Directory.CreateDirectory(outDir);
  mat=new Material(Shader.Find("Standard")){color=new Color(0.6f,0.6f,0.6f)};
  cam=new GameObject("IconCam").AddComponent<Camera>();cam.transform.position=new Vector3(0f,-10f,-1f);
  cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.nearClipPlane=0.01f;cam.farClipPlane=100f;
  var light=new GameObject("L").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(30f,-30f,0f);
  var gog=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Generated/blk_goggle/nvg_fpano_blk.prefab");
  var rails=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Generated/blk_rails/fpano_railink_rails_blk.prefab");
  foreach(bool on in new[]{true,false}){
   var g=Object.Instantiate(gog);Flip(g,on);Shot(g,gog.GetComponent<PreviewPivot>().Icon,250,"goggle_"+(on?"on":"off"));
   var r=Object.Instantiate(rails);var slot=r.GetComponentsInChildren<Transform>(true).First(t=>t.name=="mod_nvg");
   var g2=Object.Instantiate(gog,slot);g2.transform.localPosition=Vector3.zero;g2.transform.localRotation=Quaternion.Euler(90f,0f,0f);Flip(g2,on);
   foreach(var c in g2.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(c=>c.name.StartsWith("fpano_power_cable")))c.gameObject.SetActive(true);
   var fit=IconFraming.Fit(r,rails.GetComponent<PreviewPivot>().Icon,2f);
   Debug.Log($"ICONFIT2 rails+goggle {(on?"on":"off")} boundsScale {fit.boundsScale:F4} offset {fit.position.x:F5},{fit.position.y:F5}");
   Shot(r,rails.GetComponent<PreviewPivot>().Icon,250,"rails_goggle_"+(on?"on":"off"));
   var r3=Object.Instantiate(rails);var s3=r3.GetComponentsInChildren<Transform>(true).First(t=>t.name=="mod_nvg");
   var g3=Object.Instantiate(gog,s3);g3.transform.localPosition=Vector3.zero;g3.transform.localRotation=Quaternion.Euler(90f,0f,0f);Flip(g3,on);
   foreach(var c in g3.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(c=>c.name.StartsWith("fpano_power_cable")))c.gameObject.SetActive(true);
   Shot(r3,fit,250,"rails_goggle_fit_"+(on?"on":"off"));
  }
 }
 static void Flip(GameObject g,bool on){var c=g.GetComponent<CurveRotator>();c.RotatedTransform.localRotation=on?c.OnRotation:c.OffRotation;}
 static void Shot(GameObject model,PreviewPivot.IconSettings s,int w,string name){
  int h=125;
  foreach(var r in model.GetComponentsInChildren<Renderer>(true))r.sharedMaterials=r.sharedMaterials.Select(_=>mat).ToArray();
  model.transform.SetParent(cam.transform);model.transform.localPosition=Vector3.zero;model.transform.rotation=s.rotation;
  var bs=model.GetComponentsInChildren<Renderer>(false).Where(r=>!r.name.Contains("linza")).Select(r=>r.bounds).Where(b=>b.extents!=Vector3.zero).ToArray();
  var B=bs[0];foreach(var b in bs)B.Encapsulate(b);
  float aspect=(float)w/h;cam.orthographic=true;
  var p=model.transform.position-B.center;var p2=new Vector3(p.x,p.y+cam.transform.position.y,p.z);if(s.hasOffset!=0)p2+=s.position;
  model.transform.position=p2;
  cam.orthographicSize=B.extents.x/B.extents.y>aspect?B.extents.x/aspect/s.boundsScale:B.extents.y/s.boundsScale;cam.aspect=aspect;
  var rt=new RenderTexture(w,h,24);cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;
  var tex=new Texture2D(w,h,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,w,h),0,0);tex.Apply();
  File.WriteAllBytes(Path.Combine(outDir,name+".png"),tex.EncodeToPNG());
  Debug.Log($"ICONCHECK2 {name} bounds {B.center} {B.extents} size {cam.orthographicSize:F3}");
  cam.targetTexture=null;RenderTexture.active=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(model);
 }
}
