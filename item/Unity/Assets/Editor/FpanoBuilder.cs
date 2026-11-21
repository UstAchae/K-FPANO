using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.Visual;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
// Builds one bundle per colour from what item/prepare.py wrote to Assets/Source/<colour>. The prefab copies the
// GPNVG-18's layout and components (nvg_l3_gpnvg-18_anvis): root with LODGroup, Dress, NightVisionDevice,
// CurveRotator, NightVisionMount and PreviewPivot; the Base plate on the root; the flip hinge "axis" carrying the
// mount's arm and the device node nvg_fpano_LOD0 (with its figure-8 eyepiece glass and the thermal lens marker), and
// the face light.
// Run: -executeMethod FpanoBuilder.Build
public static class FpanoBuilder {
 const string Src="Assets/Source",Gen="Assets/Generated";
 static readonly string Out=Path.GetFullPath(Path.Combine(Application.dataPath,"../../built"));
 public const string DeviceNode="nvg_fpano_LOD0",ThermalLens="fpano_thermal_lens";
 [Serializable] public class Node {public string name,parent,mesh,mat;public float[] pos,rot;public bool skin,sideSkin,hidden;public string[] bones;public float[] weightCentre;public float weightNear,weightFar,sideFrom,sideTo;}
 [Serializable] public class Info {public string colour,item,root;public string[] atlases;public float[] thermalLens,onRotation,offRotation;public Node[] nodes;}

 const float RotationSpeed=0.75187969f;  // the GPNVG-18's
 static AnimationCurve Curve(){
  var c=new AnimationCurve(
   new Keyframe(0f,0f,2.1630912f,2.1630912f,0.33333334f,0.33333334f),
   new Keyframe(0.46189547f,0.99912202f,2.1630912f,0.0016316094f,0.33333334f,0.33333334f),
   new Keyframe(1f,1f,0.0016316094f,0.0016316094f,0.33333334f,0.33333334f));
  c.preWrapMode=WrapMode.ClampForever;c.postWrapMode=WrapMode.ClampForever;return c;
 }

 static Vector3 V(float[] a)=>new Vector3(a[0],a[1],a[2]);
 static Quaternion Q(float[] a)=>new Quaternion(a[0],a[1],a[2],a[3]);
 static Vector3[] R3(BinaryReader r,int n){var a=new Vector3[n];for(int i=0;i<n;i++)a[i]=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());return a;}
 static Vector2[] R2(BinaryReader r,int n){var a=new Vector2[n];for(int i=0;i<n;i++)a[i]=new Vector2(r.ReadSingle(),r.ReadSingle());return a;}
 static Mesh ReadMesh(string path,string name,string gen){
  var mesh=new Mesh{name=name,indexFormat=IndexFormat.UInt32};
  using(var r=new BinaryReader(File.OpenRead(path))){
   int n=r.ReadInt32();r.ReadInt32();int s=r.ReadInt32();
   mesh.vertices=R3(r,n);mesh.normals=R3(r,n);mesh.uv=R2(r,n);mesh.subMeshCount=s;
   for(int i=0;i<s;i++){var a=new int[r.ReadInt32()];for(int j=0;j<a.Length;j++)a[j]=r.ReadInt32();mesh.SetTriangles(a,i,true);}
   if(r.BaseStream.Position!=r.BaseStream.Length)throw new Exception("Unread mesh bytes in "+path);
  }
  mesh.RecalculateTangents();mesh.RecalculateBounds();mesh.UploadMeshData(false);
  AssetDatabase.CreateAsset(mesh,gen+"/mesh_"+name+".asset");return mesh;
 }
 static Texture2D Import(string path,TextureImporterType type,bool srgb){
  var im=(TextureImporter)AssetImporter.GetAtPath(path);
  im.textureType=type;im.sRGBTexture=srgb;im.mipmapEnabled=true;im.wrapMode=TextureWrapMode.Repeat;im.anisoLevel=4;im.maxTextureSize=2048;im.SaveAndReimport();
  return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
 }
 // Placeholder shader with the p0 shader's texture slots; finalize.py points the materials at the game's shader.
 static Shader StubShader(){
  string p=Gen+"/FpanoStub.shader";
  File.WriteAllText(p,"Shader \"UstAchae/FPANO/Stub\" { Properties { _MainTex (\"m\", 2D) = \"white\" {} _SpecMap (\"s\", 2D) = \"white\" {} _BumpMap (\"b\", 2D) = \"bump\" {} _Color (\"c\", Color) = (1,1,1,1) } SubShader { Pass { Color (1,1,1,1) } } }");
  AssetDatabase.ImportAsset(p,ImportAssetOptions.ForceSynchronousImport);return AssetDatabase.LoadAssetAtPath<Shader>(p);
 }
 public static readonly string[] Items={"rails","battery","mount","goggle"};
 public static string BundleKey(string item,string colour)=>"ustachae_fpano/"+item+"_"+colour+".bundle";

 public static void Build(){
  PlayerSettings.colorSpace=ColorSpace.Linear;
  if(AssetDatabase.IsValidFolder(Gen))AssetDatabase.DeleteAsset(Gen);
  Directory.CreateDirectory(Gen);Directory.CreateDirectory(Out);AssetDatabase.Refresh();
  var stub=StubShader();
  var builds=Directory.GetDirectories(Src).SelectMany(d=>Items.Select(it=>BuildOne(Path.GetFileName(d),it,stub))).ToArray();
  AssetDatabase.SaveAssets();
  if(BuildPipeline.BuildAssetBundles(Out,builds,BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.StrictMode,BuildTarget.StandaloneWindows64)==null)throw new Exception("Build failed");
  Debug.Log("FPANO_BUILD_SUCCESS "+string.Join(",",builds.Select(b=>b.assetBundleName)));
 }

 // Material names carry the role for finalize.py: _body (the goggle's atlas) and _mount (the mount's).
 static Material Mat(string src,string gen,string name,string atlas,Shader stub){
  var m=new Material(stub){name=name+"_"+atlas};
  m.SetTexture("_MainTex",Import(src+"/albedo_"+atlas+".png",TextureImporterType.Default,true));
  m.SetTexture("_SpecMap",Import(src+"/spec_"+atlas+".png",TextureImporterType.Default,false));
  m.SetTexture("_BumpMap",Import(src+"/normal_"+atlas+".png",TextureImporterType.NormalMap,false));
  AssetDatabase.CreateAsset(m,gen+"/mat_"+atlas+".asset");return m;
 }

 // One of the four items (split_items.py): rails, battery, mount, goggle, from Source/<colour>/info_<item>.json.
 static AssetBundleBuild BuildOne(string colour,string item,Shader stub){
  string src=Src+"/"+colour,gen=Gen+"/"+colour+"_"+item;Directory.CreateDirectory(gen);AssetDatabase.Refresh();
  var info=JsonUtility.FromJson<Info>(File.ReadAllText(src+"/info_"+item+".json"));
  string name=info.root;
  var mats=new Dictionary<string,Material>();
  foreach(var a in info.nodes.Select(n=>n.mat).Where(m=>m!="").Distinct()){
   if(a=="glass"){var glass=new Material(stub){name=name+"_glass"};AssetDatabase.CreateAsset(glass,gen+"/mat_glass.asset");mats[a]=glass;}  // no maps: finalize.py makes it the game's dark glass
   else mats[a]=Mat(src,gen,name,a,stub);
  }

  var root=new GameObject(name);
  var made=new Dictionary<string,Transform>();
  var renderers=new List<Renderer>();
  foreach(var n in info.nodes){
   var go=new GameObject(n.name);go.transform.SetParent(n.parent==""?root.transform:made[n.parent],false);
   go.transform.localPosition=V(n.pos);go.transform.localRotation=Q(n.rot);made[n.name]=go.transform;
   if(n.sideSkin){
    // A RAILINK rail mesh (prepare.py SIDE_*): bones group, left, right; a vertex goes with its side's bone past
    // sideTo in |x|, with the group within sideFrom, smooth between. Mesh in the group's frame (identity on the root).
    var mesh=ReadMesh(src+"/"+n.mesh+".bin",n.mesh,gen);var vs=mesh.vertices;var bw=new BoneWeight[vs.Length];
    for(int i=0;i<vs.Length;i++){float t=Mathf.Clamp01((Mathf.Abs(vs[i].x)-n.sideFrom)/(n.sideTo-n.sideFrom));t=t*t*(3-2*t);
     bw[i]=new BoneWeight{boneIndex0=vs[i].x<0?1:2,weight0=t,boneIndex1=0,weight1=1-t};}
    var bones=n.bones.Select(b=>made[b]).ToArray();
    mesh.boneWeights=bw;mesh.bindposes=bones.Select(b=>b.worldToLocalMatrix*go.transform.localToWorldMatrix).ToArray();EditorUtility.SetDirty(mesh);
    var sr=go.AddComponent<SkinnedMeshRenderer>();sr.sharedMesh=mesh;sr.bones=bones;sr.rootBone=bones[0];sr.sharedMaterial=mats[n.mat];
    sr.shadowCastingMode=ShadowCastingMode.On;sr.receiveShadows=true;sr.quality=SkinQuality.Bone2;sr.updateWhenOffscreen=true;
    sr.localBounds=new Bounds(mesh.bounds.center,mesh.bounds.size);renderers.Add(sr);
   }
   else if(n.skin){
    // The power cable: skinned to its bones (prefab.py POWER_*), the second bone's weight 1 within weightNear of
    // weightCentre, 0 past weightFar, smooth between; the mesh is in the root frame, as the bones' bind poses.
    var mesh=ReadMesh(src+"/"+n.mesh+".bin",n.mesh,gen);
    var c=V(n.weightCentre);var bw=new BoneWeight[mesh.vertexCount];var vs=mesh.vertices;
    for(int i=0;i<vs.Length;i++){float t=Mathf.Clamp01((n.weightFar-Vector3.Distance(vs[i],c))/(n.weightFar-n.weightNear));t=t*t*(3-2*t);
     bw[i]=new BoneWeight{boneIndex0=1,weight0=t,boneIndex1=0,weight1=1-t};}
    var bones=n.bones.Select(b=>made[b]).ToArray();
    mesh.boneWeights=bw;mesh.bindposes=bones.Select(b=>b.worldToLocalMatrix*go.transform.localToWorldMatrix).ToArray();
    EditorUtility.SetDirty(mesh);
    var sr=go.AddComponent<SkinnedMeshRenderer>();sr.sharedMesh=mesh;sr.bones=bones;sr.rootBone=bones[1];sr.sharedMaterial=mats[n.mat];
    // Its bounds follow its bones (updateWhenOffscreen): no padding, which the game's icons (all active renderers'
    // bounds) would count. Hidden by an inactive GameObject until the client binds it to the rails' socket.
    sr.shadowCastingMode=ShadowCastingMode.On;sr.receiveShadows=true;sr.updateWhenOffscreen=true;sr.quality=SkinQuality.Bone2;
    sr.localBounds=new Bounds(bones[1].InverseTransformPoint(go.transform.TransformPoint(mesh.bounds.center)),mesh.bounds.size);
    if(n.hidden)go.SetActive(false);renderers.Add(sr);
   }
   else if(n.mesh!=""){
    var mesh=ReadMesh(src+"/"+n.mesh+".bin",n.mesh,gen);
    go.AddComponent<MeshFilter>().sharedMesh=mesh;
    var r=go.AddComponent<MeshRenderer>();r.sharedMaterial=mats[n.mat];r.shadowCastingMode=n.mat=="glass"?ShadowCastingMode.Off:ShadowCastingMode.On;r.receiveShadows=true;renderers.Add(r);
    var box=go.AddComponent<BoxCollider>();box.center=mesh.bounds.center;box.size=mesh.bounds.size;box.enabled=false;  // disabled like native items
   }
  }
  var all=renderers.ToArray();
  var lodGroup=root.AddComponent<LODGroup>();lodGroup.SetLODs(new[]{new LOD(0.02f,all)});lodGroup.size=0.5f;
  var dress=root.AddComponent<Dress>();dress.DecalTextureType=1;dress.Renderers=all;
  if(item=="mount"||item=="goggle"){
   // The flip on the mount, as the Norotos TATM: CurveRotator turns "axis", NightVisionMount on the root.
   var axis=made["axis"];var on=Q(info.onRotation);var off=Q(info.offRotation);
   if(Quaternion.Angle(axis.localRotation,on)>0.01f)throw new Exception("axis rest rotation is not OnRotation");
   var rot=root.AddComponent<CurveRotator>();rot.RotatedTransform=axis;rot.RotationSpeed=RotationSpeed;rot.OffRotation=off;rot.OnRotation=on;rot.AnimationCurve=Curve();
   root.AddComponent<NightVisionMount>().Set(rot);
  }
  if(item=="goggle")Goggle(root,made[DeviceNode],info);
  var b=all[0].bounds;foreach(var r in all)b.Encapsulate(r.bounds);
  var pivot=root.AddComponent<PreviewPivot>();
  pivot.pivotPosition=b.center;pivot.pivotRotation=new Quaternion(0.0074694254f,0.98476535f,0.04375f,-0.16812912f);pivot.scale=Vector3.one;
  pivot.Icon=new PreviewPivot.IconSettings{rotation=new Quaternion(-0.049385615f,-0.94817758f,-0.25407037f,0.1843058f),boundsScale=1.8080130f,perspective=15,orthographicSize=10};
  if(item=="goggle"){  // the GPNVG-18's pivot and LOD reference; the icon framed on the goggle itself (IconFraming)
   pivot.pivotPosition=new Vector3(-0.00038808584f,-0.013348266f,-0.08924666f);
   lodGroup.localReferencePoint=new Vector3(-0.00038808584f,-0.013348244f,-0.089246705f);
   pivot.Icon=IconFraming.Fit(root,pivot.Icon,2f);
  }
  if(item=="rails"){  // a 2x1 item seen as the FAST MT's icon sees its helmet (the rails as fitted there), all of it centred
   pivot.Icon=IconFraming.Fit(root,new PreviewPivot.IconSettings{rotation=new Quaternion(-0.08213679f,-0.9169861f,-0.29617643f,0.2543021f),perspective=15,orthographicSize=10},2f);
  }
  if(item=="mount"){  // as the Norotos TATM (a 1x1 mount): its view angles and framing
   pivot.pivotRotation=new Quaternion(0f,1f,0f,-4.371139e-08f);pivot.scale=Vector3.one*1.4285715f;
   pivot.Icon=new PreviewPivot.IconSettings{rotation=new Quaternion(0f,0.94551843f,0f,-0.3255687f),boundsScale=0.9000013f,perspective=15,orthographicSize=10};
  }
  if(item=="battery"){  // a 1x1 helmet accessory, as the RAC headset frames it, seen from behind (its switch side)
   pivot.pivotRotation=new Quaternion(0f,1f,0f,-4.371139e-08f);
   pivot.Icon=new PreviewPivot.IconSettings{rotation=new Quaternion(0f,0.09584612f,0f,0.99539614f),boundsScale=0.852f,perspective=15,orthographicSize=10};
  }
  string prefab=gen+"/"+name+".prefab";PrefabUtility.SaveAsPrefabAsset(root,prefab);UnityEngine.Object.DestroyImmediate(root);
  return new AssetBundleBuild{assetBundleName=BundleKey(item,colour),assetNames=new[]{prefab}};
 }

 static void Goggle(GameObject root,Transform lod,Info info){
  // Thermal objective: centre of its window, +Z out of the front (the device looks along -Y), +Y toward the mount.
  var lens=new GameObject(ThermalLens);lens.transform.SetParent(lod,false);
  lens.transform.localPosition=V(info.thermalLens);
  lens.transform.localRotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);

  // Face light, as the GPNVG-18's "Point light" (off; the game switches it).
  var lightGo=new GameObject("Point light");lightGo.transform.SetParent(lod,false);
  lightGo.transform.localPosition=new Vector3(0.0004f,0.0516f,-0.075f);lightGo.transform.localRotation=new Quaternion(0.479f,0,0,0.878f);
  var light=lightGo.AddComponent<Light>();light.type=LightType.Point;light.color=new Color(0.28275859f,1f,0f);light.intensity=3.14f;light.range=0.05f;light.enabled=false;

  root.AddComponent<NightVisionDevice>().Set(light);
 }
}
