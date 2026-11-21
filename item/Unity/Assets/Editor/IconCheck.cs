using System.IO;
using System.Linq;
using EFT.Visual;
using UnityEditor;
using UnityEngine;
// Renders each built prefab's item icon the way the game does (IconCreatorBase.SetupScene + ItemIconCreator.CenterByBounds
// + WeaponPreview.GetBounds), grey on black, to ../icon_check/<prefab>.png, so the framing is checked before the game.
// Run: Unity.exe -batchmode -quit -projectPath Unity -executeMethod IconCheck.Run   (not -nographics)
public static class IconCheck {
 public static void Run(){
  string outDir=Path.GetFullPath(Path.Combine(Application.dataPath,"../../icon_check"));Directory.CreateDirectory(outDir);
  var mat=new Material(Shader.Find("Standard")){color=new Color(0.6f,0.6f,0.6f)};
  var cam=new GameObject("IconCam").AddComponent<Camera>();cam.transform.position=new Vector3(0f,-10f,-1f);
  cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.nearClipPlane=0.01f;cam.farClipPlane=100f;
  var light=new GameObject("L").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(30f,-30f,0f);
  foreach(var path in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/Generated"}).Select(AssetDatabase.GUIDToAssetPath)){
   var src=AssetDatabase.LoadAssetAtPath<GameObject>(path);var pivot=src.GetComponent<PreviewPivot>();if(pivot==null)continue;
   int w=(src.name.StartsWith("nvg_")&&!src.name.Contains("mount"))||src.name.Contains("railink_rails")?250:125,h=125;  // goggles 2x1, the rest 1x1
   var model=Object.Instantiate(src);
   foreach(var r in model.GetComponentsInChildren<Renderer>(true))r.sharedMaterials=r.sharedMaterials.Select(_=>mat).ToArray();
   model.transform.SetParent(cam.transform);model.transform.localPosition=Vector3.zero;model.transform.rotation=Quaternion.identity;
   var s=pivot.Icon;model.transform.rotation=s.rotation;
   var bs=model.GetComponentsInChildren<Renderer>(false).Where(r=>!r.name.Contains("linza")).Select(r=>r.bounds).Where(b=>b.extents!=Vector3.zero).ToArray();
   var B=bs[0];foreach(var b in bs)B.Encapsulate(b);
   float aspect=(float)w/h;cam.orthographic=true;
   var p=model.transform.position-B.center;var p2=new Vector3(p.x,p.y+cam.transform.position.y,p.z);if(s.hasOffset!=0)p2+=s.position;
   model.transform.position=p2;
   cam.orthographicSize=B.extents.x/B.extents.y>aspect?B.extents.x/aspect/s.boundsScale:B.extents.y/s.boundsScale;cam.aspect=aspect;
   var rt=new RenderTexture(w,h,24);cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;
   var tex=new Texture2D(w,h,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,w,h),0,0);tex.Apply();
   File.WriteAllBytes(Path.Combine(outDir,src.name+".png"),tex.EncodeToPNG());
   Debug.Log($"ICONCHECK {src.name} {w}x{h} bounds {B.center} {B.extents} size {cam.orthographicSize:F3} offset {s.position} has {s.hasOffset}");
   cam.targetTexture=null;RenderTexture.active=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(model);
  }
 }
}
