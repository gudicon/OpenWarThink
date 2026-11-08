using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Fog, background colour, cloud drift and lights from SKYGND.PAR (see MW2.Formats.SkyGround) and the mission LITE chunk.
    /// Fog strength and background colour follow mercsw.dll 0x1006a310 every frame: with density a != 0 the strength
    /// is a / camera height (units), limited to [b, 1]; the background (clear colour DAT_1008fb7c) is fog * (1 - c) + sky * c
    /// with c = strength * a / 50000 limited to [b, 1], the sky colour when a = 0. Vertex visibility is
    /// (1 - min(depth / distance, 1)) * strength (Plugin_DrawPolygon 0x10053040, ground 0x100019c1), and Glide blends towards
    /// its fog colour, which stays the fog= colour for the whole mission (grFogColorValue once, zglide.dll 0x10001b60).
    /// Clouds (texture key 0x18c) scroll by skyAnim per second (0x1005559a adds skyAnim / 181 per tick, wraps at 1).
    /// Approximation: the background uses the mixed colour as the camera clear colour; the view-dependent
    /// rotation of 0x18c on the windshield (0x10001081) is not ported.
    /// </summary>
    public class Mw2Atmosphere : MonoBehaviour
    {
        public Color32 FogColor = new Color32(200, 200, 200, 255);
        public Color32 SkyColor = new Color32(64, 64, 255, 255);
        [Tooltip("density=a: altitude scale of the fog strength, model units.")]
        public float DensityA;
        [Tooltip("density=b: lower limit of the fog strength.")]
        public float DensityB;
        [Tooltip("Fog distance in metres (density c * 2 units); 0 = no distance fog.")]
        public float FogDistance;
        [Header("Lights (mission LITE + SKYGND.PAR amb/wa/wb, see Mw2Light.cginc)")]
        public bool LightsSet;
        public Color32 Ambient;
        public Formats.SkyGround.Light LightA, LightB;
        [Tooltip("SKYGND.PAR globalintensity, limited to 0..128 (0x1002e817).")]
        public int GlobalIntensity = 0x80;
        [Tooltip("LITE ambient level (camera +0x2a).")]
        public int AmbientLevel;
        public bool SunEnabled;
        public bool SunIsDirection = true;
        [Tooltip("LITE sun vector, model units, engine axes.")]
        public Vector3 Sun;

        public Material CloudMaterial;
        public Vector2 CloudSpeed;

        public static Mw2Atmosphere Instance;
        bool _flash;
        Vector3 _savedSun;
        bool _savedDirection;
        int _savedAmbient;

        void OnEnable() => Instance = this;

        /// <summary>DAT_100a65f4: an explosion holds the sun.</summary>
        public bool Flashing => _flash;

        /// <summary>The sun position while flashing, metres (camera +0x1c..+0x24).</summary>
        public Vector3 FlashPosition => Sun * Formats.PolyModel.UnitsToMeters;

        /// <summary>
        /// Effect_Start 0x10011ea8: the first flash saves the sun (+0x1c..+0x28), the ambient level (+0x2a) and the distance
        /// darkening flag, lowers the ambient by 10 (64 when that leaves 0..255) and makes the sun a position; every flash
        /// then puts the sun at the explosion.
        /// </summary>
        public void Flash(Vector3 pointMeters)
        {
            if (!_flash)
            {
                _flash = true;
                _savedSun = Sun;
                _savedDirection = SunIsDirection;
                _savedAmbient = AmbientLevel;
                AmbientLevel -= 10;
                if (AmbientLevel > 0xff || AmbientLevel < 0) AmbientLevel = 0x40;
                SunIsDirection = false;
            }
            Sun = pointMeters / Formats.PolyModel.UnitsToMeters;
        }

        /// <summary>Effects_Update 0x1001280e: the flashing slot is over, the sun and the ambient come back.</summary>
        public void EndFlash()
        {
            if (!_flash) return;
            _flash = false;
            Sun = _savedSun;
            SunIsDirection = _savedDirection;
            AmbientLevel = _savedAmbient;
        }

        static readonly int FogColorId = Shader.PropertyToID("_Mw2FogColor");
        static readonly int AmbientId = Shader.PropertyToID("_Mw2Ambient");
        static readonly int LightAColorId = Shader.PropertyToID("_Mw2LightAColor");
        static readonly int LightAVectorId = Shader.PropertyToID("_Mw2LightAVector");
        static readonly int LightBColorId = Shader.PropertyToID("_Mw2LightBColor");
        static readonly int LightBVectorId = Shader.PropertyToID("_Mw2LightBVector");
        static readonly int SunId = Shader.PropertyToID("_Mw2Sun");
        static readonly int LightParamsId = Shader.PropertyToID("_Mw2LightParams");
        static readonly int FogParamsId = Shader.PropertyToID("_Mw2FogParams");
        Vector2 _cloudOffset;

        void LateUpdate()
        {
            var cam = Camera.main;
            float height = cam != null ? cam.transform.position.y / Formats.PolyModel.UnitsToMeters : 0f;

            float strength = 1f;
            if (DensityA != 0f)
            {
                if (height <= 0f)
                {
                    if (DensityA < 0f) strength = DensityB;
                }
                else
                {
                    float s = DensityA > 0f ? DensityA / height : -DensityA * height;
                    if (s < DensityB) strength = DensityB;
                    else if (s < 1f) strength = s;
                }
            }

            Color background;
            if (DensityA == 0f) background = SkyColor;
            else
            {
                float c = strength;
                if (DensityA > 0f) c = DensityA / 50000f * c;
                if (c < DensityB) c = DensityB;
                else if (c > 1f) c = 1f;
                background = Color.Lerp(FogColor, SkyColor, c);
            }

            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = background;
            }
            // The Glide fog colour is SKYGND.PAR fog=: SimMain passes it at +0x14 of the plugin's FirstDevice block and
            // zglide.dll calls grFogColorValue only there (0x10001b60). The mixed colour above is only the clear colour.
            Shader.SetGlobalColor(FogColorId, FogColor);
            Shader.SetGlobalVector(FogParamsId, new Vector4(FogDistance > 0f ? 1f / FogDistance : 0f, strength, 1f, 0f));

            SetLights();

            if (CloudMaterial != null)
            {
                _cloudOffset += CloudSpeed * Time.deltaTime;
                _cloudOffset.x -= Mathf.Floor(_cloudOffset.x);
                _cloudOffset.y -= Mathf.Floor(_cloudOffset.y);
                // CEL rows run top-down while Unity v runs bottom-up.
                CloudMaterial.mainTextureOffset = new Vector2(_cloudOffset.x, -_cloudOffset.y);
            }
        }

        /// <summary>Engine axes to Unity axes (PolyMeshBuilder.ToUnity: the identity).</summary>
        static Vector3 ToUnity(Vector3 v) => v;

        static void SetSlot(int colorId, int vectorId, Formats.SkyGround.Light light)
        {
            Color c = light.Color;
            Vector3 v = ToUnity(light.Vector);
            // 0x1002f955 normalises directions; positions stay in model units (converted to metres here).
            Vector3 vec = light.IsDirection ? (v.sqrMagnitude > 0 ? v.normalized : Vector3.zero) : v * Formats.PolyModel.UnitsToMeters;
            Shader.SetGlobalVector(colorId, new Vector4(c.r, c.g, c.b, light.IsDirection ? 1f : 2f));
            Shader.SetGlobalVector(vectorId, vec);
        }

        void SetLights()
        {
            if (!LightsSet)
            {
                Shader.SetGlobalVector(LightParamsId, Vector4.zero);
                return;
            }
            Shader.SetGlobalColor(AmbientId, (Color)Ambient);
            SetSlot(LightAColorId, LightAVectorId, LightA);
            SetSlot(LightBColorId, LightBVectorId, LightB);
            Shader.SetGlobalVector(SunId, new Vector4(Sun.x, Sun.y, Sun.z, SunIsDirection ? 1f : 0f));
            Shader.SetGlobalVector(LightParamsId, new Vector4(AmbientLevel, Mathf.Clamp(GlobalIntensity, 0, 0x80), SunEnabled ? 1f : 0f, 1f));
        }

        void OnDisable()
        {
            Shader.SetGlobalVector(FogParamsId, Vector4.zero);
            Shader.SetGlobalVector(LightParamsId, Vector4.zero);
            if (CloudMaterial != null) CloudMaterial.mainTextureOffset = Vector2.zero;
        }
    }
}
