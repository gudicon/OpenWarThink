using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The network branches of the unit code. DAT_100a6c3c != 0: damage is applied only on the machine that owns the mech
    /// (Shot_Move 0x10010f40, Effect_AreaDamageUnits 0x10012b59, Mech_ApplyDamage 0x10037b59), heat runs only for the owner
    /// (Mech_UpdateHeat 0x10035a9b), shots of other players use no ammunition (Weapon_FireProjectile 0x1006c554), and
    /// other players die only when their state says so (Mech_DestroySection 0x100365d3, Mech_CriticalHit 0x10036aa9).
    /// </summary>
    public partial class Mw2MechCombat
    {
        /// <summary>DAT_100a6c3c != 0.</summary>
        public static bool NetGame;
        /// <summary>A mech another machine owns.</summary>
        [System.NonSerialized] public bool NetRemote;
        [System.NonSerialized] public int NetLastAttacker = -1;
        [System.NonSerialized] public int NetFireOrders;
        /// <summary>The weapon table Mech_LoadMek reads at regeneration; the builder sets it.</summary>
        public static IList<WeaponDef> NetWeaponTable;
        bool _netDeath;
        int[] _netFront, _netRear, _netInternal;
        struct NetNode
        {
            public Transform Node, Parent;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
        }
        readonly List<NetNode> _netNodes = new List<NetNode>();
        readonly HashSet<Transform> _netNodeSet = new HashSet<Transform>();
        Collider[] _netColliders;
        bool[] _netColliderEnabled;
        List<int>[] _netItems;
        int _netHeatSinks, _netJumpMp;

        /// <summary>FUN_100350fb / FUN_100353af and FUN_1003519e. mw2dif.cfg +3 switches the mech's own damage; scenery with flag 0x200 takes its share regardless.</summary>
        public void CollisionImpact(Vector3 normal, Mw2MechCombat other, Mw2Destructible structure, Vector3 velocity, Vector3 otherVelocity)
        {
            Vector3 d = velocity - otherVelocity;
            float damage = CollisionDamage.Amount((float)MechSphereContact.Length(d.x, d.y, d.z));
            if (damage <= 0f || Destroyed) return;
            if (structure != null) structure.ApplyDamage(damage, this);
            if (!Mw2Options.GameOptions.LandingDamage) return;
            TryGetComponent<Mw2TorsoTwist>(out var twist);
            float bearing = Mathf.Atan2(-normal.x, -normal.z) * Mathf.Rad2Deg;
            var hit = CollisionDamage.Locate(damage, normal.y, bearing, transform.eulerAngles.y, twist != null ? twist.Current : 0f, Tons, other != null ? other.Tons : -1);
            Damage(hit.Location, hit.Damage, false, other);
            if (hit.Location2 != 0 && !Destroyed) Damage(hit.Location2, hit.Damage, false, other);
        }

        /// <summary>The unit's starting armour, for FUN_10051da0.</summary>
        public void NetRemember()
        {
            _netFront = (int[])Front.Clone();
            _netRear = (int[])Rear.Clone();
            _netInternal = (int[])Internal.Clone();
            _netItems = SectionItems == null ? null : System.Array.ConvertAll(SectionItems, items => items == null ? new List<int>() : new List<int>(items));
            _netHeatSinks = HeatSinks;
            _netJumpMp = TryGetComponent<Mw2MechController>(out var movement) ? movement.JumpMp : 0;
            // FUN_10052208 saves nodes/links/pose for FUN_10052148's recursive rebuild.
            _netNodes.Clear(); _netNodeSet.Clear();
            foreach (var node in GetComponentsInChildren<Transform>(true))
                if (node != transform)
                {
                    _netNodes.Add(new NetNode { Node = node, Parent = node.parent, Position = node.localPosition,
                        Rotation = node.localRotation, Scale = node.localScale });
                    _netNodeSet.Add(node);
                }
            _netColliders = GetComponentsInChildren<Collider>(true);
            _netColliderEnabled = new bool[_netColliders.Length];
            for (int i = 0; i < _netColliders.Length; i++) _netColliderEnabled[i] = _netColliders[i].enabled;
        }

        /// <summary>FUN_10021376: state of a mech the sender owns.</summary>
        public void NetApply(NetPackets.UnitState s, Mw2MechCombat attacker)
        {
            for (int i = 0; i < 8; i++)
            {
                Front[i] = (int)s.Front[i];
                Internal[i] = (int)s.Internal[i];
                if (i >= 1 && i <= 3) Rear[i] = (int)s.Rear[i - 1];
                // Mech_DestroySectionLoud: a section the owner has lost and this machine has not.
                if (Internal[i] <= 0 && !SectionGone[i]) DestroySection(i + 1, attacker);
            }
            int state = s.Flags & 0xf;
            if (state == 2)
            {
                ShutDown = false;
                PoweredDown = false;
                if (TryGetComponent<Mw2MechController>(out var controller)) controller.PowerEnabled = true;
            }
            else if (state == 3) ShutDown = true;
            if ((state == 4 || state == 5) && !Destroyed)
            {
                Ejected = state == 5;
                _netDeath = true;
                Die(attacker);
                _netDeath = false;
            }
        }

        /// <summary>FUN_1006d3ec: slots another player has fired start their burst here.</summary>
        public void NetFire(ushort mask)
        {
            NetFireOrders++;
            for (int i = 0; i < 14 && i < Weapons.Count; i++)
                if ((mask & (1 << i)) != 0) FireWeapon(i);
        }

        /// <summary>The name, heat and burst of a fire order on this machine's own mech, without keyboard input.</summary>
        public int NetFireAll()
        {
            int started = 0;
            for (int i = 0; i < Weapons.Count; i++)
                if (Weapons[i].State == 1 && Works(i)) { StartBurst(i); started++; }
            return started;
        }

        /// <summary>
        /// FUN_10051da0: regeneration. Armour, structure and destroyed flags return to the mech's start; Mech_LoadMek also reloads the MEK's
        /// ammunition and items. FUN_10052148 restores saved nodes/links/pose; Unity keeps these same nodes when detached.
        /// </summary>
        public void NetRegenerate()
        {
            if (_netFront == null) return;
            System.Array.Copy(_netFront, Front, 8);
            System.Array.Copy(_netRear, Rear, 8);
            System.Array.Copy(_netInternal, Internal, 8);
            // Mech_LoadMek1002f9e0 reloads critical inventories/cooling before InitMovement100380d0.
            if (_netItems != null) SectionItems = System.Array.ConvertAll(_netItems, items => new List<int>(items));
            HeatSinks = _netHeatSinks; SpeedFactor = 1f; LegLost = false;
            System.Array.Clear(GaugeDamage, 0, GaugeDamage.Length);
            System.Array.Clear(_armorBreachAnnounced, 0, _armorBreachAnnounced.Length);
            for (int i = 0; i < 8; i++) SectionGone[i] = false;
            Destroyed = false;
            Ejected = false;
            Heat = 0f;
            _deathEnd = 0;
            _killer = null;
            ShutDown = PoweredDown = Narced = _stateFive = _overheated = _override = false;
            HeatRate = _heatThisFrame = 0;
            foreach (var w in Weapons)
            {
                w.Broken = false; w.State = w.Pool == AmmoStore.NoPool && w.Def != null && w.Def.UsesAmmo ? -1 : 1;
                w.ShotsLeft = w.Timer = w.Stamp = 0; w.Repeat = false; w.LockTarget = null;
            }
            _fireRequests.Clear(); _fireLatch = _groupFireHeld = false;
            // FUN_100426d3: the team's objectives start over.
            var director = FindAnyObjectByType<Mw2MissionDirector>();
            if (director != null) director.NetResetTeam(Team);
            if (MekBytes != null && NetWeaponTable != null) Ammunition = AmmoStore.Load(MekLoadout.Decode(MekBytes), NetWeaponTable);
            SyncAmmo();
            // Release moving/expired debris ownership before returning these nodes to the living model.
            if (Mw2Effects.Instance != null) Mw2Effects.Instance.ReclaimUnitParts(_netNodeSet);
            foreach (var saved in _netNodes)
                if (saved.Node != null)
                {
                    saved.Node.SetParent(saved.Parent, false);
                    saved.Node.localPosition = saved.Position;
                    saved.Node.localRotation = saved.Rotation;
                    saved.Node.localScale = saved.Scale;
                }
            if (_netColliders != null)
                for (int i = 0; i < _netColliders.Length; i++)
                    if (_netColliders[i] != null) _netColliders[i].enabled = _netColliderEnabled[i];
            if (TryGetComponent<Mw2HitMap>(out var hitMap)) hitMap.ResetDamage();
            _renderers = null; _centreFrame = -1;
            if (TryGetComponent<Mw2LodGroup>(out var lods)) lods.ShowAgain();
            else foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            if (TryGetComponent<Mw2MechController>(out var controller)) controller.NetResetMovement(_netJumpMp);
            StartUp(); // Mech_InitMovement100380d0 sets state0; regeneration must take the normal startup delay.
        }
    }
}
