using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Looping sound of a scene node: TSK class 4 (0x1000e68f, update 0x100072d8). After its first update the task
    /// plays its SNDS sample in a loop on one of the eight channels 8..15 while the camera is within the range
    /// (task value * 100 units); farther away, or with no free channel, it stops (0x1000759c). The volume follows the
    /// distance, 100 - d * 100 / 250000 units (0x10007648), at a quarter of the Miles volume (>> 2), panned by bearing.
    /// The update stops the sound once the task's node object is gone (0x1000e68f: node pointer or its first field 0);
    /// a destroyed unit swaps its model (Mech_Destroy 0x10035ef6 -> Lod_SetForGamepiece), so its rotor falls silent.
    /// Approximations: Unity's 3D panning stands in for the Miles pan (0x100071e9); the node is taken as gone when the
    /// unit it belongs to is destroyed or the node is inactive.
    /// </summary>
    public class Mw2AmbientSound : MonoBehaviour
    {
        [Tooltip("SNDS sample name (task string, e.g. HELICPTR, MECFIRE1, WINDMILL).")]
        public string Sound;
        [Tooltip("Audible range in model units (task value * 100).")]
        public int RangeUnits;

        const int Channels = 8;     // DAT_100a623c channels 8..15
        static int s_playing;

        AudioSource _source;
        Mw2MechCombat _unit;
        bool _firstUpdate = true;   // [6] = 1 at creation, cleared by the first update

        void Update()
        {
            if (_firstUpdate) { _firstUpdate = false; return; }
            if (_unit == null) _unit = GetComponentInParent<Mw2MechCombat>();
            if (_unit != null && _unit.Destroyed) { Stop(); enabled = false; return; }
            var cam = Camera.main;
            if (cam == null || Mw2Audio.Instance == null) return;
            float units = Vector3.Distance(cam.transform.position, transform.position) / PolyModel.UnitsToMeters;
            if (units > RangeUnits || units > 250000f) { Stop(); return; }
            if (_source == null)
            {
                if (s_playing >= Channels) return;
                var clip = Mw2Audio.Instance.ClipByName(Sound);
                if (clip == null) return;
                _source = gameObject.AddComponent<AudioSource>();
                _source.clip = clip;
                _source.loop = true;
                _source.spatialBlend = 1f;
                _source.rolloffMode = AudioRolloffMode.Linear;
                _source.minDistance = 0.01f;
                _source.maxDistance = 250000f * PolyModel.UnitsToMeters;
                _source.volume = 0.25f;
                _source.Play();
                s_playing++;
            }
        }

        void Stop()
        {
            if (_source == null) return;
            Destroy(_source);
            _source = null;
            s_playing--;
        }

        void OnDisable() => Stop();
    }
}
