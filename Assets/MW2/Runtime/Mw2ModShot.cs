using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The beam of a mod weapon look on a flying shot (WeaponLook "shot.beam"): two camera-facing ribbons, a wide glow and a
    /// narrow core, from the shot back along its flight by the beam's length but never behind the point it started from.
    /// Port addition with no counterpart in the original program; it lives on the shot's object and goes with it.
    /// </summary>
    public class Mw2ModShot : MonoBehaviour
    {
        WeaponLook.BeamLook _beam;
        LineRenderer _glow, _core;
        Vector3 _start;

        public static void Attach(GameObject shot, WeaponLook.BeamLook beam, Vector3 muzzle)
        {
            var s = shot.AddComponent<Mw2ModShot>();
            s._beam = beam;
            s._start = muzzle;
            var material = Mw2WeaponLooks.Material(beam.Texture, Mw2WeaponLooks.Builtin.Beam);
            s._glow = s.Line("mod beam glow", material, beam.Width, Mw2WeaponLooks.ToColor(beam.Color));
            if (beam.CoreWidth > 0f)
                s._core = s.Line("mod beam core", material, beam.Width * beam.CoreWidth, Mw2WeaponLooks.ToColor(beam.CoreColor));
            s.LateUpdate();
        }

        LineRenderer Line(string name, Material material, float width, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.widthMultiplier = width;
            line.startColor = line.endColor = color;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        void LateUpdate()
        {
            if (_glow == null) return;
            var head = transform.position;
            var back = -transform.forward;
            float travelled = Vector3.Distance(head, _start);
            var tail = head + back * Mathf.Min(_beam.Length, travelled);
            if ((head - tail).sqrMagnitude < 1e-6f) tail = head + back * 0.01f;
            // Index 0 is the tail, so the texture's u runs from the tail (0) to the head (1).
            _glow.SetPosition(0, tail); _glow.SetPosition(1, head);
            if (_core != null) { _core.SetPosition(0, tail); _core.SetPosition(1, head); }
        }
    }
}
