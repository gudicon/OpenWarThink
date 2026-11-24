using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Depth pictures of a graphics mod's lighting (GraphicsQuality "lighting"), drawn before every frame of a mission with
    /// the replacement shader MW2/Depth: the sun's shadow map (an orthographic camera along the mission's light slot A, the
    /// wa light of SKYGND.PAR, over a square of the shadow distance in front of the main camera) and, for the ambient
    /// occlusion, the depth of the main camera's view; and the reflection probe, a cube of the world drawn at the camera a
    /// few faces a frame into one of two cubes while the shaders read the other (the finished one), its mips blurred for
    /// rough surfaces. Shaders read them through Mw2Modern.cginc and MW2/ModPost. Without the mod nothing is drawn and
    /// _Mw2SunShadowParams.w, _Mw2ViewParams.w and _Mw2EnvParams.x stay 0.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class Mw2GraphicsDepth : MonoBehaviour
    {
        const int SunSize = 4096;
        static Mw2GraphicsDepth s_host;
        static readonly int SunMapId = Shader.PropertyToID("_Mw2SunShadowMap");
        static readonly int SunVpId = Shader.PropertyToID("_Mw2SunShadowVP");
        static readonly int SunViewId = Shader.PropertyToID("_Mw2SunShadowView");
        static readonly int SunParamsId = Shader.PropertyToID("_Mw2SunShadowParams");
        static readonly int SunTexelId = Shader.PropertyToID("_Mw2SunShadowTexel");
        static readonly int ViewDepthId = Shader.PropertyToID("_Mw2ViewDepth");
        static readonly int ViewParamsId = Shader.PropertyToID("_Mw2ViewParams");
        static readonly int ViewSurfaceId = Shader.PropertyToID("_Mw2ViewSurface");
        static readonly int EnvCubeId = Shader.PropertyToID("_Mw2EnvCube");
        static readonly int EnvParamsId = Shader.PropertyToID("_Mw2EnvParams");
        static readonly int EnvPassId = Shader.PropertyToID("_Mw2EnvPass");

        Camera _sun, _view, _env;
        RenderTexture _sunMap, _viewDepth, _viewSurface, _envShown, _envDrawn;
        readonly RenderBuffer[] _viewTargets = new RenderBuffer[2];
        int _envFace;
        Shader _shader;

        /// <summary>Creates the host once; it stays for the whole run and draws only while the lighting is on.</summary>
        public static void Ensure()
        {
            if (s_host != null) return;
            var go = new GameObject("MW2 graphics depth") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            s_host = go.AddComponent<Mw2GraphicsDepth>();
        }

        /// <summary>The direction the sun's light travels (Unity space), from light slot A; false without such a light.</summary>
        public static bool SunVector(Mw2Atmosphere atmosphere, out Vector3 dir)
        {
            dir = Vector3.down;
            if (atmosphere == null || !atmosphere.LightsSet) return false;
            var a = atmosphere.LightA;
            if (a.Color.r + a.Color.g + a.Color.b == 0) return false;
            var cam = Camera.main;
            Vector3 v = a.IsDirection ? a.Vector
                : (cam != null ? cam.transform.position : Vector3.zero) - a.Vector * PolyModel.UnitsToMeters;
            if (v.sqrMagnitude < 1e-12f) return false;
            dir = v.normalized;
            return true;
        }

        void OnEnable() => Application.onBeforeRender += Draw;

        void OnDisable()
        {
            Application.onBeforeRender -= Draw;
            Shader.SetGlobalVector(SunParamsId, Vector4.zero);
            Shader.SetGlobalVector(ViewParamsId, Vector4.zero);
            Shader.SetGlobalVector(EnvParamsId, Vector4.zero);
        }

        void OnDestroy()
        {
            DropEnv();
            if (_sunMap != null) { _sunMap.Release(); Destroy(_sunMap); }
            if (_viewDepth != null) { _viewDepth.Release(); Destroy(_viewDepth); }
            if (_viewSurface != null) { _viewSurface.Release(); Destroy(_viewSurface); }
        }

        Camera MakeCamera(string name)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            var c = go.AddComponent<Camera>();
            c.enabled = false;
            return c;
        }

        void Draw()
        {
            var q = Mw2Graphics.Current;
            var cam = Camera.main;
            var atmosphere = Mw2Atmosphere.Instance;
            bool on = q.Lighting && cam != null && atmosphere != null && atmosphere.isActiveAndEnabled;
            if (on && _shader == null)
            {
                _shader = Shader.Find("MW2/Depth");
                if (_shader == null || !_shader.isSupported) { _shader = null; on = false; }
            }

            if (on && q.Shadows > 0f && SunVector(atmosphere, out var dir)) DrawSun(cam, dir, q);
            else Shader.SetGlobalVector(SunParamsId, Vector4.zero);

            if (on && q.NeedsViewDepth) DrawView(cam);
            else Shader.SetGlobalVector(ViewParamsId, Vector4.zero);

            if (on && q.Probe) DrawEnv(cam, q);
            else if (_envShown != null) DropEnv();
        }

        RenderTexture MakeCube(int size)
        {
            var rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32)
            {
                name = "MW2 reflection probe",
                dimension = UnityEngine.Rendering.TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        void DropEnv()
        {
            Shader.SetGlobalVector(EnvParamsId, Vector4.zero);
            if (_envShown != null) { _envShown.Release(); Destroy(_envShown); _envShown = null; }
            if (_envDrawn != null) { _envDrawn.Release(); Destroy(_envDrawn); _envDrawn = null; }
            _envFace = 0;
        }

        /// <summary>The reflection probe: q.ProbeFacesPerFrame faces into the cube being drawn; when all six are there it
        /// gets its mips and becomes the one the shaders read. The cockpit shell stays out (_Mw2EnvPass).</summary>
        void DrawEnv(Camera cam, GraphicsQuality q)
        {
            int size = q.ProbeResolution;
            if (_envShown != null && _envShown.width != size) DropEnv();
            if (_envShown == null)
            {
                _envShown = MakeCube(size);
                _envDrawn = MakeCube(size);
                _envFace = 0;
            }
            if (_env == null) _env = MakeCamera("MW2 reflection probe");
            _env.CopyFrom(cam);
            _env.enabled = false;
            _env.targetTexture = null;
            _env.allowMSAA = false;
            _env.allowHDR = false;
            _env.nearClipPlane = 0.3f;
            _env.farClipPlane = Mathf.Min(cam.farClipPlane, 4000f);
            int mask = 0;
            for (int i = 0; i < q.ProbeFacesPerFrame && _envFace + i < 6; i++) mask |= 1 << (_envFace + i);
            Shader.SetGlobalFloat(EnvPassId, 1f);
            _env.RenderToCubemap(_envDrawn, mask);
            Shader.SetGlobalFloat(EnvPassId, 0f);
            _envFace += q.ProbeFacesPerFrame;
            if (_envFace >= 6)
            {
                _envFace = 0;
                _envDrawn.GenerateMips();
                var t = _envShown; _envShown = _envDrawn; _envDrawn = t;
                Shader.SetGlobalTexture(EnvCubeId, _envShown);
                Shader.SetGlobalVector(EnvParamsId, new Vector4(1f, Mathf.Log(size, 2) - 1f, 0, 0));
            }
        }

        void DrawSun(Camera cam, Vector3 dir, GraphicsQuality q)
        {
            if (_sun == null)
            {
                _sun = MakeCamera("MW2 sun shadow");
                _sun.orthographic = true;
                _sun.clearFlags = CameraClearFlags.SolidColor;
                _sun.backgroundColor = Color.white;
                _sun.allowMSAA = false;
                _sun.allowHDR = false;
                _sun.useOcclusionCulling = false;
            }
            if (_sunMap == null)
            {
                _sunMap = new RenderTexture(SunSize, SunSize, 24, RenderTextureFormat.RFloat)
                    { name = "MW2 sun shadow map", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _sunMap.Create();
            }
            float distance = q.ShadowDistance;
            float half = distance * 0.55f;
            float depth = Mathf.Max(distance * 4f, 2000f);
            Vector3 forward = cam.transform.forward;
            forward.y = 0;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Vector3 center = cam.transform.position + forward * (distance * 0.45f);

            // Steady texels: the square moves in whole texels across the light, so its edges do not crawl.
            var rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
            Vector3 local = Quaternion.Inverse(rotation) * center;
            float texel = 2f * half / SunSize;
            local.x = Mathf.Round(local.x / texel) * texel;
            local.y = Mathf.Round(local.y / texel) * texel;
            center = rotation * local;

            _sun.transform.SetPositionAndRotation(center - dir * (depth * 0.5f), rotation);
            _sun.orthographicSize = half;
            _sun.aspect = 1f;
            _sun.nearClipPlane = 0.1f;
            _sun.farClipPlane = depth;
            _sun.cullingMask = cam.cullingMask;
            _sun.targetTexture = _sunMap;
            _sun.RenderWithShader(_shader, "Mw2Depth");
            _sun.targetTexture = null;

            Matrix4x4 view = _sun.worldToCameraMatrix;
            Shader.SetGlobalTexture(SunMapId, _sunMap);
            Shader.SetGlobalMatrix(SunVpId, _sun.projectionMatrix * view);
            Shader.SetGlobalVector(SunViewId, view.GetRow(2));
            Shader.SetGlobalVector(SunParamsId, new Vector4(1f / depth, texel * 1.5f / depth, q.Shadows, 1f));
            Shader.SetGlobalVector(SunTexelId, new Vector4(1f / SunSize, texel * 1.5f, 0, 0));
        }

        void DrawView(Camera cam)
        {
            if (_view == null) _view = MakeCamera("MW2 view depth");
            int w = Mathf.Max(1, cam.pixelWidth), h = Mathf.Max(1, cam.pixelHeight);
            if (_viewDepth == null || _viewDepth.width != w || _viewDepth.height != h)
            {
                if (_viewDepth != null) { _viewDepth.Release(); Destroy(_viewDepth); }
                if (_viewSurface != null) { _viewSurface.Release(); Destroy(_viewSurface); }
                _viewDepth = new RenderTexture(w, h, 24, RenderTextureFormat.RFloat)
                    { name = "MW2 view depth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _viewDepth.Create();
                // Normal in view space and how strongly the surface mirrors (MW2/Depth target 1), for the reflections.
                _viewSurface = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
                    { name = "MW2 view surface", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                _viewSurface.Create();
            }
            _view.CopyFrom(cam);
            _view.enabled = false;
            _view.clearFlags = CameraClearFlags.SolidColor;
            _view.backgroundColor = Color.white;
            _view.allowMSAA = false;
            _view.allowHDR = false;
            _viewTargets[0] = _viewDepth.colorBuffer;
            _viewTargets[1] = _viewSurface.colorBuffer;
            _view.SetTargetBuffers(_viewTargets, _viewDepth.depthBuffer);
            _view.RenderWithShader(_shader, "Mw2Depth");
            _view.targetTexture = null;
            Matrix4x4 p = cam.projectionMatrix;
            Shader.SetGlobalTexture(ViewDepthId, _viewDepth);
            Shader.SetGlobalTexture(ViewSurfaceId, _viewSurface);
            Shader.SetGlobalVector(ViewParamsId, new Vector4(p.m00, p.m11, cam.farClipPlane, 1f));
        }
    }
}
