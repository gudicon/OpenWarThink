using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A port extension that draws the destruction of units in its own way, next to or instead of the original pictures.
    /// The ported unit code only reports what happens (the unit died, a smaller explosion went off on the wreck, a section
    /// was blown off, the wreck blew apart) and asks whether to draw the original explosion; it never changes damage, the
    /// death sequence, its timing, sounds or debris. With no extension (<see cref="Mw2UnitView.Current"/> null) everything is
    /// the original's.
    /// </summary>
    public interface IUnitView
    {
        /// <summary>The large explosion at the unit's origin when it is destroyed (effect 0xd); returns whether the original
        /// explosion model is drawn.</summary>
        bool Died(Mw2MechCombat unit, Vector3 origin);

        /// <summary>One of the smaller explosions on the wreck (effect 3); returns whether the original one is drawn.</summary>
        bool Burst(Mw2MechCombat unit, Vector3 point);

        /// <summary>A section (location 1..8) is blown off; <paramref name="point"/> is the middle of its parts.</summary>
        void SectionLost(Mw2MechCombat unit, int location, Vector3 point);

        /// <summary>The wreck landed hard and throws its remaining parts.</summary>
        void BlownApart(Mw2MechCombat unit, Vector3 origin);

        /// <summary>A thrown-off part of the unit was shot or blasted apart (effect 7 at its centre); returns whether the
        /// original explosion is drawn.</summary>
        bool PartBlast(Mw2MechCombat unit, Vector3 point);

        /// <summary>The wreck threw a small piece (effect 0x10b) that now flies as <paramref name="piece"/>; returns whether
        /// it leaves the original smoke trail. <paramref name="landed"/>, when set, is called once when the piece stops or
        /// its time is over.</summary>
        bool DebrisThrown(Mw2MechCombat unit, Transform piece, out System.Action landed);
    }

    /// <summary>The extension that draws unit destruction, if any (port addition with no counterpart in the original program).</summary>
    public static class Mw2UnitView
    {
        public static IUnitView Current;

        /// <summary><see cref="IUnitView.Died"/>, or true (draw the original) without an extension.</summary>
        public static bool Died(Mw2MechCombat unit, Vector3 origin)
        {
            if (Current == null) return true;
            try { return Current.Died(unit, origin); }
            catch (System.Exception e) { Debug.LogException(e); return true; }
        }

        /// <summary><see cref="IUnitView.Burst"/>, or true (draw the original) without an extension.</summary>
        public static bool Burst(Mw2MechCombat unit, Vector3 point)
        {
            if (Current == null) return true;
            try { return Current.Burst(unit, point); }
            catch (System.Exception e) { Debug.LogException(e); return true; }
        }

        public static void SectionLost(Mw2MechCombat unit, int location, Vector3 point)
        {
            if (Current == null) return;
            try { Current.SectionLost(unit, location, point); }
            catch (System.Exception e) { Debug.LogException(e); }
        }

        public static void BlownApart(Mw2MechCombat unit, Vector3 origin)
        {
            if (Current == null) return;
            try { Current.BlownApart(unit, origin); }
            catch (System.Exception e) { Debug.LogException(e); }
        }

        /// <summary><see cref="IUnitView.PartBlast"/>, or true (draw the original) without an extension or a unit.</summary>
        public static bool PartBlast(Mw2MechCombat unit, Vector3 point)
        {
            if (Current == null || unit == null) return true;
            try { return Current.PartBlast(unit, point); }
            catch (System.Exception e) { Debug.LogException(e); return true; }
        }

        /// <summary><see cref="IUnitView.DebrisThrown"/>, or true (the original smoke) without an extension or a unit.</summary>
        public static bool DebrisThrown(Mw2MechCombat unit, Transform piece, out System.Action landed)
        {
            landed = null;
            if (Current == null || unit == null) return true;
            try { return Current.DebrisThrown(unit, piece, out landed); }
            catch (System.Exception e) { Debug.LogException(e); landed = null; return true; }
        }
    }
}
