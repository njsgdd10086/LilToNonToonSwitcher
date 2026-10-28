using UnityEngine;
using Object = UnityEngine.Object;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
namespace NonToonSwitcher.Tests
{
public static class SurfaceCompatibilityRegression
{
    const string P="_com_nontoon_switcher_surface_";
    static Material Convert(Material source) {
        var target=new Material(Shader.Find("NonToon"));
        typeof(NonToonSwitcher.NonToonConverter).GetMethod("ApplyMappings",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{source,target,new NonToonSwitcher.ConversionLog()});
        target.SetTexture("_BaseTexture",Texture2D.grayTexture);
        target.SetTexture("_SharedMask",Texture2D.blackTexture);
        target.SetFloat("_OutlineWidth",0);
        NonToonSwitcher.NonToonSurfaceCompatibility.ApplyOutline(source,target,new NonToonSwitcher.ConversionLog());
        return target;
    }
    // A procedural fixture avoids creating objects or dirtying the user's open scene.
    static Mesh CreateSphere() {
        const int longitude=32, latitude=16;
        var vertices=new List<Vector3>();
        var tangents=new List<Vector4>();
        var uv=new List<Vector2>();
        var triangles=new List<int>();
        for(int y=0;y<=latitude;y++) for(int x=0;x<=longitude;x++) {
            float theta=x*Mathf.PI*2/longitude, phi=y*Mathf.PI/latitude;
            vertices.Add(new Vector3(Mathf.Sin(phi)*Mathf.Cos(theta),Mathf.Cos(phi),Mathf.Sin(phi)*Mathf.Sin(theta))*0.5f);
            tangents.Add(new Vector4(-Mathf.Sin(theta),0,Mathf.Cos(theta),1));
            uv.Add(new Vector2((float)x/longitude,(float)y/latitude));
            if(y<latitude&&x<longitude) {
                int a=y*(longitude+1)+x, b=a+longitude+1;
                triangles.AddRange(new[]{a,a+1,b,a+1,b+1,b});
            }
        }
        var mesh=new Mesh {name="NonToon regression sphere"};
        mesh.SetVertices(vertices);mesh.SetNormals(vertices.Select(v=>v.normalized).ToList());
        mesh.SetTangents(tangents);mesh.SetUVs(0,uv);mesh.SetTriangles(triangles,0);
        mesh.RecalculateBounds();
        return mesh;
    }
    static Color[] Render(Material material, float normalLength=1, Color? vertexColor=null) {
        bool previousAsync=ShaderUtil.allowAsyncCompilation;ShaderUtil.allowAsyncCompilation=false;
        var preview=new PreviewRenderUtility();
        var mesh=CreateSphere();
        if(vertexColor.HasValue) mesh.colors=Enumerable.Repeat(vertexColor.Value,mesh.vertexCount).ToArray();
        mesh.normals=mesh.normals.Select(n=>n*normalLength).ToArray();
        try {
            preview.camera.transform.position=new Vector3(0,0,-3);
            preview.camera.transform.rotation=Quaternion.identity;
            preview.camera.orthographic=true;
            preview.camera.orthographicSize=0.9f;
            preview.camera.clearFlags=CameraClearFlags.SolidColor;
            preview.camera.backgroundColor=Color.black;
            preview.ambientColor=Color.white;
            foreach(var l in preview.lights) { l.intensity=1; l.transform.rotation=Quaternion.Euler(30,30,0); }
            preview.BeginPreview(new Rect(0,0,192,192),GUIStyle.none);
            preview.DrawMesh(mesh,Matrix4x4.identity,material,0);
            preview.Render(true);
            var result=preview.EndPreview();
            var old=RenderTexture.active;
            var rt=RenderTexture.GetTemporary(192,192,0,RenderTextureFormat.ARGB32);
            var tex=new Texture2D(192,192,TextureFormat.RGBA32,false,true);
            try {
                Graphics.Blit(result,rt);
                RenderTexture.active=rt;
                tex.ReadPixels(new Rect(0,0,192,192),0,0);
                tex.Apply();
                return tex.GetPixels();
            } finally {RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);UnityEngine.Object.DestroyImmediate(tex);}
        } finally {preview.Cleanup();UnityEngine.Object.DestroyImmediate(mesh);ShaderUtil.allowAsyncCompilation=previousAsync;}
    }
    static float Difference(Color[] a,Color[] b) {return a.Zip(b,(x,y)=>Mathf.Abs(x.r-y.r)+Mathf.Abs(x.g-y.g)+Mathf.Abs(x.b-y.b)).Average();}
    static int Extent(Color[] p) {int min=192,max=-1;for(int i=0;i<p.Length;i++)if(Mathf.Max(p[i].r,Mathf.Max(p[i].g,p[i].b))>0.015f){min=Mathf.Min(min,i%192);max=Mathf.Max(max,i%192);}return max-min+1;}
    static Texture2D Pattern(float offset, bool gradient) {
        var texture=new Texture2D(128,8,TextureFormat.RGBA32,false,true) {wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear};
        var pixels=new Color[128*8];
        for(int y=0;y<8;y++) for(int x=0;x<128;x++) {
            float u=Mathf.Repeat((x+0.5f)/128+offset,1);
            pixels[y*128+x]=gradient?new Color(u,0.25f,0.1f,1):(u<0.5f?Color.white:Color.black);
        }
        texture.SetPixels(pixels);texture.Apply();
        return texture;
    }
    public static List<object> RunAll() {
        var rows=new List<object>();
        var source=Shader.Find("lilToon");
        if(source==null || !NonToonSurfaceCompatibility.EnsureInstalled(new ConversionLog())) throw new InvalidOperationException("Install lilToon, NonToon and NonToon Modules before running the regression suite.");
        var s=new Material(source);
        s.SetFloat("_LightMinLimit",1);s.SetFloat("_LightMaxLimit",1);s.SetFloat("_UseShadow",0);
        s.SetTexture("_MatCapBlendMask",Texture2D.whiteTexture);s.SetFloat("_MatCapShadowMask",0); Material baseline=null,actual=null;
        try {
            s.SetFloat("_UseMatCap",0);s.SetFloat("_UseMatCap2nd",0);
            s.SetTexture("_MatCapTex",null);s.SetTexture("_MatCap2ndTex",null);
            baseline=Convert(s);var noCap=Render(baseline);
            s.SetTexture("_MatCapTex",Texture2D.whiteTexture);
            s.SetColor("_MatCapColor",new Color(1,0.2f,0.1f,1));
            s.SetFloat("_MatCapBlendMode",1);
            s.SetFloat("_MatCapEnableLighting",0);
            actual=Convert(s);float disabled=Difference(noCap,Render(actual));Object.DestroyImmediate(actual);
            rows.Add(new{name="Disabled MatCap remains invisible",pass=disabled<0.001f,difference=disabled});
            s.SetFloat("_UseMatCap",1);s.SetFloat("_MatCapBlend",1);
            s.SetColor("_MatCapColor",new Color(1,0.2f,0.1f,0));
            actual=Convert(s);float alpha=Difference(noCap,Render(actual));Object.DestroyImmediate(actual);
            rows.Add(new{name="MatCap color alpha zero has zero influence",pass=alpha<0.001f,difference=alpha});
            s.SetColor("_MatCapColor",new Color(1,0.2f,0.1f,1));s.SetFloat("_MatCapBlend",0);
            actual=Convert(s);float blend=Difference(noCap,Render(actual));Object.DestroyImmediate(actual);
            rows.Add(new{name="MatCap blend zero has zero influence",pass=blend<0.001f,difference=blend});
            s.SetFloat("_UseMatCap",0);s.SetTexture("_MatCapTex",null);s.SetFloat("_UseMatCap2nd",1);
            s.SetTexture("_MatCap2ndTex",Texture2D.whiteTexture);s.SetColor("_MatCap2ndColor",new Color(1,0,0,1));s.SetFloat("_MatCap2ndBlend",1);s.SetFloat("_MatCap2ndBlendMode",1);s.SetFloat("_MatCap2ndEnableLighting",0);
            actual=Convert(s);float second=Difference(noCap,Render(actual));Object.DestroyImmediate(actual);actual=null;
            rows.Add(new{name="Second MatCap works independently",pass=second>0.005f,difference=second});
            s.SetFloat("_UseMatCap",1);s.SetFloat("_MatCapBlend",1);s.SetFloat("_MatCapEnableLighting",0);
            s.SetTexture("_MatCapTex",Texture2D.whiteTexture);s.SetFloat("_UseMatCap2nd",0);
            s.SetColor("_MatCapColor",new Color(1,0,0,1));s.SetFloat("_MatCapBlendMode",0);
            actual=Convert(s);var normalBlend=Render(actual);Object.DestroyImmediate(actual);actual=null;
            var nc=normalBlend[192*96+96];
            rows.Add(new{name="Normal MatCap replaces the underlying color",pass=nc.r>0.9f&&nc.g<0.01f&&nc.b<0.01f,pixel=nc.ToString("F3")});
            s.SetFloat("_MatCapBlendMode",3);
            actual=Convert(s);var multiply=Render(actual);Object.DestroyImmediate(actual);actual=null;
            var mc=multiply[192*96+96];var bc=noCap[192*96+96];
            rows.Add(new{name="Multiply MatCap preserves red and removes green",pass=Mathf.Abs(mc.r-bc.r)<0.02f&&mc.g<0.01f,pixel=mc.ToString("F3")});
            s.SetFloat("_MatCapBlendMode",1);s.SetFloat("_UseMatCap2nd",1);s.SetFloat("_MatCap2ndBlendMode",1);
            s.SetColor("_MatCap2ndColor",new Color(0,1,0,1));
            actual=Convert(s);var both=Render(actual);Object.DestroyImmediate(actual);actual=null;
            var cc=both[192*96+96];
            rows.Add(new{name="Two additive MatCaps retain both layers",pass=cc.r>0.9f&&cc.g>0.9f&&Mathf.Abs(cc.b-bc.b)<0.02f,pixel=cc.ToString("F3")});
            s.SetFloat("_UseMatCap2nd",0);s.SetTexture("_MatCapBlendMask",Texture2D.blackTexture);
            actual=Convert(s);var masked=Render(actual);Object.DestroyImmediate(actual);actual=null;
            float maskDiff=Difference(noCap,masked);
            rows.Add(new{name="Black MatCap mask suppresses the effect",pass=maskDiff<0.001f,difference=maskDiff});
            var pattern=Pattern(0,false);var shiftedPattern=Pattern(0.25f,false);var gradient=Pattern(0,true);
            var bump=new Texture2D(4,4,TextureFormat.RGBA32,false,true);
            bump.SetPixels(Enumerable.Repeat(new Color(0.8f,0.5f,0.8f,1),16).ToArray());bump.Apply();
            var uvSource=new Material(source);
            try {
                uvSource.SetFloat("_UseMatCap",1);uvSource.SetFloat("_MatCapBlendMode",0);
                uvSource.SetFloat("_MatCapBlend",1);uvSource.SetColor("_MatCapColor",Color.white);
                uvSource.SetFloat("_MatCapEnableLighting",0);uvSource.SetFloat("_MatCapShadowMask",0);
                uvSource.SetTexture("_MatCapTex",gradient);uvSource.SetTexture("_BumpMap",bump);
                uvSource.SetFloat("_UseBumpMap",1);uvSource.SetFloat("_BumpScale",1);
                uvSource.SetTexture("_MatCapBumpMap",bump);uvSource.SetFloat("_MatCapBumpScale",1);
                uvSource.SetFloat("_MatCapNormalStrength",1);uvSource.SetFloat("_MatCapCustomNormal",0);
                actual=Convert(uvSource);var mainNormal=Render(actual);Object.DestroyImmediate(actual);actual=null;
                uvSource.SetFloat("_MatCapCustomNormal",1);
                actual=Convert(uvSource);var customNormal=Render(actual);Object.DestroyImmediate(actual);actual=null;
                float normalDifference=Difference(mainNormal,customNormal);
                rows.Add(new{name="MatCap applies the main bump once",pass=normalDifference<0.001f,difference=normalDifference});
                uvSource.SetTexture("_BumpMap",null);uvSource.SetFloat("_UseBumpMap",0);uvSource.SetFloat("_MatCapCustomNormal",0);
                uvSource.SetTexture("_MatCapTex",Texture2D.whiteTexture);
                uvSource.SetTexture("_MatCapBlendMask",pattern);
                uvSource.SetTextureOffset("_MainTex",new Vector2(0.25f,0));
                actual=Convert(uvSource);var transformedMask=Render(actual);Object.DestroyImmediate(actual);actual=null;
                uvSource.SetTextureOffset("_MainTex",Vector2.zero);uvSource.SetTexture("_MatCapBlendMask",shiftedPattern);
                actual=Convert(uvSource);var expectedMask=Render(actual);Object.DestroyImmediate(actual);actual=null;
                float uvDifference=Difference(transformedMask,expectedMask);
                rows.Add(new{name="MatCap masks follow source main UV",pass=uvDifference<0.001f,difference=uvDifference});
                uvSource.SetFloat("_UseMatCap",0);uvSource.SetTexture("_OutlineWidthMask",pattern);
                uvSource.SetTextureOffset("_MainTex",new Vector2(0.25f,0));
                uvSource.SetTextureScale("_OutlineWidthMask",new Vector2(2,1));
                uvSource.SetTextureOffset("_OutlineWidthMask",new Vector2(0.1f,0));
                actual=Convert(uvSource);actual.SetFloat("_OutlineWidth",10);
                var transformedOutline=Render(actual);Object.DestroyImmediate(actual);actual=null;
                uvSource.SetTextureOffset("_MainTex",Vector2.zero);uvSource.SetTexture("_OutlineWidthMask",shiftedPattern);
                uvSource.SetTextureScale("_OutlineWidthMask",Vector2.one);uvSource.SetTextureOffset("_OutlineWidthMask",Vector2.zero);
                actual=Convert(uvSource);actual.SetFloat("_OutlineWidth",10);
                var expectedOutline=Render(actual);Object.DestroyImmediate(actual);actual=null;
                float outlineUVDifference=Difference(transformedOutline,expectedOutline);
                rows.Add(new{name="Outline masks use main UV and ignore their own ST",pass=outlineUVDifference<0.001f,difference=outlineUVDifference});
            } finally {Object.DestroyImmediate(uvSource);Object.DestroyImmediate(pattern);Object.DestroyImmediate(shiftedPattern);Object.DestroyImmediate(gradient);Object.DestroyImmediate(bump);}
            s.SetTexture("_MatCapBlendMask",Texture2D.whiteTexture);
            s.SetColor("_MatCapColor",new Color(0.3f,0,0,1));s.SetFloat("_MatCapBlendMode",1);
            actual=Convert(s);var additive=Render(actual)[192*96+96];Object.DestroyImmediate(actual);actual=null;
            s.SetFloat("_MatCapBlendMode",2);
            actual=Convert(s);var screen=Render(actual)[192*96+96];Object.DestroyImmediate(actual);actual=null;
            rows.Add(new{name="Screen remains distinct from Add",pass=screen.r>bc.r+0.01f&&screen.r<additive.r-0.01f,pixel=screen.ToString("F3")});
            s.SetFloat("_UseMatCap",0);s.SetTexture("_OutlineWidthMask",Texture2D.blackTexture);
            actual=Convert(s);actual.SetFloat("_OutlineWidth",10);
            int maskedOutline=Extent(Render(actual));Object.DestroyImmediate(actual);actual=null;
            rows.Add(new{name="Black outline width mask suppresses extrusion",pass=Mathf.Abs(maskedOutline-Extent(noCap))<=2,extent=maskedOutline});
            s.SetTexture("_OutlineWidthMask",Texture2D.whiteTexture);s.SetFloat("_OutlineVertexR2Width",1);
            actual=Convert(s);actual.SetFloat("_OutlineWidth",10);
            int redZero=Extent(Render(actual,1,new Color(0,0.5f,0.5f,1)));
            int redOne=Extent(Render(actual,1,new Color(1,0.5f,0.5f,1)));
            Object.DestroyImmediate(actual);actual=null;
            rows.Add(new{name="Vertex red width mode does not decode RGB as normals",pass=Mathf.Abs(redZero-Extent(noCap))<=2&&redOne>redZero+5,redZero,redOne});
            s.SetFloat("_OutlineVertexR2Width",2);
            actual=Convert(s);actual.SetFloat("_OutlineWidth",10);
            int smoothOne=Extent(Render(actual,1,new Color(0.5f,0.5f,1,1)));
            int smoothHundred=Extent(Render(actual,100,new Color(0.5f,0.5f,1,1)));
            Object.DestroyImmediate(actual);actual=null;
            rows.Add(new{name="Encoded outline normals retain direction without scale inflation",pass=Mathf.Abs(smoothOne-smoothHundred)<=2,smoothOne,smoothHundred});
            baseline.SetFloat("_OutlineWidth",0.2f);baseline.SetColor("_OutlineColor",Color.red);
            var normal=Render(baseline,1);var skinned=Render(baseline,100);
            int normalExtent=Extent(normal),skinnedExtent=Extent(skinned);
            rows.Add(new{name="Skinned normal length does not inflate outline",pass=Mathf.Abs(normalExtent-skinnedExtent)<=2,normalExtent,skinnedExtent});
        } finally {if(actual!=null)Object.DestroyImmediate(actual);if(baseline!=null)Object.DestroyImmediate(baseline);Object.DestroyImmediate(s);}
        var integerErrors=new List<string>();
        Application.LogCallback callback=(message,stack,type)=>{
            if(type==LogType.Error&&message.Contains("property sheet")) integerErrors.Add(message);
        };
        var integerMaterial=new Material(Shader.Find("NonToon"));
        try {
            Application.logMessageReceived+=callback;
            integerMaterial.SetInteger("_jp_lilxyzw_nontoon_matcaps_Enable",1);
            ShaderUtility.SetIntPersistent(integerMaterial,"_jp_lilxyzw_nontoon_matcaps_Enable",1);
            ShaderUtility.SetFloatValue(integerMaterial,"_OutlineFromVertexColor",1);
            integerMaterial.SetInteger("_OutlineFromVertexColor",1);
            ShaderUtility.PersistIntegers(integerMaterial);
            rows.Add(new{name="Integer writes preserve property sheet types",pass=integerErrors.Count==0,errors=integerErrors.ToArray()});
        } finally {Application.logMessageReceived-=callback;Object.DestroyImmediate(integerMaterial);}
        return rows;
    }
    [MenuItem("Tools/LilToNonToon Switcher/验证 MatCap 与描边", false, 930)]
    static void RunFromMenu() {
        var rows=RunAll();
        foreach(var row in rows) Debug.Log(row);
        int failures=rows.Count(row=>!(bool)row.GetType().GetProperty("pass").GetValue(row,null));
        if(failures!=0) throw new InvalidOperationException(failures+" surface regression checks failed.");
        Debug.Log("All "+rows.Count+" surface regression checks passed.");
    }

}


}
