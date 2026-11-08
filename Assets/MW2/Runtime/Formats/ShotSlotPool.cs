namespace MW2.Formats
{
    /// <summary>FirstShots 0x10010c60, BTHG loader 0x10070e92 and Weapon_FireProjectile
    /// 0x1006c554: 255 ordered slots. Shot_Clear 0x10010dcf retains previous XYZ.</summary>
    public sealed class ShotSlotPool
    {
        public const int Capacity = 255;
        public int Generation { get; private set; }
        sealed class Slot
        {
            public int Type = -1;
            public bool HasNode, Active;
            public AiLeadPoint.Vector Previous;
        }
        readonly Slot[] _slots = new Slot[Capacity];
        public ShotSlotPool() { for (int i = 0; i < Capacity; i++) _slots[i] = new Slot(); }

        /// <summary>0x10010c60: reset scene bindings and activity, preserving XYZ across missions.</summary>
        public void Reset()
        {
            Generation++;
            foreach (var slot in _slots) { slot.Type = -1; slot.HasNode = false; slot.Active = false; }
        }
        /// <summary>0x10070e92 BTHG branch: even an unresolved node consumes its slot index.</summary>
        public void Bind(int index, int type, bool hasNode)
        { if (index >= 0 && index < Capacity) { _slots[index].Type = type; _slots[index].HasNode = hasNode; } }

        /// <summary>0x1006c554: first matching inactive slot with a node; -1 means no allocation.</summary>
        public int Take(int type)
        {
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].Type == type && !_slots[i].Active && _slots[i].HasNode)
                { _slots[i].Active = true; return i; }
            return -1;
        }
        /// <summary>0x10010dcf: release without modifying the previous-segment coordinates.</summary>
        public void Release(int index) { _slots[index].Active = false; }
        public AiLeadPoint.Vector Previous(int index) => _slots[index].Previous;
        /// <summary>0x10010f40: store the node's current position before integrating its next position.</summary>
        public void Advance(int index, AiLeadPoint.Vector current) { _slots[index].Previous = current; }
    }
}
