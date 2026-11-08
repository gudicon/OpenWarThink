using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Projectile models by type from the shot file (MERCSHT1 BTHG slots, 0x10070e92): 0 LASER1, 1 LASER2, 2 LASER3,
    /// 3/4 MISSLE1, 5 BULLET, 6 PPCSHOT1, 7 GAS0_0. Lives on the mission root.
    /// </summary>
    public class Mw2ShotModels : MonoBehaviour
    {
        [System.Serializable]
        public class Slot
        {
            public int Type, Node;
            public bool HasNode;
        }
        public List<Slot> Slots = new List<Slot>();
        // Native BSS survives FirstShots across mission loads; the scene bindings do not.
        static readonly ShotSlotPool History = new ShotSlotPool();
        public ShotSlotPool Pool => History;
        bool _bound;

        /// <summary>0x10010c60 / 0x10070e92: bind imported scene slots once before first allocation.</summary>
        public int Take(int type)
        {
            if (!_bound)
            {
                History.Reset();
                for (int i = 0; i < Slots.Count && i < ShotSlotPool.Capacity; i++)
                    History.Bind(i, Slots[i].Type, Slots[i].HasNode);
                _bound = true;
            }
            return History.Take(type);
        }
        [System.Serializable]
        public class Part
        {
            public int Type;
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 Scale = Vector3.one;     // BWD node scale (OBJ +6, i32 x3)
        }

        /// <summary>
        /// The light colour of a projectile type (Weapon_FireProjectile 0x1006c554, Glow_Take 0x1005c2eb type 2): when the
        /// first polygon of the shot model is material type 4, the palette entry named by the first vertex's +0x18 (the
        /// POLY vertex's i16 a, FUN_1000395e), & 0xff, as the table 0x102880c0 keeps it (VGA value x 4, FUN_10004cc0).
        /// </summary>
        [System.Serializable]
        public class Glow
        {
            public int Type;
            public Color32 Rgb;
        }

        public List<Glow> Glows = new List<Glow>();

        public List<Part> Parts = new List<Part>();
        public static Mw2ShotModels Instance;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }
    }
}
